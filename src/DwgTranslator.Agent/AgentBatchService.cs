using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Agent;

public sealed class AgentBatchService
{
    private static readonly string[] AutoApproveRisk = ["none", "low"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly AgentBetaConfiguration _configuration;
    private readonly IAgentBatchFileProcessor _processor;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<Func<Task>, Task> _schedule;
    private readonly Func<bool> _cadProcessExists;
    private readonly object _gate = new();

    public AgentBatchService(AgentBetaConfiguration configuration, IAgentBatchFileProcessor processor,
        Func<DateTimeOffset>? utcNow = null, Func<Func<Task>, Task>? schedule = null,
        Func<bool>? cadProcessExists = null)
    {
        _configuration = configuration;
        _processor = processor;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _schedule = schedule ?? (work => Task.Run(work));
        _cadProcessExists = cadProcessExists ?? DefaultCadProcessExists;
    }

    public AgentEnvelope Plan(AgentBatchPlanRequest request)
    {
        const string command = "batch plan";
        if (request.TargetLanguage != "en-US" || request.RoutingMode != "Auto" ||
            request.TerminologyVersion != Application.ArchitecturalMepTerminologyPolicy.Version ||
            !AgentBatchPolicy.IsSupported(request.Policy))
            return Failure(command, "BATCH_POLICY_INVALID", "The requested batch policy is not the approved versioned policy.");
        var snapshot = AgentBatchFileSystemPolicy.Enumerate(request.SourceDirectory, request.OutputDirectory,
            _configuration.AllowedDwgRoot, _configuration.BatchOutputRoots ?? []);
        if (snapshot.Error is not null) return Failure(command, snapshot.Error);
        var files = snapshot.Files!;
        if (files.Count == 0) return Failure(command, "BATCH_EMPTY", "The source directory contains no eligible DWG files.");
        var manifestHash = ManifestHash(files);
        var now = _utcNow();
        var batchId = Guid.NewGuid();
        var planId = Opaque();
        var approvalId = Opaque();
        var generationApprovalId = Opaque();
        var consent = $"I_APPROVE_EXTERNAL_PROCESSING_FOR_BATCH:{planId}:{manifestHash}";
        var approval = $"I_APPROVE_BATCH_REVIEW_AND_GENERATION:{batchId:D}:{manifestHash}:{request.TerminologyVersion}:{request.Policy}";
        var plan = new AgentBatchPlan(planId, batchId, manifestHash, Path.GetFullPath(request.SourceDirectory),
            Path.GetFullPath(request.OutputDirectory), request.TargetLanguage, request.RoutingMode,
            request.TerminologyVersion, request.Policy, files,
            new(approvalId, consent, approval, now.AddMinutes(_configuration.ApprovalLifetimeMinutes), true,
                generationApprovalId), now,
            CadSemanticContextBuilder.CurrentPolicyVersion, _configuration.PromptTemplateVersion, true,
            TranslationBatchFactory.MaximumNeighborExcerpts, TranslationBatchFactory.MaximumNeighborExcerptScalars,
            TranslationBatchFactory.MaximumNeighborExcerptScalarsPerSegment, "CreateNew", "VisualStrictV2", true);
        SavePlan(plan);
        return Success(command, JsonSerializer.SerializeToNode(new
        {
            schemaVersion = AgentBatchPolicy.SchemaVersion,
            plan.PlanId,
            plan.BatchId,
            plan.ManifestHash,
            plan.SourceDirectory,
            plan.OutputDirectory,
            plan.TargetLanguage,
            plan.RoutingMode,
            plan.TerminologyVersion,
            plan.Policy,
            plan.ContextPolicyVersion,
            plan.PromptTemplateVersion,
            plan.ReviewIncludeText,
            plan.MaximumNeighborExcerpts,
            plan.MaximumNeighborExcerptScalars,
            plan.MaximumNeighborExcerptScalarsPerSegment,
            plan.OutputMode,
            plan.ValidationPolicy,
            plan.SequentialCadExecution,
            fileCount = files.Count,
            totalBytes = files.Sum(file => file.Bytes),
            files,
            approval = plan.Approval,
            reviewPolicy = new
            {
                explicitDecisionsPerSegment = true,
                autoApproveRisk = AutoApproveRisk,
                highRisk = "agent-explicit-evaluate-edit-or-stop",
                ambiguity = request.Policy == AgentBatchPolicy.ContextualPolicyVersion
                    ? "context-bound; conflict-or-unknown-stops-file"
                    : "legacy-explicit-review",
                invariantFailure = "stop-file:BATCH_HUMAN_REVIEW_REQUIRED",
                continueOtherFilesWhenSafe = true
            },
            generationPolicy = new
            {
                createMode = "CreateNew",
                validation = "VisualStrictV2",
                sequentialCad = true,
                overwrite = false,
                openAiDuringGeneration = false
            },
            opensAutoCad = false,
            callsOpenAi = false,
            writesDwg = false,
            createsOutputDirectory = false
        }, Json));
    }

    public int ReconcileRunningBatches()
    {
        if (!Directory.Exists(BatchRoot)) return 0;
        var reconciled = 0;
        foreach (var path in Directory.EnumerateFiles(BatchRoot, "batch.json", SearchOption.AllDirectories))
        {
            var document = Read<AgentBatchDocument>(path);
            if (document is null) continue;
            // A host restart must never resume CAD/OpenAI work without an explicit, newly-bound recovery approval.
            if (document.State is "Preparing" or "Generating")
            {
                SaveBatch(document with
                {
                    Version = document.Version + 1,
                    State = "RecoveryRequired",
                    ErrorCode = "BATCH_RECOVERY_REQUIRED",
                    UpdatedAtUtc = _utcNow(),
                    HeartbeatAtUtc = _utcNow(),
                    CurrentFile = null,
                    Files = document.Files.Select(file => file.State is AgentBatchFileState.Inspecting or AgentBatchFileState.Translating or AgentBatchFileState.Generating
                        ? file with { State = AgentBatchFileState.Suspended } : file).ToArray()
                });
                reconciled++;
            }
        }
        return reconciled;
    }

    public AgentEnvelope Start(AgentBatchStartRequest request)
    {
        const string command = "batch start";
        var plan = LoadPlan(request.PlanId);
        if (plan is null) return Failure(command, "BATCH_PLAN_NOT_FOUND", "The batch plan was not found.");
        if (!ValidContextualPlanBinding(plan))
            return Failure(command, "BATCH_CONTEXT_BINDING_INVALID", "The contextual batch plan is missing or changed required workflow bindings.");
        if (_utcNow() >= plan.Approval.ExpiresAtUtc) return Failure(command, "APPROVAL_EXPIRED", "The batch approval expired.");
        if (request.ManifestHash != plan.ManifestHash || request.ApprovalId != plan.Approval.ApprovalId || request.Consent != plan.Approval.Consent)
            return Failure(command, "APPROVAL_REQUIRED", "The exact manifest-bound batch consent is required.");
        var existing = LoadBatch(plan.BatchId);
        if (existing is not null) return existing.StartIdempotencyKey == request.IdempotencyKey
            ? Success(command, JsonSerializer.SerializeToNode(new { batchId = plan.BatchId, batchVersion = existing.Version, state = existing.State, idempotentReplay = true }, Json))
            : Failure(command, "IDEMPOTENCY_CONFLICT", "The batch start already used a different idempotency key.");
        var current = AgentBatchFileSystemPolicy.Enumerate(plan.SourceDirectory, plan.OutputDirectory,
            _configuration.AllowedDwgRoot, _configuration.BatchOutputRoots ?? []);
        if (current.Error is not null || ManifestHash(current.Files ?? []) != plan.ManifestHash)
            return Failure(command, "BATCH_SOURCE_MUTATED", "The source snapshot no longer matches the approved manifest.");
        try { Directory.CreateDirectory(plan.OutputDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return Failure(command, "BATCH_OUTPUT_CREATE_FAILED", "The approved output directory could not be created."); }
        if (Directory.EnumerateFileSystemEntries(plan.OutputDirectory).Any())
            return Failure(command, "OUTPUT_ALREADY_EXISTS", "The new batch output directory is not empty.");
        var now = _utcNow();
        var document = new AgentBatchDocument(plan.BatchId, 1, "Preparing", plan.ManifestHash, plan.SourceDirectory,
            plan.OutputDirectory, now, now, now, plan.Files.Select(file => new AgentBatchFileProgress(file.RelativePath,
            file.Sha256, file.OutputPath, AgentBatchFileState.Queued)).ToArray(), StartIdempotencyKey: request.IdempotencyKey);
        SaveBatch(document);
        _ = _schedule(() => PrepareAsync(plan));
        return Success(command, JsonSerializer.SerializeToNode(new { batchId = plan.BatchId, batchVersion = 1, state = "Started", idempotentReplay = false, nextTool = "dwg_batch_status" }, Json));
    }

    public AgentEnvelope Status(Guid batchId) => Query("batch status", batchId,
        document => AgentBatchProjection.Status(document, includeFiles: false, Json));
    public AgentEnvelope ReviewSummary(Guid batchId) => Query("batch review summary", batchId,
        document => AgentBatchProjection.Status(document, includeFiles: true, Json));
    public AgentEnvelope Report(Guid batchId) => Query("batch report", batchId,
        document => AgentBatchProjection.Status(document, includeFiles: true, Json));
    public AgentEnvelope NextAction(Guid batchId) => Query("batch next action", batchId,
        document => AgentBatchProjection.NextAction(batchId, document, Json));

    public AgentEnvelope RecoveryPlan(AgentBatchRecoveryPlanRequest request)
    {
        const string command = "batch recovery plan";
        var original = LoadBatch(request.OriginalBatchId);
        var originalPlan = LoadPlanByBatch(request.OriginalBatchId);
        if (original is null || originalPlan is null) return Failure(command, "BATCH_NOT_FOUND", "The original batch and its immutable plan are required.");
        if (request.RelativePaths is null || request.RelativePaths.Count == 0 || request.RelativePaths.Count > original.Files.Count ||
            request.RelativePaths.Any(path => string.IsNullOrWhiteSpace(path)) ||
            request.RelativePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.RelativePaths.Count)
            return Failure(command, "BATCH_RECOVERY_SUBSET_INVALID", "Recovery requires a non-empty unique subset of the original batch.");
        var entries = new List<AgentBatchRecoveryEntry>();
        foreach (var relative in request.RelativePaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = original.Files.SingleOrDefault(candidate => string.Equals(candidate.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
            if (file is null) return Failure(command, "BATCH_RECOVERY_FILE_NOT_IN_ORIGINAL", "A requested recovery file is not part of the original manifest.");
            var resolved = ResolveRecoveryAction(original, originalPlan, file);
            if (resolved.Error is not null) return Failure(command, resolved.Error);
            var action = resolved.Action;
            if (action is null) return Failure(command, "BATCH_RECOVERY_ACTION_UNSAFE", "The requested file has no deterministic recovery action.");
            entries.Add(new(file.RelativePath, action.Value, file.JobId, file.SourceHash, file.OutputPath,
                file.ErrorCode, resolved.ContextRevalidation));
        }
        if (entries.Any(entry => entry.Action == AgentBatchRecoveryAction.RevalidateApprovedContext) &&
            entries.Any(entry => entry.Action != AgentBatchRecoveryAction.RevalidateApprovedContext))
            return Failure(command, "BATCH_RECOVERY_ACTION_SET_MIXED",
                "Approved-context revalidation cannot be mixed with translation or CAD recovery actions.");
        if (entries.All(entry => entry.Action == AgentBatchRecoveryAction.RevalidateApprovedContext) &&
            !HistoricalIncidentRecoveryPolicy.IsAuthorizedRecoverySubset(original, entries))
            return Failure(command, "BATCH_RECOVERY_APPROVED_CONTEXT_SUBSET_INVALID",
                "Approved-context revalidation has no authorized historical binding in this candidate.");
        var manifest = RecoveryManifestHash(original.ManifestHash, original.Version, original.State, entries);
        var now = _utcNow(); var batchId = Guid.NewGuid(); var planId = Opaque();
        var approval = new AgentBatchApproval(Opaque(), $"I_APPROVE_EXTERNAL_PROCESSING_FOR_RECOVERY_BATCH:{planId}:{manifest}",
            $"I_APPROVE_RECOVERY_BATCH_REVIEW_AND_GENERATION:{batchId:D}:{manifest}:{originalPlan.TerminologyVersion}:{originalPlan.Policy}",
            now.AddMinutes(_configuration.ApprovalLifetimeMinutes), true, Opaque());
        var selected = originalPlan.Files.Where(file => entries.Any(entry => entry.RelativePath == file.RelativePath)).ToArray();
        var plan = new AgentBatchPlan(planId, batchId, manifest, originalPlan.SourceDirectory, originalPlan.OutputDirectory,
            originalPlan.TargetLanguage, originalPlan.RoutingMode, originalPlan.TerminologyVersion, originalPlan.Policy, selected, approval, now,
            entries[0].Action == AgentBatchRecoveryAction.RevalidateApprovedContext
                ? CadSemanticContextBuilder.PolicyVersionOneTwo : originalPlan.ContextPolicyVersion,
            originalPlan.PromptTemplateVersion, originalPlan.ReviewIncludeText,
            originalPlan.MaximumNeighborExcerpts, originalPlan.MaximumNeighborExcerptScalars,
            originalPlan.MaximumNeighborExcerptScalarsPerSegment, originalPlan.OutputMode,
            originalPlan.ValidationPolicy, originalPlan.SequentialCadExecution);
        var recovery = new AgentBatchRecoveryPlan(planId, batchId, original.BatchId, original.ManifestHash, manifest, plan, entries, approval, now,
            original.Version, original.State);
        SaveRecoveryPlan(recovery);
        return Success(command, JsonSerializer.SerializeToNode(new
        {
            recovery.RecoveryPlanId,
            recovery.RecoveryBatchId,
            recovery.OriginalBatchId,
            recovery.OriginalManifestHash,
            recovery.RecoveryManifestHash,
            entries,
            approval,
            expiresAtUtc = approval.ExpiresAtUtc,
            originalDisposition = "SuspendedOnRecoveryStart",
            outputReuse = "empty-owned-original-output-only",
            opensAutoCad = false,
            callsOpenAi = false,
            writesDwg = false
        }, Json));
    }

    public AgentEnvelope StartRecovery(AgentBatchRecoveryStartRequest request)
    {
        const string command = "batch recovery start";
        var recovery = LoadRecoveryPlan(request.RecoveryPlanId);
        if (recovery is null) return Failure(command, "BATCH_RECOVERY_PLAN_NOT_FOUND", "The recovery plan was not found.");
        if (!ValidContextualPlanBinding(recovery.Plan))
            return Failure(command, "BATCH_CONTEXT_BINDING_INVALID", "The contextual recovery plan is missing or changed required workflow bindings.");
        if (_utcNow() >= recovery.Approval.ExpiresAtUtc) return Failure(command, "APPROVAL_EXPIRED", "The recovery approval expired.");
        if (request.RecoveryManifestHash != recovery.RecoveryManifestHash || request.ApprovalId != recovery.Approval.ApprovalId || request.Consent != recovery.Approval.Consent)
            return Failure(command, "APPROVAL_REQUIRED", "The exact recovery external-processing consent is required.");
        lock (_gate)
        {
            var original = LoadBatch(recovery.OriginalBatchId);
            if (original is null) return Failure(command, "BATCH_NOT_FOUND", "The original batch no longer exists.");
            if (LoadBatch(recovery.RecoveryBatchId) is { } existing)
                return existing.StartIdempotencyKey == request.IdempotencyKey
                    ? Success(command, JsonSerializer.SerializeToNode(new { batchId = existing.BatchId, batchVersion = existing.Version, state = existing.State, idempotentReplay = true }, Json))
                    : Failure(command, "IDEMPOTENCY_CONFLICT", "The recovery start already used a different idempotency key.");
            var originalPlan = LoadPlanByBatch(recovery.OriginalBatchId);
            if (!RecoveryEvidenceStillMatches(recovery, original, originalPlan))
                return Failure(command, "BATCH_RECOVERY_EVIDENCE_CHANGED",
                    "The original batch or its durable child evidence changed after the recovery plan was created.");
            var snapshot = AgentBatchFileSystemPolicy.Enumerate(recovery.Plan.SourceDirectory, recovery.Plan.OutputDirectory,
                _configuration.AllowedDwgRoot, _configuration.BatchOutputRoots ?? [], requireOutputAbsent: false);
            if (snapshot.Error is not null || snapshot.Files is null ||
                recovery.Plan.Files.Any(file => !snapshot.Files.Any(now => now.RelativePath == file.RelativePath && now.Sha256 == file.Sha256 && now.Bytes == file.Bytes)))
                return Failure(command, "BATCH_SOURCE_MUTATED", "The recovery source snapshot does not match its inherited manifest.");
            var outputSafety = OwnedCompatibleOutput(original, recovery);
            if (!outputSafety.IsSafe)
                return Failure(command, "BATCH_RECOVERY_OUTPUT_UNSAFE",
                    $"Recovery output safety check failed: {outputSafety.Category} at {outputSafety.Path}.");
            var now = _utcNow();
            var initial = recovery.Entries.Select(entry =>
            {
                var prior = original.Files.Single(file => file.RelativePath == entry.RelativePath);
                return entry.Action switch
                {
                    AgentBatchRecoveryAction.ContinueFromReview => prior with
                    {
                        State = AgentBatchFileState.Queued,
                        ErrorCode = null,
                        Retryable = false
                    },
                    AgentBatchRecoveryAction.ResumeTranslationMissingOnly => prior with { State = AgentBatchFileState.Translating },
                    AgentBatchRecoveryAction.RevalidateApprovedContext => prior with
                    {
                        State = AgentBatchFileState.Queued,
                        ErrorCode = null,
                        Retryable = false,
                        JobVersion = entry.ContextRevalidation!.ExpectedJobVersion
                    },
                    _ => new AgentBatchFileProgress(prior.RelativePath, prior.SourceHash, prior.OutputPath, AgentBatchFileState.Queued)
                };
            }).ToArray();
            SaveBatch(original with
            {
                Version = original.Version + 1,
                State = "Suspended",
                ErrorCode = "BATCH_SUPERSEDED_BY_RECOVERY",
                UpdatedAtUtc = now,
                HeartbeatAtUtc = now,
                CurrentFile = null
            });
            var document = new AgentBatchDocument(recovery.RecoveryBatchId, 1, "Preparing", recovery.RecoveryManifestHash,
                recovery.Plan.SourceDirectory, recovery.Plan.OutputDirectory, now, now, now, initial, StartIdempotencyKey: request.IdempotencyKey,
                OriginalBatchId: recovery.OriginalBatchId, RecoveryPlanId: recovery.RecoveryPlanId, RecoveryEntries: recovery.Entries);
            SaveBatch(document);
        }
        _ = _schedule(() => PrepareRecoveryAsync(recovery));
        return Success(command, JsonSerializer.SerializeToNode(new { batchId = recovery.RecoveryBatchId, batchVersion = 1, state = "Started", nextTool = "dwg_batch_status", idempotentReplay = false }, Json));
    }

    public AgentEnvelope ApproveAndGenerate(AgentBatchVersionRequest request)
    {
        const string command = "batch approve and generate";
        var document = LoadBatch(request.BatchId);
        if (document is null) return Failure(command, "BATCH_NOT_FOUND", "The batch was not found.");
        var plan = LoadPlanByBatch(request.BatchId);
        if (plan is null) return Failure(command, "BATCH_PLAN_NOT_FOUND", "The batch plan was not found.");
        if (!ValidContextualPlanBinding(plan))
            return Failure(command, "BATCH_CONTEXT_BINDING_INVALID", "The contextual batch plan is missing or changed required workflow bindings.");
        if (document.GenerationIdempotencyKey is not null)
            return document.GenerationIdempotencyKey == request.IdempotencyKey
                ? Success(command, JsonSerializer.SerializeToNode(new { batchId = request.BatchId, batchVersion = document.Version, state = document.State, idempotentReplay = true }, Json))
                : Failure(command, "IDEMPOTENCY_CONFLICT", "Batch generation already used a different idempotency key.");
        if (document.Version != request.ExpectedBatchVersion || document.State != "ReviewRequired")
            return Failure(command, "BATCH_VERSION_CONFLICT", "The batch version or state changed.");
        var approvalId = document.RefreshedGenerationApprovalId ?? plan.Approval.ReviewAndGenerationApprovalId;
        var approval = document.RefreshedGenerationApproval ?? plan.Approval.ReviewAndGenerationApproval;
        if (document.RefreshedGenerationApprovalExpiresAtUtc is { } expiresAt && _utcNow() >= expiresAt)
            return Failure(command, "APPROVAL_EXPIRED", "The reconciled batch generation approval expired.");
        if (request.ApprovalId != approvalId || request.Approval != approval)
            return Failure(command, "APPROVAL_REQUIRED", "The exact batch review/generation approval is required.");
        var next = document with { Version = document.Version + 1, State = "Generating", UpdatedAtUtc = _utcNow(), HeartbeatAtUtc = _utcNow(), GenerationIdempotencyKey = request.IdempotencyKey };
        SaveBatch(next);
        _ = _schedule(() => GenerateAsync(plan));
        return Success(command, JsonSerializer.SerializeToNode(new { batchId = request.BatchId, batchVersion = next.Version, state = "Started", nextTool = "dwg_batch_status" }, Json));
    }

    public AgentEnvelope ReconcileReview(AgentBatchReconcileReviewRequest request)
    {
        const string command = "batch reconcile review";
        AgentBatchDocument next;
        lock (_gate)
        {
            var document = LoadBatch(request.BatchId);
            if (document is null) return Failure(command, "BATCH_NOT_FOUND", "The batch was not found.");
            var retry = CanRetryFailedReviewReconciliation(document, request);
            if (document.ReviewReconciliationIdempotencyKey is not null && !retry)
                return document.ReviewReconciliationIdempotencyKey == request.IdempotencyKey
                    ? Success(command, JsonSerializer.SerializeToNode(new
                    {
                        batchId = request.BatchId,
                        batchVersion = document.Version,
                        state = document.State,
                        idempotentReplay = true
                    }, Json))
                    : Failure(command, "IDEMPOTENCY_CONFLICT", "Batch review reconciliation already used a different idempotency key.");
            if (!retry && (document.Version != request.ExpectedBatchVersion || document.State != "ReviewRequired"))
                return Failure(command, "BATCH_VERSION_CONFLICT", "The batch version or state changed.");
            if (!retry && (document.Files.Count == 0 || document.Files.Any(file => file.State != AgentBatchFileState.Reviewing || file.JobId is null)))
                return Failure(command, "BATCH_REVIEW_RECONCILIATION_INVALID_STATE", "Every batch file must have an authoritative review job.");
            next = document with
            {
                Version = document.Version + 1,
                State = "Preparing",
                UpdatedAtUtc = _utcNow(),
                HeartbeatAtUtc = _utcNow(),
                CurrentFile = null,
                ErrorCode = null,
                ReviewReconciliationIdempotencyKey = request.IdempotencyKey,
                RefreshedGenerationApprovalId = null,
                RefreshedGenerationApproval = null,
                RefreshedGenerationApprovalExpiresAtUtc = null,
                ReviewReconciliationRetryCount = retry ? 1 : 0,
                Files = document.Files.Select(file => file with
                {
                    State = AgentBatchFileState.Queued,
                    ErrorCode = null,
                    Retryable = false
                }).ToArray()
            };
            SaveBatch(next);
        }
        _ = _schedule(() => ReconcileReviewsAsync(request.BatchId));
        return Success(command, JsonSerializer.SerializeToNode(new
        {
            batchId = request.BatchId,
            batchVersion = next.Version,
            state = "Started",
            idempotentReplay = false,
            nextTool = "dwg_batch_status",
            opensAutoCad = false,
            callsOpenAi = false,
            writesDwg = false
        }, Json));
    }

    private bool CanRetryFailedReviewReconciliation(AgentBatchDocument document,
        AgentBatchReconcileReviewRequest request) =>
        document.ReviewReconciliationIdempotencyKey == request.IdempotencyKey &&
        document.Version == request.ExpectedBatchVersion &&
        document.State == "RecoveryRequired" &&
        document.ErrorCode == "BATCH_REVIEW_RECONCILIATION_FAILED" &&
        document.ReviewReconciliationRetryCount == 0 &&
        !_cadProcessExists() &&
        document.Files.Count > 0 && document.Files.All(file =>
            file.State == AgentBatchFileState.Failed &&
            (file.ErrorCode is "BATCH_CHILD_REVIEW_NOT_READY" or "BATCH_SUPERSEDING_APPROVED_REVIEW_INVALID") &&
            file.JobId is { } jobId && file.JobVersion > 0 &&
            file.GenerateOperationId is null && file.OutputHash is null &&
            !File.Exists(file.OutputPath) && !Directory.Exists(file.OutputPath) &&
            !HasContextRecoveryArtifacts(file.OutputPath, jobId));

    public AgentEnvelope Cancel(AgentBatchCancelRequest request)
    {
        const string command = "batch cancel";
        var current = LoadBatch(request.BatchId);
        if (current is null) return Failure(command, "BATCH_NOT_FOUND", "The batch was not found.");
        if (current.Version != request.ExpectedBatchVersion || current.State == "Generating")
            return Failure(command, "BATCH_CANCEL_CONFLICT", "The batch cannot be cancelled at this version/stage.");
        var next = current with
        {
            Version = current.Version + 1,
            State = "Cancelled",
            UpdatedAtUtc = _utcNow(),
            HeartbeatAtUtc = _utcNow(),
            Files = current.Files.Select(file => file.State == AgentBatchFileState.Queued ? file with { State = AgentBatchFileState.Cancelled } : file).ToArray()
        };
        SaveBatch(next);
        return Success(command, JsonSerializer.SerializeToNode(new { batchId = request.BatchId, batchVersion = next.Version, state = next.State }, Json));
    }

    private async Task PrepareAsync(AgentBatchPlan plan)
    {
        var consecutiveCadStalls = 0;
        foreach (var manifest in plan.Files)
        {
            var current = LoadBatch(plan.BatchId)!;
            if (current.State is "Cancelled" or "Suspended" or "RecoveryRequired") return;
            var index = current.Files.ToList().FindIndex(file => file.RelativePath == manifest.RelativePath);
            if (current.Files[index].State is AgentBatchFileState.Reviewing or AgentBatchFileState.Completed or AgentBatchFileState.Failed) continue;
            UpdateFile(current, index, current.Files[index] with { State = AgentBatchFileState.Inspecting }, "Preparing", manifest.RelativePath);
            AgentBatchFileProgress result;
            try { result = await _processor.PrepareAsync(plan, manifest, CancellationToken.None).ConfigureAwait(false); }
            catch { result = current.Files[index] with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_FILE_PREPARE_FAILED" }; }
            current = LoadBatch(plan.BatchId)!;
            UpdateFile(current, index, result, "Preparing", null);
            consecutiveCadStalls = result.ErrorCode is "WORKFLOW_BACKGROUND_STALLED" or "CAD_PROCESS_EXIT_REQUIRED"
                ? consecutiveCadStalls + 1 : 0;
            if (consecutiveCadStalls >= 2)
            {
                var suspended = LoadBatch(plan.BatchId)!;
                SaveBatch(suspended with
                {
                    Version = suspended.Version + 1,
                    State = "Suspended",
                    ErrorCode = "BATCH_CAD_CIRCUIT_OPEN",
                    UpdatedAtUtc = _utcNow(),
                    HeartbeatAtUtc = _utcNow(),
                    CurrentFile = null,
                    Files = suspended.Files.Select(file => file.State == AgentBatchFileState.Queued ? file with { State = AgentBatchFileState.Suspended } : file).ToArray()
                });
                return;
            }
        }
        var final = LoadBatch(plan.BatchId)!;
        var hasReviewableFiles = final.Files.Any(file => file.State == AgentBatchFileState.Reviewing);
        var requiresRecovery = !hasReviewableFiles && final.Files.Any(file => file.State == AgentBatchFileState.Failed);
        SaveBatch(final with
        {
            Version = final.Version + 1,
            State = requiresRecovery ? "RecoveryRequired" : "ReviewRequired",
            ErrorCode = requiresRecovery ? "BATCH_RECOVERY_REQUIRED" : null,
            UpdatedAtUtc = _utcNow(),
            HeartbeatAtUtc = _utcNow(),
            CurrentFile = null
        });
    }

    private async Task PrepareRecoveryAsync(AgentBatchRecoveryPlan recovery)
    {
        foreach (var entry in recovery.Entries)
        {
            var current = LoadBatch(recovery.RecoveryBatchId);
            if (current is null || current.State is "Cancelled" or "Suspended" or "RecoveryRequired") return;
            var index = current.Files.ToList().FindIndex(file => file.RelativePath == entry.RelativePath);
            if (index < 0) { SaveBatch(current with { State = "Suspended", ErrorCode = "BATCH_RECOVERY_DOCUMENT_INVALID" }); return; }
            var file = current.Files[index];
            AgentBatchFileProgress result;
            try
            {
                result = entry.Action switch
                {
                    AgentBatchRecoveryAction.ContinueFromReview => await _processor.ReconcileReviewAsync(
                        recovery.Plan, file, RecoveryScopeTransition(recovery, entry, file), CancellationToken.None).ConfigureAwait(false),
                    AgentBatchRecoveryAction.ResumeTranslationMissingOnly => await _processor.ResumeTranslationMissingOnlyAsync(
                        recovery.Plan, file, RecoveryScopeTransition(recovery, entry, file), CancellationToken.None).ConfigureAwait(false),
                    AgentBatchRecoveryAction.RevalidateApprovedContext => await _processor.RevalidateApprovedContextAsync(
                        recovery.Plan, file, ApprovedContextTransition(recovery, entry, file), CancellationToken.None).ConfigureAwait(false),
                    AgentBatchRecoveryAction.RetryCadFresh => await _processor.PrepareAsync(recovery.Plan,
                        recovery.Plan.Files.Single(candidate => candidate.RelativePath == entry.RelativePath), CancellationToken.None).ConfigureAwait(false),
                    _ => file with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_RECOVERY_ACTION_UNSAFE" }
                };
            }
            catch { result = file with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_RECOVERY_FILE_FAILED" }; }
            current = LoadBatch(recovery.RecoveryBatchId)!;
            UpdateFile(current, index, result, "Preparing", null);
        }
        var final = LoadBatch(recovery.RecoveryBatchId)!;
        if (final.Files.Any(file => file.State != AgentBatchFileState.Reviewing))
        {
            SaveBatch(final with
            {
                Version = final.Version + 1,
                State = "RecoveryRequired",
                ErrorCode = "BATCH_RECOVERY_RECONCILIATION_FAILED",
                UpdatedAtUtc = _utcNow(),
                HeartbeatAtUtc = _utcNow(),
                CurrentFile = null
            });
            return;
        }
        SaveBatch(final with { Version = final.Version + 1, State = "ReviewRequired", UpdatedAtUtc = _utcNow(), HeartbeatAtUtc = _utcNow(), CurrentFile = null });
    }

    private async Task GenerateAsync(AgentBatchPlan plan)
    {
        foreach (var file in LoadBatch(plan.BatchId)!.Files.Where(file => file.State == AgentBatchFileState.Reviewing))
        {
            var current = LoadBatch(plan.BatchId)!;
            var index = current.Files.ToList().FindIndex(item => item.RelativePath == file.RelativePath);
            UpdateFile(current, index, file with { State = AgentBatchFileState.Generating }, "Generating", file.RelativePath);
            AgentBatchFileProgress result;
            try { result = await _processor.ApproveAndGenerateAsync(plan, file, CancellationToken.None).ConfigureAwait(false); }
            catch { result = file with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_FILE_GENERATE_FAILED" }; }
            current = LoadBatch(plan.BatchId)!;
            UpdateFile(current, index, result, "Generating", null);
        }
        var final = LoadBatch(plan.BatchId)!;
        var state = final.Files.Any(file => file.State == AgentBatchFileState.Failed) ? "CompletedWithFailures" : "Completed";
        SaveBatch(final with { Version = final.Version + 1, State = state, UpdatedAtUtc = _utcNow(), HeartbeatAtUtc = _utcNow(), CurrentFile = null });
    }

    private async Task ReconcileReviewsAsync(Guid batchId)
    {
        var plan = LoadPlanByBatch(batchId);
        if (plan is null)
        {
            var missing = LoadBatch(batchId);
            if (missing is not null)
                SaveBatch(missing with
                {
                    Version = missing.Version + 1,
                    State = "RecoveryRequired",
                    ErrorCode = "BATCH_PLAN_NOT_FOUND",
                    UpdatedAtUtc = _utcNow(),
                    HeartbeatAtUtc = _utcNow()
                });
            return;
        }
        var batch = LoadBatch(batchId);
        AgentBatchRecoveryPlan? recovery = null;
        if (batch?.RecoveryPlanId is { } recoveryPlanId)
        {
            recovery = LoadRecoveryPlan(recoveryPlanId);
            if (recovery is null || !ValidRecoveryScopePlan(batch, recovery))
            {
                SaveBatch(batch with
                {
                    Version = batch.Version + 1,
                    State = "RecoveryRequired",
                    ErrorCode = "BATCH_RECOVERY_DOCUMENT_INVALID",
                    UpdatedAtUtc = _utcNow(),
                    HeartbeatAtUtc = _utcNow()
                });
                return;
            }
        }
        foreach (var selected in LoadBatch(batchId)!.Files.Where(file => file.State == AgentBatchFileState.Queued))
        {
            var current = LoadBatch(batchId)!;
            if (current.State != "Preparing") return;
            var index = current.Files.ToList().FindIndex(file => file.RelativePath == selected.RelativePath);
            AgentBatchFileProgress result;
            try
            {
                var entry = recovery?.Entries.Single(candidate => string.Equals(candidate.RelativePath,
                    current.Files[index].RelativePath, StringComparison.OrdinalIgnoreCase));
                var transition = entry is null || entry.Action is AgentBatchRecoveryAction.RetryCadFresh or
                        AgentBatchRecoveryAction.RevalidateApprovedContext
                    ? null
                    : RecoveryScopeTransition(recovery!, entry, current.Files[index]);
                result = await _processor.ReconcileReviewAsync(
                    plan, current.Files[index], transition, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                result = current.Files[index] with
                {
                    State = AgentBatchFileState.Failed,
                    ErrorCode = "BATCH_REVIEW_RECONCILIATION_FAILED",
                    Retryable = false
                };
            }
            current = LoadBatch(batchId)!;
            UpdateFile(current, index, result, "Preparing", null);
        }
        var final = LoadBatch(batchId)!;
        if (final.Files.Any(file => file.State == AgentBatchFileState.Failed))
        {
            SaveBatch(final with
            {
                Version = final.Version + 1,
                State = "RecoveryRequired",
                ErrorCode = "BATCH_REVIEW_RECONCILIATION_FAILED",
                UpdatedAtUtc = _utcNow(),
                HeartbeatAtUtc = _utcNow(),
                CurrentFile = null
            });
            return;
        }
        var version = final.Version + 1;
        var approvalId = Opaque();
        var approval = $"I_APPROVE_BATCH_REVIEW_AND_GENERATION:{batchId:D}:{final.ManifestHash}:{plan.TerminologyVersion}:{plan.Policy}:V{version}";
        SaveBatch(final with
        {
            Version = version,
            State = "ReviewRequired",
            ErrorCode = null,
            UpdatedAtUtc = _utcNow(),
            HeartbeatAtUtc = _utcNow(),
            CurrentFile = null,
            RefreshedGenerationApprovalId = approvalId,
            RefreshedGenerationApproval = approval,
            RefreshedGenerationApprovalExpiresAtUtc = _utcNow().AddMinutes(_configuration.ApprovalLifetimeMinutes)
        });
    }

    private static bool ValidContextualPlanBinding(AgentBatchPlan plan) =>
        plan.Policy != AgentBatchPolicy.ContextualPolicyVersion ||
        CadSemanticContextBuilder.IsSupportedPolicyVersion(plan.ContextPolicyVersion) &&
        plan.PromptTemplateVersion == TranslationReviewWorkflow.ContextualPromptTemplateVersion &&
        plan.ReviewIncludeText == true &&
        plan.MaximumNeighborExcerpts == TranslationBatchFactory.MaximumNeighborExcerpts &&
        plan.MaximumNeighborExcerptScalars == TranslationBatchFactory.MaximumNeighborExcerptScalars &&
        plan.MaximumNeighborExcerptScalarsPerSegment == TranslationBatchFactory.MaximumNeighborExcerptScalarsPerSegment &&
        plan.OutputMode == "CreateNew" && plan.ValidationPolicy == "VisualStrictV2" &&
        plan.SequentialCadExecution == true;

    private void UpdateFile(AgentBatchDocument current, int index, AgentBatchFileProgress file, string state, string? currentFile)
    {
        var files = current.Files.ToArray(); files[index] = file;
        SaveBatch(current with { Version = current.Version + 1, State = state, UpdatedAtUtc = _utcNow(), HeartbeatAtUtc = _utcNow(), CurrentFile = currentFile, Files = files });
    }

    private AgentEnvelope Query(string command, Guid id, Func<AgentBatchDocument, JsonNode> selector)
    { var document = LoadBatch(id); return document is null ? Failure(command, "BATCH_NOT_FOUND", "The batch was not found.") : Success(command, selector(document)); }
    private string BatchRoot => Path.Combine(_configuration.WorkspaceRoot, "batches");
    private string PlanPath(string id) => Path.Combine(BatchRoot, "plans", id + ".json");
    private string RecoveryPlanPath(string id) => Path.Combine(BatchRoot, "plans", "recovery-" + id + ".json");
    private string BatchPath(Guid id) => Path.Combine(BatchRoot, id.ToString("D"), "batch.json");
    private void SavePlan(AgentBatchPlan plan) => AtomicWrite(PlanPath(plan.PlanId), plan);
    private void SaveRecoveryPlan(AgentBatchRecoveryPlan plan) => AtomicWrite(RecoveryPlanPath(plan.RecoveryPlanId), plan);
    private void SaveBatch(AgentBatchDocument document) { lock (_gate) AtomicWrite(BatchPath(document.BatchId), document); }
    private AgentBatchPlan? LoadPlan(string id) => Read<AgentBatchPlan>(PlanPath(id));
    private AgentBatchRecoveryPlan? LoadRecoveryPlan(string id) => Read<AgentBatchRecoveryPlan>(RecoveryPlanPath(id));
    private AgentBatchPlan? LoadPlanByBatch(Guid id)
    {
        var root = Path.Combine(BatchRoot, "plans");
        if (!Directory.Exists(root)) return null;
        var direct = Directory.EnumerateFiles(root, "*.json")
            .Where(path => !Path.GetFileName(path).StartsWith("recovery-", StringComparison.Ordinal))
            .Select(Read<AgentBatchPlan>)
            .FirstOrDefault(plan => plan?.BatchId == id);
        if (direct is not null) return direct;
        return Directory.EnumerateFiles(root, "recovery-*.json")
            .Select(Read<AgentBatchRecoveryPlan>)
            .Select(recovery => recovery?.Plan)
            .FirstOrDefault(plan => plan?.BatchId == id);
    }
    private AgentBatchDocument? LoadBatch(Guid id) { lock (_gate) return Read<AgentBatchDocument>(BatchPath(id)); }
    private static T? Read<T>(string path) { try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; } catch (JsonException) { return default; } }
    private static void AtomicWrite<T>(string path, T value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, true); }
    private static string ManifestHash(IReadOnlyList<AgentBatchManifestEntry> files) { var material = string.Join("\n", files.Select(file => $"{file.RelativePath}\0{file.Bytes}\0{file.Sha256}\0{file.LastWriteTimeUtc:O}")); return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant(); }
    private static string RecoveryManifestHash(
        string originalManifestHash,
        long originalBatchVersion,
        string originalBatchState,
        IReadOnlyList<AgentBatchRecoveryEntry> entries)
    {
        var ordered = entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var historical = ordered.All(entry => entry.ContextRevalidation is null);
        var material = (historical ? string.Empty : ApprovedContextRevalidationPolicy.Version + "\n") +
            $"{originalManifestHash}\n{originalBatchVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{originalBatchState}\n" +
            string.Join("\n", ordered.Select(entry =>
                $"{entry.RelativePath}\0{entry.Action}\0{entry.SourceHash}\0{entry.OutputPath}\0{entry.AdoptedJobId:D}\0{entry.PriorErrorCode}" +
                (historical ? string.Empty : $"\0{entry.ContextRevalidation?.BindingHash}")));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private bool RecoveryEvidenceStillMatches(
        AgentBatchRecoveryPlan recovery,
        AgentBatchDocument original,
        AgentBatchPlan? originalPlan)
    {
        if (originalPlan is null || original.BatchId != recovery.OriginalBatchId ||
            originalPlan.BatchId != original.BatchId || original.Version != recovery.OriginalBatchVersion ||
            !string.Equals(original.State, recovery.OriginalBatchState, StringComparison.Ordinal) ||
            !string.Equals(original.ManifestHash, recovery.OriginalManifestHash, StringComparison.Ordinal) ||
            !string.Equals(originalPlan.ManifestHash, original.ManifestHash, StringComparison.Ordinal) ||
            !RecoverySubplanStillMatchesOriginal(recovery, originalPlan) ||
            recovery.Entries.Count == 0 ||
            recovery.Entries.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != recovery.Entries.Count ||
            !string.Equals(RecoveryManifestHash(original.ManifestHash, original.Version, original.State, recovery.Entries),
                recovery.RecoveryManifestHash, StringComparison.Ordinal))
            return false;

        foreach (var entry in recovery.Entries)
        {
            var matches = original.Files.Where(file =>
                string.Equals(file.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return false;
            var file = matches[0];
            var resolved = ResolveRecoveryAction(original, originalPlan, file);
            if (resolved.Error is not null || resolved.Action != entry.Action ||
                file.JobId != entry.AdoptedJobId ||
                !string.Equals(file.SourceHash, entry.SourceHash, StringComparison.Ordinal) ||
                !PathsEqual(file.OutputPath, entry.OutputPath) ||
                !string.Equals(file.ErrorCode, entry.PriorErrorCode, StringComparison.Ordinal) ||
                !string.Equals(resolved.ContextRevalidation?.BindingHash,
                    entry.ContextRevalidation?.BindingHash, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool RecoverySubplanStillMatchesOriginal(
        AgentBatchRecoveryPlan recovery,
        AgentBatchPlan originalPlan)
    {
        try
        {
            var plan = recovery.Plan;
            if (!string.Equals(plan.PlanId, recovery.RecoveryPlanId, StringComparison.Ordinal) ||
                plan.BatchId != recovery.RecoveryBatchId ||
                !string.Equals(plan.ManifestHash, recovery.RecoveryManifestHash, StringComparison.Ordinal) ||
                !PathsEqual(plan.SourceDirectory, originalPlan.SourceDirectory) ||
                !PathsEqual(plan.OutputDirectory, originalPlan.OutputDirectory) ||
                !string.Equals(plan.TargetLanguage, originalPlan.TargetLanguage, StringComparison.Ordinal) ||
                !string.Equals(plan.RoutingMode, originalPlan.RoutingMode, StringComparison.Ordinal) ||
                !string.Equals(plan.TerminologyVersion, originalPlan.TerminologyVersion, StringComparison.Ordinal) ||
                !string.Equals(plan.Policy, originalPlan.Policy, StringComparison.Ordinal) ||
                !string.Equals(plan.ContextPolicyVersion,
                    recovery.Entries.All(entry => entry.Action == AgentBatchRecoveryAction.RevalidateApprovedContext)
                        ? CadSemanticContextBuilder.PolicyVersionOneTwo : originalPlan.ContextPolicyVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(plan.PromptTemplateVersion, originalPlan.PromptTemplateVersion, StringComparison.Ordinal) ||
                plan.ReviewIncludeText != originalPlan.ReviewIncludeText ||
                plan.MaximumNeighborExcerpts != originalPlan.MaximumNeighborExcerpts ||
                plan.MaximumNeighborExcerptScalars != originalPlan.MaximumNeighborExcerptScalars ||
                plan.MaximumNeighborExcerptScalarsPerSegment != originalPlan.MaximumNeighborExcerptScalarsPerSegment ||
                !string.Equals(plan.OutputMode, originalPlan.OutputMode, StringComparison.Ordinal) ||
                !string.Equals(plan.ValidationPolicy, originalPlan.ValidationPolicy, StringComparison.Ordinal) ||
                plan.SequentialCadExecution != originalPlan.SequentialCadExecution ||
                plan.Approval != recovery.Approval ||
                plan.CreatedAtUtc != recovery.CreatedAtUtc ||
                plan.Files.Count != recovery.Entries.Count)
                return false;

            foreach (var entry in recovery.Entries)
            {
                var originalFiles = originalPlan.Files.Where(file =>
                    string.Equals(file.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                var recoveryFiles = plan.Files.Where(file =>
                    string.Equals(file.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (originalFiles.Length != 1 || recoveryFiles.Length != 1) return false;
                var original = originalFiles[0];
                var selected = recoveryFiles[0];
                if (!string.Equals(selected.RelativePath, original.RelativePath, StringComparison.Ordinal) ||
                    !PathsEqual(selected.SourcePath, original.SourcePath) ||
                    !PathsEqual(selected.OutputPath, original.OutputPath) ||
                    selected.Bytes != original.Bytes ||
                    !string.Equals(selected.Sha256, original.Sha256, StringComparison.Ordinal) ||
                    selected.LastWriteTimeUtc != original.LastWriteTimeUtc ||
                    !string.Equals(entry.RelativePath, original.RelativePath, StringComparison.Ordinal) ||
                    !string.Equals(entry.SourceHash, original.Sha256, StringComparison.Ordinal) ||
                    !PathsEqual(entry.OutputPath, original.OutputPath))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
    private AgentBatchRecoveryOutputSafety OwnedCompatibleOutput(AgentBatchDocument original, AgentBatchRecoveryPlan recovery)
    {
        try
        {
            if (original.StartIdempotencyKey is null)
                return AgentBatchRecoveryOutputSafety.Unsafe("ORIGINAL_OWNERSHIP_EVIDENCE_MISSING", recovery.Plan.OutputDirectory);
            var lineage = LoadValidatedRecoveryLineage(original);
            if (lineage is null)
                return AgentBatchRecoveryOutputSafety.Unsafe("LINEAGE_AMBIGUOUS_OR_INCOMPLETE", recovery.Plan.OutputDirectory);
            var completed = new Dictionary<string, AgentBatchRecoveryOutputEvidence>(StringComparer.OrdinalIgnoreCase);
            var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in lineage)
            {
                foreach (var file in document.Files)
                {
                    if (file.State == AgentBatchFileState.Completed)
                    {
                        if (string.IsNullOrWhiteSpace(file.OutputHash) || !AddCompletedEvidence(completed, file.OutputPath,
                                file.OutputHash, OutputBytes(file.OutputPath, file.OutputBytes)))
                            return AgentBatchRecoveryOutputSafety.Unsafe("COMPLETED_OUTPUT_EVIDENCE_INVALID", file.OutputPath);
                    }
                    else if (file.State == AgentBatchFileState.Failed && file.JobId is { } jobId && !string.IsNullOrWhiteSpace(file.OutputHash))
                    {
                        var candidatePath = CandidatePath(file.OutputPath, jobId);
                        if (!AddExactEvidence(candidates, candidatePath, file.OutputHash))
                            return AgentBatchRecoveryOutputSafety.Unsafe("CANDIDATE_EVIDENCE_INVALID", candidatePath);
                    }

                    var jobEvidence = CompletedJobEvidence(document, file);
                    if (jobEvidence is { } evidence && !AddCompletedEvidence(completed, file.OutputPath, evidence.Hash, evidence.Bytes))
                        return AgentBatchRecoveryOutputSafety.Unsafe("COMPLETED_JOB_EVIDENCE_AMBIGUOUS", file.OutputPath);
                }
            }
            return AgentBatchFileSystemPolicy.IsSafeRecoveryOutputDirectory(recovery.Plan.OutputDirectory, completed, candidates,
                recovery.Entries.Select(entry => entry.OutputPath).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return AgentBatchRecoveryOutputSafety.Unsafe("OUTPUT_EVIDENCE_UNREADABLE", recovery.Plan.OutputDirectory);
        }
    }

    private List<AgentBatchDocument>? LoadValidatedRecoveryLineage(AgentBatchDocument original)
    {
        if (!Directory.Exists(BatchRoot)) return null;
        var documents = Directory.EnumerateFiles(BatchRoot, "batch.json", SearchOption.AllDirectories)
            .Select(Read<AgentBatchDocument>).Where(document => document is not null).Cast<AgentBatchDocument>().ToArray();
        if (documents.GroupBy(document => document.BatchId).Any(group => group.Count() != 1) ||
            documents.Where(document => !string.IsNullOrWhiteSpace(document.RecoveryPlanId))
                .GroupBy(document => document.RecoveryPlanId!, StringComparer.Ordinal).Any(group => group.Count() != 1)) return null;
        var known = documents.ToDictionary(document => document.BatchId);
        if (!known.TryGetValue(original.BatchId, out var durableOriginal)) return null;

        var root = durableOriginal;
        var ancestors = new HashSet<Guid> { root.BatchId };
        while (root.OriginalBatchId is { } parentId)
        {
            if (!ancestors.Add(parentId) || !known.TryGetValue(parentId, out var parent) ||
                !MatchesRecoveryLineage(root, parent)) return null;
            root = parent;
        }

        var lineage = new List<AgentBatchDocument> { root };
        var knownLineage = new HashSet<Guid> { root.BatchId };
        var pending = new Queue<AgentBatchDocument>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            var children = documents.Where(document => document.OriginalBatchId == parent.BatchId).ToArray();
            foreach (var child in children)
            {
                if (!knownLineage.Add(child.BatchId) || !MatchesRecoveryLineage(child, parent)) return null;
                lineage.Add(child);
                pending.Enqueue(child);
            }
        }
        return lineage;
    }

    /// <summary>
    /// The batch progress row is a historical checkpoint. When its child job is
    /// available, the child is authoritative: a later terminal generation failure
    /// must never be silently treated as the earlier OpenAI timeout.
    /// </summary>
    private RecoveryActionResolution ResolveRecoveryAction(
        AgentBatchDocument batch,
        AgentBatchPlan plan,
        AgentBatchFileProgress file)
    {
        var legacy = LegacyRecoveryAction(file);
        var manifestEntries = plan.Files.Where(candidate =>
            string.Equals(candidate.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (batch.BatchId != plan.BatchId || !string.Equals(batch.ManifestHash, plan.ManifestHash, StringComparison.Ordinal) ||
            manifestEntries.Length != 1 || !string.Equals(file.SourceHash, manifestEntries[0].Sha256, StringComparison.Ordinal) ||
            !PathsEqual(file.OutputPath, manifestEntries[0].OutputPath))
            return new(null, new("BATCH_RECOVERY_CHILD_BINDING_INVALID",
                "The batch, immutable manifest, and file binding do not match."));
        var manifest = manifestEntries[0];
        if (file.JobId is not { } jobId)
            return legacy == AgentBatchRecoveryAction.RetryCadFresh ? FreshRetry(plan) : new(legacy, null);

        var job = Read<JobDocument>(Path.Combine(_configuration.WorkspaceRoot, jobId.ToString("D"), "job.json"));
        // RetryCadFresh does not reuse child state. ContinueFromReview and
        // ResumeTranslationMissingOnly do, so both require a durable child journal.
        if (job is null)
        {
            if (file.State == AgentBatchFileState.Failed &&
                string.Equals(file.ErrorCode, "WORKFLOW_UNEXPECTED_FAILURE", StringComparison.Ordinal))
                return new(null, new("BATCH_RECOVERY_CHILD_EVIDENCE_MISSING",
                    "Unexpected prepare recovery requires the durable child and operation journals."));
            return legacy == AgentBatchRecoveryAction.RetryCadFresh
                ? FreshRetry(plan)
                : new(null, new("BATCH_RECOVERY_CHILD_EVIDENCE_MISSING",
                    "The recovery action requires a durable child journal."));
        }
        if (job.JobId != jobId)
            return new(null, new("BATCH_RECOVERY_CHILD_BINDING_INVALID", "The durable child journal does not match the batch entry."));

        DwgTranslationJobData? data;
        try { data = job.Data.Deserialize<DwgTranslationJobData>(Json); }
        catch (JsonException)
        {
            return new(null, new("BATCH_RECOVERY_CHILD_EVIDENCE_INVALID", "The durable child journal is unreadable."));
        }
        if (data?.Specification is null || !PathsEqual(data.Specification.SourcePath, manifest.SourcePath) ||
            !string.Equals(data.Specification.SourceHash, file.SourceHash, StringComparison.Ordinal) ||
            !PathsEqual(data.Specification.OutputPath, file.OutputPath))
            return new(null, new("BATCH_RECOVERY_CHILD_BINDING_INVALID", "The durable child source or output binding changed."));
        if (plan.Policy == AgentBatchPolicy.ContextualPolicyVersion && data.Segments is { Count: > 0 } contextualSegments)
        {
            var aggregate = CadSemanticContextBuilder.AggregateHash(contextualSegments);
            if (!aggregate.IsSuccess)
                return new(null, new("BATCH_RECOVERY_CHILD_STATE_DIVERGED",
                    "The durable child semantic context is invalid or mixed-version."));
            if (contextualSegments[0].SemanticContext?.Version != plan.ContextPolicyVersion)
                return new(null, new("BATCH_RECOVERY_CONTEXT_VERSION_MISMATCH",
                    "The durable child semantic context does not match the recovery plan version."));
        }

        if (job.State == JobState.Extracted && file.State == AgentBatchFileState.Failed &&
            string.Equals(file.ErrorCode, "WORKFLOW_UNEXPECTED_FAILURE", StringComparison.Ordinal))
        {
            if (!AgentWorkflowFileStore.ValidOpaque(file.PrepareOperationId ?? string.Empty))
                return new(null, new("BATCH_RECOVERY_CHILD_STATE_DIVERGED",
                    "The extracted child lacks its exact failed prepare operation evidence."));
            var operation = Read<AgentWorkflowOperation>(Path.Combine(
                _configuration.LogRoot, "workflow", "operations", file.PrepareOperationId + ".json"));
            return operation is not null &&
                   string.Equals(operation.OperationId, file.PrepareOperationId, StringComparison.Ordinal) &&
                   string.Equals(operation.Kind, "translation.prepare", StringComparison.Ordinal) &&
                   operation.JobId == jobId &&
                   string.Equals(operation.State, "Failed", StringComparison.Ordinal) &&
                   string.Equals(operation.ErrorCode, file.ErrorCode, StringComparison.Ordinal)
                ? FreshRetry(plan)
                : new(null, new("BATCH_RECOVERY_CHILD_STATE_DIVERGED",
                    "The extracted child lacks matching failed prepare operation evidence."));
        }

        if (job.State == JobState.ReviewRequired)
            return new(AgentBatchRecoveryAction.ContinueFromReview, null);
        if (job.State == JobState.Approved)
            return IsExactPendingApprovedHistoricalGeneration(batch, plan, file, job, data)
                ? FreshRetry(plan)
                : IsExactPendingApprovedSupersedingGeneration(batch, plan, file, job, data)
                    ? new(AgentBatchRecoveryAction.ContinueFromReview, null)
                : ResolveApprovedContextRevalidation(batch, plan, file, manifest, job, data);
        if (job.State != JobState.Failed)
            return new(null, new("BATCH_RECOVERY_CHILD_STATE_DIVERGED", "The durable child state has no safe recovery action."));

        var failure = job.Data["failure"] as JsonObject;
        var code = failure?["code"]?.GetValue<string>()
            ?? job.Data["agentFailure"]?["code"]?.GetValue<string>();
        if (string.Equals(code, "GEOMETRY_INVARIANTS_CHANGED", StringComparison.Ordinal))
            return AgentFailedGeometryRecoveryEvidence.IsTolerable(job.Data)
                ? new(AgentBatchRecoveryAction.ContinueFromReview, null)
                : new(null, new("BATCH_RECOVERY_GEOMETRY_EVIDENCE_INCOMPLETE",
                    "The durable geometry evidence is missing, malformed, truncated, or material."));
        if (string.Equals(code, "OPENAI_TIMEOUT", StringComparison.Ordinal) &&
            failure?["retryable"]?.GetValue<bool>() is true &&
            string.Equals(failure["stage"]?.GetValue<string>(), "Translation", StringComparison.Ordinal))
            return new(AgentBatchRecoveryAction.ResumeTranslationMissingOnly, null);
        if (string.Equals(code, "OPENAI_UNAVAILABLE", StringComparison.Ordinal))
            return MissingOnlyResumeEvidence(batch, plan, file, manifest, job, data, failure,
                    "OPENAI_UNAVAILABLE", expectedRetryable: true)
                ? new(AgentBatchRecoveryAction.ResumeTranslationMissingOnly, null)
                : new(null, new("BATCH_RECOVERY_CHILD_STATE_DIVERGED",
                    "OpenAI-unavailable recovery requires an exact failed prepare operation and an untampered partial translation checkpoint."));
        if (string.Equals(code, "OPENAI_RATE_LIMITED", StringComparison.Ordinal))
        {
            if (MissingOnlyResumeEvidence(batch, plan, file, manifest, job, data, failure,
                    "OPENAI_RATE_LIMITED", expectedRetryable: true))
                return new(AgentBatchRecoveryAction.ResumeTranslationMissingOnly, null);
            return NoProgressRateLimitEvidence(batch, plan, file, job, data, failure)
                ? FreshRetry(plan)
                : new(null, new("BATCH_RECOVERY_CHILD_STATE_DIVERGED",
                    "Rate-limit recovery requires an exact failed operation and a valid partial review or no review artifact."));
        }
        if (string.Equals(code, "TOKEN_INTEGRITY_FAILED", StringComparison.Ordinal))
            return MissingOnlyResumeEvidence(batch, plan, file, manifest, job, data, failure,
                    "TOKEN_INTEGRITY_FAILED", expectedRetryable: false)
                ? new(AgentBatchRecoveryAction.ResumeTranslationMissingOnly, null)
                : new(null, new("BATCH_RECOVERY_CHILD_STATE_DIVERGED",
                    "Token-integrity recovery requires an exact failed prepare operation and an untampered partial translation checkpoint."));
        if (string.Equals(code, "CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT", StringComparison.Ordinal))
            return HasLateBootstrapRestoreEvidence(batch, plan, file, job, data, failure)
                ? FreshRetry(plan)
                : new(null, new("BATCH_RECOVERY_BOOTSTRAP_RESTORE_EVIDENCE_INVALID",
                    "Fresh CAD retry requires the exact failed operation and byte-exact late trust-restoration evidence."));
        if (string.Equals(code, "IPC_TIMEOUT", StringComparison.Ordinal) &&
            IsRecovery25Batch(batch, plan) && HasCadExchangeTimeoutEvidence(file, job, failure))
            return FreshRetry(plan);
        if (string.Equals(code, "CAD_UNAVAILABLE", StringComparison.Ordinal) &&
            HasTerminalCadUnavailableReadEvidence(file, job, data, failure))
            return FreshRetry(plan);
        if (code is "WORKFLOW_BACKGROUND_STALLED" or "JOB_STATE_CONFLICT" or "CAD_PROCESS_EXIT_REQUIRED" or
            "WORKFLOW_UNEXPECTED_FAILURE" or "CAD_BUSY" or "CAD_PROCESS_INITIALIZATION_FAILED" or
            "CAD_SUPPORTED_COUNT_INVALID" or "BATCH_FILE_GENERATE_FAILED")
            return FreshRetry(plan);
        return new(null, new("BATCH_RECOVERY_CHILD_STATE_DIVERGED", "The durable child error has no deterministic recovery action."));
    }

    private RecoveryActionResolution ResolveApprovedContextRevalidation(
        AgentBatchDocument batch,
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        AgentBatchManifestEntry manifest,
        JobDocument job,
        DwgTranslationJobData data)
    {
        if (file.State != AgentBatchFileState.Failed || file.JobId != job.JobId ||
            (file.JobVersion != job.Version &&
              !HistoricalIncidentRecoveryPolicy.IsKnownStaleProjection(batch, plan, file, job)) ||
            !string.Equals(file.ErrorCode, "BATCH_HUMAN_REVIEW_REQUIRED", StringComparison.Ordinal) ||
            plan.Policy != AgentBatchPolicy.ContextualPolicyVersion ||
            plan.ContextPolicyVersion != CadSemanticContextBuilder.PolicyVersionOneOne ||
            data.Specification.ReviewAutomationScope is not { } scope || scope.BatchId != batch.BatchId ||
            scope.ManifestHash != batch.ManifestHash || scope.PolicyVersion != plan.Policy ||
            data.Segments is not { Count: > 0 } segments ||
            File.Exists(file.OutputPath) || Directory.Exists(file.OutputPath) ||
            HasContextRecoveryArtifacts(file.OutputPath, job.JobId))
            return new(null, new("BATCH_RECOVERY_APPROVED_CONTEXT_EVIDENCE_INVALID",
                "Approved-context recovery requires the exact failed batch row, 1.1 child, receipt, and absent output artifacts."));

        var basename = Path.GetFileName(manifest.RelativePath);
        if (string.IsNullOrWhiteSpace(basename) ||
            !string.Equals(Path.GetFileName(manifest.SourcePath), basename, StringComparison.OrdinalIgnoreCase))
            return new(null, new("BATCH_RECOVERY_APPROVED_CONTEXT_BASENAME_INVALID",
                "The manifest-bound source basename is invalid."));
        var aggregate = CadSemanticContextBuilder.AggregateHash(segments);
        if (!aggregate.IsSuccess || segments.Any(segment =>
                segment.SemanticContext?.Version != CadSemanticContextBuilder.PolicyVersionOneOne))
            return new(null, new("BATCH_RECOVERY_APPROVED_CONTEXT_EVIDENCE_INVALID",
                "The approved 1.1 semantic-context set is invalid."));
        var fallbackCandidateCount = segments.Count(segment => segment.SemanticContext is
        { Discipline: "Unknown", DisciplineConflict: false, DisciplineEvidence.Count: 0 });
        var localEvidenceCount = segments.Count - fallbackCandidateCount;
        var review = Read<TranslationReviewSnapshot>(Path.Combine(
            _configuration.WorkspaceRoot, job.JobId.ToString("D"), "review", "session.json"));
        var receipt = review?.ReviewAutomationReceipt;
        if (review is null || receipt is null || review.JobId != job.JobId ||
            review.Rows.Count != segments.Count || review.Rows.Count == 0 ||
            review.Rows.Any(row => row.State != SegmentState.Approved || row.ExclusionReason is not null ||
                string.IsNullOrEmpty(row.FinalText)) ||
            review.ContextPolicyVersion != CadSemanticContextBuilder.PolicyVersionOneOne ||
            review.ContextHash != aggregate.Value || receipt.PolicyVersion != plan.Policy ||
            receipt.BatchId != batch.BatchId || receipt.ManifestHash != batch.ManifestHash ||
            receipt.ContextHash != aggregate.Value ||
            !ContractPatterns.Sha256().IsMatch(receipt.ReviewerReportHash) ||
            !ContractPatterns.Sha256().IsMatch(receipt.QaReportHash) ||
            receipt.SegmentContextResolutions is not (null or { Count: 0 }))
            return new(null, new("BATCH_RECOVERY_APPROVED_CONTEXT_REVIEW_INVALID",
                "The approved review or withdrawn automation receipt does not match the original batch."));
        var segmentsById = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        if (segmentsById.Count != segments.Count || review.Rows.Any(row =>
                !segmentsById.TryGetValue(row.SegmentId, out var segment) ||
                !string.Equals(row.OriginalText, segment.SourceText, StringComparison.Ordinal)))
            return new(null, new("BATCH_RECOVERY_APPROVED_CONTEXT_REVIEW_INVALID",
                "The approved review rows do not match the extracted segment set."));

        var upgraded = CadSemanticContextBuilder.UpgradeOneOneToOneTwo(segments, basename);
        if (!upgraded.IsSuccess)
            return new(null, new("BATCH_RECOVERY_APPROVED_CONTEXT_TARGET_INVALID",
                "The exact 1.1 context cannot be rebuilt under 1.2 from the manifest basename."));
        var targetAggregate = CadSemanticContextBuilder.AggregateHash(upgraded.Value!);
        var conflictIds = upgraded.Value!.Where(segment => segment.SemanticContext!.Signals.Contains(
                SegmentContextResolutionPolicy.VerticalSignal, StringComparer.Ordinal))
            .Select(segment => segment.SegmentId).Order(StringComparer.Ordinal).ToArray();
        if (!targetAggregate.IsSuccess || conflictIds.Length > 1)
            return new(null, new("BATCH_RECOVERY_APPROVED_CONTEXT_CONFLICT_SET_INVALID",
                "Context recovery supports at most one separately reviewed vertical conflict."));
        var reviewFingerprint = TranslationReviewFingerprint.Create(review);
        var jobArtifactHash = ArtifactHash(Path.Combine(_configuration.WorkspaceRoot,
            job.JobId.ToString("D"), "job.json"));
        if (jobArtifactHash is null || reviewFingerprint.ReceiptHash is null)
            return new(null, new("BATCH_RECOVERY_APPROVED_CONTEXT_ARTIFACT_INVALID",
                "The approved job or receipt artifact cannot be fingerprinted."));
        var binding = new ApprovedContextRevalidationBinding(
            job.Version, jobArtifactHash, review.Version, reviewFingerprint.ReviewHash,
            reviewFingerprint.ReceiptHash, CadSemanticContextBuilder.PolicyVersionOneOne, aggregate.Value!,
            CadSemanticContextBuilder.PolicyVersionOneTwo, targetAggregate.Value!, basename,
            review.Rows.Count, fallbackCandidateCount, localEvidenceCount, conflictIds,
            ApprovedContextRevalidationPolicy.ConflictSetHash(conflictIds), string.Empty);
        binding = binding with { BindingHash = ApprovedContextRevalidationPolicy.BindingHash(binding) };
        return ApprovedContextRevalidationPolicy.IsValid(binding)
            ? new(AgentBatchRecoveryAction.RevalidateApprovedContext, null, binding)
            : new(null, new("BATCH_RECOVERY_APPROVED_CONTEXT_BINDING_INVALID",
                "The context-revalidation binding could not be sealed."));
    }

    private static ReviewAutomationScopeTransition RecoveryScopeTransition(
        AgentBatchRecoveryPlan recovery,
        AgentBatchRecoveryEntry entry,
        AgentBatchFileProgress file)
    {
        var manifest = recovery.Plan.Files.Single(candidate =>
            string.Equals(candidate.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (entry.Action == AgentBatchRecoveryAction.RetryCadFresh || entry.AdoptedJobId is null ||
            entry.AdoptedJobId != file.JobId || !string.Equals(entry.SourceHash, file.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(entry.SourceHash, manifest.Sha256, StringComparison.Ordinal) ||
            !PathsEqual(entry.OutputPath, file.OutputPath) || !PathsEqual(entry.OutputPath, manifest.OutputPath))
            throw new InvalidOperationException("BATCH_RECOVERY_CHILD_BINDING_INVALID");
        return new(
            new ReviewAutomationScope(recovery.OriginalBatchId, recovery.OriginalManifestHash, recovery.Plan.Policy),
            new ReviewAutomationScope(recovery.RecoveryBatchId, recovery.RecoveryManifestHash, recovery.Plan.Policy),
            manifest.SourcePath,
            manifest.OutputPath,
            manifest.Sha256,
            recovery.Plan.ContextPolicyVersion ?? string.Empty);
    }

    private static ApprovedContextRevalidationTransition ApprovedContextTransition(
        AgentBatchRecoveryPlan recovery,
        AgentBatchRecoveryEntry entry,
        AgentBatchFileProgress file)
    {
        var manifest = recovery.Plan.Files.Single(candidate =>
            string.Equals(candidate.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
        var binding = entry.ContextRevalidation;
        if (entry.Action != AgentBatchRecoveryAction.RevalidateApprovedContext || binding is null ||
            !ApprovedContextRevalidationPolicy.IsValid(binding) || entry.AdoptedJobId is null ||
            entry.AdoptedJobId != file.JobId || entry.SourceHash != file.SourceHash ||
            entry.SourceHash != manifest.Sha256 || !PathsEqual(entry.OutputPath, file.OutputPath) ||
            !PathsEqual(entry.OutputPath, manifest.OutputPath) ||
            !string.Equals(Path.GetFileName(manifest.RelativePath), binding.ManifestBoundBasename, StringComparison.Ordinal))
            throw new InvalidOperationException("BATCH_RECOVERY_APPROVED_CONTEXT_BINDING_INVALID");
        return new(
            new ReviewAutomationScope(recovery.OriginalBatchId, recovery.OriginalManifestHash, recovery.Plan.Policy),
            new ReviewAutomationScope(recovery.RecoveryBatchId, recovery.RecoveryManifestHash, recovery.Plan.Policy),
            manifest.SourcePath, manifest.OutputPath, manifest.Sha256, binding);
    }

    private bool ValidRecoveryScopePlan(
        AgentBatchDocument batch,
        AgentBatchRecoveryPlan recovery)
    {
        var originalPlan = LoadPlanByBatch(recovery.OriginalBatchId);
        if (originalPlan is null || !string.Equals(originalPlan.ManifestHash, recovery.OriginalManifestHash, StringComparison.Ordinal) ||
            !RecoverySubplanStillMatchesOriginal(recovery, originalPlan) ||
            batch.BatchId != recovery.RecoveryBatchId || batch.OriginalBatchId != recovery.OriginalBatchId ||
            !string.Equals(batch.RecoveryPlanId, recovery.RecoveryPlanId, StringComparison.Ordinal) ||
            recovery.Plan.BatchId != batch.BatchId ||
            !string.Equals(batch.ManifestHash, recovery.RecoveryManifestHash, StringComparison.Ordinal) ||
            !string.Equals(recovery.Plan.ManifestHash, recovery.RecoveryManifestHash, StringComparison.Ordinal) ||
            !string.Equals(RecoveryManifestHash(recovery.OriginalManifestHash, recovery.OriginalBatchVersion,
                recovery.OriginalBatchState ?? string.Empty, recovery.Entries), recovery.RecoveryManifestHash, StringComparison.Ordinal) ||
            recovery.Plan.Policy != AgentBatchPolicy.ContextualPolicyVersion ||
            !CadSemanticContextBuilder.IsSupportedPolicyVersion(recovery.Plan.ContextPolicyVersion) ||
            recovery.Entries.Count != batch.Files.Count || recovery.Plan.Files.Count != batch.Files.Count ||
            recovery.Entries.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != recovery.Entries.Count)
            return false;
        return batch.Files.All(file => recovery.Entries.Count(entry =>
                string.Equals(entry.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.SourceHash, file.SourceHash, StringComparison.Ordinal) &&
                PathsEqual(entry.OutputPath, file.OutputPath) &&
                (entry.Action == AgentBatchRecoveryAction.RetryCadFresh || entry.AdoptedJobId == file.JobId) &&
                (entry.Action != AgentBatchRecoveryAction.RevalidateApprovedContext ||
                    ApprovedContextRevalidationPolicy.IsValid(entry.ContextRevalidation))) == 1) &&
            recovery.Plan.Files.All(manifest => batch.Files.Count(file =>
                string.Equals(file.RelativePath, manifest.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(file.SourceHash, manifest.Sha256, StringComparison.Ordinal) &&
                PathsEqual(file.OutputPath, manifest.OutputPath)) == 1);
    }

    private static RecoveryActionResolution FreshRetry(AgentBatchPlan plan) =>
        plan.Policy != AgentBatchPolicy.ContextualPolicyVersion ||
        plan.ContextPolicyVersion == CadSemanticContextBuilder.CurrentPolicyVersion
            ? new(AgentBatchRecoveryAction.RetryCadFresh, null)
            : new(null, new("BATCH_RECOVERY_CONTEXT_VERSION_MISMATCH",
                "A fresh contextual retry requires the current semantic context version and a new bound plan."));

    private bool MissingOnlyResumeEvidence(
        AgentBatchDocument batch,
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        AgentBatchManifestEntry manifest,
        JobDocument job,
        DwgTranslationJobData data,
        JsonObject? failure,
        string expectedErrorCode,
        bool expectedRetryable)
    {
        if (file.State != AgentBatchFileState.Failed || file.JobId != job.JobId || file.JobVersion != job.Version ||
            file.Retryable != expectedRetryable ||
            !string.Equals(file.ErrorCode, expectedErrorCode, StringComparison.Ordinal) ||
            !string.Equals(failure?["code"]?.GetValue<string>(), file.ErrorCode, StringComparison.Ordinal) ||
            !string.Equals(failure?["stage"]?.GetValue<string>(), "Translation", StringComparison.Ordinal) ||
            failure?["retryable"]?.GetValue<bool>() != expectedRetryable ||
            !AgentWorkflowFileStore.ValidOpaque(file.PrepareOperationId ?? string.Empty) ||
            plan.Policy != AgentBatchPolicy.ContextualPolicyVersion || plan.RoutingMode != TranslationRouting.Auto ||
            data.Specification.Routing?.RequestedMode != TranslationRouting.Auto ||
            !string.Equals(data.Specification.TargetLanguage, plan.TargetLanguage, StringComparison.Ordinal) ||
            !string.Equals(data.Specification.PromptTemplateVersion, plan.PromptTemplateVersion, StringComparison.Ordinal) ||
            !string.Equals(data.ConfigurationHash, JobConfigurationHash(data.Specification), StringComparison.Ordinal) ||
            data.Specification.ReviewAutomationScope is not { } scope || scope.BatchId != batch.BatchId ||
            !string.Equals(scope.ManifestHash, batch.ManifestHash, StringComparison.Ordinal) ||
            !string.Equals(scope.PolicyVersion, plan.Policy, StringComparison.Ordinal) ||
            !PathsEqual(data.Specification.SourcePath, manifest.SourcePath) ||
            !string.Equals(data.Specification.SourceHash, manifest.Sha256, StringComparison.Ordinal) ||
            !PathsEqual(data.Specification.OutputPath, manifest.OutputPath) ||
            data.Segments is not { Count: > 0 } segments)
            return false;

        var operation = Read<AgentWorkflowOperation>(Path.Combine(
            _configuration.LogRoot, "workflow", "operations", file.PrepareOperationId + ".json"));
        if (operation is null || !string.Equals(operation.OperationId, file.PrepareOperationId, StringComparison.Ordinal) ||
            !string.Equals(operation.Kind, "translation.prepare", StringComparison.Ordinal) ||
            operation.JobId != job.JobId || !string.Equals(operation.State, "Failed", StringComparison.Ordinal) ||
            !string.Equals(operation.Stage, "Failed", StringComparison.Ordinal) ||
            !string.Equals(operation.ErrorCode, file.ErrorCode, StringComparison.Ordinal) ||
            operation.Retryable != expectedRetryable)
            return false;

        var review = Read<TranslationReviewSnapshot>(Path.Combine(
            _configuration.WorkspaceRoot, job.JobId.ToString("D"), "review", "session.json"));
        if (review is null || review.JobId != job.JobId || review.Version != review.CompletedBatches - 1 ||
            review.CompletedBatches < 1 || review.CompletedBatches >= review.TotalBatches ||
            review.ReviewAutomationReceipt is not null || review.Rows is not { Count: > 0 } ||
            review.UsedInputTokens < 0 || review.UsedOutputTokens < 0 || review.UsedProviderRequests < 0 ||
            !string.Equals(review.TargetLanguage, data.Specification.TargetLanguage, StringComparison.Ordinal) ||
            !string.Equals(review.PromptTemplateVersion, data.Specification.PromptTemplateVersion, StringComparison.Ordinal) ||
            !string.Equals(review.RequestedMode, data.Specification.Routing.RequestedMode, StringComparison.Ordinal) ||
            !string.Equals(review.BaseModel, data.Specification.Routing.BaseModel, StringComparison.Ordinal) ||
            !string.Equals(review.RoutingVersion, data.Specification.Routing.Version, StringComparison.Ordinal) ||
            review.RoutingCalls is null || review.RoutingSegments is null ||
            review.RoutingCalls.Count != review.UsedProviderRequests || review.RoutingSegments.Count != review.Rows.Count)
            return false;

        var aggregate = CadSemanticContextBuilder.AggregateHash(segments);
        if (!aggregate.IsSuccess || review.ContextPolicyVersion != plan.ContextPolicyVersion ||
            !string.Equals(review.ContextHash, aggregate.Value, StringComparison.Ordinal))
            return false;
        var batches = TranslationBatchFactory.Create(
            segments,
            data.Specification.SourceLanguage,
            data.Specification.TargetLanguage,
            data.Specification.PromptTemplateVersion,
            data.Specification.Glossary,
            _configuration.TranslationMaxSegments,
            _configuration.TranslationMaxCharacters,
            data.Specification.Routing);
        if (!batches.IsSuccess || batches.Value!.Count != review.TotalBatches)
            return false;
        var expectedIds = batches.Value.Take(review.CompletedBatches)
            .SelectMany(item => item.Segments).Select(item => item.SegmentId).ToArray();
        if (review.Rows.Count != expectedIds.Length ||
            !review.Rows.Select(row => row.SegmentId).SequenceEqual(expectedIds, StringComparer.Ordinal))
            return false;

        var sourceById = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        for (var index = 0; index < review.Rows.Count; index++)
        {
            var row = review.Rows[index];
            var trace = review.RoutingSegments[index];
            if (!sourceById.TryGetValue(row.SegmentId, out var source) || row.State != SegmentState.Proposed ||
                row.ExclusionReason is not null || !string.Equals(row.OriginalText, source.SourceText, StringComparison.Ordinal) ||
                !string.Equals(row.FinalText, row.ProposedText, StringComparison.Ordinal) ||
                !string.Equals(trace.SegmentId, row.SegmentId, StringComparison.Ordinal) ||
                !string.Equals(trace.RiskSeverity, row.RiskSeverity, StringComparison.Ordinal) ||
                !string.Equals(trace.EffectiveModel, row.EffectiveModel, StringComparison.Ordinal) ||
                trace.Escalated != row.Escalated)
                return false;
            if (string.Equals(row.WarningCode, "high:TOKEN_INTEGRITY_FALLBACK", StringComparison.Ordinal))
            {
                if (row.RiskSeverity != "high" || !row.Escalated || trace.ValidatorResult != "review" ||
                    trace.RequestedMode != TranslationRouting.Auto ||
                    trace.BaseModel != data.Specification.Routing.BaseModel ||
                    trace.RoutingVersion != data.Specification.Routing.Version ||
                    !trace.EscalationReasonCodes.Contains("PROTECTED_TOKEN_CHANGED", StringComparer.Ordinal) ||
                    !review.RoutingCalls.Any(call =>
                        call.Tier == "escalation" &&
                        call.RequestedModel == data.Specification.Routing.EscalationModel) ||
                    !string.Equals(row.ProposedText, source.SourceText, StringComparison.Ordinal))
                    return false;
                continue;
            }
            if (!HumanReviewPolicy.Approve(source,
                    new AcceptedTranslation(row.SegmentId, row.ProposedText, row.WarningCode,
                        row.RiskSeverity, row.EffectiveModel, row.Escalated),
                    row.ProposedText).IsSuccess)
                return false;
        }
        return true;
    }

    private bool NoProgressRateLimitEvidence(
        AgentBatchDocument batch,
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        JobDocument job,
        DwgTranslationJobData data,
        JsonObject? failure)
    {
        const string code = "OPENAI_RATE_LIMITED";
        if (file.State != AgentBatchFileState.Failed || file.JobId != job.JobId ||
            file.JobVersion != job.Version || !file.Retryable || file.ErrorCode != code ||
            failure?["code"]?.GetValue<string>() != code ||
            failure?["stage"]?.GetValue<string>() != "Translation" ||
            failure?["retryable"]?.GetValue<bool>() != true ||
            !AgentWorkflowFileStore.ValidOpaque(file.PrepareOperationId ?? string.Empty) ||
            plan.Policy != AgentBatchPolicy.ContextualPolicyVersion ||
            data.Specification.ReviewAutomationScope is not { } scope ||
            scope.BatchId != batch.BatchId || scope.ManifestHash != batch.ManifestHash ||
            scope.PolicyVersion != plan.Policy || data.Segments is not { Count: > 0 })
            return false;

        var reviewPath = Path.Combine(_configuration.WorkspaceRoot, job.JobId.ToString("D"), "review");
        if (Directory.Exists(reviewPath) || File.Exists(reviewPath)) return false;
        var operation = Read<AgentWorkflowOperation>(Path.Combine(
            _configuration.LogRoot, "workflow", "operations", file.PrepareOperationId + ".json"));
        return operation is not null && operation.OperationId == file.PrepareOperationId &&
            operation.Kind == "translation.prepare" && operation.JobId == job.JobId &&
            operation.State == "Failed" && operation.Stage == "Failed" &&
            operation.ErrorCode == code && operation.Retryable;
    }

    private static string JobConfigurationHash(DwgTranslationJobSpecification specification)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(specification, Json));
        return CanonicalJsonV1.Fingerprint(document.RootElement);
    }

    private bool HasLateBootstrapRestoreEvidence(
        AgentBatchDocument batch,
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        JobDocument job,
        DwgTranslationJobData data,
        JsonObject? failure)
    {
        const string code = "CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT";
        var exactFailedProjection = file.State == AgentBatchFileState.Failed && file.JobId == job.JobId &&
            file.JobVersion == job.Version && string.Equals(file.ErrorCode, code, StringComparison.Ordinal) &&
            AgentWorkflowFileStore.ValidOpaque(file.GenerateOperationId ?? string.Empty);
        var exactStaleProjection = IsExactStaleHistoricalBootstrapProjection(batch, plan, file, job, data);
        if ((!exactFailedProjection && !exactStaleProjection) || file.Retryable ||
            string.IsNullOrWhiteSpace(data.ConfigurationHash) || failure is null ||
            !failure.ContainsKey("category") || !failure.ContainsKey("retryable") || !failure.ContainsKey("stage") ||
            !failure.ContainsKey("technicalStage") || !failure.ContainsKey("nativeErrorStatus") ||
            !string.Equals(failure["code"]?.GetValue<string>(), code, StringComparison.Ordinal) ||
            !string.Equals(failure["category"]?.GetValue<string>(), ErrorCategory.Security.ToString(), StringComparison.Ordinal) ||
            failure["retryable"]?.GetValue<bool>() is not false ||
            !string.Equals(failure["stage"]?.GetValue<string>(), "Writing", StringComparison.Ordinal) ||
            failure["technicalStage"] is not null || failure["nativeErrorStatus"] is not null ||
            File.Exists(file.OutputPath) || Directory.Exists(file.OutputPath) ||
            HasContextRecoveryArtifacts(file.OutputPath, job.JobId) || _cadProcessExists())
            return false;

        AgentWorkflowOperation? operation;
        if (exactFailedProjection)
        {
            var operationPath = Path.Combine(_configuration.LogRoot, "workflow", "operations",
                file.GenerateOperationId + ".json");
            operation = Read<AgentWorkflowOperation>(operationPath);
        }
        else
        {
            var operationRoot = Path.Combine(_configuration.LogRoot, "workflow", "operations");
            try
            {
                var operations = Directory.EnumerateFiles(operationRoot, "*.json", SearchOption.TopDirectoryOnly)
                    .Select(Read<AgentWorkflowOperation>)
                    .Where(candidate => candidate is not null && candidate.JobId == job.JobId &&
                        candidate.Kind == "generation.execute")
                    .Take(2)
                    .ToArray();
                operation = operations.Length == 1 ? operations[0] : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        if (operation is null || (exactFailedProjection && operation.OperationId != file.GenerateOperationId) ||
            operation.JobId != job.JobId || operation.Kind != "generation.execute" ||
            operation.State != "Failed" || operation.Stage != "Failed" || operation.ErrorCode != code ||
            operation.ErrorCategory != ErrorCategory.Security || operation.Retryable ||
            operation.TechnicalStage is not null || operation.NativeErrorStatus is not null ||
            operation.StartedAtUtc is null || operation.CompletedAtUtc is null ||
            operation.CompletedAtUtc < operation.StartedAtUtc ||
            operation.CleanupOutcome != "NO_GENERATION_CAD_RECEIPT")
            return false;

        var receiptPath = Path.Combine(_configuration.LogRoot, "workflow", "cad-receipts",
            job.JobId.ToString("D") + ".json");
        var receipt = Read<AgentCadLifecycleReceipt>(receiptPath);
        if (receipt is not null && (receipt.JobId != job.JobId ||
            string.Equals(receipt.OperationId, operation.OperationId, StringComparison.Ordinal) ||
            receipt.RequiresProcessTerminationApproval || receipt.ProcessExitedAtUtc is null ||
            receipt.ProcessExitedAtUtc > operation.CreatedAtUtc))
            return false;

        var bootstrapRoot = Path.Combine(_configuration.LogRoot, "cad-bootstrap", "workflow", "write");
        var assemblyPath = _configuration.WriteAdapterAssemblyPath;
        if (HistoricalIncidentRecoveryPolicy.IsExactFresh600LateBootstrapBinding(
                batch, plan, file, job.JobId, job.Version, operation))
        {
            if (!HistoricalIncidentRecoveryPolicy.TryGetFresh600GenerationWriteAdapter(out assemblyPath))
                return false;
        }
        else if (IsRecovery25Batch(batch, plan) &&
                  !HistoricalIncidentRecoveryPolicy.TryGetRecovery25GenerationWriteAdapter(out assemblyPath))
            return false;
        if (!RegularDirectory(bootstrapRoot) || !Path.IsPathFullyQualified(assemblyPath) ||
            !File.Exists(assemblyPath))
            return false;
        try
        {
            var lower = operation.StartedAtUtc.Value.UtcDateTime.AddSeconds(-5);
            var upper = operation.CompletedAtUtc.Value.UtcDateTime.AddSeconds(1);
            var matches = Directory.EnumerateFiles(bootstrapRoot, "netload-*.scr", SearchOption.TopDirectoryOnly)
                .Where(RegularFile)
                .Select(path => (Path: path, Info: new FileInfo(path)))
                .Where(candidate => candidate.Info.LastWriteTimeUtc >= lower && candidate.Info.LastWriteTimeUtc <= upper)
                .Where(candidate => ExactRestoredBootstrap(candidate.Path, candidate.Info.LastWriteTimeUtc,
                    upper, bootstrapRoot, assemblyPath))
                .Take(2)
                .Count();
            return matches == 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private bool IsExactPendingApprovedHistoricalGeneration(
        AgentBatchDocument batch,
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        JobDocument job,
        DwgTranslationJobData data)
    {
        if (!IsRecovery25Batch(batch, plan) || batch.State is not ("RecoveryRequired" or "Suspended") ||
            file.State is not (AgentBatchFileState.Reviewing or AgentBatchFileState.Suspended) ||
            !IsExactPostApprovalProjection(file, job) ||
            data.Specification.ReviewAutomationScope is not { } scope ||
            scope.BatchId != batch.BatchId || scope.ManifestHash != batch.ManifestHash ||
            !string.Equals(scope.PolicyVersion, plan.Policy, StringComparison.Ordinal) ||
            File.Exists(file.OutputPath) || Directory.Exists(file.OutputPath) ||
            HasContextRecoveryArtifacts(file.OutputPath, job.JobId) || _cadProcessExists())
            return false;
        var review = Read<TranslationReviewSnapshot>(Path.Combine(
            _configuration.WorkspaceRoot, job.JobId.ToString("D"), "review", "session.json"));
        if (review is null || !IsExactPostApprovalProjection(file, job, review) ||
            !string.Equals(review.ContextPolicyVersion, CadSemanticContextBuilder.PolicyVersionOneTwo, StringComparison.Ordinal) ||
            !string.Equals(review.ContextHash, file.ContextHash, StringComparison.Ordinal) ||
            review.Rows.Count == 0 || review.Rows.Any(row => row.FinalText is null || row.ExclusionReason is not null))
            return false;
        var decisions = review.Rows.Select(row => new ReviewDecisionInput(
            row.SegmentId, row.FinalText, row.ExclusionReason)).ToArray();
        return HistoricalIncidentRecoveryPolicy.ValidSupersedingApprovedDecisionSet(
            job.JobId, file.RelativePath, review.ContextHash!, review.ReviewAutomationReceipt, decisions);
    }

    private bool IsExactPendingApprovedSupersedingGeneration(
        AgentBatchDocument batch,
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        JobDocument job,
        DwgTranslationJobData data)
    {
        var reviewGateFailure =
            string.Equals(file.ErrorCode, "BATCH_HUMAN_REVIEW_REQUIRED", StringComparison.Ordinal) &&
            file.ReviewGateCode is "REVIEW_INVARIANT_FAILED" or "TERMINOLOGY_TARGET_MISSING";
        AgentWorkflowOperation? generationOperation = null;
        if (AgentWorkflowFileStore.ValidOpaque(file.GenerateOperationId ?? string.Empty))
            generationOperation = Read<AgentWorkflowOperation>(Path.Combine(
                _configuration.LogRoot, "workflow", "operations", file.GenerateOperationId + ".json"));
        var preCadProjectionFailure = batch.State == "CompletedWithFailures" &&
                                      generationOperation is not null &&
                                      IsExactPreCadApprovedGenerationConflict(file, generationOperation);

        if (!IsPendingApprovedSupersedingBatchState(batch.State) ||
            file.State != AgentBatchFileState.Failed ||
            (!reviewGateFailure && !preCadProjectionFailure) ||
            file.JobId != job.JobId || job.State != JobState.Approved || file.JobVersion != job.Version ||
            plan.Policy != AgentBatchPolicy.ContextualPolicyVersion ||
            plan.ContextPolicyVersion != CadSemanticContextBuilder.PolicyVersionOneTwo ||
            data.Specification.ReviewAutomationScope is not { } scope ||
            scope.BatchId != batch.BatchId || scope.ManifestHash != batch.ManifestHash ||
            !string.Equals(scope.PolicyVersion, plan.Policy, StringComparison.Ordinal) ||
            File.Exists(file.OutputPath) || Directory.Exists(file.OutputPath) ||
            HasContextRecoveryArtifacts(file.OutputPath, job.JobId) || _cadProcessExists())
            return false;
        var review = Read<TranslationReviewSnapshot>(Path.Combine(
            _configuration.WorkspaceRoot, job.JobId.ToString("D"), "review", "session.json"));
        if (review is null || review.JobId != job.JobId || review.Version != file.ReviewVersion ||
            !string.Equals(review.ContextPolicyVersion, CadSemanticContextBuilder.PolicyVersionOneTwo, StringComparison.Ordinal) ||
            !string.Equals(review.ContextHash, file.ContextHash, StringComparison.Ordinal) ||
            review.Rows.Count == 0 || review.Rows.Any(row =>
                row.State != SegmentState.Approved || row.FinalText is null || row.ExclusionReason is not null))
            return false;
        var decisions = review.Rows.Select(row => new ReviewDecisionInput(
            row.SegmentId, row.FinalText, row.ExclusionReason)).ToArray();
        return HistoricalIncidentRecoveryPolicy.ValidSupersedingApprovedDecisionSet(
            job.JobId, file.RelativePath, review.ContextHash!, review.ReviewAutomationReceipt, decisions);
    }

    internal static bool IsExactPreCadApprovedGenerationConflict(
        AgentBatchFileProgress file,
        AgentWorkflowOperation operation) =>
        file.State == AgentBatchFileState.Failed && file.Retryable &&
        string.Equals(file.ErrorCode, "JOB_STATE_CONFLICT", StringComparison.Ordinal) &&
        AgentWorkflowFileStore.ValidOpaque(file.GenerateOperationId ?? string.Empty) &&
        string.Equals(operation.OperationId, file.GenerateOperationId, StringComparison.Ordinal) &&
        operation.JobId == file.JobId && operation.Kind == "generation.execute" &&
        operation.State == "Failed" && operation.Stage == "Failed" &&
        operation.ErrorCode == "JOB_STATE_CONFLICT" && operation.Retryable &&
        operation.ErrorCategory is null && operation.TechnicalStage is null &&
        operation.NativeErrorStatus is null && operation.StartedAtUtc is null &&
        operation.DeadlineAtUtc is null && operation.CompletedAtUtc is not null &&
        operation.CreatedAtUtc <= operation.CompletedAtUtc &&
        operation.UpdatedAtUtc == operation.CompletedAtUtc &&
        operation.LastHeartbeatAtUtc == operation.CompletedAtUtc &&
        operation.CleanupOutcome is null;

    internal static bool IsPendingApprovedSupersedingBatchState(string state) =>
        state is "RecoveryRequired" or "Suspended" or "CompletedWithFailures";

    internal static bool IsExactPostApprovalProjection(
        AgentBatchFileProgress file,
        JobDocument job,
        TranslationReviewSnapshot? review = null) =>
        file.JobId == job.JobId && job.State == JobState.Approved && job.Version > 0 &&
        file.JobVersion == job.Version - 1 &&
        (review is null || (review.JobId == job.JobId && review.Version > 0 &&
                            file.ReviewVersion == review.Version - 1));

    private bool IsExactStaleHistoricalBootstrapProjection(
        AgentBatchDocument batch,
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        JobDocument job,
        DwgTranslationJobData data)
    {
        if (!IsRecovery25Batch(batch, plan) || batch.State is not ("RecoveryRequired" or "Suspended") ||
            data.Specification.ReviewAutomationScope is not { } scope ||
            scope.BatchId != batch.BatchId || scope.ManifestHash != batch.ManifestHash ||
            !string.Equals(scope.PolicyVersion, plan.Policy, StringComparison.Ordinal))
            return false;
        var review = Read<TranslationReviewSnapshot>(Path.Combine(
            _configuration.WorkspaceRoot, job.JobId.ToString("D"), "review", "session.json"));
        if (review is null || !IsExactPostGenerationFailureProjection(file, job, review) ||
            !string.Equals(review.ContextPolicyVersion, CadSemanticContextBuilder.PolicyVersionOneTwo, StringComparison.Ordinal) ||
            !string.Equals(review.ContextHash, file.ContextHash, StringComparison.Ordinal) ||
            review.Rows.Count == 0 || review.Rows.Any(row => row.FinalText is null || row.ExclusionReason is not null))
            return false;
        var decisions = review.Rows.Select(row => new ReviewDecisionInput(
            row.SegmentId, row.FinalText, row.ExclusionReason)).ToArray();
        return HistoricalIncidentRecoveryPolicy.ValidSupersedingApprovedDecisionSet(
            job.JobId, file.RelativePath, review.ContextHash!, review.ReviewAutomationReceipt, decisions);
    }

    internal static bool IsExactPostGenerationFailureProjection(
        AgentBatchFileProgress file,
        JobDocument job,
        TranslationReviewSnapshot review) =>
        file.JobId == job.JobId && file.State is AgentBatchFileState.Reviewing or AgentBatchFileState.Suspended &&
        file.JobVersion > 0 && job.State == JobState.Failed && job.Version == file.JobVersion + 3 &&
        file.ErrorCode is null && !file.Retryable &&
        !AgentWorkflowFileStore.ValidOpaque(file.GenerateOperationId ?? string.Empty) &&
        review.JobId == job.JobId && review.Version > 0 && file.ReviewVersion == review.Version - 1;

    private bool HasCadExchangeTimeoutEvidence(
        AgentBatchFileProgress file,
        JobDocument job,
        JsonObject? failure)
    {
        if (file.State is not (AgentBatchFileState.Failed or AgentBatchFileState.Suspended) ||
            file.JobId != job.JobId || File.Exists(file.OutputPath) || Directory.Exists(file.OutputPath) ||
            HasContextRecoveryArtifacts(file.OutputPath, job.JobId) || _cadProcessExists() || failure is null ||
            !failure.ContainsKey("category") || !failure.ContainsKey("retryable") || !failure.ContainsKey("stage") ||
            !failure.ContainsKey("technicalStage") || !failure.ContainsKey("nativeErrorStatus") ||
            !string.Equals(failure["code"]?.GetValue<string>(), "IPC_TIMEOUT", StringComparison.Ordinal) ||
            !string.Equals(failure["category"]?.GetValue<string>(), ErrorCategory.Transport.ToString(), StringComparison.Ordinal) ||
            failure["retryable"]?.GetValue<bool>() is not true ||
            !string.Equals(failure["stage"]?.GetValue<string>(), "Writing", StringComparison.Ordinal) ||
            failure["technicalStage"] is not null || failure["nativeErrorStatus"] is not null)
            return false;

        AgentWorkflowOperation? operation = null;
        if (AgentWorkflowFileStore.ValidOpaque(file.GenerateOperationId ?? string.Empty))
            operation = Read<AgentWorkflowOperation>(Path.Combine(_configuration.LogRoot, "workflow", "operations",
                file.GenerateOperationId + ".json"));
        else
        {
            var operationRoot = Path.Combine(_configuration.LogRoot, "workflow", "operations");
            try
            {
                operation = Directory.EnumerateFiles(operationRoot, "*.json", SearchOption.TopDirectoryOnly)
                    .Select(Read<AgentWorkflowOperation>)
                    .Where(candidate => candidate is not null && candidate.JobId == job.JobId &&
                        candidate.Kind == "generation.execute")
                    .OrderByDescending(candidate => candidate!.CreatedAtUtc)
                    .FirstOrDefault();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        if (operation is null || operation.JobId != job.JobId || operation.Kind != "generation.execute" ||
            operation.State != "Failed" || operation.Stage != "Failed" || operation.ErrorCode != "IPC_TIMEOUT" ||
            operation.ErrorCategory != ErrorCategory.Transport || !operation.Retryable ||
            operation.TechnicalStage is not null || operation.NativeErrorStatus is not null ||
            operation.CompletedAtUtc is null ||
            operation.CleanupOutcome is not ("IPC_TIMEOUT" or "CAD_PROCESS_EXITED"))
            return false;
        if (AgentWorkflowFileStore.ValidOpaque(file.GenerateOperationId ?? string.Empty) &&
            operation.OperationId != file.GenerateOperationId)
            return false;

        var receipt = Read<AgentCadLifecycleReceipt>(Path.Combine(_configuration.LogRoot, "workflow", "cad-receipts",
            job.JobId.ToString("D") + ".json"));
        return receipt is not null && receipt.JobId == job.JobId && receipt.OperationId == operation.OperationId &&
               receipt.ResponseHash is null && receipt.ReceivedAtUtc is null && receipt.ResponseErrorCode is null &&
               receipt.QuitRequestedAtUtc is not null && receipt.ProcessExitedAtUtc is not null &&
               IsExactCadExchangeTimeoutCleanup(operation.CleanupOutcome, receipt.CleanupOutcome) &&
               !receipt.RequiresProcessTerminationApproval;
    }

    internal static bool IsExactCadExchangeTimeoutCleanup(string? operationOutcome, string? receiptOutcome) =>
        string.Equals(operationOutcome, receiptOutcome, StringComparison.Ordinal) &&
        operationOutcome is "IPC_TIMEOUT" or "CAD_PROCESS_EXITED";

    private bool HasTerminalCadUnavailableReadEvidence(
        AgentBatchFileProgress file,
        JobDocument job,
        DwgTranslationJobData data,
        JsonObject? failure)
    {
        if (file.State != AgentBatchFileState.Failed || file.JobId != job.JobId ||
            file.JobVersion != job.Version || file.ErrorCode != "CAD_UNAVAILABLE" || file.Retryable ||
            !AgentWorkflowFileStore.ValidOpaque(file.PrepareOperationId ?? string.Empty) ||
            file.GenerateOperationId is not null || file.OutputHash is not null || file.OutputBytes != 0 ||
            data.Inspection is not null || data.Segments is not null || data.OutputHash is not null ||
            data.ValidationReport is not null || data.OutputBytes != 0 ||
            data.RecoveryReviewScopeSeal is not null || data.GenerationFailureReconciliationReceipt is not null ||
            data.ApprovedContextRevalidationReceipt is not null ||
            File.Exists(file.OutputPath) || Directory.Exists(file.OutputPath) ||
            HasContextRecoveryArtifacts(file.OutputPath, job.JobId) || _cadProcessExists() ||
            failure is null || failure.Count != 9 ||
            !failure.ContainsKey("code") || !failure.ContainsKey("category") ||
            !failure.ContainsKey("retryable") || !failure.ContainsKey("stage") ||
            !failure.ContainsKey("exceptionType") || !failure.ContainsKey("diagnosticId") ||
            !failure.ContainsKey("technicalStage") || !failure.ContainsKey("nativeErrorStatus") ||
            !failure.ContainsKey("invariantDiagnostics") ||
            failure["code"]?.GetValue<string>() != "CAD_UNAVAILABLE" ||
            failure["category"]?.GetValue<string>() != ErrorCategory.Environment.ToString() ||
            failure["retryable"]?.GetValue<bool>() is not false || failure["stage"] is not null ||
            failure["exceptionType"] is not null || failure["diagnosticId"] is not null ||
            failure["technicalStage"] is not null || failure["nativeErrorStatus"] is not null ||
            failure["invariantDiagnostics"] is not null)
            return false;

        var operation = Read<AgentWorkflowOperation>(Path.Combine(
            _configuration.LogRoot, "workflow", "operations", file.PrepareOperationId + ".json"));
        if (operation is null || operation.OperationId != file.PrepareOperationId ||
            operation.JobId != job.JobId || operation.Kind != "translation.prepare" ||
            operation.State != "Failed" || operation.Stage != "Failed" ||
            operation.ErrorCode != "CAD_UNAVAILABLE" || operation.Retryable ||
            operation.ErrorCategory is not null || operation.TechnicalStage is not null ||
            operation.NativeErrorStatus is not null || operation.CompletedAtUtc is null ||
            operation.CreatedAtUtc > operation.CompletedAtUtc || operation.UpdatedAtUtc != operation.CompletedAtUtc ||
            operation.LastHeartbeatAtUtc != operation.CompletedAtUtc || operation.DurationMilliseconds is null or <= 0 ||
            operation.CleanupOutcome is not null)
            return false;

        var receipt = Read<AgentCadLifecycleReceipt>(Path.Combine(
            _configuration.LogRoot, "workflow", "cad-receipts", job.JobId.ToString("D") + ".json"));
        return receipt is not null && receipt.JobId == job.JobId &&
               receipt.OperationId == operation.OperationId && Guid.TryParse(receipt.RequestId, out var requestId) &&
               requestId != Guid.Empty && ContractPatterns.Sha256().IsMatch(receipt.RequestHash) &&
               receipt.ProcessId > 0 && Path.IsPathFullyQualified(_configuration.AutoCadExecutablePath) &&
               PathsEqual(receipt.ExecutablePath, _configuration.AutoCadExecutablePath) &&
               receipt.ProcessStartedAtUtc >= operation.CreatedAtUtc &&
               receipt.LaunchedAtUtc >= receipt.ProcessStartedAtUtc &&
               ContractPatterns.Sha256().IsMatch(receipt.ResponseHash ?? string.Empty) &&
               receipt.ExtractedCount is null && receipt.ReceivedAtUtc is { } received &&
               received >= receipt.LaunchedAtUtc && receipt.QuitRequestedAtUtc is { } quit && quit >= received &&
               receipt.ProcessExitedAtUtc is { } exited && exited >= quit && exited <= operation.CompletedAtUtc &&
               receipt.ExitCode == 0 && receipt.CleanupOutcome == "CAD_PROCESS_EXITED" &&
               !receipt.RequiresProcessTerminationApproval && receipt.ResponseErrorCode == "CAD_UNAVAILABLE" &&
               receipt.ResponseErrorCategory == ErrorCategory.Environment && receipt.ResponseRetryable is false &&
               receipt.ResponseDiagnosticId is null && receipt.ResponseTechnicalStage is null &&
               receipt.ResponseNativeErrorStatus is null;
    }

    private static bool IsRecovery25Batch(AgentBatchDocument batch, AgentBatchPlan plan) => false;

    private static bool ExactRestoredBootstrap(
        string scriptPath,
        DateTime scriptWrittenAtUtc,
        DateTime operationCompletedAtUtc,
        string bootstrapRoot,
        string assemblyPath)
    {
        var name = Path.GetFileName(scriptPath);
        const string prefix = "netload-";
        const string suffix = ".scr";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var identifier = name[prefix.Length..^suffix.Length];
        var expected = CadNetloadBootstrapEvidencePolicy.Create(assemblyPath, bootstrapRoot, identifier);
        if (expected is null || !PathsEqual(expected.ScriptPath, scriptPath) ||
            !RegularFile(expected.OriginalTrustedPathsPath) || !RegularFile(expected.RestoredMarkerPath))
            return false;
        var originalInfo = new FileInfo(expected.OriginalTrustedPathsPath);
        var restoredInfo = new FileInfo(expected.RestoredMarkerPath);
        if (originalInfo.LastWriteTimeUtc < scriptWrittenAtUtc ||
            restoredInfo.LastWriteTimeUtc < originalInfo.LastWriteTimeUtc ||
            restoredInfo.LastWriteTimeUtc > operationCompletedAtUtc)
            return false;
        var script = File.ReadAllText(scriptPath);
        var original = File.ReadAllText(expected.OriginalTrustedPathsPath);
        var restored = File.ReadAllText(expected.RestoredMarkerPath);
        return string.Equals(script, expected.ScriptContent, StringComparison.Ordinal) &&
               CadNetloadBootstrapEvidencePolicy.IsOriginalSnapshot(original) &&
               string.Equals(restored, CadNetloadBootstrapEvidencePolicy.RestoredMarkerContent, StringComparison.Ordinal);
    }

    private static bool DefaultCadProcessExists()
    {
        try
        {
            var processes = Process.GetProcessesByName("acad").Concat(Process.GetProcessesByName("accoreconsole")).ToArray();
            foreach (var process in processes) process.Dispose();
            return processes.Length != 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static bool RegularDirectory(string path)
    {
        try { return Directory.Exists(path) && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) == 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool RegularFile(string path)
    {
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static AgentBatchRecoveryAction? LegacyRecoveryAction(AgentBatchFileProgress file) =>
        file.State == AgentBatchFileState.Reviewing && file.JobId is not null
            ? AgentBatchRecoveryAction.ContinueFromReview
            : file.State == AgentBatchFileState.Failed && file.ErrorCode is
                    "WORKFLOW_BACKGROUND_STALLED" or "JOB_STATE_CONFLICT" or "CAD_PROCESS_EXIT_REQUIRED" or
                    "WORKFLOW_UNEXPECTED_FAILURE" or "CAD_BUSY" or "CAD_PROCESS_INITIALIZATION_FAILED" or
                    "BATCH_FILE_GENERATE_FAILED"
                    ? AgentBatchRecoveryAction.RetryCadFresh : (AgentBatchRecoveryAction?)null;

    private sealed record RecoveryActionResolution(AgentBatchRecoveryAction? Action, AgentError? Error,
        ApprovedContextRevalidationBinding? ContextRevalidation = null);

    private static string? ArtifactHash(string path)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            return "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool HasContextRecoveryArtifacts(string outputPath, Guid jobId)
    {
        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
            var candidate = CandidatePath(outputPath, jobId);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return true;
            return Directory.EnumerateFileSystemEntries(directory, ".*", SearchOption.TopDirectoryOnly).Any(path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains(".candidate-", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains(".staging", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith(".dwgtranslator-normalized-", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains(".orphan", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }

    private AgentBatchRecoveryOutputEvidence? CompletedJobEvidence(AgentBatchDocument document, AgentBatchFileProgress file)
    {
        if (file.JobId is not { } jobId) return null;
        var job = Read<JobDocument>(Path.Combine(_configuration.WorkspaceRoot, jobId.ToString("D"), "job.json"));
        if (job is null || job.JobId != jobId || job.State != JobState.Completed) return null;
        try
        {
            var data = job.Data.Deserialize<DwgTranslationJobData>(Json);
            if (data is null) return null;
            var specification = data.Specification;
            var validation = data.ValidationReport;
            var expectedSource = Path.Combine(document.SourceDirectory, file.RelativePath);
            if (specification is null || validation is null || !validation.AutomaticPass ||
                !PathsEqual(specification.SourcePath, expectedSource) || !PathsEqual(specification.OutputPath, file.OutputPath) ||
                !string.Equals(specification.SourceHash, file.SourceHash, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(data.OutputHash) ||
                !string.Equals(data.OutputHash, validation.OutputHash, StringComparison.Ordinal))
                return null;
            var bytes = OutputBytes(file.OutputPath, data.OutputBytes);
            return bytes > 0 ? new(data.OutputHash, bytes) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long OutputBytes(string path, long durableBytes) => durableBytes > 0 ? durableBytes :
        File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool AddCompletedEvidence(Dictionary<string, AgentBatchRecoveryOutputEvidence> evidence, string path,
        string hash, long bytes)
    {
        if (bytes <= 0 || string.IsNullOrWhiteSpace(hash)) return false;
        var value = new AgentBatchRecoveryOutputEvidence(hash, bytes);
        if (evidence.TryGetValue(path, out var existing)) return existing == value;
        evidence.Add(path, value);
        return true;
    }

    private static bool AddExactEvidence(Dictionary<string, string> evidence, string path, string hash)
    {
        if (evidence.TryGetValue(path, out var existing)) return string.Equals(existing, hash, StringComparison.Ordinal);
        evidence.Add(path, hash);
        return true;
    }

    private bool MatchesRecoveryLineage(AgentBatchDocument child, AgentBatchDocument parent)
    {
        if (child.OriginalBatchId != parent.BatchId || string.IsNullOrWhiteSpace(child.RecoveryPlanId) ||
            string.IsNullOrWhiteSpace(child.StartIdempotencyKey)) return false;
        var plan = LoadRecoveryPlan(child.RecoveryPlanId);
        if (plan is null || plan.RecoveryBatchId != child.BatchId || plan.OriginalBatchId != parent.BatchId ||
            !string.Equals(plan.RecoveryManifestHash, child.ManifestHash, StringComparison.Ordinal) ||
            !PathsEqual(parent.OutputDirectory, child.OutputDirectory) ||
            !PathsEqual(plan.Plan.OutputDirectory, child.OutputDirectory) || plan.Entries.Count != child.Files.Count)
            return false;
        return plan.Entries.All(entry => child.Files.Any(file =>
            string.Equals(file.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(file.SourceHash, entry.SourceHash, StringComparison.Ordinal) &&
            PathsEqual(file.OutputPath, entry.OutputPath)));
    }

    private static string CandidatePath(string finalPath, Guid jobId) =>
        Path.Combine(Path.GetDirectoryName(finalPath)!, $".{Path.GetFileNameWithoutExtension(finalPath)}.candidate-{jobId:N}{Path.GetExtension(finalPath)}");
    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string Opaque() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static AgentEnvelope Success(string command, JsonNode? data) => new(AgentQueryService.ResponseSchema, true, command, DateTimeOffset.UtcNow, data, null);
    private static AgentEnvelope Failure(string command, string code, string message) => Failure(command, new AgentError(code, message));
    private static AgentEnvelope Failure(string command, AgentError error) => new(AgentQueryService.ResponseSchema, false, command, DateTimeOffset.UtcNow, null, error);
}
