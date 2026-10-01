using Ingestion.Core.Domain;

namespace Ingestion.Cli;

/// <param name="LineNumber">1-based line number in the file, for messages to the user.</param>
/// <param name="StructuralError">Set when the row can't be mapped onto the header, e.g. too many fields.</param>
public sealed record CsvRow(int LineNumber, RawActivityRecord Record, string? StructuralError);

/// <summary>
/// Reads the SecOps CSV export: header row first, then one record per line.
/// <para>
/// Assumptions (from the sample file):
/// <list type="bullet">
/// <item>Delimiter is ';' by default (configurable).</item>
/// <item>Fields are never quoted and contain no delimiter. A full RFC 4180 parser (e.g.
/// CsvHelper) would replace this if the export starts quoting fields.</item>
/// <item>Columns are found by header name, so their order doesn't matter.</item>
/// <item>A row with fewer fields than the header leaves the trailing columns empty; the
/// validator then reports exactly which ones are missing.</item>
/// </list>
/// </para>
/// Rows are streamed (yield) rather than loaded at once, so memory stays flat for large exports.
/// </summary>
public static class CsvActivityReader
{
    public static readonly IReadOnlyList<string> RequiredColumns =
        ["id", "asset_name", "ip", "created_utc", "source", "category"];

    public static IEnumerable<CsvRow> Read(TextReader reader, char delimiter = ';')
    {
        var header = reader.ReadLine() ?? throw new InvalidDataException("The file is empty.");
        var columns = header.Split(delimiter).Select(c => c.Trim().ToLowerInvariant()).ToArray();
        var missing = RequiredColumns.Except(columns).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException(
                $"Header is missing column(s): {string.Join(", ", missing)}. " +
                $"Found: {string.Join(", ", columns)}. Is '{delimiter}' the right delimiter?");

        var index = RequiredColumns.ToDictionary(c => c, c => Array.IndexOf(columns, c));
        var lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = line.Split(delimiter);
            string? Field(string name) => index[name] < fields.Length ? fields[index[name]] : null;

            var record = new RawActivityRecord(
                Field("id"), Field("asset_name"), Field("ip"), Field("created_utc"), Field("source"), Field("category"));
            var error = fields.Length > columns.Length
                ? $"expected {columns.Length} fields but found {fields.Length}"
                : null;
            yield return new CsvRow(lineNumber, record, error);
        }
    }
}
