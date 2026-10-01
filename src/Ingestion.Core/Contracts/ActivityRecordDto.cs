namespace Ingestion.Core.Contracts;

/// <summary>
/// One malicious-activity record as sent over the wire from the CLI to the API.
/// The CLI sends records that are already parsed and normalized. The API validates
/// them again because it is the system of record and cannot trust its clients.
/// </summary>
public sealed record ActivityRecordDto(
    long Id,
    string AssetName,
    string Ip,
    DateTimeOffset CreatedUtc,
    string Source,
    string Category);
