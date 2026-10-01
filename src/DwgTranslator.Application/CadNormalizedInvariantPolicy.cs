using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

/// <summary>
/// Compares invariant snapshots captured after equivalent AutoCAD save/reopen
/// cycles. The policy permits only the already-approved floating-point extents
/// tolerance for non-target entities; all identity and diagnostic metadata is
/// exact, and target text entities remain strict.
/// </summary>
public static class CadNormalizedInvariantPolicy
{
    public static bool AreEquivalent(
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> normalizedBaseline,
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> candidate)
    {
        ArgumentNullException.ThrowIfNull(normalizedBaseline);
        ArgumentNullException.ThrowIfNull(candidate);

        return normalizedBaseline.Count == candidate.Count &&
               normalizedBaseline.All(pair =>
                   candidate.TryGetValue(pair.Key, out var candidateRow) &&
                   AreEquivalentRow(pair.Value, candidateRow));
    }

    public static bool AreEquivalentRow(
        CadInvariantDiagnosticRow normalizedBaseline,
        CadInvariantDiagnosticRow candidate)
    {
        ArgumentNullException.ThrowIfNull(normalizedBaseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!HasExactMetadata(normalizedBaseline, candidate) ||
            (normalizedBaseline.Extents is null) != (candidate.Extents is null) ||
            !CadGeometryTolerancePolicy.HasFiniteExtents(normalizedBaseline.Extents) ||
            !CadGeometryTolerancePolicy.HasFiniteExtents(candidate.Extents))
            return false;

        if (normalizedBaseline.IsTargetText || candidate.IsTargetText)
            return string.Equals(
                normalizedBaseline.InvariantRowFingerprint,
                candidate.InvariantRowFingerprint,
                StringComparison.Ordinal);

        return CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(normalizedBaseline, candidate);
    }

    public static bool HasExactMetadata(CadInvariantDiagnosticRow baseline, CadInvariantDiagnosticRow candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        return Same(baseline.EntityHandle, candidate.EntityHandle) &&
               Same(baseline.OwnerHandle, candidate.OwnerHandle) &&
               Same(baseline.DxfType, candidate.DxfType) &&
               Same(baseline.RuntimeClass, candidate.RuntimeClass) &&
               Same(baseline.OwnerBlockName, candidate.OwnerBlockName) &&
               Same(baseline.OwnerClass, candidate.OwnerClass) &&
               baseline.IsTargetText == candidate.IsTargetText &&
               baseline.IsAnonymousDimensionBlockName == candidate.IsAnonymousDimensionBlockName &&
               baseline.ReferencedByDimensionCount == candidate.ReferencedByDimensionCount &&
               baseline.ReferencedByNonDimensionCount == candidate.ReferencedByNonDimensionCount &&
               baseline.DerivedDimensionGraphicsCandidate == candidate.DerivedDimensionGraphicsCandidate &&
               Same(baseline.Layer, candidate.Layer) &&
               baseline.ColorIndex == candidate.ColorIndex &&
               Same(baseline.LinetypeHandle, candidate.LinetypeHandle) &&
               baseline.Lineweight == candidate.Lineweight;
    }

    private static bool Same(string baseline, string candidate) =>
        string.Equals(baseline, candidate, StringComparison.Ordinal);
}
