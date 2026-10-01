using System.Net;
using System.Net.Http.Json;
using Ingestion.Api.Clients;
using Ingestion.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ingestion.Tests.Api;

public class IngestionEndpointTests : IClassFixture<IngestionEndpointTests.ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public IngestionEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static ActivityRecordDto Valid(long id) =>
        new(id, "host", "1.2.3.4", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), "Defender", "phising");

    private Task<HttpResponseMessage> PostAsync(IngestionRequest request, string? key = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingestions") { Content = JsonContent.Create(request) };
        if (key is not null) message.Headers.Add(IngestionHeaders.IdempotencyKey, key);
        return _client.SendAsync(message);
    }

    [Fact]
    public async Task Accepts_valid_records_reports_invalid_ones_and_delivers_asynchronously()
    {
        var bad = Valid(2) with { Ip = "N" };
        var response = await PostAsync(new IngestionRequest([Valid(1), bad]));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestionResponse>();
        Assert.Equal(1, body!.Accepted);
        Assert.True(body.EstimatedDeliverySeconds >= 1);
        var rejection = Assert.Single(body.Rejected);
        Assert.Equal(1, rejection.Index);

        // The background worker delivers the normalized record to Analytics.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_factory.Analytics.Sent.Any(e => e.IngestionId == body.IngestionId) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        var sent = Assert.Single(_factory.Analytics.Sent, e => e.IngestionId == body.IngestionId);
        Assert.Equal("defender", sent.Source);
        Assert.Equal("Phishing", sent.Category);
    }

    [Fact]
    public async Task All_records_invalid_returns_422_with_the_report()
    {
        var response = await PostAsync(new IngestionRequest([Valid(1) with { Source = "null" }]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestionResponse>();
        Assert.Equal(0, body!.Accepted);
    }

    [Fact]
    public async Task Empty_request_is_400()
    {
        var response = await PostAsync(new IngestionRequest([]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Same_idempotency_key_replays_the_first_response_without_requeueing()
    {
        var request = new IngestionRequest([Valid(10)]);
        var first = await (await PostAsync(request, "key-1")).Content.ReadFromJsonAsync<IngestionResponse>();
        var replay = await PostAsync(request, "key-1");

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.True(replay.Headers.Contains(IngestionHeaders.IdempotentReplayed));
        var second = await replay.Content.ReadFromJsonAsync<IngestionResponse>();
        Assert.Equal(first!.IngestionId, second!.IngestionId);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_payload_is_409()
    {
        await PostAsync(new IngestionRequest([Valid(20)]), "key-2");
        var response = await PostAsync(new IngestionRequest([Valid(21)]), "key-2");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    public sealed class ApiFactory : WebApplicationFactory<Ingestion.Api.ApiEntryPoint>
    {
        public FakeAnalytics Analytics { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IEnrichmentClient>(new FakeEnrichment());
                services.AddSingleton<IAnalyticsClient>(Analytics);
            });
        }
    }

}
