using System.Text.Json;
using Ingestion.Api.Clients;
using Ingestion.Api.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;

namespace Ingestion.Tests.Api;

public sealed class EnrichmentCacheTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly EnrichmentCache _cache;
    private int _fetches;

    public EnrichmentCacheTests() =>
        _cache = new EnrichmentCache(_memory,
            Options.Create(new EnrichmentOptions { CacheTtl = TimeSpan.FromMinutes(15), MaxStaleAge = TimeSpan.FromHours(24) }),
            _time, NullLogger<EnrichmentCache>.Instance);

    private Func<CancellationToken, Task<JsonElement>> Returns(string marker) => _ =>
    {
        Interlocked.Increment(ref _fetches);
        return Task.FromResult(JsonSerializer.SerializeToElement(new { marker }));
    };

    private Func<CancellationToken, Task<JsonElement>> Throws(Exception ex) => _ =>
    {
        Interlocked.Increment(ref _fetches);
        return Task.FromException<JsonElement>(ex);
    };

    private static string Marker(JsonElement e) => e.GetProperty("marker").GetString()!;

    [Fact]
    public async Task Fresh_entry_is_served_without_a_call()
    {
        await _cache.GetAsync("1.2.3.4", Returns("a"), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(14));
        var result = await _cache.GetAsync("1.2.3.4", Returns("b"), CancellationToken.None);

        Assert.Equal("a", Marker(result));
        Assert.Equal(1, _fetches);
    }

    [Fact]
    public async Task Concurrent_misses_for_the_same_ip_share_one_request()
    {
        var gate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonElement> Fetch(CancellationToken _)
        {
            Interlocked.Increment(ref _fetches);
            return gate.Task;
        }

        var callers = Enumerable.Range(0, 8).Select(_ => _cache.GetAsync("1.2.3.4", Fetch, CancellationToken.None)).ToList();
        gate.SetResult(JsonSerializer.SerializeToElement(new { marker = "shared" }));
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, _fetches);
        Assert.All(results, r => Assert.Equal("shared", Marker(r)));
    }

    [Fact]
    public async Task Expired_entry_is_refreshed()
    {
        await _cache.GetAsync("1.2.3.4", Returns("old"), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal("new", Marker(await _cache.GetAsync("1.2.3.4", Returns("new"), CancellationToken.None)));
    }

    [Fact]
    public async Task Failed_refresh_serves_the_stale_entry_during_an_outage()
    {
        await _cache.GetAsync("1.2.3.4", Returns("old"), CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(3));

        var result = await _cache.GetAsync("1.2.3.4", Throws(new BrokenCircuitException("open")), CancellationToken.None);

        Assert.Equal("old", Marker(result));
    }

    [Fact]
    public async Task Stale_entry_beyond_max_age_is_not_served()
    {
        await _cache.GetAsync("1.2.3.4", Returns("old"), CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(25));

        await Assert.ThrowsAsync<TransientDependencyException>(() =>
            _cache.GetAsync("1.2.3.4", Throws(new TransientDependencyException("503")), CancellationToken.None));
    }

    [Fact]
    public async Task Permanent_errors_are_not_masked_by_stale_data()
    {
        await _cache.GetAsync("1.2.3.4", Returns("old"), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(16));

        await Assert.ThrowsAsync<PermanentDependencyException>(() =>
            _cache.GetAsync("1.2.3.4", Throws(new PermanentDependencyException("401")), CancellationToken.None));
    }

    [Fact]
    public async Task Miss_without_any_cached_entry_propagates_the_failure() =>
        await Assert.ThrowsAsync<TransientDependencyException>(() =>
            _cache.GetAsync("9.9.9.9", Throws(new TransientDependencyException("503")), CancellationToken.None));

    public void Dispose() => _memory.Dispose();
}
