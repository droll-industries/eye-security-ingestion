using System.CommandLine;
using System.Globalization;
using Ingestion.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Ingestion.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var fileArgument = new Argument<FileInfo>("file") { Description = "Path to the malicious-activity CSV file." };
        var apiUrlOption = new Option<Uri>("--api-url")
        {
            Description = "Base URL of the Ingestion API.",
            DefaultValueFactory = _ => new Uri(Environment.GetEnvironmentVariable("INGESTION_API_URL") ?? "http://localhost:5080/"),
        };
        var sourceOption = new Option<string[]>("--source")
        {
            Description = "Only ingest records from these sources (repeatable), e.g. --source defender.",
            AllowMultipleArgumentsPerToken = true,
        };
        var categoryOption = new Option<string[]>("--category")
        {
            Description = "Only ingest these categories (repeatable). Any known spelling or MITRE id, e.g. phishing or T1566.",
            AllowMultipleArgumentsPerToken = true,
        };
        var fromOption = new Option<string>("--from") { Description = "Only records created on/after this UTC date (yyyy-MM-dd or ISO 8601)." };
        var toOption = new Option<string>("--to") { Description = "Only records created before this UTC date. yyyy-MM-dd includes that whole day; an ISO 8601 instant is exclusive." };
        var delimiterOption = new Option<char>("--delimiter") { Description = "CSV delimiter.", DefaultValueFactory = _ => ';' };
        var batchSizeOption = new Option<int>("--batch-size") { Description = "Records per API request.", DefaultValueFactory = _ => 1000 };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Validate and filter locally, send nothing." };
        var verboseOption = new Option<bool>("--verbose") { Description = "List every invalid/rejected row." };

        categoryOption.Validators.Add(result =>
        {
            foreach (var value in result.GetValueOrDefault<string[]>() ?? [])
                if (!CategoryNormalizer.TryNormalize(value, out _))
                    result.AddError($"Unknown category '{value}'. Known: {string.Join(", ", CategoryNormalizer.All.Select(t => t.Name))}");
        });
        fromOption.Validators.Add(r => ValidateDate(r.GetValueOrDefault<string>(), r.AddError));
        toOption.Validators.Add(r => ValidateDate(r.GetValueOrDefault<string>(), r.AddError));
        batchSizeOption.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 1 or > 10_000) r.AddError("--batch-size must be between 1 and 10000.");
        });

        var root = new RootCommand("Ingest malicious-activity logs (CSV) into the Analytics Service via the Ingestion API.")
        {
            fileArgument, apiUrlOption, sourceOption, categoryOption, fromOption, toOption,
            delimiterOption, batchSizeOption, dryRunOption, verboseOption,
        };

        root.SetAction(async (parseResult, cancellationToken) =>
        {
            var filter = new RecordFilter(
                (parseResult.GetValue(sourceOption) ?? []).Select(s => s.Trim().ToLowerInvariant()).ToHashSet(),
                (parseResult.GetValue(categoryOption) ?? [])
                    .Select(c => CategoryNormalizer.TryNormalize(c, out var t) ? t.Name : c).ToHashSet(),
                RecordFilter.ParseDate(parseResult.GetValue(fromOption), endOfDay: false),
                RecordFilter.ParseDate(parseResult.GetValue(toOption), endOfDay: true));

            var settings = new IngestSettings(
                parseResult.GetValue(fileArgument)!,
                filter,
                parseResult.GetValue(delimiterOption),
                parseResult.GetValue(batchSizeOption),
                parseResult.GetValue(dryRunOption),
                parseResult.GetValue(verboseOption));

            await using var provider = BuildServices(parseResult.GetValue(apiUrlOption)!);
            var command = new IngestCommand(
                provider.GetRequiredService<IngestionApiClient>(), TimeProvider.System, Console.Out, Console.Error);
            return await command.RunAsync(settings, cancellationToken);
        });

        return await root.Parse(args).InvokeAsync();
    }

    private static ServiceProvider BuildServices(Uri apiUrl)
    {
        var services = new ServiceCollection();
        services.AddHttpClient<IngestionApiClient>(c => c.BaseAddress = apiUrl)
            .AddStandardResilienceHandler(o =>
            {
                // Retries POSTs too; that's safe because every batch carries an Idempotency-Key.
                o.Retry.MaxRetryAttempts = 3;
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
            });
        return services.BuildServiceProvider();
    }

    private static void ValidateDate(string? value, Action<string> addError)
    {
        if (value is not null && RecordFilter.ParseDate(value, endOfDay: false) is null)
            addError($"'{value}' is not a valid date (use yyyy-MM-dd or ISO 8601).");
    }
}
