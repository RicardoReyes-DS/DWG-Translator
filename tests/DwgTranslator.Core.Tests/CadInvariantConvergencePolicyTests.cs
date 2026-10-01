using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadInvariantConvergencePolicyTests
{
    [TestMethod]
    public void WarmupCaptureCannotBecomeAuthorityAndEarliestSuccessIsCaptureFour()
    {
        var rows = Set(Row("10"));

        var second = CadInvariantConvergencePolicy.Evaluate(2, 0, rows, rows);
        var third = CadInvariantConvergencePolicy.Evaluate(3, second.ConsecutiveAuthoritativeSnapshots, rows, rows);
        var fourth = CadInvariantConvergencePolicy.Evaluate(4, third.ConsecutiveAuthoritativeSnapshots, rows, rows);

        Assert.AreEqual(CadInvariantObservationOutcome.Continue, second.Outcome);
        Assert.AreEqual(1, second.ConsecutiveAuthoritativeSnapshots);
        Assert.AreEqual(CadInvariantObservationOutcome.Continue, third.Outcome);
        Assert.AreEqual(2, third.ConsecutiveAuthoritativeSnapshots);
        Assert.AreEqual(CadInvariantObservationOutcome.Stable, fourth.Outcome);
        Assert.AreEqual(3, fourth.ConsecutiveAuthoritativeSnapshots);
    }

    [TestMethod]
    public void ColdToWarmExtentsCanConvergeWithoutRelaxingTheFinalComparison()
    {
        var cold = Set(Row("10"));
        var warm = Set(Row("20"));

        var second = CadInvariantConvergencePolicy.Evaluate(2, 0, cold, warm);
        var third = CadInvariantConvergencePolicy.Evaluate(3, second.ConsecutiveAuthoritativeSnapshots, warm, warm);
        var fourth = CadInvariantConvergencePolicy.Evaluate(4, third.ConsecutiveAuthoritativeSnapshots, warm, warm);

        Assert.AreEqual(CadInvariantObservationOutcome.Continue, second.Outcome);
        Assert.AreEqual(CadInvariantObservationOutcome.Stable, fourth.Outcome);
        Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalent(cold, warm));
    }

    [TestMethod]
    public void OneLateSafeTransitionCanStillConvergeAtTheHardLimit()
    {
        var a = Set(Row("10"));
        var b = Set(Row("20"));
        var c = Set(Row("30"));

        var second = CadInvariantConvergencePolicy.Evaluate(2, 0, a, b);
        var third = CadInvariantConvergencePolicy.Evaluate(3, second.ConsecutiveAuthoritativeSnapshots, b, c);
        var fourth = CadInvariantConvergencePolicy.Evaluate(4, third.ConsecutiveAuthoritativeSnapshots, c, c);
        var fifth = CadInvariantConvergencePolicy.Evaluate(5, fourth.ConsecutiveAuthoritativeSnapshots, c, c);

        Assert.AreEqual(CadInvariantObservationOutcome.Continue, fourth.Outcome);
        Assert.AreEqual(CadInvariantObservationOutcome.Stable, fifth.Outcome);
    }

    [TestMethod]
    public void AlternatingSafeExtentsExhaustTheBoundedSequence()
    {
        var a = Set(Row("10"));
        var b = Set(Row("20"));

        var second = CadInvariantConvergencePolicy.Evaluate(2, 0, a, b);
        var third = CadInvariantConvergencePolicy.Evaluate(3, second.ConsecutiveAuthoritativeSnapshots, b, a);
        var fourth = CadInvariantConvergencePolicy.Evaluate(4, third.ConsecutiveAuthoritativeSnapshots, a, b);
        var fifth = CadInvariantConvergencePolicy.Evaluate(5, fourth.ConsecutiveAuthoritativeSnapshots, b, a);

        Assert.AreEqual(CadInvariantObservationOutcome.Exhausted, fifth.Outcome);
        Assert.AreEqual(1, fifth.ConsecutiveAuthoritativeSnapshots);
    }

    [TestMethod]
    public void AddedRemovedTargetMetadataAndInvalidExtentsAreUnsafeImmediately()
    {
        var baseline = Row("10");
        var cases = new (Dictionary<string, CadInvariantDiagnosticRow> Before, Dictionary<string, CadInvariantDiagnosticRow> After)[]
        {
            (Set(baseline), new Dictionary<string, CadInvariantDiagnosticRow>()),
            (new Dictionary<string, CadInvariantDiagnosticRow>(), Set(baseline)),
            (Set(baseline with { IsTargetText = true }), Set(baseline with { IsTargetText = true, InvariantRowFingerprint = Fingerprint('b') })),
            (Set(baseline), Set(baseline with { Layer = "OTHER", Extents = Extents("20") })),
            (Set(baseline), Set(baseline with { Extents = null })),
            (Set(baseline), Set(baseline with { Extents = Extents("NaN") }))
        };

        foreach (var item in cases)
        {
            var decision = CadInvariantConvergencePolicy.Evaluate(2, 0, item.Before, item.After);
            Assert.AreEqual(CadInvariantObservationOutcome.Unsafe, decision.Outcome);
        }
    }

    [TestMethod]
    public void EveryIdentityAndDiagnosticMetadataMutationIsUnsafeForConvergence()
    {
        var baseline = Row("10");
        var mutations = new CadInvariantDiagnosticRow[]
        {
            baseline with { EntityHandle = "3", Extents = Extents("20") },
            baseline with { OwnerHandle = "9", Extents = Extents("20") },
            baseline with { DxfType = "LINE", Extents = Extents("20") },
            baseline with { RuntimeClass = "Autodesk.AutoCAD.DatabaseServices.Line", Extents = Extents("20") },
            baseline with { OwnerBlockName = "OTHER", Extents = Extents("20") },
            baseline with { OwnerClass = "OTHER", Extents = Extents("20") },
            baseline with { IsTargetText = true, Extents = Extents("20") },
            baseline with { IsAnonymousDimensionBlockName = true, Extents = Extents("20") },
            baseline with { ReferencedByDimensionCount = 1, Extents = Extents("20") },
            baseline with { ReferencedByNonDimensionCount = 1, Extents = Extents("20") },
            baseline with { DerivedDimensionGraphicsCandidate = true, Extents = Extents("20") },
            baseline with { Layer = "OTHER", Extents = Extents("20") },
            baseline with { ColorIndex = 7, Extents = Extents("20") },
            baseline with { LinetypeHandle = "6", Extents = Extents("20") },
            baseline with { Lineweight = 1, Extents = Extents("20") }
        };

        foreach (var mutation in mutations)
        {
            var decision = CadInvariantConvergencePolicy.Evaluate(2, 0, Set(baseline), Set(mutation));
            Assert.AreEqual(CadInvariantObservationOutcome.Unsafe, decision.Outcome);
        }
    }

    private static Dictionary<string, CadInvariantDiagnosticRow> Set(CadInvariantDiagnosticRow row) =>
        new(StringComparer.Ordinal) { ["1|2"] = row };

    private static CadInvariantExtents Extents(string maximumY) =>
        new() { Minimum = "0,0,0", Maximum = $"10,{maximumY},0" };

    private static string Fingerprint(char value) => "sha256:" + new string(value, 64);

    private static CadInvariantDiagnosticRow Row(string maximumY) => new()
    {
        EntityHandle = "2",
        OwnerHandle = "1",
        DxfType = "DIMENSION",
        RuntimeClass = "Autodesk.AutoCAD.DatabaseServices.RotatedDimension",
        OwnerBlockName = "*Model_Space",
        OwnerClass = "BLOCK_RECORD",
        IsTargetText = false,
        IsAnonymousDimensionBlockName = false,
        ReferencedByDimensionCount = 0,
        ReferencedByNonDimensionCount = 0,
        DerivedDimensionGraphicsCandidate = false,
        Layer = "BMS",
        ColorIndex = 256,
        LinetypeHandle = "5C",
        Lineweight = -1,
        Extents = Extents(maximumY),
        InvariantRowFingerprint = Fingerprint('a')
    };
}
