using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static class CadWriteResultPolicy
{
    public static Result<CadWriteResponsePayload> Validate(WireEnvelope request, WireEnvelope response)
    {
        var safeRequest = WriteSafetyPolicy.Validate(request);
        if (!safeRequest.IsSuccess) return Results.Failure<CadWriteResponsePayload>(safeRequest.Error!);
        if (response is null || response.JobId != request.JobId ||
            response.CorrelationId != request.CorrelationId || !string.Equals(response.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
            return Failure("WRITE_RESPONSE_CORRELATION_INVALID", "Write response does not match its request.");
        if (response.Status == OperationStatus.Failed && response.Error is not null)
            return Results.Failure<CadWriteResponsePayload>(response.Error);
        if (response.Status != OperationStatus.Succeeded)
            return Failure("WRITE_RESPONSE_STATUS_INVALID", "Write response has an invalid terminal status.");

        var requestPayload = EnvelopeCodec.DecodePayload<CadWriteRequestPayload>(request, MessageTypes.WriteRequest);
        var responsePayload = EnvelopeCodec.DecodePayload<CadWriteResponsePayload>(response, MessageTypes.WriteResponse);
        if (!requestPayload.IsSuccess) return Results.Failure<CadWriteResponsePayload>(requestPayload.Error!);
        if (!responsePayload.IsSuccess) return Results.Failure<CadWriteResponsePayload>(responsePayload.Error!);
        var expected = requestPayload.Value!;
        var actual = responsePayload.Value!;

        if (!ContractPatterns.Sha256().IsMatch(actual.CandidateHash) || !string.Equals(actual.SourceHashAfter, expected.ExpectedSourceHash, StringComparison.Ordinal))
            return Failure("WRITE_HASH_INTEGRITY_FAILED", "Candidate or source hash is invalid after writing.");
        if (actual.Validation is null || !actual.Validation.ReopenedByAutoCAD || !actual.Validation.EntityMappingValid ||
            !actual.Validation.GeometryInvariantsValid || !actual.Validation.FormatTokenIntegrityValid ||
            actual.Validation.VisualInvariantsValid is not true || actual.Validation.Policy != "VisualStrictV2")
            return Failure("WRITE_VALIDATION_FAILED", "The candidate did not pass strict AutoCAD validation.");
        if (actual.Promotion is null || !actual.Promotion.Performed || !SameWindowsPath(actual.Promotion.FinalPath, expected.FinalPath))
            return Failure("WRITE_PROMOTION_INVALID", "The validated candidate was not promoted to the requested destination.");
        if (actual.Applied is null || actual.Applied.Count != expected.Mappings.Count ||
            actual.Applied.Select(item => item.SegmentId).Distinct(StringComparer.Ordinal).Count() != actual.Applied.Count)
            return Failure("WRITE_APPLIED_SET_MISMATCH", "Applied mappings do not match the approved mappings.");

        var applied = actual.Applied.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        foreach (var mapping in expected.Mappings)
        {
            if (!applied.TryGetValue(mapping.SegmentId, out var result) || result is null || result.Result != "WriteSucceeded" ||
                !string.Equals(result.PostWriteTextHash, mapping.ApprovedFinalTextHash, StringComparison.Ordinal))
                return Failure("WRITE_APPLIED_SET_MISMATCH", "Every approved mapping must succeed with the expected final text hash.");
            var visual = CadVisualEvidencePolicy.Validate(mapping, result.VisualEvidence);
            if (!visual.IsSuccess) return Results.Failure<CadWriteResponsePayload>(visual.Error!);
        }

        return Results.Success(actual);
    }

    private static bool SameWindowsPath(string? left, string? right)
    {
        const char windowsSeparator = (char)92;
        return left is not null && right is not null && string.Equals(
            left.Replace('/', windowsSeparator).TrimEnd(windowsSeparator),
            right.Replace('/', windowsSeparator).TrimEnd(windowsSeparator),
            StringComparison.OrdinalIgnoreCase);
    }

    private static Result<CadWriteResponsePayload> Failure(string code, string message) =>
        Results.Failure<CadWriteResponsePayload>(new ContractError(code, ErrorCategory.Integrity, message, false));
}
