using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadExtractionResultPolicyTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private const string ManifestBasename = "SYNTHETIC-BMS-001.dwg";
    [TestMethod]
    public void ContractFixtureIsAcceptedWithDeterministicIdentity()
    {
        var result = CadExtractionResultPolicy.Validate(LoadFixture(), TestData.HashA, TestData.HashB, ManifestBasename);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(1, result.Value!.Count);
        Assert.AreEqual("A1B2", result.Value[0].Entity.Handle);
    }

    [TestMethod]
    public void FailedCadEnvelopePreservesTypedErrorAndMissingErrorUsesGenericFallback()
    {
        var cadError = new ContractError(
            "DOCUMENT_LOCKED",
            ErrorCategory.Concurrency,
            "The drawing could not be opened for shared read access.",
            false,
            DiagnosticId: "HRESULT_0x80070020");
        var failed = LoadFixture() with
        {
            Status = OperationStatus.Failed,
            Payload = null,
            Error = cadError
        };

        var propagated = CadExtractionResultPolicy.Validate(failed, TestData.HashA, TestData.HashB, ManifestBasename);

        Assert.IsFalse(propagated.IsSuccess);
        Assert.AreEqual(cadError, propagated.Error);

        var malformed = failed with { Error = null };
        var fallback = CadExtractionResultPolicy.Validate(malformed, TestData.HashA, TestData.HashB, ManifestBasename);
        Assert.IsFalse(fallback.IsSuccess);
        Assert.AreEqual("CAD_EXTRACTION_RESPONSE_STATUS_INVALID", fallback.Error!.Code);
        Assert.AreEqual(ErrorCategory.Contract, fallback.Error.Category);
    }

    [TestMethod]
    public void ChangedSourceAndTamperedTextFailIntegrityChecks()
    {
        Assert.AreEqual("SOURCE_CHANGED", CadExtractionResultPolicy.Validate(
            LoadFixture(), TestData.HashB, TestData.HashB, ManifestBasename).Error!.Code);

        var tampered = LoadFixture();
        Segment(tampered)["sourceText"] = "PUMP {TAG_02}";
        Assert.AreEqual("CAD_SOURCE_TEXT_HASH_MISMATCH", CadExtractionResultPolicy.Validate(
            tampered, TestData.HashA, TestData.HashB, ManifestBasename).Error!.Code);
    }

    [TestMethod]
    public void IdentityAndDirectHandleMustBeUnique()
    {
        var wrongIdentity = LoadFixture();
        Segment(wrongIdentity)["segmentId"] = $"seg_sha256_{new string('c', 64)}";
        Assert.AreEqual("CAD_SEGMENT_ID_MISMATCH", CadExtractionResultPolicy.Validate(
            wrongIdentity, TestData.HashA, TestData.HashB, ManifestBasename).Error!.Code);

        var duplicate = LoadFixture();
        duplicate.Payload!["segments"]!.AsArray().Add(Segment(duplicate).DeepClone());
        Assert.AreEqual("CAD_SEGMENT_DUPLICATE", CadExtractionResultPolicy.Validate(
            duplicate, TestData.HashA, TestData.HashB, ManifestBasename).Error!.Code);
    }

    [TestMethod]
    public void UnknownPayloadFieldsAndFieldTokensFailClosed()
    {
        var unknown = LoadFixture();
        unknown.Payload!["unexpected"] = true;
        Assert.AreEqual("IPC_PAYLOAD_INVALID", CadExtractionResultPolicy.Validate(
            unknown, TestData.HashA, TestData.HashB, ManifestBasename).Error!.Code);

        var fieldToken = LoadFixture();
        Segment(fieldToken)["protectedTokens"] = new JsonArray(new JsonObject
        {
            ["token"] = "%<field>%",
            ["kind"] = "FieldMarker",
            ["ordinal"] = 0
        });
        Assert.AreEqual("CAD_PROTECTED_TOKEN_INVALID", CadExtractionResultPolicy.Validate(
            fieldToken, TestData.HashA, TestData.HashB, ManifestBasename).Error!.Code);

        var phantomToken = LoadFixture();
        Segment(phantomToken)["protectedTokens"] = new JsonArray(new JsonObject
        {
            ["token"] = "{MISSING}",
            ["kind"] = "Placeholder",
            ["ordinal"] = 0
        });
        Assert.AreEqual("CAD_PROTECTED_TOKEN_NOT_FOUND", CadExtractionResultPolicy.Validate(
            phantomToken, TestData.HashA, TestData.HashB, ManifestBasename).Error!.Code);
    }

    [TestMethod]
    public void ExplicitNullsFailAsContractErrorsInsteadOfInternalExceptions()
    {
        var nullSegments = LoadFixture();
        nullSegments.Payload!["segments"] = null;
        Assert.AreEqual("CAD_EXTRACTION_PAYLOAD_INVALID", CadExtractionResultPolicy.Validate(
            nullSegments, TestData.HashA, TestData.HashB, ManifestBasename).Error!.Code);

        var nullEntity = LoadFixture();
        Segment(nullEntity)["entity"] = null;
        Assert.AreEqual("CAD_EXTRACTION_PAYLOAD_INVALID", CadExtractionResultPolicy.Validate(
            nullEntity, TestData.HashA, TestData.HashB, ManifestBasename).Error!.Code);
    }

    [TestMethod]
    public void MTextLineBreakClassificationUsesExactContents()
    {
        var response = LoadFixture();
        var segment = Segment(response);
        segment["sourceText"] = "FIRST\\PSECOND\nTHIRD";
        segment["lineBreakStyle"] = "MTextParagraph";
        segment["entity"]!["type"] = "MTEXT";
        segment["protectedTokens"] = new JsonArray(
            new JsonObject { ["token"] = "\\P", ["kind"] = "CadFormatControl", ["ordinal"] = 0 },
            new JsonObject { ["token"] = "\n", ["kind"] = "CadFormatControl", ["ordinal"] = 1 });
        UpdateIdentity(segment);

        Assert.AreEqual("CAD_LINE_BREAK_STYLE_MISMATCH", CadExtractionResultPolicy.Validate(
            response, TestData.HashA, TestData.HashB, ManifestBasename).Error!.Code);
        segment["lineBreakStyle"] = "Mixed";
        Assert.IsTrue(CadExtractionResultPolicy.Validate(
            response, TestData.HashA, TestData.HashB, ManifestBasename).IsSuccess);
    }

    [TestMethod]
    public void CompleteSemanticContextIsAcceptedWhilePartialOrTamperedContextFailsClosed()
    {
        var response = LoadFixture();
        var legacy = CadExtractionResultPolicy.Validate(response, TestData.HashA, TestData.HashB, ManifestBasename);
        Assert.IsTrue(legacy.IsSuccess, legacy.Error?.Code);
        var source = legacy.Value!.Single();
        var contexts = CadSemanticContextBuilder.Build([new CadSemanticContextInput(
            source.SegmentId, source.Entity.Type, source.Entity.Handle, source.Entity.Space, source.Entity.Layout,
            source.Entity.BlockPath, source.Entity.Layer, source.SourceText, source.SourceTextHash,
            10, 20, 2.5, "ExtentsCenter", ManifestBasename)]);
        Assert.IsTrue(contexts.IsSuccess, contexts.Error?.Code);
        Assert.AreEqual(CadSemanticContextBuilder.PolicyVersionOneZero, contexts.Value![source.SegmentId].Version);
        Segment(response)["semanticContext"] = JsonSerializer.SerializeToNode(contexts.Value![source.SegmentId], WebJson);
        Assert.IsTrue(CadExtractionResultPolicy.Validate(
            response, TestData.HashA, TestData.HashB, ManifestBasename).IsSuccess);

        var tampered = EnvelopeCodec.Decode(EnvelopeCodec.Encode(response)).Value!;
        tampered.Payload!["segments"]![0]!["semanticContext"]!["signals"] = new JsonArray("ROOF_CONTEXT");
        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_SET_INVALID",
            CadExtractionResultPolicy.Validate(
                tampered, TestData.HashA, TestData.HashB, ManifestBasename).Error?.Code);

        var partial = LoadFixture();
        var second = Segment(partial).DeepClone().AsObject();
        second["entity"]!["handle"] = "A1B3";
        second["sourceText"] = "VALVE";
        second["protectedTokens"] = new JsonArray();
        UpdateIdentity(second);
        partial.Payload!["segments"]!.AsArray().Add(second);
        Segment(partial)["semanticContext"] = JsonSerializer.SerializeToNode(contexts.Value[source.SegmentId], WebJson);
        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_PARTIAL",
            CadExtractionResultPolicy.Validate(
                partial, TestData.HashA, TestData.HashB, ManifestBasename).Error?.Code);
    }

    [TestMethod]
    public void VersionOneTwoArchitecturalFallbackIsBoundToTheActualManifestBasename()
    {
        var response = LoadFixture();
        var segmentNode = Segment(response);
        segmentNode["entity"]!["layer"] = "0";
        UpdateIdentity(segmentNode);
        var legacy = CadExtractionResultPolicy.Validate(
            response, TestData.HashA, TestData.HashB, "SAMPLE-ARQ-001.dwg");
        Assert.IsTrue(legacy.IsSuccess, legacy.Error?.Code);
        var segment = legacy.Value!.Single();
        var contexts = CadSemanticContextBuilder.Build(
            [new CadSemanticContextInput(
                segment.SegmentId,
                segment.Entity.Type,
                segment.Entity.Handle,
                segment.Entity.Space,
                segment.Entity.Layout,
                segment.Entity.BlockPath,
                segment.Entity.Layer,
                segment.SourceText,
                segment.SourceTextHash,
                10,
                20,
                2.5,
                "ExtentsCenter",
                "SAMPLE-ARQ-001.dwg")],
            CadSemanticContextBuilder.PolicyVersionOneTwo);
        Assert.IsTrue(contexts.IsSuccess, contexts.Error?.Code);
        var context = contexts.Value![segment.SegmentId];
        Assert.AreEqual("Architectural", context.Discipline);
        Assert.AreEqual(0, context.DisciplineEvidence.Count);
        Assert.IsFalse(context.DisciplineConflict);
        Assert.AreEqual(CadSemanticContextBuilder.DrawingNameArchitecturalFallback,
            context.DisciplineResolution);
        segmentNode["semanticContext"] = JsonSerializer.SerializeToNode(context, WebJson);

        var forgedNonArq = CadExtractionResultPolicy.Validate(
            response, TestData.HashA, TestData.HashB, "source.dwg");
        Assert.IsFalse(forgedNonArq.IsSuccess);
        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_MANIFEST_BINDING_MISMATCH", forgedNonArq.Error!.Code);
        Assert.AreEqual(ErrorCategory.Integrity, forgedNonArq.Error.Category);

        var manifestBoundArq = CadExtractionResultPolicy.Validate(
            response, TestData.HashA, TestData.HashB, "SAMPLE-ARQ-001.dwg");
        Assert.IsTrue(manifestBoundArq.IsSuccess, manifestBoundArq.Error?.Code);
    }

    [TestMethod]
    [DataRow("ELE", "Electrical", CadSemanticContextBuilder.DrawingNameElectricalFallback)]
    [DataRow("EST", "Structural", CadSemanticContextBuilder.DrawingNameStructuralFallback)]
    public void VersionOneThreeDisciplineFallbackRequiresTheExactManifestToken(
        string token, string discipline, string resolution)
    {
        var response = LoadFixture();
        var segmentNode = Segment(response);
        segmentNode["entity"]!["layer"] = "0";
        UpdateIdentity(segmentNode);
        var basename = $"SYNTHETIC-{token}-001.dwg";
        var legacy = CadExtractionResultPolicy.Validate(response, TestData.HashA, TestData.HashB, basename);
        Assert.IsTrue(legacy.IsSuccess, legacy.Error?.Code);
        var segment = legacy.Value!.Single();
        var built = CadSemanticContextBuilder.Build(
            [new CadSemanticContextInput(segment.SegmentId, segment.Entity.Type, segment.Entity.Handle,
                segment.Entity.Space, segment.Entity.Layout, segment.Entity.BlockPath,
                segment.Entity.Layer, segment.SourceText, segment.SourceTextHash,
                10, 20, 2.5, "ExtentsCenter", basename)],
            CadSemanticContextBuilder.PolicyVersionOneThree);
        Assert.IsTrue(built.IsSuccess, built.Error?.Code);
        var context = built.Value![segment.SegmentId];
        Assert.AreEqual(discipline, context.Discipline);
        Assert.AreEqual(resolution, context.DisciplineResolution);
        segmentNode["semanticContext"] = JsonSerializer.SerializeToNode(context, WebJson);

        var wrong = CadExtractionResultPolicy.Validate(response, TestData.HashA, TestData.HashB,
            token == "ELE" ? "SYNTHETIC-EST-001.dwg" : "SYNTHETIC-ELE-001.dwg");
        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_MANIFEST_BINDING_MISMATCH", wrong.Error?.Code);
        var right = CadExtractionResultPolicy.Validate(response, TestData.HashA, TestData.HashB, basename);
        Assert.IsTrue(right.IsSuccess, right.Error?.Code);
    }

    [TestMethod]
    public void VersionOneThreeRejectsOmittingAnUnambiguousManifestDiscipline()
    {
        var response = LoadFixture();
        var segmentNode = Segment(response);
        segmentNode["entity"]!["layer"] = "0";
        UpdateIdentity(segmentNode);
        var legacy = CadExtractionResultPolicy.Validate(response, TestData.HashA, TestData.HashB, "source.dwg");
        Assert.IsTrue(legacy.IsSuccess, legacy.Error?.Code);
        var segment = legacy.Value!.Single();
        var built = CadSemanticContextBuilder.Build(
            [new CadSemanticContextInput(segment.SegmentId, segment.Entity.Type, segment.Entity.Handle,
                segment.Entity.Space, segment.Entity.Layout, segment.Entity.BlockPath,
                segment.Entity.Layer, segment.SourceText, segment.SourceTextHash,
                10, 20, 2.5, "ExtentsCenter", "source.dwg")],
            CadSemanticContextBuilder.PolicyVersionOneThree);
        Assert.IsTrue(built.IsSuccess, built.Error?.Code);
        Assert.AreEqual("Unknown", built.Value![segment.SegmentId].Discipline);
        segmentNode["semanticContext"] = JsonSerializer.SerializeToNode(built.Value[segment.SegmentId], WebJson);

        var rejected = CadExtractionResultPolicy.Validate(response, TestData.HashA, TestData.HashB,
            "SYNTHETIC-EST-001.dwg");
        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_MANIFEST_BINDING_MISMATCH", rejected.Error?.Code);
    }

    private static WireEnvelope LoadFixture()
    {
        var path = Path.Combine(Root, "fixtures", "v1", "valid", "cad-extract-response.json");
        var result = EnvelopeCodec.Decode(File.ReadAllBytes(path));
        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        return result.Value!;
    }

    private static JsonObject Segment(WireEnvelope response) => response.Payload!["segments"]![0]!.AsObject();

    private static void UpdateIdentity(JsonObject segment)
    {
        var entity = segment["entity"]!.AsObject();
        var identity = CadSegmentIdentityV1.Create(
            TestData.HashA,
            TestData.HashB,
            new CadSegmentAddress(
                entity["type"]!.GetValue<string>(),
                entity["handle"]!.GetValue<string>(),
                entity["space"]!.GetValue<string>(),
                entity["layout"]?.GetValue<string>(),
                entity["layer"]!.GetValue<string>(),
                entity["subIndex"]!.GetValue<int>()),
            segment["sourceText"]!.GetValue<string>());
        Assert.IsTrue(identity.IsSuccess, identity.Error?.Code);
        segment["segmentId"] = identity.Value!.SegmentId;
        segment["sourceTextHash"] = identity.Value.SourceTextHash;
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
