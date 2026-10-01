using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ingestion.Api.Clients;

/// <summary>
/// The event sent to Analytics: the normalized record plus enrichment
/// (docs/contracts/analytics.openapi.yaml, AnalyticsEvent).
/// <see cref="EventId"/> is computed from the record's content, so Analytics can deduplicate
/// at-least-once deliveries, including retried batches and re-uploads (ADR 0004).
/// </summary>
public sealed record AnalyticsEvent(
    string EventId,
    Guid IngestionId,
    long RecordId,
    string AssetName,
    string Ip,
    DateTimeOffset CreatedUtc,
    string Source,
    string Category,
    string TechniqueId,
    JsonElement Enrichment);

[JsonConverter(typeof(JsonStringEnumConverter<EventDeliveryStatus>))]
public enum EventDeliveryStatus
{
    /// <summary>Stored.</summary>
    Accepted,

    /// <summary>The eventId was already stored; nothing to do. Counts as delivered.</summary>
    Duplicate,

    /// <summary>The event failed validation. Permanent: dead-letter it.</summary>
    Rejected,
}

public sealed record EventDeliveryResult(string EventId, EventDeliveryStatus Status, IReadOnlyList<string>? Errors = null);

public interface IAnalyticsClient
{
    /// <summary>
    /// Delivers events. Returns one result per event, in the same order. Throws
    /// <see cref="TransientDependencyException"/>/<see cref="PermanentDependencyException"/>
    /// when the call fails as a whole.
    /// </summary>
    Task<IReadOnlyList<EventDeliveryResult>> SendAsync(IReadOnlyList<AnalyticsEvent> events, CancellationToken cancellationToken);
}

/// <summary>
/// <c>POST {BaseUrl}/events/batch</c>: one HTTP request, so one rate-limited message, for up
/// to 100 events (PROPOSED contract, ADR 0003/0009). Rate limiting and retries are handlers on
/// the HttpClient (Program.cs). Retrying a whole batch is safe: Analytics answers
/// <c>duplicate</c> for eventIds it already stored.
/// </summary>
public sealed class BatchAnalyticsClient(HttpClient http) : IAnalyticsClient
{
    private sealed record EventBatch(IReadOnlyList<AnalyticsEvent> Events);
    private sealed record EventBatchResult(IReadOnlyList<EventDeliveryResult>? Results);

    public async Task<IReadOnlyList<EventDeliveryResult>> SendAsync(
        IReadOnlyList<AnalyticsEvent> events, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            new Uri("events/batch", UriKind.Relative), new EventBatch(events), cancellationToken).ConfigureAwait(false);
        await HttpResponseClassifier.EnsureSuccessAsync(response, "Analytics Service", cancellationToken).ConfigureAwait(false);

        EventBatchResult? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<EventBatchResult>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new TransientDependencyException("Analytics Service returned an unreadable batch result", ex);
        }

        // The contract promises one result per event, in order. If that's broken we can't tell
        // which events were stored, so retry the batch (safe thanks to eventId deduplication).
        if (body?.Results is not { } results || results.Count != events.Count)
            throw new TransientDependencyException(
                $"Analytics Service returned {body?.Results?.Count ?? 0} results for {events.Count} events");
        return results;
    }
}

/// <summary>
/// Fallback (<c>Analytics:Mode = SingleEvent</c>): <c>POST {BaseUrl}/events</c>, one event per
/// message. The dispatcher sends batches of 1 in this mode.
/// </summary>
public sealed class SingleEventAnalyticsClient(HttpClient http) : IAnalyticsClient
{
    public async Task<IReadOnlyList<EventDeliveryResult>> SendAsync(
        IReadOnlyList<AnalyticsEvent> events, CancellationToken cancellationToken)
    {
        var results = new List<EventDeliveryResult>(events.Count);
        foreach (var analyticsEvent in events)
        {
            using var response = await http.PostAsJsonAsync(
                new Uri("events", UriKind.Relative), analyticsEvent, cancellationToken).ConfigureAwait(false);
            await HttpResponseClassifier.EnsureSuccessAsync(response, "Analytics Service", cancellationToken).ConfigureAwait(false);
            results.Add(new EventDeliveryResult(analyticsEvent.EventId, EventDeliveryStatus.Accepted));
        }
        return results;
    }
}
