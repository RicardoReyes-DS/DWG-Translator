using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Application;

public static class CadReconcileRequestFactory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Result<WireEnvelope> Create(Guid jobId, string sourcePath, string orphanPath,
        string sourceHash, string orphanHash, IReadOnlyList<CadTextSegment> segments,
        TranslationReviewSnapshot review, DateTimeOffset sentAtUtc)
    {
        if (jobId == Guid.Empty || segments.Count == 0 || review.JobId != jobId ||
            review.Rows.Count != segments.Count || review.Rows.Any(row => row.State != SegmentState.Approved))
            return Failure<WireEnvelope>("RECONCILIATION_REVIEW_INVALID");
        var segmentById = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        if (segmentById.Count != segments.Count || review.Rows.Select(row => row.SegmentId).Distinct(StringComparer.Ordinal).Count() != review.Rows.Count ||
            review.Rows.Any(row => !segmentById.ContainsKey(row.SegmentId)))
            return Failure<WireEnvelope>("RECONCILIATION_SEGMENT_SET_MISMATCH");
        var mappings = review.Rows.Select(row =>
        {
            var segment = segmentById[row.SegmentId];
            return new CadWriteMapping
            {
                SegmentId = row.SegmentId,
                Handle = segment.Entity.Handle,
                ExpectedSourceTextHash = segment.SourceTextHash,
                ApprovedFinalText = row.FinalText,
                ApprovedFinalTextHash = TextHash(row.FinalText),
                ExpectedEntityType = segment.Entity.Type,
                ExpectedSpace = segment.Entity.Space,
                ExpectedLayout = segment.Entity.Layout,
                ExpectedLayer = segment.Entity.Layer
            };
        }).ToList();
        var payload = new CadReconcileRequestPayload
        {
            SourcePath = sourcePath,
            ExpectedSourceHash = sourceHash,
            OrphanPath = orphanPath,
            ExpectedOrphanHash = orphanHash,
            Mappings = mappings,
            ValidationPolicy = "VisualStrictV2"
        };
        var node = JsonSerializer.SerializeToNode(payload, Json)!.AsObject();
        var idempotency = IdempotencyMaterial.Fingerprint(MessageTypes.ReconcileRequest, ContractV1.SchemaVersion,
            new Dictionary<string, string>(), new System.Text.Json.Nodes.JsonObject
            { ["jobId"] = jobId.ToString("D"), ["payload"] = node.DeepClone() });
        return idempotency.IsSuccess
            ? Results.Success(WireEnvelope.Request(MessageTypes.ReconcileRequest, jobId, Guid.NewGuid(), idempotency.Value!, sentAtUtc, node))
            : Results.Failure<WireEnvelope>(idempotency.Error!);
    }

    private static string TextHash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Result<T> Failure<T>(string code) => Results.Failure<T>(new ContractError(code, ErrorCategory.Integrity, "Orphan reconciliation inputs are invalid.", false));
}

public static class CadReconcileResultPolicy
{
    public static Result<CadReconcileResponsePayload> Validate(WireEnvelope request, WireEnvelope response)
    {
        if (response.JobId != request.JobId || response.CorrelationId != request.CorrelationId || response.IdempotencyKey != request.IdempotencyKey)
            return Failure("RECONCILIATION_RESPONSE_CORRELATION_INVALID");
        if (response.Status == OperationStatus.Failed && response.Error is not null) return Results.Failure<CadReconcileResponsePayload>(response.Error);
        var expected = EnvelopeCodec.DecodePayload<CadReconcileRequestPayload>(request, MessageTypes.ReconcileRequest);
        var actual = EnvelopeCodec.DecodePayload<CadReconcileResponsePayload>(response, MessageTypes.ReconcileResponse);
        if (!expected.IsSuccess || !actual.IsSuccess) return Failure("RECONCILIATION_RESPONSE_INVALID");
        var e = expected.Value!; var a = actual.Value!;
        if (a.SourceHashAfter != e.ExpectedSourceHash || a.OrphanHash != e.ExpectedOrphanHash ||
            a.Validation.Policy != "VisualStrictV2" || !a.Validation.ReopenedByAutoCAD || !a.Validation.EntityMappingValid ||
            !a.Validation.GeometryInvariantsValid || !a.Validation.FormatTokenIntegrityValid || a.Validation.VisualInvariantsValid is not true ||
            a.Applied.Count != e.Mappings.Count)
            return Failure("RECONCILIATION_VALIDATION_FAILED");
        var applied = a.Applied.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        foreach (var mapping in e.Mappings)
            if (!applied.TryGetValue(mapping.SegmentId, out var item) || item.Result != "ReconciledReadOnly" ||
                item.PostWriteTextHash != mapping.ApprovedFinalTextHash || !CadVisualEvidencePolicy.Validate(mapping, item.VisualEvidence).IsSuccess)
                return Failure("RECONCILIATION_APPLIED_SET_MISMATCH");
        return Results.Success(a);
    }

    private static Result<CadReconcileResponsePayload> Failure(string code) =>
        Results.Failure<CadReconcileResponsePayload>(new ContractError(code, ErrorCategory.Integrity, "The orphan reconciliation response failed strict validation.", false));
}
