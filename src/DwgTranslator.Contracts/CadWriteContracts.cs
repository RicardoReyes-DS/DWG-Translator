using System.Text.Json.Serialization;

namespace DwgTranslator.Contracts;

public sealed record CadWriteRequestPayload
{
    [JsonRequired] public required string SourcePath { get; init; }
    [JsonRequired] public required string ExpectedSourceHash { get; init; }
    [JsonRequired] public required string CandidatePath { get; init; }
    [JsonRequired] public required string FinalPath { get; init; }
    [JsonRequired] public required string OverwritePolicy { get; init; }
    [JsonRequired] public required CadApprovalCoverage ApprovalCoverage { get; init; }
    [JsonRequired] public required List<CadWriteMapping> Mappings { get; init; }
    [JsonRequired] public required string ValidationPolicy { get; init; }
}

public sealed record CadApprovalCoverage
{
    [JsonRequired] public required int Selected { get; init; }
    [JsonRequired] public required int Approved { get; init; }
    [JsonRequired] public required int Excluded { get; init; }
    [JsonRequired] public required bool Complete { get; init; }
}

public sealed record CadWriteMapping
{
    [JsonRequired] public required string SegmentId { get; init; }
    [JsonRequired] public required string Handle { get; init; }
    [JsonRequired] public required string ExpectedSourceTextHash { get; init; }
    [JsonRequired] public required string ApprovedFinalText { get; init; }
    [JsonRequired] public required string ApprovedFinalTextHash { get; init; }
    public string? ExpectedEntityType { get; init; }
    public string? ExpectedSpace { get; init; }
    public string? ExpectedLayout { get; init; }
    public string? ExpectedLayer { get; init; }
}

public sealed record CadWriteResponsePayload
{
    [JsonRequired] public required string CandidateHash { get; init; }
    [JsonRequired] public required long CandidateBytes { get; init; }
    [JsonRequired] public required string SourceHashAfter { get; init; }
    [JsonRequired] public required List<CadAppliedMapping> Applied { get; init; }
    [JsonRequired] public required CadValidationResult Validation { get; init; }
    [JsonRequired] public required CadPromotionResult Promotion { get; init; }
}

public sealed record CadAppliedMapping
{
    [JsonRequired] public required string SegmentId { get; init; }
    [JsonRequired] public required string Result { get; init; }
    [JsonRequired] public required string PostWriteTextHash { get; init; }
    public CadVisualInvariantEvidence? VisualEvidence { get; init; }
}

public sealed record CadVisualInvariantEvidence
{
    [JsonRequired] public required Dictionary<string, string> BeforeProperties { get; init; }
    [JsonRequired] public required Dictionary<string, string> AfterProperties { get; init; }
    [JsonRequired] public required string BeforeFingerprint { get; init; }
    [JsonRequired] public required string AfterFingerprint { get; init; }
    [JsonRequired] public required bool InvariantMatch { get; init; }
    [JsonRequired] public required string BeforeExtents { get; init; }
    [JsonRequired] public required string AfterExtents { get; init; }
    [JsonRequired] public required bool BoundsChanged { get; init; }
    [JsonRequired] public required bool VisualReviewRequired { get; init; }
}

public sealed record CadValidationResult
{
    [JsonRequired] public required bool ReopenedByAutoCAD { get; init; }
    [JsonRequired] public required bool EntityMappingValid { get; init; }
    [JsonRequired] public required bool GeometryInvariantsValid { get; init; }
    [JsonRequired] public required bool FormatTokenIntegrityValid { get; init; }
    public bool? VisualInvariantsValid { get; init; }
    [JsonRequired] public required string Policy { get; init; }
}

/// <summary>
/// Bounded, non-textual evidence emitted only when the unchanged global invariant rejects a candidate.
/// This data is diagnostic only; it never alters validation or promotion.
/// </summary>
public sealed record CadInvariantDiagnostics
{
    [JsonRequired] public required string Schema { get; init; }
    [JsonRequired] public required int AddedCount { get; init; }
    [JsonRequired] public required int RemovedCount { get; init; }
    [JsonRequired] public required int ChangedCount { get; init; }
    [JsonRequired] public required bool Truncated { get; init; }
    [JsonRequired] public required List<CadInvariantDifference> Rows { get; init; }
}

/// <summary>
/// Wire-level caps for fail-closed invariant diagnostics. These limits are deliberately
/// independent from the validation policy: diagnostics cannot enlarge an IPC error
/// unboundedly or influence candidate promotion.
/// </summary>
public static class CadInvariantDiagnosticsLimits
{
    public const string Schema = "cad-invariant-diagnostics/1.0";
    public const int MaximumRows = 64;
    public const int MaximumStringLength = 256;
    public const int MaximumFieldsPerRow = 24;

