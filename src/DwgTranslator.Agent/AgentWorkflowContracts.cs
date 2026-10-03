using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

public static class AgentWorkflowContract
{
    public const string SchemaVersion = "dwg-agent-workflow/1.0";
    public const string ApprovalPolicyVersion = "dwg-agent-approval/1.0";
    public const string PathPolicyVersion = "dwg-agent-path-policy/1.0";
    public const string StateMachineVersion = "dwg-agent-state-machine/1.0";
    public const string TranslationOutboundPurpose = "external-processing-and-translation";
    public const bool TranslationIncludeNeighborExcerpts = true;
    public const bool TranslationReviewIncludeText = true;
}

public sealed record AgentTranslationPlanRequest(
    string SourcePath,
    string TargetLanguage,
    string RoutingMode,
    string? ManualModel,
    string? OutputPath,
    AgentChildBatchAuthority? ParentBatchAuthority = null);

public sealed record AgentChildBatchAuthority(
    Guid BatchId,
    string ManifestHash,
    string PolicyVersion,
    string SourceHash,
    string OutputPath,
    int FileIndex);

public sealed record AgentTranslationPrepareRequest(
    string PlanId,
    string ApprovalId,
    string SourceHash,
    string Consent,
    string IdempotencyKey);

public sealed record AgentTranslationReviewGetRequest(
    Guid JobId,
    int Page = 1,
    int PageSize = 50,
    bool IncludeText = false);

public sealed record AgentReviewDecision(
    string SegmentId,
    string Action,
    string? EditedText = null,
    string? ExclusionReason = null);

public sealed record AgentTranslationReviewApplyRequest(
    Guid JobId,
    long ExpectedJobVersion,
    IReadOnlyList<AgentReviewDecision>? Decisions,
    bool BulkApprove,
    string? BulkApproval,
    string IdempotencyKey,
    long? ExpectedReviewVersion = null,
    string? ExpectedContextHash = null,
    ReviewAutomationReceipt? AutomationAuthority = null,
    bool ReviseApproved = false);

public sealed record AgentGenerationPlanRequest(Guid JobId);

public sealed record AgentGenerateRequest(
    string GenerationPlanId,
    string ApprovalId,
    long ExpectedJobVersion,
    string Approval,
    string IdempotencyKey);

public sealed record AgentWorkflowCancelRequest(
    Guid JobId,
    long ExpectedJobVersion,
    string IdempotencyKey);

/// <summary>
/// Read-only request for a narrowly scoped return from a recoverable generation
/// failure to the durable Approved checkpoint.  The generated plan is the only
/// authority accepted by the apply endpoint.
/// </summary>
public sealed record AgentGenerationReconciliationPlanRequest(Guid JobId);

/// <summary>
/// One-time, version- and evidence-bound application of a generation recovery
/// plan.  It never starts CAD, OpenAI, review, or generation.
/// </summary>
public sealed record AgentGenerationReconciliationApplyRequest(
    string ReconciliationPlanId,
    string ApprovalId,
    long ExpectedJobVersion,
    string Consent,
    string IdempotencyKey);

/// <summary>Internal authority rebuilt from a sealed reconciliation plan; never accepted directly over HTTP.</summary>
public sealed record AgentGenerationReconciliationAuthority(
    string ReconciliationBindingHash,
    string FailureCode,
    ErrorCategory FailureCategory,
    bool FailureRetryable,
    long ApprovedCheckpointVersion,
    string ApprovedCheckpointHash,
    long ReviewVersion,
    string ReviewHash,
    int DecisionCount,
    string EvidenceHash,
    string? TechnicalStage = null,
    string? NativeErrorStatus = null,
    string? FailureStage = null);

/// <summary>
/// Internal, batch-scoped request to reopen a failed generation for review only after
/// its durable invariant evidence has been re-evaluated under the current policy.
/// </summary>
public sealed record AgentFailedGeometryReviewReconciliationRequest(
    Guid JobId,
    long ExpectedJobVersion,
    string SourceHash,
    string OutputPath);

