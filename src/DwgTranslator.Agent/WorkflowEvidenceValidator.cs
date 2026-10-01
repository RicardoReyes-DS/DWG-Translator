using System.Text.Json.Nodes;
using DwgTranslator.Contracts;
using static DwgTranslator.Agent.WorkflowEvidenceJson;

namespace DwgTranslator.Agent;

/// <summary>Shape and state validation after a strict workflow artifact scan has read a file.</summary>
internal static class WorkflowEvidenceValidator
{
    internal static bool ValidPlanEvidence(WorkflowPlanDocument? plan, string artifactName)
    {
        if (plan is null || plan.SchemaVersion != AgentWorkflowContract.SchemaVersion ||
            !ValidOpaque(plan.PlanId) || !string.Equals(artifactName, plan.PlanId + ".json", StringComparison.Ordinal) ||
            plan.Purpose is not ("translation.prepare" or "generation.execute" or "generation.reconcile.apply") ||
            plan.CreatedAtUtc.Offset != TimeSpan.Zero || plan.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            plan.ExpiresAtUtc <= plan.CreatedAtUtc || plan.ApprovalHash is not { Length: 64 } || !plan.ApprovalHash.All(Uri.IsHexDigit) ||
            plan.Binding is null || plan.Consumed != plan.ConsumedAtUtc.HasValue ||
            plan.ConsumedAtUtc is { } consumedAt && (consumedAt.Offset != TimeSpan.Zero || consumedAt < plan.CreatedAtUtc || consumedAt >= plan.ExpiresAtUtc))
            return false;
        return plan.Purpose switch
        {
            "generation.execute" => ValidGenerationExecuteBinding(plan.Binding),
            "generation.reconcile.apply" => ValidGenerationReconciliationBinding(plan.Binding),
            "translation.prepare" => plan.Binding.Count > 0,
            _ => false
        };
    }

    private static bool ValidGenerationExecuteBinding(JsonObject binding) =>
        TryGuidD(binding, "jobId", out _) && TryInt64(binding, "jobVersion", out var version) && version >= 0 &&
        TryString(binding, "sourcePath", out var sourcePath) && Path.IsPathFullyQualified(sourcePath) &&
        TrySha256(binding, "sourceHash") && TryString(binding, "reviewHash", out _) && TrySha256(binding, "reviewHash") &&
        (binding["sourceBytes"] is null || TryInt64(binding, "sourceBytes", out var sourceBytes) && sourceBytes > 0) &&
        TryString(binding, "outputPath", out var outputPath) && Path.IsPathFullyQualified(outputPath) &&
        TrySha256(binding, "outputBindingHash") && TryString(binding, "validationPolicy", out var validation) &&
        validation == "VisualStrictV2" &&
        (binding["reviewVersion"] is null || TryInt64(binding, "reviewVersion", out var reviewVersion) && reviewVersion >= 0) &&
        (binding["contextHash"] is null || TrySha256(binding, "contextHash")) &&
        (binding["reviewAutomationReceiptHash"] is null || TrySha256(binding, "reviewAutomationReceiptHash"));

    private static bool ValidGenerationReconciliationBinding(JsonObject binding)
    {
        var hasApprovedCheckpointHash = binding.ContainsKey("approvedCheckpointHash");
        var hasEvidenceHash = binding.ContainsKey("evidenceHash");
        if (hasApprovedCheckpointHash != hasEvidenceHash) return false;
        if (binding.ContainsKey("cadBootstrapTrustEvidence"))
            return !binding.ContainsKey("cadWriteEvidence") && ValidBootstrapTrustReconciliationBinding(binding);
        return hasApprovedCheckpointHash
            ? CadWriteReconciliationBindingValidator.IsValid(binding)
            : ValidLegacyGenerationReconciliationBinding(binding);
    }

