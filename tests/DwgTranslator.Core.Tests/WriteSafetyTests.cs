using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class WriteSafetyTests
{
    [TestMethod]
    public void CompleteDistinctSameVolumeWriteIsAllowed()
    {
        Assert.IsTrue(WriteSafetyPolicy.Validate(Request()).IsSuccess);
    }

    [TestMethod]
    public void SourceCandidateAndFinalMustBeDistinct()
    {
        var request = Request();
        request.Payload!["finalPath"] = request.Payload["sourcePath"]!.GetValue<string>();
        Assert.AreEqual("INPUT_OUTPUT_SAME", WriteSafetyPolicy.Validate(request).Error!.Code);
    }

    [TestMethod]
    public void CandidateMustStayOnSameVolumeAndPathsRejectTraversal()
    {
        var crossVolume = Request();
        crossVolume.Payload!["candidatePath"] = "D:\\Output\\candidate.dwg";
        Assert.AreEqual("CROSS_VOLUME_NOT_ATOMIC", WriteSafetyPolicy.Validate(crossVolume).Error!.Code);

        var traversal = Request();
        traversal.Payload!["candidatePath"] = "C:\\Output\\..\\candidate.dwg";
        Assert.AreEqual("WRITE_PAYLOAD_INVALID", WriteSafetyPolicy.Validate(traversal).Error!.Code);
    }

    [TestMethod]
    public void SourceMayUseAnotherVolumeAndUncOutputIsSupported()
    {
        var differentSourceVolume = Request();
        differentSourceVolume.Payload!["sourcePath"] = "D:\\Inputs\\source.dwg";
        Assert.IsTrue(WriteSafetyPolicy.Validate(differentSourceVolume).IsSuccess);

        var unc = Request();
        unc.Payload!["candidatePath"] = "\\\\server\\share\\Output\\candidate.dwg";
        unc.Payload["finalPath"] = "\\\\server\\share\\Output\\translated.dwg";
        Assert.IsTrue(WriteSafetyPolicy.Validate(unc).IsSuccess);
    }

    [TestMethod]
    public void CoverageArithmeticMappingsAndUniquenessAreEnforced()
    {
        var mismatch = Request();
        mismatch.Payload!["approvalCoverage"]!["selected"] = 3;
        Assert.AreEqual("APPROVAL_COVERAGE_INVALID", WriteSafetyPolicy.Validate(mismatch).Error!.Code);

        var duplicate = Request();
        var mappings = duplicate.Payload!["mappings"]!.AsArray();
        mappings.Add(mappings[0]!.DeepClone());
        duplicate.Payload["approvalCoverage"]!["selected"] = 2;
        duplicate.Payload["approvalCoverage"]!["approved"] = 2;
        Assert.AreEqual("WRITE_MAPPING_DUPLICATE", WriteSafetyPolicy.Validate(duplicate).Error!.Code);

        var malformedIdentity = Request();
        malformedIdentity.Payload!["mappings"]![0]!["segmentId"] = "seg_001";
        Assert.AreEqual("WRITE_SEGMENT_ID_INVALID", WriteSafetyPolicy.Validate(malformedIdentity).Error!.Code);
    }

    private static WireEnvelope Request() => TestData.Request(new JsonObject
    {
        ["sourcePath"] = "C:\\Drawings\\source.dwg",
        ["expectedSourceHash"] = TestData.HashA,
        ["candidatePath"] = "C:\\Output\\candidate.dwg",
        ["finalPath"] = "C:\\Output\\translated.dwg",
        ["overwritePolicy"] = "FailIfExists",
        ["approvalCoverage"] = new JsonObject { ["selected"] = 1, ["approved"] = 1, ["excluded"] = 0, ["complete"] = true },
        ["mappings"] = new JsonArray
        {
            new JsonObject
            {
                ["segmentId"] = "seg_sha256_3a7ca53637585404b2f0c4793b1af59d3789e268dae46feb6344eb5384893353",
                ["handle"] = "A1B2",
                ["expectedSourceTextHash"] = TestData.HashA,
                ["approvedFinalText"] = "BOMBA",
                ["approvedFinalTextHash"] = "sha256:36a08baae7394fcccba326af6359c08c613c5c8cf24d86d16f4d8da101d69e93",
                ["expectedEntityType"] = "TEXT",
                ["expectedSpace"] = "ModelSpace",
                ["expectedLayout"] = null,
                ["expectedLayer"] = "NOTES"
            }
        },
        ["validationPolicy"] = "VisualStrictV2"
    }) with
    {
        MessageType = MessageTypes.WriteRequest
    };
}
