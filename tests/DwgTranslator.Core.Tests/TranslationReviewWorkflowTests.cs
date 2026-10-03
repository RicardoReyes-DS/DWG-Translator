using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class TranslationReviewWorkflowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-12T23:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task PersistsValidatedReviewCheckpointAfterEveryBatch()
    {
        var store = new RecordingReviewStore();
        var workflow = new TranslationReviewWorkflow(new EchoTranslationGateway(), store, new FixedClock());
        var segments = Enumerable.Range(1, 51).Select(index => Segment(index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), $"TEXT {index}")).ToArray();

        var result = await workflow.TranslateAsync(Guid.NewGuid(), segments, "en", "es-MX", TranslationReviewWorkflow.LegacyPromptTemplateVersion, null, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, store.Snapshots.Count);
        Assert.AreEqual(51, result.Value!.Rows.Count);
        Assert.AreEqual(SegmentState.Proposed, result.Value.Rows[0].State);
        Assert.AreEqual("TR:TEXT 1", result.Value.Rows[0].ProposedText);
        Assert.AreEqual("TR:TEXT 1", result.Value.Rows[0].FinalText);
        Assert.AreEqual(2, result.Value.CompletedBatches);
        Assert.AreEqual(1L, result.Value.Version);
    }

    [TestMethod]
    public async Task StopsOnGatewayFailureWithoutInventingReviewRows()
    {
        var store = new RecordingReviewStore();
        var workflow = new TranslationReviewWorkflow(new FailingTranslationGateway(), store, new FixedClock());

        var result = await workflow.TranslateAsync(Guid.NewGuid(), [Segment('a', "PUMP")], null, "es-MX", TranslationReviewWorkflow.LegacyPromptTemplateVersion, null, CancellationToken.None);

        Assert.AreEqual("TRANSLATION_OFFLINE", result.Error!.Code);
        Assert.AreEqual(0, store.Snapshots.Count);
    }

    [TestMethod]
    public async Task RejectsEmptyJobBeforeCallingDependencies()
    {
        var store = new RecordingReviewStore();
        var workflow = new TranslationReviewWorkflow(new EchoTranslationGateway(), store, new FixedClock());

        var result = await workflow.TranslateAsync(Guid.Empty, [Segment('a', "PUMP")], null, "es-MX", TranslationReviewWorkflow.LegacyPromptTemplateVersion, null, CancellationToken.None);

        Assert.AreEqual("JOB_ID_EMPTY", result.Error!.Code);
        Assert.AreEqual(0, store.Snapshots.Count);
    }

    [TestMethod]
    public async Task AppliesConfiguredBatchLimits()
    {
        var store = new RecordingReviewStore();
        var workflow = new TranslationReviewWorkflow(new EchoTranslationGateway(), store, new FixedClock(), maxSegments: 1, maxCharacters: 10_000);

        var result = await workflow.TranslateAsync(
            Guid.NewGuid(),
            [Segment('a', "ONE"), Segment('b', "TWO"), Segment('c', "THREE")],
            "en",
            "es-MX",
            TranslationReviewWorkflow.LegacyPromptTemplateVersion,
            null,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(3, store.Snapshots.Count);
        Assert.AreEqual(3, result.Value!.CompletedBatches);
    }

    [TestMethod]
    public async Task StopsBeforeNextBatchWhenRequestBudgetIsExhausted()
    {
        var store = new RecordingReviewStore();
        var workflow = new TranslationReviewWorkflow(new EchoTranslationGateway(), store, new FixedClock(),
            maxSegments: 1, maxCharacters: 10_000, maxRequests: 1, maxTotalInputTokens: 10, maxTotalOutputTokens: 10,
            maxInputTokensPerRequest: 10, maxOutputTokensPerRequest: 10);

        var result = await workflow.TranslateAsync(Guid.NewGuid(), [Segment('a', "ONE"), Segment('b', "TWO")], "en", "es-MX",
            TranslationReviewWorkflow.LegacyPromptTemplateVersion, null, default);

        Assert.AreEqual("TRANSLATION_BUDGET_EXHAUSTED", result.Error!.Code);
        Assert.AreEqual(1, store.Snapshots.Count);
    }

    [TestMethod]
    public async Task ResumesAfterLastDurableBatchWithoutRetranslatingCompletedRows()
    {
        var jobId = Guid.NewGuid();
        var segments = new[] { Segment('a', "ONE"), Segment('b', "TWO"), Segment('c', "THREE") };
        var store = new RecordingReviewStore();
        var firstGateway = new FailsOnCallGateway(2);
        var first = new TranslationReviewWorkflow(firstGateway, store, new FixedClock(), maxSegments: 1, maxCharacters: 10_000);

        var interrupted = await first.TranslateAsync(jobId, segments, "en", "es-MX", TranslationReviewWorkflow.LegacyPromptTemplateVersion, null, default);
        Assert.AreEqual("TRANSLATION_OFFLINE", interrupted.Error!.Code);
        Assert.AreEqual(1, store.Snapshots.Count);

        var secondGateway = new EchoTranslationGateway();
        var resumed = await new TranslationReviewWorkflow(secondGateway, store, new FixedClock(), maxSegments: 1, maxCharacters: 10_000)
            .TranslateAsync(jobId, segments, "en", "es-MX", TranslationReviewWorkflow.LegacyPromptTemplateVersion, null, default);

        Assert.IsTrue(resumed.IsSuccess, resumed.Error?.Code);
        Assert.AreEqual(2, secondGateway.CallCount);
        Assert.AreEqual(3, resumed.Value!.Rows.Count);
        Assert.AreEqual(3, resumed.Value.CompletedBatches);
        Assert.AreEqual(2L, resumed.Value.Version);
    }

    [TestMethod]
    public async Task AutoReservesBudgetForPotentialSecondProviderCallBeforeDispatch()
    {
        var store = new RecordingReviewStore();
        var gateway = new EchoTranslationGateway();
        var workflow = new TranslationReviewWorkflow(gateway, store, new FixedClock(),
            maxSegments: 50, maxCharacters: 20_000, maxRequests: 1, maxTotalInputTokens: 100,
            maxTotalOutputTokens: 100, maxInputTokensPerRequest: 100, maxOutputTokensPerRequest: 100);

        var result = await workflow.TranslateAsync(Guid.NewGuid(), [Segment('a', "ONE")], "en", "es-MX",
            TranslationReviewWorkflow.LegacyPromptTemplateVersion, null, TranslationRoutingPolicyFactory.Auto(), default);

        Assert.AreEqual("TRANSLATION_BUDGET_EXHAUSTED", result.Error!.Code);
        Assert.AreEqual(0, gateway.CallCount);
        Assert.AreEqual(0, store.Snapshots.Count);
    }

    [TestMethod]
    public async Task AutoAtExactTwoRequestBudgetPersistsTerraAndSolUsage()
    {
        var store = new RecordingReviewStore();
        var gateway = new TwoCallRoutingGateway();
        var workflow = new TranslationReviewWorkflow(gateway, store, new FixedClock(),
            maxSegments: 50, maxCharacters: 20_000, maxRequests: 2, maxTotalInputTokens: 200,
            maxTotalOutputTokens: 200, maxInputTokensPerRequest: 100, maxOutputTokensPerRequest: 100);

        var result = await workflow.TranslateAsync(Guid.NewGuid(), [Segment('a', "PIPE 100")], "en", "es-MX",
            TranslationReviewWorkflow.LegacyPromptTemplateVersion, null, TranslationRoutingPolicyFactory.Auto(), default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(1, gateway.CallCount);
        Assert.AreEqual(13, result.Value!.UsedInputTokens);
        Assert.AreEqual(7, result.Value.UsedOutputTokens);
        Assert.AreEqual(2, result.Value.UsedProviderRequests);
        Assert.AreEqual(2, result.Value.RoutingCalls!.Count);
    }

    [TestMethod]
    public async Task AutoEscalatedTokenFallbackPersistsExactSourceAndHighWarning()
    {
        var store = new RecordingReviewStore();
        var workflow = new TranslationReviewWorkflow(new TokenFallbackGateway(), store, new FixedClock());
        var source = Segment('a', "PUMP {TAG}");

        var result = await workflow.TranslateAsync(
            Guid.NewGuid(),
            [source],
            "en",
            "es-MX",
            TranslationReviewWorkflow.LegacyPromptTemplateVersion,
            null,
            TranslationRoutingPolicyFactory.Auto(),
            default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(1, store.Snapshots.Count);
        var row = store.Snapshots.Single().Rows.Single();
        Assert.AreEqual(source.SourceText, row.OriginalText);
        Assert.AreEqual(source.SourceText, row.ProposedText);
        Assert.AreEqual(source.SourceText, row.FinalText);
        Assert.AreEqual("high:TOKEN_INTEGRITY_FALLBACK", row.WarningCode);
        Assert.AreEqual("high", row.RiskSeverity);
        Assert.IsTrue(row.Escalated);
        Assert.AreEqual(SegmentState.Proposed, row.State);
        Assert.IsNull(store.Snapshots.Single().ReviewAutomationReceipt);
    }

    [TestMethod]
    public async Task Prompt12PersistsAggregateContextAndRejectsLegacyOrDriftedResume()
    {
        var jobId = Guid.NewGuid();
        var contextual = ContextualSegments(('a', "PUMP", 0), ('b', "VALVE", 1));
        var store = new RecordingReviewStore();
        var workflow = new TranslationReviewWorkflow(new FailsOnCallGateway(2), store, new FixedClock(), maxSegments: 1, maxCharacters: 10_000);

        var interrupted = await workflow.TranslateAsync(jobId, contextual, "en", "es-MX",
            TranslationReviewWorkflow.ContextualPromptTemplateVersion, null, CancellationToken.None);

        Assert.AreEqual("TRANSLATION_OFFLINE", interrupted.Error!.Code);
        Assert.AreEqual(1, store.Snapshots.Count);
        Assert.AreEqual(CadSemanticContextBuilder.PolicyVersion, store.Snapshots[0].ContextPolicyVersion);
        Assert.AreEqual(CadSemanticContextBuilder.AggregateHash(contextual).Value, store.Snapshots[0].ContextHash);
        var legacy = await workflow.TranslateAsync(Guid.NewGuid(), [Segment('b', "PUMP")], "en", "es-MX",
            TranslationReviewWorkflow.ContextualPromptTemplateVersion, null, CancellationToken.None);
        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_REQUIRED", legacy.Error!.Code);
        var contextOnLegacyPrompt = await workflow.TranslateAsync(Guid.NewGuid(), contextual, "en", "es-MX",
            TranslationReviewWorkflow.LegacyPromptTemplateVersion, null, CancellationToken.None);
        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_PROMPT_MISMATCH", contextOnLegacyPrompt.Error!.Code);
        var unknownLegacy = await workflow.TranslateAsync(Guid.NewGuid(), [Segment('c', "VALVE")], "en", "es-MX",
            "translate-cad-text/1.0", null, CancellationToken.None);
        Assert.AreEqual("TRANSLATION_PROMPT_VERSION_UNSUPPORTED", unknownLegacy.Error!.Code);

        var drifted = ContextualSegments(('a', "PUMP", 0), ('b', "VALVE DRIFT", 1));
        var resumed = await new TranslationReviewWorkflow(new EchoTranslationGateway(), store, new FixedClock(), maxSegments: 1, maxCharacters: 10_000)
            .TranslateAsync(jobId, drifted, "en", "es-MX",
            TranslationReviewWorkflow.ContextualPromptTemplateVersion, null, CancellationToken.None);
        Assert.AreEqual("TRANSLATION_RESUME_SNAPSHOT_MISMATCH", resumed.Error!.Code);
    }

    [TestMethod]
    public async Task ContextualPromptPersistsCurrentContextVersionWhileLegacyContextRemainsValid()
    {
        var current = ContextualSegmentsForVersion(CadSemanticContextBuilder.CurrentPolicyVersion,
            ('a', "PUMP", 0));
        var legacy = ContextualSegmentsForVersion(CadSemanticContextBuilder.PolicyVersionOneZero,
            ('b', "VALVE", 0));
        var store = new RecordingReviewStore();
        var workflow = new TranslationReviewWorkflow(new EchoTranslationGateway(), store, new FixedClock());

        var currentResult = await workflow.TranslateAsync(Guid.NewGuid(), current, "en", "es-MX",
            TranslationReviewWorkflow.ContextualPromptTemplateVersion, null, CancellationToken.None);
        var legacyResult = await workflow.TranslateAsync(Guid.NewGuid(), legacy, "en", "es-MX",
            TranslationReviewWorkflow.ContextualPromptTemplateVersion, null, CancellationToken.None);

        Assert.IsTrue(currentResult.IsSuccess, currentResult.Error?.Code);
        Assert.AreEqual(CadSemanticContextBuilder.CurrentPolicyVersion, currentResult.Value!.ContextPolicyVersion);
        Assert.IsTrue(legacyResult.IsSuccess, legacyResult.Error?.Code);
        Assert.AreEqual(CadSemanticContextBuilder.PolicyVersionOneZero, legacyResult.Value!.ContextPolicyVersion);
    }

    private static CadTextSegment Segment(char value, string text) => Segment(new string(value, 64), text);

    private static CadTextSegment Segment(string hashMaterial, string text) => new()
    {
        SegmentId = "seg_sha256_" + hashMaterial,
        Entity = new CadEntityReference { Type = "TEXT", Handle = "1A", Space = "ModelSpace", Layout = null, BlockPath = [], Layer = "NOTES", SubIndex = 0 },
        SourceText = text,
        SourceTextHash = "sha256:" + hashMaterial,
        LineBreakStyle = "None",
        ProtectedTokens = text.Contains("{TAG}", StringComparison.Ordinal) ? [new CadProtectedToken { Token = "{TAG}", Kind = "Placeholder", Ordinal = 0 }] : [],
        FieldClassification = "None",
        State = "Extracted"
    };

    private static CadTextSegment ContextualSegment(char value, string text, double x = 0)
        => ContextualSegments((value, text, x))[0];

    private static CadTextSegment[] ContextualSegments(params (char Value, string Text, double X)[] inputs)
        => ContextualSegmentsForVersion(CadSemanticContextBuilder.PolicyVersionOneZero, inputs);

    private static CadTextSegment[] ContextualSegmentsForVersion(
        string policyVersion,
        params (char Value, string Text, double X)[] inputs)
    {
        var segments = inputs.Select((input, index) =>
            {
                var segment = Segment(input.Value, input.Text);
                return segment with
                {
                    Entity = segment.Entity with
                    {
                        Handle = (index + 1).ToString("X", System.Globalization.CultureInfo.InvariantCulture),
                        Space = index == 0 ? "ModelSpace" : "PaperSpace",
                        Layout = index == 0 ? null : "Sheet1"
                    },
                    SourceTextHash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Text))).ToLowerInvariant()
                };
            })
            .ToArray();
        var built = CadSemanticContextBuilder.Build(segments.Select((segment, index) => new CadSemanticContextInput(
            segment.SegmentId, segment.Entity.Type, segment.Entity.Handle, segment.Entity.Space,
            segment.Entity.Layout, segment.Entity.BlockPath, segment.Entity.Layer, segment.SourceText,
            segment.SourceTextHash, inputs[index].X, 0, 1, "EntityPosition")).ToArray(), policyVersion);
        Assert.IsTrue(built.IsSuccess, built.Error?.Code);
        return segments.Select(segment => segment with { SemanticContext = built.Value![segment.SegmentId] }).ToArray();
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class RecordingReviewStore : ITranslationReviewStore
    {
        public List<TranslationReviewSnapshot> Snapshots { get; } = [];
        public Task<Result<TranslationReviewSnapshot>> SaveAsync(TranslationReviewSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return Task.FromResult(Results.Success(snapshot));
        }
        public Task<Result<TranslationReviewSnapshot>> SaveAsync(TranslationReviewSnapshot snapshot, long expectedVersion, CancellationToken cancellationToken)
        {
            var current = Snapshots.LastOrDefault(item => item.JobId == snapshot.JobId);
            if (current is null || current.Version != expectedVersion || snapshot.Version != expectedVersion + 1)
                return Task.FromResult(Results.Failure<TranslationReviewSnapshot>(new ContractError(
                    "REVIEW_VERSION_CONFLICT", ErrorCategory.Concurrency, "Conflict.", false)));
            Snapshots.Add(snapshot);
            return Task.FromResult(Results.Success(snapshot));
        }
        public Task<Result<TranslationReviewSnapshot>> LoadAsync(Guid jobId, CancellationToken cancellationToken)
        {
            var current = Snapshots.LastOrDefault(snapshot => snapshot.JobId == jobId);
            return Task.FromResult(current is null
                ? Results.Failure<TranslationReviewSnapshot>(new ContractError("REVIEW_NOT_FOUND", ErrorCategory.Storage, "Missing.", false))
                : Results.Success(current));
        }
    }

    private sealed class EchoTranslationGateway : ITranslationGateway
    {
        public int CallCount { get; private set; }
        public Task<Result<WireEnvelope>> TranslateAsync(WireEnvelope request, CancellationToken cancellationToken)
        {
            CallCount++;
            var payload = JsonSerializer.Deserialize<TranslationBatchRequestPayload>(request.Payload, Json)!;
            var response = new TranslationBatchResponsePayload
            {
                Model = "fake-model",
                PromptTemplateVersion = payload.PromptTemplateVersion,
                Proposals = payload.Segments.Select(segment => new TranslationProposal
                {
                    SegmentId = segment.SegmentId,
                    TranslatedTextWithTokenAliases = "TR:" + segment.TextWithTokenAliases,
                    TokenIntegrity = "Valid"
                }).ToList(),
                Usage = new TranslationUsage { InputTokens = 1, OutputTokens = 1 }
            };
            return Task.FromResult(Results.Success(new WireEnvelope
            {
                SchemaVersion = ContractV1.SchemaVersion,
                MessageType = MessageTypes.TranslationResponse,
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                SentAtUtc = Now,
                Status = OperationStatus.Succeeded,
                Payload = JsonSerializer.SerializeToNode(response, Json)!.AsObject()
            }));
        }
    }

    private sealed class TwoCallRoutingGateway : ITranslationGateway
    {
        public int CallCount { get; private set; }
        public Task<Result<WireEnvelope>> TranslateAsync(WireEnvelope request, CancellationToken cancellationToken)
        {
            CallCount++;
            var input = request.Payload!.Deserialize<TranslationBatchRequestPayload>(Json)!;
            var proposal = new TranslationProposal
            {
                SegmentId = input.Segments[0].SegmentId,
                TranslatedTextWithTokenAliases = "PIPE 101",
                TokenIntegrity = "Valid"
            };
            var assessment = TranslationRiskEvaluator.Evaluate(input, [proposal], input.Routing!).Single();
            var calls = new List<TranslationCallTrace>
            {
                Call("base", TranslationRouting.Terra, 10, 5),
                Call("escalation", TranslationRouting.Sol, 3, 2)
            };
            var response = new TranslationBatchResponsePayload
            {
                Model = TranslationRouting.Terra,
                PromptTemplateVersion = input.PromptTemplateVersion,
                Proposals = [proposal],
                Usage = new TranslationUsage { InputTokens = 13, OutputTokens = 7 },
                Routing = new TranslationRoutingTrace
                {
                    RequestedMode = TranslationRouting.Auto,
                    BaseModel = TranslationRouting.Terra,
                    RoutingVersion = TranslationRouting.PolicyVersion,
                    EscalatedSegmentCount = 1,
                    Calls = calls,
                    Segments = [new TranslationSegmentTrace
                    {
                        SegmentId = proposal.SegmentId, RequestedMode = TranslationRouting.Auto,
                        BaseModel = TranslationRouting.Terra, EffectiveModel = TranslationRouting.Sol,
                        Escalated = true, EscalationReasonCodes = assessment.ReasonCodes.ToList(),
                        ValidatorResult = assessment.ReasonCodes.Count == 0 ? "pass" : "review",
                        RiskSeverity = assessment.RiskSeverity, RoutingVersion = TranslationRouting.PolicyVersion
                    }]
                }
            };
            return Task.FromResult(Results.Success(request with
            {
                MessageType = MessageTypes.TranslationResponse,
                Status = OperationStatus.Succeeded,
                Payload = JsonSerializer.SerializeToNode(response, Json)!.AsObject()
            }));
            TranslationCallTrace Call(string tier, string model, int inputTokens, int outputTokens) => new()
            {
                Tier = tier,
                RequestedModel = model,
                EffectiveModel = model,
                Usage = new TranslationUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
                LatencyMilliseconds = 1,
                PromptVersion = input.PromptTemplateVersion,
                SchemaVersion = TranslationRouting.SchemaVersion,
                Outcome = "succeeded"
            };
        }
    }

    private sealed class TokenFallbackGateway : ITranslationGateway
    {
        public Task<Result<WireEnvelope>> TranslateAsync(WireEnvelope request, CancellationToken cancellationToken)
        {
            var input = request.Payload!.Deserialize<TranslationBatchRequestPayload>(Json)!;
            var proposal = new TranslationProposal
            {
                SegmentId = input.Segments.Single().SegmentId,
                TranslatedTextWithTokenAliases = "BOMBA",
                TokenIntegrity = "Invalid"
            };
            var risk = TranslationRiskEvaluator.Evaluate(input, [proposal], input.Routing!).Single();
            var response = new TranslationBatchResponsePayload
            {
                Model = TranslationRouting.Terra,
                PromptTemplateVersion = input.PromptTemplateVersion,
                Proposals = [proposal],
                Usage = new TranslationUsage { InputTokens = 2, OutputTokens = 2 },
                Routing = new TranslationRoutingTrace
                {
                    RequestedMode = TranslationRouting.Auto,
                    BaseModel = TranslationRouting.Terra,
                    RoutingVersion = TranslationRouting.PolicyVersion,
                    EscalatedSegmentCount = 1,
                    Calls =
                    [
                        Call("base", TranslationRouting.Terra),
                        Call("escalation", TranslationRouting.Sol)
                    ],
                    Segments = [new TranslationSegmentTrace
                    {
                        SegmentId = proposal.SegmentId,
                        RequestedMode = TranslationRouting.Auto,
                        BaseModel = TranslationRouting.Terra,
                        EffectiveModel = TranslationRouting.Sol,
                        Escalated = true,
                        EscalationReasonCodes = risk.ReasonCodes.ToList(),
                        ValidatorResult = "review",
                        RiskSeverity = "high",
                        RoutingVersion = TranslationRouting.PolicyVersion
                    }]
                }
            };
            return Task.FromResult(Results.Success(request with
            {
                MessageType = MessageTypes.TranslationResponse,
                Status = OperationStatus.Succeeded,
                Payload = JsonSerializer.SerializeToNode(response, Json)!.AsObject()
            }));

            TranslationCallTrace Call(string tier, string model) => new()
            {
                Tier = tier,
                RequestedModel = model,
                EffectiveModel = model,
                Usage = new TranslationUsage { InputTokens = 1, OutputTokens = 1 },
                LatencyMilliseconds = 1,
                PromptVersion = input.PromptTemplateVersion,
                SchemaVersion = TranslationRouting.SchemaVersion,
                Outcome = "succeeded"
            };
        }
    }

    private sealed class FailsOnCallGateway(int failingCall) : ITranslationGateway
    {
        private readonly EchoTranslationGateway _echo = new();
        private int _calls;
        public Task<Result<WireEnvelope>> TranslateAsync(WireEnvelope request, CancellationToken cancellationToken) =>
            ++_calls == failingCall
                ? Task.FromResult(Results.Failure<WireEnvelope>(new ContractError("TRANSLATION_OFFLINE", ErrorCategory.Transport, "Offline.", true)))
                : _echo.TranslateAsync(request, cancellationToken);
    }

    private sealed class FailingTranslationGateway : ITranslationGateway
    {
        public Task<Result<WireEnvelope>> TranslateAsync(WireEnvelope request, CancellationToken cancellationToken) =>
            Task.FromResult(Results.Failure<WireEnvelope>(new ContractError("TRANSLATION_OFFLINE", ErrorCategory.Transport, "Offline.", true)));
    }
}
