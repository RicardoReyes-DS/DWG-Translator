using System.Text.Json;
using System.Globalization;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadInvariantDiagnosticsPolicyTests
{
    private static readonly string[] EmptyKeys = [];
    private static readonly string[] ChangedA = ["10|A"];
    private static readonly string[] ExpectedChangedFields = ["layer", "colorIndex", "extents", "invariantRowFingerprint"];

    [TestMethod]
    public void ChangedRowReportsEverySafeFieldAndExtentsDelta()
    {
        var before = Row("A", extent: "1,2,3", maximum: "4,5,6");
        var after = before with { Layer = "OTHER", ColorIndex = 3, Extents = new CadInvariantExtents { Minimum = "2,4,6", Maximum = "8,10,12" }, InvariantRowFingerprint = "sha256:" + new string('b', 64) };

        var result = CadInvariantDiagnosticsPolicy.Create(
            new Dictionary<string, CadInvariantDiagnosticRow> { ["10|A"] = before },
            new Dictionary<string, CadInvariantDiagnosticRow> { ["10|A"] = after }, EmptyKeys, EmptyKeys, ChangedA);

        var difference = result.Rows.Single();
        CollectionAssert.AreEquivalent(ExpectedChangedFields, difference.FieldsChanged);
        Assert.AreEqual("1", difference.ExtentsDelta!.MinimumX);
        Assert.AreEqual("2", difference.ExtentsDelta.MinimumY);
        Assert.AreEqual("6", difference.ExtentsDelta.MaximumZ);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public void BoundedEvidenceDoesNotExposeTextAndRetainsCounts()
    {
        var before = new Dictionary<string, CadInvariantDiagnosticRow>();
        var after = new Dictionary<string, CadInvariantDiagnosticRow>();
        var changed = new List<string>();
        for (var index = 0; index < CadInvariantDiagnosticsPolicy.MaximumRows + 1; index++)
        {
            var key = $"10|{index.ToString("X", CultureInfo.InvariantCulture)}";
            before[key] = Row(index.ToString("X", CultureInfo.InvariantCulture), derived: index % 2 == 0);
            after[key] = before[key] with { Extents = new CadInvariantExtents { Minimum = "1,0,0", Maximum = "2,1,1" }, InvariantRowFingerprint = "sha256:" + index.ToString("x64", CultureInfo.InvariantCulture) };
            changed.Add(key);
        }

        var result = CadInvariantDiagnosticsPolicy.Create(before, after, EmptyKeys, EmptyKeys, changed);
        var json = JsonSerializer.Serialize(result);

        Assert.AreEqual(CadInvariantDiagnosticsPolicy.MaximumRows + 1, result.ChangedCount);
        Assert.AreEqual(CadInvariantDiagnosticsPolicy.MaximumRows, result.Rows.Count);
        Assert.IsTrue(result.Truncated);
        Assert.IsFalse(json.Contains("TextString", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Contents", StringComparison.Ordinal));
        Assert.IsTrue(result.Rows.Any(row => row.Before!.DerivedDimensionGraphicsCandidate is true));
    }

    private static CadInvariantDiagnosticRow Row(string handle, string extent = "0,0,0", string maximum = "1,1,1", bool derived = false) => new()
    {
        EntityHandle = handle,
        OwnerHandle = "10",
        DxfType = "LINE",
        RuntimeClass = "Autodesk.AutoCAD.DatabaseServices.Line",
        OwnerBlockName = derived ? "*D42" : "ORDINARY",
        OwnerClass = "BLOCK_RECORD",
        IsTargetText = false,
        IsAnonymousDimensionBlockName = derived,
        ReferencedByDimensionCount = derived ? 1 : 0,
        ReferencedByNonDimensionCount = 0,
        DerivedDimensionGraphicsCandidate = derived,
        Layer = "A-ANNO",
        ColorIndex = 7,
        LinetypeHandle = "12",
        Lineweight = 25,
        Extents = new CadInvariantExtents { Minimum = extent, Maximum = maximum },
        InvariantRowFingerprint = "sha256:" + new string('a', 64)
    };
}
