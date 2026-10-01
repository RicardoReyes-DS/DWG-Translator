using System.Text;
using System.Text.Json.Nodes;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class ContractTests
{
    [TestMethod]
    public void CadTechnicalFailureMetadataUsesClosedAllowlists()
    {
        var valid = TestData.Request() with
        {
            Status = OperationStatus.Failed,
            Payload = null,
            Error = new ContractError("CAD_WRITE_FAILED", ErrorCategory.Environment, "Safe.", false,
                TechnicalStage: CadWriteTechnicalStages.NormalizedBaselineOpen,
                NativeErrorStatus: "InvalidOpenState")
        };
        Assert.IsTrue(EnvelopeCodec.Validate(valid).IsSuccess);

        var unknownStage = valid with { Error = valid.Error! with { TechnicalStage = "visual-strict-v2.unknown" } };
        var unknownStatus = valid with { Error = valid.Error! with { NativeErrorStatus = "RegexValidButUnknown" } };
        Assert.AreEqual("IPC_ERROR_INVALID", EnvelopeCodec.Validate(unknownStage).Error!.Code);
        Assert.AreEqual("IPC_ERROR_INVALID", EnvelopeCodec.Validate(unknownStatus).Error!.Code);
    }

    [TestMethod]
    public void ExplicitNullEnvelopeIdentifiersFailAsContractErrors()
    {
        var nullMessageType = JsonNode.Parse(EnvelopeCodec.Encode(TestData.Request()))!.AsObject();
        nullMessageType["messageType"] = null;
        Assert.AreEqual("IPC_OPERATION_UNSUPPORTED", EnvelopeCodec.Decode(System.Text.Encoding.UTF8.GetBytes(nullMessageType.ToJsonString())).Error!.Code);

        var nullIdempotencyKey = JsonNode.Parse(EnvelopeCodec.Encode(TestData.Request()))!.AsObject();
        nullIdempotencyKey["idempotencyKey"] = null;
        Assert.AreEqual("IPC_IDEMPOTENCY_KEY_INVALID", EnvelopeCodec.Decode(System.Text.Encoding.UTF8.GetBytes(nullIdempotencyKey.ToJsonString())).Error!.Code);
    }

    [TestMethod]
    public void ValidRequestRoundTripsWithNormativeFields()
    {
        var request = TestData.Request(new JsonObject { ["protocolVersion"] = ContractV1.SchemaVersion });
        var decoded = EnvelopeCodec.Decode(EnvelopeCodec.Encode(request));

        Assert.IsTrue(decoded.IsSuccess, decoded.Error?.Code);
        Assert.AreEqual(request.JobId, decoded.Value!.JobId);
        Assert.AreEqual(request.CorrelationId, decoded.Value.CorrelationId);
        Assert.AreEqual(TestData.HashA, decoded.Value.IdempotencyKey);
        Assert.AreEqual(TimeSpan.Zero, decoded.Value.SentAtUtc.Offset);
    }

    [TestMethod]
    public void UnknownOrMissingCommonFieldsFailClosed()
    {
        var valid = JsonNode.Parse(EnvelopeCodec.Encode(TestData.Request()))!.AsObject();
        var unknown = valid.DeepClone().AsObject();
        unknown["extra"] = true;
        var missingJob = valid.DeepClone().AsObject();
        missingJob.Remove("jobId");

        Assert.AreEqual("IPC_JSON_INVALID", EnvelopeCodec.Decode(Encoding.UTF8.GetBytes(unknown.ToJsonString())).Error!.Code);
        Assert.AreEqual("IPC_JSON_INVALID", EnvelopeCodec.Decode(Encoding.UTF8.GetBytes(missingJob.ToJsonString())).Error!.Code);
    }

    [TestMethod]
    public void InvalidVersionHashTimestampAndCorrelationAreRejected()
    {
        Assert.AreEqual("IPC_SCHEMA_UNSUPPORTED", EnvelopeCodec.Validate(TestData.Request() with { SchemaVersion = "2.0.0" }).Error!.Code);
        Assert.AreEqual("IPC_IDEMPOTENCY_KEY_INVALID", EnvelopeCodec.Validate(TestData.Request() with { IdempotencyKey = "not-a-hash" }).Error!.Code);
        Assert.AreEqual("IPC_TIMESTAMP_INVALID", EnvelopeCodec.Validate(TestData.Request() with { SentAtUtc = DateTimeOffset.Parse("2026-08-11T20:00:00-06:00", System.Globalization.CultureInfo.InvariantCulture) }).Error!.Code);
        Assert.AreEqual("IPC_CORRELATION_MISSING", EnvelopeCodec.Validate(TestData.Request() with { CorrelationId = Guid.Empty }).Error!.Code);
        Assert.AreEqual("IPC_OPERATION_UNSUPPORTED", EnvelopeCodec.Validate(TestData.Request() with { MessageType = "cad.job.unknown.request" }).Error!.Code);
    }

    [TestMethod]
    public void RequestAndResponsePayloadErrorShapesAreExclusive()
    {
        var requestWithError = TestData.Request() with
        {
            Error = new ContractError("TEST", ErrorCategory.Contract, "invalid", false)
        };
        var failedWithPayload = TestData.Request() with
        {
            Status = OperationStatus.Failed,
            Error = new ContractError("TEST", ErrorCategory.Contract, "failed", false)
        };
        var successWithError = TestData.Request() with
        {
            Status = OperationStatus.Succeeded,
            Error = new ContractError("TEST", ErrorCategory.Contract, "failed", false)
        };

        Assert.AreEqual("IPC_REQUEST_SHAPE_INVALID", EnvelopeCodec.Validate(requestWithError).Error!.Code);
        Assert.AreEqual("IPC_ERROR_SHAPE_INVALID", EnvelopeCodec.Validate(failedWithPayload).Error!.Code);
        Assert.AreEqual("IPC_RESPONSE_SHAPE_INVALID", EnvelopeCodec.Validate(successWithError).Error!.Code);

        var validFailure = TestData.Request() with
        {
            Status = OperationStatus.Failed,
            Payload = null,
            Error = new ContractError("TEST", ErrorCategory.Contract, "failed", false)
        };
        Assert.IsTrue(EnvelopeCodec.Validate(validFailure).IsSuccess);
    }

    [TestMethod]
    public void NumericEnumsAndIncompleteTypedErrorsAreRejected()
    {
        var validFailure = TestData.Request() with
        {
            Status = OperationStatus.Failed,
            Payload = null,
            Error = new ContractError("TEST_ERROR", ErrorCategory.Contract, "failed", false)
        };
        var numericStatus = JsonNode.Parse(EnvelopeCodec.Encode(validFailure))!.AsObject();
        numericStatus["status"] = 1;
        Assert.AreEqual("IPC_JSON_INVALID", EnvelopeCodec.Decode(Encoding.UTF8.GetBytes(numericStatus.ToJsonString())).Error!.Code);

        var incompleteError = JsonNode.Parse(EnvelopeCodec.Encode(validFailure))!.AsObject();
        incompleteError["error"]!.AsObject().Remove("category");
        Assert.AreEqual("IPC_JSON_INVALID", EnvelopeCodec.Decode(Encoding.UTF8.GetBytes(incompleteError.ToJsonString())).Error!.Code);
    }

    [TestMethod]
    public void InvariantDiagnosticsAreBoundedWithoutChangingFailureSemantics()
    {
        var diagnostics = new CadInvariantDiagnostics
        {
            Schema = CadInvariantDiagnosticsLimits.Schema,
            AddedCount = 0,
            RemovedCount = 0,
            ChangedCount = CadInvariantDiagnosticsLimits.MaximumRows + 1,
            Truncated = true,
            Rows = Enumerable.Range(0, CadInvariantDiagnosticsLimits.MaximumRows + 1).Select(index => new CadInvariantDifference
            {
                InvariantKey = $"10|{index:X}",
                ChangeKind = "Changed",
                FieldsChanged = ["extents"],
                Before = null,
                After = null
            }).ToList()
        };
        var failure = TestData.Request() with
        {
            Status = OperationStatus.Failed,
            Payload = null,
            Error = new ContractError("GEOMETRY_INVARIANTS_CHANGED", ErrorCategory.Integrity, "Strict failure.", false,
                InvariantDiagnostics: diagnostics)
        };

        Assert.AreEqual("IPC_ERROR_INVALID", EnvelopeCodec.Validate(failure).Error!.Code);
    }

}
