using System.Text.Json;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadReconciliationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public void StrictPolicyAcceptsCompleteReadOnlyEvidence()
    {
        var fixture = Fixture();
        var result = CadReconcileResultPolicy.Validate(fixture.Request, fixture.Response);
        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
    }

    [TestMethod]
    public void StrictPolicyRejectsMissingVisualEvidence()
    {
        var fixture = Fixture(includeEvidence: false);
        var result = CadReconcileResultPolicy.Validate(fixture.Request, fixture.Response);
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("RECONCILIATION_APPLIED_SET_MISMATCH", result.Error!.Code);
    }

    private static (WireEnvelope Request, WireEnvelope Response) Fixture(bool includeEvidence = true)
    {
        const string hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var mapping = new CadWriteMapping
        {
            SegmentId = "segment-1",
            Handle = "10",
            ExpectedSourceTextHash = hash,
            ApprovedFinalText = "synthetic",
            ApprovedFinalTextHash = hash,
            ExpectedEntityType = "TEXT",
            ExpectedSpace = "ModelSpace",
            ExpectedLayout = null,
            ExpectedLayer = "0"
        };
        var requestPayload = new CadReconcileRequestPayload
        {
            SourcePath = @"C:\safe\source.dwg",
            ExpectedSourceHash = hash,
            OrphanPath = @"C:\safe\orphan.dwg",
            ExpectedOrphanHash = hash,
            Mappings = [mapping],
            ValidationPolicy = "VisualStrictV2"
        };
        var request = WireEnvelope.Request(MessageTypes.ReconcileRequest, Guid.NewGuid(), Guid.NewGuid(), hash, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToNode(requestPayload, Json)!.AsObject());
        var properties = new Dictionary<string, string> { { "handle", "10" }, { "entityType", "TEXT" }, { "space", "ModelSpace" }, { "layout", "" }, { "ownerRecord", "*Model_Space" }, { "layer", "0" }, { "colorIndex", "256" }, { "linetypeHandle", "14" }, { "lineWeight", "-1" }, { "position", "0,0,0" }, { "alignmentPoint", "0,0,0" }, { "height", "1" }, { "rotation", "0" }, { "widthFactor", "1" }, { "oblique", "0" }, { "textStyleHandle", "2" }, { "horizontalMode", "TextLeft" }, { "verticalMode", "TextBase" }, { "normal", "0,0,1" }, { "thickness", "0" }, { "mirroredInX", "False" }, { "mirroredInY", "False" } };
        var responsePayload = new CadReconcileResponsePayload
        {
            SourceHashAfter = hash,
            OrphanHash = hash,
            Validation = new() { ReopenedByAutoCAD = true, EntityMappingValid = true, GeometryInvariantsValid = true, FormatTokenIntegrityValid = true, VisualInvariantsValid = true, Policy = "VisualStrictV2" },
            Applied = [new() { SegmentId = "segment-1", Result = "ReconciledReadOnly", PostWriteTextHash = hash, VisualEvidence = includeEvidence ? new() { BeforeProperties = properties, AfterProperties = properties, BeforeFingerprint = CadVisualEvidencePolicy.Fingerprint(properties), AfterFingerprint = CadVisualEvidencePolicy.Fingerprint(properties), InvariantMatch = true, BeforeExtents = "0,0,0:1,1,0", AfterExtents = "0,0,0:1,1,0", BoundsChanged = false, VisualReviewRequired = true } : null }]
        };
        var response = request with { MessageType = MessageTypes.ReconcileResponse, Status = OperationStatus.Succeeded, Payload = JsonSerializer.SerializeToNode(responsePayload, Json)!.AsObject() };
        return (request, response);
    }
}
