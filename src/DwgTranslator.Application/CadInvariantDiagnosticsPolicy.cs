using System.Globalization;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

/// <summary>Creates bounded diagnostics without participating in the invariant decision.</summary>
public static class CadInvariantDiagnosticsPolicy
{
    public const string Schema = CadInvariantDiagnosticsLimits.Schema;
    public const int MaximumRows = CadInvariantDiagnosticsLimits.MaximumRows;

    public static CadInvariantDiagnostics Create(
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> before,
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> after,
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed,
        IReadOnlyList<string> changed)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var rows = new List<CadInvariantDifference>(Math.Min(MaximumRows, added.Count + removed.Count + changed.Count));
        Add(rows, added, "Added", null, after);
        Add(rows, removed, "Removed", before, null);
        Add(rows, changed, "Changed", before, after);
        return new CadInvariantDiagnostics
        {
            Schema = Schema,
            AddedCount = added.Count,
            RemovedCount = removed.Count,
            ChangedCount = changed.Count,
            Truncated = added.Count + removed.Count + changed.Count > rows.Count,
            Rows = rows
        };
    }

    private static void Add(
        List<CadInvariantDifference> output,
        IReadOnlyList<string> keys,
        string kind,
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow>? before,
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow>? after)
    {
        foreach (var key in keys)
        {
            if (output.Count == MaximumRows) return;
            CadInvariantDiagnosticRow? beforeRow = null;
            CadInvariantDiagnosticRow? afterRow = null;
            if (before is not null) before.TryGetValue(key, out beforeRow);
            if (after is not null) after.TryGetValue(key, out afterRow);
            var fields = kind == "Changed" && beforeRow is not null && afterRow is not null
                ? ChangedFields(beforeRow, afterRow)
                : [kind == "Added" ? "rowAdded" : "rowRemoved"];
            if (fields.Count == 0) fields.Add("legacyInvariant");
            output.Add(new CadInvariantDifference
            {
                InvariantKey = key,
                ChangeKind = kind,
                FieldsChanged = fields,
                Before = beforeRow,
                After = afterRow,
                ExtentsDelta = beforeRow is null || afterRow is null ? null : Delta(beforeRow.Extents, afterRow.Extents)
            });
        }
    }

    private static List<string> ChangedFields(CadInvariantDiagnosticRow before, CadInvariantDiagnosticRow after)
    {
        var result = new List<string>();
        Different(before.EntityHandle, after.EntityHandle, "entityHandle", result);
        Different(before.OwnerHandle, after.OwnerHandle, "ownerHandle", result);
        Different(before.DxfType, after.DxfType, "dxfType", result);
        Different(before.RuntimeClass, after.RuntimeClass, "runtimeClass", result);
        Different(before.OwnerBlockName, after.OwnerBlockName, "ownerBlockName", result);
        Different(before.OwnerClass, after.OwnerClass, "ownerClass", result);
        Different(before.Layer, after.Layer, "layer", result);
        if (before.ColorIndex != after.ColorIndex) result.Add("colorIndex");
        Different(before.LinetypeHandle, after.LinetypeHandle, "linetypeHandle", result);
        if (before.Lineweight != after.Lineweight) result.Add("lineweight");
        if (!Equals(before.Extents, after.Extents)) result.Add("extents");
        if (before.ReferencedByDimensionCount != after.ReferencedByDimensionCount) result.Add("referencedByDimensionCount");
        if (before.ReferencedByNonDimensionCount != after.ReferencedByNonDimensionCount) result.Add("referencedByNonDimensionCount");
        if (before.DerivedDimensionGraphicsCandidate != after.DerivedDimensionGraphicsCandidate) result.Add("derivedDimensionGraphicsCandidate");
        Different(before.InvariantRowFingerprint, after.InvariantRowFingerprint, "invariantRowFingerprint", result);
        return result;
    }

    private static void Different(string before, string after, string field, List<string> changed)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal)) changed.Add(field);
    }

    private static CadInvariantExtentsDelta? Delta(CadInvariantExtents? before, CadInvariantExtents? after)
    {
        if (before is null || after is null || !Point.TryParse(before.Minimum, out var beforeMin) ||
            !Point.TryParse(before.Maximum, out var beforeMax) || !Point.TryParse(after.Minimum, out var afterMin) ||
            !Point.TryParse(after.Maximum, out var afterMax)) return null;
        return new CadInvariantExtentsDelta
        {
            MinimumX = Difference(beforeMin.X, afterMin.X),
            MinimumY = Difference(beforeMin.Y, afterMin.Y),
            MinimumZ = Difference(beforeMin.Z, afterMin.Z),
            MaximumX = Difference(beforeMax.X, afterMax.X),
            MaximumY = Difference(beforeMax.Y, afterMax.Y),
            MaximumZ = Difference(beforeMax.Z, afterMax.Z)
        };
    }

    private static string Difference(double before, double after) => (after - before).ToString("R", CultureInfo.InvariantCulture);

    private readonly record struct Point(double X, double Y, double Z)
    {
        public static bool TryParse(string value, out Point point)
        {
            var parts = value.Split(',', StringSplitOptions.None);
            if (parts.Length == 3 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                point = new Point(x, y, z);
                return true;
            }
            point = default;
            return false;
        }
    }
}
