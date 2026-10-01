using System.Security.Cryptography;
using System.Text;
using Ingestion.Api.Clients;
using Ingestion.Core.Contracts;
using Ingestion.Core.Domain;

namespace Ingestion.Api.Processing;

/// <summary>Turns a queued record into an <see cref="AnalyticsEvent"/> by calling Enrichment.</summary>
public sealed class RecordEnricher(IEnrichmentClient enrichment)
{
    public async Task<AnalyticsEvent> EnrichAsync(IngestionWorkItem item, CancellationToken cancellationToken)
    {
        var record = item.Record;
        var enriched = await enrichment.EnrichAsync(record.Ip, cancellationToken).ConfigureAwait(false);

        // Records were validated on intake, so the category is canonical here.
        CategoryNormalizer.TryNormalize(record.Category, out var technique);

        return new AnalyticsEvent(
            EventId: ComputeEventId(record),
            IngestionId: item.IngestionId,
            RecordId: record.Id,
            AssetName: record.AssetName,
            Ip: record.Ip,
            CreatedUtc: record.CreatedUtc,
            Source: record.Source,
            Category: record.Category,
            TechniqueId: technique?.Id ?? "",
            Enrichment: enriched);
    }

    /// <summary>
    /// Computed from the record's content, not its id: the sample has the same id on records
    /// with different content (e.g. 4568), so the id alone can't identify an event.
    /// </summary>
    public static string ComputeEventId(ActivityRecordDto r)
    {
        var canonical = FormattableString.Invariant(
            $"{r.Id}|{r.AssetName}|{r.Ip}|{r.CreatedUtc.UtcTicks}|{r.Source}|{r.Category}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }
}
