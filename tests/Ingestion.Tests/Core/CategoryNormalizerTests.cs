using Ingestion.Core.Domain;

namespace Ingestion.Tests.Core;

public class CategoryNormalizerTests
{
    // Every category spelling that occurs in samples/example_data.csv.
    [Theory]
    [InlineData("phising", "T1566")]
    [InlineData("Phising", "T1566")]
    [InlineData("trusted relationship", "T1199")]
    [InlineData("trusted-relationship", "T1199")]
    [InlineData("Replication through removable media", "T1091")]
    [InlineData("replication-through-removable-media", "T1091")]
    [InlineData("content injection", "T1659")]
    [InlineData("content_injection", "T1659")]
    [InlineData("exploit public facing", "T1190")]
    [InlineData("explaoit-public facing", "T1190")]
    [InlineData("supply chain compromise", "T1195")]
    [InlineData("supply_chain_compromise", "T1195")]
    [InlineData("drive by compromise", "T1189")]
    [InlineData("drive-by-compromise", "T1189")]
    [InlineData("compromise (driveby)", "T1189")]
    [InlineData("valid accounts", "T1078")]
    [InlineData("valid-accounts", "T1078")]
    [InlineData("valida_accounts", "T1078")]
    [InlineData("external remote service", "T1133")]
    [InlineData("external-remote-service", "T1133")]
    public void Maps_every_spelling_in_the_sample_to_its_MITRE_technique(string raw, string expectedId)
    {
        Assert.True(CategoryNormalizer.TryNormalize(raw, out var technique));
        Assert.Equal(expectedId, technique.Id);
    }

    [Fact]
    public void Normalization_is_idempotent_for_canonical_names_and_ids()
    {
        foreach (var technique in CategoryNormalizer.All)
        {
            Assert.True(CategoryNormalizer.TryNormalize(technique.Name, out var byName));
            Assert.Equal(technique, byName);
            Assert.True(CategoryNormalizer.TryNormalize(technique.Id, out var byId));
            Assert.Equal(technique, byId);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("malware")]
    [InlineData("phish")]
    public void Rejects_unknown_or_empty_categories_instead_of_guessing(string? raw) =>
        Assert.False(CategoryNormalizer.TryNormalize(raw, out _));
}
