using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadWriteWorkflowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-13T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public void FactoryCreatesStrictCandidateRequestFromCompleteReview()
    {
        var fixture = Fixture();
        var result = CadWriteRequestFactory.Create(fixture.JobId, "C:\\Input\\source.dwg", "C:\\Output\\translated.dwg", TestData.HashA, fixture.Segments, fixture.Review, Now);

        Assert.IsTrue(result.IsSuccess);
        var payload = JsonSerializer.Deserialize<CadWriteRequestPayload>(result.Value!.Payload, Json)!;
        Assert.AreEqual($"C:\\Output\\.translated.candidate-{fixture.JobId:N}.dwg", payload.CandidatePath);
        Assert.AreEqual(2, payload.ApprovalCoverage.Selected);
        Assert.AreEqual(1, payload.ApprovalCoverage.Approved);
        Assert.AreEqual(1, payload.ApprovalCoverage.Excluded);
        Assert.AreEqual(1, payload.Mappings.Count);
        Assert.AreEqual("A1", payload.Mappings[0].Handle);
        Assert.IsTrue(ContractPatterns.Sha256().IsMatch(payload.Mappings[0].ApprovedFinalTextHash));
        Assert.IsTrue(WriteSafetyPolicy.Validate(result.Value).IsSuccess);
        Assert.AreEqual(result.Value.IdempotencyKey, DwgTranslator.Transport.Core.EnvelopeIdempotency.Fingerprint(result.Value).Value);
    }

    [TestMethod]
    public void FactoryRejectsIncompleteCoverageAndTokenLoss()
    {
        var fixture = Fixture();
        var incomplete = fixture.Review with { Rows = [fixture.Review.Rows[0], fixture.Review.Rows[1] with { State = SegmentState.Proposed, ExclusionReason = null }] };
        Assert.AreEqual("APPROVAL_COVERAGE_INVALID", CadWriteRequestFactory.Create(fixture.JobId, "C:\\Input\\source.dwg", "C:\\Output\\translated.dwg", TestData.HashA, fixture.Segments, incomplete, Now).Error!.Code);

        var tokenLoss = fixture.Review with { Rows = [fixture.Review.Rows[0] with { FinalText = "BOMBA" }, fixture.Review.Rows[1]] };
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", CadWriteRequestFactory.Create(fixture.JobId, "C:\\Input\\source.dwg", "C:\\Output\\translated.dwg", TestData.HashA, fixture.Segments, tokenLoss, Now).Error!.Code);
    }

    [TestMethod]
    public void FactoryNormalizesLegacyWhitespaceOmissionAndRejectsVisibleInjection()
    {
        var jobId = Guid.NewGuid();
        const string source = "TITLE{\\H0.7x;  \t }";
        const string normalized = "TITULO{\\H0.7x;  \t }";
        var segment = Segment('c', source, "C3");
        segment = segment with { Entity = segment.Entity with { Type = "MTEXT" } };
        var omitted = new TranslationReviewSnapshot(
            jobId,
            1,
            "es-MX",
            "translate-cad-text/1.0",
            1,
            1,
            Now,
            [new ReviewRowSnapshot(
                segment.SegmentId,
                source,
                normalized,
                "TITULO{\\H0.7x;}",
                SegmentState.Approved,
                null,
                null)]);

        var result = CadWriteRequestFactory.Create(
            jobId,
            "C:\\Input\\source.dwg",
            "C:\\Output\\translated.dwg",
            TestData.HashA,
            [segment],
            omitted,
            Now);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var mapping = JsonSerializer.Deserialize<CadWriteRequestPayload>(result.Value!.Payload, Json)!.Mappings.Single();
        Assert.AreEqual(normalized, mapping.ApprovedFinalText);
        Assert.AreEqual(
            "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant(),
            mapping.ApprovedFinalTextHash);

        var injected = omitted with
        {
            Rows = [omitted.Rows.Single() with { FinalText = "TITULO{\\H0.7x;VISIBLE}" }]
        };
        var rejected = CadWriteRequestFactory.Create(
            jobId,
            "C:\\Input\\source.dwg",
            "C:\\Output\\translated.dwg",
            TestData.HashA,
            [segment],
            injected,
            Now);
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", rejected.Error!.Code);
    }

    [TestMethod]
    public void ResultPolicyAcceptsOnlyExactValidatedAndPromotedOutput()
    {
        var request = Request();
        var response = Response(request);
        Assert.IsTrue(CadWriteResultPolicy.Validate(request, response).IsSuccess);

        var sourceChanged = Response(request);
        sourceChanged.Payload!["sourceHashAfter"] = TestData.HashB;
        Assert.AreEqual("WRITE_HASH_INTEGRITY_FAILED", CadWriteResultPolicy.Validate(request, sourceChanged).Error!.Code);

        var invalidGeometry = Response(request);
        invalidGeometry.Payload!["validation"]!["geometryInvariantsValid"] = false;
        Assert.AreEqual("WRITE_VALIDATION_FAILED", CadWriteResultPolicy.Validate(request, invalidGeometry).Error!.Code);

        var wrongDestination = Response(request);
        wrongDestination.Payload!["promotion"]!["finalPath"] = "C:\\Output\\other.dwg";
        Assert.AreEqual("WRITE_PROMOTION_INVALID", CadWriteResultPolicy.Validate(request, wrongDestination).Error!.Code);

        var wrongText = Response(request);
        wrongText.Payload!["applied"]![0]!["postWriteTextHash"] = TestData.HashB;
        Assert.AreEqual("WRITE_APPLIED_SET_MISMATCH", CadWriteResultPolicy.Validate(request, wrongText).Error!.Code);

        var missingVisualEvidence = Response(request);
        missingVisualEvidence.Payload!["applied"]![0]!.AsObject().Remove("visualEvidence");
        Assert.AreEqual("WRITE_VISUAL_EVIDENCE_MISSING", CadWriteResultPolicy.Validate(request, missingVisualEvidence).Error!.Code);

        var movedText = Response(request);
        movedText.Payload!["applied"]![0]!["visualEvidence"]!["afterProperties"]!["position"] = "11,20,0";
        Assert.AreEqual("WRITE_VISUAL_INVARIANTS_CHANGED", CadWriteResultPolicy.Validate(request, movedText).Error!.Code);
    }

    [TestMethod]
    public void ResultPolicyPreservesAValidAdapterFailureInsteadOfMisclassifyingIt()
    {
        var request = Request();
        var adapterError = new ContractError("CANDIDATE_VALIDATION_FAILED", ErrorCategory.Integrity, "Candidate validation failed.", false);
        var response = new WireEnvelope
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = MessageTypes.WriteResponse,
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            SentAtUtc = Now,
            Status = OperationStatus.Failed,
            Error = adapterError
        };

        var result = CadWriteResultPolicy.Validate(request, response);

        Assert.AreEqual("CANDIDATE_VALIDATION_FAILED", result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Integrity, result.Error.Category);
    }

    [TestMethod]
    public void ResultPolicyKeepsStrictFailureForOrdinaryAndDerivedDiagnosticEvidence()
    {
        var request = Request();
        foreach (var derived in new[] { false, true })
        {
            var diagnosticRow = new CadInvariantDiagnosticRow
            {
                EntityHandle = "A1",
                OwnerHandle = "10",
                DxfType = "LINE",
                RuntimeClass = "Line",
                OwnerBlockName = derived ? "*D1" : "ORDINARY",
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
                Extents = new CadInvariantExtents { Minimum = "0,0,0", Maximum = "1,1,1" },
                InvariantRowFingerprint = "sha256:" + new string('a', 64)
            };
            var diagnostics = new CadInvariantDiagnostics
            {
                Schema = CadInvariantDiagnosticsPolicy.Schema,
                AddedCount = 0,
                RemovedCount = 0,
                ChangedCount = 1,
                Truncated = false,
                Rows = [new CadInvariantDifference
                {
                    InvariantKey = "10|A1", ChangeKind = "Changed", FieldsChanged = ["extents"],
                    Before = diagnosticRow,
                    After = diagnosticRow with { Extents = new CadInvariantExtents { Minimum = "0,0,0", Maximum = "2,1,1" } }
                }]
            };
            var response = new WireEnvelope
            {
                SchemaVersion = ContractV1.SchemaVersion,
                MessageType = MessageTypes.WriteResponse,
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                SentAtUtc = Now,
                Status = OperationStatus.Failed,
                Error = new ContractError("GEOMETRY_INVARIANTS_CHANGED", ErrorCategory.Integrity, "Strict failure.", false,
                    InvariantDiagnostics: diagnostics)
            };

            var result = CadWriteResultPolicy.Validate(request, response);

            Assert.AreEqual("GEOMETRY_INVARIANTS_CHANGED", result.Error!.Code);
            Assert.AreSame(diagnostics, result.Error.InvariantDiagnostics);
        }
    }

    [TestMethod]
    public async Task WorkflowUsesCadBoundaryAndReturnsValidatedResult()
    {
        var fixture = Fixture();
        var gateway = new SuccessfulCadGateway();
        var workflow = new CadWriteWorkflow(gateway, new FixedClock());

        var result = await workflow.WriteAsync(fixture.JobId, "C:\\Input\\source.dwg", "C:\\Output\\translated.dwg", TestData.HashA, fixture.Segments, fixture.Review, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, gateway.ExchangeCount);
        Assert.AreEqual(TestData.HashA, result.Value!.SourceHashAfter);
        Assert.IsTrue(result.Value.Promotion.Performed);
    }

    [TestMethod]
    public async Task WorkflowPropagatesBoundedSourceDeadlineToTheWriteAndValidationSession()
    {
        var fixture = Fixture();
        var sessions = new DeadlineCapturingSessions();
        var workflow = new CadWriteWorkflow(sessions, new FixedClock(), _ => TimeSpan.FromSeconds(1850));

        var result = await workflow.WriteAsync(fixture.JobId, "C:\\Input\\large.dwg", "C:\\Output\\translated.dwg", TestData.HashA,
            fixture.Segments, fixture.Review, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(CadSessionPurpose.Write, sessions.Purpose);
        Assert.AreEqual(TimeSpan.FromSeconds(1850), sessions.ExchangeTimeout);
        Assert.AreEqual(1, sessions.Gateway.ExchangeCount, "Write and validation must remain one bounded CAD exchange.");
    }

    [TestMethod]
    public async Task WorkflowFailsClosedWhenTheSourceDeadlineCannotBeDerived()
    {
        var fixture = Fixture();
        var sessions = new DeadlineCapturingSessions();
        var workflow = new CadWriteWorkflow(sessions, new FixedClock(), _ => TimeSpan.Zero);

        var result = await workflow.WriteAsync(fixture.JobId, "C:\\Input\\large.dwg", "C:\\Output\\translated.dwg", TestData.HashA,
            fixture.Segments, fixture.Review, CancellationToken.None);

        Assert.AreEqual("CAD_SESSION_TIMEOUT_POLICY_INVALID", result.Error!.Code);
        Assert.IsNull(sessions.ExchangeTimeout);
    }

    [TestMethod]
    public void SafetyPolicyRejectsMalformedMappingAndValidationPolicy()
    {
        var invalidHandle = Request();
        invalidHandle.Payload!["mappings"]![0]!["handle"] = "a1";
        Assert.AreEqual("WRITE_HANDLE_INVALID", WriteSafetyPolicy.Validate(invalidHandle).Error!.Code);

        var invalidHash = Request();
        invalidHash.Payload!["mappings"]![0]!["approvedFinalTextHash"] = "not-a-hash";
        Assert.AreEqual("WRITE_MAPPING_HASH_INVALID", WriteSafetyPolicy.Validate(invalidHash).Error!.Code);

        var mismatchedHash = Request();
        mismatchedHash.Payload!["mappings"]![0]!["approvedFinalTextHash"] = TestData.HashA;
        Assert.AreEqual("WRITE_MAPPING_HASH_MISMATCH", WriteSafetyPolicy.Validate(mismatchedHash).Error!.Code);

        var weakPolicy = Request();
        weakPolicy.Payload!["validationPolicy"] = "None";
        Assert.AreEqual("WRITE_VALIDATION_POLICY_INVALID", WriteSafetyPolicy.Validate(weakPolicy).Error!.Code);
    }

    private static WireEnvelope Request()
    {
        var fixture = Fixture();
        return CadWriteRequestFactory.Create(fixture.JobId, "C:\\Input\\source.dwg", "C:\\Output\\translated.dwg", TestData.HashA, fixture.Segments, fixture.Review, Now).Value!;
    }

    private static WireEnvelope Response(WireEnvelope request)
    {
        var expected = JsonSerializer.Deserialize<CadWriteRequestPayload>(request.Payload, Json)!;
        var response = new CadWriteResponsePayload
        {
            CandidateHash = TestData.HashB,
            CandidateBytes = 1234,
            SourceHashAfter = expected.ExpectedSourceHash,
            Applied = expected.Mappings.Select(mapping => new CadAppliedMapping { SegmentId = mapping.SegmentId, Result = "WriteSucceeded", PostWriteTextHash = mapping.ApprovedFinalTextHash, VisualEvidence = TestData.VisualEvidence(mapping) }).ToList(),
            Validation = new CadValidationResult { ReopenedByAutoCAD = true, EntityMappingValid = true, GeometryInvariantsValid = true, FormatTokenIntegrityValid = true, VisualInvariantsValid = true, Policy = "VisualStrictV2" },
            Promotion = new CadPromotionResult { Performed = true, FinalPath = expected.FinalPath }
        };
        return new WireEnvelope
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = MessageTypes.WriteResponse,
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            SentAtUtc = Now,
            Status = OperationStatus.Succeeded,
            Payload = JsonSerializer.SerializeToNode(response, Json)!.AsObject()
        };
    }

    private static (Guid JobId, IReadOnlyList<CadTextSegment> Segments, TranslationReviewSnapshot Review) Fixture()
    {
        var jobId = Guid.NewGuid();
        var approved = Segment('a', "PUMP {TAG}", "A1");
        var excluded = Segment('b', "KEEP", "B2");
        var review = new TranslationReviewSnapshot(jobId, 0, "es-MX", "translate-cad-text/1.0", 1, 1, Now,
        [
            new ReviewRowSnapshot(approved.SegmentId, approved.SourceText, "BOMBA {TAG}", "BOMBA {TAG}", SegmentState.Approved, null, null),
            new ReviewRowSnapshot(excluded.SegmentId, excluded.SourceText, "MANTENER", "MANTENER", SegmentState.Excluded, "KEEP_ORIGINAL", null)
        ]);
        return (jobId, new[] { approved, excluded }, review);
    }

    private static CadTextSegment Segment(char value, string text, string handle) => new()
    {
        SegmentId = "seg_sha256_" + new string(value, 64),
        Entity = new CadEntityReference { Type = "TEXT", Handle = handle, Space = "ModelSpace", Layout = null, BlockPath = [], Layer = "NOTES", SubIndex = 0 },
        SourceText = text,
        SourceTextHash = "sha256:" + new string(value, 64),
        LineBreakStyle = "None",
        ProtectedTokens = CadProtectedTokenPolicy.Extract(text).ToList(),
        FieldClassification = "None",
        State = "Extracted"
    };

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class SuccessfulCadGateway : ICadGateway
    {
        public int ExchangeCount { get; private set; }
        public Task<Result<CadCapabilities>> GetCapabilitiesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<WireEnvelope>> ExchangeAsync(WireEnvelope request, CancellationToken cancellationToken)
        {
            ExchangeCount++;
            return Task.FromResult(Results.Success(Response(request)));
        }
    }

    private sealed class DeadlineCapturingSessions : ICadGatewaySessionFactory
    {
        public CadSessionPurpose? Purpose { get; private set; }
        public TimeSpan? ExchangeTimeout { get; private set; }
        public SuccessfulCadGateway Gateway { get; } = new();

        public Task<Result<ICadGatewayLease>> OpenAsync(CadSessionPurpose purpose, CancellationToken cancellationToken) =>
            Task.FromResult(Results.Success<ICadGatewayLease>(new TestLease(Gateway)));

        public Task<Result<ICadGatewayLease>> OpenAsync(CadSessionPurpose purpose, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            Purpose = purpose;
            ExchangeTimeout = exchangeTimeout;
            return OpenAsync(purpose, cancellationToken);
        }
    }

    private sealed class TestLease(ICadGateway gateway) : ICadGatewayLease
    {
        public ICadGateway Gateway { get; } = gateway;
        public Task<Result<bool>> CloseAsync(CancellationToken cancellationToken) => Task.FromResult(Results.Success(true));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