public sealed record AgentWorkflowOperation(
    string OperationId,
    string Kind,
    Guid JobId,
    string State,
    string Stage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ErrorCode = null,
    bool Retryable = false,
    ErrorCategory? ErrorCategory = null,
    string? TechnicalStage = null,
    string? NativeErrorStatus = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? DeadlineAtUtc = null,
    DateTimeOffset? LastHeartbeatAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    long? DurationMilliseconds = null,
    string? CleanupOutcome = null);

/// <summary>
/// Redacted, durable evidence for one AutoCAD child session.  The receipt deliberately contains
/// hashes and counts only: it never persists DWG text or an IPC payload.
/// </summary>
public sealed record AgentCadLifecycleReceipt(
    Guid JobId,
    string? OperationId,
    string RequestId,
    string RequestHash,
    int ProcessId,
    string ExecutablePath,
    DateTimeOffset ProcessStartedAtUtc,
    DateTimeOffset LaunchedAtUtc,
    string? ResponseHash = null,
    int? ExtractedCount = null,
    DateTimeOffset? ReceivedAtUtc = null,
    DateTimeOffset? QuitRequestedAtUtc = null,
    DateTimeOffset? ProcessExitedAtUtc = null,
    int? ExitCode = null,
    string? CleanupOutcome = null,
    bool RequiresProcessTerminationApproval = false,
    string? ResponseErrorCode = null,
    ErrorCategory? ResponseErrorCategory = null,
    bool? ResponseRetryable = null,
    string? ResponseDiagnosticId = null,
    string? ResponseTechnicalStage = null,
    string? ResponseNativeErrorStatus = null);

public interface IAgentTranslationWorkflowBackend
{
    Task<Result<JobDocument>> CreateAsync(DwgTranslationJobSpecification specification, CancellationToken cancellationToken);
    Task<Result<JobDocument>> PrepareAsync(Guid jobId, CancellationToken cancellationToken);
    Task<Result<JobDocument>> ResumeTranslationAsync(Guid jobId, long expectedJobVersion,
        ReviewAutomationScopeTransition? scopeTransition, CancellationToken cancellationToken);
    Task<Result<JobDocument>> RebindReviewAutomationScopeAsync(Guid jobId, long expectedJobVersion,
        ReviewAutomationScopeTransition scopeTransition, CancellationToken cancellationToken);
    Task<Result<JobDocument>> RevalidateApprovedContextAsync(Guid jobId, long expectedJobVersion,
        ApprovedContextRevalidationTransition transition, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<JobDocument>(new ContractError(
            "RECOVERY_CONTEXT_REVALIDATION_BACKEND_UNAVAILABLE", ErrorCategory.Unsupported,
            "The workflow backend does not support approved-context revalidation.", false)));
    Task<Result<JobDocument>> LoadJobAsync(Guid jobId, CancellationToken cancellationToken);
    Task<Result<TranslationReviewSnapshot>> LoadReviewAsync(Guid jobId, CancellationToken cancellationToken);
    Task<Result<JobDocument>> ApproveAsync(Guid jobId, IReadOnlyList<ReviewDecisionInput> decisions,
        long expectedReviewVersion, ReviewAutomationReceipt? automationAuthority, CancellationToken cancellationToken);
    Task<Result<JobDocument>> ReviseApprovedReviewAsync(Guid jobId, long expectedJobVersion,
        IReadOnlyList<ReviewDecisionInput> decisions, long expectedReviewVersion,
        ReviewAutomationReceipt automationAuthority, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<JobDocument>(new ContractError(
            "APPROVED_REVIEW_REVISION_UNAVAILABLE", ErrorCategory.Unsupported,
            "The workflow backend does not support approved review revision.", false)));
    Task<Result<JobDocument>> ReconcileFailedGenerationToApprovedAsync(Guid jobId, long expectedVersion,
        AgentGenerationReconciliationAuthority authority, CancellationToken cancellationToken);
    Task<Result<JobDocument>> ReconcileFailedGeometryForReviewAsync(Guid jobId, long expectedVersion, CancellationToken cancellationToken);
    Task<Result<JobDocument>> GenerateAsync(Guid jobId, TranslationReviewFingerprint expectedReview,
        CancellationToken cancellationToken);
    Task<Result<JobDocument>> CancelAsync(Guid jobId, long expectedVersion, CancellationToken cancellationToken);
    Task<Result<JobDocument>> FailAsync(Guid jobId, string code, bool retryable, CancellationToken cancellationToken);
}
