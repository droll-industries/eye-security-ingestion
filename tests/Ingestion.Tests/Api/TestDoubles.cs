using System.Text.Json;
using Ingestion.Api.Clients;

namespace Ingestion.Tests.Api;

public sealed class FakeEnrichment : IEnrichmentClient
{
    public Exception? FailWith { get; set; }

    /// <summary>Evaluated per call; overrides <see cref="FailWith"/> when it returns an exception.</summary>
    public Func<Exception?> FailWhen { get; set; } = () => null;

    public int Calls { get; private set; }

    public Task<JsonElement> EnrichAsync(string ip, CancellationToken cancellationToken)
    {
        Calls++;
        var failure = FailWhen() ?? FailWith;
        return failure is not null
            ? Task.FromException<JsonElement>(failure)
            : Task.FromResult(JsonSerializer.SerializeToElement(new { ip, country = "NL" }));
    }
}

/// <summary>Records every batch; optionally fails or rejects chosen events.</summary>
public sealed class FakeAnalytics : IAnalyticsClient
{
    public Exception? FailWith { get; set; }
    public Func<AnalyticsEvent, EventDeliveryStatus> StatusFor { get; set; } = _ => EventDeliveryStatus.Accepted;
    public List<IReadOnlyList<AnalyticsEvent>> Batches { get; } = [];

    /// <summary>Simulates a misbehaving client that returns fewer results than events.</summary>
    public int DropResults { get; set; }

    public IReadOnlyList<AnalyticsEvent> Sent
    {
        get { lock (Batches) return [.. Batches.SelectMany(b => b)]; }
    }

    public Task<IReadOnlyList<EventDeliveryResult>> SendAsync(
        IReadOnlyList<AnalyticsEvent> events, CancellationToken cancellationToken)
    {
        if (FailWith is not null) return Task.FromException<IReadOnlyList<EventDeliveryResult>>(FailWith);
        lock (Batches) Batches.Add(events);
        IReadOnlyList<EventDeliveryResult> results =
            [.. events.Skip(DropResults).Select(e => new EventDeliveryResult(e.EventId, StatusFor(e), ["invalid"]))];
        return Task.FromResult(results);
    }
}
