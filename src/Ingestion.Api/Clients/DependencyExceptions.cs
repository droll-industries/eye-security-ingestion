using System.Net;

namespace Ingestion.Api.Clients;

/// <summary>
/// Failure that may succeed later: 5xx, 408, 429, timeout, open circuit, and systemic
/// configuration errors (401/403/404) that an operator can fix. Retry the record within its budget.
/// </summary>
public sealed class TransientDependencyException(string message, Exception? inner = null, bool isConfigurationError = false)
    : Exception(message, inner)
{
    /// <summary>True for 401/403/404: an operator must fix URL or credentials; alert, don't just wait.</summary>
    public bool IsConfigurationError { get; } = isConfigurationError;
}

/// <summary>Failure caused by this record or request that won't fix itself (400/422, rejected event): dead-letter now.</summary>
public sealed class PermanentDependencyException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Maps a failed HTTP response to the retry behaviour. Rule: a failure is <i>permanent</i> only
/// when it's caused by the record/request itself. 401/403 (bad or rotated key) and 404 (wrong
/// base URL; the contracts never use 404 for data) say nothing about the record; they affect
/// <i>every</i> record. Treating them as permanent would dead-letter the whole backlog within
/// seconds of a misconfiguration. They're retried within the budget and logged as errors so an
/// operator fixes the configuration.
/// </summary>
internal static class HttpResponseClassifier
{
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string dependency, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var message = $"{dependency} returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(body)}";
        if (!IsTransient(response.StatusCode)) throw new PermanentDependencyException(message);
        throw IsSystemic(response.StatusCode)
            ? new TransientDependencyException($"{message} (configuration problem: check URL/credentials)", isConfigurationError: true)
            : new TransientDependencyException(message);
    }

    internal static bool IsSystemic(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    internal static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500
        || IsSystemic(status);

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "...";
}
