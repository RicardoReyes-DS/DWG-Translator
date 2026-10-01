using System.Globalization;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

/// <summary>
/// Centralized, fail-closed comparison for floating-point geometry evidence.
/// It is intentionally limited to non-target entity extents; drawing data is
/// never rounded or changed.
/// </summary>
public static class CadGeometryTolerancePolicy
{
    // These values accept numerical representation noise only. They are not a
    // drawing-unit tolerance and must not be used for placement or text evidence.
    public const double AbsoluteTolerance = 1e-12;
    public const double RelativeTolerance = 1e-14;

    public static bool AreEquivalentGeneralInvariant(
        CadInvariantDiagnosticRow before,
        CadInvariantDiagnosticRow after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        // Target TEXT/MTEXT is protected by the stricter target/visual checks.
        if (before.IsTargetText || after.IsTargetText) return false;

        return Same(before.EntityHandle, after.EntityHandle) &&
               Same(before.OwnerHandle, after.OwnerHandle) &&
               Same(before.DxfType, after.DxfType) &&
               Same(before.RuntimeClass, after.RuntimeClass) &&
               Same(before.OwnerClass, after.OwnerClass) &&
               Same(before.Layer, after.Layer) &&
               before.ColorIndex == after.ColorIndex &&
               Same(before.LinetypeHandle, after.LinetypeHandle) &&
               before.Lineweight == after.Lineweight &&
               AreEquivalentExtents(before.Extents, after.Extents);
    }

    public static bool AreEquivalentExtents(CadInvariantExtents? before, CadInvariantExtents? after)
    {
        if (before is null || after is null) return before is null && after is null;
        return Point.TryParse(before.Minimum, out var beforeMinimum) &&
               Point.TryParse(before.Maximum, out var beforeMaximum) &&
               Point.TryParse(after.Minimum, out var afterMinimum) &&
               Point.TryParse(after.Maximum, out var afterMaximum) &&
               Equivalent(beforeMinimum.X, afterMinimum.X) &&
               Equivalent(beforeMinimum.Y, afterMinimum.Y) &&
               Equivalent(beforeMinimum.Z, afterMinimum.Z) &&
               Equivalent(beforeMaximum.X, afterMaximum.X) &&
               Equivalent(beforeMaximum.Y, afterMaximum.Y) &&
               Equivalent(beforeMaximum.Z, afterMaximum.Z);
    }

    public static bool HasFiniteExtents(CadInvariantExtents? extents) =>
        extents is null ||
        Point.TryParse(extents.Minimum, out _) && Point.TryParse(extents.Maximum, out _);

    private static bool Same(string before, string after) => string.Equals(before, after, StringComparison.Ordinal);

    private static bool Equivalent(double before, double after)
    {
        if (!double.IsFinite(before) || !double.IsFinite(after)) return false;
        var scale = Math.Max(Math.Abs(before), Math.Abs(after));
        return Math.Abs(before - after) <= AbsoluteTolerance + RelativeTolerance * scale;
    }

    private readonly record struct Point(double X, double Y, double Z)
    {
        public static bool TryParse(string value, out Point point)
        {
            var values = value.Split(',', StringSplitOptions.None);
            if (values.Length == 3 && TryFinite(values[0], out var x) && TryFinite(values[1], out var y) && TryFinite(values[2], out var z))
            {
                point = new Point(x, y, z);
                return true;
            }
            point = default;
            return false;
        }

        private static bool TryFinite(string value, out double number) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }
}
