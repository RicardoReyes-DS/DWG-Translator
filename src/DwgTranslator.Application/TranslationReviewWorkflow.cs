using System.Text.Json;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Application;

public sealed class TranslationReviewWorkflow
{
    public const string ContextualPromptTemplateVersion = "translate-cad-text/1.2";
    public const string LegacyPromptTemplateVersion = "translate-cad-text/1.1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ITranslationGateway _translation;
    private readonly ITranslationReviewStore _reviews;
    private readonly IClock _clock;
    private readonly int _maxSegments;
    private readonly int _maxCharacters;
    private readonly int _maxRequests;
    private readonly int _maxTotalInputTokens;
    private readonly int _maxTotalOutputTokens;
    private readonly int _maxInputTokensPerRequest;
    private readonly int _maxOutputTokensPerRequest;

    public TranslationReviewWorkflow(
        ITranslationGateway translation,
        ITranslationReviewStore reviews,
        IClock clock,
        int maxSegments = TranslationBatchFactory.DefaultMaxSegments,
        int maxCharacters = TranslationBatchFactory.DefaultMaxCharacters,
        int maxRequests = 100,
        int maxTotalInputTokens = 1_000_000,
        int maxTotalOutputTokens = 100_000,
        int maxInputTokensPerRequest = 100_000,
        int maxOutputTokensPerRequest = 4_096)
    {
        _translation = translation;
        _reviews = reviews;
        _clock = clock;
        if (maxSegments is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(maxSegments), "The segment batch limit is invalid.");
        if (maxCharacters is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(maxCharacters), "The character batch limit is invalid.");
        if (maxRequests is < 1 or > 10_000 || maxTotalInputTokens is < 1 or > 10_000_000 || maxTotalOutputTokens is < 1 or > 1_000_000 ||
            maxInputTokensPerRequest < 1 || maxInputTokensPerRequest > maxTotalInputTokens ||
            maxOutputTokensPerRequest < 1 || maxOutputTokensPerRequest > maxTotalOutputTokens)
            throw new ArgumentOutOfRangeException(nameof(maxRequests), "Translation budget limits are invalid.");
        _maxSegments = maxSegments;
        _maxCharacters = maxCharacters;
        _maxRequests = maxRequests;
        _maxTotalInputTokens = maxTotalInputTokens;
        _maxTotalOutputTokens = maxTotalOutputTokens;
        _maxInputTokensPerRequest = maxInputTokensPerRequest;
        _maxOutputTokensPerRequest = maxOutputTokensPerRequest;
    }

    public async Task<Result<TranslationReviewSnapshot>> TranslateAsync(
        Guid jobId,
        IReadOnlyList<CadTextSegment> segments,
        string? sourceLanguage,
        string targetLanguage,
        string promptTemplateVersion,
        IReadOnlyList<TranslationGlossaryEntry>? glossary,
        CancellationToken cancellationToken) => await TranslateAsync(
            jobId, segments, sourceLanguage, targetLanguage, promptTemplateVersion, glossary, null, cancellationToken);

    public async Task<Result<TranslationReviewSnapshot>> TranslateAsync(
        Guid jobId,
        IReadOnlyList<CadTextSegment> segments,
        string? sourceLanguage,
        string targetLanguage,
        string promptTemplateVersion,
        IReadOnlyList<TranslationGlossaryEntry>? glossary,
        TranslationRoutingPolicy? routing,
        CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty)
            return Failure("JOB_ID_EMPTY", ErrorCategory.Input, "jobId is required.");

        var batches = TranslationBatchFactory.Create(
            segments,
            sourceLanguage,
            targetLanguage,
            promptTemplateVersion,
            glossary,
            _maxSegments,
            _maxCharacters,
            routing);
        if (!batches.IsSuccess)
            return Results.Failure<TranslationReviewSnapshot>(batches.Error!);
        var batchValues = batches.Value!;

