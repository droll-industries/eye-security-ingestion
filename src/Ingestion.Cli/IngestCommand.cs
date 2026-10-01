using System.Security.Cryptography;
using System.Text.Json;
using Ingestion.Core.Contracts;
using Ingestion.Core.Domain;

namespace Ingestion.Cli;

public sealed record IngestSettings(
    FileInfo File,
    RecordFilter Filter,
    char Delimiter,
    int BatchSize,
    bool DryRun,
    bool Verbose);

public static class ExitCodes
{
    public const int Success = 0;
    /// <summary>Nothing or only part was ingested because of a fatal error (bad file, API down).</summary>
    public const int Failure = 1;
    /// <summary>Ingestion finished, but some rows were rejected (locally or by the API).</summary>
    public const int CompletedWithRejections = 2;
}

/// <summary>
/// The CLI workflow: read, validate, filter, upload in batches, report.
/// All user feedback goes to the given writers, so tests can capture it.
/// </summary>
public sealed class IngestCommand(IngestionApiClient? api, TimeProvider clock, TextWriter output, TextWriter errors)
{
    private const int MaxIssuesShown = 20;

    private sealed record ValidRecord(int LineNumber, ActivityRecordDto Record);

    public async Task<int> RunAsync(IngestSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.File.Exists)
        {
            await errors.WriteLineAsync($"error: file not found: {settings.File.FullName}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        // 1. Parse and validate locally to give fast feedback before anything is sent.
        var valid = new List<ValidRecord>();
        var issues = new List<string>();
        var total = 0;
        var filteredOut = 0;
        try
        {
            using var reader = settings.File.OpenText();
            foreach (var row in CsvActivityReader.Read(reader, settings.Delimiter))
            {
                total++;
                if (row.StructuralError is not null)
                {
                    issues.Add($"line {row.LineNumber}: {row.StructuralError}");
                    continue;
                }

                var result = RecordValidator.Parse(row.Record, clock);
                if (!result.IsValid)
                {
                    issues.Add($"line {row.LineNumber}: {string.Join("; ", result.Errors)}");
                    continue;
                }

                if (settings.Filter.Matches(result.Value!)) valid.Add(new ValidRecord(row.LineNumber, result.Value!));
                else filteredOut++;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await errors.WriteLineAsync($"error: cannot read {settings.File.Name}: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        await output.WriteLineAsync($"File:          {settings.File.FullName}").ConfigureAwait(false);
        await output.WriteLineAsync($"Rows read:     {total}").ConfigureAwait(false);
        await output.WriteLineAsync($"Invalid rows:  {issues.Count}").ConfigureAwait(false);
        if (!settings.Filter.IsEmpty)
        {
            await output.WriteLineAsync($"Filter:        {settings.Filter}").ConfigureAwait(false);
            await output.WriteLineAsync($"Filtered out:  {filteredOut}").ConfigureAwait(false);
        }
        await output.WriteLineAsync($"To ingest:     {valid.Count}").ConfigureAwait(false);
        await WriteIssuesAsync("Invalid rows (not sent)", issues, settings.Verbose).ConfigureAwait(false);

        if (settings.DryRun)
        {
            await output.WriteLineAsync("Dry run: nothing was sent.").ConfigureAwait(false);
            return issues.Count > 0 ? ExitCodes.CompletedWithRejections : ExitCodes.Success;
        }

        if (valid.Count == 0)
        {
            await errors.WriteLineAsync(filteredOut > 0
                ? "error: nothing to ingest: no valid record matches the filter."
                : "error: nothing to ingest: no valid records in the file.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        // 2. Upload in batches. One failed batch doesn't stop the others, and each batch is
        //    idempotent, so running the same command again only resends what's missing.
        var accepted = 0;
        var estimatedDeliverySeconds = 0;
        var serverRejections = new List<string>();
        var failedBatches = 0;
        var batches = valid.Chunk(settings.BatchSize).ToList();
        for (var b = 0; b < batches.Count; b++)
        {
            var batch = batches[b];
            var request = new IngestionRequest([.. batch.Select(v => v.Record)]);
            var label = $"Batch {b + 1}/{batches.Count} ({batch.Length} records)";

            SubmitResult result;
            try
            {
                result = await api!.SubmitAsync(request, IdempotencyKeyFor(request), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException
                                           or Polly.ExecutionRejectedException
                                       && !cancellationToken.IsCancellationRequested)
            {
                failedBatches++;
                await errors.WriteLineAsync($"{label}: FAILED, API unreachable: {ex.Message}").ConfigureAwait(false);
                continue;
            }

            if (result.Response is not { } response)
            {
                failedBatches++;
                await errors.WriteLineAsync($"{label}: FAILED, HTTP {(int)result.Status}: {result.Error}").ConfigureAwait(false);
                continue;
            }

            accepted += response.Accepted;
            // Each estimate includes the backlog queued before it, so the last one is the overall estimate.
            estimatedDeliverySeconds = Math.Max(estimatedDeliverySeconds, response.EstimatedDeliverySeconds);
            serverRejections.AddRange(response.Rejected.Select(r =>
                $"line {batch[r.Index].LineNumber}: {string.Join("; ", r.Errors)}"));
            var replayNote = result.Replayed ? " [already submitted earlier; not queued again]" : "";
            await output.WriteLineAsync(
                $"{label}: accepted {response.Accepted}, rejected {response.Rejected.Count}, " +
                $"ingestion id {response.IngestionId}{replayNote}").ConfigureAwait(false);
        }

        await WriteIssuesAsync("Rows rejected by the API", serverRejections, settings.Verbose).ConfigureAwait(false);

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync($"Accepted for processing: {accepted}/{valid.Count}").ConfigureAwait(false);
        if (accepted > 0)
        {
            var eta = TimeSpan.FromSeconds(estimatedDeliverySeconds);
            await output.WriteLineAsync(
                "Accepted records are queued for enrichment and delivery to Analytics, which is " +
                $"rate limited. Estimated delivery time (best case, including queued backlog): about {eta:hh\\:mm\\:ss}.").ConfigureAwait(false);
        }

        if (failedBatches > 0)
        {
            await errors.WriteLineAsync(
                $"error: {failedBatches} batch(es) failed. Re-run the same command to retry; " +
                "batches already accepted won't be ingested twice.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        return issues.Count + serverRejections.Count > 0 ? ExitCodes.CompletedWithRejections : ExitCodes.Success;
    }

    /// <summary>
    /// Computed from the batch content: re-running the same upload (e.g. after a timeout) gives
    /// the same key, so the API replays its earlier answer instead of ingesting twice.
    /// </summary>
    public static string IdempotencyKeyFor(IngestionRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

    private async Task WriteIssuesAsync(string title, List<string> issues, bool verbose)
    {
        if (issues.Count == 0) return;
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync($"{title}:").ConfigureAwait(false);
        var shown = verbose ? issues : issues.Take(MaxIssuesShown);
        foreach (var issue in shown) await output.WriteLineAsync($"  - {issue}").ConfigureAwait(false);
        if (!verbose && issues.Count > MaxIssuesShown)
            await output.WriteLineAsync($"  ... and {issues.Count - MaxIssuesShown} more (use --verbose)").ConfigureAwait(false);
    }
}
