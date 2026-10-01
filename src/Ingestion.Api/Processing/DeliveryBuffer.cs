using System.Threading.Channels;
using Ingestion.Api.Clients;

namespace Ingestion.Api.Processing;

public sealed record PendingDelivery(IngestionWorkItem Item, AnalyticsEvent Event);

/// <summary>
/// Bounded hand-off between enrichment workers and the Analytics dispatcher. When it's full,
/// enrichment pauses (back-pressure), so we don't enrich far ahead of what the rate limit lets
/// us deliver. Sized to hold about two batches, so the next batch is ready when a permit frees up.
/// </summary>
public sealed class DeliveryBuffer(int capacity)
{
    private readonly Channel<PendingDelivery> _channel = Channel.CreateBounded<PendingDelivery>(
        new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public ChannelWriter<PendingDelivery> Writer => _channel.Writer;
    public ChannelReader<PendingDelivery> Reader => _channel.Reader;
}
