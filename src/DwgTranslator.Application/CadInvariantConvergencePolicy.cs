using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public enum CadInvariantObservationOutcome
{
    Continue,
    Stable,
    Unsafe,
    Exhausted
}

public sealed record CadInvariantObservationDecision(
    CadInvariantObservationOutcome Outcome,
    int ConsecutiveAuthoritativeSnapshots);

/// <summary>
/// Governs bounded observation convergence after AutoCAD has saved an artifact.
/// Capture one is warm-up only. A material transition never becomes acceptable
/// merely because a later observation happens to repeat it.
/// </summary>
public static class CadInvariantConvergencePolicy
{
    public const int MinimumCaptures = 4;
    public const int MaximumCaptures = 5;
    public const int RequiredConsecutiveAuthoritativeSnapshots = 3;

    public static CadInvariantObservationDecision Evaluate(
        int captureNumber,
        int consecutiveAuthoritativeSnapshots,
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> previous,
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> current)
    {
        if (captureNumber < 2 || captureNumber > MaximumCaptures)
            throw new ArgumentOutOfRangeException(nameof(captureNumber));
        if (consecutiveAuthoritativeSnapshots < 0 ||
            consecutiveAuthoritativeSnapshots > captureNumber - 2)
            throw new ArgumentOutOfRangeException(nameof(consecutiveAuthoritativeSnapshots));
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var equivalent = CadNormalizedInvariantPolicy.AreEquivalent(previous, current);
        if (!equivalent && !IsSafeExtentsOnlyTransition(previous, current))
            return new(CadInvariantObservationOutcome.Unsafe, consecutiveAuthoritativeSnapshots);

        // Capture one is warm-up and is deliberately excluded from the stable
        // window. Capture two starts the first authoritative run regardless of
        // whether its observation happens to equal the warm-up observation.
        var stableSnapshots = captureNumber == 2
            ? 1
            : equivalent ? consecutiveAuthoritativeSnapshots + 1 : 1;

        if (captureNumber >= MinimumCaptures &&
            stableSnapshots >= RequiredConsecutiveAuthoritativeSnapshots)
            return new(CadInvariantObservationOutcome.Stable, stableSnapshots);

        return new(
            captureNumber == MaximumCaptures
                ? CadInvariantObservationOutcome.Exhausted
                : CadInvariantObservationOutcome.Continue,
            stableSnapshots);
    }

    public static bool IsSafeExtentsOnlyTransition(
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> previous,
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous.Count != current.Count || previous.Keys.Any(key => !current.ContainsKey(key)))
            return false;

        var changed = false;
        foreach (var pair in previous)
        {
            var before = pair.Value;
            var after = current[pair.Key];
            if (CadNormalizedInvariantPolicy.AreEquivalentRow(before, after)) continue;
            changed = true;
            if (before.IsTargetText || after.IsTargetText ||
                !CadNormalizedInvariantPolicy.HasExactMetadata(before, after) ||
                before.Extents is null || after.Extents is null ||
                !CadGeometryTolerancePolicy.HasFiniteExtents(before.Extents) ||
                !CadGeometryTolerancePolicy.HasFiniteExtents(after.Extents))
                return false;
        }
        return changed;
    }
}
