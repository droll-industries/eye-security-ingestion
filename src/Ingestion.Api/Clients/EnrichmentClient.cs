using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Ingestion.Api.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ingestion.Api.Clients;

public interface IEnrichmentClient
{
    /// <summary>Returns the enrichment payload for an IP address, treated as opaque JSON.</summary>
    Task<JsonElement> EnrichAsync(string ip, CancellationToken cancellationToken);
}

/// <summary>
/// ASSUMPTION (the Enrichment Service docs were not available, see ADR 0003):
/// <c>GET {BaseUrl}/enrichment?ip={ip}</c> returns a JSON object of data about that IP.
/// We pass the payload through unchanged as <c>enrichment</c>, so a schema change in the
/// Enrichment Service doesn't need a change here. Only this class knows the endpoint shape.
/// Resilience (retry, circuit breaker, timeouts) is set on the HttpClient in Program.cs.
/// </summary>
public sealed class EnrichmentClient(HttpClient http) : IEnrichmentClient
{
    public async Task<JsonElement> EnrichAsync(string ip, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            new Uri($"enrichment?ip={Uri.EscapeDataString(ip)}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        await HttpResponseClassifier.EnsureSuccessAsync(response, "Enrichment Service", cancellationToken).ConfigureAwait(false);

        try
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new PermanentDependencyException("Enrichment Service returned invalid JSON", ex);
        }
    }
}

/// <summary>
/// Enrichment cache shared by all workers (singleton). See ADR 0006/0010.
/// <list type="number">
/// <item><b>Fresh hit</b> (younger than CacheTtl): returned without a call. The sample has
/// 101 unique IPs across 992 records.</item>
/// <item><b>Single-flight:</b> concurrent misses for the same IP share <i>one</i> request instead of
/// each calling the struggling service. Previously about 160 calls were made for 101 IPs.</item>
/// <item><b>Stale-if-error:</b> if refreshing an expired entry fails transiently (including an
/// open circuit), the old result is used until it reaches MaxStaleAge. Repeat-offender IPs, the
/// common case in incident response, keep flowing during an outage.</item>
/// </list>
/// Assumption: enrichment depends only on the IP and changes slowly (ADR 0003). Memory: one
/// small JSON object per unique IP, evicted after MaxStaleAge. Production could swap in a
/// shared cache (Redis) so all instances benefit.
/// </summary>
public sealed class EnrichmentCache(
    IMemoryCache cache, IOptions<EnrichmentOptions> options, TimeProvider time, ILogger<EnrichmentCache> logger)
{
    private sealed record Entry(JsonElement Value, DateTimeOffset FetchedAt);

    private readonly ConcurrentDictionary<string, Lazy<Task<JsonElement>>> _inFlight = new();

    public async Task<JsonElement> GetAsync(
        string ip, Func<CancellationToken, Task<JsonElement>> fetch, CancellationToken cancellationToken)
    {
        var key = $"enrichment:{ip}";
        var opts = options.Value;
        cache.TryGetValue(key, out Entry? entry);
        var age = entry is null ? TimeSpan.MaxValue : time.GetUtcNow() - entry.FetchedAt;
        if (entry is not null && age < opts.CacheTtl) return entry.Value;

        try
        {
            // Lazy makes sure only one fetch starts per IP even when GetOrAdd races.
            var shared = _inFlight.GetOrAdd(ip, _ => new Lazy<Task<JsonElement>>(() => FetchAndStoreAsync(ip, key, fetch)));
            return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (entry is not null && age < opts.MaxStaleAge
                                   && !cancellationToken.IsCancellationRequested
                                   && Processing.FailureHandler.IsTransient(ex))
        {
            logger.LogWarning("Enrichment for {Ip} failed ({Error}); using cached result from {Age} ago",
                ip, ex.Message, age);
            return entry.Value;
        }
    }

    private async Task<JsonElement> FetchAndStoreAsync(
        string ip, string key, Func<CancellationToken, Task<JsonElement>> fetch)
    {
        try
        {
            // Not tied to any one caller's token: other callers may be waiting on the same
            // fetch. It is bounded by the resilience pipeline's total timeout instead.
            var value = await fetch(CancellationToken.None).ConfigureAwait(false);
            cache.Set(key, new Entry(value, time.GetUtcNow()), options.Value.MaxStaleAge);
            return value;
        }
        finally
        {
            _inFlight.TryRemove(ip, out _);
        }
    }
}

/// <summary>The <see cref="IEnrichmentClient"/> the pipeline uses: the HTTP client behind <see cref="EnrichmentCache"/>.</summary>
public sealed class CachingEnrichmentClient(EnrichmentClient inner, EnrichmentCache cache) : IEnrichmentClient
{
    public Task<JsonElement> EnrichAsync(string ip, CancellationToken cancellationToken) =>
        cache.GetAsync(ip, ct => inner.EnrichAsync(ip, ct), cancellationToken);
}
