using System.Threading.Channels;
using Ingestion.Core.Contracts;

namespace Ingestion.Api.Processing;

/// <param name="Attempt">Processing attempt, starting at 1. Drives the back-off delay.</param>
/// <param name="FirstFailedAt">When the record first failed; the retry budget is measured from here.</param>
public sealed record IngestionWorkItem(
    Guid IngestionId, ActivityRecordDto Record, int Attempt = 1, DateTimeOffset? FirstFailedAt = null);

/// <summary>
/// Queue between the HTTP endpoint and the background workers. Accepting an upload only
/// needs a quick in-memory write; the slow, rate-limited work happens here, after the
/// response has been sent.
/// <para>
/// IMPORTANT LIMITATION (ADR 0002): this is in memory. A crash or redeploy loses queued
/// records. For a mission-critical system, production would put a durable broker here
/// (Azure Service Bus / SQS / RabbitMQ) with a native dead-letter queue. The interface
/// stays the same: enqueue a batch on upload, consume one record at a time.
/// </para>
/// </summary>
public sealed class IngestionQueue(int capacity, TimeProvider time)
{
    private readonly Channel<IngestionWorkItem> _channel =
        Channel.CreateUnbounded<IngestionWorkItem>(new UnboundedChannelOptions { SingleReader = false });

    private int _pending;

    /// <summary>Records accepted but not yet delivered or dead-lettered (includes retry back-off).</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Enqueues the whole batch, or nothing if it would go over capacity.</summary>
    public bool TryEnqueueBatch(IReadOnlyCollection<IngestionWorkItem> items)
    {
        if (Interlocked.Add(ref _pending, items.Count) > capacity)
        {
            Interlocked.Add(ref _pending, -items.Count);
            return false;
        }

        foreach (var item in items) _channel.Writer.TryWrite(item); // unbounded: always succeeds
        return true;
    }

    /// <summary>Puts a record back after a delay. Still counted as pending meanwhile.</summary>
    public void Requeue(IngestionWorkItem item, TimeSpan delay, CancellationToken cancellationToken) =>
        _ = Task.Delay(delay, time, cancellationToken)
            .ContinueWith(_ => _channel.Writer.TryWrite(item), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    /// <summary>Called once a record is finished, either delivered or dead-lettered.</summary>
    public void MarkCompleted() => Interlocked.Decrement(ref _pending);

    public ValueTask<IngestionWorkItem> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
