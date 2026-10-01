using FakeServices;
using Ingestion.Api.Clients;
using Ingestion.Api.Processing;
using Ingestion.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ingestion.Tests.Contracts;

/// <summary>
/// Runs our real HTTP clients against tools/FakeServices, which enforces the proposed
/// contracts strictly. Checks the request shape (no 400s/rejections) and that each error
/// class is mapped to the right retry behaviour (transient → retry, permanent → dead-letter).
/// </summary>
public sealed class ClientContractTests : IDisposable
{
    private readonly FakeServicesFactory _fake = new(analyticsPermitLimit: 1000);

    private static readonly ActivityRecordDto Record =
        new(119611, "server_horizon", "102.145.229.227", new DateTimeOffset(2024, 2, 27, 0, 0, 0, TimeSpan.Zero),
            "pxtrpf", "Exploit Public-Facing Application");

    private HttpClient Client(FakeServicesFactory? factory = null, string? auth = "eye-am-hiring")
    {
        var client = (factory ?? _fake).CreateClient();
        if (auth is not null) client.DefaultRequestHeaders.Add("Authorization", auth);
        return client;
    }

    private Task<AnalyticsEvent> EventFor(ActivityRecordDto record) =>
        new RecordEnricher(new EnrichmentClient(Client()))
            .EnrichAsync(new IngestionWorkItem(Guid.NewGuid(), record), CancellationToken.None);

    [Fact]
    public async Task Enrichment_returns_a_result_for_the_ip()
    {
        var result = await new EnrichmentClient(Client()).EnrichAsync(Record.Ip, CancellationToken.None);
        Assert.Equal(Record.Ip, result.GetProperty("ip").GetString());
        Assert.Matches("^AS[0-9]+$", result.GetProperty("asn").GetString());
    }

    [Fact]
    public async Task Batch_of_enriched_events_is_accepted_and_a_resend_is_reported_as_duplicate()
    {
        var events = new[] { await EventFor(Record), await EventFor(Record with { Id = 2 }) };
        var client = new BatchAnalyticsClient(Client());

        var first = await client.SendAsync(events, CancellationToken.None);
        var resend = await client.SendAsync(events, CancellationToken.None);

        Assert.All(first, r => Assert.Equal(EventDeliveryStatus.Accepted, r.Status));
        Assert.All(resend, r => Assert.Equal(EventDeliveryStatus.Duplicate, r.Status));
        Assert.Equal(events.Select(e => e.EventId), first.Select(r => r.EventId)); // same order
    }

    [Fact]
    public async Task Invalid_event_in_a_batch_is_rejected_individually()
    {
        var valid = await EventFor(Record);
        var invalid = (await EventFor(Record with { Id = 3 })) with { Category = "phising" };

        var results = await new BatchAnalyticsClient(Client()).SendAsync([valid, invalid], CancellationToken.None);

        Assert.Equal(EventDeliveryStatus.Accepted, results[0].Status);
        Assert.Equal(EventDeliveryStatus.Rejected, results[1].Status);
        Assert.Contains(results[1].Errors!, e => e.Contains("category", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Single_event_fallback_is_accepted() =>
        Assert.Equal(EventDeliveryStatus.Accepted,
            Assert.Single(await new SingleEventAnalyticsClient(Client())
                .SendAsync([await EventFor(Record)], CancellationToken.None)).Status);

    [Fact]
    public async Task Single_event_violating_the_schema_is_a_permanent_failure()
    {
        var invalid = (await EventFor(Record)) with { Category = "phising" };
        await Assert.ThrowsAsync<PermanentDependencyException>(() =>
            new SingleEventAnalyticsClient(Client()).SendAsync([invalid], CancellationToken.None));
    }

    [Fact]
    public async Task Missing_authorization_is_retried_not_dead_lettered()
    {
        // A bad key affects every record. Dead-lettering would empty the whole backlog into the DLQ.
        var ex = await Assert.ThrowsAsync<TransientDependencyException>(() =>
            new EnrichmentClient(Client(auth: null)).EnrichAsync(Record.Ip, CancellationToken.None));
        Assert.True(ex.IsConfigurationError);
    }

    [Fact]
    public async Task Invalid_ip_is_a_permanent_failure() =>
        await Assert.ThrowsAsync<PermanentDependencyException>(() =>
            new EnrichmentClient(Client()).EnrichAsync("N", CancellationToken.None));

    [Fact]
    public async Task Rate_limit_exceeded_is_a_transient_failure_for_the_whole_batch()
    {
        using var limited = new FakeServicesFactory(analyticsPermitLimit: 1);
        var client = new BatchAnalyticsClient(Client(limited));
        var events = new[] { await EventFor(Record) };

        await client.SendAsync(events, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<TransientDependencyException>(() => client.SendAsync(events, CancellationToken.None));
        Assert.Contains("429", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _fake.Dispose();

    private sealed class FakeServicesFactory(int analyticsPermitLimit) : WebApplicationFactory<FakeServicesEntryPoint>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Deterministic: no random failures or hangs in contract tests.
            builder.UseSetting("Fake:EnrichmentFailureRate", "0");
            builder.UseSetting("Fake:EnrichmentSlowRate", "0");
            builder.UseSetting("Fake:AnalyticsPermitLimit", analyticsPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
