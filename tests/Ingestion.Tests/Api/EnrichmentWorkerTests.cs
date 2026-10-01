using Ingestion.Api.Clients;
using Ingestion.Api.Configuration;
using Ingestion.Api.Processing;
using Ingestion.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;

namespace Ingestion.Tests.Api;

public class EnrichmentWorkerTests
{
    private static readonly ActivityRecordDto Record =
        new(1, "host", "1.2.3.4", DateTimeOffset.UnixEpoch, "defender", "Phishing");

    private readonly FakeTimeProvider _time = new();
    private readonly FakeEnrichment _enrichment = new();
    private readonly IngestionQueue _queue;
    private readonly DeliveryBuffer _buffer = new(10);
    private readonly InMemoryDeadLetterSink _deadLetters;
    private readonly EnrichmentPause _pause;
    private readonly IngestionOptions _options = new()
    {
        RetryBudget = TimeSpan.FromHours(6),
        RetryBaseDelay = TimeSpan.FromSeconds(5),
        MaxRetryDelay = TimeSpan.FromMinutes(5),
    };
    private readonly EnrichmentWorker _worker;

    public EnrichmentWorkerTests()
    {
        _queue = new IngestionQueue(100, _time);
        _pause = new EnrichmentPause(_time);
        _deadLetters = new InMemoryDeadLetterSink(_time, NullLogger<InMemoryDeadLetterSink>.Instance);
        var failures = new FailureHandler(_queue, _deadLetters, Options.Create(_options), _time,
            NullLogger<FailureHandler>.Instance);
        var services = new ServiceCollection()
            .AddSingleton<IEnrichmentClient>(_enrichment)
            .AddScoped<RecordEnricher>()
            .BuildServiceProvider();
        _worker = new EnrichmentWorker(_queue, _buffer, services.GetRequiredService<IServiceScopeFactory>(),
            failures, _pause, Options.Create(_options),
            Options.Create(new EnrichmentOptions { CircuitBreakDuration = TimeSpan.FromSeconds(15) }));
    }

    private async Task<IngestionWorkItem> DequeueAsync()
    {
        Assert.True(_queue.TryEnqueueBatch([new IngestionWorkItem(Guid.NewGuid(), Record)]));
        return await _queue.DequeueAsync(CancellationToken.None); // as the worker loop does
    }

    /// <summary>Advances virtual time until a requeued record becomes available again.</summary>
    private async Task<IngestionWorkItem> AwaitRequeueAsync(TimeSpan step)
    {
        var next = _queue.DequeueAsync(CancellationToken.None).AsTask();
        for (var i = 0; i < 10_000 && !next.IsCompleted; i++)
        {
            _time.Advance(step);
            await Task.Delay(1); // let the requeue continuation run
        }
        return await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Enriched_record_is_handed_to_the_delivery_buffer_with_technique_and_event_id()
    {
        await _worker.ProcessAsync(await DequeueAsync(), CancellationToken.None);

        Assert.True(_buffer.Reader.TryRead(out var pending));
        Assert.Equal("T1566", pending.Event.TechniqueId);
        Assert.Equal("NL", pending.Event.Enrichment.GetProperty("country").GetString());
        Assert.Equal(RecordEnricher.ComputeEventId(Record), pending.Event.EventId);
        Assert.Equal(1, _queue.Pending); // still pending until Analytics confirms delivery
    }

    [Fact]
    public async Task Transient_failure_requeues_with_backoff_and_keeps_the_record_pending()
    {
        _enrichment.FailWith = new TransientDependencyException("503");
        await _worker.ProcessAsync(await DequeueAsync(), CancellationToken.None);

        Assert.Empty(_deadLetters.Items);
        Assert.Equal(1, _queue.Pending);

        var next = _queue.DequeueAsync(CancellationToken.None).AsTask();
        _time.Advance(TimeSpan.FromSeconds(3.9)); // jittered first delay is 4–5 s
        await Task.Delay(20);
        Assert.False(next.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1.1));
        var retried = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, retried.Attempt);
        Assert.Equal(_time.GetUtcNow() - TimeSpan.FromSeconds(5), retried.FirstFailedAt);
    }

    [Fact]
    public async Task A_30_minute_outage_delays_the_record_but_does_not_dead_letter_it()
    {
        // With the earlier attempt-based policy (5 attempts, about 75 s) this record was dead-lettered.
        var outageEnds = _time.GetUtcNow() + TimeSpan.FromMinutes(30);
        _enrichment.FailWhen = () => _time.GetUtcNow() < outageEnds ? new TransientDependencyException("503") : null;

        var item = await DequeueAsync();
        while (true)
        {
            await _worker.ProcessAsync(item, CancellationToken.None);
            if (_buffer.Reader.TryRead(out _)) break;
            Assert.Empty(_deadLetters.Items);
            item = await AwaitRequeueAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(_deadLetters.Items);
        Assert.True(item.Attempt > 5, $"expected many attempts, got {item.Attempt}");
    }

    [Fact]
    public async Task Outage_longer_than_the_retry_budget_dead_letters_the_record()
    {
        _options.RetryBudget = TimeSpan.FromHours(1);
        _enrichment.FailWith = new TransientDependencyException("503");

        var item = await DequeueAsync();
        while (_deadLetters.Items.Count == 0)
        {
            await _worker.ProcessAsync(item, CancellationToken.None);
            if (_deadLetters.Items.Count > 0) break;
            item = await AwaitRequeueAsync(TimeSpan.FromMinutes(1));
        }

        Assert.Contains("Retry budget", Assert.Single(_deadLetters.Items).Reason, StringComparison.Ordinal);
        Assert.Equal(0, _queue.Pending);
    }

    [Fact]
    public async Task Open_circuit_pauses_workers_and_requeues_without_using_up_an_attempt()
    {
        _enrichment.FailWith = new BrokenCircuitException("circuit open");
        await _worker.ProcessAsync(await DequeueAsync(), CancellationToken.None);

        Assert.True(_pause.IsPaused);
        var requeued = await _queue.DequeueAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, requeued.Attempt);
        Assert.Empty(_deadLetters.Items);

        _time.Advance(TimeSpan.FromSeconds(15));
        Assert.False(_pause.IsPaused);
    }

    [Fact]
    public async Task Permanent_failure_is_dead_lettered_immediately()
    {
        _enrichment.FailWith = new PermanentDependencyException("400");
        await _worker.ProcessAsync(await DequeueAsync(), CancellationToken.None);

        Assert.Single(_deadLetters.Items);
        Assert.Equal(0, _queue.Pending);
        Assert.False(_buffer.Reader.TryRead(out _));
    }

    [Fact]
    public void Backoff_doubles_is_capped_and_jittered_downwards_only()
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var delay = FailureHandler.BackoffDelay(attempt, _options);
            var expected = TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, attempt - 1), 300));
            Assert.InRange(delay, expected * 0.8, expected);
        }
    }
}