        var contextualCount = segments.Count(segment => segment.SemanticContext is not null);
        string? contextPolicyVersion = null;
        string? contextHash = null;
        if (contextualCount != 0 && contextualCount != segments.Count)
            return Failure("CAD_SEMANTIC_CONTEXT_SET_INVALID", ErrorCategory.Integrity,
                "A translation cannot mix legacy and contextual segments.");
        if (contextualCount == segments.Count)
        {
            if (!string.Equals(promptTemplateVersion, ContextualPromptTemplateVersion, StringComparison.Ordinal))
                return Failure("CAD_SEMANTIC_CONTEXT_PROMPT_MISMATCH", ErrorCategory.Integrity,
                    "Hash-bound CAD semantic context requires prompt template 1.2.");
            var aggregate = CadSemanticContextBuilder.AggregateHash(segments);
            if (!aggregate.IsSuccess) return Results.Failure<TranslationReviewSnapshot>(aggregate.Error!);
            contextPolicyVersion = segments[0].SemanticContext!.Version;
            contextHash = aggregate.Value;
        }
        else if (string.Equals(promptTemplateVersion, ContextualPromptTemplateVersion, StringComparison.Ordinal))
        {
            return Failure("CAD_SEMANTIC_CONTEXT_REQUIRED", ErrorCategory.Integrity,
                "Prompt 1.2 requires a complete hash-bound CAD semantic context set.");
        }
        else if (!string.Equals(promptTemplateVersion, LegacyPromptTemplateVersion, StringComparison.Ordinal))
        {
            return Failure("TRANSLATION_PROMPT_VERSION_UNSUPPORTED", ErrorCategory.Configuration,
                "Legacy translation without semantic context requires prompt template 1.1.");
        }

        var sources = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var rows = new List<ReviewRowSnapshot>(segments.Count);
        TranslationReviewSnapshot? durable = null;
        var usedInputTokens = 0;
        var usedOutputTokens = 0;
        var usedProviderRequests = 0;
        var routingCalls = new List<TranslationCallTrace>();
        var routingSegments = new List<TranslationSegmentTrace>();
        var firstBatch = 0;

        var existing = await _reviews.LoadAsync(jobId, cancellationToken);
        if (existing.IsSuccess)
        {
            durable = existing.Value!;
            var resumed = ValidateResumeSnapshot(durable, batchValues, sources, targetLanguage, promptTemplateVersion,
                routing, contextPolicyVersion, contextHash);
            if (!resumed.IsSuccess) return Results.Failure<TranslationReviewSnapshot>(resumed.Error!);
            if (durable.CompletedBatches == durable.TotalBatches) return Results.Success(durable);
            rows.AddRange(durable.Rows);
            firstBatch = durable.CompletedBatches;
            usedInputTokens = durable.UsedInputTokens;
            usedOutputTokens = durable.UsedOutputTokens;
            usedProviderRequests = durable.UsedProviderRequests > 0 ? durable.UsedProviderRequests : durable.CompletedBatches;
            if (durable.RoutingCalls is not null) routingCalls.AddRange(durable.RoutingCalls);
            if (durable.RoutingSegments is not null) routingSegments.AddRange(durable.RoutingSegments);
        }
        else if (!string.Equals(existing.Error?.Code, "REVIEW_NOT_FOUND", StringComparison.Ordinal))
        {
            return Results.Failure<TranslationReviewSnapshot>(existing.Error!);
        }

        for (var index = firstBatch; index < batchValues.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var maximumCallsForBatch = routing?.RequestedMode == TranslationRouting.Auto ? 2 : 1;
            if (usedProviderRequests > _maxRequests - maximumCallsForBatch ||
                usedInputTokens > _maxTotalInputTokens - maximumCallsForBatch * _maxInputTokensPerRequest ||
                usedOutputTokens > _maxTotalOutputTokens - maximumCallsForBatch * _maxOutputTokensPerRequest)
                return Failure("TRANSLATION_BUDGET_EXHAUSTED", ErrorCategory.Translation, "The configured translation budget was exhausted before the next batch.");
            var payload = batchValues[index];
            var request = CreateRequest(jobId, payload);
            var response = await _translation.TranslateAsync(request, cancellationToken);
            if (!response.IsSuccess)
                return Results.Failure<TranslationReviewSnapshot>(response.Error!);

            var accepted = TranslationResultPolicy.Validate(request, response.Value!);
            if (!accepted.IsSuccess)
                return Results.Failure<TranslationReviewSnapshot>(accepted.Error!);
            var responsePayload = response.Value!.Payload?.Deserialize<TranslationBatchResponsePayload>(Json);
            if (responsePayload?.Usage is null)
                return Failure("TRANSLATION_RESPONSE_USAGE_MISSING", ErrorCategory.Contract, "The accepted translation response has no usage metadata.");
            var usage = responsePayload.Usage;
            var providerCalls = responsePayload.Routing?.Calls.Count ?? 1;
            if (usage.InputTokens < 0 || usage.OutputTokens < 0 ||
                providerCalls is < 1 or > 2 || usedProviderRequests > _maxRequests - providerCalls ||
                usedInputTokens > _maxTotalInputTokens - usage.InputTokens || usedOutputTokens > _maxTotalOutputTokens - usage.OutputTokens)
                return Failure("TRANSLATION_BUDGET_EXCEEDED", ErrorCategory.Translation, "The provider usage exceeded the configured translation budget.");
            usedInputTokens += usage.InputTokens;
            usedOutputTokens += usage.OutputTokens;
            usedProviderRequests += providerCalls;
            if (responsePayload.Routing is not null)
            {
                routingCalls.AddRange(responsePayload.Routing.Calls);
                routingSegments.AddRange(responsePayload.Routing.Segments);
            }

            rows.AddRange(accepted.Value!.Select(proposal => new ReviewRowSnapshot(
                proposal.SegmentId,
                sources[proposal.SegmentId].SourceText,
                proposal.ProposedText,
                proposal.ProposedText,
                SegmentState.Proposed,
                null,
                proposal.WarningCode,
                proposal.RiskSeverity,
                proposal.EffectiveModel,
                proposal.Escalated)));

            var snapshot = new TranslationReviewSnapshot(
                jobId,
                durable is null ? 0 : durable.Version + 1,
                targetLanguage,
                promptTemplateVersion,
                index + 1,
                batchValues.Count,
                _clock.UtcNow,
                rows.ToArray(),
                usedInputTokens,
                usedOutputTokens,
                routing?.RequestedMode,
                routing?.BaseModel,
                routing?.Version,
                routingCalls.ToArray(),
                routingSegments.ToArray(),
                usedProviderRequests,
                contextPolicyVersion,
                contextHash);
            var saved = await _reviews.SaveAsync(snapshot, cancellationToken);
            if (!saved.IsSuccess)
                return saved;
            durable = saved.Value;
        }

