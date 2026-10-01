using Ingestion.Api.Processing;
using Microsoft.Extensions.Time.Testing;

namespace Ingestion.Tests.Api;

public class EnrichmentPauseTests
{
    [Fact]
    public async Task WaitAsync_blocks_until_the_pause_ends_and_pauses_are_never_shortened()
    {
        var time = new FakeTimeProvider();
        var pause = new EnrichmentPause(time);
        await pause.WaitAsync(CancellationToken.None); // not paused: returns immediately

        pause.PauseFor(TimeSpan.FromSeconds(15));
        pause.PauseFor(TimeSpan.FromSeconds(5)); // shorter: ignored
        var waiting = pause.WaitAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(20);
        Assert.False(waiting.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
