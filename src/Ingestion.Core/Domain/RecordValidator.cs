using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Ingestion.Core.Contracts;

namespace Ingestion.Core.Domain;

/// <summary>
/// The one set of record rules, shared by the CLI (early feedback, dry-run) and the API
/// (authoritative). Returns every error found, not just the first, so users can fix a
/// file in one pass.
/// </summary>
public static class RecordValidator
{
    /// <summary>Date format of the "created_utc" column in the SecOps export.</summary>
    public const string CsvDateFormat = "dd/MM/yyyy HH:mm";

    // Allows for clock skew between log sources and our hosts.
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Parses raw CSV strings into a typed record, then applies the semantic rules.</summary>
    public static ValidationResult<ActivityRecordDto> Parse(RawActivityRecord raw, TimeProvider clock)
    {
        var errors = new List<string>();

        long id = 0;
        if (string.IsNullOrWhiteSpace(raw.Id)) errors.Add("id is missing");
        else if (!long.TryParse(raw.Id.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id))
            errors.Add($"id '{raw.Id}' is not a positive integer");

        var created = DateTimeOffset.MinValue;
        if (string.IsNullOrWhiteSpace(raw.CreatedUtc)) errors.Add("created_utc is missing");
        // Assumption: the column is UTC, as its name says, and has no offset.
        else if (!DateTimeOffset.TryParseExact(raw.CreatedUtc.Trim(), CsvDateFormat, CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out created))
            errors.Add($"created_utc '{raw.CreatedUtc}' is not in format {CsvDateFormat}");

        if (errors.Count > 0)
        {
            // Run the string-level checks too, so the user sees every problem at once.
            errors.AddRange(CheckFields(raw.AssetName, raw.Ip, raw.Source, raw.Category, out _));
            return ValidationResult.Fail<ActivityRecordDto>(errors);
        }

        return Validate(new ActivityRecordDto(
            id, raw.AssetName ?? "", raw.Ip ?? "", created, raw.Source ?? "", raw.Category ?? ""), clock);
    }

    /// <summary>
    /// Validates and normalizes a typed record: trims, lower-cases source, maps the category
    /// to its canonical MITRE name. Running it on its own output returns the same record.
    /// </summary>
    public static ValidationResult<ActivityRecordDto> Validate(ActivityRecordDto record, TimeProvider clock)
    {
        var errors = new List<string>();
        if (record.Id <= 0) errors.Add($"id {record.Id} must be positive");
        // A JSON body without createdUtc deserializes to DateTimeOffset.MinValue (0001-01-01).
        if (record.CreatedUtc == default) errors.Add("created_utc is missing");
        else if (record.CreatedUtc > clock.GetUtcNow() + FutureTolerance)
            errors.Add($"created_utc {record.CreatedUtc:O} is in the future");

        errors.AddRange(CheckFields(record.AssetName, record.Ip, record.Source, record.Category, out var technique));
        if (errors.Count > 0) return ValidationResult.Fail<ActivityRecordDto>(errors);

        return ValidationResult.Ok(record with
        {
            AssetName = record.AssetName.Trim(),
            Ip = IPAddress.Parse(record.Ip.Trim()).ToString(),
            Source = record.Source.Trim().ToLowerInvariant(),
            Category = technique!.Name,
            CreatedUtc = record.CreatedUtc.ToUniversalTime(),
        });
    }

    private static List<string> CheckFields(
        string? assetName, string? ip, string? source, string? category, out AttackTechnique? technique)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(assetName)) errors.Add("asset_name is missing");

        if (string.IsNullOrWhiteSpace(ip)) errors.Add("ip is missing");
        else if (!IsValidIp(ip.Trim())) errors.Add($"ip '{ip}' is not a valid IPv4/IPv6 address");

        // The export writes the literal "null" when a source is unknown. We reject it:
        // a record without provenance can't be trusted during incident response.
        if (string.IsNullOrWhiteSpace(source) || source.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
            errors.Add("source is missing");

        technique = null;
        if (string.IsNullOrWhiteSpace(category)) errors.Add("category is missing");
        else if (!CategoryNormalizer.TryNormalize(category, out technique))
            errors.Add($"category '{category}' is not a known MITRE ATT&CK initial-access technique");

        return errors;
    }

    private static bool IsValidIp(string value) =>
        IPAddress.TryParse(value, out var address) && address.AddressFamily switch
        {
            // IPAddress.TryParse accepts shorthand such as "1" (= 0.0.0.1); require four octets.
            AddressFamily.InterNetwork => value.Count(c => c == '.') == 3,
            AddressFamily.InterNetworkV6 => true,
            _ => false,
        };
}
