using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static class CadExerciseValidationReportFactory
{
    public static Result<CadExerciseValidationReport> Create(CadWriteResponsePayload response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Validation?.Policy != "VisualStrictV2" || response.Validation.VisualInvariantsValid is not true ||
            response.Applied is null || response.Applied.Count == 0)
            return Failure("CAD_EXERCISE_REPORT_INPUT_INVALID", "A successful VisualStrictV2 response is required.");

        var entities = new List<CadEntityValidationSummary>(response.Applied.Count);
        foreach (var applied in response.Applied)
        {
            var evidence = applied.VisualEvidence;
            if (evidence is null || evidence.BeforeProperties is null || !evidence.InvariantMatch || !evidence.VisualReviewRequired)
                return Failure("CAD_EXERCISE_REPORT_EVIDENCE_INVALID", "Complete per-entity visual evidence is required.");
            var properties = evidence.BeforeProperties;
            if (!Value(properties, "handle", out var handle) || !Value(properties, "entityType", out var type) ||
                !Value(properties, "space", out var space) || !Value(properties, "layout", out var layout) ||
                !Value(properties, "layer", out var layer))
                return Failure("CAD_EXERCISE_REPORT_EVIDENCE_INVALID", "Visual identity fields are missing from report evidence.");
            entities.Add(new CadEntityValidationSummary(
                applied.SegmentId, handle, type, space, string.IsNullOrEmpty(layout) ? null : layout, layer,
                evidence.AfterFingerprint, properties.Count, evidence.BoundsChanged, evidence.BeforeExtents, evidence.AfterExtents,
                "PASS_AUTOMATIC_VISUAL_REVIEW_REQUIRED"));
        }

        return Results.Success(new CadExerciseValidationReport(
            response.Validation.Policy,
            AutomaticPass: true,
            VisualReviewRequired: true,
            entities.Count,
            entities.Count(entity => entity.BoundsChanged),
            response.SourceHashAfter,
            response.CandidateHash,
            entities));
    }

    private static bool Value(Dictionary<string, string> properties, string key, out string value) =>
        properties.TryGetValue(key, out value!);

    private static Result<CadExerciseValidationReport> Failure(string code, string message) =>
        Results.Failure<CadExerciseValidationReport>(new ContractError(code, ErrorCategory.Integrity, message, false));
}
