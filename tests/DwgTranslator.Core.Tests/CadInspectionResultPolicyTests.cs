using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadInspectionResultPolicyTests
{
    [TestMethod]
    public void ContractFixtureIsAcceptedAndFingerprintVectorIsFrozen()
    {
        var fingerprint = CadDrawingFingerprintV1.Create(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        Assert.AreEqual("sha256:7a25e27ef7bbb6168e277955db683d7790e6eeeb604dee6849710998ed428985", fingerprint.Value);

        var result = CadInspectionResultPolicy.Validate(LoadFixture(), TestData.HashA);
        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(fingerprint.Value, result.Value!.DrawingFingerprint);
        Assert.AreEqual(2, result.Value.SupportedCount);
    }

    [TestMethod]
    public void SourceAndSupportedCountsAreIntegrityChecked()
    {
        Assert.AreEqual("SOURCE_CHANGED", CadInspectionResultPolicy.Validate(LoadFixture(), TestData.HashB).Error!.Code);

        var impossible = LoadFixture();
        impossible.Payload!["supportedCount"] = 4;
        Assert.AreEqual("CAD_SUPPORTED_COUNT_INVALID", CadInspectionResultPolicy.Validate(impossible, TestData.HashA).Error!.Code);

        var missingClassification = LoadFixture();
        missingClassification.Payload!["unsupported"]!.AsArray().RemoveAt(1);
        Assert.AreEqual("CAD_SUPPORTED_COUNT_INVALID", CadInspectionResultPolicy.Validate(missingClassification, TestData.HashA).Error!.Code);
    }

    [TestMethod]
    public void HostAndUnsupportedSummariesFailClosed()
    {
        var wrongHost = LoadFixture();
        wrongHost.Payload!["autocad"]!["product"] = "CompatibleCAD";
        Assert.AreEqual("CAD_HOST_DESCRIPTOR_INVALID", CadInspectionResultPolicy.Validate(wrongHost, TestData.HashA).Error!.Code);

        var duplicate = LoadFixture();
        var summaries = duplicate.Payload!["unsupported"]!.AsArray();
        summaries.Add(summaries[0]!.DeepClone());
        Assert.AreEqual("CAD_UNSUPPORTED_SUMMARY_DUPLICATE", CadInspectionResultPolicy.Validate(duplicate, TestData.HashA).Error!.Code);
    }

    [TestMethod]
    public void ExplicitNullInventoryReturnsContractError()
    {
        var response = LoadFixture();
        response.Payload!["inventory"] = null;
        Assert.AreEqual("CAD_INSPECTION_PAYLOAD_INVALID", CadInspectionResultPolicy.Validate(response, TestData.HashA).Error!.Code);
        Assert.AreEqual("CAD_DRAWING_FINGERPRINT_GUID_EMPTY", CadDrawingFingerprintV1.Create(Guid.Empty).Error!.Code);
    }

    [TestMethod]
    public void TypedCadFailureIsPreservedForActionableDiagnosis()
    {
        var response = LoadFixture() with
        {
            Status = OperationStatus.Failed,
            Payload = null,
            Error = new ContractError("CAD_SESSION_UNAVAILABLE", ErrorCategory.Environment, "Safe diagnostic.", false)
        };
        Assert.AreEqual("CAD_SESSION_UNAVAILABLE", CadInspectionResultPolicy.Validate(response, TestData.HashA).Error!.Code);
    }

    private static WireEnvelope LoadFixture()
    {
        var path = Path.Combine(Root, "fixtures", "v1", "valid", "cad-inspect-response.json");
        var result = EnvelopeCodec.Decode(File.ReadAllBytes(path));
        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        return result.Value!;
    }

    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "fixtures", "v1")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
