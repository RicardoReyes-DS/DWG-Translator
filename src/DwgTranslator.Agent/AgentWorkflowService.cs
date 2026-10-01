using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Agent;

public sealed class AgentWorkflowService
{
    private const int RecoveryLineageProjectionAttempts = 2;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly AgentBetaConfiguration _configuration;
    private readonly AgentDwgPathPolicy _paths;
    private readonly AgentWorkflowFileStore _files;
    private readonly AgentWorkflowOperationCoordinator _operationCoordinator;
    private readonly IAgentTranslationWorkflowBackend _backend;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<Func<Task>, Task> _schedule;
    private readonly AgentWorkflowSupervisionOptions _supervision;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly AgentCadLifecycleReceiptStore _cadReceipts;
    private readonly Func<int, bool> _processExists;
    private readonly Func<bool> _cadProcessExists;
    private readonly CadWriteLegacyRecoveryBinding _legacyCadWriteRecovery;
    internal CadBootstrapTrustFailureReconciliationPolicy BootstrapTrustPolicy { get; init; } =
        CadBootstrapTrustFailureReconciliationPolicy.Instance;

    public AgentWorkflowService(
        AgentBetaConfiguration configuration,
        IAgentTranslationWorkflowBackend backend,
        Func<DateTimeOffset>? utcNow = null,
        Func<Func<Task>, Task>? schedule = null,
        AgentWorkflowSupervisionOptions? supervision = null,
        Func<int, bool>? processExists = null,
        Func<bool>? cadProcessExists = null)
        : this(configuration, backend, CadWriteFailureReconciliationPolicy.AuthorizedLegacy,
            utcNow, schedule, supervision, processExists, cadProcessExists)
    {
    }

    internal AgentWorkflowService(
        AgentBetaConfiguration configuration,
        IAgentTranslationWorkflowBackend backend,
        CadWriteLegacyRecoveryBinding legacyCadWriteRecovery,
        Func<DateTimeOffset>? utcNow = null,
        Func<Func<Task>, Task>? schedule = null,
        AgentWorkflowSupervisionOptions? supervision = null,
        Func<int, bool>? processExists = null,
        Func<bool>? cadProcessExists = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _legacyCadWriteRecovery = legacyCadWriteRecovery ?? throw new ArgumentNullException(nameof(legacyCadWriteRecovery));
        _paths = new AgentDwgPathPolicy(configuration.AllowedDwgRoot, configuration.OutputDwgRoot,
            configuration.BatchExecutionEnabled ? configuration.BatchOutputRoots : null);
        _files = new AgentWorkflowFileStore(configuration.LogRoot);
        _cadReceipts = new AgentCadLifecycleReceiptStore(configuration.LogRoot);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _operationCoordinator = new AgentWorkflowOperationCoordinator(_files, _backend, _utcNow);
        _schedule = schedule ?? (work => Task.Run(work));
        _supervision = supervision ?? AgentWorkflowSupervisionOptions.Default;
        _processExists = processExists ?? ProcessExists;
        _cadProcessExists = cadProcessExists ?? CadProcessExists;
        if (!_supervision.IsValid) throw new ArgumentOutOfRangeException(nameof(supervision));
    }

    public async Task<int> ReconcileOrphanedOperationsAsync(CancellationToken cancellationToken)
    {
        var now = _utcNow();
        var reconciled = 0;
        foreach (var operation in await _files.LoadOperationsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (operation.State != "Running" || now - operation.UpdatedAtUtc <= _supervision.OperationDeadline) continue;
            var receipt = _cadReceipts.Load(operation.JobId);
            if (!string.IsNullOrWhiteSpace(receipt?.ResponseHash))
            {
                if (receipt.RequiresProcessTerminationApproval)
                    await _files.MarkCadBlockedAsync(receipt, cancellationToken).ConfigureAwait(false);
                var job = await _backend.LoadJobAsync(operation.JobId, cancellationToken).ConfigureAwait(false);
                if (job.IsSuccess && job.Value!.State is JobState.Extracted or JobState.Translating or JobState.ReviewRequired or JobState.Approved or JobState.Completed)
                {
                    if (await CompleteOperationAsync(operation, "ReconciledFromCadReceipt").ConfigureAwait(false)) reconciled++;
                    continue;
                }
                // A hash-only receipt proves CAD responded but intentionally cannot reconstruct drawing text.
                // Fail closed for a direct, human-authorized reconciliation instead of reopening CAD blindly.
                if (await FailOperationAsync(operation, "CAD_RECEIPT_RECONCILIATION_REQUIRED", false).ConfigureAwait(false)) reconciled++;
                continue;
            }
            if (await FailOperationAsync(operation, "WORKFLOW_BACKGROUND_STALLED", true).ConfigureAwait(false)) reconciled++;
        }
        return reconciled;
    }

