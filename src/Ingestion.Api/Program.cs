using Ingestion.Api.Clients;
using Ingestion.Api.Configuration;
using Ingestion.Api.Endpoints;
using Ingestion.Api.Processing;
using Ingestion.Api.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddOptions<EnrichmentOptions>().BindConfiguration(EnrichmentOptions.Section).ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<AnalyticsOptions>().BindConfiguration(AnalyticsOptions.Section).ValidateDataAnnotations().ValidateOnStart();
services.AddOptions<IngestionOptions>().BindConfiguration(IngestionOptions.Section).ValidateDataAnnotations().ValidateOnStart();

services.AddProblemDetails();
services.AddMemoryCache();
services.AddHealthChecks();
services.AddSingleton(TimeProvider.System);

// --- Pipeline -------------------------------------------------------------------------
services.AddSingleton(sp => new IngestionQueue(
    sp.GetRequiredService<IOptions<IngestionOptions>>().Value.QueueCapacity, sp.GetRequiredService<TimeProvider>()));
services.AddSingleton<IDeadLetterSink, InMemoryDeadLetterSink>();
services.AddSingleton<IngestionService>();
services.AddSingleton<FailureHandler>();
services.AddSingleton<EnrichmentPause>();
services.AddSingleton(sp => new DeliveryBuffer(
    2 * sp.GetRequiredService<IOptions<AnalyticsOptions>>().Value.EffectiveBatchSize));
services.AddScoped<RecordEnricher>();
services.AddHostedService<EnrichmentWorker>();
services.AddHostedService<AnalyticsDispatcher>();

// --- Enrichment Service: unreliable, so retries + circuit breaker + timeouts (ADR 0006) ---
services.AddHttpClient<EnrichmentClient>(ConfigureDownstream<EnrichmentOptions>)
    .AddStandardResilienceHandler()
    .Configure((HttpStandardResilienceOptions o, IServiceProvider sp) =>
    {
        var opts = sp.GetRequiredService<IOptions<EnrichmentOptions>>().Value;
        o.Retry.MaxRetryAttempts = 4;             // exponential back-off with jitter (defaults)
        o.AttemptTimeout.Timeout = opts.AttemptTimeout;
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        o.CircuitBreaker.FailureRatio = 0.5;      // open when half of the calls in the sample fail…
        o.CircuitBreaker.MinimumThroughput = opts.CircuitMinimumThroughput; // …and there were at least this many
        o.CircuitBreaker.BreakDuration = opts.CircuitBreakDuration; // workers pause just as long (ADR 0010)
    });
services.AddSingleton<EnrichmentCache>();
services.AddTransient<IEnrichmentClient, CachingEnrichmentClient>();

// --- Analytics Service: hard limit of 20 messages / 10 s (ADR 0007), batched (ADR 0009) ---
// Handler order (outermost first): retry -> rate limiter -> per-attempt timeout -> network.
// The limiter sits inside the retry so every attempt uses a permit, and outside the attempt
// timeout so time spent waiting for a permit doesn't count as a timed-out attempt.
services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<AnalyticsOptions>>().Value;
    return new SlidingWindowLogRateLimiter(o.PermitLimit, o.Window + o.SafetyMargin, sp.GetRequiredService<TimeProvider>());
});
services.AddTransient<RateLimitingHandler>();
ConfigureAnalyticsHttp(services.AddHttpClient<BatchAnalyticsClient>(ConfigureDownstream<AnalyticsOptions>));
ConfigureAnalyticsHttp(services.AddHttpClient<SingleEventAnalyticsClient>(ConfigureDownstream<AnalyticsOptions>));
services.AddTransient<IAnalyticsClient>(sp =>
    sp.GetRequiredService<IOptions<AnalyticsOptions>>().Value.Mode == AnalyticsDeliveryMode.Batch
        ? sp.GetRequiredService<BatchAnalyticsClient>()
        : sp.GetRequiredService<SingleEventAnalyticsClient>());

var app = builder.Build();

app.UseExceptionHandler();
app.MapIngestionEndpoints();
app.MapHealthChecks("/health");

app.Run();

static void ConfigureAnalyticsHttp(IHttpClientBuilder http)
{
    http.AddResilienceHandler("analytics-retry", b => b.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 5,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = TimeSpan.FromSeconds(1),
        ShouldRetryAfterHeader = true,            // honour Retry-After on 429
    }));
    http.AddHttpMessageHandler<RateLimitingHandler>();
    http.AddResilienceHandler("analytics-attempt", (b, ctx) => b.AddTimeout(
        ctx.ServiceProvider.GetRequiredService<IOptions<AnalyticsOptions>>().Value.AttemptTimeout));
}

static void ConfigureDownstream<TOptions>(IServiceProvider sp, HttpClient client)
    where TOptions : DownstreamServiceOptions
{
    var options = sp.GetRequiredService<IOptions<TOptions>>().Value;
    client.BaseAddress = options.BaseUrl;
    client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", options.AuthorizationHeader);
    // Polly's timeouts govern each attempt; this is only a backstop.
    client.Timeout = Timeout.InfiniteTimeSpan;
}

namespace Ingestion.Api
{
    /// <summary>Entry-point marker for WebApplicationFactory in integration tests.</summary>
    public sealed class ApiEntryPoint;
}
