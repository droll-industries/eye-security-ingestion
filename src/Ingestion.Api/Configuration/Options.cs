using System.ComponentModel.DataAnnotations;

namespace Ingestion.Api.Configuration;

public abstract class DownstreamServiceOptions
{
    [Required] public Uri BaseUrl { get; set; } = null!;

    /// <summary>
    /// Value of the Authorization header. A secret: supply it through environment variables
    /// or a secret store (e.g. Key Vault), never commit it. It only lives in
    /// appsettings.Development.json because it is the shared assessment key.
    /// </summary>
    [Required(AllowEmptyStrings = false)] public string AuthorizationHeader { get; set; } = "";
}

public sealed class EnrichmentOptions : DownstreamServiceOptions
{
    public const string Section = "Enrichment";

    /// <summary>How long a cached enrichment result is used without refreshing it (ADR 0006).</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Stale-if-error: if refreshing an expired result fails, the old result is still used
    /// until it is this old. Threat intel on an IP changes slowly; waiting for an outage to
    /// end is worse than enriching with data a few hours old.
    /// </summary>
    public TimeSpan MaxStaleAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long the circuit stays open; enrichment workers pause for the same time.</summary>
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Calls needed in the 30 s sampling window before the circuit can open. Polly's default
    /// (100) is never reached at our traffic level: 8 workers spending most of an outage in HTTP
    /// retry back-off make about 30 calls per window, so the circuit almost never opened (ADR 0010).
    /// </summary>
    [Range(2, 1000)] public int CircuitMinimumThroughput { get; set; } = 10;

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

public enum AnalyticsDeliveryMode
{
    /// <summary>POST /events/batch: up to MaxBatchSize events per message (default).</summary>
    Batch,

    /// <summary>POST /events: one event per message. Fallback in case Analytics has no batch endpoint.</summary>
    SingleEvent,
}

public sealed class AnalyticsOptions : DownstreamServiceOptions
{
    public const string Section = "Analytics";

    /// <summary>See ADR 0009. The rate limit counts messages, so batching multiplies throughput.</summary>
    public AnalyticsDeliveryMode Mode { get; set; } = AnalyticsDeliveryMode.Batch;

    /// <summary>Max events per batch message (contract: maxItems 100).</summary>
    [Range(1, 100)] public int MaxBatchSize { get; set; } = 100;

    /// <summary>How long a partially filled batch waits for more records before being sent.</summary>
    public TimeSpan BatchLinger { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Events per message actually used: 1 in SingleEvent mode.</summary>
    public int EffectiveBatchSize => Mode == AnalyticsDeliveryMode.Batch ? MaxBatchSize : 1;

    /// <summary>Upper bound on records delivered per second, used for the CLI's delivery estimate.</summary>
    public double MaxRecordsPerSecond => PermitLimit * EffectiveBatchSize / (Window + SafetyMargin).TotalSeconds;

    /// <summary>Analytics allows 20 messages per 10 seconds (budget incident). See ADR 0007.</summary>
    [Range(1, 10_000)] public int PermitLimit { get; set; } = 20;

    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Added to the window so clock drift and network jitter can't push us over the limit.</summary>
    public TimeSpan SafetyMargin { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class IngestionOptions
{
    public const string Section = "Ingestion";

    [Range(1, 100_000)] public int MaxRecordsPerRequest { get; set; } = 10_000;

    /// <summary>Most records waiting in the queue. Uploads beyond this get 503 (back-pressure).</summary>
    [Range(1, 10_000_000)] public int QueueCapacity { get; set; } = 100_000;

    /// <summary>Concurrent enrichment consumers. They keep the delivery buffer filled so batches
    /// are full when a rate-limit permit becomes available.</summary>
    [Range(1, 64)] public int WorkerCount { get; set; } = 8;

    /// <summary>
    /// How long, from its first failure, a record keeps being retried for transient errors
    /// before it is dead-lettered. Based on time, not attempts, so an outage of minutes or
    /// hours doesn't dead-letter the backlog (ADR 0010). Alert well before it runs out.
    /// </summary>
    public TimeSpan RetryBudget { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Base delay for requeueing a failed record; doubles per attempt up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan IdempotencyKeyTtl { get; set; } = TimeSpan.FromHours(24);
}
