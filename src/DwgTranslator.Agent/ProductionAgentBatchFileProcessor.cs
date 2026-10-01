using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Agent;

/// <summary>Orchestrates the already-approved single-file workflow; it never calls CAD or OpenAI adapters directly.</summary>
public sealed class ProductionAgentBatchFileProcessor : IAgentBatchFileProcessor
{
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private readonly AgentWorkflowService _workflow;
    private readonly string _evidenceRoot;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _deadline;

    public ProductionAgentBatchFileProcessor(AgentWorkflowService workflow, string evidenceRoot,
        TimeSpan? pollInterval = null, TimeSpan? deadline = null)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _evidenceRoot = evidenceRoot ?? throw new ArgumentNullException(nameof(evidenceRoot));
        _poll = pollInterval ?? TimeSpan.FromSeconds(1);
        _deadline = deadline ?? TimeSpan.FromMinutes(20);
    }

    public async Task<AgentBatchFileProgress> PrepareAsync(AgentBatchPlan plan, AgentBatchManifestEntry file,
        CancellationToken cancellationToken)
    {
        var checkpoint = LoadCheckpoint(plan, file, "prepare");
        if (checkpoint is not null)
            return await ResumePrepareAsync(file, checkpoint, cancellationToken).ConfigureAwait(false);
        var parentDirectory = Path.GetDirectoryName(file.OutputPath)!;
        Directory.CreateDirectory(parentDirectory); // parent batch approval already consumed by BatchStart
        if (File.Exists(file.OutputPath)) return Failed(file, "OUTPUT_ALREADY_EXISTS");

        var planned = await _workflow.CreateBatchChildTranslationPlanAsync(
            new(file.SourcePath, plan.TargetLanguage, plan.RoutingMode, null, file.OutputPath,
                Authority(plan, file)), cancellationToken).ConfigureAwait(false);
        if (!planned.Success) return Failed(file, planned.Error?.Code ?? "BATCH_CHILD_PLAN_FAILED", planned.Error?.Retryable ?? false);
        var data = planned.Data!.AsObject();
        var approval = data["approval"]!.AsObject();
        var childPlanId = Text(data, "planId");
        var childApprovalId = Text(approval, "approvalId");
        var consent = Text(approval, "consent");
        PersistBinding(plan, file, "prepare", childPlanId, childApprovalId, consent);

        var prepared = await _workflow.PrepareAsync(new(childPlanId, childApprovalId, file.Sha256, consent,
            Key(plan, file, "prepare")), cancellationToken).ConfigureAwait(false);
        if (!prepared.Success) return Failed(file, prepared.Error?.Code ?? "BATCH_CHILD_PREPARE_FAILED", prepared.Error?.Retryable ?? false);
        var started = prepared.Data!.AsObject();
        var jobId = Guid.Parse(Text(started, "jobId"));
        var operationId = Text(started, "operationId");
        SaveCheckpoint(plan, file, "prepare", jobId, operationId, Hash(childApprovalId));
        var terminal = await WaitForAsync(jobId, operationId,
            new HashSet<string>(["ReviewRequired", "Failed", "Cancelled"], StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        if (!terminal.Success) return Failed(file, terminal.Error?.Code ?? "BATCH_CHILD_STATUS_FAILED", terminal.Error?.Retryable ?? false) with
        { JobId = jobId, PrepareOperationId = operationId, ChildPrepareApprovalHash = Hash(childApprovalId) };
        var status = terminal.Data!.AsObject();
        if (Text(status, "state") != "ReviewRequired")
            return FailedFromStatus(file, status, "BATCH_CHILD_PREPARE_FAILED", operationId) with
            { JobId = jobId, JobVersion = Long(status, "jobVersion"), PrepareOperationId = operationId, ChildPrepareApprovalHash = Hash(childApprovalId) };
        var review = status["review"]?.AsObject();
        var reviewSnapshot = await GetReviewSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!reviewSnapshot.IsSuccess) return Failed(file, reviewSnapshot.ErrorCode!, reviewSnapshot.Retryable) with
        { JobId = jobId, JobVersion = Long(status, "jobVersion"), PrepareOperationId = operationId, ChildPrepareApprovalHash = Hash(childApprovalId) };
        return new(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Reviewing,
            JobId: jobId, InputTokens: reviewSnapshot.InputTokens, OutputTokens: reviewSnapshot.OutputTokens,
            ProviderRequests: reviewSnapshot.ProviderRequests,
            ChildPrepareApprovalHash: Hash(childApprovalId), PrepareOperationId: operationId,
            JobVersion: Long(status, "jobVersion"), EffectiveModel: review?["baseModel"]?.GetValue<string>());
    }

    public async Task<AgentBatchFileProgress> ApproveAndGenerateAsync(AgentBatchPlan plan, AgentBatchFileProgress file,
        CancellationToken cancellationToken)
    {
        if (file.JobId is null) return file with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_CHILD_JOB_MISSING" };
        var checkpoint = LoadCheckpoint(plan, file, "generation");
        if (checkpoint is not null)
            return await ResumeGenerationAsync(file, checkpoint, cancellationToken).ConfigureAwait(false);
        var contextual = plan.Policy == AgentBatchPolicy.ContextualPolicyVersion;
        JsonObject? contextualStatus = null;
        if (contextual)
        {
            var statusEnvelope = await _workflow.JobStatusAsync(file.JobId.Value, cancellationToken).ConfigureAwait(false);
            if (!statusEnvelope.Success) return Failure(file, statusEnvelope);
            contextualStatus = statusEnvelope.Data!.AsObject();
            if (Text(contextualStatus, "state") != "Approved")
                return Failure(file, "BATCH_CONTEXTUAL_REVIEW_AUTHORITY_REQUIRED");
        }
        var rows = new List<JsonObject>();
        AgentEnvelope page;
        var pageNumber = 1;
        do
        {
            page = await _workflow.GetReviewAsync(new(file.JobId.Value, pageNumber++, 200, true), cancellationToken).ConfigureAwait(false);
            if (!page.Success) return Failure(file, page);
            rows.AddRange(page.Data!["rows"]!.AsArray().Select(node => node!.AsObject()));
        } while (rows.Count < page.Data!["total"]!.GetValue<int>());

        var decisions = new List<AgentReviewDecision>(rows.Count);
        var matches = 0;
        var ambiguities = 0;
        var edits = 0;
        var terminologyVisualReview = false;
        var textCount = rows.Count(row => string.Equals(Text(row, "entityType"), "TEXT", StringComparison.Ordinal));
        var mtextCount = rows.Count(row => string.Equals(Text(row, "entityType"), "MTEXT", StringComparison.Ordinal));
        var sourceById = rows.ToDictionary(row => Text(row, "segmentId"), row => Text(row, "originalText"), StringComparer.Ordinal);
        var reviewData = page.Data!.AsObject();
        var contextHash = Text(reviewData, "contextHash");
        var usage = reviewData["usage"]!.AsObject();
        var receiptNode = reviewData["automationReceipt"] as JsonObject;
        var reviewProjection = file with
        {
            JobVersion = contextual ? Long(contextualStatus!, "jobVersion") : file.JobVersion,
            ReviewVersion = Long(reviewData, "reviewVersion"),
            ContextPolicyVersion = OptionalText(reviewData, "contextPolicyVersion"),
            ContextHash = string.IsNullOrWhiteSpace(contextHash) ? null : contextHash,
            ReviewAutomationReceiptHash = receiptNode is null ? null : Hash(receiptNode.ToJsonString()),
            ExplicitDecisionCount = contextual ? rows.Count : 0,
            TextCount = textCount,
            MTextCount = mtextCount,
            InputTokens = Long(usage, "inputTokens"),
            OutputTokens = Long(usage, "outputTokens"),
            ProviderRequests = Long(usage, "providerRequests"),
            EffectiveModel = reviewData["baseModel"]?.GetValue<string>() ?? file.EffectiveModel
        };
        if (contextual && (!ContractPatterns.Sha256().IsMatch(contextHash) ||
            reviewData["contextPolicyVersion"]?.GetValue<string>() != plan.ContextPolicyVersion))
            return Failure(reviewProjection, "BATCH_HUMAN_REVIEW_REQUIRED") with
            { ReviewGateCode = "CONTEXT_BINDING_INVALID" };
        ReviewAutomationReceipt? receipt = null;
        var hasReceipt = contextual && TryReviewAutomationReceipt(receiptNode, out receipt);
        var sealedDecisions = contextual
            ? rows.Select(row => new ReviewDecisionInput(
                Text(row, "segmentId"), Text(row, "finalText"), null)).ToArray()
            : [];
        var exactSupersedingSet = hasReceipt &&
            HistoricalIncidentRecoveryPolicy.ValidSupersedingApprovedDecisionSet(file.JobId.Value,
                file.RelativePath, contextHash, receipt, sealedDecisions);
        if (contextual && !ValidExternalAuthority(receiptNode, plan, contextHash, rows) && !exactSupersedingSet)
            return Failure(reviewProjection, "BATCH_CONTEXTUAL_REVIEW_AUTHORITY_REQUIRED") with
            { ReviewGateCode = "CONTEXTUAL_AUTHORITY_INVALID" };
        if (hasReceipt && HistoricalIncidentRecoveryPolicy.IsSupersedingAuthorityScope(receipt) &&
            !exactSupersedingSet)
            return Failure(reviewProjection, "BATCH_HUMAN_REVIEW_REQUIRED") with
            { ReviewGateCode = "SUPERSEDING_DECISION_SET_INVALID" };
        foreach (var row in rows)
        {
            var segmentId = Text(row, "segmentId");
            var source = Text(row, "originalText");
            var proposal = Text(row, "proposedText");
            var reviewed = contextual ? Text(row, "finalText") : proposal;
            var metadata = row["context"] as JsonObject;
            if (metadata is null) return Failure(reviewProjection, "BATCH_HUMAN_REVIEW_REQUIRED") with
            { ReviewGateCode = "CONTEXT_METADATA_MISSING" };
            var layer = OptionalText(metadata, "layer");
            var layout = OptionalText(metadata, "layout");
            var sheetRole = OptionalText(metadata, "sheetRole");
            var discipline = OptionalText(metadata, "discipline");
            var disciplineConflict = metadata["disciplineConflict"]?.GetValue<bool>() ?? false;
            var signals = metadata["signals"]?.AsArray().Select(node => node?.GetValue<string>() ?? string.Empty)
                .Where(value => value.Length > 0).ToArray() ?? [];
            var neighborIds = metadata["neighborIds"]?.AsArray().Select(node => node?.GetValue<string>() ?? string.Empty)
                .Where(value => value.Length > 0).ToArray() ?? [];
            if (contextual && (metadata["version"]?.GetValue<string>() != plan.ContextPolicyVersion ||
                !ContractPatterns.Sha256().IsMatch(OptionalText(metadata, "contextHash") ?? string.Empty) ||
                disciplineConflict || discipline is null or "Unknown" ||
                signals.Contains("VERTICAL_LEVEL_CONTEXT_CONFLICT", StringComparer.Ordinal) &&
                    !HasAuthorizedVerticalResolution(receiptNode, row) ||
                neighborIds.Any(id => id == segmentId || !sourceById.ContainsKey(id))))
                return reviewProjection with
                {
                    State = AgentBatchFileState.Failed,
                    ErrorCode = "BATCH_HUMAN_REVIEW_REQUIRED",
                    TerminologyMatches = matches,
                    TerminologyAmbiguities = ambiguities,
                    ReviewGateCode = disciplineConflict ? "DISCIPLINE_CONFLICT" : discipline is null or "Unknown"
                        ? "DISCIPLINE_UNRESOLVED" : "VERTICAL_CONTEXT_CONFLICT"
                };
            var neighborText = neighborIds.Length == 0 ? null : string.Join("\n", neighborIds.Select(id => sourceById[id]));
            var terminologyContext = new TerminologyContext(layer, layout, sheetRole, neighborText,
                string.Join(' ', new[] { discipline }.Concat(signals).Where(value => !string.IsNullOrWhiteSpace(value))!));
            var sourceTerminology = ArchitecturalMepTerminologyPolicy.Apply(source, terminologyContext);
            var corrected = ArchitecturalMepTerminologyPolicy.Apply(reviewed, terminologyContext);
            var evidence = sourceTerminology.Matches.Concat(corrected.Matches).ToArray();
            matches += sourceTerminology.Matches.Count;
            ambiguities += evidence.Count(match => match.Ambiguous);
            terminologyVisualReview |= sourceTerminology.Matches.Any(match =>
                match.ConceptId is "raised-access-floor-level" or "finished-ceiling-level");
            var errors = ArchitecturalMepTerminologyPolicy.Validate(source, corrected.Text, evidence).ToList();
            if (sourceTerminology.Matches.Any(match => match.Target is not null &&
                !corrected.Text.Contains(match.Target, StringComparison.OrdinalIgnoreCase)))
                errors.Add("TERMINOLOGY_TARGET_MISSING");
            var resolvedSupersedingAmbiguity = HistoricalIncidentRecoveryPolicy.PermitsResolvedTerminologyAmbiguity(
                exactSupersedingSet, sourceTerminology, corrected, errors);
            if (!(sourceTerminology.IsValid || resolvedSupersedingAmbiguity) || !corrected.IsValid ||
                errors.Count != 0 && !resolvedSupersedingAmbiguity ||
                contextual && (Text(row, "state") != SegmentState.Approved.ToString() ||
                    !string.Equals(reviewed, corrected.Text, StringComparison.Ordinal)))
                return reviewProjection with
                {
                    State = AgentBatchFileState.Failed,
                    ErrorCode = "BATCH_HUMAN_REVIEW_REQUIRED",
                    TerminologyMatches = matches,
                    TerminologyAmbiguities = ambiguities,
                    ReviewGateCode = errors.Contains("TERMINOLOGY_TARGET_MISSING", StringComparer.Ordinal)
                        ? "TERMINOLOGY_TARGET_MISSING" : "REVIEW_INVARIANT_FAILED"
                };
            var edited = !string.Equals(proposal, contextual ? reviewed : corrected.Text, StringComparison.Ordinal);
            if (edited) edits++;
            if (!contextual)
                decisions.Add(new(segmentId, edited ? "edit" : "approve", edited ? corrected.Text : null));
        }

        long approvedVersion;
        if (contextual)
        {
            approvedVersion = Long(contextualStatus!, "jobVersion");
        }
        else
        {
            var expectedContextHash = OptionalText(reviewData, "contextHash");
            var expectedReviewVersion = expectedContextHash is null ? (long?)null : Long(reviewData, "reviewVersion");
            var applied = await _workflow.ApplyReviewAsync(new(file.JobId.Value, file.JobVersion, decisions, false, null,
                Key(plan, file, "review"), expectedReviewVersion, expectedContextHash), cancellationToken).ConfigureAwait(false);
            if (!applied.Success) return Failure(reviewProjection with
            { ExplicitDecisionCount = decisions.Count }, applied);
            approvedVersion = Long(applied.Data!.AsObject(), "jobVersion");
        }
        reviewProjection = reviewProjection with
        {
            ExplicitDecisionCount = contextual ? rows.Count : decisions.Count,
            TerminologyMatches = matches,
            TerminologyAmbiguities = ambiguities,
            ReviewEdits = edits
        };
        var generationPlan = await _workflow.CreateGenerationPlanAsync(new(file.JobId.Value), cancellationToken).ConfigureAwait(false);
        if (!generationPlan.Success) return Failure(reviewProjection, generationPlan);
        var generation = generationPlan.Data!.AsObject();
        var approval = generation["approval"]!.AsObject();
        var generationPlanId = Text(generation, "generationPlanId");
        var reviewHash = Text(generation, "reviewHash");
        var approvalId = Text(approval, "approvalId");
        var phrase = Text(approval, "phrase");
        reviewProjection = reviewProjection with
        {
            ReviewHash = reviewHash,
            ChildGenerationApprovalHash = Hash(approvalId)
        };
        PersistBinding(plan, new(file.RelativePath, file.SourceHash, file.OutputPath, 0, file.SourceHash, default),
            "generation", generationPlanId, approvalId, phrase);
        var generated = await _workflow.GenerateAsync(new(generationPlanId, approvalId, approvedVersion, phrase,
            Key(plan, file, "generate")), cancellationToken).ConfigureAwait(false);
        if (!generated.Success) return Failure(reviewProjection, generated);
        var operationId = Text(generated.Data!.AsObject(), "operationId");
        SaveCheckpoint(plan, file, "generation", file.JobId.Value, operationId, Hash(approvalId));
        var terminal = await WaitForAsync(file.JobId.Value, operationId,
            new HashSet<string>(["Completed", "Failed", "Cancelled"], StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        if (!terminal.Success) return Failure(reviewProjection with { GenerateOperationId = operationId }, terminal);
        var status = terminal.Data!.AsObject();
        if (Text(status, "state") != "Completed")
            return FailureFromStatus(reviewProjection, status, "BATCH_CHILD_GENERATE_FAILED", operationId);
        var validation = status["validation"]?.AsObject();
        return reviewProjection with
        {
            State = AgentBatchFileState.Completed,
            ErrorCode = null,
            Retryable = false,
            JobVersion = Long(status, "jobVersion"),
            OutputHash = status["outputHash"]?.GetValue<string>(),
            GenerateOperationId = operationId,
            OutputBytes = OutputBytes(file.OutputPath),
            ChildGenerationApprovalHash = Hash(approvalId),
            ReviewHash = reviewHash,
            TerminologyMatches = matches,
            TerminologyAmbiguities = ambiguities,
            ReviewEdits = edits,
            ExplicitDecisionCount = contextual ? rows.Count : decisions.Count,
            ReviewGateCode = null,
            ValidationPolicy = validation?["policy"]?.GetValue<string>(),
            VisualReviewPending = terminologyVisualReview || (validation?["visualReviewPending"]?.GetValue<bool>() ?? false),
            TextCount = textCount,
            MTextCount = mtextCount,
            InputTokens = Long(usage, "inputTokens"),
            OutputTokens = Long(usage, "outputTokens"),
            ProviderRequests = Long(usage, "providerRequests"),
            EffectiveModel = page.Data!["baseModel"]?.GetValue<string>() ?? file.EffectiveModel
        };
    }

    public async Task<AgentBatchFileProgress> ResumeTranslationMissingOnlyAsync(
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        ReviewAutomationScopeTransition scopeTransition,
        CancellationToken cancellationToken)
    {
        if (file.JobId is null) return file with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_CHILD_JOB_MISSING" };
        var resumed = await _workflow.ResumeTranslationMissingOnlyAsync(file.JobId.Value, file.JobVersion, scopeTransition,
            Key(plan, file, "resume-missing-only"), cancellationToken).ConfigureAwait(false);
        if (!resumed.Success) return Failure(file, resumed);
        var operationId = Text(resumed.Data!.AsObject(), "operationId");
        var terminal = await WaitForAsync(file.JobId.Value, operationId,
            new HashSet<string>(["ReviewRequired", "Failed", "Cancelled"], StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        if (!terminal.Success) return Failure(file, terminal) with { PrepareOperationId = operationId };
        var status = terminal.Data!.AsObject();
        return Text(status, "state") == "ReviewRequired"
            ? await ReconcileReviewAsync(plan, file with { PrepareOperationId = operationId }, scopeTransition,
                scopeAlreadyRebound: true, cancellationToken).ConfigureAwait(false)
            : FailureFromStatus(file, status, "BATCH_CHILD_RESUME_FAILED", operationId);
    }

    public async Task<AgentBatchFileProgress> RevalidateApprovedContextAsync(
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        ApprovedContextRevalidationTransition transition,
        CancellationToken cancellationToken)
    {
        if (file.JobId is null || plan.ContextPolicyVersion != CadSemanticContextBuilder.PolicyVersionOneTwo ||
            !ApprovedContextRevalidationPolicy.IsValid(transition.Binding) ||
            transition.Binding.ExpectedJobVersion != file.JobVersion)
            return file with
            {
                State = AgentBatchFileState.Failed,
                ErrorCode = "BATCH_RECOVERY_APPROVED_CONTEXT_BINDING_INVALID",
                Retryable = false
            };
        var reopened = await _workflow.RevalidateApprovedContextAsync(file.JobId.Value, file.JobVersion,
            transition, cancellationToken).ConfigureAwait(false);
        if (!reopened.Success) return Failure(file, reopened);
        var data = reopened.Data!.AsObject();
        if (Text(data, "state") != "ReviewRequired" ||
            Text(data, "contextPolicyVersion") != CadSemanticContextBuilder.PolicyVersionOneTwo ||
            Text(data, "contextHash") != transition.Binding.TargetContextHash)
            return Failure(file, "BATCH_RECOVERY_APPROVED_CONTEXT_RESULT_INVALID");
        return await ReconcileReviewAsync(plan, file with
        {
            JobVersion = Long(data, "jobVersion"),
            State = AgentBatchFileState.Reviewing,
            ErrorCode = null,
            Retryable = false
        }, scopeTransition: null, cancellationToken).ConfigureAwait(false);
    }

    public Task<AgentBatchFileProgress> ReconcileReviewAsync(
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        ReviewAutomationScopeTransition? scopeTransition,
        CancellationToken cancellationToken) =>
        ReconcileReviewAsync(plan, file, scopeTransition, scopeAlreadyRebound: false, cancellationToken);

    private async Task<AgentBatchFileProgress> ReconcileReviewAsync(
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        ReviewAutomationScopeTransition? scopeTransition,
        bool scopeAlreadyRebound,
        CancellationToken cancellationToken)
    {
        if (file.JobId is null)
            return file with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_CHILD_JOB_MISSING", Retryable = false };
        var terminal = await _workflow.JobStatusAsync(file.JobId.Value, cancellationToken).ConfigureAwait(false);
        if (!terminal.Success) return Failure(file, terminal);
        var status = terminal.Data!.AsObject();
        var useCurrentVersionForRebind = false;
        if (Text(status, "state") == "Failed" &&
            string.Equals(status["lastError"]?["code"]?.GetValue<string>(), "GEOMETRY_INVARIANTS_CHANGED", StringComparison.Ordinal))
        {
            var reconciled = await _workflow.ReconcileFailedGeometryForReviewAsync(new(
                file.JobId.Value, Long(status, "jobVersion"), file.SourceHash, file.OutputPath), cancellationToken).ConfigureAwait(false);
            if (!reconciled.Success) return Failure(file, reconciled);
            terminal = await _workflow.JobStatusAsync(file.JobId.Value, cancellationToken).ConfigureAwait(false);
            if (!terminal.Success) return Failure(file, terminal);
            status = terminal.Data!.AsObject();
            useCurrentVersionForRebind = true;
        }
        var approvedSupersedingCandidate = Text(status, "state") == "Approved" && scopeTransition is not null;
        if (Text(status, "state") != "ReviewRequired" && !approvedSupersedingCandidate)
            return Failure(file, "BATCH_CHILD_REVIEW_NOT_READY");
        if (scopeTransition is not null && !scopeAlreadyRebound && !approvedSupersedingCandidate)
        {
            var rebound = await _workflow.RebindReviewAutomationScopeAsync(
                file.JobId.Value, useCurrentVersionForRebind ? Long(status, "jobVersion") : file.JobVersion,
                scopeTransition, cancellationToken).ConfigureAwait(false);
            if (!rebound.Success) return Failure(file, rebound);
            terminal = await _workflow.JobStatusAsync(file.JobId.Value, cancellationToken).ConfigureAwait(false);
            if (!terminal.Success) return Failure(file, terminal);
            status = terminal.Data!.AsObject();
            if (Text(status, "state") != "ReviewRequired")
                return Failure(file, "BATCH_CHILD_REVIEW_NOT_READY");
        }

        var rows = new List<JsonObject>();
        var segmentIds = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? reviewData = null;
        var pageNumber = 1;
        var expectedTotal = -1;
        do
        {
            var page = await _workflow.GetReviewAsync(new(
                    file.JobId.Value, pageNumber++, 200, approvedSupersedingCandidate), cancellationToken)
                .ConfigureAwait(false);
            if (!page.Success) return Failure(file, page);
            var data = page.Data!.AsObject();
            var total = data["total"]?.GetValue<int>() ?? -1;
            if (total < 0 || (expectedTotal >= 0 && total != expectedTotal))
                return Failure(file, "BATCH_REVIEW_RECONCILIATION_INVALID");
            expectedTotal = total;
            reviewData ??= data;
            var pageRows = data["rows"]?.AsArray();
            if (pageRows is null || (pageRows.Count == 0 && rows.Count < expectedTotal))
                return Failure(file, "BATCH_REVIEW_RECONCILIATION_INVALID");
            foreach (var node in pageRows)
            {
                var row = node?.AsObject();
                if (row is null || !segmentIds.Add(Text(row, "segmentId")))
                    return Failure(file, "BATCH_REVIEW_RECONCILIATION_INVALID");
                rows.Add(row);
            }
        } while (rows.Count < expectedTotal);

        if (rows.Count != expectedTotal || reviewData is null)
            return Failure(file, "BATCH_REVIEW_RECONCILIATION_INVALID");
        if (approvedSupersedingCandidate)
        {
            var approvedContextHash = OptionalText(reviewData, "contextHash") ?? string.Empty;
            var approvedReceiptNode = reviewData["automationReceipt"];
            var approvedDecisions = rows.Select(row => new ReviewDecisionInput(
                Text(row, "segmentId"), OptionalText(row, "finalText"), null)).ToArray();
            if (!TryReviewAutomationReceipt(approvedReceiptNode, out var approvedReceipt) || approvedReceipt is null ||
                !HistoricalIncidentRecoveryPolicy.ValidSupersedingApprovedDecisionSet(
                    file.JobId.Value, file.RelativePath, approvedContextHash, approvedReceipt, approvedDecisions) ||
                !ValidApprovedSupersedingTransition(plan, file, scopeTransition!, approvedReceipt))
                return Failure(file, "BATCH_SUPERSEDING_APPROVED_REVIEW_INVALID");
        }
        var usage = reviewData["usage"]?.AsObject();
        var contextHash = OptionalText(reviewData, "contextHash");
        var receipt = reviewData["automationReceipt"] as JsonObject;
        return file with
        {
            State = AgentBatchFileState.Reviewing,
            ErrorCode = null,
            Retryable = false,
            JobVersion = Long(status, "jobVersion"),
            ReviewVersion = Long(reviewData, "reviewVersion"),
            ContextPolicyVersion = OptionalText(reviewData, "contextPolicyVersion"),
            ContextHash = string.IsNullOrWhiteSpace(contextHash) ? null : contextHash,
            ReviewAutomationReceiptHash = receipt is null ? null : Hash(receipt.ToJsonString()),
            ExplicitDecisionCount = rows.Count,
            TextCount = rows.Count(row => string.Equals(Text(row, "entityType"), "TEXT", StringComparison.Ordinal)),
            MTextCount = rows.Count(row => string.Equals(Text(row, "entityType"), "MTEXT", StringComparison.Ordinal)),
            InputTokens = usage is null ? 0 : Long(usage, "inputTokens"),
            OutputTokens = usage is null ? 0 : Long(usage, "outputTokens"),
            ProviderRequests = usage is null ? 0 : Long(usage, "providerRequests"),
            EffectiveModel = reviewData["baseModel"]?.GetValue<string>() ??
                status["review"]?["baseModel"]?.GetValue<string>() ?? file.EffectiveModel
        };
    }

    private async Task<AgentEnvelope> WaitForAsync(Guid jobId, string operationId, HashSet<string> terminal,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _deadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await _workflow.JobStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (!status.Success) return status;
            var data = status.Data!.AsObject();
            var state = Text(data, "state");
            var reachedTerminalJobState = terminal.Contains(state);
            var operation = await _workflow.LoadOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            var exactOperation = operation?.JobId == jobId &&
                string.Equals(operation.OperationId, operationId, StringComparison.Ordinal);
            if (exactOperation && string.Equals(operation!.State, "Failed", StringComparison.Ordinal))
            {
                data["operation"] = new JsonObject
                {
                    ["operationId"] = operation.OperationId,
                    ["state"] = operation.State,
                    ["errorCode"] = operation.ErrorCode,
                    ["retryable"] = operation.Retryable
                };
                return status;
            }
            if (exactOperation && string.Equals(operation!.State, "Completed", StringComparison.Ordinal) &&
                reachedTerminalJobState)
                return status;

            // The workflow persists the operation before launching its background work.  A terminal
            // job snapshot can therefore still be the checkpoint from before this exact operation
            // (notably Failed -> resume-missing-only).  Never attribute that stale terminal state to
            // an operation that is still active or whose terminal journal is not yet observable.
            await Task.Delay(_poll, cancellationToken).ConfigureAwait(false);
        }
        return new(AgentQueryService.ResponseSchema, false, "batch child wait", DateTimeOffset.UtcNow, null,
            new("WORKFLOW_BACKGROUND_STALLED", "The child workflow exceeded its durable deadline.", true));
    }

    private async Task<ReviewUsageSnapshot> GetReviewSnapshotAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var page = await _workflow.GetReviewAsync(new(jobId, 1, 1, false), cancellationToken).ConfigureAwait(false);
        if (!page.Success)
            return new(false, 0, 0, 0, null, page.Error?.Code ?? "BATCH_CHILD_REVIEW_FAILED", page.Error?.Retryable ?? false);
        var data = page.Data!.AsObject();
        var usage = data["usage"]?.AsObject();
        if (usage is null)
            return new(false, 0, 0, 0, null, "BATCH_CHILD_REVIEW_USAGE_MISSING", false);
        return new(true, Long(usage, "inputTokens"), Long(usage, "outputTokens"),
            Long(usage, "providerRequests"), data["baseModel"]?.GetValue<string>(), null, false);
    }

    private async Task<AgentBatchFileProgress> ResumePrepareAsync(AgentBatchManifestEntry file, ChildCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var terminal = await WaitForAsync(checkpoint.JobId, checkpoint.OperationId,
            new HashSet<string>(["ReviewRequired", "Failed", "Cancelled"], StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        if (!terminal.Success) return Failed(file, terminal.Error?.Code ?? "BATCH_CHILD_STATUS_FAILED", terminal.Error?.Retryable ?? false);
        var status = terminal.Data!.AsObject();
        if (Text(status, "state") != "ReviewRequired")
            return FailedFromStatus(file, status, "BATCH_CHILD_PREPARE_FAILED", checkpoint.OperationId) with
            {
                JobId = checkpoint.JobId,
                JobVersion = Long(status, "jobVersion"),
                PrepareOperationId = checkpoint.OperationId,
                ChildPrepareApprovalHash = checkpoint.ApprovalHash
            };
        var reviewSnapshot = await GetReviewSnapshotAsync(checkpoint.JobId, cancellationToken).ConfigureAwait(false);
        return reviewSnapshot.IsSuccess
            ? new(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Reviewing,
                JobId: checkpoint.JobId, JobVersion: Long(status, "jobVersion"),
                InputTokens: reviewSnapshot.InputTokens, OutputTokens: reviewSnapshot.OutputTokens,
                ProviderRequests: reviewSnapshot.ProviderRequests,
                PrepareOperationId: checkpoint.OperationId, ChildPrepareApprovalHash: checkpoint.ApprovalHash,
                EffectiveModel: reviewSnapshot.EffectiveModel)
            : Failed(file, reviewSnapshot.ErrorCode!, reviewSnapshot.Retryable);
    }

    private sealed record ReviewUsageSnapshot(bool IsSuccess, long InputTokens, long OutputTokens,
        long ProviderRequests, string? EffectiveModel, string? ErrorCode, bool Retryable);

    private async Task<AgentBatchFileProgress> ResumeGenerationAsync(AgentBatchFileProgress file, ChildCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var terminal = await WaitForAsync(checkpoint.JobId, checkpoint.OperationId,
            new HashSet<string>(["Completed", "Failed", "Cancelled"], StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        if (!terminal.Success) return Failure(file, terminal);
        var status = terminal.Data!.AsObject();
        if (Text(status, "state") != "Completed")
            return FailureFromStatus(file, status, "BATCH_CHILD_GENERATE_FAILED", checkpoint.OperationId);
        var validation = status["validation"]?.AsObject();
        return file with
        {
            State = AgentBatchFileState.Completed,
            ErrorCode = null,
            Retryable = false,
            JobVersion = Long(status, "jobVersion"),
            OutputHash = status["outputHash"]?.GetValue<string>(),
            GenerateOperationId = checkpoint.OperationId,
            OutputBytes = OutputBytes(file.OutputPath),
            ChildGenerationApprovalHash = checkpoint.ApprovalHash,
            ValidationPolicy = validation?["policy"]?.GetValue<string>(),
            VisualReviewPending = validation?["visualReviewPending"]?.GetValue<bool>() ?? false
        };
    }

    private void PersistBinding(AgentBatchPlan parent, AgentBatchManifestEntry file, string stage,
        string childPlanId, string approvalId, string authority)
    {
        var directory = Path.Combine(_evidenceRoot, parent.BatchId.ToString("D"));
        Directory.CreateDirectory(directory);
        var document = JsonSerializer.Serialize(new
        {
            schemaVersion = "dwg-agent-batch-child-binding/1.0",
            parent.BatchId,
            parent.ManifestHash,
            parent.Policy,
            parent.TerminologyVersion,
            file.RelativePath,
            file.Sha256,
            file.OutputPath,
            stage,
            childPlanIdHash = Hash(childPlanId),
            approvalHash = Hash(approvalId),
            authorityHash = Hash(authority)
        });
        var path = Path.Combine(directory, Hash(file.RelativePath + "|" + stage)[7..] + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, document);
        File.Move(temp, path, true);
    }

    private static long OutputBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private void SaveCheckpoint(AgentBatchPlan plan, AgentBatchManifestEntry file, string stage, Guid jobId,
        string operationId, string approvalHash)
    {
        var path = CheckpointPath(plan, file, stage);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new ChildCheckpoint(jobId, operationId, approvalHash)));
        File.Move(temp, path, true);
    }

    private void SaveCheckpoint(AgentBatchPlan plan, AgentBatchFileProgress file, string stage, Guid jobId,
        string operationId, string approvalHash) => SaveCheckpoint(plan,
        new AgentBatchManifestEntry(file.RelativePath, file.SourceHash, file.OutputPath, 0, file.SourceHash, default),
        stage, jobId, operationId, approvalHash);

    private ChildCheckpoint? LoadCheckpoint(AgentBatchPlan plan, AgentBatchManifestEntry file, string stage)
    {
        var path = CheckpointPath(plan, file, stage);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ChildCheckpoint>(File.ReadAllText(path)) : null; }
        catch (JsonException) { return null; }
    }

    private ChildCheckpoint? LoadCheckpoint(AgentBatchPlan plan, AgentBatchFileProgress file, string stage) =>
        LoadCheckpoint(plan, new AgentBatchManifestEntry(file.RelativePath, file.SourceHash, file.OutputPath, 0,
            file.SourceHash, default), stage);

    private string CheckpointPath(AgentBatchPlan plan, AgentBatchManifestEntry file, string stage) =>
        Path.Combine(_evidenceRoot, plan.BatchId.ToString("D"), Hash(file.RelativePath + "|checkpoint|" + stage)[7..] + ".checkpoint.json");

    private sealed record ChildCheckpoint(Guid JobId, string OperationId, string ApprovalHash);

    private static string Key(AgentBatchPlan parent, AgentBatchManifestEntry file, string stage) =>
        "batch-child-" + Hash($"{parent.BatchId:D}|{parent.ManifestHash}|{parent.Policy}|{file.Sha256}|{file.OutputPath}|{stage}")[7..];
    private static string Key(AgentBatchPlan parent, AgentBatchFileProgress file, string stage) =>
        "batch-child-" + Hash($"{parent.BatchId:D}|{parent.ManifestHash}|{parent.Policy}|{file.SourceHash}|{file.OutputPath}|{stage}")[7..];
    private static AgentChildBatchAuthority Authority(AgentBatchPlan parent, AgentBatchManifestEntry file) => new(
        parent.BatchId, parent.ManifestHash, parent.Policy, file.Sha256, file.OutputPath,
        parent.Files.ToList().FindIndex(candidate => candidate.RelativePath == file.RelativePath));
    private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(JsonObject value, string name) => value[name]?.GetValue<string>() ?? string.Empty;
    private static string? OptionalText(JsonObject value, string name) => value[name] is JsonValue node &&
        node.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
    private static bool ValidExternalAuthority(JsonObject? authority, AgentBatchPlan plan, string contextHash,
        IReadOnlyList<JsonObject> rows)
    {
        if (authority is null ||
            OptionalText(authority, "policyVersion") != ReviewAutomationPolicy.ContextualAgentCreateNew ||
            !Guid.TryParse(OptionalText(authority, "batchId"), out var batchId) || batchId != plan.BatchId ||
            OptionalText(authority, "manifestHash") != plan.ManifestHash ||
            OptionalText(authority, "contextHash") != contextHash ||
            !ContractPatterns.Sha256().IsMatch(OptionalText(authority, "manifestHash") ?? string.Empty) ||
            !ContractPatterns.Sha256().IsMatch(OptionalText(authority, "contextHash") ?? string.Empty) ||
            !ContractPatterns.Sha256().IsMatch(OptionalText(authority, "reviewerReportHash") ?? string.Empty) ||
            !ContractPatterns.Sha256().IsMatch(OptionalText(authority, "qaReportHash") ?? string.Empty))
            return false;
        var conflicts = rows.Where(row => (row["context"]?["signals"] as JsonArray)?
            .Any(node => node?.GetValue<string>() == SegmentContextResolutionPolicy.VerticalSignal) == true).ToArray();
        var resolutions = authority["segmentContextResolutions"] as JsonArray;
        if (conflicts.Length == 0) return resolutions is null or { Count: 0 };
        if (conflicts.Length != 1 || resolutions is not { Count: 1 } ||
            !TryContextResolution(resolutions[0], out var resolution)) return false;
        var row = conflicts[0];
        var metadata = row["context"] as JsonObject;
        return resolution!.ReviewerReportHash == OptionalText(authority, "reviewerReportHash") &&
            resolution.QaReportHash == OptionalText(authority, "qaReportHash") &&
            resolution.SegmentId == Text(row, "segmentId") &&
            resolution.SourceTextHash == OptionalText(metadata!, "sourceTextHash") &&
            resolution.ContextHash == OptionalText(metadata!, "contextHash") &&
            resolution.FinalTextHash == OptionalText(row, "finalHash");
    }

    private static bool ValidApprovedSupersedingTransition(
        AgentBatchPlan plan,
        AgentBatchFileProgress file,
        ReviewAutomationScopeTransition transition,
        ReviewAutomationReceipt receipt)
    {
        var manifests = plan.Files.Where(candidate =>
            string.Equals(candidate.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1) return false;
        var manifest = manifests[0];
        return transition.ExpectedScope.BatchId == receipt.BatchId &&
               transition.ExpectedScope.ManifestHash == receipt.ManifestHash &&
               transition.ExpectedScope.PolicyVersion == receipt.PolicyVersion &&
               transition.TargetScope.BatchId == plan.BatchId &&
               transition.TargetScope.ManifestHash == plan.ManifestHash &&
               transition.TargetScope.PolicyVersion == plan.Policy &&
               transition.ContextPolicyVersion == plan.ContextPolicyVersion &&
               transition.SourceHash == file.SourceHash && transition.SourceHash == manifest.Sha256 &&
               string.Equals(transition.SourcePath, manifest.SourcePath, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(transition.OutputPath, file.OutputPath, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(transition.OutputPath, manifest.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAuthorizedVerticalResolution(JsonObject? authority, JsonObject row)
    {
        var resolutions = authority?["segmentContextResolutions"] as JsonArray;
        return resolutions is { Count: 1 } && TryContextResolution(resolutions[0], out var resolution) &&
            resolution!.SegmentId == Text(row, "segmentId");
    }

    private static bool TryContextResolution(JsonNode? node, out SegmentContextResolutionAuthority? authority)
    {
        authority = null;
        try
        {
            authority = node?.Deserialize<SegmentContextResolutionAuthority>(ReceiptJson);
            return SegmentContextResolutionPolicy.IsStructurallyValid(authority);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReviewAutomationReceipt(JsonNode? node, out ReviewAutomationReceipt? receipt)
    {
        receipt = null;
        try
        {
            receipt = node?.Deserialize<ReviewAutomationReceipt>(ReceiptJson);
            return receipt is not null;
        }
        catch (JsonException) { return false; }
    }
    private static long Long(JsonObject value, string name)
    {
        if (value[name] is not JsonValue number) return 0;
        if (number.TryGetValue<long>(out var longValue)) return longValue;
        return number.TryGetValue<int>(out var intValue) ? intValue : 0;
    }
    private static (string Code, bool Retryable) FailureDetails(
        JsonObject status,
        string fallbackCode,
        string? operationId)
    {
        var lastError = status["lastError"] as JsonObject;
        if (lastError is not null)
            return (lastError["code"]?.GetValue<string>() ?? fallbackCode,
                lastError["retryable"]?.GetValue<bool>() ?? false);
        var operation = status["operation"] as JsonObject;
        if (operation is not null && operationId is not null &&
            string.Equals(Text(operation, "operationId"), operationId, StringComparison.Ordinal) &&
            string.Equals(Text(operation, "state"), "Failed", StringComparison.Ordinal))
            return (operation["errorCode"]?.GetValue<string>() ?? fallbackCode,
                operation["retryable"]?.GetValue<bool>() ?? false);
        return (fallbackCode, false);
    }
    private static AgentBatchFileProgress Failed(AgentBatchManifestEntry file, string code, bool retryable = false) =>
        new(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Failed, ErrorCode: code, Retryable: retryable);

    private static AgentBatchFileProgress FailedFromStatus(
        AgentBatchManifestEntry file,
        JsonObject status,
        string fallbackCode,
        string? operationId = null)
    {
        var failure = FailureDetails(status, fallbackCode, operationId);
        return Failed(
            file,
            failure.Code,
            failure.Retryable);
    }
    private static AgentBatchFileProgress Failure(AgentBatchFileProgress file, AgentEnvelope envelope) =>
        file with { State = AgentBatchFileState.Failed, ErrorCode = envelope.Error?.Code ?? "BATCH_CHILD_FAILED", Retryable = envelope.Error?.Retryable ?? false };
    private static AgentBatchFileProgress Failure(AgentBatchFileProgress file, string code) =>
        file with { State = AgentBatchFileState.Failed, ErrorCode = code, Retryable = false };
    private static AgentBatchFileProgress FailureFromStatus(
        AgentBatchFileProgress file,
        JsonObject status,
        string fallbackCode,
        string? operationId = null)
    {
        var failure = FailureDetails(status, fallbackCode, operationId);
        return file with
        {
            State = AgentBatchFileState.Failed,
            ErrorCode = failure.Code,
            Retryable = failure.Retryable,
            JobVersion = Long(status, "jobVersion"),
            GenerateOperationId = operationId ?? file.GenerateOperationId
        };
    }
}
