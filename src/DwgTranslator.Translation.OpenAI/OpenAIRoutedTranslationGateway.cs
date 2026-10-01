using System.Diagnostics;
using System.Text.Json;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Translation.OpenAI;

public sealed class OpenAIRoutedTranslationGateway : ITranslationGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    private readonly HttpClient _client;
    private readonly ISecretStore _secrets;
    private readonly SecretReference _secretReference;
    private readonly IOpenAiModelProvider _models;
    private readonly OpenAITranslationGateway.Limits _limits;

    public OpenAIRoutedTranslationGateway(
        HttpClient client,
        ISecretStore secrets,
        SecretReference secretReference,
        IOpenAiModelProvider models,
        OpenAITranslationGateway.Limits limits)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _secretReference = secretReference ?? throw new ArgumentNullException(nameof(secretReference));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _limits = OpenAITranslationGateway.Limits.Validate(limits);
    }

    public async Task<Result<WireEnvelope>> TranslateAsync(WireEnvelope request, CancellationToken cancellationToken)
    {
        var decoded = TranslationResultPolicy.ValidateRequest(request);
        if (!decoded.IsSuccess) return Results.Failure<WireEnvelope>(decoded.Error!);
        var input = decoded.Value!;
        var policy = input.Routing ?? TranslationRoutingPolicyFactory.Manual(_models.Model);
        var policyResult = TranslationRoutingPolicyFactory.Validate(policy);
        if (!policyResult.IsSuccess) return Results.Failure<WireEnvelope>(policyResult.Error!);
        if (!_models.IsModelAccessible(policy.BaseModel))
            return Failure("OPENAI_BASE_MODEL_UNAVAILABLE", "The exact base model is unavailable; no tier substitution was made.");

        var baseAttempt = await CallAsync(request, policy.BaseModel, cancellationToken).ConfigureAwait(false);
        if (!baseAttempt.Result.IsSuccess)
        {
            if (policy.RequestedMode != TranslationRouting.Auto || policy.EscalationModel is null ||
                baseAttempt.Result.Error?.Code != "OPENAI_STRUCTURED_OUTPUT_INVALID" || !_models.IsModelAccessible(policy.EscalationModel))
                return baseAttempt.Result;
            var retryPayload = input with
            {
                Escalation = new TranslationEscalationContext
                {
                    BaseModel = policy.BaseModel,
                    Segments = input.Segments.Select(segment => new TranslationEscalationSegment
                    {
                        SegmentId = segment.SegmentId,
                        BaseProposalWithTokenAliases = string.Empty,
                        ReasonCodes = ["SCHEMA_INVALID"]
                    }).ToList()
                }
            };
            var retryAttempt = await CallAsync(CreateRequest(request, retryPayload), policy.EscalationModel, cancellationToken).ConfigureAwait(false);
            if (!retryAttempt.Result.IsSuccess) return retryAttempt.Result;
            var retryPayloadResponse = Decode(retryAttempt.Result.Value!);
            if (!retryPayloadResponse.IsSuccess) return Results.Failure<WireEnvelope>(retryPayloadResponse.Error!);
            return CreateResponse(request, input, policy, retryPayloadResponse.Value!, retryAttempt.ElapsedMilliseconds,
                null, null, 0, null, input.Segments.Select(segment => segment.SegmentId).ToHashSet(StringComparer.Ordinal), ["SCHEMA_INVALID"], baseAttempt.ElapsedMilliseconds);
        }

        var baseResponse = Decode(baseAttempt.Result.Value!);
        if (!baseResponse.IsSuccess) return Results.Failure<WireEnvelope>(baseResponse.Error!);
        var baseRisk = TranslationRiskEvaluator.Evaluate(input, baseResponse.Value!.Proposals, policy);
        var highIds = baseRisk.Where(item => item.RiskSeverity == "high").Select(item => item.SegmentId).ToHashSet(StringComparer.Ordinal);
        TranslationBatchResponsePayload? escalationResponse = null;
        TranslationBatchResponsePayload? invalidEscalationResponse = null;
        long escalationLatency = 0;
        string? escalationErrorCode = null;
        if (policy.RequestedMode == TranslationRouting.Auto && policy.EscalateHighRisk && policy.EscalationModel is not null && highIds.Count > 0 &&
            _models.IsModelAccessible(policy.EscalationModel))
        {
            // Contextual requests form a closed semantic unit: a high-risk segment may reference a
            // lower-risk support segment through NeighborExcerpts. The original request has already
            // passed the 50-segment/20k-character outbound policy, so Sol must receive that exact
            // validated unit. A high-only subset is not a valid contextual request.
            var retryAttempt = await CallAsync(request, policy.EscalationModel, cancellationToken).ConfigureAwait(false);
            escalationLatency = retryAttempt.ElapsedMilliseconds;
            if (retryAttempt.Result.IsSuccess)
            {
                var decodedRetry = Decode(retryAttempt.Result.Value!);
                // The provider response has no durable routing trace yet. Validate its exact segment
                // set, token restoration, glossary and contextual bindings against an otherwise
                // identical non-routed view; CreateResponse adds the audited mixed routing trace.
                var validationRequest = request with
                {
                    Payload = JsonSerializer.SerializeToNode(input with { Routing = null, Escalation = null }, Json)!.AsObject()
                };
                var validatedRetry = TranslationResultPolicy.Validate(validationRequest, retryAttempt.Result.Value!);
                if (!validatedRetry.IsSuccess)
                {
                    escalationErrorCode = validatedRetry.Error?.Code ?? "OPENAI_ESCALATION_RESPONSE_INVALID";
                    if (decodedRetry.IsSuccess) invalidEscalationResponse = decodedRetry.Value;
                }
                else
                {
                    if (decodedRetry.IsSuccess) escalationResponse = decodedRetry.Value;
                    else escalationErrorCode = decodedRetry.Error?.Code ?? "OPENAI_ESCALATION_RESPONSE_INVALID";
                }
            }
            else escalationErrorCode = retryAttempt.Result.Error?.Code ?? "OPENAI_ESCALATION_FAILED";
        }
        return CreateResponse(request, input, policy, baseResponse.Value, baseAttempt.ElapsedMilliseconds,
            escalationResponse, invalidEscalationResponse, escalationLatency, escalationErrorCode, highIds, null, null);
    }

    private static Result<WireEnvelope> CreateResponse(
        WireEnvelope request,
        TranslationBatchRequestPayload input,
        TranslationRoutingPolicy policy,
        TranslationBatchResponsePayload baseResponse,
        long baseLatency,
        TranslationBatchResponsePayload? escalation,
        TranslationBatchResponsePayload? invalidEscalation,
        long escalationLatency,
        string? escalationErrorCode,
        HashSet<string> highIds,
        IReadOnlyList<string>? forcedReasons,
        long? failedBaseLatency)
    {
        var baseById = baseResponse.Proposals.GroupBy(item => item.SegmentId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var baseRisk = TranslationRiskEvaluator.Evaluate(input, baseResponse.Proposals, policy).ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        var retryById = escalation?.Proposals.GroupBy(item => item.SegmentId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
            ?? new Dictionary<string, TranslationProposal>(StringComparer.Ordinal);
        var retryRisk = escalation is null ? new Dictionary<string, TranslationRiskAssessment>(StringComparer.Ordinal) :
            TranslationRiskEvaluator.Evaluate(input, escalation.Proposals, policy).ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        var final = new List<TranslationProposal>(input.Segments.Count);
        var selections = new Dictionary<string, (string EffectiveModel, bool Escalated)>(StringComparer.Ordinal);
        foreach (var segment in input.Segments)
        {
            baseById.TryGetValue(segment.SegmentId, out var selected);
            var assessment = baseRisk[segment.SegmentId];
            var effective = baseResponse.Model;
            var attempted = highIds.Contains(segment.SegmentId) && (escalation is not null || escalationErrorCode is not null || forcedReasons is not null);
            if (highIds.Contains(segment.SegmentId) && selected is null && retryById.TryGetValue(segment.SegmentId, out var onlyRetry))
            {
                selected = onlyRetry;
                assessment = retryRisk[segment.SegmentId];
                effective = escalation!.Model;
            }
            else if (highIds.Contains(segment.SegmentId) && selected is not null && retryById.TryGetValue(segment.SegmentId, out var candidateProposal) &&
                retryRisk.TryGetValue(segment.SegmentId, out var candidateRisk) && TranslationRiskEvaluator.PreferCandidate(assessment, candidateRisk))
            {
                selected = candidateProposal;
                assessment = candidateRisk;
                effective = escalation!.Model;
            }
            if (selected is null) return Failure("TRANSLATION_SEGMENT_SET_MISMATCH", "The provider did not return every requested segment.");
            final.Add(selected);
            selections.Add(segment.SegmentId, (effective, attempted || forcedReasons is not null));
        }

        // Recompute the final mixed proposal set under the full request: repeated-source and
        // glossary checks are batch-scoped.
        var finalRisk = TranslationRiskEvaluator.Evaluate(input, final, policy)
            .ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        var traces = new List<TranslationSegmentTrace>(input.Segments.Count);
        foreach (var segment in input.Segments)
        {
            var assessment = finalRisk[segment.SegmentId];
            var selection = selections[segment.SegmentId];
            var reasons = assessment.ReasonCodes.ToList();
            if (forcedReasons is not null) reasons.AddRange(forcedReasons);
            if (highIds.Contains(segment.SegmentId) && escalation is null && policy.RequestedMode == TranslationRouting.Auto)
                reasons.Add("SOL_UNAVAILABLE_OR_FAILED");
            traces.Add(new TranslationSegmentTrace
            {
                SegmentId = segment.SegmentId,
                RequestedMode = policy.RequestedMode,
                BaseModel = policy.BaseModel,
                EffectiveModel = selection.EffectiveModel,
                Escalated = selection.Escalated,
                EscalationReasonCodes = reasons.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList(),
                ValidatorResult = assessment.ReasonCodes.Count == 0 ? "pass" : "review",
                RiskSeverity = assessment.RiskSeverity,
                RoutingVersion = policy.Version
            });
        }
        var calls = new List<TranslationCallTrace>();
        if (forcedReasons is null)
            calls.Add(Trace("base", policy.BaseModel, baseResponse, baseLatency, input.PromptTemplateVersion));
        else
        {
            calls.Add(FailedTrace("base", policy.BaseModel, "OPENAI_STRUCTURED_OUTPUT_INVALID", failedBaseLatency ?? 0, input.PromptTemplateVersion));
            calls.Add(Trace("escalation", policy.EscalationModel!, baseResponse, baseLatency, input.PromptTemplateVersion));
        }
        if (escalation is not null) calls.Add(Trace("escalation", policy.EscalationModel!, escalation, escalationLatency, input.PromptTemplateVersion));
        else if (escalationErrorCode is not null) calls.Add(FailedTrace("escalation", policy.EscalationModel!, escalationErrorCode,
            escalationLatency, input.PromptTemplateVersion, invalidEscalation?.Usage, invalidEscalation?.ProviderRequestId));
        var response = new TranslationBatchResponsePayload
        {
            Model = baseResponse.Model,
            PromptTemplateVersion = input.PromptTemplateVersion,
            Proposals = final,
            Usage = new TranslationUsage
            {
                InputTokens = calls.Sum(call => call.Usage.InputTokens),
                OutputTokens = calls.Sum(call => call.Usage.OutputTokens)
            },
            Routing = new TranslationRoutingTrace
            {
                RequestedMode = policy.RequestedMode,
                BaseModel = policy.BaseModel,
                RoutingVersion = policy.Version,
                EscalatedSegmentCount = traces.Count(item => item.Escalated),
                Calls = calls,
                Segments = traces
            }
        };
        return Results.Success(new WireEnvelope
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = MessageTypes.TranslationResponse,
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            SentAtUtc = DateTimeOffset.UtcNow,
            Status = OperationStatus.Succeeded,
            Payload = JsonSerializer.SerializeToNode(response, Json)!.AsObject()
        });
    }

    private async Task<CallAttempt> CallAsync(WireEnvelope request, string model, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var gateway = new OpenAITranslationGateway(_client, _secrets, _secretReference, model, _limits);
        var result = await gateway.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return new CallAttempt(result, watch.ElapsedMilliseconds);
    }

    private static WireEnvelope CreateRequest(WireEnvelope original, TranslationBatchRequestPayload payload) => WireEnvelope.Request(
        MessageTypes.TranslationRequest,
        original.JobId,
        Guid.NewGuid(),
        TranslationBatchFactory.ConfigurationHash(payload),
        DateTimeOffset.UtcNow,
        JsonSerializer.SerializeToNode(payload, Json)!.AsObject());

    private static Result<TranslationBatchResponsePayload> Decode(WireEnvelope response) =>
        EnvelopeCodec.DecodePayload<TranslationBatchResponsePayload>(response, MessageTypes.TranslationResponse);

    private static TranslationCallTrace Trace(string tier, string requestedModel, TranslationBatchResponsePayload response, long latency, string promptVersion) => new()
    {
        Tier = tier,
        RequestedModel = requestedModel,
        EffectiveModel = response.Model,
        Usage = response.Usage,
        RequestId = response.ProviderRequestId,
        LatencyMilliseconds = latency,
        PromptVersion = promptVersion,
        SchemaVersion = TranslationRouting.SchemaVersion,
        Outcome = "succeeded"
    };

    private static TranslationCallTrace FailedTrace(string tier, string requestedModel, string errorCode, long latency, string promptVersion,
        TranslationUsage? usage = null, string? requestId = null) => new()
        {
            Tier = tier,
            RequestedModel = requestedModel,
            EffectiveModel = requestedModel,
            Usage = usage ?? new TranslationUsage { InputTokens = 0, OutputTokens = 0 },
            RequestId = requestId,
            LatencyMilliseconds = latency,
            PromptVersion = promptVersion,
            SchemaVersion = TranslationRouting.SchemaVersion,
            Outcome = "failed",
            ErrorCode = errorCode
        };

    private static Result<WireEnvelope> Failure(string code, string message) =>
        Results.Failure<WireEnvelope>(new ContractError(code, ErrorCategory.Configuration, message, false));
    private sealed record CallAttempt(Result<WireEnvelope> Result, long ElapsedMilliseconds);
}
