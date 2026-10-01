using System.Globalization;
using System.Text.Json;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Transport.Core;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadInvariantDiffCompactPolicyTests
{
    private const string SourceHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CandidateHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private static readonly string[] RequestPayloadKeys =
        ["candidatePath", "expectedCandidateHash", "expectedSourceHash", "readOnly", "sourcePath"];
    private static readonly string[] TextChangeFields = ["textPayloadHash", "fingerprint"];
    private static readonly string[] AddedAndRemovedKinds = ["Added", "Removed"];

    [TestMethod]
    public async Task LargeUnchangedSnapshotsProduceAFrameBoundedCompleteResponse()
    {
        var rows = Enumerable.Range(1, 20_000)
            .Select(index => Row(index.ToString("X", CultureInfo.InvariantCulture), layer: new string('L', 240)))
            .ToList();
        var legacyShapeBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sourceEntities = rows,
            candidateEntities = rows
        }).Length;

        var result = CadInvariantDiffCompactPolicy.Create(SourceHash, CandidateHash, rows, rows);

        Assert.IsTrue(legacyShapeBytes > ContractV1.MaxFrameBytes, $"Legacy shape was only {legacyShapeBytes} bytes.");
        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(20_000, result.Value!.MatchedEntityCount);
        Assert.AreEqual(20_000, result.Value.UnchangedEntityCount);
        Assert.AreEqual(0, result.Value.Differences.Count);
        Assert.IsTrue(result.Value.DifferencesComplete);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(result.Value).Length < CadInvariantDiffContract.MaximumSerializedPayloadBytes);

        var envelope = new WireEnvelope
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = MessageTypes.InvariantDiffResponse,
            JobId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            IdempotencyKey = Hash('1'),
            SentAtUtc = DateTimeOffset.UtcNow,
            Status = OperationStatus.Succeeded,
            Payload = JsonSerializer.SerializeToNode(result.Value)!.AsObject()
        };
        var encoded = EnvelopeCodec.Encode(envelope);
        using var frame = new MemoryStream();
        await LengthPrefixedFrame.WriteAsync(frame, encoded, CancellationToken.None);
        frame.Position = 0;
        CollectionAssert.AreEqual(encoded, await LengthPrefixedFrame.ReadAsync(frame, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [TestMethod]
    public void CompleteResponseBindsCountsFingerprintsAndChangedRows()
    {
        var source = new[] { Row("1"), Row("2"), Row("3", textHash: Hash('c'), fingerprint: Hash('d')) };
        var candidate = new[] { Row("1"), Row("2"), Row("3", textHash: Hash('e'), fingerprint: Hash('f')) };

        var result = CadInvariantDiffCompactPolicy.Create(SourceHash, CandidateHash, source, candidate);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var value = result.Value!;
        Assert.AreEqual(CadInvariantDiffContract.ResponseSchema, value.Schema);
        Assert.AreEqual(3, value.SourceEntityCount);
        Assert.AreEqual(3, value.CandidateEntityCount);
        Assert.AreEqual(3, value.MatchedEntityCount);
        Assert.AreEqual(2, value.UnchangedEntityCount);
        Assert.AreEqual(0, value.AddedEntityCount);
        Assert.AreEqual(0, value.RemovedEntityCount);
        Assert.AreEqual(1, value.ChangedEntityCount);
        Assert.AreNotEqual(value.SourceFingerprint, value.CandidateFingerprint);
        CollectionAssert.AreEqual(TextChangeFields, value.Differences.Single().FieldsChanged);
        Assert.IsTrue(CadInvariantDiffCompactPolicy.Validate(value, SourceHash, CandidateHash).IsSuccess);
    }

    [TestMethod]
    public void RejectsTruncationInsteadOfReturningAnIncompleteDifferenceSet()
    {
        var candidate = Enumerable.Range(1, CadInvariantDiffContract.MaximumDifferences + 1)
            .Select(index => Row(index.ToString("X", CultureInfo.InvariantCulture)))
            .ToList();

        var result = CadInvariantDiffCompactPolicy.Create(SourceHash, CandidateHash, [], candidate);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("CAD_INVARIANT_DIFF_RESULT_TOO_LARGE", result.Error!.Code);
        Assert.IsFalse(result.Error.Retryable);
    }

    [TestMethod]
    public void RejectsTamperedCompletenessAndChangedFields()
    {
        var created = CadInvariantDiffCompactPolicy.Create(SourceHash, CandidateHash,
            [Row("1", textHash: Hash('c'), fingerprint: Hash('d'))],
            [Row("1", textHash: Hash('e'), fingerprint: Hash('f'))]).Value!;

        var incomplete = created with { DifferencesComplete = false };
        var alteredDifference = created.Differences[0] with { FieldsChanged = ["fingerprint"] };
        var altered = created with { Differences = [alteredDifference] };
        var alteredFingerprint = created with { SourceFingerprint = Hash('8') };

        Assert.AreEqual("CAD_INVARIANT_DIFF_RESPONSE_INVALID", CadInvariantDiffCompactPolicy.Validate(incomplete, SourceHash, CandidateHash).Error!.Code);
        Assert.AreEqual("CAD_INVARIANT_DIFF_RESPONSE_INVALID", CadInvariantDiffCompactPolicy.Validate(altered, SourceHash, CandidateHash).Error!.Code);
        Assert.AreEqual("CAD_INVARIANT_DIFF_RESPONSE_INVALID", CadInvariantDiffCompactPolicy.Validate(alteredFingerprint, SourceHash, CandidateHash).Error!.Code);
        Assert.AreEqual("CAD_INVARIANT_DIFF_RESPONSE_INVALID", CadInvariantDiffCompactPolicy.Validate(created, Hash('9'), CandidateHash).Error!.Code);
    }

    [TestMethod]
    public void AddedAndRemovedRowsRemainCompleteAndFailClosed()
    {
        var result = CadInvariantDiffCompactPolicy.Create(SourceHash, CandidateHash,
            [Row("1"), Row("2")],
            [Row("2"), Row("3")]);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var value = result.Value!;
        Assert.AreEqual(1, value.AddedEntityCount);
        Assert.AreEqual(1, value.RemovedEntityCount);
        Assert.AreEqual(1, value.MatchedEntityCount);
        Assert.AreEqual(1, value.UnchangedEntityCount);
        Assert.AreEqual(0, value.ChangedEntityCount);
        Assert.IsTrue(value.DifferencesComplete);
        CollectionAssert.AreEquivalent(AddedAndRemovedKinds, value.Differences.Select(row => row.ChangeKind).ToArray());
    }

    [TestMethod]
    public async Task WorkflowRequestUsesCanonicalEnvelopeIdempotency()
    {
        var payload = CadInvariantDiffCompactPolicy.Create(SourceHash, CandidateHash, [Row("1")], [Row("1")]).Value!;
        var gateway = new RecordingGateway(request => SuccessfulResponse(request, payload));
        var workflow = new CadInvariantDiffWorkflow(new Sessions(gateway), new Clock());

        var result = await workflow.CompareAsync(Guid.NewGuid(),
            "C:\\Input\\source.dwg", SourceHash, "C:\\Output\\candidate.dwg", CandidateHash, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.IsNotNull(gateway.Request);
        var canonical = EnvelopeIdempotency.Fingerprint(gateway.Request);
        Assert.IsTrue(canonical.IsSuccess, canonical.Error?.Code);
        Assert.AreEqual(canonical.Value, gateway.Request.IdempotencyKey);
    }

    [TestMethod]
    public async Task WorkflowRequestUsesOnlyStrictCamelCaseContractKeys()
    {
        var payload = CadInvariantDiffCompactPolicy.Create(SourceHash, CandidateHash, [Row("1")], [Row("1")]).Value!;
        var gateway = new RecordingGateway(request => SuccessfulResponse(request, payload));
        var workflow = new CadInvariantDiffWorkflow(new Sessions(gateway), new Clock());

        var result = await workflow.CompareAsync(Guid.NewGuid(),
            "C:\\Input\\source.dwg", SourceHash, "C:\\Output\\candidate.dwg", CandidateHash, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.IsNotNull(gateway.Request?.Payload);
        CollectionAssert.AreEquivalent(RequestPayloadKeys, gateway.Request.Payload.Select(property => property.Key).ToArray());
        var decoded = gateway.Request.Payload.Deserialize<CadInvariantDiffRequestPayload>(StrictJson);
        Assert.IsNotNull(decoded);
        Assert.AreEqual("C:\\Input\\source.dwg", decoded.SourcePath);
        Assert.AreEqual(SourceHash, decoded.ExpectedSourceHash);
        Assert.AreEqual("C:\\Output\\candidate.dwg", decoded.CandidatePath);
        Assert.AreEqual(CandidateHash, decoded.ExpectedCandidateHash);
        Assert.IsTrue(decoded.ReadOnly);
        Assert.AreEqual(EnvelopeIdempotency.Fingerprint(gateway.Request).Value, gateway.Request.IdempotencyKey);
    }

    [TestMethod]
    public async Task WorkflowPreservesTypedFailedResponseInsteadOfDecodingNullPayload()
    {
        var remoteError = new ContractError("IDEMPOTENCY_KEY_MISMATCH", ErrorCategory.Contract,
            "The key must equal the canonical request fingerprint.", false);
        var gateway = new RecordingGateway(request => new WireEnvelope
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = MessageTypes.InvariantDiffResponse,
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            SentAtUtc = DateTimeOffset.Parse("2026-09-02T23:00:00Z", CultureInfo.InvariantCulture),
            Status = OperationStatus.Failed,
            Error = remoteError
        });
        var workflow = new CadInvariantDiffWorkflow(new Sessions(gateway), new Clock());

        var result = await workflow.CompareAsync(Guid.NewGuid(),
            "C:\\Input\\source.dwg", SourceHash, "C:\\Output\\candidate.dwg", CandidateHash, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreSame(remoteError, result.Error);
        Assert.AreEqual("IDEMPOTENCY_KEY_MISMATCH", result.Error!.Code);
        Assert.IsFalse(result.Error.Retryable);
    }

    private static CadInvariantDiffEntity Row(
        string handle,
        string layer = "0",
        string? textHash = null,
        string? fingerprint = null) => new()
        {
            OwnerHandle = "B1",
            EntityHandle = handle,
            DxfType = "TEXT",
            RuntimeClass = "Autodesk.AutoCAD.DatabaseServices.DBText",
            IsTextEntity = true,
            Layer = layer,
            ColorIndex = 7,
            LinetypeHandle = "10",
            Lineweight = 0,
            Extents = new CadInvariantExtents { Minimum = "0,0,0", Maximum = "1,1,0" },
            TextPayloadHash = textHash ?? Hash('c'),
            Fingerprint = fingerprint ?? Hash('d')
        };

    private static string Hash(char value) => "sha256:" + new string(value, 64);

    private static WireEnvelope SuccessfulResponse(WireEnvelope request, CadInvariantDiffResponsePayload payload) => new()
    {
        SchemaVersion = ContractV1.SchemaVersion,
        MessageType = MessageTypes.InvariantDiffResponse,
        JobId = request.JobId,
        CorrelationId = request.CorrelationId,
        IdempotencyKey = request.IdempotencyKey,
        SentAtUtc = DateTimeOffset.Parse("2026-09-02T23:00:00Z", CultureInfo.InvariantCulture),
        Status = OperationStatus.Succeeded,
        Payload = JsonSerializer.SerializeToNode(payload, Json)!.AsObject()
    };

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-09-02T22:59:00Z", CultureInfo.InvariantCulture);
    }

    private sealed class Sessions(ICadGateway gateway) : ICadGatewaySessionFactory
    {
        public Task<Result<ICadGatewayLease>> OpenAsync(CadSessionPurpose purpose, CancellationToken cancellationToken) =>
            Task.FromResult(Results.Success<ICadGatewayLease>(new Lease(gateway)));
    }

    private sealed class Lease(ICadGateway gateway) : ICadGatewayLease
    {
        public ICadGateway Gateway { get; } = gateway;

        public Task<Result<bool>> CloseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Results.Success(true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingGateway(Func<WireEnvelope, WireEnvelope> respond) : ICadGateway
    {
        public WireEnvelope? Request { get; private set; }

        public Task<Result<CadCapabilities>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Results.Success(new CadCapabilities(
                ContractV1.SchemaVersion, "Synthetic AutoCAD", [CadOperations.InvariantDiff])));

        public Task<Result<WireEnvelope>> ExchangeAsync(WireEnvelope request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(Results.Success(respond(request)));
        }
    }
}
