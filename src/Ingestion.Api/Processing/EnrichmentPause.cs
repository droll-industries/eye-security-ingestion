namespace Ingestion.Api.Processing;

/// <summary>
/// Shared pause for the enrichment workers while the Enrichment circuit breaker is open
/// (ADR 0010). Without it, workers keep taking records, each one fails immediately on the open
/// circuit and goes back on the queue, which only produces load, logs and noise. With it, the
/// queue simply grows during the outage and is worked off once the service is back.
/// </summary>
public sealed class EnrichmentPause(TimeProvider time)
{
    private readonly Lock _lock = new();
    private DateTimeOffset _until = DateTimeOffset.MinValue;

    public bool IsPaused
    {
        get { lock (_lock) return time.GetUtcNow() < _until; }
    }

    /// <summary>Pauses until at least now + <paramref name="duration"/> (never shortens a pause).</summary>
    public void PauseFor(TimeSpan duration)
    {
        lock (_lock)
        {
            var until = time.GetUtcNow() + duration;
            if (until > _until) _until = until;
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_lock) remaining = _until - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining, time, cancellationToken).ConfigureAwait(false);
        }
    }
}
