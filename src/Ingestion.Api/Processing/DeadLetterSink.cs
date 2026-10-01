using System.Collections.Concurrent;

namespace Ingestion.Api.Processing;

public sealed record DeadLetter(IngestionWorkItem Item, string Reason, DateTimeOffset At);

public interface IDeadLetterSink
{
    void Add(IngestionWorkItem item, Exception reason);
}

/// <summary>
/// Keeps records that could not be delivered after every retry, so no record is silently
/// dropped. Each one is logged at Error level with enough detail to redrive it.
/// Production: the broker's dead-letter queue, plus an alert on its depth and a redrive
/// tool (ADR 0002). In memory here only to keep the solution small.
/// </summary>
public sealed class InMemoryDeadLetterSink(TimeProvider time, ILogger<InMemoryDeadLetterSink> logger) : IDeadLetterSink
{
    private readonly ConcurrentQueue<DeadLetter> _items = new();

    public IReadOnlyCollection<DeadLetter> Items => _items;

    public void Add(IngestionWorkItem item, Exception reason)
    {
        _items.Enqueue(new DeadLetter(item, reason.Message, time.GetUtcNow()));
        logger.LogError(reason,
            "Dead-lettered record {RecordId} from ingestion {IngestionId} after {Attempt} attempt(s)",
            item.Record.Id, item.IngestionId, item.Attempt);
    }
}
