using System.Text.Json.Nodes;
using DwgTranslator.Contracts;
using static DwgTranslator.Agent.WorkflowEvidenceJson;

namespace DwgTranslator.Agent;

/// <summary>Pure validation of a sealed CAD write reconciliation binding.</summary>
internal static class CadWriteReconciliationBindingValidator
{
    private static readonly string[] Fields =
    [
        "jobId", "jobVersion", "failureCode", "failureCategory", "failureRetryable",
        "technicalStage", "nativeErrorStatus", "sourcePath", "sourceHash", "sourceBytes",
        "outputPath", "reviewVersion", "reviewHash", "decisionCount", "approvedCheckpointVersion",
        "approvedCheckpointHash", "evidenceHash", "cadWriteEvidence", "reconciliationBindingHash"
    ];

    internal static bool IsValid(JsonObject binding)
    {
        if (!HasExactFields(binding, Fields) ||
            !TryGuidD(binding, "jobId", out var jobId) ||
            !string.Equals(binding["jobId"]!.GetValue<string>(), jobId.ToString("D"), StringComparison.Ordinal) ||
            !TryInt64(binding, "jobVersion", out var version) || version < 0 ||
            !TryString(binding, "failureCode", out var code) || code != "CAD_WRITE_FAILED" ||
            !TryString(binding, "failureCategory", out var category) || category != ErrorCategory.Environment.ToString() ||
            !TryBoolean(binding, "failureRetryable", out var retryable) || retryable ||
            !TryNullableCadDiagnostic(binding, "technicalStage", CadWriteTechnicalStages.IsSupported, out var technicalStage) ||
            !TryNullableCadDiagnostic(binding, "nativeErrorStatus", CadNativeErrorStatuses.IsSupported, out var nativeErrorStatus) ||
            !TryString(binding, "sourcePath", out var sourcePath) || !Path.IsPathFullyQualified(sourcePath) ||
            !TrySha256(binding, "sourceHash") ||
            !TryInt64(binding, "sourceBytes", out var sourceBytes) || sourceBytes <= 0 ||
            !TryString(binding, "outputPath", out var outputPath) || !Path.IsPathFullyQualified(outputPath) ||
            !TryInt64(binding, "reviewVersion", out var reviewVersion) || reviewVersion < 0 ||
            !TrySha256(binding, "reviewHash") ||
            !TryInt64(binding, "decisionCount", out var decisionCount) || decisionCount <= 0 ||
            !TryInt64(binding, "approvedCheckpointVersion", out var checkpointVersion) || checkpointVersion < 0 ||
            !TrySha256(binding, "approvedCheckpointHash") || !TrySha256(binding, "evidenceHash") ||
            binding["cadWriteEvidence"] is not JsonObject evidence || evidence.Count == 0 ||
            !TryString(evidence, "policyVersion", out var policy) || policy != "cad-write-failure-reconciliation/1.0" ||
            !TryBoolean(evidence, "legacyException", out var legacyException) ||
            !TryNullableCadDiagnostic(evidence, "technicalStage", CadWriteTechnicalStages.IsSupported, out var evidenceStage) ||
            !TryNullableCadDiagnostic(evidence, "nativeErrorStatus", CadNativeErrorStatuses.IsSupported, out var evidenceStatus) ||
            !string.Equals(evidenceStage, technicalStage, StringComparison.Ordinal) ||
            !string.Equals(evidenceStatus, nativeErrorStatus, StringComparison.Ordinal) ||
            !(legacyException
                ? technicalStage is null && nativeErrorStatus is null
                : CadWriteFailureReconciliationPolicy.IsRecoverableFuture(technicalStage, nativeErrorStatus)) ||
            !TryBoolean(evidence, "cleanupComplete", out var cleanupComplete) || !cleanupComplete ||
            !TryBoolean(evidence, "outputAbsent", out var outputAbsent) || !outputAbsent ||
            !TryBoolean(evidence, "candidateAbsent", out var candidateAbsent) || !candidateAbsent ||
            !TryBoolean(evidence, "normalizedBaselineAbsent", out var normalizedAbsent) || !normalizedAbsent ||
            !TryBoolean(evidence, "operationsAndLeasesQuiescent", out var quiescent) || !quiescent ||
            !TryInt64(evidence, "approvedCheckpointVersion", out var evidenceCheckpointVersion) ||
            evidenceCheckpointVersion != checkpointVersion ||
            !TrySha256(evidence, "approvedCheckpointHash") ||
            !string.Equals(evidence["approvedCheckpointHash"]!.GetValue<string>(),
                binding["approvedCheckpointHash"]!.GetValue<string>(), StringComparison.Ordinal) ||
            !TrySha256(evidence, "reviewHash") ||
            !string.Equals(evidence["reviewHash"]!.GetValue<string>(), binding["reviewHash"]!.GetValue<string>(), StringComparison.Ordinal) ||
            !TrySha256(binding, "reconciliationBindingHash"))
            return false;

        if (!string.Equals(binding["evidenceHash"]!.GetValue<string>(), CanonicalHash(evidence), StringComparison.Ordinal))
            return false;
        var unsealed = binding.DeepClone().AsObject();
        unsealed.Remove("reconciliationBindingHash");
        return string.Equals(binding["reconciliationBindingHash"]!.GetValue<string>(), CanonicalHash(unsealed), StringComparison.Ordinal);
    }
}
