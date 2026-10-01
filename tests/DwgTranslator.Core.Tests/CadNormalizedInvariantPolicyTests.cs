using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadNormalizedInvariantPolicyTests
{
    [TestMethod]
    public void MatchingPostNormalizationExtentsPass()
    {
        var baseline = Set(Row(maximumY: "10.8333333333333", fingerprint: Fingerprint('a')));
        var candidate = Set(Row(maximumY: "10.8333333333333", fingerprint: Fingerprint('a')));

        Assert.IsTrue(CadNormalizedInvariantPolicy.AreEquivalent(baseline, candidate));
    }

    [TestMethod]
    public void ExistingNumericalNoiseToleranceIsPreservedButNotExpanded()
    {
        var baseline = Row(maximumY: "10", fingerprint: Fingerprint('a'));
        Assert.IsTrue(CadNormalizedInvariantPolicy.AreEquivalentRow(
            baseline, Row(maximumY: "10.0000000000005", fingerprint: Fingerprint('b'))));
        Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalentRow(
            baseline, Row(maximumY: "10.0000001", fingerprint: Fingerprint('b'))));
    }

    [TestMethod]
    public void AddedOrRemovedRowsFailClosed()
    {
        var row = Row(maximumY: "10", fingerprint: Fingerprint('a'));
        Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalent(Set(row), new Dictionary<string, CadInvariantDiagnosticRow>()));
        Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalent(new Dictionary<string, CadInvariantDiagnosticRow>(), Set(row)));
        Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalent(
            Set(row),
            new Dictionary<string, CadInvariantDiagnosticRow>(StringComparer.Ordinal)
            {
                ["1|2"] = row,
                ["1|3"] = row with { EntityHandle = "3" }
            }));
    }

    [TestMethod]
    public void TargetTextAndMTextNeverUseExtentsTolerance()
    {
        foreach (var dxfType in new[] { "TEXT", "MTEXT" })
        {
            var baseline = Row(maximumY: "10", fingerprint: Fingerprint('a'), target: true, dxfType: dxfType);
            Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalentRow(
                baseline,
                Row(maximumY: "10", fingerprint: Fingerprint('b'), target: true, dxfType: dxfType)), dxfType);
            Assert.IsTrue(CadNormalizedInvariantPolicy.AreEquivalentRow(
                baseline,
                Row(maximumY: "20", fingerprint: Fingerprint('a'), target: true, dxfType: dxfType)), dxfType);
        }
    }

    [TestMethod]
    public void EveryIdentityAndDiagnosticMetadataChangeFailsClosed()
    {
        var baseline = Row(maximumY: "10", fingerprint: Fingerprint('a'));
        var mutations = new CadInvariantDiagnosticRow[]
        {
            baseline with { EntityHandle = "3" },
            baseline with { OwnerHandle = "9" },
            baseline with { DxfType = "LINE" },
            baseline with { RuntimeClass = "Autodesk.AutoCAD.DatabaseServices.Line" },
            baseline with { OwnerBlockName = "OTHER" },
            baseline with { OwnerClass = "OTHER" },
            baseline with { IsTargetText = true },
            baseline with { IsAnonymousDimensionBlockName = false },
            baseline with { ReferencedByDimensionCount = 2 },
            baseline with { ReferencedByNonDimensionCount = 2 },
            baseline with { DerivedDimensionGraphicsCandidate = false },
            baseline with { Layer = "OTHER" },
            baseline with { ColorIndex = 7 },
            baseline with { LinetypeHandle = "6" },
            baseline with { Lineweight = 1 }
        };

        foreach (var mutation in mutations)
            Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalentRow(baseline, mutation));
    }

    [TestMethod]
    public void MissingAsymmetricMalformedAndNonFiniteExtentsFailClosed()
    {
        var baseline = Row(maximumY: "10", fingerprint: Fingerprint('a'));
        Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalentRow(baseline, baseline with { Extents = null }));
        Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalentRow(baseline, baseline with
        {
            Extents = new CadInvariantExtents { Minimum = "invalid", Maximum = "1,2,3" }
        }));
        foreach (var value in new[] { "NaN", "Infinity", "-Infinity" })
        {
            var nonFinite = Row(maximumY: value, fingerprint: Fingerprint('a'));
            Assert.IsFalse(CadNormalizedInvariantPolicy.AreEquivalentRow(nonFinite, nonFinite), value);
        }
    }

    private static Dictionary<string, CadInvariantDiagnosticRow> Set(CadInvariantDiagnosticRow row) =>
        new(StringComparer.Ordinal) { ["1|2"] = row };

    private static string Fingerprint(char value) => "sha256:" + new string(value, 64);

    private static CadInvariantDiagnosticRow Row(
        string maximumY,
        string fingerprint,
        bool target = false,
        string dxfType = "DIMENSION") => new()
        {
            EntityHandle = "2",
            OwnerHandle = "1",
            DxfType = dxfType,
            RuntimeClass = $"Autodesk.AutoCAD.DatabaseServices.{dxfType}",
            OwnerBlockName = "*D1",
            OwnerClass = "BLOCK_RECORD",
            IsTargetText = target,
            IsAnonymousDimensionBlockName = true,
            ReferencedByDimensionCount = 1,
            ReferencedByNonDimensionCount = 0,
            DerivedDimensionGraphicsCandidate = true,
            Layer = "BMS",
            ColorIndex = 8,
            LinetypeHandle = "5",
            Lineweight = -1,
            Extents = new CadInvariantExtents { Minimum = "0,0,0", Maximum = $"10,{maximumY},0" },
            InvariantRowFingerprint = fingerprint
        };
}
