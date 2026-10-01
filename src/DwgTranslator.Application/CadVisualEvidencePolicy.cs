using System.Text.Json;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static class CadVisualEvidencePolicy
{
    private static readonly HashSet<string> CommonKeys = new(StringComparer.Ordinal)
    {
        "handle", "entityType", "space", "layout", "ownerRecord", "layer", "colorIndex", "linetypeHandle", "lineWeight"
    };

    private static readonly HashSet<string> TextKeys = new(CommonKeys.Concat(new[]
    {
        "position", "alignmentPoint", "height", "rotation", "widthFactor", "oblique", "textStyleHandle",
        "horizontalMode", "verticalMode", "normal", "thickness", "mirroredInX", "mirroredInY"
    }), StringComparer.Ordinal);

    private static readonly HashSet<string> MTextKeys = new(CommonKeys.Concat(new[]
    {
        "location", "textHeight", "rotation", "width", "attachment", "flowDirection", "textStyleHandle", "normal"
    }), StringComparer.Ordinal);

    public static string Fingerprint(IReadOnlyDictionary<string, string> properties)
    {
        var ordered = properties.OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return CanonicalJsonV1.Fingerprint(JsonSerializer.SerializeToElement(ordered));
    }

    public static Result<bool> Validate(CadWriteMapping mapping, CadVisualInvariantEvidence? evidence)
    {
        if (evidence is null || evidence.BeforeProperties is null || evidence.AfterProperties is null)
            return Failure("WRITE_VISUAL_EVIDENCE_MISSING", "Every applied entity requires before/after visual evidence.");
        var expectedKeys = mapping.ExpectedEntityType switch
        {
            "TEXT" => TextKeys,
            "MTEXT" => MTextKeys,
            _ => null
        };
        if (expectedKeys is null || !evidence.BeforeProperties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedKeys) ||
            !evidence.AfterProperties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedKeys))
            return Failure("WRITE_VISUAL_EVIDENCE_FIELDS_INVALID", "Visual evidence does not contain the exact property set for the entity type.");
        if (!MatchesExpectedIdentity(mapping, evidence.BeforeProperties) || !MatchesExpectedIdentity(mapping, evidence.AfterProperties))
            return Failure("WRITE_VISUAL_IDENTITY_MISMATCH", "Visual evidence does not match the extracted entity identity and location.");
        var before = Fingerprint(evidence.BeforeProperties);
        var after = Fingerprint(evidence.AfterProperties);
        if (!ContractPatterns.Sha256().IsMatch(evidence.BeforeFingerprint) || !ContractPatterns.Sha256().IsMatch(evidence.AfterFingerprint) ||
            !string.Equals(before, evidence.BeforeFingerprint, StringComparison.Ordinal) ||
            !string.Equals(after, evidence.AfterFingerprint, StringComparison.Ordinal) ||
            !evidence.InvariantMatch || !string.Equals(before, after, StringComparison.Ordinal) ||
            !evidence.BeforeProperties.OrderBy(x => x.Key).SequenceEqual(evidence.AfterProperties.OrderBy(x => x.Key)))
            return Failure("WRITE_VISUAL_INVARIANTS_CHANGED", "Placement, style or visual properties changed during translation.");
        if (string.IsNullOrWhiteSpace(evidence.BeforeExtents) || string.IsNullOrWhiteSpace(evidence.AfterExtents) || !evidence.VisualReviewRequired)
            return Failure("WRITE_VISUAL_REVIEW_EVIDENCE_INVALID", "Bounds evidence and explicit visual review are required.");
        if (evidence.BoundsChanged != !string.Equals(evidence.BeforeExtents, evidence.AfterExtents, StringComparison.Ordinal))
            return Failure("WRITE_BOUNDS_EVIDENCE_INVALID", "Bounds change classification does not match the captured extents.");
        return Results.Success(true);
    }

    private static bool MatchesExpectedIdentity(CadWriteMapping mapping, Dictionary<string, string> properties) =>
        properties.TryGetValue("handle", out var handle) && string.Equals(handle, mapping.Handle, StringComparison.Ordinal) &&
        properties.TryGetValue("entityType", out var type) && string.Equals(type, mapping.ExpectedEntityType, StringComparison.Ordinal) &&
        properties.TryGetValue("space", out var space) && string.Equals(space, mapping.ExpectedSpace, StringComparison.Ordinal) &&
        properties.TryGetValue("layout", out var layout) && string.Equals(layout, mapping.ExpectedLayout ?? "", StringComparison.Ordinal) &&
        properties.TryGetValue("layer", out var layer) && string.Equals(layer, mapping.ExpectedLayer, StringComparison.Ordinal);

    private static Result<bool> Failure(string code, string message) =>
        Results.Failure<bool>(new ContractError(code, ErrorCategory.Integrity, message, false));
}
