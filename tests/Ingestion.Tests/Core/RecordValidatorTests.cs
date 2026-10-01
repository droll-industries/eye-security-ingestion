using Ingestion.Core.Contracts;
using Ingestion.Core.Domain;
using Microsoft.Extensions.Time.Testing;

namespace Ingestion.Tests.Core;

public class RecordValidatorTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero));

    private static RawActivityRecord Raw(
        string? id = "119611", string? asset = "server_horizon", string? ip = "102.145.229.227",
        string? created = "27/02/2024 00:00", string? source = "pxtrpf", string? category = "explaoit-public facing") =>
        new(id, asset, ip, created, source, category);

    [Fact]
    public void Parses_and_normalizes_a_valid_row()
    {
        var result = RecordValidator.Parse(Raw(source: " PXTRPF "), _clock);

        Assert.True(result.IsValid);
        var record = result.Value!;
        Assert.Equal(119611, record.Id);
        Assert.Equal("pxtrpf", record.Source);
        Assert.Equal("Exploit Public-Facing Application", record.Category);
        Assert.Equal(new DateTimeOffset(2024, 2, 27, 0, 0, 0, TimeSpan.Zero), record.CreatedUtc);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("1")]          // IPAddress.TryParse would accept this as 0.0.0.1
    [InlineData("300.1.1.1")]
    [InlineData("1.2.3")]
    public void Rejects_invalid_ip(string ip)
    {
        var result = RecordValidator.Parse(Raw(ip: ip), _clock);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ip", StringComparison.Ordinal));
    }

    [Fact]
    public void Accepts_ipv6() => Assert.True(RecordValidator.Parse(Raw(ip: "2001:db8::1"), _clock).IsValid);

    [Theory]
    [InlineData("null")]
    [InlineData("NULL")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_missing_source_including_literal_null(string? source) =>
        Assert.Contains("source is missing", RecordValidator.Parse(Raw(source: source), _clock).Errors);

    [Theory]
    [InlineData("2024-02-27")]
    [InlineData("02/27/2024 00:00")] // US order: month 27 doesn't exist
    [InlineData("27/02/2024")]
    public void Rejects_dates_not_in_the_export_format(string created) =>
        Assert.False(RecordValidator.Parse(Raw(created: created), _clock).IsValid);

    [Fact]
    public void Rejects_records_from_the_future() =>
        Assert.False(RecordValidator.Parse(Raw(created: "01/01/2025 00:00"), _clock).IsValid);

    [Fact]
    public void Reports_all_errors_at_once()
    {
        var result = RecordValidator.Parse(Raw(id: "abc", ip: "N", source: "null", category: ""), _clock);
        Assert.Equal(4, result.Errors.Count);
    }

    [Fact]
    public void Validate_is_idempotent_so_the_API_accepts_CLI_output_unchanged()
    {
        var once = RecordValidator.Parse(Raw(), _clock).Value!;
        var twice = RecordValidator.Validate(once, _clock);
        Assert.True(twice.IsValid);
        Assert.Equal(once, twice.Value);
    }

    [Fact]
    public void Validate_rejects_missing_created_utc_from_json()
    {
        var once = RecordValidator.Parse(Raw(), _clock).Value!;
        var result = RecordValidator.Validate(once with { CreatedUtc = default }, _clock);
        Assert.Contains("created_utc is missing", result.Errors);
    }

    [Fact]
    public void Validate_handles_nulls_from_untrusted_json()
    {
        var dto = new ActivityRecordDto(1, null!, null!, DateTimeOffset.UnixEpoch, null!, null!);
        var result = RecordValidator.Validate(dto, _clock);
        Assert.False(result.IsValid);
        Assert.Equal(4, result.Errors.Count);
    }
}
