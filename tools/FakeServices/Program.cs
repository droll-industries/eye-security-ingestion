// Local stand-ins for the Enrichment and Analytics services. They implement the PROPOSED
// contracts in docs/contracts/*.openapi.yaml strictly, so contract violations by our
// clients show up as 400s here (and in the contract tests) rather than in production.
// NOT production code: it exists to demo and exercise the resilience behaviour
// (flaky enrichment, strict rate limit, batch partial failures) end to end.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FakeServices;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
var app = builder.Build();

var config = app.Configuration;
var failureRate = config.GetValue("Fake:EnrichmentFailureRate", 0.3);
var slowRate = config.GetValue("Fake:EnrichmentSlowRate", 0.05);
var analyticsLimit = config.GetValue("Fake:AnalyticsPermitLimit", 20);
var analyticsWindow = config.GetValue("Fake:AnalyticsWindow", TimeSpan.FromSeconds(10));
const string AuthKey = "eye-am-hiring";

var analyticsLog = new Queue<DateTimeOffset>();
var received = new ConcurrentDictionary<string, JsonElement>();
var stats = new ConcurrentDictionary<string, int>();
var gate = new Lock();
var outageUntil = DateTimeOffset.MinValue; // set via POST /admin/enrichment-outage
void Count(string key) => stats.AddOrUpdate(key, 1, (_, n) => n + 1);

// securitySchemes.eyeAuth (both contracts)
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/stats") || ctx.Request.Path.StartsWithSegments("/admin")
        || ctx.Request.Headers.Authorization == AuthKey)
    {
        await next();
        return;
    }
    Count("unauthorized");
    await Results.Problem("Missing or invalid Authorization header.", statusCode: 401).ExecuteAsync(ctx);
});

// enrichment.openapi.yaml: GET /enrichment?ip=
app.MapGet("/enrichment", async (string? ip, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out _))
    {
        Count("enrichment_bad_request");
        return Results.Problem($"'{ip}' is not a valid IP address.", statusCode: 400);
    }

    if (DateTimeOffset.UtcNow < outageUntil)
    {
        Count("enrichment_outage");
        return Results.StatusCode(503);
    }

    var roll = Random.Shared.NextDouble();
    if (roll < failureRate)
    {
        Count("enrichment_failed");
        return Results.StatusCode(Random.Shared.Next(2) == 0 ? 500 : 503);
    }
    if (roll < failureRate + slowRate)
    {
        Count("enrichment_slow");
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
    }

    Count("enrichment_ok");
    return Results.Ok(FakeEnrichment.For(ip));
});

// Rate limit shared by both Analytics endpoints: one HTTP request = one message, whatever its size.
IResult? TryTakePermit()
{
    lock (gate)
    {
        var now = DateTimeOffset.UtcNow;
        while (analyticsLog.Count > 0 && now - analyticsLog.Peek() >= analyticsWindow) analyticsLog.Dequeue();
        if (analyticsLog.Count >= analyticsLimit)
        {
            Count("analytics_rate_limited");
            var retryAfter = (int)Math.Ceiling((analyticsLog.Peek() + analyticsWindow - now).TotalSeconds);
            return new RetryAfterResult(Results.Problem("Rate limit exceeded.", statusCode: 429), retryAfter);
        }
        analyticsLog.Enqueue(now);
        return null;
    }
}

// Stores a valid event. Returns false if its eventId was already stored (duplicate, no-op).
bool Store(JsonElement analyticsEvent)
{
    var isNew = received.TryAdd(analyticsEvent.GetProperty("eventId").GetString()!, analyticsEvent.Clone());
    Count(isNew ? "analytics_accepted" : "analytics_duplicates");
    return isNew;
}

// analytics.openapi.yaml: POST /events/batch (preferred: up to 100 events per message)
app.MapPost("/events/batch", (JsonElement body) =>
{
    if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("events", out var events)
        || events.ValueKind != JsonValueKind.Array)
        return Results.Problem("Body must be an object with an 'events' array.", statusCode: 400);
    if (events.GetArrayLength() is 0 or > AnalyticsEventSchema.MaxBatchSize)
        return Results.Problem($"'events' must contain 1-{AnalyticsEventSchema.MaxBatchSize} items.", statusCode: 400);

    if (TryTakePermit() is { } limited) return limited;
    Count("analytics_batches");

    var results = events.EnumerateArray().Select(e =>
    {
        var eventId = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("eventId", out var id) ? id.ToString() : "";
        var violations = AnalyticsEventSchema.Validate(e);
        if (violations.Count > 0)
        {
            Count("analytics_rejected");
            return new { eventId, status = "rejected", errors = violations };
        }
        return Store(e)
            ? new { eventId, status = "accepted", errors = new List<string>() }
            : new { eventId, status = "duplicate", errors = new List<string>() };
    }).ToList();

    return Results.Ok(new { results });
});

