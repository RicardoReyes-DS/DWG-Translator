using DwgTranslator.Application;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadSegmentIdentityTests
{
    [TestMethod]
    public void FrozenVectorProducesExactSegmentAndSourceTextHashes()
    {
        var identity = CadSegmentIdentityV1.Create(
            TestData.HashA,
            TestData.HashB,
            new CadSegmentAddress("TEXT", "A1B2", "ModelSpace", null, "NOTES", 0),
            "PUMP {TAG_01}");

        Assert.IsTrue(identity.IsSuccess, identity.Error?.Code);
        Assert.AreEqual("seg_sha256_3a7ca53637585404b2f0c4793b1af59d3789e268dae46feb6344eb5384893353", identity.Value!.SegmentId);
        Assert.AreEqual("sha256:d9281850f5cd986026891576eb115b2ed200f242df5ff691fd7c3fdde4d02564", identity.Value.SourceTextHash);
    }

    [TestMethod]
    public void IdentityIgnoresMutableLayerButChangesWithCadAddress()
    {
        var original = Create(new CadSegmentAddress("MTEXT", "ABC", "PaperSpace", "Layout1", "NOTES", 0));
        var movedLayer = Create(new CadSegmentAddress("MTEXT", "ABC", "PaperSpace", "Layout1", "TRANSLATED", 0));
        var otherLayout = Create(new CadSegmentAddress("MTEXT", "ABC", "PaperSpace", "Layout2", "NOTES", 0));

        Assert.AreEqual(original.SegmentId, movedLayer.SegmentId);
        Assert.AreNotEqual(original.SegmentId, otherLayout.SegmentId);
    }

    [TestMethod]
    public void AddressValidationRejectsAmbiguousOrOutOfScopeEntities()
    {
        Assert.AreEqual("CAD_SEGMENT_LAYOUT_INVALID", CreateResult(new CadSegmentAddress("TEXT", "A1", "ModelSpace", "Model", "NOTES", 0)).Error!.Code);
        Assert.AreEqual("CAD_SEGMENT_LAYOUT_INVALID", CreateResult(new CadSegmentAddress("TEXT", "A1", "PaperSpace", null, "NOTES", 0)).Error!.Code);
        Assert.AreEqual("CAD_SEGMENT_ADDRESS_INVALID", CreateResult(new CadSegmentAddress("ATTRIB", "A1", "ModelSpace", null, "NOTES", 0)).Error!.Code);
        Assert.AreEqual("CAD_SEGMENT_ADDRESS_INVALID", CreateResult(new CadSegmentAddress("TEXT", "a1", "ModelSpace", null, "NOTES", 0)).Error!.Code);
    }

    private static CadSegmentIdentity Create(CadSegmentAddress address) => CreateResult(address).Value!;

    private static DwgTranslator.Contracts.Result<CadSegmentIdentity> CreateResult(CadSegmentAddress address) =>
        CadSegmentIdentityV1.Create(TestData.HashA, TestData.HashB, address, "text");
}
