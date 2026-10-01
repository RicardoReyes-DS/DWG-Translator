using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static class CadExtractionResultPolicy
{
    public static Result<IReadOnlyList<CadTextSegment>> Validate(
        WireEnvelope response,
        string expectedSourceHash,
        string expectedDrawingFingerprint,
        string manifestBoundBasename)
    {
        if (response is null)
            return Failure("CAD_EXTRACTION_RESPONSE_NULL", ErrorCategory.Contract, "Extraction response is required.");
        if (expectedSourceHash is null || expectedDrawingFingerprint is null ||
            !ContractPatterns.Sha256().IsMatch(expectedSourceHash) ||
            !ContractPatterns.Sha256().IsMatch(expectedDrawingFingerprint) ||
            string.IsNullOrWhiteSpace(manifestBoundBasename) || manifestBoundBasename.Length > 255 ||
            !string.Equals(Path.GetFileName(manifestBoundBasename), manifestBoundBasename, StringComparison.Ordinal))
        {
            return Failure("CAD_EXTRACTION_EXPECTATION_INVALID", ErrorCategory.Contract,
                "Expected source/drawing hashes and a manifest-bound basename are required.");
        }

        if (response.Status != OperationStatus.Succeeded)
            return response.Error is not null
                ? Results.Failure<IReadOnlyList<CadTextSegment>>(response.Error)
                : Failure("CAD_EXTRACTION_RESPONSE_STATUS_INVALID", ErrorCategory.Contract, "A successful extraction response is required.");

        var decoded = EnvelopeCodec.DecodePayload<CadExtractResponsePayload>(response, MessageTypes.ExtractResponse);
        if (!decoded.IsSuccess)
            return Results.Failure<IReadOnlyList<CadTextSegment>>(decoded.Error!);
        var payload = decoded.Value!;

        if (payload.SourceHash is null || payload.Segments is null)
            return Failure("CAD_EXTRACTION_PAYLOAD_INVALID", ErrorCategory.Contract, "Extraction payload contains null required values.");
        if (!string.Equals(payload.SourceHash, expectedSourceHash, StringComparison.Ordinal))
            return Failure("SOURCE_CHANGED", ErrorCategory.Integrity, "Extraction source hash differs from the inspected source.");
        if (payload.ExcludedFieldCount < 0 || payload.Segments.Count > 100_000)
            return Failure("CAD_EXTRACTION_COUNTS_INVALID", ErrorCategory.Contract, "Extraction counts are outside contract limits.");

        var segmentIds = new HashSet<string>(StringComparer.Ordinal);
        var handles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in payload.Segments)
        {
            if (segment is null)
                return Failure("CAD_EXTRACTION_PAYLOAD_INVALID", ErrorCategory.Contract, "Extraction contains a null segment.");
            var validation = ValidateSegment(segment, expectedSourceHash, expectedDrawingFingerprint, segmentIds, handles);
            if (!validation.IsSuccess)
                return Results.Failure<IReadOnlyList<CadTextSegment>>(validation.Error!);
        }

        var contextualCount = payload.Segments.Count(segment => segment.SemanticContext is not null);
        if (contextualCount != 0 && contextualCount != payload.Segments.Count)
            return Failure("CAD_SEMANTIC_CONTEXT_PARTIAL", ErrorCategory.Integrity, "New semantic context must cover the complete extraction set.");
        if (contextualCount != 0)
        {
            var aggregate = CadSemanticContextBuilder.AggregateHash(payload.Segments);
            if (!aggregate.IsSuccess)
                return Results.Failure<IReadOnlyList<CadTextSegment>>(aggregate.Error!);
            if (payload.Segments.Any(segment => segment.SemanticContext is
                {
                    Version: CadSemanticContextBuilder.PolicyVersionOneTwo,
                    DisciplineResolution: CadSemanticContextBuilder.DrawingNameArchitecturalFallback
                }) &&
                !CadSemanticContextBuilder.IsArchitecturalManifestBasename(manifestBoundBasename))
                return Failure("CAD_SEMANTIC_CONTEXT_MANIFEST_BINDING_MISMATCH", ErrorCategory.Integrity,
                    "Architectural drawing-name fallback requires the delimited ARQ token in the manifest-bound basename.");
        }

        return Results.Success<IReadOnlyList<CadTextSegment>>(payload.Segments.AsReadOnly());
    }

    private static Result<bool> ValidateSegment(
        CadTextSegment segment,
        string sourceHash,
        string drawingFingerprint,
        HashSet<string> segmentIds,
        HashSet<string> handles)
    {
        if (segment.Entity is null || segment.Entity.BlockPath is null || segment.ProtectedTokens is null ||
            segment.SegmentId is null || segment.SourceText is null || segment.SourceTextHash is null ||
            segment.LineBreakStyle is null || segment.FieldClassification is null || segment.State is null ||
            segment.Entity.Type is null || segment.Entity.Handle is null || segment.Entity.Space is null || segment.Entity.Layer is null)
        {
            return Failure<bool>("CAD_EXTRACTION_PAYLOAD_INVALID", ErrorCategory.Contract, "Segment contains null required values.");
        }
        if (segment.Entity.BlockPath.Count != 0 ||
            segment.FieldClassification != "None" ||
            segment.State != "Extracted" ||
            string.IsNullOrWhiteSpace(segment.SourceText))
        {
            return Failure<bool>("CAD_SEGMENT_SCOPE_INVALID", ErrorCategory.Unsupported, "Only non-empty direct, non-field TEXT/MTEXT segments are accepted.");
        }

        var identity = CadSegmentIdentityV1.Create(
            sourceHash,
            drawingFingerprint,
            new CadSegmentAddress(
                segment.Entity.Type,
                segment.Entity.Handle,
                segment.Entity.Space,
                segment.Entity.Layout,
                segment.Entity.Layer,
                segment.Entity.SubIndex),
            segment.SourceText);
        if (!identity.IsSuccess)
            return Results.Failure<bool>(identity.Error!);
        if (!string.Equals(segment.SegmentId, identity.Value!.SegmentId, StringComparison.Ordinal))
            return Failure<bool>("CAD_SEGMENT_ID_MISMATCH", ErrorCategory.Integrity, "Segment ID differs from the deterministic v1 identity.");
        if (!string.Equals(segment.SourceTextHash, identity.Value.SourceTextHash, StringComparison.Ordinal))
            return Failure<bool>("CAD_SOURCE_TEXT_HASH_MISMATCH", ErrorCategory.Integrity, "Source text hash differs from the exact UTF-8 text.");
        if (!segmentIds.Add(segment.SegmentId) || !handles.Add(segment.Entity.Handle))
            return Failure<bool>("CAD_SEGMENT_DUPLICATE", ErrorCategory.Integrity, "Segment IDs and direct entity handles must be unique.");

        var expectedLineBreakStyle = ExpectedLineBreakStyle(segment.Entity.Type, segment.SourceText);
        if (!string.Equals(segment.LineBreakStyle, expectedLineBreakStyle, StringComparison.Ordinal))
            return Failure<bool>("CAD_LINE_BREAK_STYLE_MISMATCH", ErrorCategory.Integrity, "lineBreakStyle differs from the exact extracted content.");

        if (segment.ProtectedTokens.Count > 1_000)
            return Failure<bool>("CAD_PROTECTED_TOKEN_INVALID", ErrorCategory.Contract, "Protected token count exceeds the contract limit.");

        var tokenOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < segment.ProtectedTokens.Count; index++)
        {
            var token = segment.ProtectedTokens[index];
            if (token is null || token.Ordinal != index || string.IsNullOrEmpty(token.Token) || token.Token.Length > 1_000 ||
                token.Kind is not ("Placeholder" or "CadFormatControl" or "NonTranslatable"))
            {
                return Failure<bool>("CAD_PROTECTED_TOKEN_INVALID", ErrorCategory.Integrity, "Protected tokens require contiguous ordinals and supported non-field kinds.");
            }

            tokenOccurrences[token.Token] = tokenOccurrences.TryGetValue(token.Token, out var count) ? count + 1 : 1;
        }

        if (tokenOccurrences.Any(item => CountOccurrences(segment.SourceText, item.Key) < item.Value))
            return Failure<bool>("CAD_PROTECTED_TOKEN_NOT_FOUND", ErrorCategory.Integrity, "Every protected token must occur in the exact source text.");

        var expectedTokens = CadProtectedTokenPolicy.Extract(segment.SourceText);
        if (expectedTokens.Count != segment.ProtectedTokens.Count || expectedTokens.Where((expected, index) =>
                expected.Token != segment.ProtectedTokens[index].Token || expected.Kind != segment.ProtectedTokens[index].Kind ||
                expected.Ordinal != segment.ProtectedTokens[index].Ordinal).Any())
            return Failure<bool>("CAD_PROTECTED_TOKEN_SET_MISMATCH", ErrorCategory.Integrity, "Protected tokens must be the complete canonical set extracted from the exact source text.");

        return Results.Success(true);
    }

    public static string ExpectedLineBreakStyle(string entityType, string sourceText)
    {
        if (entityType == "TEXT")
            return "None";

        var hasParagraph = sourceText.Contains(@"\P", StringComparison.Ordinal);
        var hasTextNewline = sourceText.Contains('\n') || sourceText.Contains('\r');
        return (hasParagraph, hasTextNewline) switch
        {
            (true, true) => "Mixed",
            (true, false) => "MTextParagraph",
            (false, true) => "TextNewline",
            _ => "None"
        };
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private static Result<IReadOnlyList<CadTextSegment>> Failure(string code, ErrorCategory category, string message) =>
        Results.Failure<IReadOnlyList<CadTextSegment>>(new ContractError(code, category, message, false));

    private static Result<T> Failure<T>(string code, ErrorCategory category, string message) =>
        Results.Failure<T>(new ContractError(code, category, message, false));
}
