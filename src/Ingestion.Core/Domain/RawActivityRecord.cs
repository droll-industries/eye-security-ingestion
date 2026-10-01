namespace Ingestion.Core.Domain;

/// <summary>A CSV row as untyped strings, before parsing. Null means the column was missing.</summary>
public sealed record RawActivityRecord(
    string? Id,
    string? AssetName,
    string? Ip,
    string? CreatedUtc,
    string? Source,
    string? Category);
