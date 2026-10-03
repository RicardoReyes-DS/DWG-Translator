using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Application;

public sealed class DwgTranslationJobCoordinator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly IJobStore _jobs;
    private readonly ITranslationReviewStore _reviews;
    private readonly CadReadWorkflow _read;
    private readonly TranslationReviewWorkflow _translation;
    private readonly CadWriteWorkflow _write;
    private readonly IClock _clock;

    public DwgTranslationJobCoordinator(
        IJobStore jobs,
        ITranslationReviewStore reviews,
        CadReadWorkflow read,
        TranslationReviewWorkflow translation,
        CadWriteWorkflow write,
        IClock clock)
    {
        _jobs = jobs;
        _reviews = reviews;
        _read = read;
        _translation = translation;
        _write = write;
        _clock = clock;
    }

    public async Task<Result<JobDocument>> CreateAsync(
        DwgTranslationJobSpecification specification,
        CancellationToken cancellationToken)
    {
        var validated = ValidateSpecification(specification);
        if (!validated.IsSuccess) return Results.Failure<JobDocument>(validated.Error!);
        var configurationHash = ConfigurationHash(specification);
        var jobId = Guid.NewGuid();
        var data = new DwgTranslationJobData(specification, configurationHash, null, null, null);
        var document = new JobDocument(jobId, JobState.Draft, 0, _clock.UtcNow, Serialize(data));
        var created = await _jobs.CreateAsync(document, cancellationToken);
        if (!created.IsSuccess) return created;
        var audited = await AuditAsync(created.Value!, null, JobState.Draft, cancellationToken);
        return audited.IsSuccess ? created : Results.Failure<JobDocument>(audited.Error!);
    }

    public async Task<Result<JobDocument>> InspectAndExtractAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var loaded = await LoadInStateAsync(jobId, JobState.Draft, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value!;
        var inspecting = await TransitionAsync(current, JobState.Inspecting, current.Data, cancellationToken);
        if (!inspecting.IsSuccess) return inspecting;
        var data = Deserialize(inspecting.Value!.Data);
        if (!data.IsSuccess) return await FailAsync(inspecting.Value, data.Error!, cancellationToken);

        Result<CadReadResult> read;
        try
        {
            read = await _read.InspectAndExtractAsync(
                jobId,
                data.Value!.Specification.SourcePath,
                data.Value.Specification.SourceHash,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await TransitionAsync(inspecting.Value, JobState.Cancelled, inspecting.Value.Data, CancellationToken.None);
        }
        if (!read.IsSuccess) return await FailAsync(inspecting.Value, read.Error!, cancellationToken);
        if (read.Value!.Segments.Count == 0)
            return await FailAsync(inspecting.Value, Error("CAD_NO_TRANSLATABLE_TEXT", ErrorCategory.Unsupported, "No direct TEXT or MTEXT segments are available."), cancellationToken);

        var nextData = data.Value with { Inspection = read.Value.Inspection, Segments = read.Value.Segments };
        var extracted = await TransitionAsync(inspecting.Value, JobState.Extracted, Serialize(nextData), cancellationToken);
        return extracted.IsSuccess
            ? await CheckpointAsync(extracted.Value!, nextData, cancellationToken)
            : extracted;
    }

    public async Task<Result<JobDocument>> TranslateAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var loaded = await LoadInStateAsync(jobId, JobState.Extracted, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value!;
        var translating = await TransitionAsync(current, JobState.Translating, current.Data, cancellationToken);
        if (!translating.IsSuccess) return translating;
        return await TranslateCoreAsync(translating.Value!, cancellationToken);
    }

    public Task<Result<JobDocument>> ResumeTranslationAsync(Guid jobId, CancellationToken cancellationToken) =>
        ResumeTranslationAsync(jobId, expectedJobVersion: null, scopeTransition: null, cancellationToken);

    public Task<Result<JobDocument>> ResumeTranslationAsync(
        Guid jobId,
        long expectedJobVersion,
        CancellationToken cancellationToken) =>
        ResumeTranslationAsync(jobId, (long?)expectedJobVersion, null, cancellationToken);

    public Task<Result<JobDocument>> ResumeTranslationAsync(
        Guid jobId,
        long expectedJobVersion,
        ReviewAutomationScopeTransition? scopeTransition,
        CancellationToken cancellationToken) =>
        ResumeTranslationAsync(jobId, (long?)expectedJobVersion, scopeTransition, cancellationToken);

    private async Task<Result<JobDocument>> ResumeTranslationAsync(
        Guid jobId,
        long? expectedJobVersion,
        ReviewAutomationScopeTransition? scopeTransition,
        CancellationToken cancellationToken)
    {
        var loaded = await _jobs.LoadAsync(jobId, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value!;
        var projectedResume = expectedJobVersion is not null && scopeTransition is not null &&
            current.State == JobState.Translating && current.Version == expectedJobVersion.Value + 1;
        if (expectedJobVersion is not null &&
            (current.State != JobState.Failed || current.Version != expectedJobVersion.Value) && !projectedResume)
            return Failure("JOB_STATE_CONFLICT", ErrorCategory.Concurrency,
                "The failed translation checkpoint changed before recovery started.");
        if (current.State == JobState.Failed)
        {
            var failure = current.Data["failure"] as JsonObject;
            var code = failure?["code"]?.GetValue<string>();
            var translationStage = string.Equals(
                failure?["stage"]?.GetValue<string>(), "Translation", StringComparison.Ordinal);
            var retryableCheckpoint = failure?["retryable"]?.GetValue<bool>() is true;
            var tokenIntegrityCheckpoint = string.Equals(code, "TOKEN_INTEGRITY_FAILED", StringComparison.Ordinal);
            if (!translationStage || !retryableCheckpoint && !tokenIntegrityCheckpoint)
                return Failure("RECOVERY_TRANSLATION_NOT_RETRYABLE", ErrorCategory.Concurrency, "The failed job has no retryable translation checkpoint.");
            var restoredData = current.Data.DeepClone().AsObject();
            var scopeWasRebound = false;
            if (scopeTransition is not null)
            {
                var validated = await ValidateScopeTransitionAsync(current, scopeTransition,
                    requireCompleteReviewCoverage: false, cancellationToken).ConfigureAwait(false);
                if (!validated.IsSuccess) return Results.Failure<JobDocument>(validated.Error!);
                var currentScope = validated.Value!.Data.Specification.ReviewAutomationScope!;
                scopeWasRebound = !ScopeEquals(currentScope, scopeTransition.TargetScope);
                if (scopeWasRebound)
                    restoredData = Serialize(RebindScope(validated.Value.Data, scopeTransition,
                        current.Version + 1, validated.Value.ReviewFingerprint));
            }
            restoredData.Remove("failure");
            restoredData.Remove("agentFailure");
            var restored = current with { State = JobState.Translating, Version = current.Version + 1, UpdatedAtUtc = _clock.UtcNow, Data = restoredData };
            var saved = await _jobs.SaveAsync(restored, current.Version, cancellationToken);
            if (!saved.IsSuccess) return saved;
            current = saved.Value!;
            // The CAS is already durable. Finish its small local projection even if the
            // caller is cancelled so recovery cannot be stranded in Translating.
            var projected = await ProjectTranslationRecoveryAsync(current, scopeTransition, scopeWasRebound,
                CancellationToken.None).ConfigureAwait(false);
            if (!projected.IsSuccess) return projected;
        }
        else if (projectedResume)
        {
            var validated = await ValidateScopeTransitionAsync(current, scopeTransition!,
                requireCompleteReviewCoverage: false, cancellationToken).ConfigureAwait(false);
            if (!validated.IsSuccess) return Results.Failure<JobDocument>(validated.Error!);
            if (!ScopeEquals(validated.Value!.Data.Specification.ReviewAutomationScope!, scopeTransition!.TargetScope) ||
                current.Data.ContainsKey("failure") || current.Data.ContainsKey("agentFailure"))
                return Failure("RECOVERY_TRANSLATION_PROJECTION_CONFLICT", ErrorCategory.Integrity,
                    "The translating recovery projection does not match the sealed target lineage.");
            var projected = await ProjectTranslationRecoveryAsync(current, scopeTransition,
                scopeWasRebound: true, CancellationToken.None).ConfigureAwait(false);
            if (!projected.IsSuccess) return projected;
        }
        else if (current.State != JobState.Translating)
        {
            return Failure("JOB_STATE_CONFLICT", ErrorCategory.Concurrency, $"Expected Translating or a retryable translation failure but found {current.State}.");
        }
        return await TranslateCoreAsync(current, cancellationToken);
    }

    /// <summary>
    /// Rebinds an adopted ReviewRequired job to its sealed recovery lineage. The
    /// transition is internal-only, optimistic, and idempotent when the exact
    /// target scope is already present.
    /// </summary>
    public async Task<Result<JobDocument>> RebindReviewAutomationScopeAsync(
        Guid jobId,
        long expectedJobVersion,
        ReviewAutomationScopeTransition scopeTransition,
        CancellationToken cancellationToken)
    {
        var loaded = await _jobs.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value!;
        if (current.State != JobState.ReviewRequired ||
            current.Version != expectedJobVersion && current.Version != expectedJobVersion + 1)
            return Failure("JOB_STATE_CONFLICT", ErrorCategory.Concurrency,
                "The review checkpoint changed before recovery scope rebinding.");

        var validated = await ValidateScopeTransitionAsync(current, scopeTransition,
            requireCompleteReviewCoverage: true, cancellationToken).ConfigureAwait(false);
        if (!validated.IsSuccess) return Results.Failure<JobDocument>(validated.Error!);
        var currentScope = validated.Value!.Data.Specification.ReviewAutomationScope!;
        DwgTranslationJobData reboundData;
        if (ScopeEquals(currentScope, scopeTransition.ExpectedScope))
        {
            if (current.Version != expectedJobVersion)
                return Failure("JOB_STATE_CONFLICT", ErrorCategory.Concurrency,
                    "The review checkpoint changed before recovery scope rebinding.");
            reboundData = RebindScope(validated.Value.Data, scopeTransition, current.Version + 1,
                validated.Value.ReviewFingerprint);
            var rebound = current with
            {
                Version = current.Version + 1,
                UpdatedAtUtc = _clock.UtcNow,
                Data = Serialize(reboundData)
            };
            var saved = await _jobs.SaveAsync(rebound, current.Version, cancellationToken).ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                if (!string.Equals(saved.Error?.Code, "JOB_VERSION_CONFLICT", StringComparison.Ordinal)) return saved;
                var raced = await _jobs.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (!raced.IsSuccess || raced.Value!.State != JobState.ReviewRequired ||
                    raced.Value.Version != expectedJobVersion + 1)
                    return saved;
                current = raced.Value;
                validated = await ValidateScopeTransitionAsync(current, scopeTransition,
                    requireCompleteReviewCoverage: true, cancellationToken).ConfigureAwait(false);
                if (!validated.IsSuccess ||
                    !ScopeEquals(validated.Value!.Data.Specification.ReviewAutomationScope!, scopeTransition.TargetScope))
                    return saved;
                reboundData = validated.Value.Data;
            }
            else
            {
                current = saved.Value!;
            }
        }
        else
        {
            reboundData = validated.Value.Data;
            if (current.Version > reboundData.RecoveryReviewScopeSeal!.TransitionJobVersion)
                return current.Version == reboundData.RecoveryReviewScopeSeal.TransitionJobVersion + 1
                    ? Results.Success(current)
                    : Failure("RECOVERY_REVIEW_SCOPE_BINDING_CHANGED", ErrorCategory.Integrity,
                        "The rebound job advanced outside the single trusted review-completion transition.");
        }

        // Once the scope CAS wins, audit/checkpoint projection is compensating local
        // durability work and must not be abandoned with the caller's cancellation.
        return await ProjectReviewScopeRebindAsync(current, reboundData, scopeTransition,
            CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<Result<JobDocument>> RevalidateApprovedContextAsync(
        Guid jobId,
        long expectedJobVersion,
        ApprovedContextRevalidationTransition transition,
        CancellationToken cancellationToken)
    {
        if (transition is null || !ApprovedContextRevalidationPolicy.IsValid(transition.Binding) ||
            transition.Binding.ExpectedJobVersion != expectedJobVersion ||
            !ValidScope(transition.ExpectedScope) || !ValidScope(transition.TargetScope) ||
            transition.ExpectedScope.BatchId == transition.TargetScope.BatchId ||
            transition.ExpectedScope.PolicyVersion != ReviewAutomationPolicy.ContextualAgentCreateNew ||
            transition.TargetScope.PolicyVersion != ReviewAutomationPolicy.ContextualAgentCreateNew)
            return Failure("RECOVERY_CONTEXT_REVALIDATION_BINDING_INVALID", ErrorCategory.Integrity,
                "The approved-context recovery binding is invalid.");

        var loaded = await _jobs.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value!;
        if (current.State == JobState.ReviewRequired && current.Version == expectedJobVersion + 1)
            return await CompleteApprovedContextRevalidationAsync(current, transition, CancellationToken.None)
                .ConfigureAwait(false);
        if (current.State != JobState.Approved || current.Version != expectedJobVersion)
            return Failure("JOB_STATE_CONFLICT", ErrorCategory.Concurrency,
                "The approved job changed before semantic-context revalidation.");

        var parsed = Deserialize(current.Data);
        if (!parsed.IsSuccess || parsed.Value!.Segments is not { Count: > 0 } segments)
            return Results.Failure<JobDocument>(parsed.Error ?? Error("RECOVERY_CONTEXT_REVALIDATION_EVIDENCE_INVALID",
                ErrorCategory.Integrity, "The approved job payload is incomplete."));
        var data = parsed.Value;
        var scope = data.Specification.ReviewAutomationScope;
        if (scope is null || !ScopeEquals(scope, transition.ExpectedScope) ||
            !PathsEqual(data.Specification.SourcePath, transition.SourcePath) ||
            !PathsEqual(data.Specification.OutputPath, transition.OutputPath) ||
            !string.Equals(data.Specification.SourceHash, transition.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(data.ConfigurationHash, ConfigurationHash(data.Specification), StringComparison.Ordinal))
            return Failure("RECOVERY_CONTEXT_REVALIDATION_BINDING_CHANGED", ErrorCategory.Integrity,
                "The approved job scope, source, output, or configuration changed.");

        var aggregate = CadSemanticContextBuilder.AggregateHash(segments);
        var fallbackCandidateCount = segments.Count(segment => segment.SemanticContext is
        { Discipline: "Unknown", DisciplineConflict: false, DisciplineEvidence.Count: 0 });
        if (!aggregate.IsSuccess ||
            segments.Any(segment => segment.SemanticContext?.Version != transition.Binding.ExpectedContextPolicyVersion) ||
            fallbackCandidateCount != transition.Binding.ExpectedFallbackCandidateCount ||
            segments.Count - fallbackCandidateCount != transition.Binding.ExpectedLocalEvidenceCount ||
            !string.Equals(aggregate.Value, transition.Binding.ExpectedContextHash, StringComparison.Ordinal))
            return Failure("RECOVERY_CONTEXT_REVALIDATION_CONTEXT_CHANGED", ErrorCategory.Integrity,
                "The approved semantic-context set changed before recovery.");
        var review = await _reviews.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess) return Results.Failure<JobDocument>(review.Error!);
        var snapshot = review.Value!;
        var fingerprint = TranslationReviewFingerprint.Create(snapshot);
        if (!ValidApprovedContextReview(snapshot, segments, data, transition.ExpectedScope) ||
            snapshot.Version != transition.Binding.ExpectedReviewVersion ||
            snapshot.Rows.Count != transition.Binding.RowCount ||
            !string.Equals(fingerprint.ReviewHash, transition.Binding.ExpectedReviewHash, StringComparison.Ordinal) ||
            !string.Equals(fingerprint.ReceiptHash, transition.Binding.ExpectedReceiptHash, StringComparison.Ordinal))
            return Failure("RECOVERY_CONTEXT_REVALIDATION_REVIEW_CHANGED", ErrorCategory.Integrity,
                "The approved review or its withdrawn authority changed before recovery.");

        var upgraded = CadSemanticContextBuilder.UpgradeOneOneToOneTwo(
            segments, transition.Binding.ManifestBoundBasename);
        if (!upgraded.IsSuccess) return Results.Failure<JobDocument>(upgraded.Error!);
        var upgradedAggregate = CadSemanticContextBuilder.AggregateHash(upgraded.Value!);
        var conflictIds = upgraded.Value!.Where(segment => segment.SemanticContext!.Signals.Contains(
                SegmentContextResolutionPolicy.VerticalSignal, StringComparer.Ordinal))
            .Select(segment => segment.SegmentId).Order(StringComparer.Ordinal).ToArray();
        if (!upgradedAggregate.IsSuccess ||
            !string.Equals(upgradedAggregate.Value, transition.Binding.TargetContextHash, StringComparison.Ordinal) ||
            !conflictIds.SequenceEqual(transition.Binding.RequiredResolutionSegmentIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return Failure("RECOVERY_CONTEXT_REVALIDATION_TARGET_MISMATCH", ErrorCategory.Integrity,
                "The rebuilt semantic context does not match the sealed target.");

        var marker = new ApprovedContextRevalidationReceipt(
            transition.Binding.BindingHash, expectedJobVersion, expectedJobVersion + 1,
            snapshot.Version, snapshot.Version + 1, fingerprint.ReviewHash, fingerprint.ReceiptHash!,
            transition.Binding.ExpectedContextHash, transition.Binding.TargetContextHash,
            transition.Binding.ExpectedContextPolicyVersion, transition.Binding.TargetContextPolicyVersion,
            conflictIds);
        var specification = data.Specification with { ReviewAutomationScope = transition.TargetScope };
        var nextData = data with
        {
            Specification = specification,
            ConfigurationHash = ConfigurationHash(specification),
            Segments = upgraded.Value,
            ApprovedContextRevalidationReceipt = marker,
            RecoveryReviewScopeSeal = null,
            GenerationFailureReconciliationReceipt = null
        };
        var reopened = current with
        {
            State = JobState.ReviewRequired,
            Version = current.Version + 1,
            UpdatedAtUtc = _clock.UtcNow,
            Data = Serialize(nextData)
        };
        var saved = await _jobs.SaveAsync(reopened, current.Version, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            var raced = await _jobs.LoadAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (!raced.IsSuccess || raced.Value!.State != JobState.ReviewRequired ||
                raced.Value.Version != expectedJobVersion + 1)
                return saved;
            reopened = raced.Value;
        }
        else reopened = saved.Value!;
        return await CompleteApprovedContextRevalidationAsync(reopened, transition, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<Result<JobDocument>> TranslateCoreAsync(JobDocument translating, CancellationToken cancellationToken)
    {
        var data = Deserialize(translating.Data);
        if (!data.IsSuccess || data.Value!.Segments is null)
            return await FailAsync(translating, data.Error ?? Error("JOB_SEGMENTS_MISSING", ErrorCategory.Storage, "Extracted segments are missing."), cancellationToken, "Translation");

        Result<TranslationReviewSnapshot> review;
        try
        {
            review = await _translation.TranslateAsync(
                translating.JobId,
                data.Value.Segments,
                data.Value.Specification.SourceLanguage,
                data.Value.Specification.TargetLanguage,
                data.Value.Specification.PromptTemplateVersion,
                data.Value.Specification.Glossary,
                data.Value.Specification.Routing,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await TransitionAsync(translating, JobState.Cancelled, translating.Data, CancellationToken.None);
        }
        catch (Exception exception)
        {
            return await FailAsync(
                translating,
                new ContractError("TRANSLATION_UNEXPECTED_FAILURE", ErrorCategory.Translation, "Translation stopped unexpectedly and can be resumed from its durable checkpoint.", true),
                CancellationToken.None,
                "Translation",
                exception.GetType().FullName);
        }
        if (!review.IsSuccess) return await FailAsync(translating, review.Error!, cancellationToken, "Translation");

        var requiredData = BindCurrentReview(data.Value, TranslationReviewFingerprint.Create(review.Value!));
        if (!requiredData.IsSuccess) return Results.Failure<JobDocument>(requiredData.Error!);
        var required = await TransitionAsync(translating, JobState.ReviewRequired,
            Serialize(requiredData.Value!), cancellationToken);
        return required.IsSuccess
            ? await CheckpointAsync(required.Value!, requiredData.Value!, cancellationToken,
                reviewSeal: requiredData.Value!.RecoveryReviewScopeSeal)
            : required;
    }

    public async Task<Result<JobDocument>> ApproveAsync(
        Guid jobId,
        IReadOnlyList<ReviewDecisionInput> decisions,
        CancellationToken cancellationToken) =>
        await ApproveAsync(jobId, decisions, null, cancellationToken);

    public async Task<Result<JobDocument>> ApproveAsync(
        Guid jobId,
        IReadOnlyList<ReviewDecisionInput> decisions,
        ReviewAutomationReceipt? automationAuthority,
        CancellationToken cancellationToken)
        => await ApproveAsync(jobId, decisions, null, automationAuthority, cancellationToken);

    public async Task<Result<JobDocument>> ApproveAsync(
        Guid jobId,
        IReadOnlyList<ReviewDecisionInput> decisions,
        long? expectedReviewVersion,
        ReviewAutomationReceipt? automationAuthority,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadInStateAsync(jobId, JobState.ReviewRequired, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var data = Deserialize(loaded.Value!.Data);
        if (!data.IsSuccess || data.Value!.Segments is null)
            return Results.Failure<JobDocument>(data.Error ?? Error("JOB_SEGMENTS_MISSING", ErrorCategory.Storage, "Extracted segments are missing."));
        var current = await _reviews.LoadAsync(jobId, cancellationToken);
        if (!current.IsSuccess) return Results.Failure<JobDocument>(current.Error!);
        if (expectedReviewVersion is not null && current.Value!.Version != expectedReviewVersion.Value)
            return Failure("REVIEW_VERSION_CONFLICT", ErrorCategory.Concurrency,
                "The durable review version changed before approval.");
        if (automationAuthority is not null && !ValidAutomationAuthority(
                automationAuthority, current.Value!, data.Value.Specification.ReviewAutomationScope,
                data.Value.Segments))
            return Failure("REVIEW_AUTOMATION_AUTHORITY_INVALID", ErrorCategory.Integrity,
                "The review automation authority does not match the durable job, batch, manifest, or semantic context.");
        if (decisions is null || decisions.Count != current.Value!.Rows.Count ||
            decisions.Select(decision => decision.SegmentId).Distinct(StringComparer.Ordinal).Count() != decisions.Count)
            return Failure("REVIEW_DECISION_SET_MISMATCH", ErrorCategory.Input, "One unique decision is required for every review row.");

        var decisionsById = decisions.ToDictionary(decision => decision.SegmentId, StringComparer.Ordinal);
        var segmentsById = data.Value.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var rows = new List<ReviewRowSnapshot>(current.Value.Rows.Count);
        foreach (var row in current.Value.Rows)
        {
            if (!decisionsById.TryGetValue(row.SegmentId, out var decision) || !segmentsById.TryGetValue(row.SegmentId, out var segment))
                return Failure("REVIEW_DECISION_SET_MISMATCH", ErrorCategory.Input, "Review decisions must match extracted segments exactly.");
            if (decision.ExclusionReason is not null)
            {
                var excluded = HumanReviewPolicy.Exclude(segment, decision.ExclusionReason);
                if (!excluded.IsSuccess) return Results.Failure<JobDocument>(excluded.Error!);
                rows.Add(row with { State = SegmentState.Excluded, FinalText = row.ProposedText, ExclusionReason = decision.ExclusionReason });
            }
            else
            {
                var approved = HumanReviewPolicy.Approve(segment, new AcceptedTranslation(row.SegmentId, row.ProposedText), decision.FinalText ?? string.Empty);
                if (!approved.IsSuccess) return Results.Failure<JobDocument>(approved.Error!);
                rows.Add(row with { State = SegmentState.Approved, FinalText = approved.Value!.FinalText!, ExclusionReason = null });
            }
        }

        var snapshot = current.Value with
        {
            Version = current.Value.Version + 1,
            UpdatedAtUtc = _clock.UtcNow,
            Rows = rows,
            ReviewAutomationReceipt = automationAuthority
        };
        var saved = await _reviews.SaveAsync(snapshot, current.Value.Version, cancellationToken);
        if (!saved.IsSuccess) return Results.Failure<JobDocument>(saved.Error!);
        var coverage = ApprovalPolicy.Evaluate(rows.Select(ToSegmentReview));
        if (!coverage.IsSuccess) return Results.Failure<JobDocument>(Error("APPROVAL_COVERAGE_INVALID", ErrorCategory.Integrity, "Every segment must be approved or deliberately excluded."));

        var approvedJob = await TransitionAsync(loaded.Value, JobState.Approved, loaded.Value.Data, cancellationToken, coverage.Value);
        return approvedJob.IsSuccess
            ? await CheckpointAsync(approvedJob.Value!, data.Value, cancellationToken)
            : approvedJob;
    }

    public Task<Result<JobDocument>> GenerateAsync(Guid jobId, CancellationToken cancellationToken) =>
        GenerateAsync(jobId, null, cancellationToken);

    public async Task<Result<JobDocument>> ReviseApprovedReviewAsync(Guid jobId, long expectedJobVersion,
        IReadOnlyList<ReviewDecisionInput> decisions, long expectedReviewVersion,
        ReviewAutomationReceipt authority, CancellationToken cancellationToken)
    {
        var loaded = await LoadInStateAsync(jobId, JobState.Approved, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var job = loaded.Value!;
        if (job.Version != expectedJobVersion)
            return Failure("JOB_STATE_CONFLICT", ErrorCategory.Concurrency,
                "The approved job changed before correction.");
        var parsed = Deserialize(job.Data);
        if (!parsed.IsSuccess || parsed.Value!.Segments is not { Count: > 0 } segments)
            return Results.Failure<JobDocument>(parsed.Error ?? Error("JOB_SEGMENTS_MISSING",
                ErrorCategory.Storage, "Extracted segments are missing."));
        var reviewResult = await _reviews.LoadAsync(jobId, cancellationToken);
        if (!reviewResult.IsSuccess) return Results.Failure<JobDocument>(reviewResult.Error!);
        var review = reviewResult.Value!;
        if (review.Version != expectedReviewVersion || review.Rows.Count != segments.Count ||
            review.ReviewAutomationReceipt is null ||
            review.Rows.Any(row => row.State != SegmentState.Approved || row.FinalText is null) ||
            !ValidAutomationAuthority(authority, review, parsed.Value.Specification.ReviewAutomationScope, segments) ||
            authority.ReviewerReportHash == review.ReviewAutomationReceipt.ReviewerReportHash ||
            authority.QaReportHash == review.ReviewAutomationReceipt.QaReportHash ||
            decisions.Count != review.Rows.Count ||
            decisions.Select(item => item.SegmentId).Distinct(StringComparer.Ordinal).Count() != decisions.Count)
            return Failure("APPROVED_REVIEW_CORRECTION_BINDING_INVALID", ErrorCategory.Integrity,
                "The approved review, new authority, or complete decision set changed.");

        var byId = decisions.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        var segmentsById = segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        var changed = 0;
        var rows = new List<ReviewRowSnapshot>(review.Rows.Count);
        foreach (var row in review.Rows)
        {
            if (!byId.TryGetValue(row.SegmentId, out var decision) ||
                !segmentsById.TryGetValue(row.SegmentId, out var segment) ||
                decision.ExclusionReason is not null || string.IsNullOrEmpty(decision.FinalText))
                return Failure("APPROVED_REVIEW_CORRECTION_SCOPE_INVALID", ErrorCategory.Input,
                    "The correction must retain every approved segment and change one final text.");
            var approved = HumanReviewPolicy.Approve(segment,
                new AcceptedTranslation(row.SegmentId, row.ProposedText), decision.FinalText);
            if (!approved.IsSuccess) return Results.Failure<JobDocument>(approved.Error!);
            if (!string.Equals(row.FinalText, decision.FinalText, StringComparison.Ordinal))
            {
                changed++;
                if (ArchitecturalMepTerminologyPolicy.Validate(segment.SourceText,
                    decision.FinalText, Array.Empty<TerminologyMatch>()).Count != 0)
                    return Failure("APPROVED_REVIEW_CORRECTION_INVARIANT_FAILED", ErrorCategory.Integrity,
                        "The changed final text failed a prewrite invariant.");
            }
            rows.Add(row with { State = SegmentState.Approved, FinalText = decision.FinalText,
                ExclusionReason = null });
        }
        if (changed != 1)
            return Failure("APPROVED_REVIEW_CORRECTION_SCOPE_INVALID", ErrorCategory.Input,
                "Exactly one final text must change in this correction.");

        var revised = review with
        {
            Version = review.Version + 1,
            UpdatedAtUtc = _clock.UtcNow,
            Rows = rows,
            ReviewAutomationReceipt = authority
        };
        var reboundData = BindCurrentReview(parsed.Value, TranslationReviewFingerprint.Create(revised));
        if (!reboundData.IsSuccess) return Results.Failure<JobDocument>(reboundData.Error!);
        var savedReview = await _reviews.SaveAsync(revised, review.Version, cancellationToken);
        if (!savedReview.IsSuccess) return Results.Failure<JobDocument>(savedReview.Error!);
        var updated = job with
        {
            Version = job.Version + 1,
            UpdatedAtUtc = _clock.UtcNow,
            Data = Serialize(reboundData.Value!)
        };
        var savedJob = await _jobs.SaveAsync(updated, job.Version, cancellationToken);
        if (!savedJob.IsSuccess) return savedJob;
        var before = TranslationReviewFingerprint.Create(review);
        var after = TranslationReviewFingerprint.Create(revised);
        var audit = await _jobs.AppendAuditAsync(new AuditRecord(Guid.NewGuid(), jobId, Guid.NewGuid(),
            _clock.UtcNow, "ApprovedReviewCorrected", new JsonObject
            {
                ["jobVersion"] = updated.Version,
                ["reviewVersion"] = revised.Version,
                ["previousReviewHash"] = before.ReviewHash,
                ["newReviewHash"] = after.ReviewHash,
                ["newReceiptHash"] = after.ReceiptHash,
                ["changedSegments"] = 1
            }), cancellationToken);
        if (!audit.IsSuccess) return Results.Failure<JobDocument>(audit.Error!);
        return await CheckpointAsync(savedJob.Value!, reboundData.Value!, cancellationToken,
            reviewSeal: reboundData.Value!.RecoveryReviewScopeSeal);
    }

    public async Task<Result<JobDocument>> GenerateAsync(
        Guid jobId,
        TranslationReviewFingerprint? expectedReview,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadInStateAsync(jobId, JobState.Approved, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var review = await _reviews.LoadAsync(jobId, cancellationToken);
        if (!review.IsSuccess) return Results.Failure<JobDocument>(review.Error!);
        var data = Deserialize(loaded.Value!.Data);
        if (!data.IsSuccess || data.Value!.Segments is null)
            return Results.Failure<JobDocument>(data.Error ?? Error("JOB_SEGMENTS_MISSING", ErrorCategory.Storage, "Extracted segments are missing."));
        if (expectedReview is not null && TranslationReviewFingerprint.Create(review.Value!) != expectedReview)
            return Failure("GENERATION_REVIEW_BINDING_MISMATCH", ErrorCategory.Integrity,
                "The approved review, semantic context, or automation receipt changed after generation planning.");

        var writing = await TransitionAsync(loaded.Value, JobState.Writing, loaded.Value.Data, cancellationToken);
        if (!writing.IsSuccess) return writing;
        Result<CadWriteResponsePayload> result;
        try
        {
            result = await _write.WriteAsync(
                jobId,
                data.Value.Specification.SourcePath,
                data.Value.Specification.OutputPath,
                data.Value.Specification.SourceHash,
                data.Value.Segments,
                review.Value!,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(
                writing.Value!,
                new ContractError("CAD_WRITE_CANCELLED_AT_SAFE_BOUNDARY", ErrorCategory.Transport, "CAD writing was cancelled at a safe boundary.", true),
                CancellationToken.None);
        }
        if (!result.IsSuccess) return await FailAsync(writing.Value!, result.Error!, cancellationToken, "Writing");

        var report = CadExerciseValidationReportFactory.Create(result.Value!);
        if (!report.IsSuccess) return await FailAsync(writing.Value!, report.Error!, cancellationToken);
        if (result.Value!.CandidateBytes <= 0)
            return await FailAsync(writing.Value!, Error("OUTPUT_SIZE_UNAVAILABLE", ErrorCategory.Storage,
                "The promoted output size could not be verified."), cancellationToken);
        var nextData = data.Value with
        {
            OutputHash = result.Value!.CandidateHash,
            ValidationReport = report.Value,
            OutputBytes = result.Value.CandidateBytes
        };
        var validating = await TransitionAsync(writing.Value!, JobState.Validating, Serialize(nextData), cancellationToken);
        if (!validating.IsSuccess) return validating;
        var validatingJob = validating.Value!;
        var completed = await TransitionAsync(validatingJob, JobState.Completed, validatingJob.Data, cancellationToken);
        return completed.IsSuccess
            ? await CheckpointAsync(completed.Value!, nextData, cancellationToken)
            : completed;
    }

    private async Task<Result<JobDocument>> LoadInStateAsync(Guid jobId, JobState expected, CancellationToken cancellationToken)
    {
        var loaded = await _jobs.LoadAsync(jobId, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        return loaded.Value!.State == expected
            ? loaded
            : Failure("JOB_STATE_CONFLICT", ErrorCategory.Concurrency, $"Expected {expected} but found {loaded.Value.State}.");
    }

    private async Task<Result<JobDocument>> TransitionAsync(
        JobDocument current,
        JobState next,
        JsonObject data,
        CancellationToken cancellationToken,
        ApprovalCoverage? coverage = null)
    {
        if (!JobLifecycle.CanTransition(current.State, next))
            return Failure("JOB_TRANSITION_INVALID", ErrorCategory.Concurrency, $"Transition {current.State} to {next} is not allowed.");
        var updated = current with { State = next, Version = current.Version + 1, UpdatedAtUtc = _clock.UtcNow, Data = data };
        var saved = await _jobs.SaveAsync(updated, current.Version, cancellationToken);
        if (!saved.IsSuccess) return saved;
        var audited = await AuditAsync(saved.Value!, current.State, next, cancellationToken, coverage);
        return audited.IsSuccess ? saved : Results.Failure<JobDocument>(audited.Error!);
    }

    private async Task<Result<JobDocument>> FailAsync(
        JobDocument current,
        ContractError reason,
        CancellationToken cancellationToken,
        string? stage = null,
        string? exceptionType = null)
    {
        var data = current.Data.DeepClone().AsObject();
        data["failure"] = new JsonObject
        {
            ["code"] = reason.Code,
            ["category"] = reason.Category.ToString(),
            ["retryable"] = reason.Retryable,
            ["stage"] = stage,
            ["exceptionType"] = exceptionType,
            ["diagnosticId"] = reason.DiagnosticId,
            ["technicalStage"] = reason.TechnicalStage,
            ["nativeErrorStatus"] = reason.NativeErrorStatus,
            ["invariantDiagnostics"] = reason.InvariantDiagnostics is null
                ? null
                : JsonSerializer.SerializeToNode(reason.InvariantDiagnostics, Json)
        };
        return await TransitionAsync(current, JobState.Failed, data, cancellationToken);
    }

    private async Task<Result<bool>> AuditRecoveryAsync(
        JobDocument document,
        JobState from,
        ReviewAutomationScopeTransition? scopeTransition,
        bool scopeWasRebound,
        RecoveryReviewScopeSeal? reviewSeal,
        CancellationToken cancellationToken)
    {
        var data = new JsonObject
        {
            ["jobId"] = document.JobId.ToString("D"),
            ["from"] = from.ToString(),
            ["to"] = JobState.Translating.ToString(),
            ["state"] = JobState.Translating.ToString()
        };
        if (scopeTransition is not null)
        {
            data["reviewScopeRebound"] = scopeWasRebound;
            data["fromBatchId"] = scopeTransition.ExpectedScope.BatchId.ToString("D");
            data["fromManifestHash"] = scopeTransition.ExpectedScope.ManifestHash;
            data["toBatchId"] = scopeTransition.TargetScope.BatchId.ToString("D");
            data["toManifestHash"] = scopeTransition.TargetScope.ManifestHash;
            data["policyVersion"] = scopeTransition.TargetScope.PolicyVersion;
            AddReviewSeal(data, reviewSeal!);
        }
        return await _jobs.AppendAuditAsync(new AuditRecord(
            StableGuid($"translation-recovery:event:{document.JobId:D}:{document.Version}"),
            document.JobId,
            StableGuid($"translation-recovery:correlation:{document.JobId:D}:{document.Version}"),
            document.UpdatedAtUtc.ToUniversalTime(),
            "JobTranslationRecoveryStarted", data), cancellationToken);
    }

    private async Task<Result<JobDocument>> CompleteApprovedContextRevalidationAsync(
        JobDocument document,
        ApprovedContextRevalidationTransition transition,
        CancellationToken cancellationToken)
    {
        try
        {
            var parsed = Deserialize(document.Data);
            var data = parsed.IsSuccess ? parsed.Value : null;
            var marker = data?.ApprovedContextRevalidationReceipt;
            if (document.State != JobState.ReviewRequired ||
                document.Version != transition.Binding.ExpectedJobVersion + 1 || data?.Segments is not { Count: > 0 } segments ||
                marker is null || marker.BindingHash != transition.Binding.BindingHash ||
                marker.ApprovedJobVersion != transition.Binding.ExpectedJobVersion ||
                marker.ReopenedJobVersion != document.Version ||
                marker.PreviousReviewVersion != transition.Binding.ExpectedReviewVersion ||
                marker.ReopenedReviewVersion != transition.Binding.ExpectedReviewVersion + 1 ||
                marker.PreviousReviewHash != transition.Binding.ExpectedReviewHash ||
                marker.PreviousReceiptHash != transition.Binding.ExpectedReceiptHash ||
                marker.PreviousContextHash != transition.Binding.ExpectedContextHash ||
                marker.ReopenedContextHash != transition.Binding.TargetContextHash ||
                marker.PreviousContextPolicyVersion != transition.Binding.ExpectedContextPolicyVersion ||
                marker.ReopenedContextPolicyVersion != transition.Binding.TargetContextPolicyVersion ||
                !marker.RequiredResolutionSegmentIds.SequenceEqual(
                    transition.Binding.RequiredResolutionSegmentIds, StringComparer.Ordinal) ||
                !ScopeEquals(data.Specification.ReviewAutomationScope!, transition.TargetScope) ||
                !string.Equals(data.ConfigurationHash, ConfigurationHash(data.Specification), StringComparison.Ordinal))
                return Failure("RECOVERY_CONTEXT_REVALIDATION_MARKER_MISMATCH", ErrorCategory.Integrity,
                    "The reopened job does not carry the exact context-revalidation marker.");

            var aggregate = CadSemanticContextBuilder.AggregateHash(segments);
            if (!aggregate.IsSuccess || !string.Equals(aggregate.Value, marker.ReopenedContextHash, StringComparison.Ordinal) ||
                segments.Any(segment => segment.SemanticContext?.Version != marker.ReopenedContextPolicyVersion))
                return Failure("RECOVERY_CONTEXT_REVALIDATION_TARGET_MISMATCH", ErrorCategory.Integrity,
                    "The reopened semantic-context set differs from the sealed target.");

            var loadedReview = await _reviews.LoadAsync(document.JobId, cancellationToken).ConfigureAwait(false);
            if (!loadedReview.IsSuccess) return Results.Failure<JobDocument>(loadedReview.Error!);
            var review = loadedReview.Value!;
            if (review.Version == marker.PreviousReviewVersion)
            {
                var oldFingerprint = TranslationReviewFingerprint.Create(review);
                if (!ValidApprovedContextReview(review, segments: null, data, transition.ExpectedScope) ||
                    review.Rows.Count != transition.Binding.RowCount ||
                    oldFingerprint.ReviewHash != marker.PreviousReviewHash ||
                    oldFingerprint.ReceiptHash != marker.PreviousReceiptHash)
                    return Failure("RECOVERY_CONTEXT_REVALIDATION_REVIEW_CHANGED", ErrorCategory.Integrity,
                        "The review changed after the job revalidation CAS.");
                var reopenedReview = ReopenReview(review, marker, document.UpdatedAtUtc);
                var savedReview = await _reviews.SaveAsync(reopenedReview, review.Version, cancellationToken)
                    .ConfigureAwait(false);
                if (!savedReview.IsSuccess)
                {
                    loadedReview = await _reviews.LoadAsync(document.JobId, CancellationToken.None).ConfigureAwait(false);
                    if (!loadedReview.IsSuccess) return Results.Failure<JobDocument>(savedReview.Error!);
                    review = loadedReview.Value!;
                }
                else review = savedReview.Value!;
            }
            var expectedReview = ReopenReview(review with
            {
                Version = marker.PreviousReviewVersion,
                ContextPolicyVersion = marker.PreviousContextPolicyVersion,
                ContextHash = marker.PreviousContextHash,
                ReviewAutomationReceipt = null
            }, marker, document.UpdatedAtUtc);
            if (!SameReopenedReview(review, expectedReview, marker, segments))
                return Failure("RECOVERY_CONTEXT_REVALIDATION_REVIEW_MISMATCH", ErrorCategory.Integrity,
                    "The reopened review is incomplete or differs from the sealed migration.");

            var audit = await _jobs.AppendAuditAsync(new AuditRecord(
                StableGuid($"approved-context-revalidation:event:{document.JobId:D}:{document.Version}:{marker.BindingHash}"),
                document.JobId,
                StableGuid($"approved-context-revalidation:correlation:{document.JobId:D}:{document.Version}:{marker.BindingHash}"),
                document.UpdatedAtUtc.ToUniversalTime(), "ApprovedContextRevalidated",
                new JsonObject
                {
                    ["from"] = JobState.Approved.ToString(),
                    ["to"] = JobState.ReviewRequired.ToString(),
                    ["previousPolicyVersion"] = marker.PreviousContextPolicyVersion,
                    ["targetPolicyVersion"] = marker.ReopenedContextPolicyVersion,
                    ["previousContextHash"] = marker.PreviousContextHash,
                    ["targetContextHash"] = marker.ReopenedContextHash,
                    ["previousReviewVersion"] = marker.PreviousReviewVersion,
                    ["reopenedReviewVersion"] = marker.ReopenedReviewVersion,
                    ["withdrawnReceiptHash"] = marker.PreviousReceiptHash,
                    ["requiredResolutionCount"] = marker.RequiredResolutionSegmentIds.Count,
                    ["bindingHash"] = marker.BindingHash
                }), cancellationToken).ConfigureAwait(false);
            if (!audit.IsSuccess) return ContextProjectionIncomplete();
            var checkpoint = await CheckpointAsync(document, data, cancellationToken, document.UpdatedAtUtc)
                .ConfigureAwait(false);
            return checkpoint.IsSuccess ? checkpoint : ContextProjectionIncomplete();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return ContextProjectionIncomplete();
        }
    }

    private static bool ValidApprovedContextReview(
        TranslationReviewSnapshot review,
        IReadOnlyList<CadTextSegment>? segments,
        DwgTranslationJobData data,
        ReviewAutomationScope expectedScope)
    {
        var receipt = review.ReviewAutomationReceipt;
        var sourceSegments = segments ?? data.Segments!;
        var ids = sourceSegments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        return review.JobId != Guid.Empty && receipt is not null &&
            review.Rows.Count == sourceSegments.Count && review.Rows.Count > 0 &&
            review.Rows.All(row => ids.TryGetValue(row.SegmentId, out var segment) &&
                row.State == SegmentState.Approved && row.ExclusionReason is null &&
                string.Equals(row.OriginalText, segment.SourceText, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(row.FinalText)) &&
            review.ContextPolicyVersion == CadSemanticContextBuilder.PolicyVersionOneOne &&
            receipt.PolicyVersion == ReviewAutomationPolicy.ContextualAgentCreateNew &&
            receipt.BatchId == expectedScope.BatchId && receipt.ManifestHash == expectedScope.ManifestHash &&
            receipt.ContextHash == review.ContextHash &&
            ContractPatterns.Sha256().IsMatch(receipt.ReviewerReportHash) &&
            ContractPatterns.Sha256().IsMatch(receipt.QaReportHash) &&
            receipt.SegmentContextResolutions is null or { Count: 0 };
    }

    private static TranslationReviewSnapshot ReopenReview(
        TranslationReviewSnapshot review,
        ApprovedContextRevalidationReceipt marker,
        DateTimeOffset updatedAtUtc) => review with
        {
            Version = marker.ReopenedReviewVersion,
            UpdatedAtUtc = updatedAtUtc.ToUniversalTime(),
            ContextPolicyVersion = marker.ReopenedContextPolicyVersion,
            ContextHash = marker.ReopenedContextHash,
            ReviewAutomationReceipt = null,
            Rows = review.Rows.Select(row => row with
            {
                ProposedText = row.FinalText,
                State = SegmentState.Proposed,
                ExclusionReason = null
            }).ToArray()
        };

    private static bool SameReopenedReview(
        TranslationReviewSnapshot actual,
        TranslationReviewSnapshot expected,
        ApprovedContextRevalidationReceipt marker,
        IReadOnlyList<CadTextSegment> segments)
    {
        if (actual.Version != marker.ReopenedReviewVersion || actual.UpdatedAtUtc != expected.UpdatedAtUtc ||
            actual.ContextPolicyVersion != marker.ReopenedContextPolicyVersion ||
            actual.ContextHash != marker.ReopenedContextHash || actual.ReviewAutomationReceipt is not null ||
            actual.Rows.Count != expected.Rows.Count || actual.Rows.Count != segments.Count)
            return false;
        return actual.Rows.Zip(expected.Rows).All(pair => pair.First == pair.Second);
    }

    private static Result<JobDocument> ContextProjectionIncomplete() => Results.Failure<JobDocument>(new ContractError(
        "RECOVERY_CONTEXT_REVALIDATION_PROJECTION_INCOMPLETE", ErrorCategory.Storage,
        "Context revalidation was committed but its review, audit, or checkpoint projection is incomplete.", true));

    private async Task<Result<bool>> AuditScopeRebindAsync(
        JobDocument document,
        ReviewAutomationScope from,
        ReviewAutomationScope to,
        RecoveryReviewScopeSeal reviewSeal,
        CancellationToken cancellationToken)
    {
        var data = new JsonObject
        {
            ["jobId"] = document.JobId.ToString("D"),
            ["jobVersion"] = document.Version,
            ["fromBatchId"] = from.BatchId.ToString("D"),
            ["fromManifestHash"] = from.ManifestHash,
            ["toBatchId"] = to.BatchId.ToString("D"),
            ["toManifestHash"] = to.ManifestHash,
            ["policyVersion"] = to.PolicyVersion
        };
        AddReviewSeal(data, reviewSeal);
        return await _jobs.AppendAuditAsync(new AuditRecord(
            StableGuid($"review-scope-rebind:event:{document.JobId:D}:{document.Version}:{to.BatchId:D}:{to.ManifestHash}"),
            document.JobId,
            StableGuid($"review-scope-rebind:correlation:{document.JobId:D}:{document.Version}:{to.BatchId:D}:{to.ManifestHash}"),
            document.UpdatedAtUtc.ToUniversalTime(),
            "ReviewAutomationScopeRebound", data), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ScopeTransitionValidation>> ValidateScopeTransitionAsync(
        JobDocument current,
        ReviewAutomationScopeTransition transition,
        bool requireCompleteReviewCoverage,
        CancellationToken cancellationToken)
    {
        if (!ValidScope(transition.ExpectedScope) || !ValidScope(transition.TargetScope) ||
            transition.ExpectedScope.BatchId == transition.TargetScope.BatchId ||
            !string.Equals(transition.ExpectedScope.PolicyVersion, transition.TargetScope.PolicyVersion, StringComparison.Ordinal) ||
            !string.Equals(transition.TargetScope.PolicyVersion, ReviewAutomationPolicy.ContextualAgentCreateNew, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(transition.SourcePath) || string.IsNullOrWhiteSpace(transition.OutputPath) ||
            !ContractPatterns.Sha256().IsMatch(transition.SourceHash ?? string.Empty) ||
            !CadSemanticContextBuilder.IsSupportedPolicyVersion(transition.ContextPolicyVersion))
            return Results.Failure<ScopeTransitionValidation>(Error("RECOVERY_REVIEW_SCOPE_TRANSITION_INVALID",
                ErrorCategory.Integrity, "The recovery review-scope transition is invalid."));

        var durable = current.Data.DeepClone().AsObject();
        durable.Remove("failure");
        durable.Remove("agentFailure");
        var data = Deserialize(durable);
        if (!data.IsSuccess) return Results.Failure<ScopeTransitionValidation>(data.Error!);
        var value = data.Value!;
        var scope = value.Specification.ReviewAutomationScope;
        if (scope is null || !ScopeEquals(scope, transition.ExpectedScope) && !ScopeEquals(scope, transition.TargetScope))
            return Results.Failure<ScopeTransitionValidation>(Error("RECOVERY_REVIEW_SCOPE_CONFLICT",
                ErrorCategory.Integrity, "The adopted job belongs to a different review automation scope."));
        if (!PathsEqual(value.Specification.SourcePath, transition.SourcePath) ||
            !PathsEqual(value.Specification.OutputPath, transition.OutputPath) ||
            !string.Equals(value.Specification.SourceHash, transition.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(value.ConfigurationHash, ConfigurationHash(value.Specification), StringComparison.Ordinal))
            return Results.Failure<ScopeTransitionValidation>(Error("RECOVERY_REVIEW_SCOPE_BINDING_CHANGED",
                ErrorCategory.Integrity, "The adopted job configuration or immutable file binding changed."));
        if (value.Segments is not { Count: > 0 } segments)
            return Results.Failure<ScopeTransitionValidation>(Error("RECOVERY_REVIEW_CONTEXT_INVALID",
                ErrorCategory.Integrity, "The adopted job has no complete semantic-context source set."));

        var aggregate = CadSemanticContextBuilder.AggregateHash(segments);
        if (!aggregate.IsSuccess || segments.Any(segment =>
                !string.Equals(segment.SemanticContext?.Version, transition.ContextPolicyVersion, StringComparison.Ordinal)))
            return Results.Failure<ScopeTransitionValidation>(Error("RECOVERY_REVIEW_CONTEXT_INVALID",
                ErrorCategory.Integrity, "The adopted job semantic context is invalid or mixed-version."));
        var review = await _reviews.LoadAsync(current.JobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess) return Results.Failure<ScopeTransitionValidation>(review.Error!);
        var snapshot = review.Value!;
        var segmentIds = segments.Select(segment => segment.SegmentId).ToHashSet(StringComparer.Ordinal);
        var uniqueRowCount = snapshot.Rows.Select(row => row.SegmentId).Distinct(StringComparer.Ordinal).Count();
        if (snapshot.JobId != current.JobId || snapshot.ReviewAutomationReceipt is not null ||
            snapshot.Rows.Count == 0 || uniqueRowCount != snapshot.Rows.Count ||
            snapshot.Rows.Any(row => !segmentIds.Contains(row.SegmentId)) ||
            requireCompleteReviewCoverage && (snapshot.Rows.Count != segmentIds.Count || uniqueRowCount != segmentIds.Count) ||
            !string.Equals(snapshot.TargetLanguage, value.Specification.TargetLanguage, StringComparison.Ordinal) ||
            !string.Equals(snapshot.PromptTemplateVersion, value.Specification.PromptTemplateVersion, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ContextPolicyVersion, transition.ContextPolicyVersion, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ContextHash, aggregate.Value, StringComparison.Ordinal))
            return Results.Failure<ScopeTransitionValidation>(Error("RECOVERY_REVIEW_EVIDENCE_CHANGED",
                ErrorCategory.Integrity, "The adopted review receipt, rows, or semantic-context binding changed."));
        var fingerprint = TranslationReviewFingerprint.Create(snapshot);
        if (ScopeEquals(scope, transition.TargetScope) &&
            !SealEquals(value.RecoveryReviewScopeSeal, transition, fingerprint, current.Version))
            return Results.Failure<ScopeTransitionValidation>(Error("RECOVERY_REVIEW_SEAL_MISMATCH",
                ErrorCategory.Integrity, "The rebound job does not carry the exact sealed review fingerprint."));
        return Results.Success(new ScopeTransitionValidation(value, fingerprint));
    }

    private async Task<Result<JobDocument>> ProjectTranslationRecoveryAsync(
        JobDocument document,
        ReviewAutomationScopeTransition? scopeTransition,
        bool scopeWasRebound,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = Deserialize(document.Data);
            if (!data.IsSuccess) return Results.Failure<JobDocument>(data.Error!);
            if (scopeTransition is not null)
            {
                var sealedReview = await ValidateSealedReviewAsync(document.JobId, document.Version, data.Value!, scopeTransition,
                    cancellationToken).ConfigureAwait(false);
                if (!sealedReview.IsSuccess) return Results.Failure<JobDocument>(sealedReview.Error!);
            }
            var audited = await AuditRecoveryAsync(document, JobState.Failed, scopeTransition, scopeWasRebound,
                data.Value!.RecoveryReviewScopeSeal, cancellationToken).ConfigureAwait(false);
            if (!audited.IsSuccess) return ProjectionIncomplete();
            var checkpoint = await CheckpointAsync(document, data.Value, cancellationToken, document.UpdatedAtUtc,
                    data.Value.RecoveryReviewScopeSeal)
                .ConfigureAwait(false);
            if (!checkpoint.IsSuccess) return ProjectionIncomplete();
            if (scopeTransition is not null)
            {
                var stillSealed = await ValidateSealedReviewAsync(document.JobId, document.Version, data.Value, scopeTransition,
                    cancellationToken).ConfigureAwait(false);
                if (!stillSealed.IsSuccess) return Results.Failure<JobDocument>(stillSealed.Error!);
            }
            return checkpoint;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ProjectionIncomplete();
        }
    }

    private async Task<Result<JobDocument>> ProjectReviewScopeRebindAsync(
        JobDocument document,
        DwgTranslationJobData data,
        ReviewAutomationScopeTransition transition,
        CancellationToken cancellationToken)
    {
        try
        {
            var sealedReview = await ValidateSealedReviewAsync(document.JobId, document.Version, data, transition, cancellationToken)
                .ConfigureAwait(false);
            if (!sealedReview.IsSuccess) return Results.Failure<JobDocument>(sealedReview.Error!);
            var reviewSeal = data.RecoveryReviewScopeSeal!;
            var audited = await AuditScopeRebindAsync(document, transition.ExpectedScope, transition.TargetScope,
                reviewSeal, cancellationToken).ConfigureAwait(false);
            if (!audited.IsSuccess) return ProjectionIncomplete();
            var checkpoint = await CheckpointAsync(document, data, cancellationToken, document.UpdatedAtUtc, reviewSeal)
                .ConfigureAwait(false);
            if (!checkpoint.IsSuccess) return ProjectionIncomplete();
            var stillSealed = await ValidateSealedReviewAsync(document.JobId, document.Version, data, transition, cancellationToken)
                .ConfigureAwait(false);
            return stillSealed.IsSuccess ? checkpoint : Results.Failure<JobDocument>(stillSealed.Error!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ProjectionIncomplete();
        }
    }

    private static Result<JobDocument> ProjectionIncomplete() => Results.Failure<JobDocument>(new ContractError(
        "RECOVERY_LINEAGE_PROJECTION_INCOMPLETE", ErrorCategory.Storage,
        "Recovery lineage was committed but its redacted audit/checkpoint projection is incomplete.", true));

    private static DwgTranslationJobData RebindScope(
        DwgTranslationJobData data,
        ReviewAutomationScopeTransition transition,
        long transitionJobVersion,
        TranslationReviewFingerprint review)
    {
        var specification = data.Specification with { ReviewAutomationScope = transition.TargetScope };
        var seal = new RecoveryReviewScopeSeal(transition.ExpectedScope, transition.TargetScope,
            transitionJobVersion,
            review.Version, review.ReviewHash, review.ContextHash,
            review.Version, review.ReviewHash, review.ContextHash);
        return data with
        {
            Specification = specification,
            ConfigurationHash = ConfigurationHash(specification),
            RecoveryReviewScopeSeal = seal
        };
    }

    private async Task<Result<bool>> ValidateSealedReviewAsync(
        Guid jobId,
        long jobVersion,
        DwgTranslationJobData data,
        ReviewAutomationScopeTransition transition,
        CancellationToken cancellationToken)
    {
        var review = await _reviews.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess) return Results.Failure<bool>(review.Error!);
        return SealEquals(data.RecoveryReviewScopeSeal, transition,
                TranslationReviewFingerprint.Create(review.Value!), jobVersion)
            ? Results.Success(true)
            : Results.Failure<bool>(Error("RECOVERY_REVIEW_SEAL_MISMATCH", ErrorCategory.Integrity,
                "The recovery review changed after its lineage CAS was sealed."));
    }

    private static bool SealEquals(
        RecoveryReviewScopeSeal? seal,
        ReviewAutomationScopeTransition transition,
        TranslationReviewFingerprint fingerprint,
        long jobVersion) =>
        seal is not null && ScopeEquals(seal.ExpectedScope, transition.ExpectedScope) &&
        ScopeEquals(seal.TargetScope, transition.TargetScope) && seal.TransitionJobVersion > 0 &&
        seal.TransitionJobVersion <= jobVersion && seal.TransitionReviewVersion >= 0 &&
        seal.TransitionReviewVersion <= seal.BoundReviewVersion &&
        ContractPatterns.Sha256().IsMatch(seal.TransitionReviewHash) &&
        ContractPatterns.Sha256().IsMatch(seal.TransitionContextHash ?? string.Empty) &&
        ContractPatterns.Sha256().IsMatch(seal.BoundReviewHash) &&
        seal.BoundReviewVersion == fingerprint.Version &&
        string.Equals(seal.BoundReviewHash, fingerprint.ReviewHash, StringComparison.Ordinal) &&
        string.Equals(seal.BoundContextHash, fingerprint.ContextHash, StringComparison.Ordinal) &&
        string.Equals(seal.TransitionContextHash, fingerprint.ContextHash, StringComparison.Ordinal);

    private static Result<DwgTranslationJobData> BindCurrentReview(
        DwgTranslationJobData data,
        TranslationReviewFingerprint review)
    {
        var seal = data.RecoveryReviewScopeSeal;
        if (seal is null) return Results.Success(data);
        if (!string.Equals(seal.TransitionContextHash, review.ContextHash, StringComparison.Ordinal))
            return Results.Failure<DwgTranslationJobData>(Error("RECOVERY_REVIEW_SEAL_MISMATCH",
                ErrorCategory.Integrity, "The completed recovery review changed its sealed semantic context."));
        return Results.Success(data with
        {
            RecoveryReviewScopeSeal = seal with
            {
                BoundReviewVersion = review.Version,
                BoundReviewHash = review.ReviewHash,
                BoundContextHash = review.ContextHash
            }
        });
    }

    private static void AddReviewSeal(JsonObject data, RecoveryReviewScopeSeal seal)
    {
        data["transitionJobVersion"] = seal.TransitionJobVersion;
        data["reviewVersion"] = seal.TransitionReviewVersion;
        data["reviewHash"] = seal.TransitionReviewHash;
        data["reviewContextHash"] = seal.TransitionContextHash;
        data["boundReviewVersion"] = seal.BoundReviewVersion;
        data["boundReviewHash"] = seal.BoundReviewHash;
        data["boundContextHash"] = seal.BoundContextHash;
    }

    private static bool ValidScope(ReviewAutomationScope scope) =>
        scope.BatchId != Guid.Empty && ContractPatterns.Sha256().IsMatch(scope.ManifestHash ?? string.Empty) &&
        !string.IsNullOrWhiteSpace(scope.PolicyVersion) && scope.PolicyVersion.Length <= 120;

    private static bool ScopeEquals(ReviewAutomationScope left, ReviewAutomationScope right) =>
        left.BatchId == right.BatchId && string.Equals(left.ManifestHash, right.ManifestHash, StringComparison.Ordinal) &&
        string.Equals(left.PolicyVersion, right.PolicyVersion, StringComparison.Ordinal);

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    private async Task<Result<JobDocument>> CheckpointAsync(
        JobDocument document,
        DwgTranslationJobData data,
        CancellationToken cancellationToken,
        DateTimeOffset? createdAtUtc = null,
        RecoveryReviewScopeSeal? reviewSeal = null)
    {
        var checkpointData = new JsonObject
        {
            ["state"] = document.State.ToString(),
            ["jobVersion"] = document.Version
        };
        if (reviewSeal is not null) AddReviewSeal(checkpointData, reviewSeal);
        var checkpoint = new JobCheckpoint(
            document.JobId,
            document.State,
            document.Version,
            (createdAtUtc ?? _clock.UtcNow).ToUniversalTime(),
            data.Specification.SourceHash,
            data.ConfigurationHash,
            ContractV1.SchemaVersion,
            checkpointData);
        var saved = await _jobs.SaveCheckpointAsync(checkpoint, cancellationToken);
        return saved.IsSuccess ? Results.Success(document) : Results.Failure<JobDocument>(saved.Error!);
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record ScopeTransitionValidation(
        DwgTranslationJobData Data,
        TranslationReviewFingerprint ReviewFingerprint);

    private async Task<Result<bool>> AuditAsync(
        JobDocument document,
        JobState? from,
        JobState to,
        CancellationToken cancellationToken,
        ApprovalCoverage? coverage = null)
    {
        var data = new JsonObject
        {
            ["jobId"] = document.JobId.ToString("D"),
            ["from"] = from?.ToString(),
            ["to"] = to.ToString(),
            ["state"] = to.ToString()
        };
        if (coverage is not null)
        {
            data["selected"] = coverage.Selected;
            data["approved"] = coverage.Approved;
            data["excluded"] = coverage.Excluded;
        }
        return await _jobs.AppendAuditAsync(new AuditRecord(Guid.NewGuid(), document.JobId, Guid.NewGuid(), _clock.UtcNow, "JobStateChanged", data), cancellationToken);
    }

    private static Result<bool> ValidateSpecification(DwgTranslationJobSpecification specification)
    {
        if (specification is null || string.IsNullOrWhiteSpace(specification.SourcePath) || string.IsNullOrWhiteSpace(specification.OutputPath) ||
            !specification.SourcePath.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || !specification.OutputPath.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(specification.SourcePath, specification.OutputPath, StringComparison.OrdinalIgnoreCase) ||
            !ContractPatterns.Sha256().IsMatch(specification.SourceHash ?? string.Empty) ||
            !LanguageTag.Create(specification.TargetLanguage).IsSuccess ||
            (specification.SourceLanguage is not null && !LanguageTag.Create(specification.SourceLanguage).IsSuccess) ||
            string.IsNullOrWhiteSpace(specification.PromptTemplateVersion) || specification.PromptTemplateVersion.Length > 120 ||
            (specification.ReviewAutomationScope is { } scope &&
                (scope.BatchId == Guid.Empty || !ContractPatterns.Sha256().IsMatch(scope.ManifestHash ?? string.Empty) ||
                 string.IsNullOrWhiteSpace(scope.PolicyVersion) || scope.PolicyVersion.Length > 120)) ||
            (specification.Routing is not null && !TranslationRoutingPolicyFactory.Validate(specification.Routing).IsSuccess))
            return Results.Failure<bool>(Error("JOB_SPECIFICATION_INVALID", ErrorCategory.Input, "The source, output, hash and translation configuration are invalid."));
        return Results.Success(true);
    }

    private static string ConfigurationHash(DwgTranslationJobSpecification specification)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(specification, Json));
        return CanonicalJsonV1.Fingerprint(document.RootElement);
    }

    private static JsonObject Serialize(DwgTranslationJobData data) => JsonSerializer.SerializeToNode(data, Json)!.AsObject();

    private static Result<DwgTranslationJobData> Deserialize(JsonObject data)
    {
        try
        {
            var value = data.Deserialize<DwgTranslationJobData>(Json);
            return value is null
                ? Results.Failure<DwgTranslationJobData>(Error("JOB_DATA_INVALID", ErrorCategory.Storage, "Job data is empty."))
                : Results.Success(value);
        }
        catch (JsonException)
        {
            return Results.Failure<DwgTranslationJobData>(Error("JOB_DATA_INVALID", ErrorCategory.Storage, "Job data is invalid."));
        }
    }

    private static SegmentReview ToSegmentReview(ReviewRowSnapshot row) => row.State switch
    {
        SegmentState.Approved => SegmentReview.Approve(row.SegmentId, row.FinalText).Value!,
        SegmentState.Excluded => SegmentReview.Exclude(row.SegmentId, row.ExclusionReason).Value!,
        _ => SegmentReview.Pending(row.SegmentId, row.State, row.FinalText).Value!
    };

    private static bool ValidAutomationAuthority(
        ReviewAutomationReceipt authority,
        TranslationReviewSnapshot review,
        ReviewAutomationScope? scope,
        IReadOnlyList<CadTextSegment> segments) =>
        scope is not null && scope.BatchId != Guid.Empty && authority.BatchId == scope.BatchId &&
        authority.PolicyVersion == ReviewAutomationPolicy.ContextualAgentCreateNew &&
        scope.PolicyVersion == ReviewAutomationPolicy.ContextualAgentCreateNew &&
        string.Equals(authority.ManifestHash, scope.ManifestHash, StringComparison.Ordinal) &&
        string.Equals(authority.ContextHash, review.ContextHash, StringComparison.Ordinal) &&
        ContractPatterns.Sha256().IsMatch(authority.ManifestHash) &&
        ContractPatterns.Sha256().IsMatch(authority.ContextHash) &&
        ContractPatterns.Sha256().IsMatch(authority.ReviewerReportHash) &&
        ContractPatterns.Sha256().IsMatch(authority.QaReportHash) &&
        SegmentContextResolutionPolicy.ValidForReceipt(authority, review, segments);

    private static ContractError Error(string code, ErrorCategory category, string message) => new(code, category, message, false);
    private static Result<JobDocument> Failure(string code, ErrorCategory category, string message) => Results.Failure<JobDocument>(Error(code, category, message));
}
