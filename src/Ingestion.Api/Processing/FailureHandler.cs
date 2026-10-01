using Ingestion.Api.Clients;
using Ingestion.Api.Configuration;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

namespace Ingestion.Api.Processing;

/// <summary>
/// Record-level failure policy, shared by the enrichment and delivery stages (ADR 0006, 0010).
/// <list type="bullet">
/// <item><b>Permanent</b> failure (400/422, rejected event) → dead-letter now. Auth/URL errors are
/// <i>not</i> permanent: they affect every record (see HttpResponseClassifier).</item>
/// <item><b>Transient</b> failure → requeue with exponential back-off (capped, with jitter), for
/// as long as the record is within its <i>time-based</i> retry budget. A dependency outage
/// therefore delays records instead of dead-lettering them.</item>
/// <item><b>Open circuit</b> → requeue immediately <i>without</i> counting an attempt. The call never
/// reached the service, so it says nothing about this record; the workers are paused anyway
/// (<see cref="EnrichmentPause"/>). The retry budget still applies.</item>
/// </list>
/// A requeued record goes through enrichment again; the per-IP cache usually makes that free.
/// </summary>
public sealed class FailureHandler(
    IngestionQueue queue,
    IDeadLetterSink deadLetters,
    IOptions<IngestionOptions> options,
    TimeProvider time,
    ILogger<FailureHandler> logger)
{
    public void Handle(IngestionWorkItem item, Exception ex, CancellationToken stoppingToken)
    {
        var now = time.GetUtcNow();
        var firstFailedAt = item.FirstFailedAt ?? now;
        var opts = options.Value;

        if (!IsTransient(ex))
        {
            DeadLetter(item, ex);
            return;
        }
        if (now - firstFailedAt >= opts.RetryBudget)
        {
            DeadLetter(item, new TransientDependencyException(
                $"Retry budget of {opts.RetryBudget} exhausted after {item.Attempt} attempt(s): {ex.Message}", ex));
            return;
        }

        if (ex is BrokenCircuitException)
        {
            queue.Requeue(item with { FirstFailedAt = firstFailedAt }, TimeSpan.Zero, stoppingToken);
            return;
        }

        var delay = BackoffDelay(item.Attempt, opts);
        if (ex is TransientDependencyException { IsConfigurationError: true })
            logger.LogError(ex, "Dependency rejects our configuration; record {RecordId} will be retried in {Delay}. Fix the URL/credentials",
                item.Record.Id, delay);
        else
            logger.LogWarning(ex, "Transient failure for record {RecordId} (attempt {Attempt}); retrying in {Delay}",
                item.Record.Id, item.Attempt, delay);
        queue.Requeue(item with { Attempt = item.Attempt + 1, FirstFailedAt = firstFailedAt }, delay, stoppingToken);
    }

    /// <summary>
    /// base × 2^(attempt-1), capped at MaxRetryDelay, then scaled by a random 80–100 %. The jitter
    /// spreads out records that failed together (e.g. a whole batch), so they don't all retry
    /// at the same moment.
    /// </summary>
    internal static TimeSpan BackoffDelay(int attempt, IngestionOptions opts)
    {
        var exponential = opts.RetryBaseDelay * Math.Pow(2, Math.Min(attempt - 1, 30));
        var capped = exponential < opts.MaxRetryDelay ? exponential : opts.MaxRetryDelay;
        return capped * (0.8 + 0.2 * Random.Shared.NextDouble());
    }

    private void DeadLetter(IngestionWorkItem item, Exception ex)
    {
        deadLetters.Add(item, ex);
        queue.MarkCompleted();
    }

    internal static bool IsTransient(Exception ex) => ex is
        TransientDependencyException
        or HttpRequestException
        or ExecutionRejectedException // Polly: open circuit, timeout, rate limiter rejection
        or TimeoutException
        or TaskCanceledException; // HttpClient timeout (shutdown is handled by the callers)
}
