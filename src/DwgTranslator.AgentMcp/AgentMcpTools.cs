using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DwgTranslator.AgentMcp;

[McpServerToolType]
public sealed class AgentMcpTools(IAgentHostReadClient host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "dwg_health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Check the isolated DWG Translator Agent Beta host and workspace health without opening AutoCAD.")]
    public Task<CallToolResult> HealthAsync(CancellationToken cancellationToken) =>
        WrapAsync(host.HealthAsync(cancellationToken));

    [McpServerTool(Name = "dwg_capabilities", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("List the effective read-only capabilities exposed by DWG Translator Agent Beta.")]
    public Task<CallToolResult> CapabilitiesAsync(CancellationToken cancellationToken) =>
        WrapAsync(host.CapabilitiesAsync(cancellationToken));

    [McpServerTool(Name = "dwg_jobs_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("List translation jobs in the isolated Agent Beta workspace. This never reads the stable workspace.")]
    public Task<CallToolResult> ListJobsAsync(CancellationToken cancellationToken) =>
        WrapAsync(host.ListJobsAsync(cancellationToken));

    [McpServerTool(Name = "dwg_job_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Get one Agent Beta translation job by UUID without modifying it.")]
    public Task<CallToolResult> GetJobAsync(
        [Description("Translation job UUID in canonical form.")] string jobId,
        CancellationToken cancellationToken) =>
        Guid.TryParse(jobId, out var parsed) && parsed != Guid.Empty
            ? WrapAsync(host.GetJobAsync(parsed, cancellationToken))
            : WrapAsync(Task.FromResult(new AgentMcpResult("dwg-agent-cli/1.0", false, "jobs get", DateTimeOffset.UtcNow, null,
                new("AGENT_JOB_ID_INVALID", "A valid non-empty job UUID is required."))));

    [McpServerTool(Name = "dwg_inspection_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Validate an allowlisted DWG path and calculate its approval-bound SHA-256 without opening AutoCAD.")]
    public Task<CallToolResult> CreateInspectionPlanAsync(
        [Description("Absolute .dwg path below the configured Agent Beta input root.")] string sourcePath,
        CancellationToken cancellationToken) =>
        WrapAsync(host.CreateInspectionPlanAsync(sourcePath, cancellationToken));

    [McpServerTool(Name = "dwg_inspect_dry_run", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Open a hash-approved DWG with AutoCAD read-only, inspect and extract TEXT/MTEXT, then verify the source stayed unchanged. Never translates or writes DWG.")]
    public Task<CallToolResult> InspectDwgAsync(
        [Description("Absolute .dwg path returned by dwg_inspection_plan.")] string sourcePath,
        [Description("Exact sourceHash returned by dwg_inspection_plan, including the sha256: prefix.")] string expectedSourceHash,
        [Description("Exact human approval phrase returned by dwg_inspection_plan.")] string approval,
        CancellationToken cancellationToken) =>
        WrapAsync(host.InspectDwgAsync(sourcePath, expectedSourceHash, approval, cancellationToken));

    [McpServerTool(Name = "dwg_invariant_diff_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Create a hash- and AutoCAD-2026-bound read-only differential plan for source/candidate DWG pairs. It never starts AutoCAD.")]
    public Task<CallToolResult> CreateInvariantDiffPlanAsync(
        [Description("One to three source/candidate pairs; each object has sourcePath, candidatePath and optional jobId.")] IReadOnlyList<AgentMcpInvariantDiffPair>? pairs,
        CancellationToken cancellationToken = default) =>
        WrapAsync(host.CreateInvariantDiffPlanAsync(new { pairs }, cancellationToken));

    [McpServerTool(Name = "dwg_invariant_diff_run", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: consume one invariant-diff plan and open only its source/candidate DWGs through configured AutoCAD 2026 in read-only mode. Never saves or writes DWG.")]
    public Task<CallToolResult> RunInvariantDiffAsync(string planId, string planHash, string approvalId, string consent, string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WrapAsync(host.RunInvariantDiffAsync(new { planId, planHash, approvalId, consent, idempotencyKey }, cancellationToken));

    [McpServerTool(Name = "dwg_translation_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Plan a DWG translation, hash the source, validate routes/models/output, and issue a single-use plan-bound approval artifact. Does not open AutoCAD or call OpenAI.")]
    public Task<CallToolResult> CreateTranslationPlanAsync(
        [Description("Absolute input DWG path below InputRoot.")] string sourcePath,
        [Description("BCP-47 target language tag.")] string targetLanguage,
        [Description("Routing mode: Auto, Economy, MaximumQuality, or Manual.")] string routingMode = "Auto",
        [Description("Exact accessible model required only for Manual; ambiguous gpt-5.6 is forbidden.")] string? manualModel = null,
        [Description("Optional absolute new .dwg path below OutputRoot; it must not exist.")] string? outputPath = null,
        CancellationToken cancellationToken = default) =>
        WrapAsync(host.CreateTranslationPlanAsync(new { sourcePath, targetLanguage, routingMode, manualModel, outputPath }, cancellationToken));

    [McpServerTool(Name = "dwg_translation_prepare", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: consume a current translation plan and explicit external-processing consent, then start durable read-only CAD extraction and routed OpenAI translation. Creates ReviewRequired proposals; never writes a DWG.")]
    public Task<CallToolResult> PrepareTranslationAsync(
        string planId, string approvalId, string sourceHash, string consent, string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WrapAsync(host.PrepareTranslationAsync(new { planId, approvalId, sourceHash, consent, idempotencyKey }, cancellationToken));

    [McpServerTool(Name = "dwg_translation_review_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Read a page of review metadata, risk codes, effective models and escalations. Text is excluded by default and included only when includeText=true.")]
    public Task<CallToolResult> GetTranslationReviewAsync(
        string jobId, int page = 1, int pageSize = 50, bool includeText = false,
        CancellationToken cancellationToken = default) => Guid.TryParse(jobId, out var parsed) && parsed != Guid.Empty
        ? WrapAsync(host.GetTranslationReviewAsync(new { jobId = parsed, page, pageSize, includeText }, cancellationToken))
        : InvalidJob("translation review get");

    [McpServerTool(Name = "dwg_translation_review_apply", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: atomically apply one decision per segment at the exact job/review/context version. Contextual automation requires a batch/manifest/reviewer/QA hash-bound authority receipt. Bulk approval needs a job-bound phrase and is rejected when any segment is high risk. Does not call CAD or OpenAI.")]
    public Task<CallToolResult> ApplyTranslationReviewAsync(
        string jobId,
        long expectedJobVersion,
        IReadOnlyList<AgentMcpReviewDecision>? decisions,
        bool bulkApprove,
        string? bulkApproval,
        string idempotencyKey,
        long? expectedReviewVersion = null,
        string? expectedContextHash = null,
        AgentMcpReviewAutomationReceipt? automationAuthority = null,
        CancellationToken cancellationToken = default) => Guid.TryParse(jobId, out var parsed) && parsed != Guid.Empty
        ? WrapAsync(host.ApplyTranslationReviewAsync(new
        {
            jobId = parsed,
            expectedJobVersion,
            decisions,
            bulkApprove,
            bulkApproval,
            idempotencyKey,
            expectedReviewVersion,
            expectedContextHash,
            automationAuthority
        }, cancellationToken))
        : InvalidJob("translation review apply");

    [McpServerTool(Name = "dwg_generation_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Plan generation for an Approved job and issue a single-use approval bound to job version, source/review hashes and canonical new output. Does not open AutoCAD.")]
    public Task<CallToolResult> CreateGenerationPlanAsync(string jobId, CancellationToken cancellationToken = default) =>
        Guid.TryParse(jobId, out var parsed) && parsed != Guid.Empty
            ? WrapAsync(host.CreateGenerationPlanAsync(new { jobId = parsed }, cancellationToken))
            : InvalidJob("generation plan");

    [McpServerTool(Name = "dwg_generation_reconcile_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Create a read-only, evidence-bound plan to restore an allowlisted failed generation to its existing Approved checkpoint. Never opens CAD, calls OpenAI, or writes DWG.")]
    public Task<CallToolResult> CreateGenerationReconciliationPlanAsync(string jobId, CancellationToken cancellationToken = default) =>
        Guid.TryParse(jobId, out var parsed) && parsed != Guid.Empty
            ? WrapAsync(host.CreateGenerationReconciliationPlanAsync(new { jobId = parsed }, cancellationToken))
            : InvalidJob("generation reconciliation plan");

    [McpServerTool(Name = "dwg_generation_reconcile_apply", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: atomically restore one allowlisted failed generation to its durable Approved checkpoint at the exact job version. Does not open CAD, call OpenAI, or write DWG.")]
    public Task<CallToolResult> ApplyGenerationReconciliationAsync(string reconciliationPlanId, string approvalId, long expectedJobVersion,
        string consent, string idempotencyKey, CancellationToken cancellationToken = default) =>
        WrapAsync(host.ApplyGenerationReconciliationAsync(new { reconciliationPlanId, approvalId, expectedJobVersion, consent, idempotencyKey }, cancellationToken));

    [McpServerTool(Name = "dwg_generate", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: consume an exact generation plan and start one durable CAD write/VisualStrictV2 validation. Publishes with CreateNew only on PASS; never calls OpenAI.")]
    public Task<CallToolResult> GenerateAsync(
        string generationPlanId, string approvalId, long expectedJobVersion, string approval, string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WrapAsync(host.GenerateAsync(new { generationPlanId, approvalId, expectedJobVersion, approval, idempotencyKey }, cancellationToken));

    [McpServerTool(Name = "dwg_job_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Get durable workflow state, stage progress, redacted failure metadata, routing summary and validation status without returning drawing text.")]
    public Task<CallToolResult> JobStatusAsync(string jobId, CancellationToken cancellationToken = default) =>
        Guid.TryParse(jobId, out var parsed) && parsed != Guid.Empty
            ? WrapAsync(host.JobStatusAsync(parsed, cancellationToken))
            : InvalidJob("workflow job status");

    [McpServerTool(Name = "dwg_job_next_action", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Return the safe next MCP tool for a durable job. Completed operations are never suggested again.")]
    public Task<CallToolResult> JobNextActionAsync(string jobId, CancellationToken cancellationToken = default) =>
        Guid.TryParse(jobId, out var parsed) && parsed != Guid.Empty
            ? WrapAsync(host.JobNextActionAsync(parsed, cancellationToken))
            : InvalidJob("workflow job next action");

    [McpServerTool(Name = "dwg_workflow_cancel", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: cancel a workflow only before Writing and only at the exact job version. Evidence is retained.")]
    public Task<CallToolResult> CancelWorkflowAsync(
        string jobId, long expectedJobVersion, string idempotencyKey,
        CancellationToken cancellationToken = default) => Guid.TryParse(jobId, out var parsed) && parsed != Guid.Empty
        ? WrapAsync(host.CancelWorkflowAsync(new { jobId = parsed, expectedJobVersion, idempotencyKey }, cancellationToken))
        : InvalidJob("workflow cancel");

    [McpServerTool(Name = "dwg_batch_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Create a manifest/hash-bound batch approval plan. Enumerates only safe DWG files and never creates output, opens CAD, or calls OpenAI.")]
    public Task<CallToolResult> CreateBatchPlanAsync(string sourceDirectory, string outputDirectory, string targetLanguage,
        string routingMode, string terminologyVersion, string policy, CancellationToken cancellationToken = default) =>
        WrapAsync(host.CreateBatchPlanAsync(new { sourceDirectory, outputDirectory, targetLanguage, routingMode, terminologyVersion, policy }, cancellationToken));

    [McpServerTool(Name = "dwg_batch_start", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: consume an exact batch plan/manifest consent, create the new output directory, and start sequential durable extraction/translation.")]
    public Task<CallToolResult> StartBatchAsync(string planId, string manifestHash, string approvalId, string consent,
        string idempotencyKey, CancellationToken cancellationToken = default) =>
        WrapAsync(host.StartBatchAsync(new { planId, manifestHash, approvalId, consent, idempotencyKey }, cancellationToken));

    [McpServerTool(Name = "dwg_batch_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Read durable aggregate batch progress, heartbeat, usage, current file and redacted errors.")]
    public Task<CallToolResult> BatchStatusAsync(string batchId, CancellationToken cancellationToken = default) => BatchId(batchId, "batch status", host.BatchStatusAsync, cancellationToken);

    [McpServerTool(Name = "dwg_batch_next_action", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Return the safe next batch tool without replaying completed CAD/OpenAI work.")]
    public Task<CallToolResult> BatchNextActionAsync(string batchId, CancellationToken cancellationToken = default) => BatchId(batchId, "batch next action", host.BatchNextActionAsync, cancellationToken);

    [McpServerTool(Name = "dwg_batch_review_summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Read aggregate terminology, risk and bounds review evidence. Text is excluded by default.")]
    public Task<CallToolResult> BatchReviewSummaryAsync(string batchId, CancellationToken cancellationToken = default) => BatchId(batchId, "batch review summary", host.BatchReviewSummaryAsync, cancellationToken);

    [McpServerTool(Name = "dwg_batch_approve_and_generate", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: apply explicit validator-safe decisions under the manifest-bound policy and generate sequential CreateNew DWGs with VisualStrictV2.")]
    public Task<CallToolResult> ApproveAndGenerateBatchAsync(string batchId, long expectedBatchVersion, string approvalId,
        string approval, string idempotencyKey, CancellationToken cancellationToken = default) => Guid.TryParse(batchId, out var parsed) && parsed != Guid.Empty
        ? WrapAsync(host.ApproveAndGenerateBatchAsync(new { batchId = parsed, expectedBatchVersion, approvalId, approval, idempotencyKey }, cancellationToken))
        : InvalidBatch("batch approve and generate");

    [McpServerTool(Name = "dwg_batch_report", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Read the redacted per-file batch report: hashes, counts, model usage, terminology, decisions, validation and failures.")]
    public Task<CallToolResult> BatchReportAsync(string batchId, CancellationToken cancellationToken = default) => BatchId(batchId, "batch report", host.BatchReportAsync, cancellationToken);

    [McpServerTool(Name = "dwg_batch_cancel", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: cancel queued batch work before the current generation stage; retain durable evidence.")]
    public Task<CallToolResult> CancelBatchAsync(string batchId, long expectedBatchVersion, string idempotencyKey,
        CancellationToken cancellationToken = default) => Guid.TryParse(batchId, out var parsed) && parsed != Guid.Empty
        ? WrapAsync(host.CancelBatchAsync(new { batchId = parsed, expectedBatchVersion, idempotencyKey }, cancellationToken))
        : InvalidBatch("batch cancel");

    [McpServerTool(Name = "dwg_batch_recovery_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Create a new manifest-bound recovery plan for an explicit subset. It suspends nothing and never opens CAD or calls OpenAI.")]
    public Task<CallToolResult> CreateBatchRecoveryPlanAsync(string originalBatchId, IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default) => Guid.TryParse(originalBatchId, out var parsed) && parsed != Guid.Empty
        ? WrapAsync(host.CreateBatchRecoveryPlanAsync(new { originalBatchId = parsed, relativePaths }, cancellationToken))
        : InvalidBatch("batch recovery plan");

    [McpServerTool(Name = "dwg_batch_recovery_start", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("APPROVAL REQUIRED: atomically suspend the original batch and start only the exact approved recovery subset.")]
    public Task<CallToolResult> StartBatchRecoveryAsync(string recoveryPlanId, string recoveryManifestHash, string approvalId,
        string consent, string idempotencyKey, CancellationToken cancellationToken = default) =>
        WrapAsync(host.StartBatchRecoveryAsync(new { recoveryPlanId, recoveryManifestHash, approvalId, consent, idempotencyKey }, cancellationToken));

    [McpServerTool(Name = "dwg_batch_reconcile_review", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AgentMcpResult))]
    [Description("Reconcile every review-ready child job into the same batch, clear historical errors, rebuild redacted metrics, and issue a fresh version-bound generation approval. Never opens CAD, calls OpenAI, or writes DWG.")]
    public Task<CallToolResult> ReconcileBatchReviewAsync(string batchId, long expectedBatchVersion, string idempotencyKey,
        CancellationToken cancellationToken = default) => Guid.TryParse(batchId, out var parsed) && parsed != Guid.Empty
        ? WrapAsync(host.ReconcileBatchReviewAsync(new { batchId = parsed, expectedBatchVersion, idempotencyKey }, cancellationToken))
        : InvalidBatch("batch reconcile review");

    private static Task<CallToolResult> BatchId(string value, string command,
        Func<Guid, CancellationToken, Task<AgentMcpResult>> action, CancellationToken cancellationToken) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? WrapAsync(action(parsed, cancellationToken)) : InvalidBatch(command);
    private static Task<CallToolResult> InvalidBatch(string command) => WrapAsync(Task.FromResult(
        new AgentMcpResult("dwg-agent-cli/1.0", false, command, DateTimeOffset.UtcNow, null,
            new("BATCH_ID_INVALID", "A valid non-empty batch UUID is required."))));

    private static Task<CallToolResult> InvalidJob(string command) => WrapAsync(Task.FromResult(
        new AgentMcpResult("dwg-agent-cli/1.0", false, command, DateTimeOffset.UtcNow, null,
            new("AGENT_JOB_ID_INVALID", "A valid non-empty job UUID is required."))));

    private static async Task<CallToolResult> WrapAsync(Task<AgentMcpResult> pending)
    {
        var result = await pending.ConfigureAwait(false);
        var serialized = JsonSerializer.Serialize(result, Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = serialized }],
            StructuredContent = JsonSerializer.SerializeToElement(result, Json),
            IsError = !result.Success
        };
    }
}

public sealed record AgentMcpReviewDecision(
    [property: Description("Stable segment ID from dwg_translation_review_get.")] string SegmentId,
    [property: Description("approve, edit, or exclude.")] string Action,
    [property: Description("Required only for edit.")] string? EditedText = null,
    [property: Description("Required only for exclude.")] string? ExclusionReason = null);

public sealed record AgentMcpReviewAutomationReceipt(
    [property: Description("Must be contextual-agent-review-create-new/1.0.")] string PolicyVersion,
    [property: Description("Parent batch UUID bound to the child job.")] string BatchId,
    [property: Description("SHA-256 parent manifest fingerprint.")] string ManifestHash,
    [property: Description("SHA-256 aggregate semantic context fingerprint.")] string ContextHash,
    [property: Description("SHA-256 redacted reviewer report fingerprint.")] string ReviewerReportHash,
    [property: Description("SHA-256 redacted independent QA report fingerprint.")] string QaReportHash,
    [property: Description("Optional single hash-bound vertical-context resolution; required only when the 1.2 review contains that conflict.")]
    IReadOnlyList<AgentMcpSegmentContextResolutionAuthority>? SegmentContextResolutions = null);

public sealed record AgentMcpSegmentContextResolutionAuthority(
    string PolicyVersion,
    string SegmentId,
    string SourceTextHash,
    string ContextHash,
    string FinalTextHash,
    string Signal,
    string Resolution,
    string ReviewerReportHash,
    string QaReportHash,
    string AuthorityHash);

public sealed record AgentMcpInvariantDiffPair(string SourcePath, string CandidatePath, string? JobId = null);
