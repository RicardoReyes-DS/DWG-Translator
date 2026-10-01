using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

/// <summary>
/// Shared, fail-closed admission check for reopening a failed generation at its
/// existing review checkpoint. Diagnostic records remain evidence only: a
/// malformed, truncated, material, or non-finite difference is never reopened.
/// </summary>
internal static class AgentFailedGeometryRecoveryEvidence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsTolerable(JsonObject data)
    {
        var failure = data["failure"] as JsonObject;
        if (!string.Equals(failure?["code"]?.GetValue<string>(), "GEOMETRY_INVARIANTS_CHANGED", StringComparison.Ordinal)) return false;
        try
        {
            var diagnostics = failure!["invariantDiagnostics"]?.Deserialize<CadInvariantDiagnostics>(Json);
            if (diagnostics is null || !CadInvariantDiagnosticsLimits.IsValid(diagnostics) || diagnostics.Truncated ||
                diagnostics.AddedCount != 0 || diagnostics.RemovedCount != 0 || diagnostics.ChangedCount == 0 ||
                diagnostics.Rows.Count != diagnostics.ChangedCount) return false;
            return diagnostics.Rows.All(IsTolerableDifference);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsTolerableDifference(CadInvariantDifference difference)
    {
        if (!string.Equals(difference.ChangeKind, "Changed", StringComparison.Ordinal) || difference.Before is null || difference.After is null ||
            difference.FieldsChanged.Count == 0 || difference.FieldsChanged.Any(field =>
                !string.Equals(field, "extents", StringComparison.Ordinal) &&
                !string.Equals(field, "invariantRowFingerprint", StringComparison.Ordinal))) return false;
        var before = difference.Before;
        var after = difference.After;
        return !before.IsTargetText && !after.IsTargetText &&
            string.Equals(before.OwnerBlockName, after.OwnerBlockName, StringComparison.Ordinal) &&
            before.IsAnonymousDimensionBlockName == after.IsAnonymousDimensionBlockName &&
            before.ReferencedByDimensionCount == after.ReferencedByDimensionCount &&
            before.ReferencedByNonDimensionCount == after.ReferencedByNonDimensionCount &&
            before.DerivedDimensionGraphicsCandidate == after.DerivedDimensionGraphicsCandidate &&
            CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, after);
    }
}
