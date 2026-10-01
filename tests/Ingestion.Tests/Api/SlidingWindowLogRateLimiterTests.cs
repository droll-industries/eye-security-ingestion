using Ingestion.Api.RateLimiting;
using Microsoft.Extensions.Time.Testing;

namespace Ingestion.Tests.Api;

public class SlidingWindowLogRateLimiterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Grants_up_to_the_limit_immediately_then_blocks_until_the_oldest_permit_expires()
    {
        var time = new FakeTimeProvider();
        using var limiter = new SlidingWindowLogRateLimiter(20, Window, time);

        for (var i = 0; i < 20; i++) await limiter.WaitAsync(CancellationToken.None);

        var twentyFirst = limiter.WaitAsync(CancellationToken.None);
        Assert.False(twentyFirst.IsCompleted);

        time.Advance(Window - TimeSpan.FromMilliseconds(1));
        Assert.False(twentyFirst.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await twentyFirst.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Never_exceeds_the_limit_in_any_window_even_across_boundaries()
    {
        // The case where fixed-window and token-bucket limiters go wrong: a burst at the
        // end of one window followed by a burst at the start of the next.
        var time = new FakeTimeProvider();
        using var limiter = new SlidingWindowLogRateLimiter(20, Window, time);
        var grants = new List<DateTimeOffset>();

        for (var i = 0; i < 10; i++) { await limiter.WaitAsync(CancellationToken.None); grants.Add(time.GetUtcNow()); }
        time.Advance(TimeSpan.FromSeconds(9.9));

        var pending = Task.Run(async () =>
        {
            for (var i = 0; i < 30; i++) { await limiter.WaitAsync(CancellationToken.None); lock (grants) grants.Add(time.GetUtcNow()); }
        });
        while (!pending.IsCompleted)
        {
            await Task.Delay(1);
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        await pending;

        Assert.Equal(40, grants.Count);
        foreach (var start in grants)
            Assert.True(grants.Count(g => g >= start && g < start + Window) <= 20,
                $"more than 20 grants in the window starting {start:O}");
    }

    [Fact]
    public async Task Waiting_can_be_cancelled()
    {
        var time = new FakeTimeProvider();
        using var limiter = new SlidingWindowLogRateLimiter(1, Window, time);
        await limiter.WaitAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiting = limiter.WaitAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