    private static bool ValidLegacyGenerationReconciliationBinding(JsonObject binding)
    {
        if (!HasExactFields(binding, LegacyGenerationReconciliationBindingFields) ||
            !TryGuidD(binding, "jobId", out var jobId) ||
            !string.Equals(binding["jobId"]!.GetValue<string>(), jobId.ToString("D"), StringComparison.Ordinal) ||
            !TryInt64(binding, "jobVersion", out var version) || version < 0 ||
            !TryString(binding, "failureCode", out var code) ||
            code is not ("IPC_TIMEOUT" or "IPC_PEER_DISCONNECTED" or "CAD_WRITE_CANCELLED_AT_SAFE_BOUNDARY") ||
            !TryString(binding, "sourcePath", out var sourcePath) || !Path.IsPathFullyQualified(sourcePath) ||
            !TrySha256(binding, "sourceHash") ||
            !TryInt64(binding, "sourceBytes", out var sourceBytes) || sourceBytes <= 0 ||
            !TryString(binding, "outputPath", out var outputPath) || !Path.IsPathFullyQualified(outputPath) ||
            !TryInt64(binding, "reviewVersion", out var reviewVersion) || reviewVersion < 0 ||
            !TrySha256(binding, "reviewHash") ||
            !TryInt64(binding, "decisionCount", out var decisionCount) || decisionCount <= 0 ||
            !TrySha256(binding, "reconciliationBindingHash"))
            return false;

        var expected = TextHash(
            $"{jobId:D}|{version}|{binding["sourceHash"]!.GetValue<string>()}|{sourceBytes}|{outputPath}|{reviewVersion}|{binding["reviewHash"]!.GetValue<string>()}");
        return string.Equals(binding["reconciliationBindingHash"]!.GetValue<string>(), expected, StringComparison.Ordinal);
    }

    private static bool ValidBootstrapTrustReconciliationBinding(JsonObject binding)
    {
        var policy = CadBootstrapTrustFailureReconciliationPolicy.Instance;
        if (!HasExactFields(binding, BootstrapTrustReconciliationBindingFields) ||
            !TryGuidD(binding, "jobId", out var jobId) || jobId != policy.JobId ||
            !TryInt64(binding, "jobVersion", out var version) || version != policy.FailedJobVersion ||
            !TryString(binding, "failureCode", out var code) || code != "CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT" ||
            !TryString(binding, "failureCategory", out var category) || category != ErrorCategory.Security.ToString() ||
            !TryBoolean(binding, "failureRetryable", out var retryable) || retryable ||
            !TryString(binding, "failureStage", out var failureStage) || failureStage != "Writing" ||
            binding["technicalStage"] is not null || binding["nativeErrorStatus"] is not null ||
            !TryString(binding, "sourcePath", out var sourcePath) || !Path.IsPathFullyQualified(sourcePath) ||
            !string.Equals(Path.GetFileName(sourcePath), policy.SourceFileName, StringComparison.Ordinal) ||
            !TryString(binding, "sourceHash", out var sourceHash) || sourceHash != policy.SourceHash ||
            !TryInt64(binding, "sourceBytes", out var sourceBytes) || sourceBytes <= 0 ||
            !TryString(binding, "outputPath", out var outputPath) || !Path.IsPathFullyQualified(outputPath) ||
            !string.Equals(Path.GetFileName(outputPath), policy.OutputFileName, StringComparison.Ordinal) ||
            !TryInt64(binding, "reviewVersion", out var reviewVersion) || reviewVersion != policy.ReviewVersion ||
            !TryString(binding, "reviewHash", out var reviewHash) || reviewHash != policy.ReviewHash ||
            !TryInt64(binding, "decisionCount", out var decisionCount) || decisionCount != policy.ReviewDecisionCount ||
            !TryInt64(binding, "approvedCheckpointVersion", out var checkpointVersion) ||
            checkpointVersion != policy.ApprovedCheckpointVersion ||
            !TryString(binding, "approvedCheckpointHash", out var checkpointHash) ||
            checkpointHash != policy.ApprovedCheckpointArtifactHash ||
            !TrySha256(binding, "evidenceHash") || !TrySha256(binding, "reconciliationBindingHash") ||
            binding["cadBootstrapTrustEvidence"] is not JsonObject evidence ||
            !HasExactFields(evidence, BootstrapTrustEvidenceFields))
            return false;

        if (!TryString(evidence, "policyVersion", out var policyVersion) || policyVersion != policy.PolicyVersion ||
            !TryString(evidence, "failedJobArtifactHash", out var jobHash) || jobHash != policy.FailedJobArtifactHash ||
            !TryString(evidence, "jobConfigurationHash", out var configurationHash) ||
            configurationHash != policy.JobConfigurationHash ||
            !TryString(evidence, "reviewArtifactHash", out var reviewArtifactHash) ||
            reviewArtifactHash != policy.ReviewArtifactHash ||
            !TryString(evidence, "reviewHash", out var evidenceReviewHash) || evidenceReviewHash != policy.ReviewHash ||
            !TryString(evidence, "contextHash", out var contextHash) || contextHash != policy.ContextHash ||
            !TryString(evidence, "reviewAutomationReceiptHash", out var receiptHash) ||
            receiptHash != policy.ReviewAutomationReceiptHash ||
            !TryInt64(evidence, "approvedCheckpointVersion", out var evidenceCheckpointVersion) ||
            evidenceCheckpointVersion != policy.ApprovedCheckpointVersion ||
            !TryString(evidence, "approvedCheckpointHash", out var evidenceCheckpointHash) ||
            evidenceCheckpointHash != policy.ApprovedCheckpointArtifactHash ||
            !TryString(evidence, "consumedGenerationPlanId", out var planId) || planId != policy.GenerationPlanId ||
            !TryString(evidence, "consumedGenerationPlanHash", out var planHash) ||
            planHash != policy.GenerationPlanArtifactHash ||
            !TryString(evidence, "operationId", out var operationId) || operationId != policy.OperationId ||
            !TryString(evidence, "operationHash", out var operationHash) || operationHash != policy.OperationArtifactHash ||
            !TryString(evidence, "generationIdempotencyMarkerHash", out var markerHash) ||
            markerHash != policy.GenerationMarkerArtifactHash ||
            !TryString(evidence, "generationIdempotencyMarkerName", out var markerName) ||
            markerName != policy.GenerationMarkerName ||
            !TryString(evidence, "generationRequestBodyHash", out var requestHash) ||
            requestHash != policy.GenerationRequestBodyHash ||
            !TryString(evidence, "bootstrapArtifactName", out var bootstrapName) ||
            bootstrapName != policy.BootstrapArtifactName ||
            !TryString(evidence, "bootstrapArtifactHash", out var bootstrapHash) ||
            bootstrapHash != policy.BootstrapArtifactHash ||
            !TrySha256(evidence, "candidatePathHash"))
            return false;

        foreach (var name in new[]
                 {
                     "cleanupComplete", "outputAbsent", "candidateAbsent", "stagingAbsent",
                     "bootstrapOriginalSnapshotAbsent", "bootstrapRestoreMarkerAbsent",
                     "generationCadReceiptAbsent", "cadProcessAbsent", "operationsLeasesAndFenceQuiescent"
                 })
            if (!TryBoolean(evidence, name, out var value) || !value) return false;

        if (!string.Equals(binding["evidenceHash"]!.GetValue<string>(), CanonicalHash(evidence), StringComparison.Ordinal))
            return false;
        var unsealed = binding.DeepClone().AsObject();
        unsealed.Remove("reconciliationBindingHash");
        return string.Equals(binding["reconciliationBindingHash"]!.GetValue<string>(),
            CanonicalHash(unsealed), StringComparison.Ordinal);
    }

