using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Agent;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Agent.Tests;

[TestClass]
public sealed class AgentWorkflowServiceTests
{
    private static readonly JsonSerializerOptions TestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private string _root = null!;
    private string _input = null!;
    private string _output = null!;
    private string _source = null!;
    private DateTimeOffset _now;

    [TestMethod]
    public async Task FailedCadExtractionPreservesDurableCauseAndNeverTranslates()
    {
        const string code = "CAD_BOOTSTRAP_EVIDENCE_INVALID";
        var diagnostics = new CadInvariantDiagnostics
        {
            Schema = CadInvariantDiagnosticsLimits.Schema,
            AddedCount = 1,
            RemovedCount = 2,
            ChangedCount = 3,
            Truncated = false,
            Rows = []
        };
        var data = new JsonObject
        {
            ["failure"] = new JsonObject
            {
                ["code"] = code,
                ["category"] = ErrorCategory.Security.ToString(),
                ["retryable"] = false,
                ["diagnosticId"] = "diagnostic-safe",
                ["invariantDiagnostics"] = JsonSerializer.SerializeToNode(diagnostics, TestJson)
            }
        };
        var failed = new JobDocument(Guid.NewGuid(), JobState.Failed, 2, _now, data);
        var translateCalls = 0;

        var result = await AgentTranslationWorkflowBackend.ContinueAfterExtractionAsync(Results.Success(failed), () =>
        {
            translateCalls++;
            return Task.FromResult(Results.Success(failed));
        });

        Assert.AreEqual(0, translateCalls);
        Assert.AreEqual(code, result.Error?.Code);
        Assert.AreEqual(ErrorCategory.Security, result.Error?.Category);
        Assert.AreEqual(false, result.Error?.Retryable);
        Assert.AreEqual("diagnostic-safe", result.Error?.DiagnosticId);
        Assert.AreEqual(diagnostics.Schema, result.Error?.InvariantDiagnostics?.Schema);
        Assert.AreEqual(diagnostics.AddedCount, result.Error?.InvariantDiagnostics?.AddedCount);
        Assert.AreEqual(diagnostics.RemovedCount, result.Error?.InvariantDiagnostics?.RemovedCount);
        Assert.AreEqual(diagnostics.ChangedCount, result.Error?.InvariantDiagnostics?.ChangedCount);
        Assert.AreEqual(diagnostics.Truncated, result.Error?.InvariantDiagnostics?.Truncated);
        Assert.AreEqual(0, result.Error?.InvariantDiagnostics?.Rows.Count);
    }

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwg-agent-workflow-tests", Guid.NewGuid().ToString("N"));
        _input = Path.Combine(_root, "input");
        _output = Path.Combine(_root, "output");
        Directory.CreateDirectory(_input);
        Directory.CreateDirectory(_output);
        Directory.CreateDirectory(Path.Combine(_root, "jobs"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        _source = Path.Combine(_input, "source.dwg");
        File.WriteAllText(_source, "SYNTHETIC-DWG-BYTES-NOT-A-REAL-DRAWING");
        _now = new DateTimeOffset(2026, 8, 20, 20, 0, 0, TimeSpan.Zero);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        for (var attempt = 0; Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException && attempt < 49)
            {
                // Workflow calls are supervised in the background. A terminal job can become
                // observable a few milliseconds before its CAD lease stream is disposed.
                await Task.Delay(20);
            }
        }
    }

    [TestMethod]
    public async Task FakeWorkflowRequiresPlansHumanReviewAndCompletesWithoutCadOrNetwork()
    {
        var backend = new FakeBackend(highRisk: false);
        var service = Service(backend);
        var final = Path.Combine(_output, "source-de.dwg");

        var plan = await service.CreateTranslationPlanAsync(new(_source, "de", "Auto", null, final), CancellationToken.None);
        Assert.IsTrue(plan.Success);
        Assert.IsFalse(File.Exists(final));
        var translationContext = plan.Data!["translationContext"]!.AsObject();
        Assert.AreEqual(TranslationReviewWorkflow.ContextualPromptTemplateVersion,
            translationContext["promptTemplateVersion"]!.GetValue<string>());
        Assert.AreEqual(CadSemanticContextBuilder.CurrentPolicyVersion,
            translationContext["contextPolicyVersion"]!.GetValue<string>());
        Assert.IsTrue(translationContext["includeNeighborExcerpts"]!.GetValue<bool>());
        Assert.IsTrue(translationContext["reviewIncludeText"]!.GetValue<bool>());
        Assert.AreEqual(4, translationContext["maximumNeighborExcerpts"]!.GetValue<int>());
        Assert.AreEqual(160, translationContext["maximumNeighborExcerptScalars"]!.GetValue<int>());
        Assert.AreEqual(512, translationContext["maximumNeighborExcerptScalarsPerSegment"]!.GetValue<int>());
        Assert.AreEqual(AgentWorkflowContract.TranslationOutboundPurpose,
            translationContext["outboundPurpose"]!.GetValue<string>());
        var planId = Text(plan, "planId");
        var sourceHash = plan.Data!["source"]!["hash"]!.GetValue<string>();
        var approvalId = plan.Data["approval"]!["approvalId"]!.GetValue<string>();
        var consent = plan.Data["approval"]!["consent"]!.GetValue<string>();

        var started = await service.PrepareAsync(new(planId, approvalId, sourceHash, consent, "prepare-0001"), CancellationToken.None);
        Assert.IsTrue(started.Success);
        var jobId = Guid.Parse(Text(started, "jobId"));
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);

        var review = await service.GetReviewAsync(new(jobId), CancellationToken.None);
        Assert.IsTrue(review.Success);
        Assert.IsFalse(review.Data!.ToJsonString().Contains("ORIGINAL_SENSITIVE", StringComparison.Ordinal));
        Assert.IsFalse(review.Data.ToJsonString().Contains("PROPOSAL_SENSITIVE", StringComparison.Ordinal));

        var applied = await service.ApplyReviewAsync(new(
            jobId, backend.Job!.Version, null, true,
            AgentWorkflowService.BulkApproval(jobId, backend.Job.Version), "review-0001"), CancellationToken.None);
        Assert.IsTrue(applied.Success);
        Assert.AreEqual(JobState.Approved, backend.Job!.State);

        var generationPlan = await service.CreateGenerationPlanAsync(new(jobId), CancellationToken.None);
        Assert.IsTrue(generationPlan.Success);
        var generationStarted = await service.GenerateAsync(new(
            Text(generationPlan, "generationPlanId"),
            generationPlan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            backend.Job.Version,
            generationPlan.Data["approval"]!["phrase"]!.GetValue<string>(),
            "generate-0001"), CancellationToken.None);
        Assert.IsTrue(generationStarted.Success);
        await WaitForAsync(() => backend.Job?.State == JobState.Completed);

        var status = await service.JobStatusAsync(jobId, CancellationToken.None);
        Assert.AreEqual("Completed", Text(status, "state"));
        Assert.IsTrue(status.Data!["validation"]!["automaticPass"]!.GetValue<bool>());
        Assert.IsTrue(status.Data["validation"]!["visualReviewPending"]!.GetValue<bool>());
        Assert.IsTrue(File.Exists(final));

