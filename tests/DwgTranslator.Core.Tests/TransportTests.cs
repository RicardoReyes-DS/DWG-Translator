using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Contracts;
using DwgTranslator.Transport.Core;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class TransportTests
{
    [TestMethod]
    public void CanonicalJsonOrdersObjectsNormalizesNumbersAndPreservesArrays()
    {
        using var first = JsonDocument.Parse("{\"z\":[2,1],\"n\":1.0,\"a\":true}");
        using var second = JsonDocument.Parse("{\"a\":true,\"n\":1e0,\"z\":[2,1]}");
        using var differentArray = JsonDocument.Parse("{\"a\":true,\"n\":1,\"z\":[1,2]}");

        Assert.AreEqual("{\"a\":true,\"n\":1,\"z\":[2,1]}", System.Text.Encoding.UTF8.GetString(CanonicalJsonV1.Serialize(first.RootElement)));
        Assert.AreEqual(CanonicalJsonV1.Fingerprint(first.RootElement), CanonicalJsonV1.Fingerprint(second.RootElement));
        Assert.AreNotEqual(CanonicalJsonV1.Fingerprint(first.RootElement), CanonicalJsonV1.Fingerprint(differentArray.RootElement));
    }

    [TestMethod]
    public void CanonicalJsonRejectsDuplicatePropertiesAndOutOfRangeNumbers()
    {
        using var duplicate = JsonDocument.Parse("{\"a\":1,\"a\":2}");
        using var huge = JsonDocument.Parse("{\"n\":1e1000}");
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalJsonV1.Serialize(duplicate.RootElement));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalJsonV1.Serialize(huge.RootElement));
    }

    [TestMethod]
    public void IdempotencyRegistryStoresReplaysRejectsConflictAndCopiesBuffers()
    {
        var registry = new IdempotencyRegistry();
        var original = new byte[] { 1, 2 };
        var stored = registry.Register(TestData.HashA, TestData.HashA, original);
        original[0] = 9;
        stored.Value!.Response[1] = 9;

        var replay = registry.Register(TestData.HashA, TestData.HashA, new byte[] { 7 });
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, replay.Value!.Response);
        Assert.AreEqual(IdempotencyDisposition.Replayed, replay.Value.Disposition);
        Assert.AreEqual("IDEMPOTENCY_CONFLICT", registry.Register(TestData.HashA, TestData.HashB, new byte[] { 3 }).Error!.Code);
    }

    [TestMethod]
    public async Task IdempotencyRegistrationIsAtomicUnderContention()
    {
        var registry = new IdempotencyRegistry();
        var results = await Task.WhenAll(
            Task.Run(() => registry.Register(TestData.HashA, TestData.HashA, new byte[] { 1 })),
            Task.Run(() => registry.Register(TestData.HashA, TestData.HashB, new byte[] { 2 })));

        Assert.AreEqual(1, results.Count(result => result.IsSuccess));
        Assert.AreEqual(1, results.Count(result => result.Error?.Code is "IDEMPOTENCY_CONFLICT" or "IDEMPOTENCY_KEY_MISMATCH"));
    }

    [TestMethod]
    public void IdempotencyMaterialIsStableAndChangesWithRelevantParameters()
    {
        var first = IdempotencyMaterial.Fingerprint(
            MessageTypes.ExtractRequest,
            ContractV1.SchemaVersion,
            new Dictionary<string, string> { ["source"] = TestData.HashA, ["configuration"] = TestData.HashB },
            new JsonObject { ["readOnly"] = true, ["attempt"] = 1.0 });
        var reordered = IdempotencyMaterial.Fingerprint(
            MessageTypes.ExtractRequest,
            ContractV1.SchemaVersion,
            new Dictionary<string, string> { ["configuration"] = TestData.HashB, ["source"] = TestData.HashA },
            new JsonObject { ["attempt"] = 1, ["readOnly"] = true });
        var changed = IdempotencyMaterial.Fingerprint(
            MessageTypes.ExtractRequest,
            ContractV1.SchemaVersion,
            new Dictionary<string, string> { ["configuration"] = TestData.HashB, ["source"] = TestData.HashA },
            new JsonObject { ["attempt"] = 2, ["readOnly"] = true });

        Assert.IsTrue(first.IsSuccess, first.Error?.Code);
        Assert.AreEqual(first.Value, reordered.Value);
        Assert.AreNotEqual(first.Value, changed.Value);

        var noInputHashes = IdempotencyMaterial.Fingerprint(
            MessageTypes.CapabilitiesRequest,
            ContractV1.SchemaVersion,
            new Dictionary<string, string>(),
            new JsonObject { ["protocolVersion"] = "1.0.0" });
        Assert.IsTrue(noInputHashes.IsSuccess, noInputHashes.Error?.Code);
    }

    [TestMethod]
    public void IdempotencyKeyMustEqualCanonicalFingerprint()
    {
        var registry = new IdempotencyRegistry();
        Assert.AreEqual("IDEMPOTENCY_KEY_MISMATCH", registry.Register(TestData.HashA, TestData.HashB, new byte[] { 1 }).Error!.Code);
    }

    [TestMethod]
    public void IdempotencyMaterialRejectsUnsupportedOperationAndNumericDomain()
    {
        var unknown = IdempotencyMaterial.Fingerprint(
            "cad.job.unknown.request", ContractV1.SchemaVersion,
            new Dictionary<string, string> { ["source"] = TestData.HashA }, new JsonObject());
        Assert.AreEqual("IDEMPOTENCY_MATERIAL_INVALID", unknown.Error!.Code);

        var parameters = JsonNode.Parse("{\"value\":1e1000}")!.AsObject();
        var outOfRange = IdempotencyMaterial.Fingerprint(
            MessageTypes.ExtractRequest, ContractV1.SchemaVersion,
            new Dictionary<string, string> { ["source"] = TestData.HashA }, parameters);
        Assert.AreEqual("IDEMPOTENCY_MATERIAL_INVALID", outOfRange.Error!.Code);
    }

    [TestMethod]
    public void EnvelopeReplayUsesCurrentCorrelationAndDefensivePayloadCopy()
    {
        var initial = TestData.Request(new JsonObject { ["readOnly"] = true }) with { MessageType = MessageTypes.ExtractRequest };
        var fingerprint = EnvelopeIdempotency.Fingerprint(initial).Value!;
        initial = initial with { IdempotencyKey = fingerprint };
        var response = new WireEnvelope
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = MessageTypes.ExtractResponse,
            JobId = initial.JobId,
            CorrelationId = initial.CorrelationId,
            IdempotencyKey = initial.IdempotencyKey,
            SentAtUtc = initial.SentAtUtc.AddSeconds(1),
            Status = OperationStatus.Succeeded,
            Payload = new JsonObject { ["count"] = 1 }
        };
        var registry = new EnvelopeReplayRegistry();
        Assert.IsTrue(registry.Store(initial, fingerprint, response).IsSuccess);
        response.Payload!["count"] = 99;

        var retry = initial with { CorrelationId = Guid.NewGuid(), SentAtUtc = initial.SentAtUtc.AddSeconds(5) };
        var replay = registry.Replay(retry, EnvelopeIdempotency.Fingerprint(retry).Value!, retry.SentAtUtc.AddSeconds(1)).Value!;
        Assert.AreEqual(retry.CorrelationId, replay!.CorrelationId);
        Assert.AreEqual(1, replay.Payload!["count"]!.GetValue<int>());
    }

    [TestMethod]
    public void SessionBindingRequiresCorrectInitialNonceAndRejectsReplay()
    {
        using var binding = SessionBinding.Create();
        var nonce = binding.ExportForTrustedLauncher();
        var valid = TestData.Request(new JsonObject { ["sessionNonce"] = nonce });

        Assert.IsTrue(binding.ValidateInitialHandshake(valid).IsSuccess);
        Assert.AreEqual("IPC_HANDSHAKE_REPLAY", binding.ValidateInitialHandshake(valid).Error!.Code);
    }

    [TestMethod]
    public void SessionBindingRejectsWrongOperationMissingAndInvalidNonce()
    {
        using var wrongOperation = SessionBinding.Create();
        Assert.AreEqual("IPC_HANDSHAKE_REQUIRED", wrongOperation.ValidateInitialHandshake(TestData.Request() with { MessageType = MessageTypes.InspectRequest }).Error!.Code);

        using var missing = SessionBinding.Create();
        Assert.AreEqual("IPC_SESSION_NONCE_MISSING", missing.ValidateInitialHandshake(TestData.Request()).Error!.Code);

        using var invalid = SessionBinding.Create();
        Assert.AreEqual("IPC_SESSION_NONCE_INVALID", invalid.ValidateInitialHandshake(TestData.Request(new JsonObject { ["sessionNonce"] = "bad" })).Error!.Code);

        using var invalidEnvelope = SessionBinding.Create();
        var nonce = invalidEnvelope.ExportForTrustedLauncher();
        Assert.AreEqual("IPC_HANDSHAKE_ENVELOPE_INVALID", invalidEnvelope.ValidateInitialHandshake(TestData.Request(new JsonObject { ["sessionNonce"] = nonce }) with { JobId = Guid.Empty }).Error!.Code);
    }

    [TestMethod]
    public void SessionBindingImportsOnlyExactTrustedLauncherNonce()
    {
        using var source = SessionBinding.Create();
        var encoded = source.ExportForTrustedLauncher();
        var imported = SessionBinding.ImportFromTrustedLauncher(encoded);
        Assert.IsTrue(imported.IsSuccess, imported.Error?.Code);
        using var binding = imported.Value!;
        Assert.IsTrue(binding.ValidateInitialHandshake(TestData.Request(new JsonObject { ["sessionNonce"] = encoded })).IsSuccess);
        Assert.AreEqual("IPC_SESSION_NONCE_MISSING", SessionBinding.ImportFromTrustedLauncher(null).Error!.Code);
        Assert.AreEqual("IPC_SESSION_NONCE_INVALID", SessionBinding.ImportFromTrustedLauncher("bad").Error!.Code);
        var shortNonce = Convert.ToBase64String(new byte[31]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual("IPC_SESSION_NONCE_INVALID", SessionBinding.ImportFromTrustedLauncher(shortNonce).Error!.Code);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(16_777_217)]
    public async Task InvalidFrameLengthsFailClosed(int length)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        await using var stream = new MemoryStream(prefix);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LengthPrefixedFrame.ReadAsync(stream, TimeSpan.FromSeconds(1), default));
        Assert.AreEqual("IPC_FRAME_LENGTH_INVALID", error.Message);
    }

    [TestMethod]
    public async Task FrameRoundTripsAndRejectsTruncationAndInvalidUtf8()
    {
        await using var roundTrip = new MemoryStream();
        await LengthPrefixedFrame.WriteAsync(roundTrip, "{}"u8.ToArray(), default);
        roundTrip.Position = 0;
        CollectionAssert.AreEqual("{}"u8.ToArray(), await LengthPrefixedFrame.ReadAsync(roundTrip, TimeSpan.FromSeconds(1), default));

        await using var truncated = Frame(new byte[] { 1, 2 }, 3);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => LengthPrefixedFrame.ReadAsync(truncated, TimeSpan.FromSeconds(1), default));
        await using var invalidUtf8 = Frame(new byte[] { 0xff }, 1);
        await Assert.ThrowsExactlyAsync<System.Text.DecoderFallbackException>(() => LengthPrefixedFrame.ReadAsync(invalidUtf8, TimeSpan.FromSeconds(1), default));
    }

    [TestMethod]
    public async Task FrameCapacitySupportsBoundedLargeCadValidationEvidence()
    {
        var payload = new byte[1_048_577];
        Array.Fill(payload, (byte)'v');
        await using var stream = new MemoryStream();
        await LengthPrefixedFrame.WriteAsync(stream, payload, default);
        stream.Position = 0;
        CollectionAssert.AreEqual(payload, await LengthPrefixedFrame.ReadAsync(stream, TimeSpan.FromSeconds(5), default));
    }

    private static MemoryStream Frame(byte[] content, int declaredLength)
    {
        var bytes = new byte[4 + content.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, declaredLength);
        content.CopyTo(bytes, 4);
        return new MemoryStream(bytes);
    }
}
