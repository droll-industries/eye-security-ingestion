using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Ingestion.Core.Domain;

/// <summary>
/// Maps the free-text categories that SecOps tools produce onto canonical MITRE ATT&amp;CK
/// Initial Access techniques. The sample file spells the same technique 2-4 ways
/// (e.g. "phising", "Phising"; "valid-accounts", "valida_accounts").
/// See docs/adr/0005-data-validation-and-normalization.md.
/// </summary>
public static class CategoryNormalizer
{
    public static readonly AttackTechnique Phishing = new("T1566", "Phishing");
    public static readonly AttackTechnique TrustedRelationship = new("T1199", "Trusted Relationship");
    public static readonly AttackTechnique RemovableMedia = new("T1091", "Replication Through Removable Media");
    public static readonly AttackTechnique ContentInjection = new("T1659", "Content Injection");
    public static readonly AttackTechnique ExploitPublicFacing = new("T1190", "Exploit Public-Facing Application");
    public static readonly AttackTechnique SupplyChain = new("T1195", "Supply Chain Compromise");
    public static readonly AttackTechnique DriveBy = new("T1189", "Drive-by Compromise");
    public static readonly AttackTechnique ValidAccounts = new("T1078", "Valid Accounts");
    public static readonly AttackTechnique ExternalRemoteServices = new("T1133", "External Remote Services");

    public static IReadOnlyList<AttackTechnique> All { get; } =
    [
        Phishing, TrustedRelationship, RemovableMedia, ContentInjection, ExploitPublicFacing,
        SupplyChain, DriveBy, ValidAccounts, ExternalRemoteServices,
    ];

    // Keys are in "comparison form" (see ToKey). Each canonical name maps to itself, so
    // normalizing an already-normalized value is a no-op (the API re-validates CLI output).
    // Assumption: known typos (e.g. "valida accounts", "explaoit") are safe to correct.
    // Anything not listed here is rejected instead of guessed.
    private static readonly FrozenDictionary<string, AttackTechnique> Aliases = BuildAliases();

    private static FrozenDictionary<string, AttackTechnique> BuildAliases()
    {
        var aliases = new Dictionary<string, AttackTechnique>
        {
            ["phising"] = Phishing,
            ["valida accounts"] = ValidAccounts,
            ["explaoit public facing"] = ExploitPublicFacing,
            ["exploit public facing"] = ExploitPublicFacing,
            ["compromise driveby"] = DriveBy,
            ["driveby compromise"] = DriveBy,
            ["external remote service"] = ExternalRemoteServices,
        };
        foreach (var technique in All)
        {
            aliases[ToKey(technique.Name)] = technique;
            aliases[ToKey(technique.Id)] = technique;
        }
        return aliases.ToFrozenDictionary();
    }

    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out AttackTechnique? technique)
    {
        technique = null;
        return !string.IsNullOrWhiteSpace(raw) && Aliases.TryGetValue(ToKey(raw), out technique);
    }

    /// <summary>Lower-case; '-', '_' and whitespace collapse to one space; other punctuation dropped.</summary>
    private static string ToKey(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c is '-' or '_' || char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }
}