    private static readonly string[] LegacyGenerationReconciliationBindingFields =
    [
        "jobId", "jobVersion", "failureCode", "sourcePath", "sourceHash", "sourceBytes",
        "outputPath", "reviewVersion", "reviewHash", "decisionCount", "reconciliationBindingHash"
    ];
    private static readonly string[] BootstrapTrustReconciliationBindingFields =
    [
        "jobId", "jobVersion", "failureCode", "failureCategory", "failureRetryable", "failureStage",
        "technicalStage", "nativeErrorStatus", "sourcePath", "sourceHash", "sourceBytes", "outputPath",
        "reviewVersion", "reviewHash", "decisionCount", "approvedCheckpointVersion",
        "approvedCheckpointHash", "evidenceHash", "cadBootstrapTrustEvidence", "reconciliationBindingHash"
    ];
    private static readonly string[] BootstrapTrustEvidenceFields =
    [
        "policyVersion", "failedJobArtifactHash", "jobConfigurationHash", "reviewArtifactHash",
        "reviewHash", "contextHash", "reviewAutomationReceiptHash", "approvedCheckpointVersion",
        "approvedCheckpointHash", "consumedGenerationPlanId", "consumedGenerationPlanHash",
        "operationId", "operationHash", "generationIdempotencyMarkerHash",
        "generationIdempotencyMarkerName", "generationRequestBodyHash", "bootstrapArtifactName",
        "bootstrapArtifactHash", "candidatePathHash", "cleanupComplete", "outputAbsent",
        "candidateAbsent", "stagingAbsent", "bootstrapOriginalSnapshotAbsent",
        "bootstrapRestoreMarkerAbsent", "generationCadReceiptAbsent", "cadProcessAbsent",
        "operationsLeasesAndFenceQuiescent"
    ];

