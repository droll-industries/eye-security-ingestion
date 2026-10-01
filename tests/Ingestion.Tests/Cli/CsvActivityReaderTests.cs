using Ingestion.Cli;
using Ingestion.Core.Domain;
using Microsoft.Extensions.Time.Testing;

namespace Ingestion.Tests.Cli;

public class CsvActivityReaderTests
{
    [Fact]
    public void Sample_file_has_999_rows_of_which_exactly_7_are_invalid()
    {
        using var reader = File.OpenText(Path.Combine(AppContext.BaseDirectory, "TestData", "example_data.csv"));
        var clock = new FakeTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var rows = CsvActivityReader.Read(reader).ToList();
        var invalidLines = rows
            .Where(r => r.StructuralError is not null || !RecordValidator.Parse(r.Record, clock).IsValid)
            .Select(r => r.LineNumber)
            .ToList();

        Assert.Equal(999, rows.Count);
        // Known bad rows (found while profiling the file): missing column, IP "N",
        // four "null" sources, one empty category.
        Assert.Equal([105, 436, 604, 613, 647, 667, 675], invalidLines);
    }

    [Fact]
    public void Maps_columns_by_header_name_not_position()
    {
        var csv = "category;source;created_utc;ip;asset_name;id\nphising;defender;01/01/2024 00:00;1.2.3.4;host;42\n";
        var row = Assert.Single(CsvActivityReader.Read(new StringReader(csv)));
        Assert.Equal("42", row.Record.Id);
        Assert.Equal("phising", row.Record.Category);
    }

    [Fact]
    public void Short_row_leaves_missing_columns_null_and_long_row_is_a_structural_error()
    {
        var csv = "id;asset_name;ip;created_utc;source;category\n1;a;1.2.3.4;01/01/2024 00:00;x\n\n2;a;1.2.3.4;01/01/2024 00:00;x;phising;extra\n";
        var rows = CsvActivityReader.Read(new StringReader(csv)).ToList();

        Assert.Equal(2, rows.Count); // blank line skipped
        Assert.Null(rows[0].Record.Category);
        Assert.Null(rows[0].StructuralError);
        Assert.Equal(4, rows[1].LineNumber);
        Assert.NotNull(rows[1].StructuralError);
    }

    [Fact]
    public void Missing_header_column_fails_fast_with_a_hint_about_the_delimiter()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            CsvActivityReader.Read(new StringReader("id,asset_name,ip,created_utc,source,category\n")).ToList());
        Assert.Contains("delimiter", ex.Message, StringComparison.Ordinal);
    }
}
