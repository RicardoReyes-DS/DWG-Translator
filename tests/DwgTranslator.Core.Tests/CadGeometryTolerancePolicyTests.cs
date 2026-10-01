using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadGeometryTolerancePolicyTests
{
    [TestMethod]
    public void ObservedEllipseDeltaAndItsNegativeAreEquivalent()
    {
        var before = Row(maximumY: "-59.86510354484327");
        Assert.IsTrue(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before,
            Row(maximumY: "-59.86510354484328")));
        Assert.IsTrue(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before,
            Row(maximumY: "-59.86510354484326")));
    }

    [TestMethod]
    public void ZeroAndSmallCoordinatesHonorAbsoluteToleranceBoundary()
    {
        var before = Row(maximumY: "0");
        Assert.IsTrue(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, Row(maximumY: "0.0000000000005")));
        Assert.IsFalse(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, Row(maximumY: "0.000000000002")));
    }

    [TestMethod]
    public void LargeCoordinatesHonorRelativeToleranceBoundary()
    {
        var before = Row(maximumY: "1000000000");
        Assert.IsTrue(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, Row(maximumY: "1000000000.000005")));
        Assert.IsFalse(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, Row(maximumY: "1000000000.00002")));
    }

    [TestMethod]
    public void MaterialOrStructuralChangesRemainStrictFailures()
    {
        var before = Row(maximumY: "10");
        Assert.IsFalse(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, Row(maximumY: "10.0000001")));
        Assert.IsFalse(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, Row(maximumY: "10", layer: "OTHER")));
        Assert.IsFalse(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, Row(maximumY: "10", owner: "1A76B")));
        Assert.IsFalse(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, Row(maximumY: "10", dxfType: "LINE")));
        Assert.IsFalse(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(before, Row(maximumY: "10", target: true)));
    }

    [TestMethod]
    public void NonFiniteCoordinatesFailClosed()
    {
        foreach (var value in new[] { "NaN", "Infinity", "-Infinity" })
            Assert.IsFalse(CadGeometryTolerancePolicy.AreEquivalentGeneralInvariant(Row(maximumY: "0"), Row(maximumY: value)), value);
    }

    private static CadInvariantDiagnosticRow Row(string maximumY, string layer = "P-PIPE", string owner = "1A76A", string dxfType = "ELLIPSE", bool target = false) => new()
    {
        EntityHandle = "1A76F",
        OwnerHandle = owner,
        DxfType = dxfType,
        RuntimeClass = dxfType == "ELLIPSE" ? "Autodesk.AutoCAD.DatabaseServices.Ellipse" : "Autodesk.AutoCAD.DatabaseServices.Line",
        OwnerBlockName = "ASC_IPS-Elbow",
        OwnerClass = "BLOCK_RECORD",
        IsTargetText = target,
        IsAnonymousDimensionBlockName = false,
        ReferencedByDimensionCount = 0,
        ReferencedByNonDimensionCount = 1,
        DerivedDimensionGraphicsCandidate = false,
        Layer = layer,
        ColorIndex = 8,
        LinetypeHandle = "5C",
        Lineweight = -1,
        Extents = new CadInvariantExtents { Minimum = "44.15985196933291,-59.87636689388197,0", Maximum = $"44.181079753194155,{maximumY},0" },
        InvariantRowFingerprint = "sha256:" + new string('a', 64)
    };
}
