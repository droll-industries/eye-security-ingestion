namespace Ingestion.Api.RateLimiting;

/// <summary>
/// Sliding-log rate limiter: allows at most <c>permitLimit</c> acquisitions in any period of
/// length <c>window</c>, without exception.
/// <para>
/// Why not System.Threading.RateLimiting? Fixed-window and token-bucket limiters allow up to
/// 2x the limit across a window boundary. The segmented sliding window is only accurate to
/// one segment. Analytics enforces a hard limit with a budget behind it, so we keep the
/// exact timestamps of the last N permits (N = 20, so cheap) instead. See ADR 0007.
/// </para>
/// <para>
/// Waiters are served in order: the lock is held while waiting for a slot, so callers queue
/// on the semaphore. Scope: one process. If the API scales out, this must move to a shared
/// store (e.g. Redis) or be enforced at one egress point (ADR 0007).
/// </para>
/// </summary>
public sealed class SlidingWindowLogRateLimiter(int permitLimit, TimeSpan window, TimeProvider time) : IDisposable
{
    private readonly Queue<DateTimeOffset> _grants = new(permitLimit);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = time.GetUtcNow();
                while (_grants.Count > 0 && now - _grants.Peek() >= window) _grants.Dequeue();

                if (_grants.Count < permitLimit)
                {
                    _grants.Enqueue(now);
                    return;
                }

                var waitFor = _grants.Peek() + window - now;
                await Task.Delay(waitFor, time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
