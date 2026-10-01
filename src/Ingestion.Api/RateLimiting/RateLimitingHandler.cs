namespace Ingestion.Api.RateLimiting;

/// <summary>
/// Takes a permit before every outgoing request. It sits inside the retry handler, so retries
/// also use up the budget (each one is a message as far as Analytics is concerned).
/// </summary>
public sealed class RateLimitingHandler(SlidingWindowLogRateLimiter limiter) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
