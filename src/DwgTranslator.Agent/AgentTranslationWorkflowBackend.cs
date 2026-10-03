using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Agent;

public sealed class AgentTranslationWorkflowBackend(
    DwgTranslationJobCoordinator coordinator,
    IJobStore jobs,
    ITranslationReviewStore reviews,
    IClock clock,
    string? workspaceRoot = null) : IAgentTranslationWorkflowBackend
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string? _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? null : Path.GetFullPath(workspaceRoot);
    internal CadBootstrapTrustFailureReconciliationPolicy BootstrapTrustPolicy { get; init; } =
        CadBootstrapTrustFailureReconciliationPolicy.Instance;

    public Task<Result<JobDocument>> CreateAsync(
        DwgTranslationJobSpecification specification,
        CancellationToken cancellationToken) => coordinator.CreateAsync(specification, cancellationToken);

    public async Task<Result<JobDocument>> PrepareAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var extracted = await coordinator.InspectAndExtractAsync(jobId, cancellationToken).ConfigureAwait(false);
        return await ContinueAfterExtractionAsync(extracted,
            () => coordinator.TranslateAsync(jobId, cancellationToken)).ConfigureAwait(false);
    }

    internal static async Task<Result<JobDocument>> ContinueAfterExtractionAsync(
        Result<JobDocument> extracted,
        Func<Task<Result<JobDocument>>> translate)
    {
        if (!extracted.IsSuccess) return extracted;
        if (extracted.Value!.State == JobState.Extracted)
            return await translate().ConfigureAwait(false);
        if (extracted.Value.State == JobState.Failed && extracted.Value.Data["failure"] is JsonObject failure)
        {
            var code = failure["code"]?.GetValue<string>();
            var retryable = failure["retryable"]?.GetValue<bool>() ?? false;
            var categoryValue = failure["category"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(code) &&
                Enum.TryParse<ErrorCategory>(categoryValue, ignoreCase: false, out var category) &&
                Enum.IsDefined(category))
            {
                var diagnosticId = failure["diagnosticId"]?.GetValue<string>();
                var diagnostics = failure["invariantDiagnostics"]?.Deserialize<CadInvariantDiagnostics>(Json);
                var technicalStage = StringValue(failure, "technicalStage");
                var nativeErrorStatus = StringValue(failure, "nativeErrorStatus");
                return Results.Failure<JobDocument>(new ContractError(code, category,
                    "CAD preparation failed before translation.", retryable,
                    DiagnosticId: diagnosticId, InvariantDiagnostics: diagnostics,
                    TechnicalStage: technicalStage, NativeErrorStatus: nativeErrorStatus));
            }
        }
        return Failure("JOB_STATE_CONFLICT", "CAD preparation did not reach the Extracted state.");
    }

    public Task<Result<JobDocument>> ResumeTranslationAsync(Guid jobId, long expectedJobVersion,
        ReviewAutomationScopeTransition? scopeTransition, CancellationToken cancellationToken) =>
        coordinator.ResumeTranslationAsync(jobId, expectedJobVersion, scopeTransition, cancellationToken);

    public Task<Result<JobDocument>> RebindReviewAutomationScopeAsync(Guid jobId, long expectedJobVersion,
        ReviewAutomationScopeTransition scopeTransition, CancellationToken cancellationToken) =>
        coordinator.RebindReviewAutomationScopeAsync(jobId, expectedJobVersion, scopeTransition, cancellationToken);

    public Task<Result<JobDocument>> RevalidateApprovedContextAsync(Guid jobId, long expectedJobVersion,
        ApprovedContextRevalidationTransition transition, CancellationToken cancellationToken) =>
        coordinator.RevalidateApprovedContextAsync(jobId, expectedJobVersion, transition, cancellationToken);

    public Task<Result<JobDocument>> LoadJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        jobs.LoadAsync(jobId, cancellationToken);

    public Task<Result<TranslationReviewSnapshot>> LoadReviewAsync(Guid jobId, CancellationToken cancellationToken) =>
        reviews.LoadAsync(jobId, cancellationToken);

    public Task<Result<JobDocument>> ApproveAsync(
        Guid jobId,
        IReadOnlyList<ReviewDecisionInput> decisions,
        long expectedReviewVersion,
        ReviewAutomationReceipt? automationAuthority,
        CancellationToken cancellationToken) => coordinator.ApproveAsync(jobId, decisions, expectedReviewVersion, automationAuthority, cancellationToken);

    public Task<Result<JobDocument>> ReviseApprovedReviewAsync(Guid jobId, long expectedJobVersion,
        IReadOnlyList<ReviewDecisionInput> decisions, long expectedReviewVersion,
        ReviewAutomationReceipt automationAuthority, CancellationToken cancellationToken) =>
        coordinator.ReviseApprovedReviewAsync(jobId, expectedJobVersion, decisions,
            expectedReviewVersion, automationAuthority, cancellationToken);

    /// <summary>
    /// Restores only a failed, recoverable generation to the exact durable
    /// Approved checkpoint.  This does not invoke the coordinator: therefore it
    /// cannot re-extract, translate, review, or write a DWG.
    /// </summary>
    public async Task<Result<JobDocument>> ReconcileFailedGenerationToApprovedAsync(
        Guid jobId,
        long expectedVersion,
        AgentGenerationReconciliationAuthority authority,
        CancellationToken cancellationToken)
    {
        if (authority is null || !ContractPatterns.Sha256().IsMatch(authority.ReconciliationBindingHash) ||
            !ContractPatterns.Sha256().IsMatch(authority.ReviewHash) ||
            !ContractPatterns.Sha256().IsMatch(authority.EvidenceHash))
            return Failure("GENERATION_RECONCILIATION_EVIDENCE_INVALID", "The reconciliation authority is incomplete.");
        var loaded = await jobs.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value!;
        if (current.State == JobState.Approved && current.Version == expectedVersion + 1)
            return await CompleteGenerationReconciliationProjectionAsync(current, expectedVersion, authority, cancellationToken).ConfigureAwait(false);
        if (current.State != JobState.Failed || current.Version != expectedVersion || !IsRecoverableGenerationFailure(current, authority))
            return Failure("JOB_STATE_CONFLICT", "The failed generation checkpoint changed before reconciliation.");

        var data = DeserializeJobData(current.Data);
        if (data is null || data.Segments is null || data.Segments.Count == 0)
            return Failure("GENERATION_RECONCILIATION_EVIDENCE_INVALID", "The durable generation payload is incomplete.");

        var checkpoint = await LatestApprovedCheckpointAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!checkpoint.IsSuccess) return Results.Failure<JobDocument>(checkpoint.Error!);
        if (!string.Equals(checkpoint.Value!.Document.SourceHash, data.Specification.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.Value.Document.ConfigurationHash, data.ConfigurationHash, StringComparison.Ordinal) ||
            authority.ApprovedCheckpointVersion >= 0 &&
            (checkpoint.Value.Document.ExpectedVersion != authority.ApprovedCheckpointVersion ||
             !string.Equals(checkpoint.Value.ArtifactHash, authority.ApprovedCheckpointHash, StringComparison.Ordinal)))
            return Failure("GENERATION_RECONCILIATION_CHECKPOINT_MISMATCH", "The durable Approved checkpoint no longer matches the failed generation.");

        var review = await reviews.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess || review.Value is null || review.Value.Version != authority.ReviewVersion ||
            review.Value.Rows.Count != authority.DecisionCount || !HasExactApprovedReview(review.Value, data.Segments) ||
            !string.Equals(TranslationReviewFingerprint.Create(review.Value).ReviewHash, authority.ReviewHash, StringComparison.Ordinal))
            return Failure("GENERATION_RECONCILIATION_REVIEW_MISMATCH", "The approved review payload is incomplete or no longer matches the job.");

        var restoredData = current.Data.DeepClone().AsObject();
        restoredData.Remove("failure");
        restoredData.Remove("agentFailure");
        var marker = new GenerationFailureReconciliationReceipt(
            expectedVersion, authority.FailureCode, authority.ReconciliationBindingHash,
            checkpoint.Value.Document.ExpectedVersion, checkpoint.Value.ArtifactHash,
            review.Value.Version, authority.ReviewHash, review.Value.Rows.Count, authority.EvidenceHash);
        var restoredPayload = data with { GenerationFailureReconciliationReceipt = marker };
        restoredData = JsonSerializer.SerializeToNode(restoredPayload, Json)!.AsObject();
        var restored = current with
        {
            State = JobState.Approved,
            Version = current.Version + 1,
            UpdatedAtUtc = clock.UtcNow,
            Data = restoredData
        };
        var saved = await jobs.SaveAsync(restored, current.Version, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            // A concurrent caller may have won the exact CAS.  Reload only that one safe
            // successor and complete its deterministic audit/checkpoint projection; every
            // other state, version, or marker remains a conflict.
            var winner = await jobs.LoadAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (!winner.IsSuccess || winner.Value!.State != JobState.Approved || winner.Value.Version != expectedVersion + 1)
                return saved;
            return await CompleteGenerationReconciliationProjectionAsync(
                winner.Value, expectedVersion, authority, CancellationToken.None).ConfigureAwait(false);
        }
        return await CompleteGenerationReconciliationProjectionAsync(saved.Value!, expectedVersion, authority, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Result<JobDocument>> CompleteGenerationReconciliationProjectionAsync(
        JobDocument current,
        long expectedFailedVersion,
        AgentGenerationReconciliationAuthority authority,
        CancellationToken cancellationToken)
    {
        var data = DeserializeJobData(current.Data);
        var marker = data?.GenerationFailureReconciliationReceipt;
        if (current.State != JobState.Approved || current.Version != expectedFailedVersion + 1 || data is null || marker is null ||
            marker.FailedJobVersion != expectedFailedVersion ||
            !string.Equals(marker.FailureCode, authority.FailureCode, StringComparison.Ordinal) ||
            !string.Equals(marker.ReconciliationBindingHash, authority.ReconciliationBindingHash, StringComparison.Ordinal) ||
            marker.ApprovedCheckpointVersion != authority.ApprovedCheckpointVersion && authority.ApprovedCheckpointVersion >= 0 ||
            authority.ApprovedCheckpointVersion >= 0 && !string.Equals(marker.ApprovedCheckpointHash, authority.ApprovedCheckpointHash, StringComparison.Ordinal) ||
            marker.ReviewVersion != authority.ReviewVersion || marker.DecisionCount != authority.DecisionCount ||
            !string.Equals(marker.ReviewHash, authority.ReviewHash, StringComparison.Ordinal) ||
            !string.Equals(marker.EvidenceHash, authority.EvidenceHash, StringComparison.Ordinal))
            return Failure("GENERATION_RECONCILIATION_MARKER_MISMATCH", "The reconciled job does not carry the exact durable authority marker.");

        var eventId = StableGuid($"generation-reconciliation:event:{current.JobId:D}:{expectedFailedVersion}:{authority.ReconciliationBindingHash}");
        var correlationId = StableGuid($"generation-reconciliation:correlation:{current.JobId:D}:{expectedFailedVersion}:{authority.ReconciliationBindingHash}");
        var audit = await jobs.AppendAuditAsync(new AuditRecord(
            eventId, current.JobId, correlationId, current.UpdatedAtUtc.ToUniversalTime(), "AgentGenerationFailureReconciled",
            new JsonObject
            {
                ["from"] = JobState.Failed.ToString(),
                ["to"] = JobState.Approved.ToString(),
                ["failureCode"] = authority.FailureCode,
                ["failedJobVersion"] = expectedFailedVersion,
                ["checkpointVersion"] = marker.ApprovedCheckpointVersion,
                ["checkpointHash"] = marker.ApprovedCheckpointHash,
                ["reviewVersion"] = marker.ReviewVersion,
                ["reviewHash"] = marker.ReviewHash,
                ["decisionCount"] = marker.DecisionCount,
                ["evidenceHash"] = marker.EvidenceHash,
                ["reconciliationBindingHash"] = marker.ReconciliationBindingHash
            }), cancellationToken).ConfigureAwait(false);
        if (!audit.IsSuccess) return Results.Failure<JobDocument>(audit.Error!);
        var checkpoint = new JobCheckpoint(current.JobId, JobState.Approved, current.Version,
            current.UpdatedAtUtc.ToUniversalTime(), data.Specification.SourceHash, data.ConfigurationHash,
            ContractV1.SchemaVersion, new JsonObject
            {
                ["state"] = JobState.Approved.ToString(),
                ["jobVersion"] = current.Version,
                ["generationReconciliationEvidenceHash"] = marker.EvidenceHash,
                ["reconciliationBindingHash"] = marker.ReconciliationBindingHash
            });
        var projected = await jobs.SaveCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return projected.IsSuccess ? Results.Success(current) : Results.Failure<JobDocument>(projected.Error!);
    }

    /// <summary>
    /// This is intentionally not a general Failed-to-ReviewRequired transition. The
    /// caller has already verified the narrowly-scoped durable geometry evidence;
    /// this method performs the optimistic, auditable state transition only.
    /// </summary>
    public async Task<Result<JobDocument>> ReconcileFailedGeometryForReviewAsync(
        Guid jobId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var loaded = await jobs.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value!;
        if (current.State != JobState.Failed || current.Version != expectedVersion ||
            !string.Equals(current.Data["failure"]?["code"]?.GetValue<string>(), "GEOMETRY_INVARIANTS_CHANGED", StringComparison.Ordinal))
            return Failure("JOB_STATE_CONFLICT", "The failed geometry job changed before reconciliation.");

        var data = current.Data.DeepClone().AsObject();
        data.Remove("failure");
        data.Remove("agentFailure");
        var updated = current with
        {
            State = JobState.ReviewRequired,
            Version = current.Version + 1,
            UpdatedAtUtc = clock.UtcNow,
            Data = data
        };
        var saved = await jobs.SaveAsync(updated, current.Version, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess) return saved;
        var audit = await jobs.AppendAuditAsync(new AuditRecord(
            Guid.NewGuid(), jobId, Guid.NewGuid(), clock.UtcNow, "AgentGeometryInvariantReviewReconciled",
            new JsonObject
            {
                ["from"] = JobState.Failed.ToString(),
                ["to"] = JobState.ReviewRequired.ToString(),
                ["reason"] = "GEOMETRY_INVARIANTS_CHANGED"
            }), cancellationToken).ConfigureAwait(false);
        return audit.IsSuccess ? saved : Results.Failure<JobDocument>(audit.Error!);
    }

    public Task<Result<JobDocument>> GenerateAsync(Guid jobId, TranslationReviewFingerprint expectedReview,
        CancellationToken cancellationToken) => coordinator.GenerateAsync(jobId, expectedReview, cancellationToken);

    public async Task<Result<JobDocument>> CancelAsync(Guid jobId, long expectedVersion, CancellationToken cancellationToken)
    {
        var loaded = await jobs.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value!;
        if (current.Version != expectedVersion)
            return Failure("JOB_STATE_CONFLICT", "The job version changed before cancellation.");
        if (current.State is JobState.Writing or JobState.Validating or JobState.Completed or JobState.Failed or JobState.Cancelled ||
            !JobLifecycle.CanTransition(current.State, JobState.Cancelled))
            return Failure("JOB_STATE_CONFLICT", "This job can no longer be cancelled safely.");
        var updated = current with
        {
            State = JobState.Cancelled,
            Version = current.Version + 1,
            UpdatedAtUtc = clock.UtcNow,
            Data = current.Data.DeepClone().AsObject()
        };
        var saved = await jobs.SaveAsync(updated, current.Version, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess) return saved;
        var audit = await jobs.AppendAuditAsync(new AuditRecord(
            Guid.NewGuid(), jobId, Guid.NewGuid(), clock.UtcNow, "AgentWorkflowCancelled",
            new JsonObject { ["from"] = current.State.ToString(), ["to"] = JobState.Cancelled.ToString() }), cancellationToken).ConfigureAwait(false);
        return audit.IsSuccess ? saved : Results.Failure<JobDocument>(audit.Error!);
    }

    public async Task<Result<JobDocument>> FailAsync(Guid jobId, string code, bool retryable, CancellationToken cancellationToken)
    {
        var loaded = await jobs.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value!;
        if (current.State is JobState.Failed or JobState.Cancelled or JobState.Completed) return Results.Success(current);
        if (!JobLifecycle.CanTransition(current.State, JobState.Failed))
            return Failure("JOB_STATE_CONFLICT", "The job cannot transition to Failed from its current state.");
        var data = current.Data.DeepClone().AsObject();
        data["agentFailure"] = new JsonObject { ["code"] = code, ["retryable"] = retryable };
        var updated = current with { State = JobState.Failed, Version = current.Version + 1, UpdatedAtUtc = clock.UtcNow, Data = data };
        var saved = await jobs.SaveAsync(updated, current.Version, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess) return saved;
        var audit = await jobs.AppendAuditAsync(new AuditRecord(Guid.NewGuid(), jobId, Guid.NewGuid(), clock.UtcNow,
            "AgentWorkflowFailed", new JsonObject { ["code"] = code, ["retryable"] = retryable }), cancellationToken).ConfigureAwait(false);
        return audit.IsSuccess ? saved : Results.Failure<JobDocument>(audit.Error!);
    }

    private static Result<JobDocument> Failure(string code, string message) =>
        Results.Failure<JobDocument>(new ContractError(code, ErrorCategory.Concurrency, message, false));

    private bool IsRecoverableGenerationFailure(JobDocument current, AgentGenerationReconciliationAuthority authority)
    {
        var failure = current.Data["failure"] as JsonObject ?? current.Data["agentFailure"] as JsonObject;
        var code = StringValue(failure, "code");
        var retryable = false;
        var hasRetryable = failure?["retryable"] is JsonValue retryableValue && retryableValue.TryGetValue<bool>(out retryable);
        var categoryText = StringValue(failure, "category");
        var hasCategory = Enum.TryParse<ErrorCategory>(categoryText, ignoreCase: false, out var category) && Enum.IsDefined(category);
        if (code == "CAD_WRITE_FAILED" && (!hasRetryable || !hasCategory ||
            !string.Equals(StringValue(failure, "technicalStage"), authority.TechnicalStage, StringComparison.Ordinal) ||
            !string.Equals(StringValue(failure, "nativeErrorStatus"), authority.NativeErrorStatus, StringComparison.Ordinal)))
            return false;
        if (!hasRetryable) retryable = false;
        if (!hasCategory) category = authority.FailureCategory;
        if (!string.Equals(code, authority.FailureCode, StringComparison.Ordinal) || retryable != authority.FailureRetryable ||
            category != authority.FailureCategory) return false;
        if (code == "CAD_WRITE_FAILED")
            return !retryable && category == ErrorCategory.Environment &&
                ContractPatterns.Sha256().IsMatch(authority.EvidenceHash);
        if (code == "CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT")
        {
            var policy = BootstrapTrustPolicy;
            var exact = policy.IsExactFailure(
                current.JobId, current.Version, code, categoryText, hasRetryable, retryable,
                StringValue(failure, "stage"), StringValue(failure, "technicalStage"),
                StringValue(failure, "nativeErrorStatus"));
            return exact && category == ErrorCategory.Security &&
                string.Equals(authority.FailureStage, "Writing", StringComparison.Ordinal) &&
                authority.TechnicalStage is null && authority.NativeErrorStatus is null &&
                authority.ApprovedCheckpointVersion == policy.ApprovedCheckpointVersion &&
                string.Equals(authority.ApprovedCheckpointHash, policy.ApprovedCheckpointArtifactHash, StringComparison.Ordinal) &&
                authority.ReviewVersion == policy.ReviewVersion &&
                authority.DecisionCount == policy.ReviewDecisionCount &&
                string.Equals(authority.ReviewHash, policy.ReviewHash, StringComparison.Ordinal) &&
                ContractPatterns.Sha256().IsMatch(authority.EvidenceHash);
        }
        return (code is "IPC_TIMEOUT" or "IPC_PEER_DISCONNECTED" or "CAD_WRITE_CANCELLED_AT_SAFE_BOUNDARY") &&
            (retryable || code is "IPC_TIMEOUT" or "IPC_PEER_DISCONNECTED");
    }

    private static string? StringValue(JsonObject? value, string name) =>
        value?[name] is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;

    private sealed record ApprovedCheckpointEvidence(JobCheckpoint Document, string ArtifactHash);

    private async Task<Result<ApprovedCheckpointEvidence>> LatestApprovedCheckpointAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot))
            return Results.Failure<ApprovedCheckpointEvidence>(new ContractError("GENERATION_RECONCILIATION_CHECKPOINT_UNAVAILABLE", ErrorCategory.Storage,
                "The durable checkpoint reader is unavailable.", false));
        var root = Path.GetFullPath(Path.Combine(_workspaceRoot, jobId.ToString("D"), "checkpoints"));
        if (!root.StartsWith(_workspaceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            return Results.Failure<ApprovedCheckpointEvidence>(new ContractError("GENERATION_RECONCILIATION_CHECKPOINT_MISSING", ErrorCategory.Storage,
                "No approved checkpoint is available.", false));
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var checkpoint = JsonSerializer.Deserialize<JobCheckpoint>(bytes, Json);
                if (checkpoint?.JobId == jobId && checkpoint.SafeState == JobState.Approved)
                    return Results.Success(new ApprovedCheckpointEvidence(checkpoint,
                        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Results.Failure<ApprovedCheckpointEvidence>(new ContractError("GENERATION_RECONCILIATION_CHECKPOINT_UNREADABLE", ErrorCategory.Storage,
                "The approved checkpoint could not be validated.", false));
        }
        return Results.Failure<ApprovedCheckpointEvidence>(new ContractError("GENERATION_RECONCILIATION_CHECKPOINT_MISSING", ErrorCategory.Storage,
            "No approved checkpoint is available.", false));
    }

    private static Guid StableGuid(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static DwgTranslationJobData? DeserializeJobData(JsonObject value)
    {
        try
        {
            var data = value.DeepClone().AsObject();
            data.Remove("failure");
            data.Remove("agentFailure");
            return data.Deserialize<DwgTranslationJobData>(Json);
        }
        catch (JsonException) { return null; }
    }

    private static bool HasExactApprovedReview(TranslationReviewSnapshot review, IReadOnlyList<CadTextSegment> segments)
    {
        if (review.JobId == Guid.Empty || review.Rows.Count != segments.Count || review.Rows.Count == 0) return false;
        var ids = segments.Select(item => item.SegmentId).ToHashSet(StringComparer.Ordinal);
        return ids.Count == segments.Count && review.Rows.All(row => ids.Remove(row.SegmentId) &&
            row.State is SegmentState.Approved or SegmentState.Excluded && !string.IsNullOrEmpty(row.FinalText));
    }
}