        return Results.Success(durable!);
    }

    private static Result<bool> ValidateResumeSnapshot(
        TranslationReviewSnapshot snapshot,
        IReadOnlyList<TranslationBatchRequestPayload> batches,
        Dictionary<string, CadTextSegment> sources,
        string targetLanguage,
        string promptTemplateVersion,
        TranslationRoutingPolicy? routing,
        string? contextPolicyVersion,
        string? contextHash)
    {
        if (!string.Equals(snapshot.TargetLanguage, targetLanguage, StringComparison.Ordinal) ||
            !string.Equals(snapshot.PromptTemplateVersion, promptTemplateVersion, StringComparison.Ordinal) ||
            !string.Equals(snapshot.RequestedMode, routing?.RequestedMode, StringComparison.Ordinal) ||
            !string.Equals(snapshot.BaseModel, routing?.BaseModel, StringComparison.Ordinal) ||
            !string.Equals(snapshot.RoutingVersion, routing?.Version, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ContextPolicyVersion, contextPolicyVersion, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ContextHash, contextHash, StringComparison.Ordinal) ||
            snapshot.ReviewAutomationReceipt is not null ||
            snapshot.TotalBatches != batches.Count || snapshot.CompletedBatches < 1 || snapshot.CompletedBatches > batches.Count ||
            snapshot.Rows.Any(row => row.State != SegmentState.Proposed || !sources.TryGetValue(row.SegmentId, out var source) ||
                                     !string.Equals(row.OriginalText, source.SourceText, StringComparison.Ordinal)))
            return Results.Failure<bool>(new ContractError("TRANSLATION_RESUME_SNAPSHOT_MISMATCH", ErrorCategory.Integrity, "The partial translation checkpoint does not match this job.", false));

        var expectedIds = batches.Take(snapshot.CompletedBatches)
            .SelectMany(batch => batch.Segments)
            .Select(segment => segment.SegmentId)
            .ToArray();
        if (snapshot.Rows.Count != expectedIds.Length || !snapshot.Rows.Select(row => row.SegmentId).SequenceEqual(expectedIds, StringComparer.Ordinal))
            return Results.Failure<bool>(new ContractError("TRANSLATION_RESUME_ROWS_MISMATCH", ErrorCategory.Integrity, "The partial translation rows do not match completed batches.", false));
        return Results.Success(true);
    }

    private WireEnvelope CreateRequest(Guid jobId, TranslationBatchRequestPayload payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, Json)!.AsObject();
        return WireEnvelope.Request(
            MessageTypes.TranslationRequest,
            jobId,
            Guid.NewGuid(),
            TranslationBatchFactory.ConfigurationHash(payload),
            _clock.UtcNow,
            node);
    }

    private static Result<TranslationReviewSnapshot> Failure(string code, ErrorCategory category, string message) =>
        Results.Failure<TranslationReviewSnapshot>(new ContractError(code, category, message, false));
}