        var replay = await service.GenerateAsync(new(
            Text(generationPlan, "generationPlanId"),
            generationPlan.Data["approval"]!["approvalId"]!.GetValue<string>(),
            applied.Data!["jobVersion"]!.GetValue<long>(),
            generationPlan.Data["approval"]!["phrase"]!.GetValue<string>(),
            "generate-0001"), CancellationToken.None);
        Assert.IsTrue(replay.Success);
        Assert.IsTrue(replay.Data!["idempotentReplay"]!.GetValue<bool>());
        Assert.AreEqual(1, backend.GenerateCalls);
    }

    [TestMethod]
    [DataRow("review")]
    [DataRow("context")]
    [DataRow("receipt")]
    public async Task GenerationRejectsReviewBindingTamperAfterPlanWithoutCallingBackend(string field)
    {
        var backend = new FakeBackend(highRisk: false);
        var service = Service(backend);
        var final = Path.Combine(_output, $"tamper-{field}.dwg");
        var plan = await service.CreateTranslationPlanAsync(new(_source, "de", "Auto", null, final), CancellationToken.None);
        var started = await service.PrepareAsync(new(Text(plan, "planId"),
            plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(),
            plan.Data["approval"]!["consent"]!.GetValue<string>(), $"prepare-{field}"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);
        Assert.IsNull(backend.LastReviewAutomationScope,
            "A direct translation plan must not activate a batch contextual review scope.");
        var applied = await service.ApplyReviewAsync(new(jobId, backend.Job!.Version, null, true,
            AgentWorkflowService.BulkApproval(jobId, backend.Job.Version), $"review-{field}"), CancellationToken.None);
        Assert.IsTrue(applied.Success, applied.Error?.Code);
        var generationPlan = await service.CreateGenerationPlanAsync(new(jobId), CancellationToken.None);
        Assert.IsTrue(generationPlan.Success, generationPlan.Error?.Code);
        backend.TamperApprovedReviewAfterPlan(field);

        var generated = await service.GenerateAsync(new(Text(generationPlan, "generationPlanId"),
            generationPlan.Data!["approval"]!["approvalId"]!.GetValue<string>(), backend.Job!.Version,
            generationPlan.Data["approval"]!["phrase"]!.GetValue<string>(), $"generate-{field}"), CancellationToken.None);

        Assert.AreEqual("GENERATION_REVIEW_BINDING_MISMATCH", generated.Error!.Code);
        Assert.AreEqual(0, backend.GenerateCalls);
        Assert.IsFalse(File.Exists(final));
    }

    [TestMethod]
    public async Task GenerationRechecksReviewInsideBackendAfterSchedulingBeforeCad()
    {
        var backend = new FakeBackend(highRisk: false);
        var service = Service(backend);
        var final = Path.Combine(_output, "scheduled-review-tamper.dwg");
        var plan = await service.CreateTranslationPlanAsync(new(_source, "de", "Auto", null, final), CancellationToken.None);
        var started = await service.PrepareAsync(new(Text(plan, "planId"),
            plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(),
            plan.Data["approval"]!["consent"]!.GetValue<string>(), "prepare-scheduled-tamper"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);
        var applied = await service.ApplyReviewAsync(new(jobId, backend.Job!.Version, null, true,
            AgentWorkflowService.BulkApproval(jobId, backend.Job.Version), "review-scheduled-tamper"), CancellationToken.None);
        Assert.IsTrue(applied.Success, applied.Error?.Code);
        var generationPlan = await service.CreateGenerationPlanAsync(new(jobId), CancellationToken.None);
        backend.PauseGenerationBeforeReviewValidation();

        var generation = await service.GenerateAsync(new(Text(generationPlan, "generationPlanId"),
            generationPlan.Data!["approval"]!["approvalId"]!.GetValue<string>(), backend.Job!.Version,
            generationPlan.Data["approval"]!["phrase"]!.GetValue<string>(), "generate-scheduled-tamper"), CancellationToken.None);
        Assert.IsTrue(generation.Success, generation.Error?.Code);
        var operationId = Text(generation, "operationId");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await backend.WaitForGenerationEntryAsync(deadline.Token);
        backend.TamperApprovedReviewAfterPlan("review");
        backend.ReleaseGenerationValidation();
        JsonObject? terminal = null;
        await WaitForAsync(async () =>
        {
            terminal = await LoadOperationAsync(operationId);
            return terminal?["errorCode"]?.GetValue<string>() == "GENERATION_REVIEW_BINDING_MISMATCH";
        });

        Assert.AreEqual(0, backend.GenerateCalls);
        Assert.IsFalse(File.Exists(final));
        Assert.AreEqual("GENERATION_REVIEW_BINDING_MISMATCH",
            terminal!["errorCode"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("review", "GENERATION_REVIEW_BINDING_MISMATCH")]
    [DataRow("source", "SOURCE_CHANGED")]
    [DataRow("output", "OUTPUT_ALREADY_EXISTS")]
    public async Task GenerationRevalidatesDurableInputsAfterSchedulingUnderLeases(
        string mutation, string expectedError)
    {
        var backend = new FakeBackend(highRisk: false);
        var scheduled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(backend, work => Task.Run(async () =>
        {
            scheduled.TrySetResult(true);
            await release.Task;
            await work();
        }));
        var final = Path.Combine(_output, $"lease-{mutation}.dwg");
        var plan = await service.CreateTranslationPlanAsync(new(_source, "de", "Auto", null, final), CancellationToken.None);
        var started = await service.PrepareAsync(new(Text(plan, "planId"),
            plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(),
            plan.Data["approval"]!["consent"]!.GetValue<string>(), $"prepare-lease-{mutation}"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));
        release.TrySetResult(true);
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);
        var applied = await service.ApplyReviewAsync(new(jobId, backend.Job!.Version, null, true,
            AgentWorkflowService.BulkApproval(jobId, backend.Job.Version), $"review-lease-{mutation}"), CancellationToken.None);
        var generationPlan = await service.CreateGenerationPlanAsync(new(jobId), CancellationToken.None);
        scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var generated = await service.GenerateAsync(new(Text(generationPlan, "generationPlanId"),
            generationPlan.Data!["approval"]!["approvalId"]!.GetValue<string>(), backend.Job!.Version,
            generationPlan.Data["approval"]!["phrase"]!.GetValue<string>(), $"generate-lease-{mutation}"), CancellationToken.None);
        Assert.IsTrue(generated.Success, generated.Error?.Code);
        var operationId = Text(generated, "operationId");
        await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (mutation)
        {
            case "review":
                backend.TamperApprovedReviewAfterPlan("review");
                break;
            case "source":
                await File.AppendAllTextAsync(_source, "-CHANGED");
                break;
            case "output":
                await File.WriteAllTextAsync(final, "SYNTHETIC-EXISTING-OUTPUT");
                break;
        }
        release.TrySetResult(true);

        JsonObject? terminal = null;
        await WaitForAsync(async () =>
        {
            terminal = await LoadOperationAsync(operationId);
            return terminal?["errorCode"]?.GetValue<string>() == expectedError;
        });
        Assert.AreEqual(0, backend.GenerateCalls);
        Assert.AreEqual(mutation == "output", File.Exists(final));
        Assert.IsTrue(applied.Success, applied.Error?.Code);
    }

    private async Task<JsonObject?> LoadOperationAsync(string operationId)
    {
        var path = Path.Combine(_root, "logs", "workflow", "operations", operationId + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [TestMethod]
    public async Task ProductionBatchProcessorReusesWorkflowWithChildBindingsAndExplicitDecisions()
    {
        var backend = new FakeBackend(highRisk: true);
        var batchOutput = Path.Combine(_input, "batch-eng");
        Directory.CreateDirectory(batchOutput);
        var configuration = new AgentBetaConfiguration(
            "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true,
            Path.Combine(_root, "jobs"), Path.Combine(_root, "logs"),
            AllowedDwgRoot: _input, OpenAiEnabled: true, OutputDwgRoot: _output,
            ConfiguredAccessibleModels: [TranslationRouting.Terra, TranslationRouting.Luna, TranslationRouting.Sol],
            BatchOutputRoots: [batchOutput], BatchExecutionEnabled: true);
        var workflow = new AgentWorkflowService(configuration, backend, () => _now);
        var processor = new ProductionAgentBatchFileProcessor(workflow, Path.Combine(_root, "bindings"),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(2));
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(batchOutput, "source-ENG.dwg");
        var file = new AgentBatchManifestEntry("source.dwg", _source, output,
            snapshot.Snapshot!.Bytes, snapshot.Snapshot.Hash, snapshot.Snapshot.LastWriteTimeUtc);
        var plan = new AgentBatchPlan("parent-plan", Guid.NewGuid(), "sha256:" + new string('c', 64),
            _input, batchOutput, "en-US", "Auto", ArchitecturalMepTerminologyPolicy.Version,
            AgentBatchPolicy.PolicyVersion, [file],
            new("parent-approval", "parent-consent", "parent-generation", _now.AddMinutes(30), true), _now,
            ContextPolicyVersion: CadSemanticContextBuilder.PolicyVersionOneTwo);

        var prepared = await processor.PrepareAsync(plan, file, CancellationToken.None);
        Assert.AreEqual(AgentBatchFileState.Reviewing, prepared.State);
        Assert.IsNotNull(prepared.JobId);
        Assert.IsTrue(backend.IsContextual, "The child specification lost its contextual batch scope.");
        Assert.IsNotNull(backend.Review?.ContextHash, "The contextual fake did not create an aggregate review hash.");
        StringAssert.StartsWith(prepared.ChildPrepareApprovalHash, "sha256:");
        Assert.AreEqual(11, prepared.InputTokens);
        Assert.AreEqual(7, prepared.OutputTokens);
        Assert.AreEqual(1, prepared.ProviderRequests);
        var resumedPrepare = await processor.PrepareAsync(plan, file, CancellationToken.None);
        Assert.AreEqual(prepared.InputTokens, resumedPrepare.InputTokens);
        Assert.AreEqual(prepared.OutputTokens, resumedPrepare.OutputTokens);
        Assert.AreEqual(prepared.ProviderRequests, resumedPrepare.ProviderRequests);

        var redactedReview = await workflow.GetReviewAsync(new(prepared.JobId.Value), CancellationToken.None);
        Assert.IsTrue(redactedReview.Success, redactedReview.Error?.Code);
        Assert.AreEqual(CadSemanticContextBuilder.PolicyVersionOneTwo,
            redactedReview.Data!["contextPolicyVersion"]!.GetValue<string>());
        StringAssert.StartsWith(redactedReview.Data["contextHash"]!.GetValue<string>(), "sha256:");
        var redactedContext = redactedReview.Data["rows"]![0]!["context"]!.AsObject();
        Assert.IsNull(redactedContext["layer"]);
        Assert.IsNull(redactedContext["layout"]);
        Assert.IsNull(redactedContext["neighborIds"]);
        StringAssert.StartsWith(redactedContext["semanticKey"]!.GetValue<string>(), "sha256:");
        Assert.IsNotNull(redactedContext["xBand"]);
        Assert.IsNotNull(redactedContext["yBand"]);
        Assert.IsNotNull(redactedContext["readingOrder"]);
        Assert.IsNotNull(redactedContext["neighbors"]);
        Assert.IsFalse(redactedReview.Data.ToJsonString().Contains("ORIGINAL_SENSITIVE", StringComparison.Ordinal));

        var textReview = await workflow.GetReviewAsync(new(prepared.JobId.Value, IncludeText: true), CancellationToken.None);
        Assert.IsTrue(textReview.Success, textReview.Error?.Code);
        var textContext = textReview.Data!["rows"]![0]!["context"]!.AsObject();
        Assert.AreEqual("BMS", textContext["layer"]!.GetValue<string>());
        Assert.AreEqual("Controls", textContext["discipline"]!.GetValue<string>());
        Assert.IsNotNull(textContext["neighborIds"]);
        Assert.AreEqual(1, textReview.Data.ToJsonString().Split("ORIGINAL_SENSITIVE", StringSplitOptions.None).Length - 1);

        var reconciled = await processor.ReconcileReviewAsync(plan, prepared with
        {
            ErrorCode = "OPENAI_TIMEOUT",
            Retryable = true,
            TextCount = 0,
            MTextCount = 0,
            EffectiveModel = null
        }, null, CancellationToken.None);
        Assert.AreEqual(AgentBatchFileState.Reviewing, reconciled.State);
        Assert.IsNull(reconciled.ErrorCode);
        Assert.IsFalse(reconciled.Retryable);
        Assert.AreEqual(1, reconciled.TextCount);
        Assert.AreEqual(0, reconciled.MTextCount);
        Assert.AreEqual(TranslationRouting.Terra, reconciled.EffectiveModel);
        Assert.AreEqual(prepared.InputTokens, reconciled.InputTokens);
        Assert.AreEqual(prepared.OutputTokens, reconciled.OutputTokens);
        Assert.AreEqual(prepared.ProviderRequests, reconciled.ProviderRequests);
        Assert.IsTrue(reconciled.JobVersion >= prepared.JobVersion);

        var rejectedBeforeExternalReview = await processor.ApproveAndGenerateAsync(plan, reconciled, CancellationToken.None);
        Assert.AreEqual(AgentBatchFileState.Failed, rejectedBeforeExternalReview.State);
        Assert.AreEqual("BATCH_CONTEXTUAL_REVIEW_AUTHORITY_REQUIRED", rejectedBeforeExternalReview.ErrorCode);
        Assert.AreEqual(0, backend.GenerateCalls);
        var externalAuthority = new ReviewAutomationReceipt(
            ReviewAutomationPolicy.ContextualAgentCreateNew, plan.BatchId, plan.ManifestHash,
            backend.Review!.ContextHash!, "sha256:" + new string('8', 64), "sha256:" + new string('9', 64));
        backend.ConfigureExternallyApproved(externalAuthority);

        var staleApprovedProjection = await processor.ReconcileReviewAsync(plan, reconciled, null,
            CancellationToken.None);
        Assert.AreEqual(AgentBatchFileState.Reviewing, staleApprovedProjection.State,
            staleApprovedProjection.ErrorCode);
        Assert.AreEqual(backend.Job!.Version, staleApprovedProjection.JobVersion);
        Assert.AreEqual(backend.Review.Version, staleApprovedProjection.ReviewVersion);
        StringAssert.StartsWith(staleApprovedProjection.ReviewAutomationReceiptHash, "sha256:");
        Assert.AreEqual(0, backend.GenerateCalls);

        var wrongManifest = await processor.ReconcileReviewAsync(plan with
        {
            ManifestHash = "sha256:" + new string('0', 64)
        }, reconciled, null, CancellationToken.None);
        Assert.AreEqual("BATCH_APPROVED_REVIEW_INVALID", wrongManifest.ErrorCode);
        Assert.AreEqual(0, backend.GenerateCalls);

        var completed = await processor.ApproveAndGenerateAsync(plan, staleApprovedProjection, CancellationToken.None);
        Assert.AreEqual(AgentBatchFileState.Completed, completed.State,
            completed.ErrorCode + ":" + completed.ReviewGateCode);
        Assert.IsNull(completed.ErrorCode);
        Assert.IsFalse(completed.Retryable);
        Assert.AreEqual(1, completed.ExplicitDecisionCount);
        Assert.AreEqual(1, backend.GenerateCalls);
        Assert.AreEqual(prepared.InputTokens, completed.InputTokens);
        Assert.AreEqual(prepared.OutputTokens, completed.OutputTokens);
        Assert.AreEqual(prepared.ProviderRequests, completed.ProviderRequests);
        Assert.IsTrue(File.Exists(output));
        Assert.IsNotNull(backend.LastAutomationAuthority);
        Assert.AreEqual(plan.BatchId, backend.LastAutomationAuthority.BatchId);
        Assert.AreEqual(plan.ManifestHash, backend.LastAutomationAuthority.ManifestHash);
        Assert.AreEqual(backend.Review!.ContextHash, backend.LastAutomationAuthority.ContextHash);
        Assert.AreEqual(backend.LastAutomationAuthority, backend.Review.ReviewAutomationReceipt);
        StringAssert.StartsWith(completed.ChildGenerationApprovalHash, "sha256:");
        Assert.AreEqual(2, Directory.EnumerateFiles(Path.Combine(_root, "bindings", plan.BatchId.ToString("D")), "*.json")
            .Count(path => !path.EndsWith(".checkpoint.json", StringComparison.Ordinal)));
        var replay = await processor.ApproveAndGenerateAsync(plan, prepared, CancellationToken.None);
        Assert.AreEqual(AgentBatchFileState.Completed, replay.State);
        Assert.AreEqual(1, backend.GenerateCalls);
        Assert.AreEqual(prepared.InputTokens, replay.InputTokens);
        Assert.AreEqual(prepared.OutputTokens, replay.OutputTokens);
        Assert.AreEqual(prepared.ProviderRequests, replay.ProviderRequests);
    }

    [TestMethod]
    public async Task ApprovedReviewCorrectionRequiresExactFailedPrewriteBatchAndOneNumericSafeEdit()
    {
        var backend = new FakeBackend(highRisk: false, reviewSource: "NOTE 100 THEN 5",
            reviewProposal: "NOTE 5 THEN 100");
        var batchOutput = Path.Combine(_input, "batch-approved-correction");
        Directory.CreateDirectory(batchOutput);
        var workspace = Path.Combine(_root, "jobs");
        var configuration = new AgentBetaConfiguration("dwg-agent-beta-bootstrap/1.0", "AgentBeta",
            true, true, workspace, Path.Combine(_root, "logs"), AllowedDwgRoot: _input,
            OpenAiEnabled: true, OutputDwgRoot: _output, BatchOutputRoots: [batchOutput],
            ConfiguredAccessibleModels: [TranslationRouting.Terra, TranslationRouting.Luna, TranslationRouting.Sol],
            BatchExecutionEnabled: true);
        var workflow = new AgentWorkflowService(configuration, backend, () => _now,
            cadProcessExists: () => false);
        var processor = new ProductionAgentBatchFileProcessor(workflow, Path.Combine(_root, "correction-bindings"),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(2));
        var source = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(batchOutput, "source-ENG.dwg");
        var manifest = new AgentBatchManifestEntry("source.dwg", _source, output,
            source.Snapshot!.Bytes, source.Snapshot.Hash, source.Snapshot.LastWriteTimeUtc);
        var plan = new AgentBatchPlan("correction-plan", Guid.NewGuid(), "sha256:" + new string('6', 64),
            _input, batchOutput, "en-US", "Auto", ArchitecturalMepTerminologyPolicy.Version,
            AgentBatchPolicy.ContextualPolicyVersion, [manifest],
            new("approval", "consent", "generation", _now.AddMinutes(30), true), _now,
            ContextPolicyVersion: CadSemanticContextBuilder.PolicyVersionOneTwo);
        var prepared = await processor.PrepareAsync(plan, manifest, CancellationToken.None);
        Assert.AreEqual(AgentBatchFileState.Reviewing, prepared.State,
            prepared.ErrorCode + ":" + prepared.ReviewGateCode);
        var oldAuthority = new ReviewAutomationReceipt(ReviewAutomationPolicy.ContextualAgentCreateNew,
            plan.BatchId, plan.ManifestHash, backend.Review!.ContextHash!,
            "sha256:" + new string('7', 64), "sha256:" + new string('8', 64));
        backend.ConfigureExternallyApproved(oldAuthority);
        var approved = backend.Job!;
        var review = backend.Review!;
        var batch = new AgentBatchDocument(plan.BatchId, 9, "ReviewRequired", plan.ManifestHash,
            _input, batchOutput, _now, _now, _now,
            [new AgentBatchFileProgress(manifest.RelativePath, manifest.Sha256, manifest.OutputPath,
                AgentBatchFileState.Failed, approved.JobId, ErrorCode: "BATCH_HUMAN_REVIEW_REQUIRED",
                JobVersion: approved.Version, ReviewVersion: review.Version,
                ReviewGateCode: "REVIEW_INVARIANT_FAILED")]);
        var batchPath = Path.Combine(workspace, "batches", plan.BatchId.ToString("D"), "batch.json");
        Directory.CreateDirectory(Path.GetDirectoryName(batchPath)!);
        File.WriteAllText(batchPath, JsonSerializer.Serialize(batch, TestJson));
        var newAuthority = oldAuthority with
        {
            ReviewerReportHash = "sha256:" + new string('9', 64),
            QaReportHash = "sha256:" + new string('a', 64)
        };
        var decision = new AgentReviewDecision(review.Rows.Single().SegmentId, "edit", "NOTE 100 THEN 5");
        AgentTranslationReviewApplyRequest Request(string key) => new(approved.JobId, approved.Version,
            [decision], false, null, key, review.Version, review.ContextHash, newAuthority,
            ReviseApproved: true);

        var denied = await workflow.ApplyReviewAsync(Request("correction-wrong-batch"), CancellationToken.None);
        Assert.AreEqual("APPROVED_REVIEW_BATCH_EVIDENCE_INVALID", denied.Error!.Code);
        Assert.AreEqual(approved.Version, backend.Job!.Version);
        File.WriteAllText(batchPath, JsonSerializer.Serialize(batch with { State = "CompletedWithFailures" }, TestJson));
        var corrected = await workflow.ApplyReviewAsync(Request("correction-exact-batch"), CancellationToken.None);
        Assert.IsTrue(corrected.Success, corrected.Error?.Code);
        Assert.IsTrue(corrected.Data!["revisedApproved"]!.GetValue<bool>());
        Assert.AreEqual(approved.Version + 1, backend.Job!.Version);
        Assert.AreEqual(review.Version + 1, backend.Review!.Version);
        Assert.AreEqual(newAuthority, backend.Review.ReviewAutomationReceipt);
        Assert.AreEqual(0, backend.GenerateCalls);
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task ProductionBatchProcessorPreservesRetryableFromInitialAndResumedChildFailure()
    {
        var backend = new FakeBackend(
            highRisk: false,
            prepareFailureCode: "OPENAI_TIMEOUT",
            prepareFailureRetryable: true);
        var batchOutput = Path.Combine(_input, "batch-timeout");
        Directory.CreateDirectory(batchOutput);
        var configuration = new AgentBetaConfiguration(
            "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true,
            Path.Combine(_root, "jobs"), Path.Combine(_root, "logs"),
            AllowedDwgRoot: _input, OpenAiEnabled: true, OutputDwgRoot: _output,
            ConfiguredAccessibleModels: [TranslationRouting.Terra],
            BatchOutputRoots: [batchOutput], BatchExecutionEnabled: true);
        var workflow = new AgentWorkflowService(configuration, backend, () => _now);
        var processor = new ProductionAgentBatchFileProcessor(
            workflow, Path.Combine(_root, "timeout-bindings"),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(2));
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(batchOutput, "source-ENG.dwg");
        var file = new AgentBatchManifestEntry(
            "source.dwg", _source, output,
            snapshot.Snapshot!.Bytes, snapshot.Snapshot.Hash, snapshot.Snapshot.LastWriteTimeUtc);
        var plan = new AgentBatchPlan(
            "parent-timeout-plan", Guid.NewGuid(), "sha256:" + new string('c', 64),
            _input, batchOutput, "en-US", "Auto", ArchitecturalMepTerminologyPolicy.Version,
            AgentBatchPolicy.PolicyVersion, [file],
            new("parent-timeout-approval", "parent-timeout-consent", "parent-timeout-generation", _now.AddMinutes(30), true), _now);

        var initial = await processor.PrepareAsync(plan, file, CancellationToken.None);
        var resumed = await processor.PrepareAsync(plan, file, CancellationToken.None);
        foreach (var failure in new[] { initial, resumed })
        {
            Assert.AreEqual(AgentBatchFileState.Failed, failure.State);
            Assert.AreEqual("OPENAI_TIMEOUT", failure.ErrorCode);
            Assert.IsTrue(failure.Retryable);
        }
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("conflict")]
    [DataRow("invalid-neighbor")]
    public async Task ContextualBatchStopsOnUnknownConflictOrInvalidNeighborWithoutGeneration(string fault)
    {
        var backend = new FakeBackend(highRisk: false, contextualFault: fault);
        var batchOutput = Path.Combine(_input, "batch-context-fault-" + fault);
        Directory.CreateDirectory(batchOutput);
        var configuration = new AgentBetaConfiguration(
            "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true,
            Path.Combine(_root, "jobs"), Path.Combine(_root, "logs"),
            AllowedDwgRoot: _input, OpenAiEnabled: true, OutputDwgRoot: _output,
            ConfiguredAccessibleModels: [TranslationRouting.Terra],
            BatchOutputRoots: [batchOutput], BatchExecutionEnabled: true);
        var workflow = new AgentWorkflowService(configuration, backend, () => _now);
        var processor = new ProductionAgentBatchFileProcessor(workflow, Path.Combine(_root, "bindings-" + fault),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(2));
        var source = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(batchOutput, "source-ENG.dwg");
        var file = new AgentBatchManifestEntry("source.dwg", _source, output,
            source.Snapshot!.Bytes, source.Snapshot.Hash, source.Snapshot.LastWriteTimeUtc);
        var plan = new AgentBatchPlan("fault-plan-" + fault, Guid.NewGuid(), "sha256:" + new string('6', 64),
            _input, batchOutput, "en-US", "Auto", ArchitecturalMepTerminologyPolicy.Version,
            AgentBatchPolicy.ContextualPolicyVersion, [file],
            new("approval", "consent", "generation", _now.AddMinutes(30), true), _now);
        var prepared = await processor.PrepareAsync(plan, file, CancellationToken.None);
        var authority = new ReviewAutomationReceipt(
            ReviewAutomationPolicy.ContextualAgentCreateNew, plan.BatchId, plan.ManifestHash,
            backend.Review!.ContextHash!, "sha256:" + new string('7', 64), "sha256:" + new string('8', 64));
        backend.ConfigureExternallyApproved(authority);

        var result = await processor.ApproveAndGenerateAsync(plan, prepared, CancellationToken.None);

        Assert.AreEqual(AgentBatchFileState.Failed, result.State);
        Assert.AreEqual("BATCH_HUMAN_REVIEW_REQUIRED", result.ErrorCode);
        Assert.AreEqual(0, backend.GenerateCalls);
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task LegacyBatchDoesNotInjectHvacRaisedFloorOrCeilingDefaults()
    {
        var backend = new FakeBackend(highRisk: false, reviewSource: "N.P.F. +1.00", reviewProposal: "N.P.F. +1.00");
        var batchOutput = Path.Combine(_input, "batch-legacy-no-defaults");
        Directory.CreateDirectory(batchOutput);
        var configuration = new AgentBetaConfiguration(
            "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true,
            Path.Combine(_root, "jobs"), Path.Combine(_root, "logs"),
            AllowedDwgRoot: _input, OpenAiEnabled: true, OutputDwgRoot: _output,
            ConfiguredAccessibleModels: [TranslationRouting.Terra],
            BatchOutputRoots: [batchOutput], BatchExecutionEnabled: true);
        var workflow = new AgentWorkflowService(configuration, backend, () => _now);
        var processor = new ProductionAgentBatchFileProcessor(workflow, Path.Combine(_root, "legacy-bindings"),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(2));
        var source = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(batchOutput, "source-ENG.dwg");
        var file = new AgentBatchManifestEntry("source.dwg", _source, output,
            source.Snapshot!.Bytes, source.Snapshot.Hash, source.Snapshot.LastWriteTimeUtc);
        var plan = new AgentBatchPlan("legacy-plan", Guid.NewGuid(), "sha256:" + new string('5', 64),
            _input, batchOutput, "en-US", "Auto", ArchitecturalMepTerminologyPolicy.Version,
            AgentBatchPolicy.LegacyPolicyVersion, [file],
            new("approval", "consent", "generation", _now.AddMinutes(30), true), _now);
        var prepared = await processor.PrepareAsync(plan, file, CancellationToken.None);

        var result = await processor.ApproveAndGenerateAsync(plan, prepared, CancellationToken.None);

        Assert.AreEqual(AgentBatchFileState.Failed, result.State);
        Assert.AreEqual("BATCH_HUMAN_REVIEW_REQUIRED", result.ErrorCode);
        Assert.AreEqual(0, backend.GenerateCalls);
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task ContextualReviewApplyFailsClosedOnHashDriftAndPersistsExactAuthority()
    {
        var backend = new FakeBackend(highRisk: true);
        var service = Service(backend);
        var source = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var batchId = Guid.NewGuid();
        var manifestHash = "sha256:" + new string('7', 64);
        var output = Path.Combine(_output, "context-bound.dwg");
        var plan = await service.CreateBatchChildTranslationPlanAsync(new(
            _source, "en-US", "Auto", null, output,
            new(batchId, manifestHash, AgentBatchPolicy.ContextualPolicyVersion,
                source.Snapshot!.Hash, output, 0)), CancellationToken.None);
        var started = await service.PrepareAsync(new(
            Text(plan, "planId"),
            plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            source.Snapshot.Hash,
            plan.Data["approval"]!["consent"]!.GetValue<string>(),
            "prepare-context-bound"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);
        var review = await service.GetReviewAsync(new(jobId, IncludeText: true), CancellationToken.None);
        Assert.IsTrue(backend.IsContextual, "The child specification lost its contextual batch scope.");
        Assert.IsNotNull(backend.Review?.ContextHash, "The contextual fake did not create an aggregate review hash.");
        var row = review.Data!["rows"]![0]!.AsObject();
        var reviewVersion = review.Data["reviewVersion"]!.GetValue<long>();
        var contextHash = review.Data["contextHash"]!.GetValue<string>();
        var authority = new ReviewAutomationReceipt(
            ReviewAutomationPolicy.ContextualAgentCreateNew, batchId, manifestHash, contextHash,
            "sha256:" + new string('8', 64), "sha256:" + new string('9', 64));
        var decisions = new[] { new AgentReviewDecision(
            row["segmentId"]!.GetValue<string>(), "approve") };

        var bulk = await service.ApplyReviewAsync(new(jobId, backend.Job!.Version, null, true,
            AgentWorkflowService.BulkApproval(jobId, backend.Job.Version), "review-context-bulk",
            reviewVersion, contextHash, authority), CancellationToken.None);
        Assert.AreEqual("CONTEXTUAL_REVIEW_BULK_NOT_ALLOWED", bulk.Error!.Code);

        var missingAuthority = await service.ApplyReviewAsync(new(
            jobId, backend.Job!.Version, decisions, false, null, "review-context-no-authority",
            reviewVersion, contextHash), CancellationToken.None);
        Assert.AreEqual("REVIEW_AUTOMATION_AUTHORITY_INVALID", missingAuthority.Error!.Code);

        var stale = await service.ApplyReviewAsync(new(
            jobId, backend.Job!.Version, decisions, false, null, "review-context-stale",
            reviewVersion, "sha256:" + new string('0', 64), authority), CancellationToken.None);

        Assert.IsFalse(stale.Success);
        Assert.AreEqual("REVIEW_CONTEXT_MISMATCH", stale.Error!.Code);
        var applied = await service.ApplyReviewAsync(new(
            jobId, backend.Job.Version, decisions, false, null, "review-context-valid",
            reviewVersion, contextHash, authority), CancellationToken.None);
        Assert.IsTrue(applied.Success, applied.Error?.Code);
        Assert.AreEqual(authority, backend.LastAutomationAuthority);
        Assert.AreEqual(authority, backend.Review!.ReviewAutomationReceipt);
    }

    [TestMethod]
    public async Task IndividualContextualReviewAllowsManualApprovalWithoutAutomationReceipt()
    {
        var backend = new FakeBackend(highRisk: false, forceContextual: true);
        var service = Service(backend);
        var plan = await service.CreateTranslationPlanAsync(new(
            _source, "de", "Auto", null, Path.Combine(_output, "individual-context.dwg")), CancellationToken.None);
        var started = await service.PrepareAsync(new(Text(plan, "planId"),
            plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(),
            plan.Data["approval"]!["consent"]!.GetValue<string>(), "prepare-individual-context"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);
        var review = await service.GetReviewAsync(new(jobId), CancellationToken.None);

        var applied = await service.ApplyReviewAsync(new(jobId, backend.Job!.Version, null, true,
            AgentWorkflowService.BulkApproval(jobId, backend.Job.Version), "review-individual-context",
            review.Data!["reviewVersion"]!.GetValue<long>(), review.Data["contextHash"]!.GetValue<string>()),
            CancellationToken.None);

        Assert.IsTrue(applied.Success, applied.Error?.Code);
        Assert.AreEqual(JobState.Approved, backend.Job!.State);
        Assert.IsNull(backend.Review!.ReviewAutomationReceipt);
    }

    [TestMethod]
    public async Task ConcurrentContextualReviewApplyHasOneReceiptWinnerAndOneVersionConflict()
    {
        var backend = new FakeBackend(highRisk: false);
        var service = Service(backend);
        var source = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var batchId = Guid.NewGuid();
        var manifestHash = "sha256:" + new string('3', 64);
        var output = Path.Combine(_output, "concurrent-review.dwg");
        var plan = await service.CreateBatchChildTranslationPlanAsync(new(_source, "en-US", "Auto", null, output,
            new(batchId, manifestHash, AgentBatchPolicy.ContextualPolicyVersion, source.Snapshot!.Hash, output, 0)),
            CancellationToken.None);
        var started = await service.PrepareAsync(new(Text(plan, "planId"),
            plan.Data!["approval"]!["approvalId"]!.GetValue<string>(), source.Snapshot.Hash,
            plan.Data["approval"]!["consent"]!.GetValue<string>(), "prepare-concurrent-review"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);
        var review = await service.GetReviewAsync(new(jobId, IncludeText: true), CancellationToken.None);
        var row = review.Data!["rows"]![0]!.AsObject();
        var reviewVersion = review.Data["reviewVersion"]!.GetValue<long>();
        var contextHash = review.Data["contextHash"]!.GetValue<string>();
        var firstAuthority = new ReviewAutomationReceipt(ReviewAutomationPolicy.ContextualAgentCreateNew, batchId,
            manifestHash, contextHash, "sha256:" + new string('1', 64), "sha256:" + new string('2', 64));
        var secondAuthority = firstAuthority with
        {
            ReviewerReportHash = "sha256:" + new string('4', 64),
            QaReportHash = "sha256:" + new string('5', 64)
        };
        var decisions = new[] { new AgentReviewDecision(row["segmentId"]!.GetValue<string>(), "approve") };
        backend.EnableReviewApprovalRace();

        var results = await Task.WhenAll(
            service.ApplyReviewAsync(new(jobId, backend.Job!.Version, decisions, false, null, "review-race-first",
                reviewVersion, contextHash, firstAuthority), CancellationToken.None),
            service.ApplyReviewAsync(new(jobId, backend.Job!.Version, decisions, false, null, "review-race-second",
                reviewVersion, contextHash, secondAuthority), CancellationToken.None));

        Assert.AreEqual(1, results.Count(result => result.Success));
        Assert.AreEqual(1, results.Count(result => result.Error?.Code == "REVIEW_VERSION_CONFLICT"));
        Assert.AreEqual(JobState.Approved, backend.Job!.State);
        Assert.AreEqual(reviewVersion + 1, backend.Review!.Version);
        Assert.AreEqual(results[0].Success ? firstAuthority : secondAuthority, backend.Review.ReviewAutomationReceipt);
    }

    [TestMethod]
    public async Task LegacyBatchWithContextUsesVersionAndHashButNoAutomationReceipt()
    {
        var backend = new FakeBackend(highRisk: false, reviewSource: "PUMP", reviewProposal: "PUMP", forceContextual: true);
        var batchOutput = Path.Combine(_input, "batch-legacy-context");
        Directory.CreateDirectory(batchOutput);
        var workflow = new AgentWorkflowService(new AgentBetaConfiguration(
            "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true, Path.Combine(_root, "jobs"), Path.Combine(_root, "logs"),
            AllowedDwgRoot: _input, OpenAiEnabled: true, OutputDwgRoot: _output,
            ConfiguredAccessibleModels: [TranslationRouting.Terra], BatchOutputRoots: [batchOutput], BatchExecutionEnabled: true),
            backend, () => _now);
        var processor = new ProductionAgentBatchFileProcessor(workflow, Path.Combine(_root, "legacy-context-bindings"),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(2));
        var source = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(batchOutput, "source-ENG.dwg");
        var file = new AgentBatchManifestEntry("source.dwg", _source, output,
            source.Snapshot!.Bytes, source.Snapshot.Hash, source.Snapshot.LastWriteTimeUtc);
        var plan = new AgentBatchPlan("legacy-context-plan", Guid.NewGuid(), "sha256:" + new string('4', 64),
            _input, batchOutput, "en-US", "Auto", ArchitecturalMepTerminologyPolicy.Version,
            AgentBatchPolicy.LegacyPolicyVersion, [file], new("approval", "consent", "generation", _now.AddMinutes(30), true), _now);
        var prepared = await processor.PrepareAsync(plan, file, CancellationToken.None);

        var completed = await processor.ApproveAndGenerateAsync(plan, prepared, CancellationToken.None);

        Assert.AreEqual(AgentBatchFileState.Completed, completed.State, completed.ErrorCode);
        Assert.AreEqual(1, backend.GenerateCalls);
        Assert.IsNull(backend.LastAutomationAuthority);
    }

    [TestMethod]
    public async Task JobNextActionUsesRetryableAgentFailureFallback()
    {
        var backend = new FakeBackend(highRisk: false);
        var service = Service(backend);
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var created = await backend.CreateAsync(new(
            _source, Path.Combine(_output, "agent-failure.dwg"), snapshot.Snapshot!.Hash,
            null, "en-US", "prompt", null), CancellationToken.None);
        await backend.FailAsync(created.Value!.JobId, "WORKFLOW_BACKGROUND_STALLED", true, CancellationToken.None);

        var next = await service.JobNextActionAsync(created.Value.JobId, CancellationToken.None);

        Assert.IsTrue(next.Success, next.Error?.Code);
        Assert.IsTrue(next.Data!["recoverable"]!.GetValue<bool>());
        Assert.AreEqual("dwg_job_status", next.Data["tool"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SharedOperationStatusCanBeReadWhileAnotherHandleHasWriteAccess()
    {
        var backend = new FakeBackend(highRisk: false, delayPrepare: true);
        var service = Service(backend);
        var plan = await service.CreateTranslationPlanAsync(new(
            _source, "de", "Auto", null, Path.Combine(_output, "sharing.dwg")), CancellationToken.None);
        var started = await service.PrepareAsync(new(
            Text(plan, "planId"),
            plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(),
            plan.Data["approval"]!["consent"]!.GetValue<string>(),
            "prepare-sharing"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));
        var operationId = Text(started, "operationId");
        var path = Path.Combine(
            _root, "logs", "workflow", "operations", operationId + ".json");
        AgentEnvelope status;
        await using (var writer = new FileStream(
                         path, FileMode.Open, FileAccess.ReadWrite,
                         FileShare.ReadWrite | FileShare.Delete))
        {
            status = await service.JobStatusAsync(jobId, CancellationToken.None);
        }

        Assert.IsTrue(status.Success, status.Error?.Code);
        Assert.AreEqual(operationId, status.Data!["operation"]!["operationId"]!.GetValue<string>());
        Assert.AreEqual("Running", status.Data["operation"]!["state"]!.GetValue<string>());
        backend.ReleasePrepare();
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);
        await WaitForAsync(async () =>
        {
            var completed = await service.JobStatusAsync(jobId, CancellationToken.None);
            return completed.Data?["operation"]?["state"]?.GetValue<string>() == "Completed";
        });
        await Task.Delay(50);
    }

    [TestMethod]
    public async Task FailedGeometryRecoveryReopensOnlyCompleteTolerableEvidenceAndIsIdempotent()
    {
        var backend = new FakeBackend(highRisk: false);
        var service = Service(backend);
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(_output, "recovery-geometry.dwg");
        var created = await backend.CreateAsync(new(_source, output, snapshot.Snapshot!.Hash, null, "en-US", "prompt", null), CancellationToken.None);
        backend.ConfigureFailedGeometry(GeometryDiagnostics());

        var reconciled = await service.ReconcileFailedGeometryForReviewAsync(new(
            created.Value!.JobId, 7, snapshot.Snapshot.Hash, output), CancellationToken.None);

        Assert.IsTrue(reconciled.Success, reconciled.Error?.Code);
        Assert.AreEqual("ReviewRequired", Text(reconciled, "state"));
        Assert.AreEqual(8L, TextLong(reconciled, "jobVersion"));
        Assert.AreEqual(JobState.ReviewRequired, backend.Job!.State);
        var replay = await service.ReconcileFailedGeometryForReviewAsync(new(
            created.Value.JobId, 7, snapshot.Snapshot.Hash, output), CancellationToken.None);
        Assert.IsTrue(replay.Success, replay.Error?.Code);
        Assert.IsTrue(replay.Data!["idempotentReplay"]!.GetValue<bool>());
        Assert.AreEqual(1, backend.GeometryReconciliationCalls);
    }

    [TestMethod]
    public async Task FailedGeometryRecoveryFailsClosedForMaterialOrIncompleteEvidenceAndPayloadMismatches()
    {
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        foreach (var scenario in new[] { "material", "missing", "source", "review" })
        {
            var backend = new FakeBackend(highRisk: false);
            var service = Service(backend);
            var output = Path.Combine(_output, scenario + ".dwg");
            var created = await backend.CreateAsync(new(_source, output, snapshot.Snapshot!.Hash, null, "en-US", "prompt", null), CancellationToken.None);
            backend.ConfigureFailedGeometry(
                scenario == "missing" ? null : GeometryDiagnostics(material: scenario == "material"),
                reviewMatches: scenario != "review");
            var requestHash = scenario == "source" ? "sha256:" + new string('0', 64) : snapshot.Snapshot.Hash;

            var result = await service.ReconcileFailedGeometryForReviewAsync(new(
                created.Value!.JobId, 7, requestHash, output), CancellationToken.None);

            Assert.IsFalse(result.Success, scenario);
            Assert.AreEqual("BATCH_CHILD_REVIEW_NOT_READY", result.Error!.Code, scenario);
            Assert.AreEqual(JobState.Failed, backend.Job!.State, scenario);
            Assert.AreEqual(0, backend.GeometryReconciliationCalls, scenario);
        }
    }

    [TestMethod]
    public async Task GenerationReconciliationIsPlanBoundIdempotentAndDoesNotInvokeCadOrTranslation()
    {
        var backend = new FakeBackend(highRisk: false);
        var service = Service(backend);
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(_output, "reconcile-generation.dwg");
        var created = await backend.CreateAsync(new(_source, output, snapshot.Snapshot!.Hash, null, "en-US", "prompt", null), CancellationToken.None);
        backend.ConfigureFailedGeneration("IPC_TIMEOUT", retryable: true);

        var plan = await service.CreateGenerationReconciliationPlanAsync(new(created.Value!.JobId), CancellationToken.None);
        Assert.IsTrue(plan.Success, plan.Error?.Code);
        Assert.AreEqual(7L, TextLong(plan, "expectedJobVersion"));
        var applied = await service.ApplyGenerationReconciliationAsync(new(
            Text(plan, "reconciliationPlanId"), plan.Data!["approval"]!["approvalId"]!.GetValue<string>(), 7,
            plan.Data["approval"]!["consent"]!.GetValue<string>(), "generation-reconcile-001"), CancellationToken.None);
        Assert.IsTrue(applied.Success, applied.Error?.Code);
        Assert.AreEqual("Approved", Text(applied, "state"));
        Assert.AreEqual(8L, TextLong(applied, "jobVersion"));
        Assert.AreEqual(0, backend.PrepareCalls);
        Assert.AreEqual(0, backend.GenerateCalls);

        var replay = await service.ApplyGenerationReconciliationAsync(new(
            Text(plan, "reconciliationPlanId"), plan.Data["approval"]!["approvalId"]!.GetValue<string>(), 7,
            plan.Data["approval"]!["consent"]!.GetValue<string>(), "generation-reconcile-001"), CancellationToken.None);
        Assert.IsTrue(replay.Success);
        Assert.IsTrue(replay.Data!["idempotentReplay"]!.GetValue<bool>());
        Assert.AreEqual(1, backend.GenerationReconciliationCalls);
    }

    [TestMethod]
    public async Task GenerationReconciliationFailsClosedForSourceOutputCandidateAndReviewMismatches()
    {
        foreach (var scenario in new[] { "source", "candidate", "review", "failure" })
        {
            var backend = new FakeBackend(highRisk: false);
            var service = Service(backend);
            var snapshot = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
            var output = Path.Combine(_output, scenario + ".dwg");
            var created = await backend.CreateAsync(new(_source, output, snapshot.Snapshot!.Hash, null, "en-US", "prompt", null), CancellationToken.None);
            backend.ConfigureFailedGeneration(scenario == "failure" ? "OUTPUT_SIZE_UNAVAILABLE" : "IPC_TIMEOUT", retryable: true,
                reviewMatches: scenario != "review");
            if (scenario == "source") File.AppendAllText(_source, "CHANGED");
            if (scenario == "candidate") File.WriteAllText(Path.Combine(_output, $".{scenario}.candidate-{created.Value!.JobId:N}.dwg"), "candidate");

            var plan = await service.CreateGenerationReconciliationPlanAsync(new(created.Value!.JobId), CancellationToken.None);
            Assert.IsFalse(plan.Success, scenario);
            Assert.AreEqual(JobState.Failed, backend.Job!.State, scenario);
            Assert.AreEqual(0, backend.GenerationReconciliationCalls, scenario);
            if (scenario == "source")
            {
                File.WriteAllText(_source, "SYNTHETIC-DWG-BYTES-NOT-A-REAL-DRAWING");
            }
        }
    }

    [TestMethod]
    public async Task ProductionProcessorReconcilesTolerableFailedGeometryBeforeExposingReview()
    {
        var backend = new FakeBackend(highRisk: false);
        var batchOutput = Path.Combine(_input, "recovery-output");
        Directory.CreateDirectory(batchOutput);
        var configuration = new AgentBetaConfiguration("dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true,
            Path.Combine(_root, "jobs"), Path.Combine(_root, "logs"), AllowedDwgRoot: _input, OpenAiEnabled: true,
            OutputDwgRoot: _output, BatchOutputRoots: [batchOutput], BatchExecutionEnabled: true);
        var workflow = new AgentWorkflowService(configuration, backend, () => _now);
        var snapshot = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(batchOutput, "source-ENG.dwg");
        var created = await backend.CreateAsync(new(_source, output, snapshot.Snapshot!.Hash, null, "en-US", "prompt", null), CancellationToken.None);
        backend.ConfigureFailedGeometry(GeometryDiagnostics());
        var manifest = new AgentBatchManifestEntry("source.dwg", _source, output, snapshot.Snapshot.Bytes, snapshot.Snapshot.Hash, snapshot.Snapshot.LastWriteTimeUtc);
        var plan = new AgentBatchPlan("recovery-plan", Guid.NewGuid(), "sha256:" + new string('c', 64), _input, batchOutput,
            "en-US", "Auto", ArchitecturalMepTerminologyPolicy.Version, AgentBatchPolicy.PolicyVersion, [manifest],
            new("approval", "consent", "review", _now.AddMinutes(30), true), _now);
        var processor = new ProductionAgentBatchFileProcessor(workflow, Path.Combine(_root, "bindings"));

        var result = await processor.ReconcileReviewAsync(plan, new(manifest.RelativePath, manifest.Sha256, manifest.OutputPath,
            AgentBatchFileState.Reviewing, created.Value!.JobId, JobVersion: 4), null, CancellationToken.None);

        Assert.AreEqual(AgentBatchFileState.Reviewing, result.State);
        Assert.AreEqual(8L, result.JobVersion);
        Assert.IsNull(result.ErrorCode);
        Assert.AreEqual(1, backend.GeometryReconciliationCalls);
    }

    [TestMethod]
    public async Task BulkApprovalFailsClosedWhenAnySegmentIsHighRisk()
    {
        var backend = new FakeBackend(highRisk: true);
        var service = Service(backend);
        var plan = await service.CreateTranslationPlanAsync(new(_source, "de", "Auto", null, Path.Combine(_output, "high-de.dwg")), CancellationToken.None);
        var started = await service.PrepareAsync(new(
            Text(plan, "planId"),
            plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(),
            plan.Data["approval"]!["consent"]!.GetValue<string>(),
            "prepare-high"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);

        var result = await service.ApplyReviewAsync(new(
            jobId, backend.Job!.Version, null, true,
            AgentWorkflowService.BulkApproval(jobId, backend.Job.Version), "review-high"), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("REVIEW_HIGH_RISK_EXPLICIT_REQUIRED", result.Error!.Code);
        Assert.AreEqual(JobState.ReviewRequired, backend.Job.State);
    }

    [TestMethod]
    public async Task InvalidEditReportsSegmentAndInvariantWithoutText()
    {
        var backend = new FakeBackend(highRisk: false);
        var service = Service(backend);
        var plan = await service.CreateTranslationPlanAsync(new(
            _source, "de", "Auto", null, Path.Combine(_output, "diagnostic-de.dwg")), CancellationToken.None);
        var started = await service.PrepareAsync(new(
            Text(plan, "planId"),
            plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(),
            plan.Data["approval"]!["consent"]!.GetValue<string>(),
            "prepare-diagnostic"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));
        await WaitForAsync(() => backend.Job?.State == JobState.ReviewRequired);
        var segmentId = backend.Review!.Rows.Single().SegmentId;

        var result = await service.ApplyReviewAsync(new(
            jobId, backend.Job!.Version,
            [new AgentReviewDecision(segmentId, "edit", "SAFE_EDIT\n")],
            false, null, "review-diagnostic"), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("REVIEW_EDIT_INVALID", result.Error!.Code);
        Assert.AreEqual($"Segment {segmentId} failed invariant TOKEN_INTEGRITY_FAILED.", result.Error.Message);
        Assert.IsFalse(result.Error.Message.Contains("SAFE_EDIT", StringComparison.Ordinal));
        Assert.AreEqual(JobState.ReviewRequired, backend.Job.State);
    }

    [TestMethod]
    public async Task ExpiredAndReplayedApprovalsAndDivergentIdempotencyFailClosed()
    {
        var backend = new FakeBackend(false);
        var service = Service(backend);
        var plan = await service.CreateTranslationPlanAsync(new(_source, "de", "Auto", null, Path.Combine(_output, "expired.dwg")), CancellationToken.None);
        _now = _now.AddMinutes(31);
        var expired = await service.PrepareAsync(new(
            Text(plan, "planId"), plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(), plan.Data["approval"]!["consent"]!.GetValue<string>(),
            "prepare-expired"), CancellationToken.None);
        Assert.AreEqual("APPROVAL_EXPIRED", expired.Error!.Code);

        _now = _now.AddMinutes(-31);
        var fresh = await service.CreateTranslationPlanAsync(new(_source, "de", "Auto", null, Path.Combine(_output, "fresh.dwg")), CancellationToken.None);
        var request = new AgentTranslationPrepareRequest(
            Text(fresh, "planId"), fresh.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            fresh.Data["source"]!["hash"]!.GetValue<string>(), fresh.Data["approval"]!["consent"]!.GetValue<string>(),
            "prepare-fresh");
        var first = await service.PrepareAsync(request, CancellationToken.None);
        var replay = await service.PrepareAsync(request, CancellationToken.None);
        var approvalReplay = await service.PrepareAsync(request with { IdempotencyKey = "prepare-replay" }, CancellationToken.None);
        var conflict = await service.PrepareAsync(request with { SourceHash = "sha256:" + new string('0', 64) }, CancellationToken.None);
        Assert.IsTrue(first.Success);
        Assert.IsTrue(replay.Success);
        Assert.IsTrue(replay.Data!["idempotentReplay"]!.GetValue<bool>());
        Assert.AreEqual("APPROVAL_REPLAYED", approvalReplay.Error!.Code);
        Assert.AreEqual("IDEMPOTENCY_CONFLICT", conflict.Error!.Code);
        Assert.AreEqual(1, backend.CreateCalls);
    }

    [TestMethod]
    public async Task PlanRejectsOutsideRootAndAmbiguousManualAliasBeforeBackendUse()
    {
        var backend = new FakeBackend(false);
        var service = Service(backend);
        var outside = Path.Combine(_root, "outside.dwg");
        File.WriteAllText(outside, "outside");

        var forbidden = await service.CreateTranslationPlanAsync(new(outside, "de", "Auto", null, null), CancellationToken.None);
        var alias = await service.CreateTranslationPlanAsync(new(_source, "de", "Manual", "gpt-5.6", null), CancellationToken.None);

        Assert.AreEqual("SOURCE_PATH_FORBIDDEN", forbidden.Error!.Code);
        Assert.AreEqual("ROUTING_MODE_INVALID", alias.Error!.Code);
        Assert.AreEqual(0, backend.CreateCalls);
    }

    [TestMethod]
    public async Task PublicTranslationPlanRejectsForgedBatchAuthority()
    {
        var backend = new FakeBackend(false);
        var service = Service(backend);
        var source = await AgentDwgPathPolicy.SnapshotAsync(_source, CancellationToken.None);
        var output = Path.Combine(_output, "forged-child.dwg");

        var result = await service.CreateTranslationPlanAsync(new(_source, "en-US", "Auto", null, output,
            new(Guid.NewGuid(), "sha256:" + new string('a', 64), AgentBatchPolicy.ContextualPolicyVersion,
                source.Snapshot!.Hash, output, 0)), CancellationToken.None);

        Assert.AreEqual("BATCH_CHILD_AUTHORITY_INTERNAL_ONLY", result.Error!.Code);
        Assert.AreEqual(0, backend.CreateCalls);
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.Combine(_root, "logs", "workflow", "plans"), "*.json").Count());
    }

    [TestMethod]
    public async Task PrepareRejectsTamperedDurableContextBindingsAndConfigurationDrift()
    {
        var backend = new FakeBackend(false);
        var service = Service(backend);
        var mutations = new (string Name, JsonNode Value)[]
        {
            ("promptTemplateVersion", JsonValue.Create(TranslationReviewWorkflow.LegacyPromptTemplateVersion)!),
            ("contextPolicyVersion", JsonValue.Create("cad-semantic-context/unknown")!),
            ("includeNeighborExcerpts", JsonValue.Create(false)!),
            ("reviewIncludeText", JsonValue.Create(false)!),
            ("maximumNeighborExcerpts", JsonValue.Create(5)!),
            ("maximumNeighborExcerptScalars", JsonValue.Create(161)!),
            ("maximumNeighborExcerptScalarsPerSegment", JsonValue.Create(513)!),
            ("outboundPurpose", JsonValue.Create("different-purpose")!),
            ("planKind", JsonValue.Create("batch-child")!)
        };

        foreach (var mutation in mutations)
        {
            var plan = await service.CreateTranslationPlanAsync(new(_source, "de", "Auto", null,
                Path.Combine(_output, $"tampered-{mutation.Name}.dwg")), CancellationToken.None);
            var planId = Text(plan, "planId");
            var path = Path.Combine(_root, "logs", "workflow", "plans", planId + ".json");
            var durable = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            durable["binding"]!.AsObject()[mutation.Name] = mutation.Value.DeepClone();
            await File.WriteAllTextAsync(path, durable.ToJsonString());

            var result = await service.PrepareAsync(new(planId,
                plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
                plan.Data["source"]!["hash"]!.GetValue<string>(),
                plan.Data["approval"]!["consent"]!.GetValue<string>(),
                "tamper-" + mutation.Name), CancellationToken.None);

            Assert.AreEqual("TRANSLATION_PLAN_BINDING_INVALID", result.Error!.Code, mutation.Name);
        }

        var driftPlan = await service.CreateTranslationPlanAsync(new(_source, "de", "Auto", null,
            Path.Combine(_output, "config-drift.dwg")), CancellationToken.None);
        var driftedService = Service(backend, promptTemplateVersion: TranslationReviewWorkflow.LegacyPromptTemplateVersion);
        var drift = await driftedService.PrepareAsync(new(Text(driftPlan, "planId"),
            driftPlan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            driftPlan.Data["source"]!["hash"]!.GetValue<string>(),
            driftPlan.Data["approval"]!["consent"]!.GetValue<string>(), "config-drift"), CancellationToken.None);

        Assert.AreEqual("TRANSLATION_PLAN_BINDING_INVALID", drift.Error!.Code);
        Assert.AreEqual(0, backend.CreateCalls);
    }

    [TestMethod]
    public async Task RestartReconcilesOrphanedRunningOperationWithoutReplayingBackend()
    {
        var backend = new FakeBackend(false);
        var service = Service(backend, _ => Task.CompletedTask,
            new(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10)));
        var plan = await service.CreateTranslationPlanAsync(new(
            _source, "de", "Auto", null, Path.Combine(_output, "orphan.dwg")), CancellationToken.None);
        var started = await service.PrepareAsync(new(
            Text(plan, "planId"), plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(), plan.Data["approval"]!["consent"]!.GetValue<string>(),
            "prepare-orphan"), CancellationToken.None);

        _now = _now.AddMinutes(11);
        var reconciled = await service.ReconcileOrphanedOperationsAsync(CancellationToken.None);
        var status = await service.JobStatusAsync(Guid.Parse(Text(started, "jobId")), CancellationToken.None);

        Assert.AreEqual(1, reconciled);
        Assert.AreEqual("WORKFLOW_BACKGROUND_STALLED", status.Data!["operation"]!["errorCode"]!.GetValue<string>());
        Assert.IsTrue(status.Data["operation"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual(0, backend.PrepareCalls);
    }

    [TestMethod]
    public async Task DeadlineFencesLateBackendCompletionAndDoesNotReplayCad()
    {
        var backend = new FakeBackend(false, delayPrepare: true);
        // Keep this synthetic timeout test fast while exercising the production
        // size-bounded deadline path. The normal default is deliberately minutes.
        var service = Service(backend, supervision: new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(30)),
            cadExchangeSeconds: 1);
        var plan = await service.CreateTranslationPlanAsync(new(
            _source, "de", "Auto", null, Path.Combine(_output, "late.dwg")), CancellationToken.None);
        var started = await service.PrepareAsync(new(
            Text(plan, "planId"), plan.Data!["approval"]!["approvalId"]!.GetValue<string>(),
            plan.Data["source"]!["hash"]!.GetValue<string>(), plan.Data["approval"]!["consent"]!.GetValue<string>(),
            "prepare-late"), CancellationToken.None);
        var jobId = Guid.Parse(Text(started, "jobId"));

        AgentEnvelope? observedStatus = null;
        await WaitForAsync(async () =>
        {
            observedStatus = await service.JobStatusAsync(jobId, CancellationToken.None);
            return observedStatus.Data!["operation"]!["errorCode"]?.GetValue<string>() == "WORKFLOW_BACKGROUND_STALLED";
        }, () => observedStatus?.Data?["operation"]?.ToJsonString());
        backend.ReleasePrepare();
        await Task.Delay(30);
        var final = await service.JobStatusAsync(jobId, CancellationToken.None);

        Assert.AreEqual("Failed", final.Data!["operation"]!["state"]!.GetValue<string>());
        Assert.AreEqual("WORKFLOW_BACKGROUND_STALLED", final.Data["operation"]!["errorCode"]!.GetValue<string>());
        Assert.AreEqual(1, backend.PrepareCalls);
    }

    [TestMethod]
    public void CadReceiptPersistsOnlyRedactedRecoveryMetadata()
    {
        var store = new AgentCadLifecycleReceiptStore(Path.Combine(_root, "logs"));
        var jobId = Guid.NewGuid();
        var receipt = new AgentCadLifecycleReceipt(jobId, "operation-token-0123456789", Guid.NewGuid().ToString("D"),
            "sha256:" + new string('a', 64), 1234, Path.Combine(_root, "acad.exe"), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "sha256:" + new string('b', 64), 192, DateTimeOffset.UtcNow,
            CleanupOutcome: "CAD_PROCESS_EXIT_REQUIRED", RequiresProcessTerminationApproval: true);
        store.Upsert(receipt);
        var saved = store.Load(jobId);
        Assert.IsNotNull(saved);
        Assert.AreEqual(receipt.ResponseHash, saved.ResponseHash);
        Assert.AreEqual(192, saved.ExtractedCount);
        Assert.IsTrue(saved.RequiresProcessTerminationApproval);
    }

    private AgentWorkflowService Service(FakeBackend backend,
        Func<Func<Task>, Task>? schedule = null,
        AgentWorkflowSupervisionOptions? supervision = null,
        int cadExchangeSeconds = 120,
        string promptTemplateVersion = TranslationReviewWorkflow.ContextualPromptTemplateVersion) => new(
        new AgentBetaConfiguration(
            "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true,
            Path.Combine(_root, "jobs"), Path.Combine(_root, "logs"),
            AllowedDwgRoot: _input,
            OpenAiEnabled: true,
            OutputDwgRoot: _output,
            CadExchangeSeconds: cadExchangeSeconds,
            CadExchangeMaxSeconds: cadExchangeSeconds,
            PromptTemplateVersion: promptTemplateVersion,
            ConfiguredAccessibleModels: [TranslationRouting.Terra, TranslationRouting.Luna, TranslationRouting.Sol]),
        backend,
        () => _now, schedule, supervision);

    private static string Text(AgentEnvelope envelope, string property) => envelope.Data![property]!.GetValue<string>();
    private static long TextLong(AgentEnvelope envelope, string property) => envelope.Data![property]!.GetValue<long>();

    private static CadInvariantDiagnostics GeometryDiagnostics(bool material = false) => new()
    {
        Schema = CadInvariantDiagnosticsLimits.Schema,
        AddedCount = 0,
        RemovedCount = 0,
        ChangedCount = 1,
        Truncated = false,
        Rows = [new CadInvariantDifference
        {
            InvariantKey = "1A76A|1A76F",
            ChangeKind = "Changed",
            FieldsChanged = ["extents", "invariantRowFingerprint"],
            Before = GeometryRow("-59.86510354484327"),
            After = GeometryRow(material ? "-59.865103544" : "-59.86510354484328"),
            ExtentsDelta = new()
            {
                MinimumX = "0", MinimumY = "0", MinimumZ = "0", MaximumX = "0",
                MaximumY = material ? "8.4327E-10" : "-7.105427357601002E-15", MaximumZ = "0"
            }
        }]
    };

    private static CadInvariantDiagnosticRow GeometryRow(string maximumY) => new()
    {
        EntityHandle = "1A76F",
        OwnerHandle = "1A76A",
        DxfType = "ELLIPSE",
        RuntimeClass = "AcDbEllipse",
        OwnerBlockName = "*Model_Space",
        OwnerClass = "AcDbBlockTableRecord",
        IsTargetText = false,
        IsAnonymousDimensionBlockName = false,
        ReferencedByDimensionCount = 0,
        ReferencedByNonDimensionCount = 0,
        DerivedDimensionGraphicsCandidate = false,
        Layer = "P-PIPE",
        ColorIndex = 256,
        LinetypeHandle = "BYLAYER",
        Lineweight = -1,
        Extents = new() { Minimum = "0,0,0", Maximum = "1," + maximumY + ",1" },
        InvariantRowFingerprint = "sha256:" + new string('f', 64)
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++) await Task.Delay(10);
        Assert.IsTrue(condition(), "Synthetic background operation did not complete.");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, Func<string?>? diagnostic = null)
    {
        for (var attempt = 0; attempt < 300 && !await condition(); attempt++) await Task.Delay(10);
        Assert.IsTrue(await condition(),
            $"Synthetic background operation did not reach its durable state. Observed: {diagnostic?.Invoke() ?? "<none>"}");
    }

    private sealed class FakeBackend(
        bool highRisk,
        bool delayPrepare = false,
        string? prepareFailureCode = null,
        bool prepareFailureRetryable = false,
        string reviewSource = "ORIGINAL_SENSITIVE",
        string reviewProposal = "PROPOSAL_SENSITIVE",
        string? contextualFault = null,
        bool forceContextual = false) : IAgentTranslationWorkflowBackend
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool>? _prepareGate = delayPrepare
            ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        private bool _contextual;
        private bool _reviewApprovalRace;
        private int _jobRaceLoads;
        private int _reviewRaceLoads;
        private TaskCompletionSource<bool> _jobRace = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _reviewRace = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? _generateGate;
        private TaskCompletionSource<bool>? _generateEntered;
        public JobDocument? Job { get; private set; }
        public TranslationReviewSnapshot? Review { get; private set; }
        public int CreateCalls { get; private set; }
        public int GenerateCalls { get; private set; }
        public int GeometryReconciliationCalls { get; private set; }
        public ReviewAutomationReceipt? LastAutomationAuthority { get; private set; }
        public ReviewAutomationScope? LastReviewAutomationScope { get; private set; }
        public bool IsContextual => _contextual;

        public Task<Result<JobDocument>> CreateAsync(DwgTranslationJobSpecification specification, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                CreateCalls++;
                LastReviewAutomationScope = specification.ReviewAutomationScope;
                _contextual = forceContextual || specification.ReviewAutomationScope?.PolicyVersion == AgentBatchPolicy.ContextualPolicyVersion;
                var data = new DwgTranslationJobData(specification, "config", null, null, null);
                Job = new(Guid.NewGuid(), JobState.Draft, 0, DateTimeOffset.UtcNow,
                    JsonSerializer.SerializeToNode(data, Json)!.AsObject());
                return Task.FromResult(Results.Success(Job));
            }
        }

        public async Task<Result<JobDocument>> PrepareAsync(Guid jobId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _prepareCalls);
            if (_prepareGate is not null) await _prepareGate.Task.ConfigureAwait(false);
            lock (_gate)
            {
                var segment = Segment(_contextual, reviewSource, contextualFault);
                var data = Job!.Data.Deserialize<DwgTranslationJobData>(Json)! with { Segments = [segment] };
                var node = JsonSerializer.SerializeToNode(data, Json)!.AsObject();
                if (prepareFailureCode is not null)
                {
                    node["failure"] = new JsonObject
                    {
                        ["code"] = prepareFailureCode,
                        ["retryable"] = prepareFailureRetryable,
                        ["stage"] = "Translating"
                    };
                    Job = Job with
                    {
                        State = JobState.Failed,
                        Version = 4,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Data = node
                    };
                    return Results.Success(Job);
                }

                Job = Job with
                {
                    State = JobState.ReviewRequired,
                    Version = 4,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Data = node
                };
                string? contextHash = null;
                if (_contextual)
                {
                    var aggregate = CadSemanticContextBuilder.AggregateHash([segment]);
                    if (!aggregate.IsSuccess && contextualFault != "invalid-neighbor")
                        throw new InvalidOperationException(aggregate.Error?.Code);
                    contextHash = aggregate.IsSuccess ? aggregate.Value : "sha256:" + new string('f', 64);
                }
                Review = new(jobId, 0, "de", _contextual ? TranslationReviewWorkflow.ContextualPromptTemplateVersion : "translate-cad-text/1.1", 1, 1, DateTimeOffset.UtcNow,
                [new(segment.SegmentId, reviewSource, reviewProposal, reviewProposal,
                    SegmentState.Proposed, null, highRisk ? "PROTECTED_TOKEN_CHANGED" : null,
                    highRisk ? "high" : "none", TranslationRouting.Terra, false)],
                    RequestedMode: TranslationRouting.Auto,
                    BaseModel: TranslationRouting.Terra,
                    RoutingVersion: TranslationRouting.PolicyVersion,
                    RoutingCalls: [],
                    RoutingSegments: [new TranslationSegmentTrace
                    {
                        SegmentId = segment.SegmentId, RequestedMode = TranslationRouting.Auto,
                        BaseModel = TranslationRouting.Terra, EffectiveModel = TranslationRouting.Terra,
                        Escalated = false, EscalationReasonCodes = [], ValidatorResult = highRisk ? "review" : "pass",
                        RiskSeverity = highRisk ? "high" : "none", RoutingVersion = TranslationRouting.PolicyVersion
                    }],
                    UsedInputTokens: 11,
                    UsedOutputTokens: 7,
                    UsedProviderRequests: 1,
                    ContextPolicyVersion: _contextual ? CadSemanticContextBuilder.PolicyVersionOneTwo : null,
                    ContextHash: contextHash);
                return Results.Success(Job);
            }
        }

        public Task<Result<JobDocument>> ResumeTranslationAsync(Guid jobId, long expectedJobVersion,
            ReviewAutomationScopeTransition? scopeTransition, CancellationToken cancellationToken) =>
            PrepareAsync(jobId, cancellationToken);

        public Task<Result<JobDocument>> RebindReviewAutomationScopeAsync(Guid jobId, long expectedJobVersion,
            ReviewAutomationScopeTransition scopeTransition, CancellationToken cancellationToken) =>
            Task.FromResult(Failure<JobDocument>("REVIEW_AUTOMATION_SCOPE_REBIND_UNAVAILABLE"));

        private int _prepareCalls;
        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public void ReleasePrepare() => _prepareGate?.TrySetResult(true);

        public async Task<Result<JobDocument>> LoadJobAsync(Guid jobId, CancellationToken cancellationToken)
        {
            JobDocument? snapshot;
            lock (_gate) snapshot = Job;
            if (_reviewApprovalRace)
            {
                if (Interlocked.Increment(ref _jobRaceLoads) == 2) _jobRace.TrySetResult(true);
                await _jobRace.Task.WaitAsync(cancellationToken);
            }
            return snapshot is not null && snapshot.JobId == jobId ? Results.Success(snapshot) : Failure<JobDocument>("JOB_NOT_FOUND");
        }

        public async Task<Result<TranslationReviewSnapshot>> LoadReviewAsync(Guid jobId, CancellationToken cancellationToken)
        {
            TranslationReviewSnapshot? snapshot;
            lock (_gate) snapshot = Review;
            if (_reviewApprovalRace)
            {
                if (Interlocked.Increment(ref _reviewRaceLoads) == 2) _reviewRace.TrySetResult(true);
                await _reviewRace.Task.WaitAsync(cancellationToken);
            }
            return snapshot is not null && snapshot.JobId == jobId ? Results.Success(snapshot) : Failure<TranslationReviewSnapshot>("REVIEW_NOT_FOUND");
        }

        public void EnableReviewApprovalRace()
        {
            _jobRaceLoads = 0;
            _reviewRaceLoads = 0;
            _jobRace = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _reviewRace = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _reviewApprovalRace = true;
        }

        public void PauseGenerationBeforeReviewValidation()
        {
            _generateGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _generateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task<bool> WaitForGenerationEntryAsync(CancellationToken cancellationToken) =>
            (_generateEntered ?? throw new InvalidOperationException("Generation pause is not configured."))
            .Task.WaitAsync(cancellationToken);

        public void ReleaseGenerationValidation() => _generateGate?.TrySetResult(true);

        public void ConfigureFailedGeometry(CadInvariantDiagnostics? diagnostics, bool reviewMatches = true)
        {
            lock (_gate)
            {
                var segment = Segment();
                var data = Job!.Data.Deserialize<DwgTranslationJobData>(Json)! with { Segments = [segment] };
                var node = JsonSerializer.SerializeToNode(data, Json)!.AsObject();
                node["failure"] = new JsonObject
                {
                    ["code"] = "GEOMETRY_INVARIANTS_CHANGED",
                    ["retryable"] = false,
                    ["invariantDiagnostics"] = diagnostics is null ? null : JsonSerializer.SerializeToNode(diagnostics, Json)
                };
                Job = Job with { State = JobState.Failed, Version = 7, UpdatedAtUtc = DateTimeOffset.UtcNow, Data = node };
                Review = new(reviewMatches ? Job.JobId : Guid.NewGuid(), 8, "en-US", "prompt", 1, 1, DateTimeOffset.UtcNow,
                    [new(segment.SegmentId, segment.SourceText, "PROPOSAL", "PROPOSAL", SegmentState.Approved, null, null)],
                    RequestedMode: TranslationRouting.Auto, BaseModel: TranslationRouting.Terra,
                    RoutingVersion: TranslationRouting.PolicyVersion);
            }
        }

        public void ConfigureFailedGeneration(string code, bool retryable, bool reviewMatches = true)
        {
            lock (_gate)
            {
                var segment = Segment();
                var data = Job!.Data.Deserialize<DwgTranslationJobData>(Json)! with { Segments = [segment] };
                var node = JsonSerializer.SerializeToNode(data, Json)!.AsObject();
                node["failure"] = new JsonObject { ["code"] = code, ["retryable"] = retryable, ["stage"] = "Writing" };
                Job = Job with { State = JobState.Failed, Version = 7, UpdatedAtUtc = DateTimeOffset.UtcNow, Data = node };
                Review = new(reviewMatches ? Job.JobId : Guid.NewGuid(), 9, "en-US", "prompt", 1, 1, DateTimeOffset.UtcNow,
                    [new(segment.SegmentId, segment.SourceText, "PROPOSAL", "PROPOSAL", SegmentState.Approved, null, null)],
                    RequestedMode: TranslationRouting.Auto, BaseModel: TranslationRouting.Terra,
                    RoutingVersion: TranslationRouting.PolicyVersion);
            }
        }

        public void ConfigureExternallyApproved(ReviewAutomationReceipt authority)
        {
            lock (_gate)
            {
                Review = Review! with
                {
                    Version = Review.Version + 1,
                    Rows = Review.Rows.Select(row => row with
                    {
                        State = SegmentState.Approved,
                        FinalText = row.ProposedText
                    }).ToArray(),
                    ReviewAutomationReceipt = authority
                };
                LastAutomationAuthority = authority;
                Job = Job! with
                {
                    State = JobState.Approved,
                    Version = Job.Version + 1,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }

        public void TamperApprovedReviewAfterPlan(string field)
        {
            lock (_gate)
            {
                Review = field switch
                {
                    "review" => Review! with
                    {
                        Rows = Review.Rows.Select(row => row with { FinalText = row.FinalText + "-TAMPER" }).ToArray()
                    },
                    "context" => Review! with
                    {
                        ContextPolicyVersion = CadSemanticContextBuilder.PolicyVersion,
                        ContextHash = "sha256:" + new string('8', 64)
                    },
                    "receipt" => Review! with
                    {
                        ReviewAutomationReceipt = new(ReviewAutomationPolicy.ContextualAgentCreateNew, Guid.NewGuid(),
                            "sha256:" + new string('9', 64), Review.ContextHash ?? "sha256:" + new string('8', 64),
                            "sha256:" + new string('a', 64), "sha256:" + new string('b', 64))
                    },
                    _ => throw new ArgumentOutOfRangeException(nameof(field))
                };
            }
        }

        public int GenerationReconciliationCalls { get; private set; }

        public Task<Result<JobDocument>> ReconcileFailedGenerationToApprovedAsync(Guid jobId, long expectedVersion,
            AgentGenerationReconciliationAuthority authority, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Job is null || Job.JobId != jobId || Job.State != JobState.Failed || Job.Version != expectedVersion ||
                    Review is null || Review.Version != authority.ReviewVersion || Review.Rows.Count != authority.DecisionCount)
                    return Task.FromResult(Failure<JobDocument>("JOB_STATE_CONFLICT"));
                GenerationReconciliationCalls++;
                var data = Job.Data.DeepClone().AsObject();
                data.Remove("failure"); data.Remove("agentFailure");
                Job = Job with { State = JobState.Approved, Version = Job.Version + 1, UpdatedAtUtc = DateTimeOffset.UtcNow, Data = data };
                return Task.FromResult(Results.Success(Job));
            }
        }

        public Task<Result<JobDocument>> ReconcileFailedGeometryForReviewAsync(Guid jobId, long expectedVersion, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Job is null || Job.JobId != jobId || Job.State != JobState.Failed || Job.Version != expectedVersion)
                    return Task.FromResult(Failure<JobDocument>("JOB_STATE_CONFLICT"));
                GeometryReconciliationCalls++;
                var data = Job.Data.DeepClone().AsObject();
                data.Remove("failure"); data.Remove("agentFailure");
                Job = Job with { State = JobState.ReviewRequired, Version = Job.Version + 1, UpdatedAtUtc = DateTimeOffset.UtcNow, Data = data };
                return Task.FromResult(Results.Success(Job));
            }
        }

        public Task<Result<JobDocument>> ApproveAsync(Guid jobId, IReadOnlyList<ReviewDecisionInput> decisions,
            long expectedReviewVersion, ReviewAutomationReceipt? automationAuthority, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Review is null || Review.Version != expectedReviewVersion || Job?.State != JobState.ReviewRequired)
                    return Task.FromResult(Failure<JobDocument>("REVIEW_VERSION_CONFLICT"));
                Review = Review! with
                {
                    Version = Review.Version + 1,
                    Rows = Review.Rows.Select(row => row with { State = SegmentState.Approved, FinalText = decisions.Single().FinalText! }).ToArray(),
                    ReviewAutomationReceipt = automationAuthority
                };
                LastAutomationAuthority = automationAuthority;
                Job = Job! with { State = JobState.Approved, Version = Job.Version + 1, UpdatedAtUtc = DateTimeOffset.UtcNow };
                return Task.FromResult(Results.Success(Job));
            }
        }

        public Task<Result<JobDocument>> ReviseApprovedReviewAsync(Guid jobId, long expectedJobVersion,
            IReadOnlyList<ReviewDecisionInput> decisions, long expectedReviewVersion,
            ReviewAutomationReceipt automationAuthority, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Job is null || Job.JobId != jobId || Job.State != JobState.Approved ||
                    Job.Version != expectedJobVersion || Review is null || Review.Version != expectedReviewVersion ||
                    decisions.Count != Review.Rows.Count)
                    return Task.FromResult(Failure<JobDocument>("APPROVED_REVIEW_CORRECTION_BINDING_INVALID"));
                var byId = decisions.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
                Review = Review with
                {
                    Version = Review.Version + 1,
                    Rows = Review.Rows.Select(row => row with
                    {
                        State = SegmentState.Approved,
                        FinalText = byId[row.SegmentId].FinalText!
                    }).ToArray(),
                    ReviewAutomationReceipt = automationAuthority
                };
                Job = Job with { Version = Job.Version + 1, UpdatedAtUtc = DateTimeOffset.UtcNow };
                LastAutomationAuthority = automationAuthority;
                return Task.FromResult(Results.Success(Job));
            }
        }

        public async Task<Result<JobDocument>> GenerateAsync(Guid jobId, TranslationReviewFingerprint expectedReview,
            CancellationToken cancellationToken)
        {
            if (_generateGate is not null)
            {
                _generateEntered!.TrySetResult(true);
                await _generateGate.Task.WaitAsync(cancellationToken);
            }
            lock (_gate)
            {
                if (Review is null || TranslationReviewFingerprint.Create(Review) != expectedReview)
                    return Failure<JobDocument>("GENERATION_REVIEW_BINDING_MISMATCH");
                GenerateCalls++;
                var data = Job!.Data.Deserialize<DwgTranslationJobData>(Json)!;
                File.WriteAllText(data.Specification.OutputPath, "SYNTHETIC-VALIDATED-OUTPUT");
                var hash = "sha256:" + new string('d', 64);
                var report = new CadExerciseValidationReport("VisualStrictV2", true, true, 1, 0,
                    data.Specification.SourceHash, hash, []);
                data = data with { OutputHash = hash, ValidationReport = report };
                Job = Job with
                {
                    State = JobState.Completed,
                    Version = Job.Version + 3,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Data = JsonSerializer.SerializeToNode(data, Json)!.AsObject()
                };
                return Results.Success(Job);
            }
        }

        public Task<Result<JobDocument>> CancelAsync(Guid jobId, long expectedVersion, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Job = Job! with { State = JobState.Cancelled, Version = Job.Version + 1 };
                return Task.FromResult(Results.Success(Job));
            }
        }

        public Task<Result<JobDocument>> FailAsync(Guid jobId, string code, bool retryable, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var data = Job!.Data.DeepClone().AsObject();
                data["agentFailure"] = new JsonObject
                {
                    ["code"] = code,
                    ["retryable"] = retryable
                };
                Job = Job with
                {
                    State = JobState.Failed,
                    Version = Job.Version + 1,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Data = data
                };
                return Task.FromResult(Results.Success(Job));
            }
        }

        private static CadTextSegment Segment(bool contextual = false, string sourceText = "ORIGINAL_SENSITIVE",
            string? contextualFault = null)
        {
            var layer = contextualFault switch
            {
                "unknown" or "invalid-neighbor" => "0",
                "conflict" => "BMS-ARCH",
                _ when contextual => "BMS",
                _ => "0"
            };
            var segment = new CadTextSegment
            {
                SegmentId = "seg_sha256_" + new string('a', 64),
                Entity = new CadEntityReference
                {
                    Type = "TEXT",
                    Handle = "1A",
                    Space = "ModelSpace",
                    Layout = null,
                    BlockPath = [],
                    Layer = layer,
                    SubIndex = 0
                },
                SourceText = sourceText,
                SourceTextHash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceText))).ToLowerInvariant(),
                LineBreakStyle = "None",
                ProtectedTokens = [],
                FieldClassification = "PlainText",
                State = "Extracted"
            };
            if (!contextual) return segment;
            var built = CadSemanticContextBuilder.Build([new CadSemanticContextInput(
                segment.SegmentId,
                segment.Entity.Type,
                segment.Entity.Handle,
                segment.Entity.Space,
                segment.Entity.Layout,
                segment.Entity.BlockPath,
                segment.Entity.Layer,
                segment.SourceText,
                segment.SourceTextHash,
                0,
                0,
                1,
                "EntityPosition",
                 contextualFault is null ? "source.dwg" : null)],
                 CadSemanticContextBuilder.PolicyVersionOneTwo);
            if (!built.IsSuccess) throw new InvalidOperationException(built.Error?.Code);
            var context = built.Value![segment.SegmentId];
            if (contextualFault == "invalid-neighbor")
                context = context with
                {
                    Neighbors = [new CadSemanticNeighbor
                    {
                        SegmentId = "seg_sha256_" + new string('f', 64),
                        SourceTextHash = "sha256:" + new string('e', 64),
                        EntityType = "TEXT",
                        Relation = "Right",
                        DistanceBand = 1,
                        SameLayer = false
                    }]
                };
            return segment with
            {
                SemanticContext = context
            };
        }

        private static Result<T> Failure<T>(string code) => Results.Failure<T>(new ContractError(
            code, ErrorCategory.Storage, "Synthetic failure.", false));
    }
}
