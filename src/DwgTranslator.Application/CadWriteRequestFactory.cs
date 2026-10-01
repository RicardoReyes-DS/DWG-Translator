using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Application;

public static class CadWriteRequestFactory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Result<WireEnvelope> Create(
        Guid jobId,
        string sourcePath,
        string finalPath,
        string expectedSourceHash,
        IReadOnlyList<CadTextSegment> segments,
        TranslationReviewSnapshot review,
        DateTimeOffset sentAtUtc)
    {
        if (jobId == Guid.Empty || review is null || review.JobId != jobId || sentAtUtc.Offset != TimeSpan.Zero ||
            segments is null || segments.Count == 0 || review.Rows is null || review.Rows.Count != segments.Count)
            return Failure("WRITE_INPUT_INVALID", "Write preparation inputs are invalid.");

        if (segments.Select(segment => segment.SegmentId).Distinct(StringComparer.Ordinal).Count() != segments.Count ||
            review.Rows.Select(row => row.SegmentId).Distinct(StringComparer.Ordinal).Count() != review.Rows.Count)
            return Failure("WRITE_SEGMENT_SET_MISMATCH", "Review rows must match extracted segments exactly.");
        var segmentById = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        if (review.Rows.Any(row => !segmentById.ContainsKey(row.SegmentId)) || segmentById.Keys.Any(id => review.Rows.All(row => row.SegmentId != id)))
            return Failure("WRITE_SEGMENT_SET_MISMATCH", "Review rows must match extracted segments exactly.");

        var approved = review.Rows.Where(row => row.State == SegmentState.Approved).ToArray();
        var excluded = review.Rows.Where(row => row.State == SegmentState.Excluded).ToArray();
        if (approved.Length == 0 || approved.Length + excluded.Length != review.Rows.Count ||
            excluded.Any(row => string.IsNullOrWhiteSpace(row.ExclusionReason)))
            return Failure("APPROVAL_COVERAGE_INVALID", "Every segment must be approved or deliberately excluded, with at least one approved mapping.");

        var mappings = new List<CadWriteMapping>(approved.Length);
        foreach (var row in approved)
        {
            var segment = segmentById[row.SegmentId];
            var tokenCheck = HumanReviewPolicy.Approve(segment, new AcceptedTranslation(row.SegmentId, row.ProposedText), row.FinalText);
            if (!tokenCheck.IsSuccess)
                return Results.Failure<WireEnvelope>(tokenCheck.Error!);
            var approvedFinalText = tokenCheck.Value!.FinalText!;
            mappings.Add(new CadWriteMapping
            {
                SegmentId = row.SegmentId,
                Handle = segment.Entity.Handle,
                ExpectedSourceTextHash = segment.SourceTextHash,
                ApprovedFinalText = approvedFinalText,
                ApprovedFinalTextHash = ContentHash(approvedFinalText),
                ExpectedEntityType = segment.Entity.Type,
                ExpectedSpace = segment.Entity.Space,
                ExpectedLayout = segment.Entity.Layout,
                ExpectedLayer = segment.Entity.Layer
            });
        }

        var candidatePath = CandidatePathFor(finalPath, jobId);
        var payload = new CadWriteRequestPayload
        {
            SourcePath = sourcePath,
            ExpectedSourceHash = expectedSourceHash,
            CandidatePath = candidatePath,
            FinalPath = finalPath,
            OverwritePolicy = "FailIfExists",
            ApprovalCoverage = new CadApprovalCoverage
            {
                Selected = review.Rows.Count,
                Approved = approved.Length,
                Excluded = excluded.Length,
                Complete = true
            },
            Mappings = mappings,
            ValidationPolicy = "VisualStrictV2"
        };
        var payloadNode = JsonSerializer.SerializeToNode(payload, Json)!.AsObject();
        var idempotency = IdempotencyMaterial.Fingerprint(
            MessageTypes.WriteRequest,
            ContractV1.SchemaVersion,
            new Dictionary<string, string>(),
            new System.Text.Json.Nodes.JsonObject { ["jobId"] = jobId.ToString("D"), ["payload"] = payloadNode.DeepClone() });
        if (!idempotency.IsSuccess) return Results.Failure<WireEnvelope>(idempotency.Error!);
        var envelope = WireEnvelope.Request(MessageTypes.WriteRequest, jobId, Guid.NewGuid(), idempotency.Value!, sentAtUtc, payloadNode);
        var safety = WriteSafetyPolicy.Validate(envelope);
        return safety.IsSuccess ? Results.Success(envelope) : Results.Failure<WireEnvelope>(safety.Error!);
    }

    public static string CandidatePathFor(string finalPath, Guid jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        if (jobId == Guid.Empty) throw new ArgumentException("A non-empty job ID is required.", nameof(jobId));
        const char windowsSeparator = (char)92;
        var normalized = finalPath.Replace('/', windowsSeparator);
        var separator = normalized.LastIndexOf(windowsSeparator);
        var directory = separator >= 0 ? normalized[..(separator + 1)] : string.Empty;
        var fileName = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        var stem = fileName.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        return $"{directory}.{stem}.candidate-{jobId:N}.dwg";
    }

    private static string ContentHash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Result<WireEnvelope> Failure(string code, string message) =>
        Results.Failure<WireEnvelope>(new ContractError(code, ErrorCategory.Integrity, message, false));
}
