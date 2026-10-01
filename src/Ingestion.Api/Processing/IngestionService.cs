using System.Security.Cryptography;
using System.Text.Json;
using Ingestion.Api.Configuration;
using Ingestion.Core.Contracts;
using Ingestion.Core.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ingestion.Api.Processing;

public abstract record SubmitOutcome
{
    /// <param name="AllRejected">True if no record was valid (the endpoint returns 422).</param>
    /// <param name="Replayed">True if this is the stored result of an earlier request with the same idempotency key.</param>
    public sealed record Accepted(IngestionResponse Response, bool AllRejected, bool Replayed = false) : SubmitOutcome;
    public sealed record BadRequest(string Detail) : SubmitOutcome;
    public sealed record QueueFull : SubmitOutcome;
    public sealed record IdempotencyConflict : SubmitOutcome;
}

/// <summary>Validates an upload, enqueues the valid records and builds the per-record report.</summary>
public sealed class IngestionService(
    IngestionQueue queue,
    IMemoryCache cache,
    IOptions<IngestionOptions> options,
    IOptions<AnalyticsOptions> analyticsOptions,
    TimeProvider time,
    ILogger<IngestionService> logger) : IDisposable
{
    // Serializes keyed submissions so two concurrent retries with the same key can't both
    // enqueue. Enqueueing is in memory and fast, so one global lock is cheap enough.
    private readonly SemaphoreSlim _idempotencyLock = new(1, 1);

    private sealed record IdempotencyEntry(string Fingerprint, SubmitOutcome.Accepted Outcome);

    public async Task<SubmitOutcome> SubmitAsync(
        IngestionRequest? request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (request?.Records is null || request.Records.Count == 0)
            return new SubmitOutcome.BadRequest("Request must contain at least one record.");
        if (request.Records.Count > options.Value.MaxRecordsPerRequest)
            return new SubmitOutcome.BadRequest(
                $"At most {options.Value.MaxRecordsPerRequest} records per request; split the upload.");

        if (string.IsNullOrWhiteSpace(idempotencyKey)) return Process(request);

        // Assumption: idempotency only has to hold within one instance and for the key's TTL.
        // When scaled out this would be a shared store (Redis / DB unique constraint).
        var fingerprint = Fingerprint(request);
        var cacheKey = $"idempotency:{idempotencyKey}";
        await _idempotencyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(cacheKey, out IdempotencyEntry? existing) && existing is not null)
            {
                if (existing.Fingerprint != fingerprint) return new SubmitOutcome.IdempotencyConflict();
                logger.LogInformation("Replaying ingestion {IngestionId} for idempotency key {Key}",
                    existing.Outcome.Response.IngestionId, idempotencyKey);
                return existing.Outcome with { Replayed = true };
            }

            var outcome = Process(request);
            if (outcome is SubmitOutcome.Accepted accepted) // a full queue is retryable, so don't cache it
                cache.Set(cacheKey, new IdempotencyEntry(fingerprint, accepted), options.Value.IdempotencyKeyTtl);
            return outcome;
        }
        finally
        {
            _idempotencyLock.Release();
        }
    }

    private SubmitOutcome Process(IngestionRequest request)
    {
        var ingestionId = Guid.CreateVersion7();
        var accepted = new List<IngestionWorkItem>(request.Records.Count);
        var rejected = new List<RecordRejection>();

        for (var i = 0; i < request.Records.Count; i++)
        {
            var record = request.Records[i];
            if (record is null)
            {
                rejected.Add(new RecordRejection(i, null, ["record is null"]));
                continue;
            }

            var result = RecordValidator.Validate(record, time);
            if (result.IsValid) accepted.Add(new IngestionWorkItem(ingestionId, result.Value!));
            else rejected.Add(new RecordRejection(i, record.Id, result.Errors));
        }

        if (accepted.Count > 0 && !queue.TryEnqueueBatch(accepted))
        {
            logger.LogWarning("Queue full ({Pending} pending); rejecting ingestion of {Count} records",
                queue.Pending, accepted.Count);
            return new SubmitOutcome.QueueFull();
        }

        logger.LogInformation(
            "Ingestion {IngestionId}: received {Received}, accepted {Accepted}, rejected {Rejected}",
            ingestionId, request.Records.Count, accepted.Count, rejected.Count);

        return new SubmitOutcome.Accepted(
            new IngestionResponse(ingestionId, request.Records.Count, accepted.Count, rejected,
                EstimatedDeliverySeconds: (int)Math.Ceiling(queue.Pending / analyticsOptions.Value.MaxRecordsPerSecond)),
            AllRejected: accepted.Count == 0);
    }

    public void Dispose() => _idempotencyLock.Dispose();

    private static string Fingerprint(IngestionRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request.Records)));
}
