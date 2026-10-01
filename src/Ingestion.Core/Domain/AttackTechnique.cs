namespace Ingestion.Core.Domain;

/// <summary>A MITRE ATT&amp;CK technique (Initial Access tactic, TA0001).</summary>
public sealed record AttackTechnique(string Id, string Name);
