using Ingestion.Cli;
using Ingestion.Core.Contracts;

namespace Ingestion.Tests.Cli;

public class RecordFilterTests
{
    private static ActivityRecordDto Record(string source, string category, DateTimeOffset created) =>
        new(1, "host", "1.2.3.4", created, source, category);

    private static readonly DateTimeOffset March15 = new(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_filter_matches_everything() =>
        Assert.True(RecordFilter.None.Matches(Record("defender", "Phishing", March15)));

    [Fact]
    public void Options_are_ANDed_and_values_ORed()
    {
        var filter = new RecordFilter(
            new HashSet<string> { "defender", "crowdstrike" }, new HashSet<string> { "Phishing" }, null, null);

        Assert.True(filter.Matches(Record("crowdstrike", "Phishing", March15)));
        Assert.False(filter.Matches(Record("crowdstrike", "Valid Accounts", March15)));
        Assert.False(filter.Matches(Record("pxtrpf", "Phishing", March15)));
    }

    [Fact]
    public void Date_only_to_is_inclusive_of_the_whole_day()
    {
        var filter = RecordFilter.None with
        {
            FromInclusive = RecordFilter.ParseDate("2024-03-15", endOfDay: false),
            ToExclusive = RecordFilter.ParseDate("2024-03-15", endOfDay: true),
        };

        Assert.True(filter.Matches(Record("x", "Phishing", March15)));
        Assert.True(filter.Matches(Record("x", "Phishing", new DateTimeOffset(2024, 3, 15, 23, 59, 0, TimeSpan.Zero))));
        Assert.False(filter.Matches(Record("x", "Phishing", new DateTimeOffset(2024, 3, 16, 0, 0, 0, TimeSpan.Zero))));
    }
}