    internal static bool ValidOperationEvidence(AgentWorkflowOperation? operation, string artifactName)
    {
        if (operation is null || !ValidOpaque(operation.OperationId) ||
            !string.Equals(artifactName, operation.OperationId + ".json", StringComparison.Ordinal) ||
            operation.JobId == Guid.Empty ||
            operation.Kind is not ("translation.prepare" or "translation.resume-missing-only" or "generation.execute") ||
            operation.State is not ("Running" or "Completed" or "Failed") ||
            operation.Stage is not ("Draft" or "Inspecting" or "Extracted" or "Translating" or "ReviewRequired" or
                "Approved" or "Writing" or "Validating" or "Completed" or "Failed" or "Cancelled" or
                "ReconciledFromCadReceipt" or "GenerationQueued" or "GenerationPreflight" or
                "GenerationReviewBinding" or "GenerationSourceSnapshot" or "GenerationWriting" or
                "GenerationCleanup") ||
            operation.CreatedAtUtc.Offset != TimeSpan.Zero || operation.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            operation.UpdatedAtUtc < operation.CreatedAtUtc ||
            operation.StartedAtUtc is { } started &&
                (started.Offset != TimeSpan.Zero || started < operation.CreatedAtUtc) ||
            operation.DeadlineAtUtc is { } deadline &&
                (deadline.Offset != TimeSpan.Zero || operation.StartedAtUtc is null || deadline <= operation.StartedAtUtc) ||
            operation.LastHeartbeatAtUtc is { } heartbeat &&
                (heartbeat.Offset != TimeSpan.Zero || heartbeat < operation.CreatedAtUtc || heartbeat > operation.UpdatedAtUtc) ||
            operation.CompletedAtUtc is { } completed &&
                (completed.Offset != TimeSpan.Zero || completed < (operation.StartedAtUtc ?? operation.CreatedAtUtc) ||
                 completed != operation.UpdatedAtUtc) ||
            operation.DurationMilliseconds is < 0 ||
            (operation.CompletedAtUtc is null) != (operation.DurationMilliseconds is null) ||
            operation.CleanupOutcome is { Length: 0 or > 128 } ||
            operation.TechnicalStage is { } technicalStage && !CadWriteTechnicalStages.IsSupported(technicalStage) ||
            operation.NativeErrorStatus is { } nativeStatus && !CadNativeErrorStatuses.IsSupported(nativeStatus))
            return false;
        if (operation.State == "Failed")
            return !string.IsNullOrWhiteSpace(operation.ErrorCode) && operation.ErrorCode.Length <= 128;
        return operation.ErrorCode is null && !operation.Retryable && operation.ErrorCategory is null &&
            operation.TechnicalStage is null && operation.NativeErrorStatus is null;
    }

    internal static bool ValidIdempotencyEvidence(JsonObject? document, string artifactName)
    {
        if (document is null || Path.GetExtension(artifactName) != ".json" ||
            Path.GetFileNameWithoutExtension(artifactName) is not { Length: 64 } name || !name.All(Uri.IsHexDigit) ||
            document.Count is < 3 or > 4 || document["schemaVersion"]?.GetValue<string>() != AgentWorkflowContract.SchemaVersion ||
            document["requestHash"]?.GetValue<string>() is not { Length: 64 } requestHash || !requestHash.All(Uri.IsHexDigit) ||
            document["state"]?.GetValue<string>() is not ("Pending" or "Completed"))
            return false;
        var state = document["state"]!.GetValue<string>();
        if (state == "Pending") return document["response"] is null && document.Count == 3;
        if (document["response"] is not JsonObject response || response.Count == 0 || document.Count != 4 ||
            !TryGuidD(response, "jobId", out _) || !TryString(response, "state", out var responseState))
            return false;
        if (response["schemaVersion"] is not null &&
            (!TryString(response, "schemaVersion", out var responseSchema) || responseSchema != AgentWorkflowContract.SchemaVersion))
            return false;
        if (response["cancellationRequested"] is not null)
            return response.Count == 4 && response["schemaVersion"] is not null &&
                response["cancellationRequested"] is JsonValue cancellationValue &&
                cancellationValue.TryGetValue<bool>(out var cancellationRequested) && cancellationRequested &&
                responseState is "Draft" or "Inspecting" or "Extracted" or "Translating" or "ReviewRequired" or "Approved";
        if (!TryInt64(response, "jobVersion", out var jobVersion) || jobVersion < 0 ||
            responseState is not ("Started" or "Translating" or "Approved" or "Cancelled"))
            return false;
        return response["operationId"] is null ||
            TryString(response, "operationId", out var operationId) && ValidOpaque(operationId);
    }
}
