using Ingestion.Api.Configuration;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace Ingestion.Api.Processing;

/// <summary>
/// Stage 1 of 2: <c>WorkerCount</c> concurrent consumers take records off the queue, enrich them
/// one at a time (enrichment has no batch API) and pass them to the <see cref="DeliveryBuffer"/>.
/// Failures go to <see cref="FailureHandler"/>. While the Enrichment circuit is open, all
/// workers pause instead of taking more records (<see cref="EnrichmentPause"/>, ADR 0010).
/// </summary>
public sealed class EnrichmentWorker(
    IngestionQueue queue,
    DeliveryBuffer buffer,
    IServiceScopeFactory scopeFactory,
    FailureHandler failures,
    EnrichmentPause pause,
    IOptions<IngestionOptions> options,
    IOptions<EnrichmentOptions> enrichmentOptions) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, options.Value.WorkerCount).Select(_ => ConsumeAsync(stoppingToken)));

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                // Wait *before* taking a record, so nothing is held back while paused.
                await pause.WaitAsync(stoppingToken).ConfigureAwait(false);
                var item = await queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
                await ProcessAsync(item, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Queued records are lost with the in-memory queue (ADR 0002).
        }
    }

    internal async Task ProcessAsync(IngestionWorkItem item, CancellationToken stoppingToken)
    {
        PendingDelivery pending;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var analyticsEvent = await scope.ServiceProvider.GetRequiredService<RecordEnricher>()
                .EnrichAsync(item, stoppingToken).ConfigureAwait(false);
            pending = new PendingDelivery(item, analyticsEvent);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is BrokenCircuitException) pause.PauseFor(enrichmentOptions.Value.CircuitBreakDuration);
            failures.Handle(item, ex, stoppingToken);
            return;
        }

        // Waits while the buffer is full: back-pressure from the rate-limited delivery stage.
        await buffer.Writer.WriteAsync(pending, stoppingToken).ConfigureAwait(false);
    }
}
