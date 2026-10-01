using System.Text.Json;
using System.Text.RegularExpressions;
using Ingestion.Api.Clients;
using Ingestion.Api.Processing;
using Ingestion.Core.Contracts;
using Ingestion.Core.Domain;
using YamlDotNet.Serialization;

namespace Ingestion.Tests.Contracts;

/// <summary>
/// Keeps docs/contracts/*.openapi.yaml and the code in sync. If someone changes the event
/// shape or the category list without updating the proposed contract (or the reverse),
/// these tests fail.
/// </summary>
public class ContractSpecTests
{
    private static readonly Dictionary<object, object> AnalyticsSpec = Load("analytics.openapi.yaml");
    private static readonly Dictionary<object, object> EnrichmentSpec = Load("enrichment.openapi.yaml");

    private static Dictionary<object, object> Load(string file) =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "contracts", file)));

    private static Dictionary<object, object> Schema(Dictionary<object, object> spec, string name) =>
        (Dictionary<object, object>)((Dictionary<object, object>)((Dictionary<object, object>)spec["components"])["schemas"])[name];

    private static List<string> Strings(object list) => ((List<object>)list).Select(o => (string)o).ToList();

    private static readonly AnalyticsEvent SampleEvent = new(
        RecordEnricher.ComputeEventId(new ActivityRecordDto(1, "a", "1.2.3.4", DateTimeOffset.UnixEpoch, "s", "Phishing")),
        Guid.NewGuid(), 1, "a", "1.2.3.4", DateTimeOffset.UnixEpoch, "s", "Phishing", "T1566",
        JsonSerializer.SerializeToElement(new { ip = "1.2.3.4" }));

    [Fact]
    public void AnalyticsEvent_serializes_to_exactly_the_properties_the_spec_declares()
    {
        var schema = Schema(AnalyticsSpec, "AnalyticsEvent");
        // AnalyticsClient uses PostAsJsonAsync, i.e. the web defaults (camelCase).
        var json = JsonSerializer.SerializeToElement(SampleEvent, JsonSerializerOptions.Web);
        var sent = json.EnumerateObject().Select(p => p.Name).Order().ToList();

        Assert.Equal(Strings(schema["required"]).Order(), sent);
        Assert.Equal(((Dictionary<object, object>)schema["properties"]).Keys.Cast<string>().Order(), sent);
        Assert.Equal("false", schema["additionalProperties"]);
    }

    [Fact]
    public void Category_and_technique_enums_match_the_normalizer()
    {
        var properties = (Dictionary<object, object>)Schema(AnalyticsSpec, "AnalyticsEvent")["properties"];
        var category = (Dictionary<object, object>)properties["category"];
        var technique = (Dictionary<object, object>)properties["techniqueId"];

        Assert.Equal(CategoryNormalizer.All.Select(t => t.Name), Strings(category["enum"]));
        Assert.Equal(CategoryNormalizer.All.Select(t => t.Id), Strings(technique["enum"]));
    }

    [Fact]
    public void EventId_matches_the_spec_pattern()
    {
        var properties = (Dictionary<object, object>)Schema(AnalyticsSpec, "AnalyticsEvent")["properties"];
        var pattern = (string)((Dictionary<object, object>)properties["eventId"])["pattern"];
        Assert.Matches(new Regex(pattern), SampleEvent.EventId);
    }

    [Fact]
    public void Batch_size_and_result_statuses_match_the_client()
    {
        var events = (Dictionary<object, object>)((Dictionary<object, object>)Schema(AnalyticsSpec, "EventBatch")["properties"])["events"];
        Assert.Equal(new Ingestion.Api.Configuration.AnalyticsOptions().MaxBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture), events["maxItems"]);

        var status = (Dictionary<object, object>)((Dictionary<object, object>)Schema(AnalyticsSpec, "EventResult")["properties"])["status"];
        Assert.Equal(
            Enum.GetNames<EventDeliveryStatus>().Select(n => n.ToLowerInvariant()),
            Strings(status["enum"]));
    }

    [Fact]
    public void Enrichment_result_only_requires_ip_and_allows_extra_fields()
    {
        // We pass enrichment through unchanged, so the contract must stay open to extension.
        var schema = Schema(EnrichmentSpec, "EnrichmentResult");
        Assert.Equal(["ip"], Strings(schema["required"]));
        Assert.Equal("true", schema["additionalProperties"]);
    }
}
