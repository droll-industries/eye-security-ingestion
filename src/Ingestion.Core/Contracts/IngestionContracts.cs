namespace Ingestion.Core.Contracts;

public sealed record IngestionRequest(IReadOnlyList<ActivityRecordDto> Records);

/// <summary>
/// Returned with 202 Accepted. <see cref="Accepted"/> records are queued for
/// enrichment and delivery to Analytics; they are not yet in Analytics.
/// </summary>
/// <param name="EstimatedDeliverySeconds">
/// Best-case time until everything queued so far (this upload plus the backlog ahead of it)
/// reaches Analytics, given the rate limit and batch size. Retries make it longer.
/// </param>
public sealed record IngestionResponse(
    Guid IngestionId,
    int Received,
    int Accepted,
    IReadOnlyList<RecordRejection> Rejected,
    int EstimatedDeliverySeconds = 0);

/// <param name="Index">Zero-based position of the record in <see cref="IngestionRequest.Records"/>.</param>
public sealed record RecordRejection(int Index, long? Id, IReadOnlyList<string> Errors);

public static class IngestionHeaders
{
    /// <summary>Client-generated key that makes a retried upload safe (no double ingestion).</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>Response header set when the response is a replay of an earlier, identical request.</summary>
    public const string IdempotentReplayed = "Idempotent-Replayed";
}
