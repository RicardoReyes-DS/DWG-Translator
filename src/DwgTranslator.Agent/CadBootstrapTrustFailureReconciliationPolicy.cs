namespace DwgTranslator.Agent;

/// <summary>No historical bootstrap incident is authorized in this candidate.</summary>
internal sealed class CadBootstrapTrustFailureReconciliationPolicy
{
    public static CadBootstrapTrustFailureReconciliationPolicy Instance { get; } = new();
    internal CadBootstrapTrustFailureReconciliationPolicy() { }

    public string PolicyVersion { get; init; } = "cad-bootstrap-trust-reconciliation/1.0";
    public Guid JobId { get; init; }
    public long FailedJobVersion { get; init; }
    public long ApprovedCheckpointVersion { get; init; }
    public int ReviewDecisionCount { get; init; }
    public long ReviewVersion { get; init; }
    public string SourceFileName { get; init; } = string.Empty;
    public string OutputFileName { get; init; } = string.Empty;
    public string SourceHash { get; init; } = string.Empty;
    public string JobConfigurationHash { get; init; } = string.Empty;
    public string FailedJobArtifactHash { get; init; } = string.Empty;
    public string ReviewArtifactHash { get; init; } = string.Empty;
    public string ReviewHash { get; init; } = string.Empty;
    public string ContextHash { get; init; } = string.Empty;
    public string ReviewAutomationReceiptHash { get; init; } = string.Empty;
    public string ApprovedCheckpointArtifactHash { get; init; } = string.Empty;
    public string GenerationPlanId { get; init; } = string.Empty;
    public string GenerationPlanArtifactHash { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string OperationArtifactHash { get; init; } = string.Empty;
    public string GenerationMarkerName { get; init; } = string.Empty;
    public string GenerationMarkerArtifactHash { get; init; } = string.Empty;
    public string GenerationRequestBodyHash { get; init; } = string.Empty;
    public string BootstrapArtifactName { get; init; } = string.Empty;
    public string BootstrapArtifactHash { get; init; } = string.Empty;
    public string WritingAuditArtifactHash { get; init; } = string.Empty;
    public string FailureAuditArtifactHash { get; init; } = string.Empty;

#pragma warning disable CA1822 // Keep the historical instance API while every binding is denied.
    public bool IsExactFailure(Guid jobId, long failedVersion, string? code,
        string? category, bool hasRetryable, bool retryable, string? stage,
        string? technicalStage, string? nativeErrorStatus) => false;
#pragma warning restore CA1822
}
