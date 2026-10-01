using System.Net;
using System.Net.Http.Json;
using Ingestion.Core.Contracts;

namespace Ingestion.Cli;

public sealed record SubmitResult(HttpStatusCode Status, IngestionResponse? Response, bool Replayed, string? Error);

/// <summary>
/// Calls the Ingestion API. Retries for transient errors are configured on the HttpClient
/// (Program.cs). Retrying a POST is safe because every batch carries an Idempotency-Key.
/// </summary>
public sealed class IngestionApiClient(HttpClient http)
{
    public async Task<SubmitResult> SubmitAsync(
        IngestionRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("api/v1/ingestions", UriKind.Relative))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add(IngestionHeaders.IdempotencyKey, idempotencyKey);

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var replayed = response.Headers.Contains(IngestionHeaders.IdempotentReplayed);

        // 202 and 422 both carry the per-record report.
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.UnprocessableEntity)
        {
            var body = await response.Content.ReadFromJsonAsync<IngestionResponse>(cancellationToken).ConfigureAwait(false);
            return new SubmitResult(response.StatusCode, body, replayed, null);
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new SubmitResult(response.StatusCode, null, replayed, error);
    }
}
