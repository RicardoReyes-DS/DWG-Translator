using System.Security.Cryptography;
using System.Text;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Application;

public sealed record AcceptedTranslation(
    string SegmentId,
    string ProposedText,
    string? WarningCode = null,
    string RiskSeverity = "none",
    string? EffectiveModel = null,
    bool Escalated = false);

public static class TranslationResultPolicy
{
    public static Result<TranslationBatchRequestPayload> ValidateRequest(WireEnvelope request)
    {
        var decoded = EnvelopeCodec.DecodePayload<TranslationBatchRequestPayload>(request, MessageTypes.TranslationRequest);
        if (!decoded.IsSuccess) return decoded;
        return IsRequestValid(decoded.Value!)
            ? decoded
            : Results.Failure<TranslationBatchRequestPayload>(new ContractError("TRANSLATION_REQUEST_INVALID", ErrorCategory.Translation, "Translation request semantics are invalid.", false));
    }

    public static Result<IReadOnlyList<AcceptedTranslation>> Validate(WireEnvelope request, WireEnvelope response)
    {
        var requestPayload = ValidateRequest(request);
        if (!requestPayload.IsSuccess) return Results.Failure<IReadOnlyList<AcceptedTranslation>>(requestPayload.Error!);
        var responsePayload = EnvelopeCodec.DecodePayload<TranslationBatchResponsePayload>(response, MessageTypes.TranslationResponse);
        if (!responsePayload.IsSuccess) return Results.Failure<IReadOnlyList<AcceptedTranslation>>(responsePayload.Error!);
        if (response.Status != OperationStatus.Succeeded || response.JobId != request.JobId || response.CorrelationId != request.CorrelationId ||
            !string.Equals(response.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
            return Failure("TRANSLATION_RESPONSE_CORRELATION_INVALID", "Translation response does not match its request.");

        var input = requestPayload.Value!;
        var output = responsePayload.Value!;
        if (string.IsNullOrWhiteSpace(output.Model) || output.Model.Length > 120 ||
            !string.Equals(output.PromptTemplateVersion, input.PromptTemplateVersion, StringComparison.Ordinal) ||
            output.Usage is null || output.Usage.InputTokens < 0 || output.Usage.OutputTokens < 0 || output.Proposals is null || output.Proposals.Count != input.Segments.Count)
            return Failure("TRANSLATION_RESPONSE_INVALID", "Translation response metadata or cardinality is invalid.");

        Dictionary<string, TranslationSegmentTrace>? routedTraces = null;
        Dictionary<string, TranslationRiskAssessment>? routedRisk = null;
        if (input.Routing is not null)
        {
            var policy = TranslationRoutingPolicyFactory.Validate(input.Routing);
            if (!policy.IsSuccess || output.Routing is null ||
                output.Routing.RequestedMode != input.Routing.RequestedMode || output.Routing.BaseModel != input.Routing.BaseModel ||
                output.Routing.RoutingVersion != input.Routing.Version || output.Routing.Calls is null || output.Routing.Segments is null ||
                output.Routing.Calls.Sum(call => call.Usage.InputTokens) != output.Usage.InputTokens ||
                output.Routing.Calls.Sum(call => call.Usage.OutputTokens) != output.Usage.OutputTokens ||
                output.Routing.EscalatedSegmentCount != output.Routing.Segments.Count(trace => trace.Escalated) ||
                output.Routing.Calls.Any(call => call.RequestedModel == "gpt-5.6" || call.LatencyMilliseconds < 0 ||
                    call.PromptVersion != input.PromptTemplateVersion || call.SchemaVersion != TranslationRouting.SchemaVersion ||
                    call.Usage.InputTokens < 0 || call.Usage.OutputTokens < 0 || call.RequestId is { Length: > 128 } ||
                    call.RequestId is not null && call.RequestId.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')) ||
                    call.Outcome is not ("succeeded" or "failed") || call.Outcome == "failed" &&
                        (string.IsNullOrWhiteSpace(call.ErrorCode) || call.ErrorCode.Length > 120 || call.ErrorCode.Any(character => !char.IsAsciiLetterUpper(character) && !char.IsDigit(character) && character != '_'))) ||
                output.Routing.Segments.Count != input.Segments.Count ||
                output.Routing.Segments.Select(item => item.SegmentId).Distinct(StringComparer.Ordinal).Count() != input.Segments.Count)
                return Failure("TRANSLATION_ROUTING_TRACE_INVALID", "The routing trace is incomplete or inconsistent.");
            routedTraces = output.Routing.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
            if (input.Segments.Any(segment => !routedTraces.ContainsKey(segment.SegmentId)) ||
                routedTraces.Values.Any(trace => trace.RoutingVersion != input.Routing.Version || trace.BaseModel != input.Routing.BaseModel ||
                    trace.RequestedMode != input.Routing.RequestedMode || trace.RiskSeverity is not ("none" or "low" or "medium" or "high") ||
                    string.IsNullOrWhiteSpace(trace.EffectiveModel) || trace.EffectiveModel == "gpt-5.6" || trace.EscalationReasonCodes is null))
                return Failure("TRANSLATION_ROUTING_TRACE_INVALID", "The segment routing trace is incomplete or inconsistent.");
            routedRisk = TranslationRiskEvaluator.Evaluate(input, output.Proposals, input.Routing).ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
            if (routedRisk.Any(item => routedTraces[item.Key].RiskSeverity != item.Value.RiskSeverity ||
                                       routedTraces[item.Key].ValidatorResult != (item.Value.ReasonCodes.Count == 0 ? "pass" : "review")))
                return Failure("TRANSLATION_ROUTING_VALIDATOR_MISMATCH", "The persisted routing trace does not match deterministic validation.");
        }
        else if (output.Routing is not null)
        {
            return Failure("TRANSLATION_ROUTING_TRACE_UNEXPECTED", "A legacy request cannot acquire a routing policy implicitly.");
        }

        if (output.Proposals.Any(item => item is null || string.IsNullOrWhiteSpace(item.SegmentId)) ||
            output.Proposals.Select(item => item.SegmentId).Distinct(StringComparer.Ordinal).Count() != output.Proposals.Count)
            return Failure("TRANSLATION_SEGMENT_SET_MISMATCH", "Translation proposals must contain every requested segment exactly once.");
        var proposals = output.Proposals.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        if (proposals.Keys.Any(id => !input.Segments.Any(segment => segment.SegmentId == id)))
            return Failure("TRANSLATION_SEGMENT_SET_MISMATCH", "Translation proposals must contain every requested segment exactly once.");

        var accepted = new List<AcceptedTranslation>(input.Segments.Count);
        foreach (var segment in input.Segments)
        {
            var proposal = proposals[segment.SegmentId];
            if (proposal.TranslatedTextWithTokenAliases is null || proposal.TranslatedTextWithTokenAliases.Length > 65_535)
                return Failure("TOKEN_INTEGRITY_FAILED", "The proposal did not declare valid protected-token integrity.");
            var restored = TranslationTokenAliases.Restore(
                proposal.TranslatedTextWithTokenAliases,
                segment.ProtectedTokenAliases,
                segment.TextWithTokenAliases);
            if (!string.Equals(proposal.TokenIntegrity, "Valid", StringComparison.Ordinal) || !restored.IsSuccess)
            {
                var fallback = TokenIntegrityFallback(input, output, segment, routedTraces, routedRisk);
                if (fallback is null)
                    return !restored.IsSuccess
                        ? Results.Failure<IReadOnlyList<AcceptedTranslation>>(restored.Error!)
                        : Failure("TOKEN_INTEGRITY_FAILED", "The proposal did not declare valid protected-token integrity.");
                accepted.Add(fallback);
                continue;
            }
            if (routedTraces is null && !GlossaryIntegrityValid(input.Glossary, segment.TextWithTokenAliases, restored.Value!))
                return Failure("GLOSSARY_INTEGRITY_FAILED", "The proposal did not apply every mandatory glossary occurrence.");
            var trace = routedTraces?[segment.SegmentId];
            var risk = routedRisk?[segment.SegmentId];
            var warning = risk is null || risk.ReasonCodes.Count == 0 ? null : $"{risk.RiskSeverity}:{string.Join(',', risk.ReasonCodes)}";
            accepted.Add(new AcceptedTranslation(segment.SegmentId, restored.Value!, warning, risk?.RiskSeverity ?? "none", trace?.EffectiveModel, trace?.Escalated ?? false));
        }
        return Results.Success<IReadOnlyList<AcceptedTranslation>>(accepted);
    }

    private static AcceptedTranslation? TokenIntegrityFallback(
        TranslationBatchRequestPayload input,
        TranslationBatchResponsePayload output,
        TranslationSegment segment,
        Dictionary<string, TranslationSegmentTrace>? routedTraces,
        Dictionary<string, TranslationRiskAssessment>? routedRisk)
    {
        if (input.Routing?.RequestedMode != TranslationRouting.Auto || output.Routing is null ||
            routedTraces is null || routedRisk is null ||
            !routedTraces.TryGetValue(segment.SegmentId, out var trace) ||
            !routedRisk.TryGetValue(segment.SegmentId, out var risk) ||
            !trace.Escalated || trace.RiskSeverity != "high" || trace.ValidatorResult != "review" ||
            !trace.EscalationReasonCodes.Contains("PROTECTED_TOKEN_CHANGED", StringComparer.Ordinal) ||
            risk.RiskSeverity != "high" || !risk.ReasonCodes.Contains("PROTECTED_TOKEN_CHANGED", StringComparer.Ordinal) ||
            !output.Routing.Calls.Any(call =>
                string.Equals(call.Tier, "escalation", StringComparison.Ordinal) &&
                string.Equals(call.RequestedModel, input.Routing.EscalationModel, StringComparison.Ordinal)))
            return null;

        // The provider target is intentionally discarded. Rebuild the exact source solely from
        // the already validated request so the durable row remains write-safe and visibly high-risk.
        var source = TranslationTokenAliases.Restore(
            segment.TextWithTokenAliases,
            segment.ProtectedTokenAliases,
            segment.TextWithTokenAliases);
        return source.IsSuccess
            ? new AcceptedTranslation(
                segment.SegmentId,
                source.Value!,
                "high:TOKEN_INTEGRITY_FALLBACK",
                "high",
                trace.EffectiveModel,
                true)
            : null;
    }

    private static bool IsRequestValid(TranslationBatchRequestPayload value)
    {
        if (!LanguageTag.Create(value.TargetLanguage).IsSuccess || (value.SourceLanguage is not null && !LanguageTag.Create(value.SourceLanguage).IsSuccess) ||
            string.IsNullOrWhiteSpace(value.PromptTemplateVersion) || value.PromptTemplateVersion.Length > 120 || value.Glossary is null || value.Glossary.Count > 10_000 ||
            value.Segments is null || value.Segments.Count is < 1 or > 1_000 ||
            (value.Routing is not null && !TranslationRoutingPolicyFactory.Validate(value.Routing).IsSuccess) ||
            (value.Escalation is not null && (value.Routing?.RequestedMode != TranslationRouting.Auto || value.Escalation.Segments is null ||
                value.Escalation.Segments.Count != value.Segments.Count || value.Escalation.Segments.Any(item =>
                    string.IsNullOrWhiteSpace(item.SegmentId) || item.BaseProposalWithTokenAliases is null || item.ReasonCodes is null || item.ReasonCodes.Count == 0))))
            return false;
        if (value.Glossary.Any(item => string.IsNullOrEmpty(item.Source) || item.Source.Length > 500 || string.IsNullOrEmpty(item.Target) || item.Target.Length > 500) ||
            value.Glossary.Select(item => item.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Glossary.Count ||
            value.Segments.Any(segment => segment is null || !ValidBaseSegment(segment)) ||
            value.Segments.Select(segment => segment.SegmentId).Distinct(StringComparer.Ordinal).Count() != value.Segments.Count)
            return false;

        var sources = new Dictionary<string, RequestSource>(value.Segments.Count, StringComparer.Ordinal);
        foreach (var segment in value.Segments)
        {
            var restored = TranslationTokenAliases.Restore(
                segment.TextWithTokenAliases,
                segment.ProtectedTokenAliases,
                segment.TextWithTokenAliases);
            if (!restored.IsSuccess) return false;
            sources.Add(segment.SegmentId, new RequestSource(segment, restored.Value!, SourceHash(restored.Value!)));
        }

        var contextualCount = value.Segments.Count(segment => HasSemanticContext(segment.Context));
        if (string.Equals(value.PromptTemplateVersion, TranslationReviewWorkflow.LegacyPromptTemplateVersion, StringComparison.Ordinal))
            return contextualCount == 0;
        if (!string.Equals(value.PromptTemplateVersion, TranslationReviewWorkflow.ContextualPromptTemplateVersion, StringComparison.Ordinal) ||
            contextualCount != value.Segments.Count ||
            value.Segments.Select(segment => segment.Context.ContextHash).Distinct(StringComparer.Ordinal).Count() != value.Segments.Count)
            return false;
        return value.Segments.All(segment => ValidSemanticContext(segment, sources));
    }

    private static bool ValidBaseSegment(TranslationSegment segment) =>
        ContractPatterns.SegmentId().IsMatch(segment.SegmentId ?? string.Empty) &&
        segment.TextWithTokenAliases is { Length: <= 65_535 } &&
        segment.ProtectedTokenAliases is { Count: <= 1_000 } &&
        segment.ProtectedTokenAliases.All(item => item.Key is { Length: >= 2 and <= 16 } && item.Key[0] == 'T' &&
            item.Key.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 && item.Value is { Length: >= 1 and <= 1_000 }) &&
        segment.Context is not null && segment.Context.BlockPath is { Count: 0 } &&
        segment.Context.EntityType is "TEXT" or "MTEXT" && segment.Context.Space is "ModelSpace" or "PaperSpace" &&
        segment.Context.Layer is { Length: >= 1 and <= 255 } && !string.IsNullOrWhiteSpace(segment.Context.Layer) &&
        ((segment.Context.Space == "ModelSpace" && segment.Context.Layout is null) ||
         (segment.Context.Space == "PaperSpace" && segment.Context.Layout is { Length: >= 1 and <= 255 } && !string.IsNullOrWhiteSpace(segment.Context.Layout)));

    private static bool HasSemanticContext(TranslationSegmentContext context) =>
        context.ContextVersion is not null || context.ContextHash is not null || context.SemanticKey is not null ||
        context.SheetRole is not null || context.Discipline is not null || context.DisciplineConflict is not null ||
        context.DisciplineResolution is not null ||
        context.XBand is not null || context.YBand is not null || context.Signals is not null || context.NeighborExcerpts is not null;

    private static bool ValidSemanticContext(TranslationSegment segment, Dictionary<string, RequestSource> sources)
    {
        var context = segment.Context;
        if (!CadSemanticContextBuilder.IsSupportedPolicyVersion(context.ContextVersion) ||
            !ContractPatterns.Sha256().IsMatch(context.ContextHash ?? string.Empty) ||
            !ContractPatterns.Sha256().IsMatch(context.SemanticKey ?? string.Empty) ||
            context.SheetRole != (context.Space == "PaperSpace" ? "Sheet" : "Model") ||
            context.Discipline is not ("Unknown" or "Architectural" or "Structural" or "Mechanical" or "Electrical" or "Plumbing" or "Fire" or "Controls") ||
            (context.Discipline == "Structural" && context.ContextVersion != CadSemanticContextBuilder.PolicyVersionOneThree) ||
            context.DisciplineConflict is null || context.DisciplineConflict == true && context.Discipline != "Unknown" ||
            (context.ContextVersion == CadSemanticContextBuilder.PolicyVersionOneZero && context.DisciplineResolution is not null) ||
            (context.ContextVersion == CadSemanticContextBuilder.PolicyVersionOneOne &&
                context.DisciplineResolution is not (null or CadSemanticContextBuilder.LocalEvidenceOverridesDrawingHint)) ||
            (context.ContextVersion == CadSemanticContextBuilder.PolicyVersionOneTwo &&
                context.DisciplineResolution is not (null or CadSemanticContextBuilder.LocalEvidenceOverridesDrawingHint or
                    CadSemanticContextBuilder.DrawingNameArchitecturalFallback)) ||
            (context.ContextVersion == CadSemanticContextBuilder.PolicyVersionOneThree &&
                context.DisciplineResolution is not (null or CadSemanticContextBuilder.LocalEvidenceOverridesDrawingHint or
                    CadSemanticContextBuilder.DrawingNameArchitecturalFallback or
                    CadSemanticContextBuilder.DrawingNameElectricalFallback or
                    CadSemanticContextBuilder.DrawingNameStructuralFallback)) ||
            (context.DisciplineResolution is not null &&
                (context.DisciplineConflict == true || context.Discipline is "Unknown" or "Controls")) ||
            (context.DisciplineResolution == CadSemanticContextBuilder.DrawingNameArchitecturalFallback &&
                context.Discipline != "Architectural") ||
            (context.DisciplineResolution == CadSemanticContextBuilder.DrawingNameElectricalFallback &&
                context.Discipline != "Electrical") ||
            (context.DisciplineResolution == CadSemanticContextBuilder.DrawingNameStructuralFallback &&
                context.Discipline != "Structural") ||
            context.XBand is null or < 0 or > 15 || context.YBand is null or < 0 or > 15 ||
            context.Signals is null || context.Signals.Count > 8 ||
            context.Signals.Any(signal => signal is not ("CEILING_CONTEXT" or "RAISED_FLOOR_CONTEXT" or "ROOF_CONTEXT" or "VERTICAL_LEVEL_CONTEXT_CONFLICT")) ||
            context.Signals.Distinct(StringComparer.Ordinal).Count() != context.Signals.Count ||
            !context.Signals.SequenceEqual(context.Signals.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            context.NeighborExcerpts is null || context.NeighborExcerpts.Count > TranslationBatchFactory.MaximumNeighborExcerpts ||
            context.NeighborExcerpts.Select(excerpt => excerpt.SegmentId).Distinct(StringComparer.Ordinal).Count() != context.NeighborExcerpts.Count)
            return false;

        var totalScalars = 0;
        foreach (var excerpt in context.NeighborExcerpts)
        {
            if (excerpt is null || excerpt.SegmentId is null || excerpt.SegmentId == segment.SegmentId ||
                !ContractPatterns.SegmentId().IsMatch(excerpt.SegmentId) ||
                !ContractPatterns.Sha256().IsMatch(excerpt.SourceTextHash ?? string.Empty) ||
                excerpt.EntityType is not ("TEXT" or "MTEXT") ||
                excerpt.Relation is not ("SamePosition" or "Above" or "Below" or "Left" or "Right") ||
                excerpt.DistanceBand is < 0 or > 3 || excerpt.Text is null ||
                !sources.TryGetValue(excerpt.SegmentId, out var source) ||
                !string.Equals(excerpt.SourceTextHash, source.SourceHash, StringComparison.Ordinal) ||
                !string.Equals(excerpt.EntityType, source.Segment.Context.EntityType, StringComparison.Ordinal) ||
                excerpt.SameLayer != string.Equals(context.Layer, source.Segment.Context.Layer, StringComparison.Ordinal) ||
                !string.Equals(context.Space, source.Segment.Context.Space, StringComparison.Ordinal) ||
                !string.Equals(context.Layout, source.Segment.Context.Layout, StringComparison.Ordinal) ||
                !source.SourceText.StartsWith(excerpt.Text, StringComparison.Ordinal))
                return false;
            var scalars = excerpt.Text.EnumerateRunes().Count();
            if (scalars is < 1 or > TranslationBatchFactory.MaximumNeighborExcerptScalars) return false;
            totalScalars += scalars;
            if (totalScalars > TranslationBatchFactory.MaximumNeighborExcerptScalarsPerSegment) return false;
            if (excerpt.Text.Length != source.SourceText.Length &&
                !TranslationBatchFactory.SafeProtectedTokenBoundary(source.SourceText, excerpt.Text.Length,
                    CadProtectedTokenPolicy.Extract(source.SourceText)))
                return false;
        }
        return true;
    }

    private static string SourceHash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record RequestSource(TranslationSegment Segment, string SourceText, string SourceHash);

    private static bool GlossaryIntegrityValid(IReadOnlyList<TranslationGlossaryEntry> glossary, string source, string translated)
    {
        foreach (var entry in glossary)
        {
            var comparison = entry.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var required = CountOccurrences(source, entry.Source, comparison);
            if (required > 0 && CountOccurrences(translated, entry.Target, comparison) < required) return false;
        }
        return true;
    }

    private static int CountOccurrences(string value, string term, StringComparison comparison)
    {
        var count = 0;
        for (var index = 0; index <= value.Length - term.Length;)
        {
            var found = value.IndexOf(term, index, comparison);
            if (found < 0) break;
            count++;
            index = found + term.Length;
        }
        return count;
    }

    private static Result<IReadOnlyList<AcceptedTranslation>> Failure(string code, string message) =>
        Results.Failure<IReadOnlyList<AcceptedTranslation>>(new ContractError(code, ErrorCategory.Translation, message, false));
}
