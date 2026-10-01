using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

/// <summary>Generic, typed pre-candidate failure recovery. Historical exceptions are disabled.</summary>
internal static class CadWriteFailureReconciliationPolicy
{
    internal static CadWriteLegacyRecoveryBinding AuthorizedLegacy { get; } = new();

    private static readonly HashSet<string> RecoverableFutureStages = new(StringComparer.Ordinal)
    {
        CadWriteTechnicalStages.NormalizedBaselineCopy,
        CadWriteTechnicalStages.NormalizedBaselineOpen,
        CadWriteTechnicalStages.NormalizedBaselinePrime,
        CadWriteTechnicalStages.NormalizedBaselineSave,
        CadWriteTechnicalStages.NormalizedBaselineStabilize,
        CadWriteTechnicalStages.SourceRevalidate
    };

    private static readonly HashSet<string> RecoverableFutureStatuses = new(StringComparer.Ordinal)
    {
        "InvalidInput", "InvalidOpenState", "NotOpenForRead", "NotOpenForWrite",
        "WasNotOpenForWrite", "NoDatabase", "GeneralModelingFailure"
    };

    internal static bool IsRecoverableFuture(string? stage, string? nativeStatus)
    {
        if (string.Equals(stage, CadWriteTechnicalStages.CandidateStabilize, StringComparison.Ordinal))
            return nativeStatus is null;

        return stage is not null && nativeStatus is not null &&
            RecoverableFutureStages.Contains(stage) && RecoverableFutureStatuses.Contains(nativeStatus);
    }
}

internal sealed record CadWriteLegacyRecoveryBinding
{
    internal Guid JobId { get; init; }
    internal long FailedJobVersion { get; init; }
    internal long ApprovedCheckpointVersion { get; init; }
    internal long ReviewVersion { get; init; }
    internal int DecisionCount { get; init; }
    internal string SourceHash { get; init; } = string.Empty;
    internal string JobConfigurationHash { get; init; } = string.Empty;
    internal string FailureActiveConfigurationHash { get; init; } = string.Empty;
    internal string ConfigurationSecurityProjectionHash { get; init; } = string.Empty;
    internal string JobArtifactHash { get; init; } = string.Empty;
    internal string ReviewArtifactHash { get; init; } = string.Empty;
    internal string ReviewHash { get; init; } = string.Empty;
    internal string ContextHash { get; init; } = string.Empty;
    internal string ReviewAutomationReceiptHash { get; init; } = string.Empty;
    internal string CheckpointArtifactHash { get; init; } = string.Empty;
    internal string GenerationPlanId { get; init; } = string.Empty;
    internal string GenerationPlanArtifactHash { get; init; } = string.Empty;
    internal string OperationId { get; init; } = string.Empty;
    internal string OperationArtifactHash { get; init; } = string.Empty;
    internal string CadReceiptArtifactHash { get; init; } = string.Empty;
    internal string GenerationIdempotencyArtifactHash { get; init; } = string.Empty;
    internal string GenerationIdempotencyArtifactName { get; init; } = string.Empty;
    internal string GenerationRequestBodyHash { get; init; } = string.Empty;
    internal string RequestHash { get; init; } = string.Empty;
    internal string ResponseHash { get; init; } = string.Empty;
    internal string WritingAuditArtifactHash { get; init; } = string.Empty;
    internal string FailureAuditArtifactHash { get; init; } = string.Empty;
}