    public Task<AgentEnvelope> CreateTranslationPlanAsync(
        AgentTranslationPlanRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ParentBatchAuthority is not null)
            return Task.FromResult(Failure("translation plan", "BATCH_CHILD_AUTHORITY_INTERNAL_ONLY",
                "Batch child authority is accepted only through the internal batch processor."));
        return CreateTranslationPlanCoreAsync(request, requireParentBatchAuthority: false, cancellationToken);
    }

    internal Task<AgentEnvelope> CreateBatchChildTranslationPlanAsync(
        AgentTranslationPlanRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ParentBatchAuthority is null)
            return Task.FromResult(Failure("translation plan", "BATCH_CHILD_AUTHORITY_REQUIRED",
                "The internal batch child plan requires durable parent batch authority."));
        return CreateTranslationPlanCoreAsync(request, requireParentBatchAuthority: true, cancellationToken);
    }

    private async Task<AgentEnvelope> CreateTranslationPlanCoreAsync(
        AgentTranslationPlanRequest request,
        bool requireParentBatchAuthority,
        CancellationToken cancellationToken)
    {
        const string command = "translation plan";
        if (!_configuration.OpenAiEnabled)
            return Failure(command, "OPENAI_EXECUTION_DISABLED", "Agent Beta external translation is disabled.");
        var source = _paths.ValidateSource(request.SourcePath);
        if (source.Error is not null) return Failure(command, source.Error);
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(source.Path!, cancellationToken).ConfigureAwait(false);
        if (snapshot.Error is not null) return Failure(command, snapshot.Error);
        var outputValue = string.IsNullOrWhiteSpace(request.OutputPath)
            ? _paths.DefaultOutput(source.Path!, request.TargetLanguage)
            : request.OutputPath!;
        var output = _paths.ValidateNewOutput(outputValue);
        if (output.Error is not null) return Failure(command, output.Error);
        if (!LanguageTag.Create(request.TargetLanguage).IsSuccess)
            return Failure(command, "TARGET_LANGUAGE_INVALID", "A supported BCP-47 target language is required.");
        var routing = CreateRouting(request.RoutingMode, request.ManualModel);
        if (routing.Error is not null) return Failure(command, routing.Error);
        var access = ModelAccess(routing.Policy!);
        if (access.Error is not null) return Failure(command, access.Error);

        var now = _utcNow();
        var binding = new JsonObject
        {
            ["sourcePath"] = source.Path,
            ["sourceHash"] = snapshot.Snapshot!.Hash,
            ["sourceBytes"] = snapshot.Snapshot.Bytes,
            ["sourceLastWriteTimeUtc"] = snapshot.Snapshot.LastWriteTimeUtc.ToString("O"),
            ["outputPath"] = output.Path,
            ["targetLanguage"] = request.TargetLanguage,
            ["routing"] = JsonSerializer.SerializeToNode(routing.Policy, Json),
            ["pathPolicyVersion"] = AgentWorkflowContract.PathPolicyVersion,
            ["routingPolicyVersion"] = TranslationRouting.PolicyVersion,
            ["promptTemplateVersion"] = _configuration.PromptTemplateVersion,
            ["contextPolicyVersion"] = CadSemanticContextBuilder.CurrentPolicyVersion,
            ["includeNeighborExcerpts"] = AgentWorkflowContract.TranslationIncludeNeighborExcerpts,
            ["reviewIncludeText"] = AgentWorkflowContract.TranslationReviewIncludeText,
            ["maximumNeighborExcerpts"] = TranslationBatchFactory.MaximumNeighborExcerpts,
            ["maximumNeighborExcerptScalars"] = TranslationBatchFactory.MaximumNeighborExcerptScalars,
            ["maximumNeighborExcerptScalarsPerSegment"] = TranslationBatchFactory.MaximumNeighborExcerptScalarsPerSegment,
            ["outboundPurpose"] = AgentWorkflowContract.TranslationOutboundPurpose,
            ["planKind"] = requireParentBatchAuthority ? "batch-child" : "direct"
        };
        if (request.ParentBatchAuthority is { } parent)
        {
            if (parent.BatchId == Guid.Empty || parent.FileIndex < 0 ||
                !string.Equals(parent.SourceHash, snapshot.Snapshot.Hash, StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath(parent.OutputPath), output.Path, StringComparison.Ordinal) ||
                !ContractPatterns.Sha256().IsMatch(parent.ManifestHash) || !AgentBatchPolicy.IsSupported(parent.PolicyVersion))
                return Failure(command, "BATCH_CHILD_AUTHORITY_INVALID", "The child authority must bind this exact batch snapshot, source and output.");
            binding["parentBatchAuthority"] = JsonSerializer.SerializeToNode(parent, Json);
        }
        var created = await _files.CreatePlanAsync("translation.prepare", binding, now,
            TimeSpan.FromMinutes(_configuration.ApprovalLifetimeMinutes), cancellationToken).ConfigureAwait(false);
        var consent = TranslationConsent(created.Document.PlanId);
        return Success(command, new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["planId"] = created.Document.PlanId,
            ["expiresAtUtc"] = created.Document.ExpiresAtUtc.ToString("O"),
            ["source"] = new JsonObject
            {
                ["path"] = source.Path,
                ["hash"] = snapshot.Snapshot.Hash,
                ["bytes"] = snapshot.Snapshot.Bytes,
                ["lastWriteTimeUtc"] = snapshot.Snapshot.LastWriteTimeUtc.ToString("O")
            },
            ["outputPath"] = output.Path,
            ["routing"] = JsonSerializer.SerializeToNode(routing.Policy, Json),
            ["translationContext"] = new JsonObject
            {
                ["promptTemplateVersion"] = _configuration.PromptTemplateVersion,
                ["contextPolicyVersion"] = CadSemanticContextBuilder.CurrentPolicyVersion,
                ["includeNeighborExcerpts"] = AgentWorkflowContract.TranslationIncludeNeighborExcerpts,
                ["reviewIncludeText"] = AgentWorkflowContract.TranslationReviewIncludeText,
                ["maximumNeighborExcerpts"] = TranslationBatchFactory.MaximumNeighborExcerpts,
                ["maximumNeighborExcerptScalars"] = TranslationBatchFactory.MaximumNeighborExcerptScalars,
                ["maximumNeighborExcerptScalarsPerSegment"] = TranslationBatchFactory.MaximumNeighborExcerptScalarsPerSegment,
                ["outboundPurpose"] = AgentWorkflowContract.TranslationOutboundPurpose
            },
            ["modelAccess"] = access.Data,
            ["stages"] = new JsonArray("Draft", "Inspecting", "Extracted", "Translating", "ReviewRequired"),
            ["approval"] = new JsonObject
            {
                ["approvalId"] = created.ApprovalId,
                ["consent"] = consent,
                ["purpose"] = AgentWorkflowContract.TranslationOutboundPurpose,
                ["singleUse"] = true
            },
            ["policies"] = PolicyVersions(),
            ["opensAutoCad"] = false,
            ["callsOpenAi"] = false,
            ["writesDwg"] = false
        });
    }

    public async Task<AgentEnvelope> PrepareAsync(
        AgentTranslationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        const string command = "translation prepare";
        if (!_configuration.OpenAiEnabled)
            return Failure(command, "OPENAI_EXECUTION_DISABLED", "Agent Beta external translation is disabled.");
        var requestNode = JsonSerializer.SerializeToNode(request, Json)!;
        var idempotency = await _files.BeginIdempotencyAsync(request.IdempotencyKey,
            AgentWorkflowFileStore.RequestHash(requestNode), cancellationToken).ConfigureAwait(false);
        var replay = Replay(command, idempotency);
        if (replay is not null) return replay;

        var loaded = await _files.LoadPlanAsync(request.PlanId, "translation.prepare", cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null) return Failure(command, loaded.Error);
        var plan = loaded.Plan!;
        var planBinding = ValidateTranslationPlanBinding(plan.Binding);
        if (planBinding is not null) return Failure(command, planBinding);
        if (!string.Equals(request.Consent, TranslationConsent(plan.PlanId), StringComparison.Ordinal))
            return Failure(command, "CONSENT_REQUIRED", "The exact plan-specific external-processing consent is required.");
        if (!string.Equals(request.SourceHash, StringValue(plan.Binding, "sourceHash"), StringComparison.Ordinal))
            return Failure(command, "SOURCE_HASH_MISMATCH", "The source hash does not match the approved plan.");
        var source = _paths.ValidateSource(StringValue(plan.Binding, "sourcePath"));
        var output = _paths.ValidateNewOutput(StringValue(plan.Binding, "outputPath"));
        if (source.Error is not null) return Failure(command, source.Error);
        if (output.Error is not null) return Failure(command, output.Error);
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(source.Path!, cancellationToken).ConfigureAwait(false);
        if (snapshot.Error is not null) return Failure(command, snapshot.Error);
        if (!string.Equals(snapshot.Snapshot!.Hash, request.SourceHash, StringComparison.Ordinal) ||
            snapshot.Snapshot.Bytes != plan.Binding["sourceBytes"]!.GetValue<long>() ||
            snapshot.Snapshot.LastWriteTimeUtc.ToString("O") != StringValue(plan.Binding, "sourceLastWriteTimeUtc"))
            return Failure(command, "SOURCE_HASH_MISMATCH", "The source changed after planning.");

        var consumed = await _files.ConsumePlanAsync(plan, request.ApprovalId, _utcNow(), cancellationToken).ConfigureAwait(false);
        if (consumed is not null) return Failure(command, consumed);
        var policy = plan.Binding["routing"]!.Deserialize<TranslationRoutingPolicy>(Json)!;
        var glossary = _configuration.BatchExecutionEnabled && string.Equals(StringValue(plan.Binding, "targetLanguage"), "en-US", StringComparison.Ordinal)
            ? ArchitecturalMepTerminologyPolicy.Glossary : null;
        ReviewAutomationScope? reviewAutomationScope = null;
        if (plan.Binding["parentBatchAuthority"] is { } authorityNode)
        {
            var authority = authorityNode.Deserialize<AgentChildBatchAuthority>(Json);
            if (authority is null)
                return Failure(command, "BATCH_CHILD_AUTHORITY_INVALID", "The durable child authority is invalid.");
            reviewAutomationScope = new ReviewAutomationScope(
                authority.BatchId,
                authority.ManifestHash,
                authority.PolicyVersion);
        }
        var specification = new DwgTranslationJobSpecification(
            source.Path!, output.Path!, request.SourceHash, null, StringValue(plan.Binding, "targetLanguage"),
            StringValue(plan.Binding, "promptTemplateVersion"), glossary, policy, reviewAutomationScope);
        var created = await _backend.CreateAsync(specification, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess) return Failure(command, created.Error!);

        var operation = NewOperation("translation.prepare", created.Value!.JobId, "Inspecting");
        await _files.SaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        _cadReceipts.BindOperation(operation.JobId, operation.OperationId);
        var response = new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["operationId"] = operation.OperationId,
            ["jobId"] = created.Value.JobId.ToString("D"),
            ["jobVersion"] = created.Value.Version,
            ["state"] = "Started",
            ["nextTool"] = "dwg_job_status",
            ["idempotentReplay"] = false
        };
        await _files.CompleteIdempotencyAsync(request.IdempotencyKey, response, cancellationToken).ConfigureAwait(false);
        LaunchPrepare(operation, snapshot.Snapshot!.Bytes);
        return Success(command, response);
    }

    /// <summary>Internal batch-only continuation. The domain workflow sends only incomplete durable batches.</summary>
    internal async Task<AgentEnvelope> ResumeTranslationMissingOnlyAsync(
        Guid jobId,
        long expectedJobVersion,
        ReviewAutomationScopeTransition scopeTransition,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string command = "translation resume missing only";
        if (!await WaitForOperationSlotAsync(jobId, cancellationToken).ConfigureAwait(false))
            return Failure(command, "JOB_OPERATION_CONFLICT",
                "The previous durable operation has not released the job execution slot.", true);
        var idempotency = await _files.BeginIdempotencyAsync(idempotencyKey,
            AgentWorkflowFileStore.RequestHash(new JsonObject
            {
                ["jobId"] = jobId.ToString("D"),
                ["expectedJobVersion"] = expectedJobVersion,
                ["mode"] = "missing-only",
                ["expectedBatchId"] = scopeTransition.ExpectedScope.BatchId.ToString("D"),
                ["expectedManifestHash"] = scopeTransition.ExpectedScope.ManifestHash,
                ["targetBatchId"] = scopeTransition.TargetScope.BatchId.ToString("D"),
                ["targetManifestHash"] = scopeTransition.TargetScope.ManifestHash,
                ["policyVersion"] = scopeTransition.TargetScope.PolicyVersion
            }), cancellationToken).ConfigureAwait(false);
        var replay = Replay(command, idempotency);
        if (replay is not null) return replay;
        var job = await _backend.LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!job.IsSuccess) return Failure(command, job.Error!);
        var failure = job.Value!.Data["failure"] as JsonObject;
        var failureCode = failure?["code"]?.GetValue<string>();
        var retryableTranslation = failure?["retryable"]?.GetValue<bool>() is true;
        var tokenIntegrity = string.Equals(failureCode, "TOKEN_INTEGRITY_FAILED", StringComparison.Ordinal);
        var failedCheckpoint = job.Value.State == JobState.Failed && job.Value.Version == expectedJobVersion &&
            string.Equals(failure?["stage"]?.GetValue<string>(), "Translation", StringComparison.Ordinal) &&
            (retryableTranslation || tokenIntegrity);
        var projectedCheckpoint = IsProjectedTranslationRecovery(job.Value, expectedJobVersion, scopeTransition);
        if (!failedCheckpoint && !projectedCheckpoint)
            return Failure(command, "BATCH_RECOVERY_EVIDENCE_CHANGED",
                "The exact failed translation checkpoint changed before missing-only recovery started.");
        var operation = NewOperation("translation.resume-missing-only", jobId, "Translating");
        await _files.SaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        var response = new JsonObject
        {
            ["operationId"] = operation.OperationId,
            ["jobId"] = jobId.ToString("D"),
            ["jobVersion"] = job.Value!.Version,
            ["state"] = "Started",
            ["missingOnly"] = true
        };
        await _files.CompleteIdempotencyAsync(idempotencyKey, response, cancellationToken).ConfigureAwait(false);
        LaunchResumeTranslation(operation, expectedJobVersion, scopeTransition);
        return Success(command, response);
    }

    private static bool IsProjectedTranslationRecovery(
        JobDocument job,
        long expectedFailedVersion,
        ReviewAutomationScopeTransition transition)
    {
        if (job.State != JobState.Translating || job.Version != expectedFailedVersion + 1 ||
            job.Data.ContainsKey("failure") || job.Data.ContainsKey("agentFailure")) return false;
        try
        {
            var data = job.Data.Deserialize<DwgTranslationJobData>(Json);
            var scope = data?.Specification.ReviewAutomationScope;
            return scope is not null && scope.BatchId == transition.TargetScope.BatchId &&
                string.Equals(scope.ManifestHash, transition.TargetScope.ManifestHash, StringComparison.Ordinal) &&
                string.Equals(scope.PolicyVersion, transition.TargetScope.PolicyVersion, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Internal recovery-only CAS. No HTTP route exposes this transition and it
    /// does not dispatch OpenAI or CAD work.
    /// </summary>
    internal async Task<AgentEnvelope> RebindReviewAutomationScopeAsync(
        Guid jobId,
        long expectedJobVersion,
        ReviewAutomationScopeTransition scopeTransition,
        CancellationToken cancellationToken)
    {
        const string command = "translation rebind recovery review scope";
        Result<JobDocument>? rebound = null;
        for (var attempt = 1; attempt <= RecoveryLineageProjectionAttempts; attempt++)
        {
            rebound = await _backend.RebindReviewAutomationScopeAsync(
                jobId, expectedJobVersion, scopeTransition, cancellationToken).ConfigureAwait(false);
            if (rebound.IsSuccess) break;
            if (attempt == RecoveryLineageProjectionAttempts ||
                !string.Equals(rebound.Error!.Code, "RECOVERY_LINEAGE_PROJECTION_INCOMPLETE", StringComparison.Ordinal))
                return Failure(command, rebound.Error!);
        }
        var job = rebound!.Value!;
        return Success(command, new JsonObject
        {
            ["jobId"] = job.JobId.ToString("D"),
            ["jobVersion"] = job.Version,
            ["state"] = job.State.ToString(),
            ["scopeRebound"] = job.Version != expectedJobVersion
        });
    }

    internal async Task<AgentEnvelope> RevalidateApprovedContextAsync(
        Guid jobId,
        long expectedJobVersion,
        ApprovedContextRevalidationTransition transition,
        CancellationToken cancellationToken)
    {
        const string command = "translation revalidate approved context";
        if (jobId == Guid.Empty || transition is null ||
            !ApprovedContextRevalidationPolicy.IsValid(transition.Binding) ||
            transition.Binding.ExpectedJobVersion != expectedJobVersion || _running.ContainsKey(jobId))
            return Failure(command, "RECOVERY_CONTEXT_REVALIDATION_BINDING_INVALID",
                "The approved-context recovery binding is invalid or active.");
        var source = _paths.ValidateSource(transition.SourcePath);
        var output = _paths.ValidateNewOutput(transition.OutputPath);
        if (source.Error is not null || output.Error is not null ||
            HasActiveCandidateOrStaging(transition.OutputPath, jobId) ||
            await _files.HasActiveOperationAsync(cancellationToken).ConfigureAwait(false) ||
            _files.IsLeaseActive("job:" + jobId.ToString("D")) || _files.IsLeaseActive("cad:global") ||
            _files.IsLeaseActive("output:" + transition.OutputPath) || _files.LoadCadBlocked() is not null)
            return Failure(command, "RECOVERY_CONTEXT_REVALIDATION_QUIESCENCE_REQUIRED",
                "Context revalidation requires an absent output/candidate and complete workflow quiescence.");
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(source.Path!, cancellationToken).ConfigureAwait(false);
        if (snapshot.Error is not null ||
            !string.Equals(snapshot.Snapshot!.Hash, transition.SourceHash, StringComparison.Ordinal))
            return Failure(command, "RECOVERY_CONTEXT_REVALIDATION_SOURCE_CHANGED",
                "The manifest-bound source changed before context revalidation.");
        var current = await _backend.LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess) return Failure(command, current.Error!);
        if (current.Value!.State == JobState.Approved && current.Value.Version == expectedJobVersion)
        {
            var jobArtifactHash = await ArtifactHashAsync(Path.Combine(_configuration.WorkspaceRoot,
                jobId.ToString("D"), "job.json"), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(jobArtifactHash, transition.Binding.ExpectedJobArtifactHash, StringComparison.Ordinal))
                return Failure(command, "RECOVERY_CONTEXT_REVALIDATION_JOB_ARTIFACT_CHANGED",
                    "The approved job artifact changed after the recovery binding was sealed.");
        }
        else if (current.Value.State != JobState.ReviewRequired ||
                 current.Value.Version != expectedJobVersion + 1)
            return Failure(command, "JOB_STATE_CONFLICT",
                "The job changed before approved-context revalidation.");

        Result<JobDocument>? result = null;
        for (var attempt = 1; attempt <= RecoveryLineageProjectionAttempts; attempt++)
        {
            result = await _backend.RevalidateApprovedContextAsync(jobId, expectedJobVersion, transition,
                cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess) break;
            if (attempt == RecoveryLineageProjectionAttempts || result.Error!.Code !=
                "RECOVERY_CONTEXT_REVALIDATION_PROJECTION_INCOMPLETE")
                return Failure(command, result.Error!);
        }
        return Success(command, new JsonObject
        {
            ["jobId"] = jobId.ToString("D"),
            ["jobVersion"] = result!.Value!.Version,
            ["state"] = result.Value.State.ToString(),
            ["contextPolicyVersion"] = transition.Binding.TargetContextPolicyVersion,
            ["contextHash"] = transition.Binding.TargetContextHash,
            ["withdrawnPreviousAuthority"] = true,
            ["requiresFreshReview"] = true,
            ["requiredSegmentContextResolutionCount"] = transition.Binding.RequiredResolutionSegmentIds.Count,
            ["opensAutoCad"] = false,
            ["callsOpenAi"] = false,
            ["writesDwg"] = false
        });
    }

    private async Task<bool> WaitForOperationSlotAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        while (_running.ContainsKey(jobId) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        return !_running.ContainsKey(jobId);
    }

    /// <summary>
    /// Internal recovery continuation for a failed generation. It is deliberately
    /// fail-closed: only complete, bounded invariant evidence that now passes the
    /// current floating-point policy can reopen the existing review payload.
    /// </summary>
    public async Task<AgentEnvelope> ReconcileFailedGeometryForReviewAsync(
        AgentFailedGeometryReviewReconciliationRequest request,
        CancellationToken cancellationToken)
    {
        const string command = "translation reconcile failed geometry for review";
        if (request.JobId == Guid.Empty || request.ExpectedJobVersion < 0 ||
            string.IsNullOrWhiteSpace(request.SourceHash) || string.IsNullOrWhiteSpace(request.OutputPath))
            return Failure(command, "BATCH_CHILD_REVIEW_NOT_READY", "The child recovery binding is invalid.");

        var loaded = await _backend.LoadJobAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Failure(command, loaded.Error!);
        var job = loaded.Value!;
        if (job.State == JobState.ReviewRequired && job.Version == request.ExpectedJobVersion + 1)
            return Success(command, ReconciledReviewResponse(job, idempotent: true));
        if (job.State != JobState.Failed || job.Version != request.ExpectedJobVersion)
            return Failure(command, "BATCH_CHILD_REVIEW_NOT_READY", "The child is not at the expected failed checkpoint.");

        // Failure metadata is an Agent envelope extension, not part of the core
        // DwgTranslationJobData schema. Keep it for evidence below, but never let
        // its presence make otherwise durable core payload unreadable.
        var coreData = job.Data.DeepClone().AsObject();
        coreData.Remove("failure");
        coreData.Remove("agentFailure");
        var data = DeserializeJobData(coreData);
        if (data.Error is not null || data.Data is null ||
            !string.Equals(data.Data.Specification.SourceHash, request.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(data.Data.Specification.OutputPath, request.OutputPath, StringComparison.Ordinal))
            return Failure(command, "BATCH_CHILD_REVIEW_NOT_READY", "The child source or output binding does not match the recovery entry.");

        var source = _paths.ValidateSource(data.Data.Specification.SourcePath);
        var output = _paths.ValidateNewOutput(data.Data.Specification.OutputPath);
        if (source.Error is not null || output.Error is not null)
            return Failure(command, "BATCH_CHILD_REVIEW_NOT_READY", "The child source or new output path is no longer safe.");
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(source.Path!, cancellationToken).ConfigureAwait(false);
        if (snapshot.Error is not null || snapshot.Snapshot is null ||
            !string.Equals(snapshot.Snapshot.Hash, request.SourceHash, StringComparison.Ordinal))
            return Failure(command, "BATCH_CHILD_REVIEW_NOT_READY", "The child source hash changed before recovery.");

        if (!AgentFailedGeometryRecoveryEvidence.IsTolerable(job.Data) || !await HasIntactReviewPayloadAsync(job, data.Data, cancellationToken).ConfigureAwait(false))
            return Failure(command, "BATCH_CHILD_REVIEW_NOT_READY", "The failed child lacks complete, policy-valid review or invariant evidence.");

        var reconciled = await _backend.ReconcileFailedGeometryForReviewAsync(request.JobId, request.ExpectedJobVersion, cancellationToken)
            .ConfigureAwait(false);
        if (!reconciled.IsSuccess) return Failure(command, reconciled.Error!);
        return Success(command, ReconciledReviewResponse(reconciled.Value!, idempotent: false));
    }

    public async Task<AgentEnvelope> GetReviewAsync(
        AgentTranslationReviewGetRequest request,
        CancellationToken cancellationToken)
    {
        const string command = "translation review get";
        if (request.JobId == Guid.Empty || request.Page < 1 || request.PageSize is < 1 or > 200)
            return Failure(command, "REVIEW_REQUEST_INVALID", "A valid job, page and page size are required.");
        var job = await _backend.LoadJobAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (!job.IsSuccess) return Failure(command, job.Error!);
        if (job.Value!.State is not (JobState.ReviewRequired or JobState.Approved or JobState.Writing or JobState.Validating or JobState.Completed))
            return Failure(command, "JOB_STATE_CONFLICT", "The job has no available review session.");
        var review = await _backend.LoadReviewAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess) return Failure(command, review.Error!);
        var jobData = DeserializeJobData(job.Value.Data);
        if (jobData.Error is not null) return Failure(command, jobData.Error);
        var segments = jobData.Data!.Segments!.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var traces = (review.Value!.RoutingSegments ?? []).ToDictionary(trace => trace.SegmentId, StringComparer.Ordinal);
        var rows = review.Value.Rows.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).Select(row =>
        {
            traces.TryGetValue(row.SegmentId, out var trace);
            segments.TryGetValue(row.SegmentId, out var segment);
            var semantic = segment?.SemanticContext;
            JsonObject? semanticValue = null;
            if (semantic is not null)
            {
                semanticValue = new JsonObject
                {
                    ["version"] = semantic.Version,
                    ["contextHash"] = semantic.ContextHash,
                    ["sourceTextHash"] = segment!.SourceTextHash,
                    ["semanticKey"] = semantic.SemanticKey,
                    ["sheetRole"] = semantic.SheetRole,
                    ["discipline"] = semantic.Discipline,
                    ["disciplineConflict"] = semantic.DisciplineConflict,
                    ["xBand"] = semantic.XBand,
                    ["yBand"] = semantic.YBand,
                    ["readingOrder"] = semantic.ReadingOrder,
                    ["signals"] = JsonSerializer.SerializeToNode(semantic.Signals, Json),
                    ["neighborhoodDigest"] = semantic.NeighborhoodDigest,
                    ["neighborCount"] = semantic.Neighbors.Count,
                    ["neighbors"] = JsonSerializer.SerializeToNode(semantic.Neighbors.Select(neighbor => new
                    {
                        neighbor.SegmentId,
                        neighbor.SourceTextHash,
                        neighbor.EntityType,
                        neighbor.Relation,
                        neighbor.DistanceBand,
                        neighbor.SameLayer
                    }).ToArray(), Json)
                };
                if (semantic.DisciplineResolution is not null)
                    semanticValue["disciplineResolution"] = semantic.DisciplineResolution;
            }
            var value = new JsonObject
            {
                ["segmentId"] = row.SegmentId,
                ["entityType"] = segment?.Entity.Type,
                ["state"] = row.State.ToString(),
                ["warningCode"] = row.WarningCode,
                ["riskCodes"] = JsonSerializer.SerializeToNode(trace?.EscalationReasonCodes ?? [], Json),
                ["riskSeverity"] = row.RiskSeverity,
                ["effectiveModel"] = row.EffectiveModel,
                ["escalated"] = row.Escalated,
                ["originalHash"] = AgentWorkflowFileStore.TextHash(row.OriginalText),
                ["proposalHash"] = AgentWorkflowFileStore.TextHash(row.ProposedText),
                ["finalHash"] = AgentWorkflowFileStore.TextHash(row.FinalText),
                ["context"] = semanticValue
            };
            if (request.IncludeText)
            {
                value["originalText"] = row.OriginalText;
                value["proposedText"] = row.ProposedText;
                value["finalText"] = row.FinalText;
                var context = value["context"] as JsonObject ?? new JsonObject();
                context["layer"] = segment?.Entity.Layer;
                context["layout"] = segment?.Entity.Layout;
                if (semantic is not null)
                {
                    context["neighborIds"] = JsonSerializer.SerializeToNode(
                        semantic.Neighbors.Select(neighbor => neighbor.SegmentId).ToArray(), Json);
                }
                value["context"] = context;
            }
            return value;
        }).ToArray();
        return Success(command, new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["jobId"] = request.JobId.ToString("D"),
            ["jobVersion"] = job.Value.Version,
            ["reviewVersion"] = review.Value.Version,
            ["contextPolicyVersion"] = review.Value.ContextPolicyVersion,
            ["contextHash"] = review.Value.ContextHash,
            ["automationReceipt"] = review.Value.ReviewAutomationReceipt is null ? null :
                JsonSerializer.SerializeToNode(review.Value.ReviewAutomationReceipt, Json),
            ["state"] = job.Value.State.ToString(),
            ["includeText"] = request.IncludeText,
            ["page"] = request.Page,
            ["pageSize"] = request.PageSize,
            ["total"] = review.Value.Rows.Count,
            ["highRiskCount"] = review.Value.Rows.Count(row => row.RiskSeverity == "high"),
            ["escalatedCount"] = review.Value.Rows.Count(row => row.Escalated),
            ["requestedMode"] = review.Value.RequestedMode,
            ["baseModel"] = review.Value.BaseModel,
            ["routingVersion"] = review.Value.RoutingVersion,
            ["usage"] = new JsonObject
            {
                ["inputTokens"] = review.Value.UsedInputTokens,
                ["outputTokens"] = review.Value.UsedOutputTokens,
                ["providerRequests"] = review.Value.UsedProviderRequests
            },
            ["calls"] = JsonSerializer.SerializeToNode((review.Value.RoutingCalls ?? []).Select(call => new
            {
                call.Tier,
                call.RequestedModel,
                call.EffectiveModel,
                call.Usage.InputTokens,
                call.Usage.OutputTokens,
                call.RequestId,
                call.LatencyMilliseconds,
                call.PromptVersion,
                call.SchemaVersion,
                call.Outcome,
                call.ErrorCode
            }).ToArray(), Json),
            ["rows"] = new JsonArray(rows)
        });
    }

    public async Task<AgentEnvelope> ApplyReviewAsync(
        AgentTranslationReviewApplyRequest request,
        CancellationToken cancellationToken)
    {
        const string command = "translation review apply";
        var idempotency = await _files.BeginIdempotencyAsync(request.IdempotencyKey,
            AgentWorkflowFileStore.RequestHash(JsonSerializer.SerializeToNode(request, Json)!), cancellationToken).ConfigureAwait(false);
        var replay = Replay(command, idempotency);
        if (replay is not null) return replay;
        var job = await _backend.LoadJobAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (!job.IsSuccess) return Failure(command, job.Error!);
        if (job.Value!.State != JobState.ReviewRequired || job.Value.Version != request.ExpectedJobVersion)
            return Failure(command, "JOB_STATE_CONFLICT", "The job state or version changed before review was applied.");
        var review = await _backend.LoadReviewAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess) return Failure(command, review.Error!);
        var data = DeserializeJobData(job.Value.Data);
        if (data.Error is not null) return Failure(command, data.Error);
        var reviewBinding = ValidateReviewBinding(request, review.Value!, data.Data!.Specification.ReviewAutomationScope,
            data.Data.Segments!);
        if (reviewBinding is not null) return Failure(command, reviewBinding);
        var decisions = BuildReviewDecisions(request, review.Value!, data.Data!.Segments!);
        if (decisions.Error is not null) return Failure(command, decisions.Error);
        if (data.Data.ApprovedContextRevalidationReceipt is { } contextMarker &&
            !HistoricalIncidentRecoveryPolicy.ValidFreshReviewDecisionSet(request.JobId,
                Path.GetFileName(data.Data.Specification.SourcePath), contextMarker,
                review.Value!, decisions.Decisions!, request.AutomationAuthority))
            return Failure(command, "RECOVERY_CONTEXT_REVIEW_DECISION_SET_INVALID",
                "The fresh context review has no authorized historical decision binding.");
        var approved = await _backend.ApproveAsync(request.JobId, decisions.Decisions!, review.Value!.Version, request.AutomationAuthority,
            cancellationToken).ConfigureAwait(false);
        if (!approved.IsSuccess) return Failure(command, approved.Error!);
        var response = new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["jobId"] = request.JobId.ToString("D"),
            ["jobVersion"] = approved.Value!.Version,
            ["state"] = approved.Value.State.ToString(),
            ["decisionCount"] = decisions.Decisions!.Count,
            ["bulkApproved"] = request.BulkApprove,
            ["reviewVersion"] = review.Value!.Version + 1,
            ["contextHash"] = review.Value.ContextHash,
            ["automationAuthorityPersisted"] = request.AutomationAuthority is not null,
            ["nextTool"] = "dwg_generation_plan"
        };
        await _files.CompleteIdempotencyAsync(request.IdempotencyKey, response, cancellationToken).ConfigureAwait(false);
        return Success(command, response);
    }

    /// <summary>
    /// Creates a read-only, evidence-bound authority to restore a failed write to
    /// its existing Approved checkpoint.  It intentionally cannot start any CAD
    /// or OpenAI work, and it never reconstructs a review decision set.
    /// </summary>
    public async Task<AgentEnvelope> CreateGenerationReconciliationPlanAsync(
        AgentGenerationReconciliationPlanRequest request,
        CancellationToken cancellationToken)
    {
        const string command = "generation reconciliation plan";
        var verified = await VerifyGenerationReconciliationAsync(request.JobId, null, cancellationToken).ConfigureAwait(false);
        if (verified.Error is not null) return Failure(command, verified.Error);
        var context = verified.Context!;
        var now = _utcNow();
        var binding = CreateReconciliationBinding(context);
        var created = await _files.CreatePlanAsync("generation.reconcile.apply", binding, now,
            TimeSpan.FromMinutes(_configuration.ApprovalLifetimeMinutes), cancellationToken).ConfigureAwait(false);
        var consent = GenerationReconciliationConsent(request.JobId, context.FailedJobVersion,
            StringValue(binding, "reconciliationBindingHash"));
        return Success(command, new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["reconciliationPlanId"] = created.Document.PlanId,
            ["expiresAtUtc"] = created.Document.ExpiresAtUtc.ToString("O"),
            ["jobId"] = request.JobId.ToString("D"),
            ["expectedJobVersion"] = context.FailedJobVersion,
            ["failureCode"] = context.FailureCode,
            ["source"] = new JsonObject { ["path"] = context.Source.Path, ["hash"] = context.Source.Hash, ["bytes"] = context.Source.Bytes },
            ["outputPath"] = context.OutputPath,
            ["review"] = new JsonObject { ["version"] = context.Review.Version, ["hash"] = context.ReviewHash, ["decisionCount"] = context.Review.Rows.Count },
            ["approval"] = new JsonObject { ["approvalId"] = created.ApprovalId, ["consent"] = consent, ["singleUse"] = true },
            ["opensAutoCad"] = false,
            ["callsOpenAi"] = false,
            ["writesDwg"] = false
        });
    }

    /// <summary>
    /// Consumes a single-use reconciliation plan and performs the optimistic
    /// Failed-to-Approved checkpoint transition.  The underlying backend repeats
    /// durable checkpoint validation under its own compare-and-swap boundary.
    /// </summary>
    public async Task<AgentEnvelope> ApplyGenerationReconciliationAsync(
        AgentGenerationReconciliationApplyRequest request,
        CancellationToken cancellationToken)
    {
        const string command = "generation reconciliation apply";
        var idempotency = await _files.BeginIdempotencyAsync(request.IdempotencyKey,
            AgentWorkflowFileStore.RequestHash(JsonSerializer.SerializeToNode(request, Json)!), cancellationToken).ConfigureAwait(false);
        if (idempotency.Kind == IdempotencyBeginKind.Completed)
        {
            var replayResponse = idempotency.Response!.DeepClone().AsObject();
            replayResponse["idempotentReplay"] = true;
            return Success(command, replayResponse);
        }
        if (idempotency.Kind == IdempotencyBeginKind.Conflict)
            return Failure(command, "IDEMPOTENCY_CONFLICT", "The idempotency key is invalid or bound to another request.");
        // Pending is intentionally re-entered for this one projection-safe endpoint.  It can
        // only finish the exact consumed plan/CAS marker and never authorizes another transition.
        var loaded = await _files.LoadPlanAsync(request.ReconciliationPlanId, "generation.reconcile.apply", cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null) return Failure(command, loaded.Error);
        var plan = loaded.Plan!;
        var jobId = Guid.TryParse(StringValue(plan.Binding, "jobId"), out var parsed) ? parsed : Guid.Empty;
        var planVersion = plan.Binding["jobVersion"]?.GetValue<long>() ?? -1;
        if (jobId == Guid.Empty || request.ExpectedJobVersion != planVersion)
            return Failure(command, "JOB_STATE_CONFLICT", "The requested job version differs from the reconciliation plan.");
        var expectedConsent = GenerationReconciliationConsent(jobId, planVersion, StringValue(plan.Binding, "reconciliationBindingHash"));
        if (!string.Equals(request.Consent, expectedConsent, StringComparison.Ordinal))
            return Failure(command, "APPROVAL_REQUIRED", "The exact job/version/evidence-bound reconciliation consent is required.");
        if (!AgentWorkflowFileStore.ApprovalMatches(plan, request.ApprovalId))
            return Failure(command, "APPROVAL_REQUIRED", "The exact opaque approval artifact is required.");
        var wasConsumed = plan.Consumed;

        var verified = await VerifyGenerationReconciliationAsync(jobId, plan.Binding, cancellationToken).ConfigureAwait(false);
        if (verified.Error is not null) return Failure(command, verified.Error);
        if (!plan.Consumed)
        {
            var consumed = await _files.ConsumePlanAsync(plan, request.ApprovalId, _utcNow(), cancellationToken).ConfigureAwait(false);
            if (consumed is not null && consumed.Code != "APPROVAL_REPLAYED") return Failure(command, consumed);
            if (consumed?.Code == "APPROVAL_REPLAYED")
            {
                var consumedPlan = await _files.LoadPlanAsync(request.ReconciliationPlanId,
                    "generation.reconcile.apply", cancellationToken).ConfigureAwait(false);
                if (consumedPlan.Error is not null || consumedPlan.Plan is null || !consumedPlan.Plan.Consumed ||
                    consumedPlan.Plan.ConsumedAtUtc is null ||
                    !string.Equals(CanonicalHash(plan.Binding), CanonicalHash(consumedPlan.Plan.Binding), StringComparison.Ordinal))
                    return Failure(command, "GENERATION_RECONCILIATION_BINDING_MISMATCH",
                        "The concurrently consumed reconciliation plan does not match the sealed binding.");
                plan = consumedPlan.Plan!;
                wasConsumed = true;
            }
        }
        else if (plan.ConsumedAtUtc is null)
            return Failure(command, "GENERATION_RECONCILIATION_BINDING_MISMATCH", "The consumed reconciliation plan lacks durable consumption evidence.");

        // Re-read every mutable artifact after the one-time plan mutation and immediately before
        // the backend CAS. A post-consume crash can safely enter here with the same or a fresh key.
        verified = await VerifyGenerationReconciliationAsync(jobId, plan.Binding, cancellationToken).ConfigureAwait(false);
        if (verified.Error is not null) return Failure(command, verified.Error);
        var context = verified.Context!;
        var authority = new AgentGenerationReconciliationAuthority(
            StringValue(plan.Binding, "reconciliationBindingHash"), context.FailureCode,
            context.FailureCategory, context.FailureRetryable, context.ApprovedCheckpointVersion,
            context.ApprovedCheckpointHash, context.Review.Version, context.ReviewHash,
            context.Review.Rows.Count, context.EvidenceHash, context.TechnicalStage, context.NativeErrorStatus,
            context.FailureStage);
        var reconciled = await _backend.ReconcileFailedGenerationToApprovedAsync(jobId, planVersion,
            authority, cancellationToken).ConfigureAwait(false);
        if (!reconciled.IsSuccess) return Failure(command, reconciled.Error!);
        var response = new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["jobId"] = jobId.ToString("D"),
            ["jobVersion"] = reconciled.Value!.Version,
            ["state"] = reconciled.Value.State.ToString(),
            ["restoredFrom"] = "ApprovedCheckpoint",
            ["nextTool"] = "dwg_generation_plan",
            ["idempotentReplay"] = wasConsumed || idempotency.Kind == IdempotencyBeginKind.Pending
        };
        await _files.CompleteIdempotencyAsync(request.IdempotencyKey, response, cancellationToken).ConfigureAwait(false);
        return Success(command, response);
    }

    public async Task<AgentEnvelope> CreateGenerationPlanAsync(
        AgentGenerationPlanRequest request,
        CancellationToken cancellationToken)
    {
        const string command = "generation plan";
        var planningStarted = Stopwatch.GetTimestamp();
        var job = await _backend.LoadJobAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (!job.IsSuccess) return Failure(command, job.Error!);
        if (job.Value!.State != JobState.Approved)
            return Failure(command, "JOB_STATE_CONFLICT", "Only an Approved job can be planned for generation.");
        var data = DeserializeJobData(job.Value.Data);
        if (data.Error is not null) return Failure(command, data.Error);
        var output = _paths.ValidateNewOutput(data.Data!.Specification.OutputPath);
        if (output.Error is not null) return Failure(command, output.Error);
        var source = _paths.ValidateSource(data.Data.Specification.SourcePath);
        if (source.Error is not null) return Failure(command, source.Error);
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(source.Path!, cancellationToken).ConfigureAwait(false);
        if (snapshot.Error is not null) return Failure(command, snapshot.Error);
        if (!string.Equals(snapshot.Snapshot!.Hash, data.Data.Specification.SourceHash, StringComparison.Ordinal))
            return Failure(command, "SOURCE_HASH_MISMATCH", "The source changed after translation approval.");
        var review = await _backend.LoadReviewAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess) return Failure(command, review.Error!);
        if (review.Value!.JobId != request.JobId || !HasCompleteApprovedReview(review.Value, data.Data.Segments!))
            return Failure(command, "GENERATION_REVIEW_INVALID", "Generation requires a complete approved review matching the extracted segment set.");
        var reviewFingerprint = TranslationReviewFingerprint.Create(review.Value);
        var now = _utcNow();
        var binding = new JsonObject
        {
            ["jobId"] = request.JobId.ToString("D"),
            ["jobVersion"] = job.Value.Version,
            ["reviewVersion"] = reviewFingerprint.Version,
            ["sourcePath"] = source.Path,
            ["sourceHash"] = snapshot.Snapshot.Hash,
            ["sourceBytes"] = snapshot.Snapshot.Bytes,
            ["reviewHash"] = reviewFingerprint.ReviewHash,
            ["contextHash"] = reviewFingerprint.ContextHash,
            ["reviewAutomationReceiptHash"] = reviewFingerprint.ReceiptHash,
            ["outputPath"] = output.Path,
            ["outputBindingHash"] = OutputBindingHash(request.JobId, job.Value.Version, output.Path!, reviewFingerprint.ReviewHash),
            ["validationPolicy"] = "VisualStrictV2"
        };
        var created = await _files.CreatePlanAsync("generation.execute", binding, now,
            TimeSpan.FromMinutes(_configuration.ApprovalLifetimeMinutes), cancellationToken).ConfigureAwait(false);
        var phrase = GenerationApproval(request.JobId, job.Value.Version, StringValue(binding, "outputBindingHash"));
        return Success(command, new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["generationPlanId"] = created.Document.PlanId,
            ["expiresAtUtc"] = created.Document.ExpiresAtUtc.ToString("O"),
            ["jobId"] = request.JobId.ToString("D"),
            ["jobVersion"] = job.Value.Version,
            ["sourceHash"] = snapshot.Snapshot.Hash,
            ["reviewHash"] = binding["reviewHash"]!.DeepClone(),
            ["outputPath"] = output.Path,
            ["outputBindingHash"] = binding["outputBindingHash"]!.DeepClone(),
            ["validationPolicy"] = "VisualStrictV2",
            ["planLatencyMilliseconds"] = Math.Max(0L,
                (long)Math.Ceiling(Stopwatch.GetElapsedTime(planningStarted).TotalMilliseconds)),
            ["approval"] = new JsonObject
            {
                ["approvalId"] = created.ApprovalId,
                ["phrase"] = phrase,
                ["singleUse"] = true
            },
            ["opensAutoCad"] = false,
            ["writesDwg"] = false
        });
    }

    public async Task<AgentEnvelope> GenerateAsync(AgentGenerateRequest request, CancellationToken cancellationToken)
    {
        const string command = "generation execute";
        var idempotency = await _files.BeginIdempotencyAsync(request.IdempotencyKey,
            AgentWorkflowFileStore.RequestHash(JsonSerializer.SerializeToNode(request, Json)!), cancellationToken).ConfigureAwait(false);
        var replay = Replay(command, idempotency);
        if (replay is not null) return replay;
        var loaded = await _files.LoadPlanAsync(request.GenerationPlanId, "generation.execute", cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null) return Failure(command, loaded.Error);
        var plan = loaded.Plan!;
        var jobId = Guid.Parse(StringValue(plan.Binding, "jobId"));
        var planVersion = plan.Binding["jobVersion"]!.GetValue<long>();
        var expectedPhrase = GenerationApproval(jobId, planVersion, StringValue(plan.Binding, "outputBindingHash"));
        if (!string.Equals(request.Approval, expectedPhrase, StringComparison.Ordinal))
            return Failure(command, "APPROVAL_REQUIRED", "The exact job/version/output-bound generation approval is required.");
        if (request.ExpectedJobVersion != planVersion)
            return Failure(command, "JOB_STATE_CONFLICT", "The requested job version differs from the generation plan.");
        var job = await _backend.LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!job.IsSuccess) return Failure(command, job.Error!);
        if (job.Value!.State != JobState.Approved || job.Value.Version != planVersion)
            return Failure(command, "JOB_STATE_CONFLICT", "The job state or version changed after generation planning.");
        var output = _paths.ValidateNewOutput(StringValue(plan.Binding, "outputPath"));
        if (output.Error is not null) return Failure(command, output.Error);
        var data = DeserializeJobData(job.Value.Data);
        if (data.Error is not null) return Failure(command, data.Error);
        var review = await _backend.LoadReviewAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess) return Failure(command, review.Error!);
        if (!MatchesGenerationReviewBinding(plan.Binding, jobId, review.Value!, data.Data!.Segments!))
            return Failure(command, "GENERATION_REVIEW_BINDING_MISMATCH", "The approved review, semantic context, or automation receipt changed after generation planning.");
        var expectedReview = TranslationReviewFingerprint.Create(review.Value!);
        var consumed = await _files.ConsumePlanAsync(plan, request.ApprovalId, _utcNow(), cancellationToken).ConfigureAwait(false);
        if (consumed is not null) return Failure(command, consumed);

        var operation = NewOperation("generation.execute", jobId, "GenerationQueued");
        await _files.SaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        var response = new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["operationId"] = operation.OperationId,
            ["jobId"] = jobId.ToString("D"),
            ["jobVersion"] = job.Value.Version,
            ["state"] = "Started",
            ["nextTool"] = "dwg_job_status"
        };
        await _files.CompleteIdempotencyAsync(request.IdempotencyKey, response, cancellationToken).ConfigureAwait(false);
        LaunchGenerate(operation, StringValue(plan.Binding, "sourcePath"), StringValue(plan.Binding, "sourceHash"),
            output.Path!, planVersion, expectedReview,
            plan.Binding["sourceBytes"]?.GetValue<long>() ?? new FileInfo(StringValue(plan.Binding, "sourcePath")).Length);
        return Success(command, response);
    }

    public async Task<AgentEnvelope> JobStatusAsync(Guid jobId, CancellationToken cancellationToken)
    {
        const string command = "workflow job status";
        var job = await _backend.LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!job.IsSuccess) return Failure(command, job.Error!);
        var operation = await _files.LoadLatestOperationAsync(jobId, cancellationToken).ConfigureAwait(false);
        var data = DeserializeJobData(job.Value!.Data);
        TranslationReviewSnapshot? review = null;
        if (job.Value.State is JobState.ReviewRequired or JobState.Approved or JobState.Writing or JobState.Validating or JobState.Completed)
        {
            var loadedReview = await _backend.LoadReviewAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (loadedReview.IsSuccess) review = loadedReview.Value;
        }
        return Success(command, AgentWorkflowJobProjection.Status(jobId, job.Value, operation, review, data.Data, Json));
    }

    internal Task<AgentWorkflowOperation?> LoadOperationAsync(
        string operationId,
        CancellationToken cancellationToken) =>
        _files.LoadOperationAsync(operationId, cancellationToken);

    public async Task<AgentEnvelope> JobNextActionAsync(Guid jobId, CancellationToken cancellationToken)
    {
        const string command = "workflow job next action";
        var job = await _backend.LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!job.IsSuccess) return Failure(command, job.Error!);
        return Success(command, AgentWorkflowJobProjection.NextAction(jobId, job.Value!));
    }

    public async Task<AgentEnvelope> CancelAsync(AgentWorkflowCancelRequest request, CancellationToken cancellationToken)
    {
        const string command = "workflow cancel";
        var idempotency = await _files.BeginIdempotencyAsync(request.IdempotencyKey,
            AgentWorkflowFileStore.RequestHash(JsonSerializer.SerializeToNode(request, Json)!), cancellationToken).ConfigureAwait(false);
        var replay = Replay(command, idempotency);
        if (replay is not null) return replay;
        var job = await _backend.LoadJobAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (!job.IsSuccess) return Failure(command, job.Error!);
        if (job.Value!.Version != request.ExpectedJobVersion ||
            job.Value.State is JobState.Writing or JobState.Validating or JobState.Completed or JobState.Failed or JobState.Cancelled)
            return Failure(command, "JOB_STATE_CONFLICT", "Cancellation is allowed only before Writing at the exact job version.");
        JsonObject response;
        if (_running.TryGetValue(request.JobId, out var running))
        {
            running.Cancel();
            response = new JsonObject
            {
                ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
                ["jobId"] = request.JobId.ToString("D"),
                ["cancellationRequested"] = true,
                ["state"] = job.Value.State.ToString()
            };
        }
        else
        {
            var cancelled = await _backend.CancelAsync(request.JobId, request.ExpectedJobVersion, cancellationToken).ConfigureAwait(false);
            if (!cancelled.IsSuccess) return Failure(command, cancelled.Error!);
            response = new JsonObject
            {
                ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
                ["jobId"] = request.JobId.ToString("D"),
                ["jobVersion"] = cancelled.Value!.Version,
                ["state"] = cancelled.Value.State.ToString()
            };
        }
        await _files.CompleteIdempotencyAsync(request.IdempotencyKey, response, cancellationToken).ConfigureAwait(false);
        return Success(command, response);
    }

    private void LaunchPrepare(AgentWorkflowOperation operation, long sourceBytes)
    {
        var source = new CancellationTokenSource();
        if (!_running.TryAdd(operation.JobId, source)) throw new InvalidOperationException("JOB_OPERATION_CONFLICT");
        _ = _schedule(async () =>
        {
            await using var lease = await _files.TryAcquireLeaseAsync("cad:global", CancellationToken.None).ConfigureAwait(false);
            if (lease is null)
            {
                var blocked = _files.LoadCadBlocked();
                await FailOperationAsync(operation, blocked is null ? "JOB_STATE_CONFLICT" : "CAD_PROCESS_EXIT_REQUIRED", true).ConfigureAwait(false);
                _running.TryRemove(operation.JobId, out _);
                source.Dispose();
                return;
            }
            try
            {
                var work = _backend.PrepareAsync(operation.JobId, source.Token);
                var cadDeadline = CadDeadlinePolicy.ForSourceBytes(_configuration, sourceBytes);
                var deadlineAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(
                    cadDeadline.TotalSeconds + _supervision.EffectiveCadCleanupGrace.TotalSeconds,
                    _supervision.OperationDeadline.TotalSeconds));
                while (!work.IsCompleted)
                {
                    if (DateTimeOffset.UtcNow >= deadlineAt)
                    {
                        source.Cancel();
                        // The CAD boundary owns its lease until its cooperative close completes.  Wait the
                        // independently-configured grace before making a terminal decision; never release it
                        // immediately on a deadline as the former runner did.
                        var settled = await Task.WhenAny(work, Task.Delay(_supervision.EffectiveCadCleanupGrace, CancellationToken.None)).ConfigureAwait(false);
                        if (settled == work)
                        {
                            var afterCleanup = await work.ConfigureAwait(false);
                            if (afterCleanup.IsSuccess)
                                await CompleteOrFailPrepareOperationAsync(operation, afterCleanup.Value!).ConfigureAwait(false);
                            else
                                await FailOperationAsync(operation, afterCleanup.Error!.Code, afterCleanup.Error.Retryable).ConfigureAwait(false);
                            return;
                        }
                        var receipt = _cadReceipts.Load(operation.JobId);
                        var code = receipt?.RequiresProcessTerminationApproval == true
                            ? "CAD_PROCESS_EXIT_REQUIRED"
                            : "WORKFLOW_BACKGROUND_STALLED";
                        if (receipt?.RequiresProcessTerminationApproval == true)
                            await _files.MarkCadBlockedAsync(receipt, CancellationToken.None).ConfigureAwait(false);
                        await FailOperationAsync(operation, code, true).ConfigureAwait(false);
                        return;
                    }
                    await Task.WhenAny(work, Task.Delay(_supervision.HeartbeatInterval, source.Token)).ConfigureAwait(false);
                    if (!work.IsCompleted)
                        await HeartbeatOperationAsync(operation).ConfigureAwait(false);
                }
                var result = await work.ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    var receipt = _cadReceipts.Load(operation.JobId);
                    if (receipt?.RequiresProcessTerminationApproval == true)
                        await _files.MarkCadBlockedAsync(receipt, CancellationToken.None).ConfigureAwait(false);
                    await CompleteOrFailPrepareOperationAsync(operation, result.Value!).ConfigureAwait(false);
                }
                else
                    await FailOperationAsync(operation, result.Error!.Code, result.Error.Retryable).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await FailOperationAsync(operation, "WORKFLOW_CANCELLED", false).ConfigureAwait(false);
            }
            catch
            {
                await FailOperationAsync(operation, "WORKFLOW_UNEXPECTED_FAILURE", false).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(operation.JobId, out _);
                source.Dispose();
            }
        });
    }

    private void LaunchGenerate(AgentWorkflowOperation operation, string sourcePath, string expectedHash, string outputPath,
        long expectedJobVersion, TranslationReviewFingerprint expectedReview, long sourceBytes)
    {
        var source = new CancellationTokenSource();
        if (!_running.TryAdd(operation.JobId, source)) throw new InvalidOperationException("JOB_OPERATION_CONFLICT");
        _ = _schedule(async () =>
        {
            await using var jobLease = await _files.TryAcquireLeaseAsync("job:" + operation.JobId.ToString("D"), CancellationToken.None).ConfigureAwait(false);
            await using var cadLease = await _files.TryAcquireLeaseAsync("cad:global", CancellationToken.None).ConfigureAwait(false);
            await using var outputLease = await _files.TryAcquireLeaseAsync("output:" + outputPath, CancellationToken.None).ConfigureAwait(false);
            if (jobLease is null || cadLease is null || outputLease is null)
            {
                await FailOperationAsync(operation, "JOB_STATE_CONFLICT", true).ConfigureAwait(false);
                _running.TryRemove(operation.JobId, out _);
                source.Dispose();
                return;
            }
            try
            {
                var cadDeadline = CadDeadlinePolicy.ForSourceBytes(_configuration, sourceBytes);
                var totalDeadline = TimeSpan.FromSeconds(Math.Max(
                    cadDeadline.TotalSeconds + _supervision.EffectiveCadCleanupGrace.TotalSeconds,
                    _supervision.OperationDeadline.TotalSeconds));
                var deadlineStarted = Stopwatch.GetTimestamp();
                var deadlineAtUtc = _utcNow().ToUniversalTime().Add(totalDeadline);
                var tracked = await UpdateRunningOperationAsync(operation, "GenerationPreflight",
                    deadlineAtUtc).ConfigureAwait(false);
                var job = await _backend.LoadJobAsync(operation.JobId, source.Token).ConfigureAwait(false);
                if (!job.IsSuccess || job.Value!.State != JobState.Approved || job.Value.Version != expectedJobVersion)
                {
                    await FailOperationAsync(tracked, job.Error?.Code ?? "JOB_STATE_CONFLICT",
                        job.Error?.Retryable ?? false).ConfigureAwait(false);
                    return;
                }
                var data = DeserializeJobData(job.Value.Data);
                if (data.Error is not null)
                {
                    await FailOperationAsync(tracked, data.Error.Code, data.Error.Retryable).ConfigureAwait(false);
                    return;
                }
                tracked = await UpdateRunningOperationAsync(tracked, "GenerationReviewBinding").ConfigureAwait(false);
                var review = await _backend.LoadReviewAsync(operation.JobId, source.Token).ConfigureAwait(false);
                if (!review.IsSuccess || review.Value!.JobId != operation.JobId ||
                    !HasCompleteApprovedReview(review.Value, data.Data!.Segments!) ||
                    TranslationReviewFingerprint.Create(review.Value) != expectedReview)
                {
                    await FailOperationAsync(tracked, review.Error?.Code ?? "GENERATION_REVIEW_BINDING_MISMATCH",
                        review.Error?.Retryable ?? false).ConfigureAwait(false);
                    return;
                }
                tracked = await UpdateRunningOperationAsync(tracked, "GenerationSourceSnapshot").ConfigureAwait(false);
                var before = await AgentDwgPathPolicy.SnapshotAsync(sourcePath, source.Token).ConfigureAwait(false);
                if (before.Error is not null || !string.Equals(before.Snapshot!.Hash, expectedHash, StringComparison.Ordinal))
                {
                    await FailOperationAsync(tracked, "SOURCE_CHANGED", false).ConfigureAwait(false);
                    return;
                }
                var output = _paths.ValidateNewOutput(outputPath);
                if (output.Error is not null)
                {
                    await FailOperationAsync(tracked, output.Error.Code, output.Error.Retryable).ConfigureAwait(false);
                    return;
                }
                tracked = await UpdateRunningOperationAsync(tracked, "GenerationWriting").ConfigureAwait(false);
                var work = _backend.GenerateAsync(operation.JobId, expectedReview, source.Token);
                while (!work.IsCompleted && Stopwatch.GetElapsedTime(deadlineStarted) < totalDeadline)
                {
                    await Task.WhenAny(work, Task.Delay(_supervision.HeartbeatInterval, source.Token)).ConfigureAwait(false);
                    if (!work.IsCompleted)
                    {
                        var heartbeatAt = _utcNow().ToUniversalTime();
                        tracked = tracked with
                        {
                            UpdatedAtUtc = heartbeatAt,
                            LastHeartbeatAtUtc = heartbeatAt
                        };
                        await _files.TryTransitionOperationAsync(tracked, "Running", CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                if (!work.IsCompleted)
                {
                    source.Cancel();
                    tracked = await UpdateRunningOperationAsync(tracked, "GenerationCleanup",
                        cleanupOutcome: "CANCELLATION_REQUESTED").ConfigureAwait(false);
                    var settled = await Task.WhenAny(work,
                        Task.Delay(_supervision.EffectiveCadCleanupGrace, CancellationToken.None)).ConfigureAwait(false);
                    if (settled != work)
                    {
                        var stalledReceipt = _cadReceipts.Load(operation.JobId);
                        var requiresApproval = stalledReceipt?.OperationId == operation.OperationId &&
                                               stalledReceipt.RequiresProcessTerminationApproval;
                        if (requiresApproval)
                            await _files.MarkCadBlockedAsync(stalledReceipt!, CancellationToken.None).ConfigureAwait(false);
                        var candidatePath = CadWriteRequestFactory.CandidatePathFor(outputPath, operation.JobId);
                        var hasPromotionEvidence = _cadProcessExists() || File.Exists(candidatePath) ||
                                                   File.Exists(outputPath);
                        tracked = await UpdateRunningOperationAsync(tracked, "GenerationCleanup",
                            cleanupOutcome: requiresApproval
                                ? "TERMINATION_APPROVAL_REQUIRED"
                                : hasPromotionEvidence
                                    ? "AWAITING_CAD_PROMOTION_BOUNDARY"
                                    : "AWAITING_BACKEND_BOUNDARY").ConfigureAwait(false);
                        // An incomplete managed generation task is itself authority that the writer
                        // may still cross candidate -> final, even if process and both paths are
                        // momentarily absent during an atomic rename. Never publish a terminal from
                        // non-atomic observations. Retain all leases/fences and observe the real
                        // backend boundary below.
                    }
                }
                var result = await work.ConfigureAwait(false);
                var receipt = _cadReceipts.Load(operation.JobId);
                var cleanupOutcome = receipt?.OperationId == operation.OperationId
                    ? receipt.CleanupOutcome ?? (receipt.RequiresProcessTerminationApproval
                        ? "TERMINATION_APPROVAL_REQUIRED" : "CAD_CLEANUP_UNRECORDED")
                    : "NO_GENERATION_CAD_RECEIPT";
                if (receipt?.OperationId == operation.OperationId && receipt.RequiresProcessTerminationApproval)
                    await _files.MarkCadBlockedAsync(receipt, CancellationToken.None).ConfigureAwait(false);
                tracked = await UpdateRunningOperationAsync(tracked, "GenerationCleanup",
                    cleanupOutcome: cleanupOutcome).ConfigureAwait(false);
                var after = await AgentDwgPathPolicy.SnapshotAsync(sourcePath, CancellationToken.None).ConfigureAwait(false);
                if (!result.IsSuccess)
                    await FailOperationAsync(tracked, result.Error!.Code, result.Error.Retryable,
                        result.Error.Category, result.Error.TechnicalStage, result.Error.NativeErrorStatus).ConfigureAwait(false);
                else if (after.Error is not null || after.Snapshot!.Hash != expectedHash)
                    await FailOperationAsync(tracked, "SOURCE_CHANGED", false).ConfigureAwait(false);
                else
                    await CompleteOrFailGenerationOperationAsync(tracked, result.Value!).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var receipt = _cadReceipts.Load(operation.JobId);
                var cleanup = receipt?.OperationId == operation.OperationId
                    ? receipt.CleanupOutcome ?? "CAD_CLEANUP_UNRECORDED"
                    : "NO_GENERATION_CAD_RECEIPT";
                var tracked = await UpdateRunningOperationAsync(operation, "GenerationCleanup",
                    cleanupOutcome: cleanup).ConfigureAwait(false);
                await FailOperationAsync(tracked, "CAD_WRITE_CANCELLED_AT_SAFE_BOUNDARY", true,
                    ErrorCategory.Transport).ConfigureAwait(false);
            }
            catch
            {
                await FailOperationAsync(operation, "WORKFLOW_UNEXPECTED_FAILURE", false).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(operation.JobId, out _);
                source.Dispose();
            }
        });
    }

    private void LaunchResumeTranslation(
        AgentWorkflowOperation operation,
        long expectedJobVersion,
        ReviewAutomationScopeTransition scopeTransition)
    {
        var source = new CancellationTokenSource();
        if (!_running.TryAdd(operation.JobId, source)) throw new InvalidOperationException("JOB_OPERATION_CONFLICT");
        _ = _schedule(async () =>
        {
            try
            {
                var deadline = DateTimeOffset.UtcNow + _supervision.OperationDeadline;
                for (var attempt = 1; attempt <= RecoveryLineageProjectionAttempts; attempt++)
                {
                    var work = _backend.ResumeTranslationAsync(
                        operation.JobId, expectedJobVersion, scopeTransition, source.Token);
                    while (!work.IsCompleted && DateTimeOffset.UtcNow < deadline)
                    {
                        await Task.WhenAny(work, Task.Delay(_supervision.HeartbeatInterval, source.Token)).ConfigureAwait(false);
                        if (!work.IsCompleted) await HeartbeatOperationAsync(operation).ConfigureAwait(false);
                    }
                    if (!work.IsCompleted)
                    {
                        source.Cancel();
                        await FailOperationAsync(operation, "OPENAI_TIMEOUT", true).ConfigureAwait(false);
                        return;
                    }
                    var result = await work.ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        await CompleteOperationAsync(operation, result.Value!.State.ToString()).ConfigureAwait(false);
                        return;
                    }
                    if (attempt < RecoveryLineageProjectionAttempts &&
                        string.Equals(result.Error!.Code, "RECOVERY_LINEAGE_PROJECTION_INCOMPLETE", StringComparison.Ordinal))
                    {
                        await HeartbeatOperationAsync(operation).ConfigureAwait(false);
                        continue;
                    }
                    await FailOperationAsync(operation, result.Error!.Code, result.Error.Retryable,
                        result.Error.Category, result.Error.TechnicalStage, result.Error.NativeErrorStatus).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) { await FailOperationAsync(operation, "WORKFLOW_CANCELLED", false).ConfigureAwait(false); }
            catch { await FailOperationAsync(operation, "WORKFLOW_UNEXPECTED_FAILURE", false).ConfigureAwait(false); }
            finally
            {
                _running.TryRemove(operation.JobId, out _);
                source.Dispose();
            }
        });
    }

    private Task<bool> HeartbeatOperationAsync(AgentWorkflowOperation operation) =>
        _operationCoordinator.HeartbeatAsync(operation);

    private Task<AgentWorkflowOperation> UpdateRunningOperationAsync(
        AgentWorkflowOperation operation,
        string stage,
        DateTimeOffset? deadlineAtUtc = null,
        string? cleanupOutcome = null) =>
        _operationCoordinator.UpdateRunningAsync(operation, stage, deadlineAtUtc, cleanupOutcome);

    private Task<bool> CompleteOperationAsync(AgentWorkflowOperation operation, string stage) =>
        _operationCoordinator.CompleteAsync(operation, stage);

    private Task<bool> CompleteOrFailPrepareOperationAsync(AgentWorkflowOperation operation, JobDocument job)
    {
        if (job.State != JobState.Failed)
            return CompleteOperationAsync(operation, job.State.ToString());

        var failure = DurableFailure(job.Data);
        return FailOperationAsync(operation, failure.Code, failure.Retryable, failure.Category,
            failure.TechnicalStage, failure.NativeErrorStatus);
    }

    private Task<bool> CompleteOrFailGenerationOperationAsync(AgentWorkflowOperation operation, JobDocument job) =>
        CompleteOrFailPrepareOperationAsync(operation, job);

    private static ContractError DurableFailure(JsonObject data)
    {
        foreach (var name in new[] { "failure", "agentFailure" })
        {
            if (data[name] is not JsonObject failure ||
                failure["code"] is not JsonValue codeValue ||
                !codeValue.TryGetValue<string>(out var code) || string.IsNullOrWhiteSpace(code))
                continue;
            var retryable = failure["retryable"] is JsonValue retryableValue &&
                            retryableValue.TryGetValue<bool>(out var value) && value;
            return new ContractError(code,
                ParseCategory(failure["category"]?.GetValue<string>(), ErrorCategory.Internal),
                "The durable workflow operation failed.", retryable,
                DiagnosticId: NullableStringValue(failure, "diagnosticId"),
                TechnicalStage: NullableStringValue(failure, "technicalStage"),
                NativeErrorStatus: NullableStringValue(failure, "nativeErrorStatus"));
        }
        return new("WORKFLOW_UNEXPECTED_FAILURE", ErrorCategory.Internal,
            "The durable workflow operation failed unexpectedly.", false);
    }

    private Task<bool> FailOperationAsync(
        AgentWorkflowOperation operation,
        string code,
        bool retryable,
        ErrorCategory? category = null,
        string? technicalStage = null,
        string? nativeErrorStatus = null) =>
        _operationCoordinator.FailAsync(operation, code, retryable, category, technicalStage, nativeErrorStatus);

    private AgentWorkflowOperation NewOperation(string kind, Guid jobId, string stage) =>
        _operationCoordinator.New(kind, jobId, stage);

    private static (TranslationRoutingPolicy? Policy, AgentError? Error) CreateRouting(string mode, string? manualModel)
    {
        TranslationRoutingPolicy? policy = mode switch
        {
            TranslationRouting.Auto => TranslationRoutingPolicyFactory.Auto(),
            TranslationRouting.Economy => TranslationRoutingPolicyFactory.Economy(),
            TranslationRouting.MaximumQuality => TranslationRoutingPolicyFactory.MaximumQuality(),
            TranslationRouting.Manual when !string.IsNullOrWhiteSpace(manualModel) => TranslationRoutingPolicyFactory.Manual(manualModel),
            _ => null
        };
        if (policy is null || !TranslationRoutingPolicyFactory.Validate(policy).IsSuccess)
            return (null, new("ROUTING_MODE_INVALID", "Use Auto, Economy, MaximumQuality, or Manual with an exact model."));
        return (policy, null);
    }

    private (JsonObject? Data, AgentError? Error) ModelAccess(TranslationRoutingPolicy policy)
    {
        var configured = (_configuration.ConfiguredAccessibleModels ?? []).ToHashSet(StringComparer.Ordinal);
        if (!configured.Contains(policy.BaseModel))
            return (null, new("MODEL_ACCESS_DENIED", "The exact base model is not in the account-access allowlist; no tier substitution was made."));
        return (new JsonObject
        {
            ["baseModel"] = policy.BaseModel,
            ["baseAccessible"] = true,
            ["escalationModel"] = policy.EscalationModel,
            ["escalationAccessible"] = policy.EscalationModel is null ? null : configured.Contains(policy.EscalationModel),
            ["source"] = "configured-account-access-cache",
            ["noSilentSubstitution"] = true
        }, null);
    }

    private static (IReadOnlyList<ReviewDecisionInput>? Decisions, AgentError? Error) BuildReviewDecisions(
        AgentTranslationReviewApplyRequest request,
        TranslationReviewSnapshot review,
        IReadOnlyList<CadTextSegment> segments)
    {
        var segmentsById = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        if (request.BulkApprove)
        {
            if (review.Rows.Any(row => row.RiskSeverity == "high"))
                return (null, new("REVIEW_HIGH_RISK_EXPLICIT_REQUIRED", "High-risk segments require explicit per-segment decisions."));
            if (!string.Equals(request.BulkApproval, BulkApproval(request.JobId, request.ExpectedJobVersion), StringComparison.Ordinal))
                return (null, new("APPROVAL_REQUIRED", "The exact job/version-bound bulk-review approval is required."));
            return (review.Rows.Select(row => new ReviewDecisionInput(row.SegmentId, row.ProposedText, null)).ToArray(), null);
        }
        if (request.Decisions is null || request.Decisions.Count != review.Rows.Count ||
            request.Decisions.Select(item => item.SegmentId).Distinct(StringComparer.Ordinal).Count() != request.Decisions.Count)
            return (null, new("REVIEW_DECISION_SET_MISMATCH", "One explicit unique decision is required for every segment."));
        var supplied = request.Decisions.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        var result = new List<ReviewDecisionInput>(review.Rows.Count);
        foreach (var row in review.Rows)
        {
            if (!supplied.TryGetValue(row.SegmentId, out var decision) || !segmentsById.TryGetValue(row.SegmentId, out var segment))
                return (null, new("REVIEW_DECISION_SET_MISMATCH", "Review decisions must match the extracted segment set exactly."));
            switch (decision.Action)
            {
                case "approve":
                    if (!HumanReviewPolicy.Approve(segment, new AcceptedTranslation(row.SegmentId, row.ProposedText), row.ProposedText).IsSuccess)
                        return (null, new("REVIEW_INVARIANT_FAILED", "An approved proposal failed CAD text invariants."));
                    result.Add(new(row.SegmentId, row.ProposedText, null));
                    break;
                case "edit":
                    if (string.IsNullOrEmpty(decision.EditedText))
                        return (null, new("REVIEW_EDIT_INVALID", $"Segment {row.SegmentId} failed invariant EDITED_TEXT_REQUIRED."));
                    var edited = HumanReviewPolicy.Approve(
                        segment,
                        new AcceptedTranslation(row.SegmentId, row.ProposedText),
                        decision.EditedText);
                    if (!edited.IsSuccess)
                        return (null, new("REVIEW_EDIT_INVALID", $"Segment {row.SegmentId} failed invariant {edited.Error!.Code}."));
                    result.Add(new(row.SegmentId, decision.EditedText, null));
                    break;
                case "exclude":
                    if (string.IsNullOrWhiteSpace(decision.ExclusionReason) || !HumanReviewPolicy.Exclude(segment, decision.ExclusionReason).IsSuccess)
                        return (null, new("REVIEW_EXCLUSION_INVALID", "An exclusion requires a valid reason."));
                    result.Add(new(row.SegmentId, null, decision.ExclusionReason));
                    break;
                default:
                    return (null, new("REVIEW_ACTION_INVALID", "Review actions are approve, edit, or exclude."));
            }
        }
        return (result, null);
    }

    private static AgentError? ValidateReviewBinding(
        AgentTranslationReviewApplyRequest request,
        TranslationReviewSnapshot review,
        ReviewAutomationScope? scope,
        IReadOnlyList<CadTextSegment> segments)
    {
        if (review.ContextHash is null && review.ContextPolicyVersion is null)
            return request.ExpectedReviewVersion is null && request.ExpectedContextHash is null && request.AutomationAuthority is null
                ? null
                : new("REVIEW_CONTEXT_NOT_AVAILABLE", "Legacy review snapshots cannot accept contextual authority fields.");
        if (review.ContextHash is null || review.ContextPolicyVersion is null ||
            request.ExpectedReviewVersion != review.Version)
            return new("REVIEW_VERSION_CONFLICT", "The review version or contextual metadata changed before approval.");
        if (!string.Equals(request.ExpectedContextHash, review.ContextHash, StringComparison.Ordinal))
            return new("REVIEW_CONTEXT_MISMATCH", "The expected semantic context hash does not match the durable review.");
        var authority = request.AutomationAuthority;
        if (scope?.PolicyVersion is null or AgentBatchPolicy.LegacyPolicyVersion)
            return authority is null ? null :
                new("REVIEW_AUTOMATION_AUTHORITY_INVALID", "Human and legacy batch review cannot attach contextual automation authority.");
        if (scope.PolicyVersion == ReviewAutomationPolicy.ContextualAgentCreateNew && request.BulkApprove)
            return new("CONTEXTUAL_REVIEW_BULK_NOT_ALLOWED", "Contextual automated review requires one explicit decision per segment.");
        if (scope.PolicyVersion != ReviewAutomationPolicy.ContextualAgentCreateNew || authority is null ||
            authority.PolicyVersion != ReviewAutomationPolicy.ContextualAgentCreateNew || authority.BatchId != scope.BatchId ||
            !string.Equals(authority.ManifestHash, scope.ManifestHash, StringComparison.Ordinal) ||
            !string.Equals(authority.ContextHash, review.ContextHash, StringComparison.Ordinal) ||
            !ContractPatterns.Sha256().IsMatch(authority.ManifestHash) ||
            !ContractPatterns.Sha256().IsMatch(authority.ContextHash) ||
            !ContractPatterns.Sha256().IsMatch(authority.ReviewerReportHash) ||
            !ContractPatterns.Sha256().IsMatch(authority.QaReportHash) ||
            !SegmentContextResolutionPolicy.ValidForReceipt(authority, review, segments))
            return new("REVIEW_AUTOMATION_AUTHORITY_INVALID", "The review authority is not bound to this batch, manifest, context, reviewer report and QA report.");
        return null;
    }

    private AgentError? ValidateTranslationPlanBinding(JsonObject binding)
    {
        try
        {
            var planKind = StringValue(binding, "planKind");
            var hasParentAuthority = binding["parentBatchAuthority"] is not null;
            if ((planKind == "direct" && hasParentAuthority) ||
                (planKind == "batch-child" && !hasParentAuthority) ||
                planKind is not ("direct" or "batch-child") ||
                StringValue(binding, "pathPolicyVersion") != AgentWorkflowContract.PathPolicyVersion ||
                StringValue(binding, "routingPolicyVersion") != TranslationRouting.PolicyVersion ||
                StringValue(binding, "promptTemplateVersion") != _configuration.PromptTemplateVersion ||
                StringValue(binding, "promptTemplateVersion") != TranslationReviewWorkflow.ContextualPromptTemplateVersion ||
                !CadSemanticContextBuilder.IsSupportedPolicyVersion(StringValue(binding, "contextPolicyVersion")) ||
                binding["includeNeighborExcerpts"]?.GetValue<bool>() != AgentWorkflowContract.TranslationIncludeNeighborExcerpts ||
                binding["reviewIncludeText"]?.GetValue<bool>() != AgentWorkflowContract.TranslationReviewIncludeText ||
                binding["maximumNeighborExcerpts"]?.GetValue<int>() != TranslationBatchFactory.MaximumNeighborExcerpts ||
                binding["maximumNeighborExcerptScalars"]?.GetValue<int>() != TranslationBatchFactory.MaximumNeighborExcerptScalars ||
                binding["maximumNeighborExcerptScalarsPerSegment"]?.GetValue<int>() != TranslationBatchFactory.MaximumNeighborExcerptScalarsPerSegment ||
                StringValue(binding, "outboundPurpose") != AgentWorkflowContract.TranslationOutboundPurpose)
                return Invalid();
            if (!hasParentAuthority) return null;
            var authority = binding["parentBatchAuthority"]!.Deserialize<AgentChildBatchAuthority>(Json);
            if (authority is null || authority.BatchId == Guid.Empty || authority.FileIndex < 0 ||
                !string.Equals(authority.SourceHash, StringValue(binding, "sourceHash"), StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath(authority.OutputPath), StringValue(binding, "outputPath"), StringComparison.Ordinal) ||
                !ContractPatterns.Sha256().IsMatch(authority.ManifestHash) || !AgentBatchPolicy.IsSupported(authority.PolicyVersion))
                return Invalid();
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Invalid();
        }

        static AgentError Invalid() => new("TRANSLATION_PLAN_BINDING_INVALID",
            "The durable translation plan no longer matches the approved prompt, context, excerpt, path or batch authority policy.");
    }

    private static bool MatchesGenerationReviewBinding(JsonObject binding, Guid jobId,
        TranslationReviewSnapshot review, IReadOnlyList<CadTextSegment> segments)
    {
        if (review.JobId != jobId || binding["reviewVersion"]?.GetValue<long>() != review.Version ||
            !HasCompleteApprovedReview(review, segments)) return false;
        var fingerprint = TranslationReviewFingerprint.Create(review);
        return string.Equals(StringValue(binding, "reviewHash"), fingerprint.ReviewHash, StringComparison.Ordinal) &&
            string.Equals(StringValue(binding, "contextHash"), fingerprint.ContextHash ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(StringValue(binding, "reviewAutomationReceiptHash"), fingerprint.ReceiptHash ?? string.Empty, StringComparison.Ordinal);
    }

    private static (DwgTranslationJobData? Data, AgentError? Error) DeserializeJobData(JsonObject data)
    {
        try
        {
            var value = data.Deserialize<DwgTranslationJobData>(Json);
            return value?.Segments is null
                ? (null, new("JOB_DATA_INVALID", "The durable job data is incomplete."))
                : (value, null);
        }
        catch (JsonException) { return (null, new("JOB_DATA_INVALID", "The durable job data is invalid.")); }
    }

    private async Task<bool> HasIntactReviewPayloadAsync(
        JobDocument job,
        DwgTranslationJobData data,
        CancellationToken cancellationToken)
    {
        if (data.Segments is null || data.Segments.Count == 0) return false;
        var review = await _backend.LoadReviewAsync(job.JobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess || review.Value is null || review.Value.JobId != job.JobId ||
            review.Value.Rows.Count != data.Segments.Count) return false;

        var segments = data.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        if (segments.Count != data.Segments.Count || review.Value.Rows.Select(row => row.SegmentId).Distinct(StringComparer.Ordinal).Count() != review.Value.Rows.Count)
            return false;
        return review.Value.Rows.All(row => segments.TryGetValue(row.SegmentId, out var segment) &&
            string.Equals(row.OriginalText, segment.SourceText, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(row.ProposedText) && !string.IsNullOrEmpty(row.FinalText));
    }

    private async Task<(GenerationReconciliationContext? Context, AgentError? Error)> VerifyGenerationReconciliationAsync(
        Guid jobId,
        JsonObject? expectedBinding,
        CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty) return (null, new("GENERATION_RECONCILIATION_REQUEST_INVALID", "A non-empty job ID is required."));
        if (_running.ContainsKey(jobId)) return (null, new("GENERATION_RECONCILIATION_OPERATION_ACTIVE", "The job has an active in-memory operation."));
        var job = await _backend.LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!job.IsSuccess) return (null, ToAgentError(job.Error!));
        var current = job.Value!;
        var failure = current.Data["failure"] as JsonObject ?? current.Data["agentFailure"] as JsonObject;
        var alreadyReconciled = current.State == JobState.Approved && expectedBinding is not null;
        var failedJobVersion = alreadyReconciled
            ? expectedBinding!["jobVersion"]?.GetValue<long>() ?? -1
            : current.Version;
        var code = alreadyReconciled ? StringValue(expectedBinding!, "failureCode") : failure?["code"]?.GetValue<string>();
        var failureEvidence = alreadyReconciled ? expectedBinding : failure;
        var hasTypedRetryable = TryBooleanValue(failureEvidence, alreadyReconciled ? "failureRetryable" : "retryable", out var retryable);
        var categoryText = alreadyReconciled ? NullableStringValue(expectedBinding!, "failureCategory") : NullableStringValue(failure, "category");
        var hasTypedCategory = Enum.TryParse<ErrorCategory>(categoryText, ignoreCase: false, out var category) && Enum.IsDefined(category);
        if (!hasTypedCategory)
            category = code is "IPC_TIMEOUT" or "IPC_PEER_DISCONNECTED" or "CAD_WRITE_CANCELLED_AT_SAFE_BOUNDARY"
                ? ErrorCategory.Transport : ErrorCategory.Environment;
        var technicalStage = alreadyReconciled ? NullableStringValue(expectedBinding!, "technicalStage") : NullableStringValue(failure, "technicalStage");
        var nativeErrorStatus = alreadyReconciled ? NullableStringValue(expectedBinding!, "nativeErrorStatus") : NullableStringValue(failure, "nativeErrorStatus");
        var failureStage = alreadyReconciled ? NullableStringValue(expectedBinding!, "failureStage") : NullableStringValue(failure, "stage");
        var exactLegacyCandidate = false;
        var exactBootstrapTrustCandidate = BootstrapTrustPolicy.IsExactFailure(
            jobId, failedJobVersion, code, categoryText, hasTypedRetryable, retryable,
            failureStage, technicalStage, nativeErrorStatus);
        if (string.Equals(code, "CAD_WRITE_FAILED", StringComparison.Ordinal) &&
            (!hasTypedRetryable || retryable || !hasTypedCategory || category != ErrorCategory.Environment))
            return (null, new("GENERATION_RECONCILIATION_FAILURE_NOT_ALLOWED", "The durable CAD failure lacks exact typed category/retryability evidence."));
        if (string.Equals(code, "CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT", StringComparison.Ordinal) &&
            (!hasTypedRetryable || retryable || !hasTypedCategory || category != ErrorCategory.Security ||
             !exactBootstrapTrustCandidate))
            return (null, new("GENERATION_RECONCILIATION_FAILURE_NOT_ALLOWED",
                "The bootstrap trust failure has no authorized historical binding."));
        if (current.State is not (JobState.Failed or JobState.Approved) || alreadyReconciled && current.Version != failedJobVersion + 1 ||
            !IsRecoverableGenerationCode(code, retryable, category, technicalStage, nativeErrorStatus,
                exactLegacyCandidate, exactBootstrapTrustCandidate))
            return (null, new("GENERATION_RECONCILIATION_FAILURE_NOT_ALLOWED", "The durable failure is not an allowlisted recoverable generation failure."));
        var coreData = current.Data.DeepClone().AsObject();
        coreData.Remove("failure");
        coreData.Remove("agentFailure");
        var data = DeserializeJobData(coreData);
        if (data.Error is not null || data.Data is null) return (null, data.Error);
        if (alreadyReconciled)
        {
            var marker = data.Data.GenerationFailureReconciliationReceipt;
            if (marker is null || marker.FailedJobVersion != failedJobVersion ||
                !string.Equals(marker.FailureCode, code, StringComparison.Ordinal) ||
                !string.Equals(marker.ReconciliationBindingHash, StringValue(expectedBinding!, "reconciliationBindingHash"), StringComparison.Ordinal) ||
                !string.Equals(marker.EvidenceHash, StringValue(expectedBinding!, "evidenceHash"), StringComparison.Ordinal))
                return (null, new("GENERATION_RECONCILIATION_MARKER_MISMATCH", "The reconciled job marker differs from the sealed plan."));
        }
        var source = _paths.ValidateSource(data.Data.Specification.SourcePath);
        var output = _paths.ValidateNewOutput(data.Data.Specification.OutputPath);
        if (source.Error is not null || output.Error is not null)
            return (null, new("GENERATION_RECONCILIATION_PATH_STATE_INVALID", "The source or absent CreateNew output path is no longer safe."));
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(source.Path!, cancellationToken).ConfigureAwait(false);
        if (snapshot.Error is not null || snapshot.Snapshot is null ||
            !string.Equals(snapshot.Snapshot.Hash, data.Data.Specification.SourceHash, StringComparison.Ordinal))
            return (null, new("GENERATION_RECONCILIATION_SOURCE_MISMATCH", "The source no longer matches the durable approved checkpoint."));
        if (HasActiveCandidateOrStaging(output.Path!, jobId))
            return (null, new("GENERATION_RECONCILIATION_CANDIDATE_OR_STAGING_PRESENT", "A candidate or staging artifact is present for this job."));
        var latest = await _files.LoadLatestOperationAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (latest?.State == "Running" || await _files.HasActiveOperationAsync(cancellationToken).ConfigureAwait(false) ||
            _files.IsLeaseActive("job:" + jobId.ToString("D")) || _files.IsLeaseActive("cad:global") ||
            _files.IsLeaseActive("output:" + output.Path) || _files.LoadCadBlocked() is not null)
            return (null, new("GENERATION_RECONCILIATION_OPERATION_ACTIVE", "A durable CAD, output, or workflow lease is active."));
        var review = await _backend.LoadReviewAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess || review.Value is null || !HasCompleteApprovedReview(review.Value, data.Data.Segments!))
            return (null, new("GENERATION_RECONCILIATION_REVIEW_MISMATCH", "The durable approved review is incomplete or does not match extracted segments."));
        var reviewFingerprint = TranslationReviewFingerprint.Create(review.Value);
        JsonObject? cadWriteEvidence = null;
        JsonObject? cadBootstrapTrustEvidence = null;
        var approvedCheckpointVersion = -1L;
        var approvedCheckpointHash = string.Empty;
        var evidenceHash = ReconciliationBindingHash(jobId, failedJobVersion, snapshot.Snapshot.Hash,
            snapshot.Snapshot.Bytes, output.Path!, review.Value.Version, reviewFingerprint.ReviewHash);
        if (string.Equals(code, "CAD_WRITE_FAILED", StringComparison.Ordinal))
        {
            var cad = await VerifyCadWriteFailureEvidenceAsync(current, failedJobVersion, alreadyReconciled,
                data.Data, snapshot.Snapshot, output.Path!, review.Value, reviewFingerprint, expectedBinding,
                cancellationToken).ConfigureAwait(false);
            if (cad.Error is not null) return (null, cad.Error);
            cadWriteEvidence = cad.Evidence;
            approvedCheckpointVersion = cad.ApprovedCheckpointVersion;
            approvedCheckpointHash = cad.ApprovedCheckpointHash!;
            evidenceHash = CanonicalHash(cadWriteEvidence!);
        }
        else if (string.Equals(code, "CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT", StringComparison.Ordinal))
        {
            var trust = await VerifyCadBootstrapTrustFailureEvidenceAsync(current, failedJobVersion,
                alreadyReconciled, data.Data, snapshot.Snapshot, output.Path!, review.Value,
                reviewFingerprint, expectedBinding, cancellationToken).ConfigureAwait(false);
            if (trust.Error is not null) return (null, trust.Error);
            cadBootstrapTrustEvidence = trust.Evidence;
            approvedCheckpointVersion = trust.ApprovedCheckpointVersion;
            approvedCheckpointHash = trust.ApprovedCheckpointHash!;
            evidenceHash = CanonicalHash(cadBootstrapTrustEvidence!);
        }
        var context = new GenerationReconciliationContext(current, failedJobVersion, alreadyReconciled,
            snapshot.Snapshot, output.Path!, review.Value, reviewFingerprint.ReviewHash, code!, category,
            retryable, failureStage, technicalStage, nativeErrorStatus, approvedCheckpointVersion, approvedCheckpointHash,
            evidenceHash, cadWriteEvidence, cadBootstrapTrustEvidence);
        if (expectedBinding is not null && !BindingMatches(expectedBinding, context))
            return (null, new("GENERATION_RECONCILIATION_BINDING_MISMATCH", "The job, source, output, or review lineage changed after planning."));
        return (context, null);
    }

    private static bool HasCompleteApprovedReview(TranslationReviewSnapshot review, IReadOnlyList<CadTextSegment> segments)
    {
        if (review.Rows.Count == 0 || review.Rows.Count != segments.Count) return false;
        var expected = segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        return expected.Count == segments.Count && review.Rows.All(row => expected.TryGetValue(row.SegmentId, out var segment) &&
            string.Equals(row.OriginalText, segment.SourceText, StringComparison.Ordinal) &&
            row.State is SegmentState.Approved or SegmentState.Excluded && !string.IsNullOrEmpty(row.FinalText));
    }

    private static bool IsRecoverableGenerationCode(string? code, bool retryable, ErrorCategory category,
        string? technicalStage, string? nativeErrorStatus, bool exactLegacyCandidate,
        bool exactBootstrapTrustCandidate) =>
        code switch
        {
            "IPC_TIMEOUT" or "IPC_PEER_DISCONNECTED" => category == ErrorCategory.Transport,
            "CAD_WRITE_CANCELLED_AT_SAFE_BOUNDARY" => retryable && category == ErrorCategory.Transport,
            "CAD_WRITE_FAILED" => !retryable && category == ErrorCategory.Environment &&
                (exactLegacyCandidate || CadWriteFailureReconciliationPolicy.IsRecoverableFuture(technicalStage, nativeErrorStatus)),
            "CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT" => !retryable && category == ErrorCategory.Security &&
                exactBootstrapTrustCandidate,
            _ => false
        };

    private static bool BindingMatches(JsonObject binding, GenerationReconciliationContext context) =>
        string.Equals(CanonicalHash(binding), CanonicalHash(CreateReconciliationBinding(context)), StringComparison.Ordinal);

    private static bool HasActiveCandidateOrStaging(string outputPath, Guid jobId)
    {
        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return true;
            var exactCandidate = CadWriteRequestFactory.CandidatePathFor(outputPath, jobId);
            if (File.Exists(exactCandidate) || Directory.Exists(exactCandidate)) return true;
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

    private static AgentError ToAgentError(ContractError error) => new(error.Code, error.Message, error.Retryable);
    private sealed record GenerationReconciliationContext(
        JobDocument Job,
        long FailedJobVersion,
        bool AlreadyReconciled,
        AgentDwgSnapshot Source,
        string OutputPath,
        TranslationReviewSnapshot Review,
        string ReviewHash,
        string FailureCode,
        ErrorCategory FailureCategory,
        bool FailureRetryable,
        string? FailureStage,
        string? TechnicalStage,
        string? NativeErrorStatus,
        long ApprovedCheckpointVersion,
        string ApprovedCheckpointHash,
        string EvidenceHash,
        JsonObject? CadWriteEvidence,
        JsonObject? CadBootstrapTrustEvidence);

    private sealed record CadWriteEvidenceResult(
        JsonObject? Evidence,
        long ApprovedCheckpointVersion,
        string? ApprovedCheckpointHash,
        AgentError? Error);

    private sealed record CadBootstrapTrustEvidenceResult(
        JsonObject? Evidence,
        long ApprovedCheckpointVersion,
        string? ApprovedCheckpointHash,
        AgentError? Error);

    private sealed record ActiveConfigurationEvidence(
        string? ActiveHash,
        string? SecurityProjectionHash,
        string? RuntimeManifestHash,
        AgentError? Error);

    private static JsonObject CreateReconciliationBinding(GenerationReconciliationContext context)
    {
        var binding = new JsonObject
        {
            ["jobId"] = context.Job.JobId.ToString("D"),
            ["jobVersion"] = context.FailedJobVersion,
            ["failureCode"] = context.FailureCode,
            ["failureCategory"] = context.FailureCategory.ToString(),
            ["failureRetryable"] = context.FailureRetryable,
            ["sourcePath"] = context.Source.Path,
            ["sourceHash"] = context.Source.Hash,
            ["sourceBytes"] = context.Source.Bytes,
            ["outputPath"] = context.OutputPath,
            ["reviewVersion"] = context.Review.Version,
            ["reviewHash"] = context.ReviewHash,
            ["decisionCount"] = context.Review.Rows.Count,
            ["approvedCheckpointVersion"] = context.ApprovedCheckpointVersion,
            ["approvedCheckpointHash"] = context.ApprovedCheckpointHash,
            ["evidenceHash"] = context.EvidenceHash
        };
        if (context.CadBootstrapTrustEvidence is not null)
        {
            binding["failureStage"] = context.FailureStage;
            binding["technicalStage"] = context.TechnicalStage;
            binding["nativeErrorStatus"] = context.NativeErrorStatus;
            binding["cadBootstrapTrustEvidence"] = context.CadBootstrapTrustEvidence.DeepClone();
        }
        else
        {
            binding["technicalStage"] = context.TechnicalStage;
            binding["nativeErrorStatus"] = context.NativeErrorStatus;
            binding["cadWriteEvidence"] = context.CadWriteEvidence?.DeepClone();
        }
        binding["reconciliationBindingHash"] = CanonicalHash(binding);
        return binding;
    }

    private async Task<CadBootstrapTrustEvidenceResult> VerifyCadBootstrapTrustFailureEvidenceAsync(
        JobDocument job,
        long failedJobVersion,
        bool alreadyReconciled,
        DwgTranslationJobData data,
        AgentDwgSnapshot source,
        string outputPath,
        TranslationReviewSnapshot review,
        TranslationReviewFingerprint reviewFingerprint,
        JsonObject? expectedBinding,
        CancellationToken cancellationToken)
    {
        static CadBootstrapTrustEvidenceResult Invalid(string code =
            "GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_EVIDENCE_INVALID") =>
            new(null, -1, null, new(code,
                "Bootstrap trust evidence is incomplete, stale, or inconsistent."));

        var policy = BootstrapTrustPolicy;
        if (job.JobId != policy.JobId || failedJobVersion != policy.FailedJobVersion ||
            !string.Equals(Path.GetFileName(source.Path), policy.SourceFileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(outputPath), policy.OutputFileName, StringComparison.Ordinal) ||
            !string.Equals(source.Hash, policy.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(data.ConfigurationHash, policy.JobConfigurationHash, StringComparison.Ordinal) ||
            review.Version != policy.ReviewVersion || review.Rows.Count != policy.ReviewDecisionCount ||
            !string.Equals(reviewFingerprint.ReviewHash, policy.ReviewHash, StringComparison.Ordinal) ||
            !string.Equals(reviewFingerprint.ContextHash, policy.ContextHash, StringComparison.Ordinal) ||
            !string.Equals(reviewFingerprint.ReceiptHash, policy.ReviewAutomationReceiptHash, StringComparison.Ordinal))
            return Invalid();

        var jobPath = Path.Combine(_configuration.WorkspaceRoot, job.JobId.ToString("D"), "job.json");
        var reviewPath = Path.Combine(_configuration.WorkspaceRoot, job.JobId.ToString("D"), "review", "session.json");
        var jobArtifactHash = alreadyReconciled
            ? expectedBinding?["cadBootstrapTrustEvidence"]?["failedJobArtifactHash"]?.GetValue<string>()
            : await ArtifactHashAsync(jobPath, cancellationToken).ConfigureAwait(false);
        var reviewArtifactHash = await ArtifactHashAsync(reviewPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(jobArtifactHash, policy.FailedJobArtifactHash, StringComparison.Ordinal) ||
            !string.Equals(reviewArtifactHash, policy.ReviewArtifactHash, StringComparison.Ordinal))
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_ARTIFACT_DRIFT");

        var workflowRoot = Path.Combine(_configuration.LogRoot, "workflow");
        var planPath = Path.Combine(workflowRoot, "plans", policy.GenerationPlanId + ".json");
        var planEvidence = await LoadExactEvidenceAsync<WorkflowPlanDocument>(planPath, cancellationToken).ConfigureAwait(false);
        var plan = planEvidence.Document;
        if (plan is null || !string.Equals(planEvidence.ArtifactHash, policy.GenerationPlanArtifactHash, StringComparison.Ordinal) ||
            plan.PlanId != policy.GenerationPlanId || plan.Purpose != "generation.execute" ||
            !plan.Consumed || plan.ConsumedAtUtc is null ||
            plan.Binding["jobVersion"]?.GetValue<long>() != policy.ApprovedCheckpointVersion ||
            !string.Equals(StringValue(plan.Binding, "jobId"), policy.JobId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(StringValue(plan.Binding, "sourcePath"), source.Path, StringComparison.Ordinal) ||
            !string.Equals(StringValue(plan.Binding, "sourceHash"), source.Hash, StringComparison.Ordinal) ||
            !string.Equals(StringValue(plan.Binding, "outputPath"), outputPath, StringComparison.Ordinal) ||
            plan.Binding["reviewVersion"]?.GetValue<long>() != policy.ReviewVersion ||
            !string.Equals(StringValue(plan.Binding, "reviewHash"), policy.ReviewHash, StringComparison.Ordinal) ||
            !string.Equals(StringValue(plan.Binding, "contextHash"), policy.ContextHash, StringComparison.Ordinal) ||
            !string.Equals(StringValue(plan.Binding, "reviewAutomationReceiptHash"),
                policy.ReviewAutomationReceiptHash, StringComparison.Ordinal) ||
            !string.Equals(StringValue(plan.Binding, "validationPolicy"), "VisualStrictV2", StringComparison.Ordinal))
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_PLAN_INVALID");

        var checkpointPath = Path.Combine(_configuration.WorkspaceRoot, job.JobId.ToString("D"), "checkpoints",
            $"{policy.ApprovedCheckpointVersion:D12}.json");
        var checkpoint = await LoadCheckpointEvidenceAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
        if (checkpoint.Document is null ||
            !string.Equals(checkpoint.ArtifactHash, policy.ApprovedCheckpointArtifactHash, StringComparison.Ordinal) ||
            checkpoint.Document.JobId != policy.JobId || checkpoint.Document.SafeState != JobState.Approved ||
            checkpoint.Document.ExpectedVersion != policy.ApprovedCheckpointVersion ||
            !string.Equals(checkpoint.Document.SourceHash, policy.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.Document.ConfigurationHash, policy.JobConfigurationHash, StringComparison.Ordinal))
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_CHECKPOINT_INVALID");

        var operationEvidence = await _files.LoadOperationEvidenceAsync(policy.OperationId, cancellationToken).ConfigureAwait(false);
        var operation = operationEvidence?.Document;
        var latest = await _files.LoadLatestOperationAsync(job.JobId, cancellationToken).ConfigureAwait(false);
        if (operation is null || !string.Equals(operationEvidence?.ArtifactHash, policy.OperationArtifactHash, StringComparison.Ordinal))
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_OPERATION_ARTIFACT_DRIFT");
        if (latest?.OperationId != policy.OperationId)
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_OPERATION_STALE");
        if (operation.JobId != policy.JobId ||
            operation.Kind != "generation.execute" || operation.State != "Failed" || operation.Stage != "Failed" ||
            operation.ErrorCode != "CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT" ||
            operation.ErrorCategory != ErrorCategory.Security || operation.Retryable ||
            operation.TechnicalStage is not null || operation.NativeErrorStatus is not null ||
            plan.ConsumedAtUtc > operation.CreatedAtUtc)
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_OPERATION_INVALID");

        var markerPath = Path.Combine(workflowRoot, "idempotency", policy.GenerationMarkerName);
        var markerEvidence = await LoadExactJsonObjectEvidenceAsync(markerPath, cancellationToken).ConfigureAwait(false);
        var marker = markerEvidence.Document;
        var markerResponse = marker?["response"] as JsonObject;
        if (marker is null || !string.Equals(markerEvidence.ArtifactHash, policy.GenerationMarkerArtifactHash, StringComparison.Ordinal) ||
            marker["state"]?.GetValue<string>() != "Completed" ||
            marker["requestHash"]?.GetValue<string>() != policy.GenerationRequestBodyHash ||
            markerResponse?["jobId"]?.GetValue<string>() != policy.JobId.ToString("D") ||
            markerResponse["operationId"]?.GetValue<string>() != policy.OperationId ||
            markerResponse["jobVersion"]?.GetValue<long>() != policy.ApprovedCheckpointVersion ||
            markerResponse["state"]?.GetValue<string>() != "Started")
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_MARKER_INVALID");

        var bootstrapRoot = Path.Combine(_configuration.LogRoot, "cad-bootstrap", "workflow", "write");
        var bootstrapPath = Path.Combine(bootstrapRoot, policy.BootstrapArtifactName);
        var bootstrapHash = await ArtifactHashAsync(bootstrapPath, cancellationToken).ConfigureAwait(false);
        var bootstrapStem = Path.GetFileNameWithoutExtension(policy.BootstrapArtifactName);
        if (!string.Equals(bootstrapHash, policy.BootstrapArtifactHash, StringComparison.Ordinal) ||
            File.Exists(Path.Combine(bootstrapRoot, bootstrapStem + ".original-trustedpaths.txt")) ||
            File.Exists(Path.Combine(bootstrapRoot, bootstrapStem + ".restored.marker")) ||
            _cadProcessExists())
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_BOOTSTRAP_INVALID");

        var receipt = _cadReceipts.Load(job.JobId);
        if (receipt is null || receipt.OperationId == policy.OperationId || receipt.RequiresProcessTerminationApproval ||
            receipt.ProcessExitedAtUtc is null || receipt.ProcessExitedAtUtc > operation.CreatedAtUtc ||
            _processExists(receipt.ProcessId))
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_CAD_ABSENCE_INVALID");

        var auditRoot = Path.Combine(_configuration.WorkspaceRoot, job.JobId.ToString("D"), "audit");
        if (!await ContainsArtifactHashesAsync(auditRoot,
                [policy.WritingAuditArtifactHash, policy.FailureAuditArtifactHash], cancellationToken).ConfigureAwait(false))
            return Invalid("GENERATION_RECONCILIATION_BOOTSTRAP_TRUST_AUDIT_INVALID");

        var candidatePath = CadWriteRequestFactory.CandidatePathFor(outputPath, job.JobId);
        var evidence = new JsonObject
        {
            ["policyVersion"] = policy.PolicyVersion,
            ["failedJobArtifactHash"] = jobArtifactHash,
            ["jobConfigurationHash"] = data.ConfigurationHash,
            ["reviewArtifactHash"] = reviewArtifactHash,
            ["reviewHash"] = reviewFingerprint.ReviewHash,
            ["contextHash"] = reviewFingerprint.ContextHash,
            ["reviewAutomationReceiptHash"] = reviewFingerprint.ReceiptHash,
            ["approvedCheckpointVersion"] = policy.ApprovedCheckpointVersion,
            ["approvedCheckpointHash"] = checkpoint.ArtifactHash,
            ["consumedGenerationPlanId"] = plan.PlanId,
            ["consumedGenerationPlanHash"] = planEvidence.ArtifactHash,
            ["operationId"] = operation.OperationId,
            ["operationHash"] = operationEvidence!.ArtifactHash,
            ["generationIdempotencyMarkerHash"] = markerEvidence.ArtifactHash,
            ["generationIdempotencyMarkerName"] = policy.GenerationMarkerName,
            ["generationRequestBodyHash"] = policy.GenerationRequestBodyHash,
            ["bootstrapArtifactName"] = policy.BootstrapArtifactName,
            ["bootstrapArtifactHash"] = bootstrapHash,
            ["candidatePathHash"] = AgentWorkflowFileStore.TextHash(candidatePath),
            ["cleanupComplete"] = true,
            ["outputAbsent"] = true,
            ["candidateAbsent"] = true,
            ["stagingAbsent"] = true,
            ["bootstrapOriginalSnapshotAbsent"] = true,
            ["bootstrapRestoreMarkerAbsent"] = true,
            ["generationCadReceiptAbsent"] = true,
            ["cadProcessAbsent"] = true,
            ["operationsLeasesAndFenceQuiescent"] = true
        };
        return new(evidence, policy.ApprovedCheckpointVersion, checkpoint.ArtifactHash, null);
    }

    private async Task<CadWriteEvidenceResult> VerifyCadWriteFailureEvidenceAsync(
        JobDocument job,
        long failedJobVersion,
        bool alreadyReconciled,
        DwgTranslationJobData data,
        AgentDwgSnapshot source,
        string outputPath,
        TranslationReviewSnapshot review,
        TranslationReviewFingerprint reviewFingerprint,
        JsonObject? expectedBinding,
        CancellationToken cancellationToken)
    {
        static CadWriteEvidenceResult Invalid(string code = "GENERATION_RECONCILIATION_CAD_EVIDENCE_INVALID") =>
            new(null, -1, null, new(code, "The CAD write failure evidence is incomplete, stale, or inconsistent."));

        if (_configuration.ActiveConfigurationPath is null || _configuration.ActiveConfigurationHash is null ||
            !ContractPatterns.Sha256().IsMatch(_configuration.ActiveConfigurationHash)) return Invalid();
        var configurationEvidence = await LoadActiveConfigurationEvidenceAsync(cancellationToken).ConfigureAwait(false);
        var activeConfigurationHash = configurationEvidence.ActiveHash;
        if (configurationEvidence.Error is not null || activeConfigurationHash is null)
            return Invalid("GENERATION_RECONCILIATION_CONFIGURATION_DRIFT");

        var planScan = await _files.LoadPlanEvidenceStrictAsync("generation.execute", job.JobId, cancellationToken).ConfigureAwait(false);
        if (planScan.HadInvalidArtifact) return Invalid("GENERATION_RECONCILIATION_CAD_PLAN_LEDGER_INVALID");
        var matchingPlans = planScan.Items.Where(item => item.Document.Consumed && item.Document.ConsumedAtUtc is not null &&
            item.Document.Binding["jobVersion"]?.GetValue<long>() == failedJobVersion - 2 &&
            string.Equals(StringValue(item.Document.Binding, "sourcePath"), source.Path, StringComparison.Ordinal) &&
            string.Equals(StringValue(item.Document.Binding, "sourceHash"), source.Hash, StringComparison.Ordinal) &&
            string.Equals(StringValue(item.Document.Binding, "outputPath"), outputPath, StringComparison.Ordinal) &&
            item.Document.Binding["reviewVersion"]?.GetValue<long>() == review.Version &&
            string.Equals(StringValue(item.Document.Binding, "reviewHash"), reviewFingerprint.ReviewHash, StringComparison.Ordinal) &&
            string.Equals(StringValue(item.Document.Binding, "contextHash"), reviewFingerprint.ContextHash ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(StringValue(item.Document.Binding, "reviewAutomationReceiptHash"), reviewFingerprint.ReceiptHash ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(StringValue(item.Document.Binding, "validationPolicy"), "VisualStrictV2", StringComparison.Ordinal)).ToArray();
        if (matchingPlans.Length != 1) return Invalid("GENERATION_RECONCILIATION_CAD_PLAN_EVIDENCE_INVALID");
        var generationPlan = matchingPlans[0];
        var checkpointVersion = generationPlan.Document.Binding["jobVersion"]!.GetValue<long>();
        var checkpointPath = Path.Combine(_configuration.WorkspaceRoot, job.JobId.ToString("D"), "checkpoints", $"{checkpointVersion:D12}.json");
        var checkpointEvidence = await LoadCheckpointEvidenceAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
        var checkpointHash = checkpointEvidence.ArtifactHash;
        var approvedCheckpoint = checkpointEvidence.Document;
        if (approvedCheckpoint is null || checkpointHash is null || approvedCheckpoint.JobId != job.JobId ||
            approvedCheckpoint.SafeState != JobState.Approved || approvedCheckpoint.ExpectedVersion != checkpointVersion ||
            approvedCheckpoint.CreatedAtUtc.Offset != TimeSpan.Zero ||
            !string.Equals(approvedCheckpoint.SourceHash, source.Hash, StringComparison.Ordinal) ||
            !string.Equals(approvedCheckpoint.ConfigurationHash, data.ConfigurationHash, StringComparison.Ordinal) ||
            !string.Equals(approvedCheckpoint.ContractVersion, ContractV1.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(approvedCheckpoint.Data["state"]?.GetValue<string>(), JobState.Approved.ToString(), StringComparison.Ordinal) ||
            approvedCheckpoint.Data["jobVersion"]?.GetValue<long>() != checkpointVersion)
            return Invalid("GENERATION_RECONCILIATION_CAD_CHECKPOINT_INVALID");

        var receiptEvidence = _cadReceipts.LoadEvidence(job.JobId);
        var receipt = receiptEvidence.Receipt;
        if (receipt is null || receiptEvidence.ArtifactHash is null || receipt.JobId != job.JobId ||
            string.IsNullOrWhiteSpace(receipt.OperationId) || !AgentWorkflowFileStore.ValidOpaque(receipt.OperationId) ||
            !Guid.TryParseExact(receipt.RequestId, "D", out _) || !ContractPatterns.Sha256().IsMatch(receipt.RequestHash) ||
            receipt.ProcessId <= 0 || string.IsNullOrWhiteSpace(receipt.ExecutablePath) || !Path.IsPathFullyQualified(receipt.ExecutablePath) ||
            !ContractPatterns.Sha256().IsMatch(receipt.ResponseHash ?? string.Empty) ||
            !string.Equals(receipt.ResponseErrorCode, "CAD_WRITE_FAILED", StringComparison.Ordinal) ||
            receipt.ResponseErrorCategory != ErrorCategory.Environment || receipt.ResponseRetryable != false ||
            receipt.ReceivedAtUtc is null || receipt.QuitRequestedAtUtc is null || receipt.ProcessExitedAtUtc is null ||
            receipt.ProcessStartedAtUtc == default || receipt.LaunchedAtUtc == default ||
            !IsUtc(receipt.ProcessStartedAtUtc) || !IsUtc(receipt.LaunchedAtUtc) || !IsUtc(receipt.ReceivedAtUtc.Value) ||
            !IsUtc(receipt.QuitRequestedAtUtc.Value) || !IsUtc(receipt.ProcessExitedAtUtc.Value) ||
            receipt.LaunchedAtUtc < receipt.ProcessStartedAtUtc || receipt.ReceivedAtUtc < receipt.LaunchedAtUtc ||
            receipt.QuitRequestedAtUtc < receipt.ReceivedAtUtc || receipt.ProcessExitedAtUtc < receipt.QuitRequestedAtUtc ||
            receipt.ExitCode != 0 || !string.Equals(receipt.CleanupOutcome, "CAD_PROCESS_EXITED", StringComparison.Ordinal) ||
            receipt.RequiresProcessTerminationApproval || _processExists(receipt.ProcessId) || _cadProcessExists())
            return Invalid("GENERATION_RECONCILIATION_CAD_RECEIPT_INVALID");

        var operationEvidence = await _files.LoadOperationEvidenceAsync(receipt.OperationId, cancellationToken).ConfigureAwait(false);
        if (operationEvidence is null || operationEvidence.Document.JobId != job.JobId ||
            !string.Equals(operationEvidence.Document.Kind, "generation.execute", StringComparison.Ordinal) ||
            operationEvidence.Document.State is not ("Completed" or "Failed") ||
            !string.Equals(operationEvidence.Document.Stage, "Failed", StringComparison.Ordinal) ||
            generationPlan.Document.ConsumedAtUtc > operationEvidence.Document.CreatedAtUtc ||
            receipt.LaunchedAtUtc < operationEvidence.Document.CreatedAtUtc ||
            operationEvidence.Document.UpdatedAtUtc < receipt.ProcessExitedAtUtc)
            return Invalid("GENERATION_RECONCILIATION_CAD_OPERATION_INVALID");
        var operationScan = await _files.LoadOperationsStrictAsync(cancellationToken).ConfigureAwait(false);
        if (operationScan.HadInvalidArtifact) return Invalid("GENERATION_RECONCILIATION_CAD_OPERATION_LEDGER_INVALID");
        var generationOperations = operationScan.Items
            .Where(item => item.JobId == job.JobId && string.Equals(item.Kind, "generation.execute", StringComparison.Ordinal))
            .ToArray();
        if (generationOperations.Any(item => !string.Equals(item.OperationId, receipt.OperationId, StringComparison.Ordinal) &&
            item.CreatedAtUtc >= operationEvidence.Document.CreatedAtUtc))
            return Invalid("GENERATION_RECONCILIATION_CAD_OPERATION_STALE");

        var idempotencyScan = await _files.LoadCompletedIdempotencyEvidenceStrictAsync(job.JobId, receipt.OperationId, cancellationToken).ConfigureAwait(false);
        if (idempotencyScan.HadInvalidArtifact || idempotencyScan.Items.Count != 1)
            return Invalid("GENERATION_RECONCILIATION_CAD_EXECUTION_MARKER_INVALID");
        var generationMarker = idempotencyScan.Items[0];
        var markerResponse = generationMarker.Document["response"] as JsonObject;
        var markerRequestHash = generationMarker.Document["requestHash"]?.GetValue<string>();
        if (generationMarker.Document["state"]?.GetValue<string>() != "Completed" ||
            markerRequestHash is null || markerRequestHash.Length != 64 || !markerRequestHash.All(Uri.IsHexDigit) ||
            !string.Equals(markerResponse?["jobId"]?.GetValue<string>(), job.JobId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(markerResponse?["operationId"]?.GetValue<string>(), receipt.OperationId, StringComparison.Ordinal) ||
            markerResponse?["jobVersion"]?.GetValue<long>() != checkpointVersion ||
            markerResponse["state"]?.GetValue<string>() != "Started")
            return Invalid("GENERATION_RECONCILIATION_CAD_EXECUTION_MARKER_INVALID");

        var candidatePath = CadWriteRequestFactory.CandidatePathFor(outputPath, job.JobId);
        if (File.Exists(outputPath) || Directory.Exists(outputPath) || File.Exists(candidatePath) || Directory.Exists(candidatePath) ||
            HasActiveCandidateOrStaging(outputPath, job.JobId)) return Invalid("GENERATION_RECONCILIATION_CANDIDATE_OR_STAGING_PRESENT");

        var jobPath = Path.Combine(_configuration.WorkspaceRoot, job.JobId.ToString("D"), "job.json");
        var reviewPath = Path.Combine(_configuration.WorkspaceRoot, job.JobId.ToString("D"), "review", "session.json");
        var jobArtifactHash = alreadyReconciled
            ? expectedBinding?["cadWriteEvidence"]?["failedJobArtifactHash"]?.GetValue<string>()
            : await ArtifactHashAsync(jobPath, cancellationToken).ConfigureAwait(false);
        var reviewArtifactHash = await ArtifactHashAsync(reviewPath, cancellationToken).ConfigureAwait(false);
        if (!ContractPatterns.Sha256().IsMatch(jobArtifactHash ?? string.Empty) || reviewArtifactHash is null)
            return Invalid("GENERATION_RECONCILIATION_CAD_ARTIFACT_HASH_INVALID");

        var isLegacy = false;
        if (isLegacy)
        {
            var auditRoot = Path.Combine(_configuration.WorkspaceRoot, job.JobId.ToString("D"), "audit");
            if (!string.Equals(source.Hash, _legacyCadWriteRecovery.SourceHash, StringComparison.Ordinal) ||
                !string.Equals(data.ConfigurationHash, _legacyCadWriteRecovery.JobConfigurationHash, StringComparison.Ordinal) ||
                !string.Equals(configurationEvidence.SecurityProjectionHash, _legacyCadWriteRecovery.ConfigurationSecurityProjectionHash, StringComparison.Ordinal) ||
                !string.Equals(jobArtifactHash, _legacyCadWriteRecovery.JobArtifactHash, StringComparison.Ordinal) ||
                review.Version != _legacyCadWriteRecovery.ReviewVersion || review.Rows.Count != _legacyCadWriteRecovery.DecisionCount ||
                !string.Equals(reviewArtifactHash, _legacyCadWriteRecovery.ReviewArtifactHash, StringComparison.Ordinal) ||
                !string.Equals(reviewFingerprint.ReviewHash, _legacyCadWriteRecovery.ReviewHash, StringComparison.Ordinal) ||
                !string.Equals(reviewFingerprint.ContextHash, _legacyCadWriteRecovery.ContextHash, StringComparison.Ordinal) ||
                !string.Equals(reviewFingerprint.ReceiptHash, _legacyCadWriteRecovery.ReviewAutomationReceiptHash, StringComparison.Ordinal) ||
                checkpointVersion != _legacyCadWriteRecovery.ApprovedCheckpointVersion ||
                !string.Equals(checkpointHash, _legacyCadWriteRecovery.CheckpointArtifactHash, StringComparison.Ordinal) ||
                !string.Equals(generationPlan.Document.PlanId, _legacyCadWriteRecovery.GenerationPlanId, StringComparison.Ordinal) ||
                !string.Equals(generationPlan.ArtifactHash, _legacyCadWriteRecovery.GenerationPlanArtifactHash, StringComparison.Ordinal) ||
                !string.Equals(receipt.OperationId, _legacyCadWriteRecovery.OperationId, StringComparison.Ordinal) ||
                !string.Equals(operationEvidence.ArtifactHash, _legacyCadWriteRecovery.OperationArtifactHash, StringComparison.Ordinal) ||
                !string.Equals(receiptEvidence.ArtifactHash, _legacyCadWriteRecovery.CadReceiptArtifactHash, StringComparison.Ordinal) ||
                !string.Equals(generationMarker.ArtifactHash, _legacyCadWriteRecovery.GenerationIdempotencyArtifactHash, StringComparison.Ordinal) ||
                !string.Equals(generationMarker.ArtifactName, _legacyCadWriteRecovery.GenerationIdempotencyArtifactName, StringComparison.Ordinal) ||
                !string.Equals(markerRequestHash, _legacyCadWriteRecovery.GenerationRequestBodyHash, StringComparison.Ordinal) ||
                !string.Equals(receipt.RequestHash, _legacyCadWriteRecovery.RequestHash, StringComparison.Ordinal) ||
                !string.Equals(receipt.ResponseHash, _legacyCadWriteRecovery.ResponseHash, StringComparison.Ordinal) ||
                !await ContainsArtifactHashesAsync(auditRoot,
                    [_legacyCadWriteRecovery.WritingAuditArtifactHash, _legacyCadWriteRecovery.FailureAuditArtifactHash],
                    cancellationToken).ConfigureAwait(false))
                return Invalid("GENERATION_RECONCILIATION_CAD_LEGACY_BINDING_INVALID");
        }
        else
        {
            var failure = job.Data["failure"] as JsonObject ?? job.Data["agentFailure"] as JsonObject;
            var stage = alreadyReconciled ? NullableStringValue(expectedBinding!, "technicalStage") : NullableStringValue(failure, "technicalStage");
            var nativeStatus = alreadyReconciled ? NullableStringValue(expectedBinding!, "nativeErrorStatus") : NullableStringValue(failure, "nativeErrorStatus");
            if (!CadWriteFailureReconciliationPolicy.IsRecoverableFuture(stage, nativeStatus) ||
                !string.Equals(receipt.ResponseTechnicalStage, stage, StringComparison.Ordinal) ||
                !string.Equals(receipt.ResponseNativeErrorStatus, nativeStatus, StringComparison.Ordinal) ||
                operationEvidence.Document.State != "Failed" ||
                !string.Equals(operationEvidence.Document.ErrorCode, "CAD_WRITE_FAILED", StringComparison.Ordinal) ||
                operationEvidence.Document.ErrorCategory != ErrorCategory.Environment || operationEvidence.Document.Retryable ||
                !string.Equals(operationEvidence.Document.TechnicalStage, stage, StringComparison.Ordinal) ||
                !string.Equals(operationEvidence.Document.NativeErrorStatus, nativeStatus, StringComparison.Ordinal))
                return Invalid("GENERATION_RECONCILIATION_CAD_TYPED_BINDING_INVALID");
        }

        var evidence = new JsonObject
        {
            ["policyVersion"] = "cad-write-failure-reconciliation/1.0",
            ["legacyException"] = isLegacy,
            ["failedJobArtifactHash"] = jobArtifactHash,
            ["jobConfigurationHash"] = data.ConfigurationHash,
            ["failureActiveConfigurationHash"] = isLegacy
                ? _legacyCadWriteRecovery.FailureActiveConfigurationHash
                : activeConfigurationHash,
            ["activeConfigurationHash"] = activeConfigurationHash,
            ["configurationSecurityProjectionHash"] = configurationEvidence.SecurityProjectionHash,
            ["runtimeManifestHash"] = configurationEvidence.RuntimeManifestHash,
            ["reviewArtifactHash"] = reviewArtifactHash,
            ["reviewHash"] = reviewFingerprint.ReviewHash,
            ["contextHash"] = reviewFingerprint.ContextHash,
            ["reviewAutomationReceiptHash"] = reviewFingerprint.ReceiptHash,
            ["approvedCheckpointVersion"] = checkpointVersion,
            ["approvedCheckpointHash"] = checkpointHash,
            ["consumedGenerationPlanId"] = generationPlan.Document.PlanId,
            ["consumedGenerationPlanHash"] = generationPlan.ArtifactHash,
            ["consumedAtUtc"] = generationPlan.Document.ConsumedAtUtc!.Value.ToUniversalTime().ToString("O"),
            ["operationId"] = operationEvidence.Document.OperationId,
            ["operationHash"] = operationEvidence.ArtifactHash,
            ["cadReceiptHash"] = receiptEvidence.ArtifactHash,
            ["generationIdempotencyMarkerHash"] = generationMarker.ArtifactHash,
            ["generationIdempotencyMarkerName"] = generationMarker.ArtifactName,
            ["generationRequestBodyHash"] = markerRequestHash,
            ["requestHash"] = receipt.RequestHash,
            ["responseHash"] = receipt.ResponseHash,
            ["candidatePathHash"] = AgentWorkflowFileStore.TextHash(candidatePath),
            ["technicalStage"] = receipt.ResponseTechnicalStage,
            ["nativeErrorStatus"] = receipt.ResponseNativeErrorStatus,
            ["cleanupComplete"] = true,
            ["outputAbsent"] = true,
            ["candidateAbsent"] = true,
            ["normalizedBaselineAbsent"] = true,
            ["operationsAndLeasesQuiescent"] = true
        };
        return new(evidence, checkpointVersion, checkpointHash, null);
    }

    private static ErrorCategory ParseCategory(string? value, ErrorCategory fallback) =>
        Enum.TryParse<ErrorCategory>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    private static string? NullableStringValue(JsonObject? value, string name) =>
        value?[name] is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static bool TryBooleanValue(JsonObject? value, string name, out bool result)
    {
        result = false;
        return value?[name] is JsonValue item && item.TryGetValue<bool>(out result);
    }

    private static string CanonicalHash(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return CanonicalJsonV1.Fingerprint(document.RootElement);
    }

    private static async Task<string?> ArtifactHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return AgentWorkflowFileStore.Sha256(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<(T? Document, string? ArtifactHash)> LoadExactEvidenceAsync<T>(
        string path,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return (null, null);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<T>(bytes, Json), AgentWorkflowFileStore.Sha256(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    private static async Task<(JsonObject? Document, string? ArtifactHash)> LoadExactJsonObjectEvidenceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return (null, null);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return (JsonNode.Parse(bytes)?.AsObject(), AgentWorkflowFileStore.Sha256(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    private static async Task<(JobCheckpoint? Document, string? ArtifactHash)> LoadCheckpointEvidenceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return (null, null);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<JobCheckpoint>(bytes, Json), AgentWorkflowFileStore.Sha256(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    private async Task<ActiveConfigurationEvidence> LoadActiveConfigurationEvidenceAsync(CancellationToken cancellationToken)
    {
        static ActiveConfigurationEvidence Invalid() => new(null, null, null,
            new("GENERATION_RECONCILIATION_CONFIGURATION_DRIFT", "The active configuration or runtime manifest changed."));
        try
        {
            var path = _configuration.ActiveConfigurationPath;
            if (path is null || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return Invalid();
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var activeHash = AgentWorkflowFileStore.Sha256(bytes);
            if (!string.Equals(activeHash, _configuration.ActiveConfigurationHash, StringComparison.Ordinal)) return Invalid();
            var node = JsonNode.Parse(bytes)?.AsObject();
            if (node is null) return Invalid();
            var runtimeValues = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hostExecutablePath"] = _configuration.HostExecutablePath,
                ["readOnlyBundleDirectory"] = _configuration.ReadOnlyBundleDirectory,
                ["readOnlyAdapterAssemblyPath"] = _configuration.ReadOnlyAdapterAssemblyPath,
                ["writeBundleDirectory"] = _configuration.WriteBundleDirectory,
                ["writeAdapterAssemblyPath"] = _configuration.WriteAdapterAssemblyPath
            };
            if (runtimeValues.Any(item => !string.Equals(node[item.Key]?.GetValue<string>(), item.Value,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return Invalid();

            var hostDirectory = Path.GetDirectoryName(_configuration.HostExecutablePath);
            var runtimeRoot = hostDirectory is null ? null : Directory.GetParent(hostDirectory)?.FullName;
            if (runtimeRoot is null || runtimeValues.Values.Any(value => !PathWithin(value, runtimeRoot))) return Invalid();
            var manifestPath = Path.Combine(runtimeRoot, "runtime-manifest.sha256.json");
            var manifestHash = await ArtifactHashAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (manifestHash is null) return Invalid();

            foreach (var key in runtimeValues.Keys) node.Remove(key);
            return new(activeHash, CanonicalHash(node), manifestHash, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Invalid();
        }
    }

    private static bool PathWithin(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<bool> ContainsArtifactHashesAsync(
        string directory,
        IReadOnlyCollection<string> expected,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return false;
        var remaining = expected.ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = await ArtifactHashAsync(path, cancellationToken).ConfigureAwait(false);
                if (hash is not null) remaining.Remove(hash);
            }
            return remaining.Count == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool ProcessExists(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return false; }
    }

    private static bool CadProcessExists()
    {
        foreach (var name in new[] { "acad", "accoreconsole" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Any(process => !process.HasExited)) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    private static JsonObject ReconciledReviewResponse(JobDocument job, bool idempotent) => new()
    {
        ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
        ["jobId"] = job.JobId.ToString("D"),
        ["jobVersion"] = job.Version,
        ["state"] = job.State.ToString(),
        ["idempotentReplay"] = idempotent
    };

    private static string TranslationConsent(string planId) => $"I_APPROVE_EXTERNAL_PROCESSING_FOR_PLAN:{planId}";
    public static string BulkApproval(Guid jobId, long version) => $"I_APPROVE_BULK_REVIEW_FOR_JOB:{jobId:D}:V{version}";
    private static string GenerationApproval(Guid jobId, long version, string bindingHash) =>
        $"I_APPROVE_DWG_GENERATION_FOR_JOB:{jobId:D}:V{version}:{bindingHash}";
    private static string GenerationReconciliationConsent(Guid jobId, long version, string bindingHash) =>
        $"I_APPROVE_GENERATION_RECONCILIATION_FOR_JOB:{jobId:D}:V{version}:{bindingHash}";
    private static string ReconciliationBindingHash(Guid jobId, long version, string sourceHash, long sourceBytes,
        string outputPath, long reviewVersion, string reviewHash) => AgentWorkflowFileStore.TextHash(
            $"{jobId:D}|{version}|{sourceHash}|{sourceBytes}|{outputPath}|{reviewVersion}|{reviewHash}");
    private static string OutputBindingHash(Guid jobId, long version, string outputPath, string reviewHash) =>
        AgentWorkflowFileStore.TextHash($"{jobId:D}|{version}|{outputPath}|{reviewHash}");
    private static string StringValue(JsonObject value, string name) => value[name]?.GetValue<string>() ?? string.Empty;
    private static JsonObject PolicyVersions() => new()
    {
        ["workflow"] = AgentWorkflowContract.StateMachineVersion,
        ["approval"] = AgentWorkflowContract.ApprovalPolicyVersion,
        ["path"] = AgentWorkflowContract.PathPolicyVersion,
        ["routing"] = TranslationRouting.PolicyVersion,
        ["cadValidation"] = "VisualStrictV2",
        ["prompt"] = TranslationReviewWorkflow.ContextualPromptTemplateVersion,
        ["contract"] = ContractV1.SchemaVersion
    };

    private AgentEnvelope? Replay(string command, IdempotencyBeginResult result)
    {
        if (result.Kind == IdempotencyBeginKind.Acquired) return null;
        if (result.Kind == IdempotencyBeginKind.Completed)
        {
            var response = result.Response!.DeepClone().AsObject();
            response["idempotentReplay"] = true;
            return Success(command, response);
        }
        return result.Kind == IdempotencyBeginKind.Pending
            ? Failure(command, "IDEMPOTENCY_PENDING", "An operation with this idempotency key is already pending.", true)
            : Failure(command, "IDEMPOTENCY_CONFLICT", "The idempotency key was used with divergent input.");
    }

    private AgentEnvelope Success(string command, JsonNode data) =>
        new(AgentQueryService.ResponseSchema, true, command, _utcNow(), data, null);
    private AgentEnvelope Failure(string command, AgentError error) =>
        new(AgentQueryService.ResponseSchema, false, command, _utcNow(), null, error);
    private AgentEnvelope Failure(string command, ContractError error) =>
        Failure(command, new AgentError(NormalizeError(error.Code), error.Message, error.Retryable));
    private AgentEnvelope Failure(string command, string code, string message, bool retryable = false) =>
        Failure(command, new AgentError(code, message, retryable));
    private static string NormalizeError(string code) => code switch
    {
        "JOB_VERSION_CONFLICT" => "JOB_STATE_CONFLICT",
        "OPENAI_BASE_MODEL_UNAVAILABLE" => "MODEL_ACCESS_DENIED",
        _ => code
    };
}
