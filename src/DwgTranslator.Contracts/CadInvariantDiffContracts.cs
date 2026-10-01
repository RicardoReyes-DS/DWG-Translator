using System.Text.Json.Serialization;

namespace DwgTranslator.Contracts;

/// <summary>Read-only, content-redacted entity snapshot used only for invariant differential diagnostics.</summary>
public sealed record CadInvariantDiffEntity
{
    [JsonRequired] public required string OwnerHandle { get; init; }
    [JsonRequired] public required string EntityHandle { get; init; }
    [JsonRequired] public required string DxfType { get; init; }
    [JsonRequired] public required string RuntimeClass { get; init; }
    [JsonRequired] public required bool IsTextEntity { get; init; }
    [JsonRequired] public required string Layer { get; init; }
    [JsonRequired] public required int ColorIndex { get; init; }
    [JsonRequired] public required string LinetypeHandle { get; init; }
    [JsonRequired] public required int Lineweight { get; init; }
    public CadInvariantExtents? Extents { get; init; }
    /// <summary>SHA-256 of DBText.TextString or MText.Contents; never the drawing text itself.</summary>
    public string? TextPayloadHash { get; init; }
    [JsonRequired] public required string Fingerprint { get; init; }
}

public sealed record CadInvariantDiffRequestPayload
{
    [JsonRequired] public required string SourcePath { get; init; }
    [JsonRequired] public required string ExpectedSourceHash { get; init; }
    [JsonRequired] public required string CandidatePath { get; init; }
    [JsonRequired] public required string ExpectedCandidateHash { get; init; }
    [JsonRequired] public required bool ReadOnly { get; init; }
}

public sealed record CadInvariantDiffResponsePayload
{
    [JsonRequired] public required string Schema { get; init; }
    [JsonRequired] public required string SourceHashAfter { get; init; }
    [JsonRequired] public required string CandidateHashAfter { get; init; }
    [JsonRequired] public required string SourceFingerprint { get; init; }
    [JsonRequired] public required string CandidateFingerprint { get; init; }
    [JsonRequired] public required int SourceEntityCount { get; init; }
    [JsonRequired] public required int CandidateEntityCount { get; init; }
    [JsonRequired] public required int MatchedEntityCount { get; init; }
    [JsonRequired] public required int UnchangedEntityCount { get; init; }
    [JsonRequired] public required int AddedEntityCount { get; init; }
    [JsonRequired] public required int RemovedEntityCount { get; init; }
    [JsonRequired] public required int ChangedEntityCount { get; init; }
    [JsonRequired] public required bool DifferencesComplete { get; init; }
    [JsonRequired] public required List<CadInvariantDiffDifference> Differences { get; init; }
    [JsonRequired] public required string ComparisonFingerprint { get; init; }
}

/// <summary>
/// A complete changed-row set. Unchanged rows are committed by the full-snapshot
/// fingerprints and counts but are deliberately not repeated on the wire.
/// </summary>
public sealed record CadInvariantDiffDifference
{
    [JsonRequired] public required string InvariantKey { get; init; }
    [JsonRequired] public required string ChangeKind { get; init; }
    [JsonRequired] public required List<string> FieldsChanged { get; init; }
    public CadInvariantDiffEntity? Source { get; init; }
    public CadInvariantDiffEntity? Candidate { get; init; }
}

public static class CadInvariantDiffContract
{
    public const string ResponseSchema = "cad-invariant-diff-response/2.0";
    public const int MaximumDifferences = 2_048;
    public const int MaximumStringLength = 256;
    // Keep compact evidence comfortably below the enclosing 16 MiB wire frame.
    public const int MaximumSerializedPayloadBytes = 8 * 1024 * 1024;
}
