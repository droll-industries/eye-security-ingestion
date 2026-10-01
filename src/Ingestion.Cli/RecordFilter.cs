using System.Globalization;
using Ingestion.Core.Contracts;

namespace Ingestion.Cli;

/// <summary>
/// The optional ingestion filter (the ticket's nice-to-have). Applied after validation, so
/// it matches normalized values: <c>--category phishing</c> also matches "Phising" in the file.
/// Different options are combined with AND; several values for one option are combined with OR.
/// </summary>
public sealed record RecordFilter(
    IReadOnlySet<string> Sources,
    IReadOnlySet<string> Categories,
    DateTimeOffset? FromInclusive,
    DateTimeOffset? ToExclusive)
{
    public static readonly RecordFilter None = new(
        new HashSet<string>(), new HashSet<string>(), null, null);

    public bool IsEmpty => Sources.Count == 0 && Categories.Count == 0 && FromInclusive is null && ToExclusive is null;

    public bool Matches(ActivityRecordDto record) =>
        (Sources.Count == 0 || Sources.Contains(record.Source)) &&
        (Categories.Count == 0 || Categories.Contains(record.Category)) &&
        (FromInclusive is null || record.CreatedUtc >= FromInclusive) &&
        (ToExclusive is null || record.CreatedUtc < ToExclusive);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Sources.Count > 0) parts.Add($"source in [{string.Join(", ", Sources)}]");
        if (Categories.Count > 0) parts.Add($"category in [{string.Join(", ", Categories)}]");
        if (FromInclusive is { } from) parts.Add($"created_utc >= {from:yyyy-MM-dd HH:mm}");
        if (ToExclusive is { } to) parts.Add($"created_utc < {to:yyyy-MM-dd HH:mm}");
        return parts.Count == 0 ? "none" : string.Join(" AND ", parts);
    }

    /// <summary>Date-only values cover the whole day (UTC); --to is inclusive of that day.</summary>
    public static DateTimeOffset? ParseDate(string? value, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return endOfDay ? start.AddDays(1) : start;
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : null;
    }
}
