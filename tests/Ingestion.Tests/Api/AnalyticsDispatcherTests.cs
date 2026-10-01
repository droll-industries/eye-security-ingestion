using System.Text.Json;
using Ingestion.Api.Clients;
using Ingestion.Api.Configuration;
using Ingestion.Api.Processing;
using Ingestion.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Ingestion.Tests.Api;

public class AnalyticsDispatcherTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeAnalytics _analytics = new();
    private readonly IngestionQueue _queue;
    private readonly DeliveryBuffer _buffer = new(100);
    private readonly InMemoryDeadLetterSink _deadLetters;
    private readonly AnalyticsOptions _options = new() { MaxBatchSize = 3, BatchLinger = TimeSpan.FromSeconds(2) };
    private readonly AnalyticsDispatcher _dispatcher;

    public AnalyticsDispatcherTests()
    {
        _queue = new IngestionQueue(100, _time);
        _deadLetters = new InMemoryDeadLetterSink(_time, NullLogger<InMemoryDeadLetterSink>.Instance);
        var failures = new FailureHandler(_queue, _deadLetters,
            Options.Create(new IngestionOptions()), _time, NullLogger<FailureHandler>.Instance);
        var services = new ServiceCollection().AddSingleton<IAnalyticsClient>(_analytics).BuildServiceProvider();
        _dispatcher = new AnalyticsDispatcher(_buffer, _queue, services.GetRequiredService<IServiceScopeFactory>(),
            failures, Options.Create(_options), _time, NullLogger<AnalyticsDispatcher>.Instance);
    }

    /// <summary>Puts records into the pipeline as the enrichment stage would (queued + buffered).</summary>
    private List<PendingDelivery> Buffer(int count)
    {
        var items = Enumerable.Range(1, count).Select(i => new IngestionWorkItem(Guid.NewGuid(),
            new ActivityRecordDto(i, "host", "1.2.3.4", DateTimeOffset.UnixEpoch, "defender", "Phishing"))).ToList();
        Assert.True(_queue.TryEnqueueBatch(items));
        var pending = items.Select(i => new PendingDelivery(i, new AnalyticsEvent(
            $"{i.Record.Id:x32}", i.IngestionId, i.Record.Id, "host", "1.2.3.4", DateTimeOffset.UnixEpoch,
            "defender", "Phishing", "T1566", JsonSerializer.SerializeToElement(new { ip = "1.2.3.4" })))).ToList();
        foreach (var p in pending) Assert.True(_buffer.Writer.TryWrite(p));
        return pending;
    }

    [Fact]
    public async Task Full_batch_is_sent_without_waiting_for_the_linger_time()
    {
        Buffer(7);

        var first = await _dispatcher.NextBatchAsync(CancellationToken.None);
        var second = await _dispatcher.NextBatchAsync(CancellationToken.None);

        Assert.Equal(3, first.Count);
        Assert.Equal(3, second.Count);
    }

    [Fact]
    public async Task Partial_batch_is_sent_when_the_linger_time_elapses()
    {
        Buffer(2);
        var next = _dispatcher.NextBatchAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(1.9));
        await Task.Delay(50);
        Assert.False(next.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        var batch = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, batch.Count);
    }

    [Fact]
    public async Task SingleEvent_mode_sends_batches_of_one()
    {
        _options.Mode = AnalyticsDeliveryMode.SingleEvent;
        Buffer(2);

        Assert.Single(await _dispatcher.NextBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Accepted_and_duplicate_events_complete_and_rejected_ones_are_dead_lettered()
    {
        var batch = Buffer(3);
        _analytics.StatusFor = e => e.RecordId switch
        {
            1 => EventDeliveryStatus.Accepted,
            2 => EventDeliveryStatus.Duplicate,
            _ => EventDeliveryStatus.Rejected,
        };

        await _dispatcher.SendAsync(batch, CancellationToken.None);

        Assert.Single(_analytics.Batches); // one message for the whole batch
        var dead = Assert.Single(_deadLetters.Items);
        Assert.Equal(3, dead.Item.Record.Id);
        Assert.Equal(0, _queue.Pending);
    }

    [Fact]
    public async Task Result_count_mismatch_requeues_the_batch_instead_of_crashing()
    {
        var batch = Buffer(3);
        _analytics.DropResults = 1;

        await _dispatcher.SendAsync(batch, CancellationToken.None);

        Assert.Empty(_deadLetters.Items);
        Assert.Equal(3, _queue.Pending);
    }

    [Fact]
    public async Task Whole_batch_transient_failure_requeues_every_record()
    {
        var batch = Buffer(3);
        _analytics.FailWith = new TransientDependencyException("503");

        await _dispatcher.SendAsync(batch, CancellationToken.None);

        Assert.Empty(_deadLetters.Items);
        Assert.Equal(3, _queue.Pending); // all waiting for their retry
    }
}