// analytics.openapi.yaml: POST /events (single-event fallback)
app.MapPost("/events", (JsonElement body) =>
{
    var violations = AnalyticsEventSchema.Validate(body);
    if (violations.Count > 0)
    {
        Count("analytics_bad_request");
        return Results.Problem(string.Join("; ", violations), statusCode: 400);
    }
    if (TryTakePermit() is { } limited) return limited;
    Store(body);
    return Results.Accepted();
});

// Test hook (not part of any contract): simulate a full Enrichment outage, e.g.
//   curl -X POST "localhost:5090/admin/enrichment-outage?seconds=120"
app.MapPost("/admin/enrichment-outage", (int seconds) =>
{
    outageUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
    return Results.Ok(new { outageUntil });
});

app.MapGet("/stats", () => Results.Ok(new { stats, uniqueEventsReceived = received.Count }));

app.Run();

namespace FakeServices
{
    /// <summary>Entry-point marker for WebApplicationFactory in contract tests.</summary>
    public sealed class FakeServicesEntryPoint;

    internal static class FakeEnrichment
    {
        private static readonly string[] Countries = ["NL", "US", "DE", "BR", "CN", "RU"];
        private static readonly string[] Tags = ["scanner", "tor-exit-node", "botnet-c2", "bruteforce", "phishing-host"];

        /// <summary>Deterministic per IP, so caching behaves the way it would with real data.</summary>
        public static object For(string ip)
        {
            var hash = (uint)StableHash(ip);
            var score = (int)(hash % 101);
            var octets = ip.Split('.');
            var asn = octets.Length == 4
                ? int.Parse(octets[0], CultureInfo.InvariantCulture) * 100 + int.Parse(octets[1], CultureInfo.InvariantCulture)
                : 64512;
            return new
            {
                ip,
                country = Countries[hash % Countries.Length],
                asn = $"AS{asn}",
                asOrganization = $"Example Network {asn}",
                reputationScore = score,
                knownMalicious = score >= 70,
                tags = score >= 50 ? new[] { Tags[hash % Tags.Length] } : [],
                lastSeenUtc = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero).AddHours(-(hash % 2000)),
            };
        }

        private static int StableHash(string value) =>
            value.Aggregate(17, (h, c) => unchecked(h * 31 + c));
    }

    /// <summary>Hand-written check of components.schemas.AnalyticsEvent in analytics.openapi.yaml.</summary>
    internal static partial class AnalyticsEventSchema
    {
        public const int MaxBatchSize = 100;

        private static readonly string[] Required =
            ["eventId", "ingestionId", "recordId", "assetName", "ip", "createdUtc", "source", "category", "techniqueId", "enrichment"];

        private static readonly HashSet<string> Categories =
        [
            "Phishing", "Trusted Relationship", "Replication Through Removable Media", "Content Injection",
            "Exploit Public-Facing Application", "Supply Chain Compromise", "Drive-by Compromise",
            "Valid Accounts", "External Remote Services",
        ];

        private static readonly HashSet<string> TechniqueIds =
            ["T1566", "T1199", "T1091", "T1659", "T1190", "T1195", "T1189", "T1078", "T1133"];

        [GeneratedRegex("^[0-9a-f]{32}$")]
        private static partial Regex EventIdPattern();

        public static List<string> Validate(JsonElement body)
        {
            var errors = new List<string>();
            if (body.ValueKind != JsonValueKind.Object) return ["body must be a JSON object"];

            foreach (var name in Required)
                if (!body.TryGetProperty(name, out _)) errors.Add($"'{name}' is required");
            foreach (var property in body.EnumerateObject())
                if (!Required.Contains(property.Name)) errors.Add($"'{property.Name}' is not allowed (additionalProperties: false)");
            if (errors.Count > 0) return errors;

            string? Str(string name) => body.GetProperty(name).ValueKind == JsonValueKind.String ? body.GetProperty(name).GetString() : null;

            if (Str("eventId") is not { } eventId || !EventIdPattern().IsMatch(eventId)) errors.Add("'eventId' must be 32 lower-case hex chars");
            if (!Guid.TryParse(Str("ingestionId"), out _)) errors.Add("'ingestionId' must be a uuid");
            if (body.GetProperty("recordId") is not { ValueKind: JsonValueKind.Number } recordId || !recordId.TryGetInt64(out var id) || id < 1)
                errors.Add("'recordId' must be an integer >= 1");
            foreach (var name in new[] { "assetName", "ip", "source" })
                if (string.IsNullOrEmpty(Str(name))) errors.Add($"'{name}' must be a non-empty string");
            if (!DateTimeOffset.TryParse(Str("createdUtc"), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                errors.Add("'createdUtc' must be an ISO 8601 date-time");
            if (Str("category") is not { } category || !Categories.Contains(category)) errors.Add("'category' is not in the enum");
            if (Str("techniqueId") is not { } technique || !TechniqueIds.Contains(technique)) errors.Add("'techniqueId' is not in the enum");
            if (body.GetProperty("enrichment") is not { ValueKind: JsonValueKind.Object } enrichment || !enrichment.TryGetProperty("ip", out _))
                errors.Add("'enrichment' must be an EnrichmentResult object with 'ip'");
            return errors;
        }
    }

    internal sealed class RetryAfterResult(IResult inner, int seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
