using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;
using DwgTranslator.Infrastructure.Local;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class DwgTranslationJobCoordinatorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-13T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private const string OutputHash = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwg-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task CompletesDurableHumanApprovedFlowThroughAllBoundaries()
    {
        var fixture = CreateFixture();
        var created = await fixture.Coordinator.CreateAsync(Specification(), default);
        Assert.IsTrue(created.IsSuccess);
        var jobId = created.Value!.JobId;

        var extracted = await fixture.Coordinator.InspectAndExtractAsync(jobId, default);
        Assert.AreEqual(JobState.Extracted, extracted.Value!.State);
        var translated = await fixture.Coordinator.TranslateAsync(jobId, default);
        Assert.AreEqual(JobState.ReviewRequired, translated.Value!.State);
        var review = await fixture.Reviews.LoadAsync(jobId, default);
        var approved = await fixture.Coordinator.ApproveAsync(jobId,
            [new ReviewDecisionInput(review.Value!.Rows.Single().SegmentId, "TR:PUMP", null)], default);
        Assert.AreEqual(JobState.Approved, approved.Value!.State);
        var completed = await fixture.Coordinator.GenerateAsync(jobId, default);

        Assert.AreEqual(JobState.Completed, completed.Value!.State);
        Assert.AreEqual(8L, completed.Value.Version);
        var data = completed.Value.Data.Deserialize<DwgTranslationJobData>(Json)!;
        Assert.AreEqual(OutputHash, data.OutputHash);
        Assert.IsNotNull(data.ValidationReport);
        Assert.IsTrue(data.ValidationReport.AutomaticPass);
        Assert.IsTrue(data.ValidationReport.VisualReviewRequired);
        Assert.AreEqual(1, data.ValidationReport.EntityCount);
        Assert.AreEqual(1, data.ValidationReport.BoundsChangedCount);
        Assert.AreEqual(3, fixture.Cad.ExchangeCount);
        Assert.AreEqual(2, fixture.Sessions.OpenCount);
        Assert.AreEqual(2, fixture.Sessions.CloseCount);
        Assert.AreEqual(2, fixture.Sessions.DisposeCount);
        CollectionAssert.AreEqual(new[] { CadSessionPurpose.ReadOnly, CadSessionPurpose.Write }, fixture.Sessions.Purposes);
        Assert.AreEqual(1, fixture.Translation.CallCount);
        Assert.AreEqual(4, Directory.GetFiles(Path.Combine(_root, jobId.ToString("D"), "checkpoints"), "*.json").Length);
        Assert.AreEqual(9, Directory.GetFiles(Path.Combine(_root, jobId.ToString("D"), "audit"), "*.json").Length);
        var persisted = await fixture.Jobs.LoadAsync(jobId, default);
        Assert.AreEqual(JobState.Completed, persisted.Value!.State);
    }

    [TestMethod]
    public async Task ReadRequestsUseCanonicalEnvelopeIdempotency()
    {
        var fixture = CreateFixture();
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;

        var result = await fixture.Coordinator.InspectAndExtractAsync(jobId, default);

        Assert.AreEqual(JobState.Extracted, result.Value!.State);
        Assert.AreEqual(2, fixture.Cad.Requests.Count);
        foreach (var request in fixture.Cad.Requests)
            Assert.AreEqual(request.IdempotencyKey, DwgTranslator.Transport.Core.EnvelopeIdempotency.Fingerprint(request).Value);
    }

    [TestMethod]
    public async Task RefusesWriteBeforeDurableHumanApproval()
    {
        var fixture = CreateFixture();
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;
        await fixture.Coordinator.InspectAndExtractAsync(jobId, default);
        await fixture.Coordinator.TranslateAsync(jobId, default);

        var result = await fixture.Coordinator.GenerateAsync(jobId, default);

        Assert.AreEqual("JOB_STATE_CONFLICT", result.Error!.Code);
        Assert.AreEqual(2, fixture.Cad.ExchangeCount);
        Assert.AreEqual(JobState.ReviewRequired, (await fixture.Jobs.LoadAsync(jobId, default)).Value!.State);
    }

    [TestMethod]
    public async Task TokenLossCannotAdvanceReviewOrInvokeWriter()
    {
        var fixture = CreateFixture(sourceText: "PUMP {TAG}");
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;
        await fixture.Coordinator.InspectAndExtractAsync(jobId, default);
        await fixture.Coordinator.TranslateAsync(jobId, default);
        var row = (await fixture.Reviews.LoadAsync(jobId, default)).Value!.Rows.Single();

        var result = await fixture.Coordinator.ApproveAsync(jobId, [new ReviewDecisionInput(row.SegmentId, "BOMBA", null)], default);

        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", result.Error!.Code);
        Assert.AreEqual(JobState.ReviewRequired, (await fixture.Jobs.LoadAsync(jobId, default)).Value!.State);
        Assert.AreEqual(0L, (await fixture.Reviews.LoadAsync(jobId, default)).Value!.Version);
        Assert.AreEqual(2, fixture.Cad.ExchangeCount);
    }

    [TestMethod]
    public async Task TranslationFailureBecomesDurableFailedStateWithoutCadWrite()
    {
        var fixture = CreateFixture(translationFailure: true);
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;
        await fixture.Coordinator.InspectAndExtractAsync(jobId, default);

        var result = await fixture.Coordinator.TranslateAsync(jobId, default);

        Assert.AreEqual(JobState.Failed, result.Value!.State);
        Assert.AreEqual("TRANSLATION_OFFLINE", result.Value.Data["failure"]!["code"]!.GetValue<string>());
        Assert.AreEqual(2, fixture.Cad.ExchangeCount);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_root, jobId.ToString("D"), "checkpoints"), "*.json").Length);
    }

    [TestMethod]
    public async Task StrictInvariantDiagnosticsPersistOnFailureWithoutValidationReport()
    {
        var diagnostics = new CadInvariantDiagnostics
        {
            Schema = CadInvariantDiagnosticsPolicy.Schema,
            AddedCount = 0,
            RemovedCount = 0,
            ChangedCount = 1,
            Truncated = false,
            Rows = [new CadInvariantDifference
            {
                InvariantKey = "10|A1",
                ChangeKind = "Changed",
                FieldsChanged = ["extents"],
                Before = null,
                After = null
            }]
        };
        var fixture = CreateFixture(writeFailure: new ContractError("GEOMETRY_INVARIANTS_CHANGED", ErrorCategory.Integrity, "Strict failure.", false,
            InvariantDiagnostics: diagnostics));
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;
        await fixture.Coordinator.InspectAndExtractAsync(jobId, default);
        await fixture.Coordinator.TranslateAsync(jobId, default);
        var row = (await fixture.Reviews.LoadAsync(jobId, default)).Value!.Rows.Single();
        await fixture.Coordinator.ApproveAsync(jobId, [new ReviewDecisionInput(row.SegmentId, "TR:PUMP", null)], default);

        var failed = await fixture.Coordinator.GenerateAsync(jobId, default);

        Assert.AreEqual(JobState.Failed, failed.Value!.State);
        Assert.AreEqual("GEOMETRY_INVARIANTS_CHANGED", failed.Value.Data["failure"]!["code"]!.GetValue<string>());
        Assert.IsNull(failed.Value.Data.Deserialize<DwgTranslationJobData>(Json)!.ValidationReport);
        Assert.AreEqual(CadInvariantDiagnosticsPolicy.Schema,
            failed.Value.Data["failure"]!["invariantDiagnostics"]!["schema"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnexpectedTranslationExceptionBecomesRetryableDurableFailure()
    {
        var fixture = CreateFixture(translationThrows: true);
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;
        await fixture.Coordinator.InspectAndExtractAsync(jobId, default);

        var result = await fixture.Coordinator.TranslateAsync(jobId, default);

        Assert.AreEqual(JobState.Failed, result.Value!.State);
        Assert.AreEqual("TRANSLATION_UNEXPECTED_FAILURE", result.Value.Data["failure"]!["code"]!.GetValue<string>());
        Assert.IsTrue(result.Value.Data["failure"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual("Translation", result.Value.Data["failure"]!["stage"]!.GetValue<string>());
        Assert.AreEqual(typeof(InvalidOperationException).FullName, result.Value.Data["failure"]!["exceptionType"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task IncompatibleCadCapabilitiesFailBeforeOpeningAReadOperation()
    {
        var fixture = CreateFixture(incompatibleCad: true);
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;

        var result = await fixture.Coordinator.InspectAndExtractAsync(jobId, default);

        Assert.AreEqual(JobState.Failed, result.Value!.State);
        Assert.AreEqual("CAD_CAPABILITIES_INCOMPATIBLE", result.Value.Data["failure"]!["code"]!.GetValue<string>());
        Assert.AreEqual(0, fixture.Cad.ExchangeCount);
        Assert.AreEqual(1, fixture.Sessions.CloseCount);
        Assert.AreEqual(1, fixture.Sessions.DisposeCount);
    }

    [TestMethod]
    public async Task CancellationDuringTranslationIsPersistedAtSafeBoundary()
    {
        var fixture = CreateFixture(translationWaitsForCancellation: true);
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;
        await fixture.Coordinator.InspectAndExtractAsync(jobId, default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await fixture.Coordinator.TranslateAsync(jobId, cancellation.Token);

        Assert.AreEqual(JobState.Cancelled, result.Value!.State);
        Assert.AreEqual(JobState.Cancelled, (await fixture.Jobs.LoadAsync(jobId, default)).Value!.State);
        Assert.AreEqual(2, fixture.Cad.ExchangeCount);
    }

    [TestMethod]
    public async Task SuccessfulCadPayloadIsRejectedWhenSessionCannotClose()
    {
        var fixture = CreateFixture(cadCloseFailure: true);
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;

        var result = await fixture.Coordinator.InspectAndExtractAsync(jobId, default);

        Assert.AreEqual(JobState.Failed, result.Value!.State);
        Assert.AreEqual("CAD_CLOSE_FAILED", result.Value.Data["failure"]!["code"]!.GetValue<string>());
        Assert.AreEqual(2, fixture.Cad.ExchangeCount);
        Assert.AreEqual(1, fixture.Sessions.CloseCount);
    }

    [TestMethod]
    public async Task ReadOnlySessionRetriesOneRecoverableOpenFailure()
    {
        var fixture = CreateFixture(retryableOpenFailures: 1);
        var jobId = (await fixture.Coordinator.CreateAsync(Specification(), default)).Value!.JobId;

        var result = await fixture.Coordinator.InspectAndExtractAsync(jobId, default);

        Assert.AreEqual(JobState.Extracted, result.Value!.State);
        Assert.AreEqual(2, fixture.Sessions.OpenCount);
        Assert.AreEqual(1, fixture.Sessions.CloseCount);
        Assert.AreEqual(1, fixture.Sessions.DisposeCount);
        Assert.AreEqual(2, fixture.Cad.ExchangeCount);
    }

    private Fixture CreateFixture(
        string sourceText = "PUMP",
        bool translationFailure = false,
        bool translationThrows = false,
        bool incompatibleCad = false,
        bool translationWaitsForCancellation = false,
        bool cadCloseFailure = false,
        int retryableOpenFailures = 0,
        ContractError? writeFailure = null)
    {
        var paths = new WorkspacePaths(_root);
        var jobs = new LocalJobStore(paths);
        var reviews = new LocalTranslationReviewStore(paths);
        var clock = new FixedClock();
        var cad = new SyntheticCadGateway(sourceText, incompatibleCad, writeFailure);
        var sessions = new TrackingCadSessionFactory(cad, cadCloseFailure, retryableOpenFailures);
        var translation = new SyntheticTranslationGateway(translationFailure, translationWaitsForCancellation, translationThrows);
        var coordinator = new DwgTranslationJobCoordinator(
            jobs,
            reviews,
            new CadReadWorkflow(sessions, clock),
            new TranslationReviewWorkflow(translation, reviews, clock),
            new CadWriteWorkflow(sessions, clock),
            clock);
        return new Fixture(coordinator, jobs, reviews, cad, sessions, translation);
    }

    private static DwgTranslationJobSpecification Specification() => new(
        "C:\\Input\\source.dwg",
        "C:\\Output\\translated.dwg",
        TestData.HashA,
        "en",
        "es-MX",
        TranslationReviewWorkflow.LegacyPromptTemplateVersion,
        null);

    private sealed record Fixture(
        DwgTranslationJobCoordinator Coordinator,
        LocalJobStore Jobs,
        LocalTranslationReviewStore Reviews,
        SyntheticCadGateway Cad,
        TrackingCadSessionFactory Sessions,
        SyntheticTranslationGateway Translation);

    private sealed class TrackingCadSessionFactory(ICadGateway gateway, bool closeFailure, int retryableOpenFailures) : ICadGatewaySessionFactory
    {
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }
        public int DisposeCount { get; private set; }
        public CadSessionPurpose[] Purposes => _purposes.ToArray();
        private readonly List<CadSessionPurpose> _purposes = [];

        public Task<Result<ICadGatewayLease>> OpenAsync(CadSessionPurpose purpose, CancellationToken cancellationToken)
        {
            OpenCount++;
            _purposes.Add(purpose);
            if (OpenCount <= retryableOpenFailures)
                return Task.FromResult(Results.Failure<ICadGatewayLease>(new ContractError(
                    "IPC_TIMEOUT", ErrorCategory.Transport, "Synthetic recoverable open timeout.", true)));
            return Task.FromResult(Results.Success<ICadGatewayLease>(new TrackingLease(gateway, closeFailure, () => CloseCount++, () => DisposeCount++)));
        }

        private sealed class TrackingLease(ICadGateway gateway, bool closeFailure, Action closed, Action disposed) : ICadGatewayLease
        {
            private bool _disposed;
            private bool _closed;
            public ICadGateway Gateway { get; } = gateway;
            public Task<Result<bool>> CloseAsync(CancellationToken cancellationToken)
            {
                if (!_closed)
                {
                    _closed = true;
                    closed();
                }
                return Task.FromResult(closeFailure
                    ? Results.Failure<bool>(new ContractError("CAD_CLOSE_FAILED", ErrorCategory.Environment, "Synthetic close failure.", false))
                    : Results.Success(true));
            }
            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    disposed();
                }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class SyntheticTranslationGateway(bool fail, bool waitForCancellation, bool throws) : ITranslationGateway
    {
        public int CallCount { get; private set; }

        public async Task<Result<WireEnvelope>> TranslateAsync(WireEnvelope request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (waitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (throws) throw new InvalidOperationException("Synthetic unexpected failure.");
            if (fail)
                return Results.Failure<WireEnvelope>(new ContractError("TRANSLATION_OFFLINE", ErrorCategory.Transport, "Offline.", true));
            var payload = request.Payload!.Deserialize<TranslationBatchRequestPayload>(Json)!;
            var response = new TranslationBatchResponsePayload
            {
                Model = "synthetic-model",
                PromptTemplateVersion = payload.PromptTemplateVersion,
                Proposals = payload.Segments.Select(segment => new TranslationProposal
                {
                    SegmentId = segment.SegmentId,
                    TranslatedTextWithTokenAliases = "TR:" + segment.TextWithTokenAliases,
                    TokenIntegrity = "Valid"
                }).ToList(),
                Usage = new TranslationUsage { InputTokens = 1, OutputTokens = 1 }
            };
            return Results.Success(Response(request, MessageTypes.TranslationResponse, JsonSerializer.SerializeToNode(response, Json)!.AsObject()));
        }
    }

    private sealed class SyntheticCadGateway : ICadGateway
    {
        private readonly CadTextSegment _segment;
        private readonly bool _incompatible;
        private readonly ContractError? _writeFailure;

        public SyntheticCadGateway(string sourceText, bool incompatible, ContractError? writeFailure = null)
        {
            _incompatible = incompatible;
            _writeFailure = writeFailure;
            var address = new CadSegmentAddress("TEXT", "A1", "ModelSpace", null, "NOTES", 0);
            var identity = CadSegmentIdentityV1.Create(TestData.HashA, TestData.HashB, address, sourceText).Value!;
            _segment = new CadTextSegment
            {
                SegmentId = identity.SegmentId,
                Entity = new CadEntityReference { Type = "TEXT", Handle = "A1", Space = "ModelSpace", Layout = null, BlockPath = [], Layer = "NOTES", SubIndex = 0 },
                SourceText = sourceText,
                SourceTextHash = identity.SourceTextHash,
                LineBreakStyle = "None",
                ProtectedTokens = sourceText.Contains("{TAG}", StringComparison.Ordinal)
                    ? [new CadProtectedToken { Token = "{TAG}", Kind = "Placeholder", Ordinal = 0 }]
                    : [],
                FieldClassification = "None",
                State = "Extracted"
            };
        }

        public int ExchangeCount { get; private set; }
        public List<WireEnvelope> Requests { get; } = [];

        public Task<Result<CadCapabilities>> GetCapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult(Results.Success(
            new CadCapabilities(ContractV1.SchemaVersion, "AutoCAD 2026 synthetic", _incompatible
                ? [CadOperations.Inspect]
                : [CadOperations.Inspect, CadOperations.Extract, CadOperations.Write])));

        public Task<Result<WireEnvelope>> ExchangeAsync(WireEnvelope request, CancellationToken cancellationToken)
        {
            ExchangeCount++;
            Requests.Add(request);
            if (request.MessageType == MessageTypes.WriteRequest && _writeFailure is not null)
                return Task.FromResult(Results.Success(new WireEnvelope
                {
                    SchemaVersion = ContractV1.SchemaVersion,
                    MessageType = MessageTypes.WriteResponse,
                    JobId = request.JobId,
                    CorrelationId = request.CorrelationId,
                    IdempotencyKey = request.IdempotencyKey,
                    SentAtUtc = Now,
                    Status = OperationStatus.Failed,
                    Error = _writeFailure
                }));
            JsonObject payload = request.MessageType switch
            {
                MessageTypes.InspectRequest => JsonSerializer.SerializeToNode(new CadInspectResponsePayload
                {
                    SourceHash = TestData.HashA,
                    Autocad = new CadHostDescriptor { Product = "AutoCAD", Year = 2026, ApiVersion = "25.1" },
                    DrawingFingerprint = TestData.HashB,
                    Inventory = new Dictionary<string, int> { ["TEXT"] = 1, ["MTEXT"] = 0 },
                    SupportedCount = 1,
                    Unsupported = [],
                    Warnings = []
                }, Json)!.AsObject(),
                MessageTypes.ExtractRequest => JsonSerializer.SerializeToNode(new CadExtractResponsePayload
                {
                    SourceHash = TestData.HashA,
                    Segments = [_segment],
                    ExcludedFieldCount = 0
                }, Json)!.AsObject(),
                MessageTypes.WriteRequest => WritePayload(request),
                _ => throw new InvalidOperationException(request.MessageType)
            };
            var responseType = request.MessageType switch
            {
                MessageTypes.InspectRequest => MessageTypes.InspectResponse,
                MessageTypes.ExtractRequest => MessageTypes.ExtractResponse,
                MessageTypes.WriteRequest => MessageTypes.WriteResponse,
                _ => throw new InvalidOperationException(request.MessageType)
            };
            return Task.FromResult(Results.Success(Response(request, responseType, payload)));
        }

        private static JsonObject WritePayload(WireEnvelope request)
        {
            var expected = request.Payload!.Deserialize<CadWriteRequestPayload>(Json)!;
            return JsonSerializer.SerializeToNode(new CadWriteResponsePayload
            {
                CandidateHash = OutputHash,
                CandidateBytes = 1234,
                SourceHashAfter = expected.ExpectedSourceHash,
                Applied = expected.Mappings.Select(mapping => new CadAppliedMapping
                {
                    SegmentId = mapping.SegmentId,
                    Result = "WriteSucceeded",
                    PostWriteTextHash = mapping.ApprovedFinalTextHash,
                    VisualEvidence = TestData.VisualEvidence(mapping)
                }).ToList(),
                Validation = new CadValidationResult
                {
                    ReopenedByAutoCAD = true,
                    EntityMappingValid = true,
                    GeometryInvariantsValid = true,
                    FormatTokenIntegrityValid = true,
                    VisualInvariantsValid = true,
                    Policy = "VisualStrictV2"
                },
                Promotion = new CadPromotionResult { Performed = true, FinalPath = expected.FinalPath }
            }, Json)!.AsObject();
        }
    }

    private static WireEnvelope Response(WireEnvelope request, string messageType, JsonObject payload) => new()
    {
        SchemaVersion = ContractV1.SchemaVersion,
        MessageType = messageType,
        JobId = request.JobId,
        CorrelationId = request.CorrelationId,
        IdempotencyKey = request.IdempotencyKey,
        SentAtUtc = Now,
        Status = OperationStatus.Succeeded,
        Payload = payload
    };
}