    public static bool IsValid(CadInvariantDiagnostics diagnostics)
    {
        if (!string.Equals(diagnostics.Schema, Schema, StringComparison.Ordinal) ||
            diagnostics.AddedCount < 0 || diagnostics.RemovedCount < 0 || diagnostics.ChangedCount < 0 ||
            diagnostics.Rows is null || diagnostics.Rows.Count > MaximumRows)
        {
            return false;
        }

        return diagnostics.Rows.All(IsValid);
    }

    private static bool IsValid(CadInvariantDifference difference) =>
        IsSafe(difference.InvariantKey) &&
        (difference.ChangeKind is "Added" or "Removed" or "Changed") &&
        difference.FieldsChanged is not null && difference.FieldsChanged.Count <= MaximumFieldsPerRow &&
        difference.FieldsChanged.All(IsSafe) &&
        (difference.Before is null || IsValid(difference.Before)) &&
        (difference.After is null || IsValid(difference.After)) &&
        (difference.ExtentsDelta is null || IsValid(difference.ExtentsDelta));

    private static bool IsValid(CadInvariantDiagnosticRow row) =>
        IsSafe(row.EntityHandle) && IsSafe(row.OwnerHandle) && IsSafe(row.DxfType) && IsSafe(row.RuntimeClass) &&
        IsSafe(row.OwnerBlockName) && IsSafe(row.OwnerClass) && IsSafe(row.Layer) &&
        IsSafe(row.LinetypeHandle) && IsSafe(row.InvariantRowFingerprint) &&
        (row.Extents is null || (IsSafe(row.Extents.Minimum) && IsSafe(row.Extents.Maximum)));

    private static bool IsValid(CadInvariantExtentsDelta delta) =>
        IsSafe(delta.MinimumX) && IsSafe(delta.MinimumY) && IsSafe(delta.MinimumZ) &&
        IsSafe(delta.MaximumX) && IsSafe(delta.MaximumY) && IsSafe(delta.MaximumZ);

    private static bool IsSafe(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumStringLength;
}

public sealed record CadInvariantDifference
{
    [JsonRequired] public required string InvariantKey { get; init; }
    [JsonRequired] public required string ChangeKind { get; init; }
    [JsonRequired] public required List<string> FieldsChanged { get; init; }
    public CadInvariantDiagnosticRow? Before { get; init; }
    public CadInvariantDiagnosticRow? After { get; init; }
    public CadInvariantExtentsDelta? ExtentsDelta { get; init; }
}

/// <summary>
/// This intentionally excludes DBText.TextString, MText.Contents, approved text and protected tokens.
/// </summary>
public sealed record CadInvariantDiagnosticRow
{
    [JsonRequired] public required string EntityHandle { get; init; }
    [JsonRequired] public required string OwnerHandle { get; init; }
    [JsonRequired] public required string DxfType { get; init; }
    [JsonRequired] public required string RuntimeClass { get; init; }
    [JsonRequired] public required string OwnerBlockName { get; init; }
    [JsonRequired] public required string OwnerClass { get; init; }
    [JsonRequired] public required bool IsTargetText { get; init; }
    [JsonRequired] public required bool IsAnonymousDimensionBlockName { get; init; }
    public int? ReferencedByDimensionCount { get; init; }
    public int? ReferencedByNonDimensionCount { get; init; }
    public bool? DerivedDimensionGraphicsCandidate { get; init; }
    [JsonRequired] public required string Layer { get; init; }
    [JsonRequired] public required int ColorIndex { get; init; }
    [JsonRequired] public required string LinetypeHandle { get; init; }
    [JsonRequired] public required int Lineweight { get; init; }
    public CadInvariantExtents? Extents { get; init; }
    [JsonRequired] public required string InvariantRowFingerprint { get; init; }
}

public sealed record CadInvariantExtents
{
    [JsonRequired] public required string Minimum { get; init; }
    [JsonRequired] public required string Maximum { get; init; }
}

public sealed record CadInvariantExtentsDelta
{
    public string? MinimumX { get; init; }
    public string? MinimumY { get; init; }
    public string? MinimumZ { get; init; }
    public string? MaximumX { get; init; }
    public string? MaximumY { get; init; }
    public string? MaximumZ { get; init; }
}

public sealed record CadPromotionResult
{
    [JsonRequired] public required bool Performed { get; init; }
    [JsonRequired] public required string FinalPath { get; init; }
}

public sealed record CadReconcileRequestPayload
{
    [JsonRequired] public required string SourcePath { get; init; }
    [JsonRequired] public required string ExpectedSourceHash { get; init; }
    [JsonRequired] public required string OrphanPath { get; init; }
    [JsonRequired] public required string ExpectedOrphanHash { get; init; }
    [JsonRequired] public required List<CadWriteMapping> Mappings { get; init; }
    [JsonRequired] public required string ValidationPolicy { get; init; }
}

public sealed record CadReconcileResponsePayload
{
    [JsonRequired] public required string SourceHashAfter { get; init; }
    [JsonRequired] public required string OrphanHash { get; init; }
    [JsonRequired] public required List<CadAppliedMapping> Applied { get; init; }
    [JsonRequired] public required CadValidationResult Validation { get; init; }
}
