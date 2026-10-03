using DwgTranslator.Application;

namespace DwgTranslator.Agent;

public static class AgentBatchPolicy
{
    public const string SchemaVersion = "dwg-agent-batch/1.0";
    public const string LegacyPolicyVersion = "unattended-explicit-review-create-new/1.0";
    public const string ContextualPolicyVersion = ReviewAutomationPolicy.ContextualAgentCreateNew;
    public const string PolicyVersion = ContextualPolicyVersion;

    public static bool IsSupported(string? value) =>
        value is LegacyPolicyVersion or ContextualPolicyVersion;
}

public sealed record AgentBatchPlanRequest(
    string SourceDirectory,
    string OutputDirectory,
    string TargetLanguage,
    string RoutingMode,
    string TerminologyVersion,
    string Policy);

public sealed record AgentBatchStartRequest(
    string PlanId,
    string ManifestHash,
    string ApprovalId,
    string Consent,
    string IdempotencyKey);

public sealed record AgentBatchIdRequest(Guid BatchId);
public sealed record AgentBatchVersionRequest(Guid BatchId, long ExpectedBatchVersion, string ApprovalId, string Approval, string IdempotencyKey);
public sealed record AgentBatchCancelRequest(Guid BatchId, long ExpectedBatchVersion, string IdempotencyKey);
public sealed record AgentBatchRecoveryPlanRequest(Guid OriginalBatchId, IReadOnlyList<string> RelativePaths);
public sealed record AgentBatchRecoveryStartRequest(string RecoveryPlanId, string RecoveryManifestHash,
    string ApprovalId, string Consent, string IdempotencyKey);
public sealed record AgentBatchReconcileReviewRequest(Guid BatchId, long ExpectedBatchVersion, string IdempotencyKey);

public sealed record AgentBatchManifestEntry(string RelativePath, string SourcePath, string OutputPath, long Bytes, string Sha256, DateTimeOffset LastWriteTimeUtc);
public sealed record AgentBatchApproval(string ApprovalId, string Consent, string ReviewAndGenerationApproval,
    DateTimeOffset ExpiresAtUtc, bool SingleUse, string? ReviewAndGenerationApprovalId = null);
public sealed record AgentBatchPlan(string PlanId, Guid BatchId, string ManifestHash, string SourceDirectory, string OutputDirectory,
    string TargetLanguage, string RoutingMode, string TerminologyVersion, string Policy, IReadOnlyList<AgentBatchManifestEntry> Files,
    AgentBatchApproval Approval, DateTimeOffset CreatedAtUtc,
    string? ContextPolicyVersion = null,
    string? PromptTemplateVersion = null,
    bool? ReviewIncludeText = null,
    int? MaximumNeighborExcerpts = null,
    int? MaximumNeighborExcerptScalars = null,
    int? MaximumNeighborExcerptScalarsPerSegment = null,
    string? OutputMode = null,
    string? ValidationPolicy = null,
    bool? SequentialCadExecution = null);

public enum AgentBatchFileState { Queued, Inspecting, Translating, Reviewing, Generating, Completed, Failed, Cancelled, Suspended }
public enum AgentBatchRecoveryAction { ContinueFromReview, ResumeTranslationMissingOnly, RetryCadFresh, RevalidateApprovedContext }
public sealed record AgentBatchRecoveryEntry(string RelativePath, AgentBatchRecoveryAction Action, Guid? AdoptedJobId,
    string SourceHash, string OutputPath, string? PriorErrorCode = null,
    ApprovedContextRevalidationBinding? ContextRevalidation = null);
public sealed record AgentBatchRecoveryPlan(string RecoveryPlanId, Guid RecoveryBatchId, Guid OriginalBatchId,
    string OriginalManifestHash, string RecoveryManifestHash, AgentBatchPlan Plan,
    IReadOnlyList<AgentBatchRecoveryEntry> Entries, AgentBatchApproval Approval, DateTimeOffset CreatedAtUtc,
    long OriginalBatchVersion = 0, string? OriginalBatchState = null);
public sealed record AgentBatchFileProgress(string RelativePath, string SourceHash, string OutputPath, AgentBatchFileState State,
    Guid? JobId = null, string? OutputHash = null, string? ErrorCode = null, bool Retryable = false,
    int TextCount = 0, int MTextCount = 0, long InputTokens = 0, long OutputTokens = 0, long ProviderRequests = 0,
    int TerminologyMatches = 0, int TerminologyAmbiguities = 0, int ReviewEdits = 0,
    int BoundsChanged = 0, bool VisualReviewPending = false,
    long JobVersion = 0, string? PrepareOperationId = null, string? GenerateOperationId = null,
    string? ChildPrepareApprovalHash = null, string? ChildGenerationApprovalHash = null,
    string? ReviewHash = null, string? EffectiveModel = null, int ExplicitDecisionCount = 0,
    string? ValidationPolicy = null,
    long OutputBytes = 0,
    long ReviewVersion = 0,
    string? ContextPolicyVersion = null,
    string? ContextHash = null,
    string? ReviewAutomationReceiptHash = null,
    string? ReviewGateCode = null);
public sealed record AgentBatchDocument(Guid BatchId, long Version, string State, string ManifestHash, string SourceDirectory,
    string OutputDirectory, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset HeartbeatAtUtc,
    IReadOnlyList<AgentBatchFileProgress> Files, string? CurrentFile = null, string? ErrorCode = null,
    string? StartIdempotencyKey = null, string? GenerationIdempotencyKey = null,
    Guid? OriginalBatchId = null, string? RecoveryPlanId = null,
    IReadOnlyList<AgentBatchRecoveryEntry>? RecoveryEntries = null,
    string? ReviewReconciliationIdempotencyKey = null,
    string? RefreshedGenerationApprovalId = null,
    string? RefreshedGenerationApproval = null,
    DateTimeOffset? RefreshedGenerationApprovalExpiresAtUtc = null,
    int ReviewReconciliationRetryCount = 0);

public interface IAgentBatchFileProcessor
{
    Task<AgentBatchFileProgress> PrepareAsync(AgentBatchPlan plan, AgentBatchManifestEntry file, CancellationToken cancellationToken);
    Task<AgentBatchFileProgress> ApproveAndGenerateAsync(AgentBatchPlan plan, AgentBatchFileProgress file, CancellationToken cancellationToken);
    Task<AgentBatchFileProgress> ReconcileReviewAsync(AgentBatchPlan plan, AgentBatchFileProgress file,
        ReviewAutomationScopeTransition? scopeTransition, CancellationToken cancellationToken) =>
        Task.FromResult(file with { State = AgentBatchFileState.Reviewing, ErrorCode = null, Retryable = false });
    Task<AgentBatchFileProgress> ResumeTranslationMissingOnlyAsync(AgentBatchPlan plan, AgentBatchFileProgress file,
        ReviewAutomationScopeTransition scopeTransition, CancellationToken cancellationToken) =>
        Task.FromResult(file with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_RECOVERY_PROCESSOR_UNAVAILABLE" });
    Task<AgentBatchFileProgress> RevalidateApprovedContextAsync(AgentBatchPlan plan, AgentBatchFileProgress file,
        ApprovedContextRevalidationTransition transition, CancellationToken cancellationToken) =>
        Task.FromResult(file with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_RECOVERY_PROCESSOR_UNAVAILABLE" });
}
