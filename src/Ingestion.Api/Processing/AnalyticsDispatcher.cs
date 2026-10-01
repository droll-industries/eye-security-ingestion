using Ingestion.Api.Clients;
using Ingestion.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Ingestion.Api.Processing;

/// <summary>
/// Stage 2 of 2: the only sender to Analytics (ADR 0009). Groups enriched records into batches
/// and sends a batch when it reaches <c>EffectiveBatchSize</c> or <c>BatchLinger</c> has passed
/// since its first record, whichever comes first. Each batch is one HTTP message, so one
/// rate-limit permit.
/// <para>
/// One dispatcher is enough: throughput is limited by permits, not by concurrency. While the
/// dispatcher waits for a permit, the next batch fills up in the <see cref="DeliveryBuffer"/>.
/// </para>
/// Outcomes per event: accepted/duplicate → done; rejected → dead-letter (permanent);
/// whole-batch failure → every record goes to <see cref="FailureHandler"/> (retry or dead-letter).
/// </summary>
public sealed class AnalyticsDispatcher(
    DeliveryBuffer buffer,
    IngestionQueue queue,
    IServiceScopeFactory scopeFactory,
    FailureHandler failures,
    IOptions<AnalyticsOptions> options,
    TimeProvider time,
    ILogger<AnalyticsDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await NextBatchAsync(stoppingToken).ConfigureAwait(false) is { Count: > 0 } batch)
                await SendAsync(batch, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Buffered records are lost with the in-memory pipeline (ADR 0002).
        }
    }

    /// <summary>Waits for a first record, then collects more until the batch is full or the linger time passes.</summary>
    internal async Task<List<PendingDelivery>> NextBatchAsync(CancellationToken stoppingToken)
    {
        var maxSize = options.Value.EffectiveBatchSize;
        var batch = new List<PendingDelivery>(maxSize);
        if (!await buffer.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false)) return batch;

        using var linger = new CancellationTokenSource(options.Value.BatchLinger, time);
        using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, linger.Token);
        while (batch.Count < maxSize)
        {
            if (buffer.Reader.TryRead(out var next))
            {
                batch.Add(next);
                continue;
            }

            try
            {
                if (!await buffer.Reader.WaitToReadAsync(waitToken.Token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                break; // linger elapsed: send what we have
            }
        }
        return batch;
    }

    internal async Task SendAsync(List<PendingDelivery> batch, CancellationToken stoppingToken)
    {
        IReadOnlyList<EventDeliveryResult> results;
        try
        {
            using var scope = scopeFactory.CreateScope();
            results = await scope.ServiceProvider.GetRequiredService<IAnalyticsClient>()
                .SendAsync([.. batch.Select(p => p.Event)], stoppingToken).ConfigureAwait(false);
            // Never index past the results: an exception escaping this BackgroundService would
            // stop the whole host (the .NET default). If we can't match results to records, retry.
            if (results.Count != batch.Count)
                throw new TransientDependencyException(
                    $"Analytics client returned {results.Count} results for {batch.Count} events");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Batch of {Count} events failed as a whole", batch.Count);
            foreach (var pending in batch) failures.Handle(pending.Item, ex, stoppingToken);
            return;
        }

        int accepted = 0, duplicates = 0, rejected = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            switch (results[i].Status)
            {
                case EventDeliveryStatus.Accepted:
                    accepted++;
                    queue.MarkCompleted();
                    break;
                case EventDeliveryStatus.Duplicate:
                    duplicates++;
                    queue.MarkCompleted();
                    break;
                default:
                    rejected++;
                    failures.Handle(batch[i].Item, new PermanentDependencyException(
                        $"Analytics rejected event {results[i].EventId}: {string.Join("; ", results[i].Errors ?? [])}"),
                        stoppingToken);
                    break;
            }
        }

        logger.LogInformation(
            "Delivered batch of {Count} events: {Accepted} accepted, {Duplicates} duplicate, {Rejected} rejected",
            batch.Count, accepted, duplicates, rejected);
    }
}
