using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using DwgTranslator.Agent;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Agent.Tests;

[TestClass]
public sealed class AgentBatchServiceTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private string _root = null!, _input = null!, _source = null!, _output = null!, _workspace = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwg-batch-tests", Guid.NewGuid().ToString("N"));
        _input = Path.Combine(_root, "input"); _source = Path.Combine(_input, "batch");
        _output = Path.Combine(_input, "batch-eng"); _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_source); Directory.CreateDirectory(_workspace);
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void PlanRejectsEmptyDuplicateContentAndOutputRace()
    {
        var service = Service(new FakeProcessor());
        Assert.AreEqual("BATCH_EMPTY", service.Plan(Request()).Error!.Code);
        File.WriteAllText(Path.Combine(_source, "a.dwg"), "same");
        File.WriteAllText(Path.Combine(_source, "b.dwg"), "same");
        Assert.AreEqual("BATCH_DUPLICATE_HASH", service.Plan(Request()).Error!.Code);
        File.Delete(Path.Combine(_source, "b.dwg")); Directory.CreateDirectory(_output);
        Assert.AreEqual("OUTPUT_ALREADY_EXISTS", service.Plan(Request()).Error!.Code);
    }

    [TestMethod]
    public void HistoricalFileProgressWithoutProviderRequestsDefaultsToZeroAndRoundTrips()
    {
        const string historical = """
            {"relativePath":"a.dwg","sourceHash":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","outputPath":"C:\\out\\a.dwg","state":3,"inputTokens":10,"outputTokens":5}
            """;
        var progress = JsonSerializer.Deserialize<AgentBatchFileProgress>(historical, WebJson)!;

        Assert.AreEqual(0, progress.ProviderRequests);
        var roundTrip = JsonSerializer.Deserialize<AgentBatchFileProgress>(JsonSerializer.Serialize(progress, WebJson), WebJson)!;
        Assert.AreEqual(progress.RelativePath, roundTrip.RelativePath);
        Assert.AreEqual(0, roundTrip.ProviderRequests);
        Assert.AreEqual(2, new[] { progress, progress with { RelativePath = "b.dwg", ProviderRequests = 2 } }
            .Sum(file => file.ProviderRequests));
    }

    [TestMethod]
    public void PlanAcceptsLegacyAndPrefersContextualPolicyWhileRejectingUnknown()
    {
        File.WriteAllText(Path.Combine(_source, "a.dwg"), "a");
        var service = Service(new FakeProcessor());

        var contextual = service.Plan(Request());
        var legacy = service.Plan(Request() with { Policy = AgentBatchPolicy.LegacyPolicyVersion });
        var unknown = service.Plan(Request() with { Policy = "unversioned" });

        Assert.IsTrue(contextual.Success, contextual.Error?.Code);
        Assert.AreEqual(AgentBatchPolicy.ContextualPolicyVersion,
            contextual.Data!["policy"]!.GetValue<string>());
        Assert.AreEqual(CadSemanticContextBuilder.PolicyVersionOneTwo, contextual.Data["contextPolicyVersion"]!.GetValue<string>());
        Assert.AreEqual(TranslationReviewWorkflow.ContextualPromptTemplateVersion, contextual.Data["promptTemplateVersion"]!.GetValue<string>());
        Assert.AreEqual(4, contextual.Data["maximumNeighborExcerpts"]!.GetValue<int>());
        Assert.AreEqual(160, contextual.Data["maximumNeighborExcerptScalars"]!.GetValue<int>());
        Assert.AreEqual(512, contextual.Data["maximumNeighborExcerptScalarsPerSegment"]!.GetValue<int>());
        Assert.IsTrue(legacy.Success, legacy.Error?.Code);
        Assert.AreEqual(AgentBatchPolicy.LegacyPolicyVersion, legacy.Data!["policy"]!.GetValue<string>());
        Assert.AreEqual("BATCH_POLICY_INVALID", unknown.Error!.Code);
    }

    [TestMethod]
    public async Task ContextualPlanTamperFailsBeforeStartOrGeneration()
    {
        File.WriteAllText(Path.Combine(_source, "a.dwg"), "a");
        var processor = new FakeProcessor();
        var service = Service(processor);
        var first = service.Plan(Request()).Data!.AsObject();
        var firstApproval = first["approval"]!.AsObject();
        TamperPlan(first["planId"]!.GetValue<string>(), plan => plan["maximumNeighborExcerpts"] = 3);

        var rejectedStart = service.Start(new(first["planId"]!.GetValue<string>(), first["manifestHash"]!.GetValue<string>(),
            firstApproval["approvalId"]!.GetValue<string>(), firstApproval["consent"]!.GetValue<string>(), "tamper-start"));
        Assert.AreEqual("BATCH_CONTEXT_BINDING_INVALID", rejectedStart.Error!.Code);
        Assert.AreEqual(0, processor.PrepareCalls);

        var second = service.Plan(Request()).Data!.AsObject();
        var secondApproval = second["approval"]!.AsObject();
        var started = service.Start(new(second["planId"]!.GetValue<string>(), second["manifestHash"]!.GetValue<string>(),
            secondApproval["approvalId"]!.GetValue<string>(), secondApproval["consent"]!.GetValue<string>(), "valid-start"));
        Assert.IsTrue(started.Success, started.Error?.Code);
        var batchId = Guid.Parse(second["batchId"]!.GetValue<string>());
        await WaitAsync(() => service.Status(batchId).Data!["state"]!.GetValue<string>() == "ReviewRequired");
        var status = service.Status(batchId).Data!.AsObject();
        TamperPlan(second["planId"]!.GetValue<string>(), plan => plan["validationPolicy"] = "tampered");

        var rejectedGeneration = service.ApproveAndGenerate(new(batchId, status["batchVersion"]!.GetValue<long>(),
            secondApproval["reviewAndGenerationApprovalId"]!.GetValue<string>(),
            secondApproval["reviewAndGenerationApproval"]!.GetValue<string>(), "tamper-generation"));
        Assert.AreEqual("BATCH_CONTEXT_BINDING_INVALID", rejectedGeneration.Error!.Code);
        Assert.AreEqual(0, processor.GenerateCalls);
    }

    [TestMethod]
    public async Task OneAndManyFilesRunSequentiallyContinueFailureAndReplaySafely()
    {
        File.WriteAllText(Path.Combine(_source, "a.dwg"), "a");
        File.WriteAllText(Path.Combine(_source, "b.dwg"), "b");
        File.WriteAllText(Path.Combine(_source, "c.dwg"), "c");
        var processor = new FakeProcessor(fail: "b.dwg");
        var service = Service(processor);
        var planEnvelope = service.Plan(Request());
        Assert.IsTrue(planEnvelope.Success, planEnvelope.Error?.Code);
        var data = planEnvelope.Data!.AsObject();
        Assert.IsTrue(data["files"]!.AsArray().All(node => node!["outputPath"]!.GetValue<string>().EndsWith("-ENG.dwg", StringComparison.Ordinal)));
        var approval = data["approval"]!.AsObject();
        var start = new AgentBatchStartRequest(data["planId"]!.GetValue<string>(), data["manifestHash"]!.GetValue<string>(),
            approval["approvalId"]!.GetValue<string>(), approval["consent"]!.GetValue<string>(), "start-1");
        var started = service.Start(start);
        Assert.IsTrue(started.Success, started.Error?.Code);
        var replay = service.Start(start);
        Assert.IsTrue(replay.Success);
        Assert.IsTrue(replay.Data!["idempotentReplay"]!.GetValue<bool>());
        var batchId = Guid.Parse(data["batchId"]!.GetValue<string>());
        await WaitAsync(() => service.Status(batchId).Data!["state"]!.GetValue<string>() == "ReviewRequired");
        var status = service.Status(batchId).Data!.AsObject();
        Assert.AreEqual(1, status["totals"]!["failed"]!.GetValue<int>());
        Assert.AreEqual(2, status["totals"]!["reviewing"]!.GetValue<int>());
        Assert.AreEqual(30, status["usage"]!["inputTokens"]!.GetValue<long>());
        Assert.AreEqual(15, status["usage"]!["outputTokens"]!.GetValue<long>());
        Assert.AreEqual(6, status["usage"]!["providerRequests"]!.GetValue<long>());
        Assert.AreEqual(3, processor.PrepareCalls);

        var approved = service.ApproveAndGenerate(new(batchId, status["batchVersion"]!.GetValue<long>(),
            approval["reviewAndGenerationApprovalId"]!.GetValue<string>(), approval["reviewAndGenerationApproval"]!.GetValue<string>(), "generate-1"));
        Assert.IsTrue(approved.Success, approved.Error?.Code);
        await WaitAsync(() => service.Status(batchId).Data!["state"]!.GetValue<string>().StartsWith("Completed", StringComparison.Ordinal));
        var report = service.Report(batchId).Data!.AsObject();
        Assert.AreEqual(2, report["totals"]!["completed"]!.GetValue<int>());
        Assert.AreEqual(1, report["totals"]!["failed"]!.GetValue<int>());
        Assert.AreEqual(2, processor.GenerateCalls);
        var completedStatus = service.Status(batchId).Data!.AsObject();
        Assert.AreEqual(30, completedStatus["usage"]!["inputTokens"]!.GetValue<long>());
        Assert.AreEqual(15, completedStatus["usage"]!["outputTokens"]!.GetValue<long>());
        Assert.AreEqual(6, completedStatus["usage"]!["providerRequests"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task AllPreparationFailuresRequireRecoveryWhileMixedResultsRemainReviewable()
    {
        File.WriteAllText(Path.Combine(_source, "a.dwg"), "a");
        File.WriteAllText(Path.Combine(_source, "b.dwg"), "b");
        var processor = new FakeProcessor(fail: "a.dwg") { SecondStall = "b.dwg" };
        var service = Service(processor);
        var plan = service.Plan(Request()).Data!.AsObject();
        var approval = plan["approval"]!.AsObject();

        var started = service.Start(new(
            plan["planId"]!.GetValue<string>(),
            plan["manifestHash"]!.GetValue<string>(),
            approval["approvalId"]!.GetValue<string>(),
            approval["consent"]!.GetValue<string>(),
            "all-failed-start"));
        Assert.IsTrue(started.Success, started.Error?.Code);
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        await WaitAsync(() => service.Status(batchId).Data!["state"]!.GetValue<string>() == "RecoveryRequired");

        var status = service.Status(batchId).Data!.AsObject();
        Assert.AreEqual("BATCH_RECOVERY_REQUIRED", status["errorCode"]!.GetValue<string>());
        Assert.AreEqual(2, status["totals"]!["failed"]!.GetValue<int>());
        Assert.AreEqual(0, status["totals"]!["reviewing"]!.GetValue<int>());
    }

    [TestMethod]
    public void PlanRejectsAdsAndConfinesExactOutputRoot()
    {
        var path = Path.Combine(_source, "a.dwg"); File.WriteAllText(path, "a");
        var outside = Request() with { OutputDirectory = Path.Combine(_input, "other") };
        Assert.AreEqual("BATCH_OUTPUT_OUTSIDE_ALLOWLIST", Service(new FakeProcessor()).Plan(outside).Error!.Code);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path + ":Zone.Identifier", "downloaded");
            Assert.AreEqual("BATCH_ADS_REJECTED", Service(new FakeProcessor()).Plan(Request()).Error!.Code);
        }
    }

    [TestMethod]
    public void CapabilitiesExposeFailClosedDefaultAndExplicitProductionGate()
    {
        var baseline = new AgentBetaConfiguration("dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true,
            _workspace, Path.Combine(_root, "logs"), AllowedDwgRoot: _input, OpenAiEnabled: true,
            OutputDwgRoot: _input, BatchOutputRoots: [_output]);
        var safe = new AgentQueryService(baseline).Capabilities();
        Assert.IsFalse(safe.Data!["batch"]!["executionEnabled"]!.GetValue<bool>());
        Assert.AreEqual("fail-closed", safe.Data["batch"]!["processor"]!.GetValue<string>());
        var enabled = new AgentQueryService(baseline with { BatchExecutionEnabled = true }).Capabilities();
        Assert.IsTrue(enabled.Data!["batch"]!["executionEnabled"]!.GetValue<bool>());
        Assert.AreEqual("production-workflow-adapter/1.0", enabled.Data["batch"]!["processor"]!.GetValue<string>());
        var commands = enabled.Data["commands"]!.AsArray().Select(command => command!.GetValue<string>()).ToArray();
        CollectionAssert.Contains(commands, "batch recovery plan");
        CollectionAssert.Contains(commands, "batch recovery start");
        CollectionAssert.Contains(commands, "batch reconcile review");
        CollectionAssert.Contains(commands, "invariant diff plan");
        CollectionAssert.Contains(commands, "invariant diff run");
    }

    [TestMethod]
    public async Task RecoveryPlanAcceptsExplicitSafeActionsAndRejectsQueuedFiles()
    {
        var names = Enumerable.Range(0, 9).Select(index => $"{index:000}.dwg").ToArray();
        foreach (var name in names) File.WriteAllText(Path.Combine(_source, name), name);
        var processor = new FakeProcessor();
        var service = Service(processor);
        var planned = service.Plan(Request());
        var plan = planned.Data!.AsObject();
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        var files = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!;
        Directory.CreateDirectory(_output);
        var progress = files.Select((file, index) => index switch
        {
            < 5 => new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Reviewing,
                Guid.NewGuid(), ErrorCode: index == 0 ? "OPENAI_TIMEOUT" : null, Retryable: index == 0,
                InputTokens: 7, OutputTokens: 3, ProviderRequests: 1),
            5 => new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Failed, Guid.NewGuid(), ErrorCode: "OPENAI_TIMEOUT", Retryable: true,
                InputTokens: 7, OutputTokens: 3, ProviderRequests: 1),
            _ => new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Failed, ErrorCode: index == 8 ? "JOB_STATE_CONFLICT" : "WORKFLOW_BACKGROUND_STALLED", Retryable: true)
        }).ToArray();
        SaveBatch(new AgentBatchDocument(batchId, 20, "Preparing", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, progress, StartIdempotencyKey: "original-start"));
        for (var index = 0; index < 5; index++)
            SaveReviewRequiredJob(progress[index].JobId!.Value, files[index]);
        SaveFailedTranslationJob(progress[5].JobId!.Value, files[5], retryable: true, stage: "Translation", agentFailure: false);

        var recovery = service.RecoveryPlan(new(batchId, names));
        Assert.IsTrue(recovery.Success, recovery.Error?.Code);
        Assert.AreEqual(5, recovery.Data!["entries"]!.AsArray().Count(entry => entry!["action"]!.GetValue<string>() == "ContinueFromReview"));
        Assert.AreEqual(1, recovery.Data["entries"]!.AsArray().Count(entry => entry!["action"]!.GetValue<string>() == "ResumeTranslationMissingOnly"));
        Assert.AreEqual(3, recovery.Data["entries"]!.AsArray().Count(entry => entry!["action"]!.GetValue<string>() == "RetryCadFresh"));
        var recoveryApproval = recovery.Data["approval"]!.AsObject();
        var started = service.StartRecovery(new(recovery.Data["recoveryPlanId"]!.GetValue<string>(),
            recovery.Data["recoveryManifestHash"]!.GetValue<string>(), recoveryApproval["approvalId"]!.GetValue<string>(),
            recoveryApproval["consent"]!.GetValue<string>(), "recovery-start-1"));
        Assert.IsTrue(started.Success, started.Error?.Code);
        Assert.AreEqual("Suspended", service.Status(batchId).Data!["state"]!.GetValue<string>());
        Assert.AreEqual(0, service.Status(batchId).Data!["totals"]!["queued"]!.GetValue<int>());
        var recoveryBatchId = Guid.Parse(started.Data!["batchId"]!.GetValue<string>());
        await WaitAsync(() => service.Status(recoveryBatchId).Data!["state"]!.GetValue<string>() == "ReviewRequired");
        var recoveryStatus = service.Status(recoveryBatchId).Data!.AsObject();
        Assert.AreEqual(90, recoveryStatus["usage"]!["inputTokens"]!.GetValue<long>());
        Assert.AreEqual(45, recoveryStatus["usage"]!["outputTokens"]!.GetValue<long>());
        Assert.AreEqual(18, recoveryStatus["usage"]!["providerRequests"]!.GetValue<long>());
        var reconciled = service.ReviewSummary(recoveryBatchId).Data!["files"]!.AsArray()[0]!.AsObject();
        Assert.AreEqual("Reviewing", reconciled["state"]!.GetValue<string>());
        Assert.IsNull(reconciled["errorCode"]);
        Assert.IsFalse(reconciled["retryable"]!.GetValue<bool>());
        var generated = service.ApproveAndGenerate(new(recoveryBatchId,
            recoveryStatus["batchVersion"]!.GetValue<long>(),
            recoveryApproval["reviewAndGenerationApprovalId"]!.GetValue<string>(),
            recoveryApproval["reviewAndGenerationApproval"]!.GetValue<string>(), "recovery-generate-1"));
        Assert.IsTrue(generated.Success, generated.Error?.Code);
        await WaitAsync(() => service.Status(recoveryBatchId).Data!["state"]!.GetValue<string>().StartsWith("Completed", StringComparison.Ordinal));
        Assert.AreEqual(9, processor.GenerateCalls);
        var completedRecovery = service.Status(recoveryBatchId).Data!.AsObject();
        Assert.AreEqual(90, completedRecovery["usage"]!["inputTokens"]!.GetValue<long>());
        Assert.AreEqual(45, completedRecovery["usage"]!["outputTokens"]!.GetValue<long>());
        Assert.AreEqual(18, completedRecovery["usage"]!["providerRequests"]!.GetValue<long>());
        var replay = service.ApproveAndGenerate(new(recoveryBatchId,
            recoveryStatus["batchVersion"]!.GetValue<long>(), recoveryApproval["reviewAndGenerationApprovalId"]!.GetValue<string>(),
            recoveryApproval["reviewAndGenerationApproval"]!.GetValue<string>(), "recovery-generate-1"));
        Assert.IsTrue(replay.Success, replay.Error?.Code);
        Assert.AreEqual(9, processor.GenerateCalls);
        var reloaded = Service(processor).Status(recoveryBatchId).Data!.AsObject();
        Assert.AreEqual(90, reloaded["usage"]!["inputTokens"]!.GetValue<long>());
        Assert.AreEqual(45, reloaded["usage"]!["outputTokens"]!.GetValue<long>());
        Assert.AreEqual(18, reloaded["usage"]!["providerRequests"]!.GetValue<long>());
        Assert.AreEqual(9, processor.GenerateCalls);

        progress[8] = progress[8] with { State = AgentBatchFileState.Queued, ErrorCode = null };
        SaveBatch(new AgentBatchDocument(batchId, 21, "Preparing", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, progress, StartIdempotencyKey: "original-start"));
        Assert.AreEqual("BATCH_RECOVERY_ACTION_UNSAFE", service.RecoveryPlan(new(batchId, names)).Error!.Code);
    }

    [TestMethod]
    public async Task RecoveryDoesNotExposeReviewRequiredWhenAChildCannotBeReconciled()
    {
        File.WriteAllText(Path.Combine(_source, "a.dwg"), "a");
        var service = Service(new FakeProcessor(reconcileFailure: "BATCH_CHILD_REVIEW_NOT_READY"));
        var planned = service.Plan(Request()).Data!.AsObject();
        Directory.CreateDirectory(_output);
        var manifest = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(planned["files"]!.ToJsonString(), WebJson)!.Single();
        var originalId = Guid.Parse(planned["batchId"]!.GetValue<string>());
        var jobId = Guid.NewGuid();
        SaveReviewRequiredJob(jobId, manifest);
        SaveBatch(new AgentBatchDocument(originalId, 7, "CompletedWithFailures", planned["manifestHash"]!.GetValue<string>(),
            _source, _output, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(manifest.RelativePath, manifest.Sha256, manifest.OutputPath, AgentBatchFileState.Reviewing,
                jobId, JobVersion: 7)], StartIdempotencyKey: "original-start"));

        var recovery = service.RecoveryPlan(new(originalId, [manifest.RelativePath]));
        var started = service.StartRecovery(RecoveryStartRequest(recovery, "reconcile-fails"));
        Assert.IsTrue(started.Success, started.Error?.Code);
        var recoveryBatchId = Guid.Parse(started.Data!["batchId"]!.GetValue<string>());
        await WaitAsync(() => service.Status(recoveryBatchId).Data!["state"]!.GetValue<string>() == "RecoveryRequired");
        var status = service.Status(recoveryBatchId).Data!.AsObject();
        Assert.AreEqual("BATCH_RECOVERY_RECONCILIATION_FAILED", status["errorCode"]!.GetValue<string>());
        Assert.AreEqual(1, status["totals"]!["failed"]!.GetValue<int>());
    }

    [TestMethod]
    public void RecoveryPlanUsesDurableChildFailureInsteadOfStaleBatchOpenAiTimeout()
    {
        File.WriteAllText(Path.Combine(_source, "201.dwg"), "201");
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask);
        var plan = service.Plan(Request()).Data!.AsObject();
        var manifest = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!.Single();
        var jobId = Guid.NewGuid();
        Directory.CreateDirectory(_output);
        SaveFailedGeometryJob(jobId, manifest, diagnostics: null);
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        SaveBatch(new AgentBatchDocument(batchId, 12, "CompletedWithFailures", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(manifest.RelativePath, manifest.Sha256, manifest.OutputPath, AgentBatchFileState.Failed,
                jobId, ErrorCode: "OPENAI_TIMEOUT", Retryable: true)], StartIdempotencyKey: "original-start"));

        var recovery = service.RecoveryPlan(new(batchId, [manifest.RelativePath]));

        Assert.IsFalse(recovery.Success);
        Assert.AreEqual("BATCH_RECOVERY_GEOMETRY_EVIDENCE_INCOMPLETE", recovery.Error!.Code);
    }

    [TestMethod]
    public void TimeoutRecoveryRequiresRetryableDurableTranslationCheckpoint()
    {
        File.WriteAllText(Path.Combine(_source, "timeout.dwg"), "timeout");
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask);
        var plan = service.Plan(Request()).Data!.AsObject();
        var manifest = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!.Single();
        var jobId = Guid.NewGuid();
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        Directory.CreateDirectory(_output);
        SaveBatch(new AgentBatchDocument(
            batchId, 9, "RecoveryRequired", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(
                manifest.RelativePath, manifest.Sha256, manifest.OutputPath,
                AgentBatchFileState.Failed, jobId, ErrorCode: "OPENAI_TIMEOUT", Retryable: true)],
            StartIdempotencyKey: "timeout-parent"));

        var missingJournal = service.RecoveryPlan(new(batchId, [manifest.RelativePath]));
        Assert.IsFalse(missingJournal.Success);
        Assert.AreEqual("BATCH_RECOVERY_CHILD_EVIDENCE_MISSING", missingJournal.Error!.Code);

        foreach (var scenario in new[]
                 {
                     (Retryable: false, Stage: "Translation", AgentFailure: false),
                     (Retryable: true, Stage: "Writing", AgentFailure: false),
                     (Retryable: true, Stage: "Translation", AgentFailure: true)
                 })
        {
            SaveFailedTranslationJob(jobId, manifest, scenario.Retryable, scenario.Stage, scenario.AgentFailure);
            var rejected = service.RecoveryPlan(new(batchId, [manifest.RelativePath]));
            Assert.IsFalse(rejected.Success);
            Assert.AreEqual("BATCH_RECOVERY_CHILD_STATE_DIVERGED", rejected.Error!.Code);
        }

        SaveFailedTranslationJob(jobId, manifest, retryable: true, stage: "Translation", agentFailure: false);
        var accepted = service.RecoveryPlan(new(batchId, [manifest.RelativePath]));
        Assert.IsTrue(accepted.Success, accepted.Error?.Code);
        Assert.AreEqual(
            "ResumeTranslationMissingOnly",
            accepted.Data!["entries"]!.AsArray().Single()!["action"]!.GetValue<string>());
    }

    [TestMethod]
    public void StartRecoveryRejectsExpiredApproval()
    {
        var now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(_source, "expired.dwg"), "expired");
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask, () => now);
        var plan = service.Plan(Request()).Data!.AsObject();
        var manifest = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(
            plan["files"]!.ToJsonString(), WebJson)!.Single();
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        Directory.CreateDirectory(_output);
        SaveBatch(new AgentBatchDocument(
            batchId, 3, "RecoveryRequired", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            now, now, now,
            [new AgentBatchFileProgress(
                manifest.RelativePath, manifest.Sha256, manifest.OutputPath,
                AgentBatchFileState.Failed, ErrorCode: "CAD_BUSY", Retryable: true)],
            StartIdempotencyKey: "expired-parent"));
        var recovery = service.RecoveryPlan(new(batchId, [manifest.RelativePath]));
        Assert.IsTrue(recovery.Success, recovery.Error?.Code);

        now = now.AddMinutes(31);
        var started = service.StartRecovery(RecoveryStartRequest(recovery, "expired-start"));

        Assert.IsFalse(started.Success);
        Assert.AreEqual("APPROVAL_EXPIRED", started.Error!.Code);
        Assert.AreEqual("RecoveryRequired", service.Status(batchId).Data!["state"]!.GetValue<string>());
    }

    [TestMethod]
    public void RecoveryPlanUsesTolerableDurableGeometryAsContinueFromReview()
    {
        File.WriteAllText(Path.Combine(_source, "201.dwg"), "201");
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask);
        var plan = service.Plan(Request()).Data!.AsObject();
        var manifest = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!.Single();
        var jobId = Guid.NewGuid();
        Directory.CreateDirectory(_output);
        SaveFailedGeometryJob(jobId, manifest, TolerableDiagnostics());
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        SaveBatch(new AgentBatchDocument(batchId, 12, "CompletedWithFailures", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(manifest.RelativePath, manifest.Sha256, manifest.OutputPath, AgentBatchFileState.Failed,
                jobId, ErrorCode: "OPENAI_TIMEOUT", Retryable: true)], StartIdempotencyKey: "original-start"));

        var recovery = service.RecoveryPlan(new(batchId, [manifest.RelativePath]));

        Assert.IsTrue(recovery.Success, recovery.Error?.Code);
        Assert.AreEqual("ContinueFromReview", recovery.Data!["entries"]!.AsArray().Single()!["action"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task IterativeRecoveryAcceptsFailedSubsetAndPreservesHashVerifiedCompletedOutputs()
    {
        var names = Enumerable.Range(0, 6).Select(index => $"{index:000}.dwg").ToArray();
        foreach (var name in names) File.WriteAllText(Path.Combine(_source, name), name);
        var processor = new FakeProcessor();
        var service = Service(processor);
        var planned = service.Plan(Request());
        var plan = planned.Data!.AsObject();
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        var files = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!;
        Directory.CreateDirectory(_output);
        File.WriteAllText(files[0].OutputPath, "completed-output");
        var completedHash = HashFile(files[0].OutputPath);
        var progress = files.Select((file, index) => index switch
        {
            0 => new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Completed,
                OutputHash: completedHash),
            1 => new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Failed,
                Guid.NewGuid(), ErrorCode: "OPENAI_TIMEOUT", Retryable: true),
            2 => new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Failed,
                ErrorCode: "WORKFLOW_UNEXPECTED_FAILURE"),
            3 or 4 => new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Failed,
                ErrorCode: "CAD_BUSY"),
            _ => new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath, AgentBatchFileState.Failed,
                ErrorCode: "CAD_PROCESS_INITIALIZATION_FAILED")
        }).ToArray();
        SaveFailedTranslationJob(progress[1].JobId!.Value, files[1],
            retryable: true, stage: "Translation", agentFailure: false);
        SaveBatch(new AgentBatchDocument(batchId, 12, "CompletedWithFailures", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, progress, StartIdempotencyKey: "original-start"));

        var subset = names[1..];
        var recovery = service.RecoveryPlan(new(batchId, subset));

        Assert.IsTrue(recovery.Success, recovery.Error?.Code);
        Assert.AreEqual(1, recovery.Data!["entries"]!.AsArray().Count(entry => entry!["action"]!.GetValue<string>() == "ResumeTranslationMissingOnly"));
        Assert.AreEqual(4, recovery.Data["entries"]!.AsArray().Count(entry => entry!["action"]!.GetValue<string>() == "RetryCadFresh"));
        var approval = recovery.Data["approval"]!.AsObject();
        var request = new AgentBatchRecoveryStartRequest(recovery.Data["recoveryPlanId"]!.GetValue<string>(),
            recovery.Data["recoveryManifestHash"]!.GetValue<string>(), approval["approvalId"]!.GetValue<string>(),
            approval["consent"]!.GetValue<string>(), "iterative-recovery-start");

        File.WriteAllText(files[0].OutputPath, "tampered-output");
        Assert.AreEqual("BATCH_RECOVERY_OUTPUT_UNSAFE", service.StartRecovery(request).Error!.Code);
        File.WriteAllText(files[0].OutputPath, "completed-output");
        Assert.AreEqual(completedHash, HashFile(files[0].OutputPath));

        var started = service.StartRecovery(request);
        Assert.IsTrue(started.Success, started.Error?.Code);
        var recoveryBatchId = Guid.Parse(started.Data!["batchId"]!.GetValue<string>());
        await WaitAsync(() => service.Status(recoveryBatchId).Data!["state"]!.GetValue<string>() == "ReviewRequired");
        Assert.AreEqual(4, processor.PrepareCalls);
        Assert.AreEqual(completedHash, HashFile(files[0].OutputPath));
    }

    [TestMethod]
    public void RecoveryAcceptsHashVerifiedDescendantOutputWhenSelectedOutputIsAbsent()
    {
        var fixture = CreateDescendantOutputFixture();
        var recovery = fixture.Service.RecoveryPlan(new(fixture.ParentBatchId, [fixture.Selected.RelativePath]));
        var started = fixture.Service.StartRecovery(RecoveryStartRequest(recovery, "descendant-output"));

        Assert.IsTrue(started.Success, started.Error?.Code);
        Assert.IsFalse(File.Exists(fixture.Selected.OutputPath));
        Assert.AreEqual(fixture.CompletedHash, HashFile(fixture.Completed.OutputPath));
    }

    [TestMethod]
    public void RecoveryAllowsCompletedJobOutputOutsideTheSelectedSubset()
    {
        foreach (var name in new[] { "200.dwg", "201.dwg", "202.dwg" }) File.WriteAllText(Path.Combine(_source, name), name);
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask);
        var plan = service.Plan(Request()).Data!.AsObject();
        var files = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!;
        var completed = files.Single(file => file.RelativePath == "200.dwg");
        var selected = files.Single(file => file.RelativePath == "201.dwg");
        var secondCompleted = files.Single(file => file.RelativePath == "202.dwg");
        Directory.CreateDirectory(_output);
        File.WriteAllText(completed.OutputPath, "completed-200");
        File.WriteAllText(secondCompleted.OutputPath, "completed-202");
        var completedJob = Guid.NewGuid();
        var secondCompletedJob = Guid.NewGuid();
        SaveCompletedJob(completedJob, completed, HashFile(completed.OutputPath));
        SaveCompletedJob(secondCompletedJob, secondCompleted, HashFile(secondCompleted.OutputPath));
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        SaveBatch(new AgentBatchDocument(batchId, 12, "CompletedWithFailures", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(completed.RelativePath, completed.Sha256, completed.OutputPath, AgentBatchFileState.Reviewing, completedJob),
             new AgentBatchFileProgress(selected.RelativePath, selected.Sha256, selected.OutputPath, AgentBatchFileState.Failed, ErrorCode: "WORKFLOW_BACKGROUND_STALLED"),
             new AgentBatchFileProgress(secondCompleted.RelativePath, secondCompleted.Sha256, secondCompleted.OutputPath, AgentBatchFileState.Reviewing, secondCompletedJob)],
            StartIdempotencyKey: "original-start"));

        var recovery = service.RecoveryPlan(new(batchId, [selected.RelativePath]));
        var started = service.StartRecovery(RecoveryStartRequest(recovery, "selected-201-outputs-200-202-valid"));

        Assert.IsTrue(started.Success, started.Error?.Message);
        Assert.IsFalse(File.Exists(selected.OutputPath));
        Assert.AreEqual("completed-200", File.ReadAllText(completed.OutputPath));
        Assert.AreEqual("completed-202", File.ReadAllText(secondCompleted.OutputPath));
    }

    [TestMethod]
    public void RecoveryRejectsExistingSelectedOutputEvenWhenACompletedJobOwnsAnotherOutput()
    {
        foreach (var name in new[] { "200.dwg", "201.dwg" }) File.WriteAllText(Path.Combine(_source, name), name);
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask);
        var plan = service.Plan(Request()).Data!.AsObject();
        var files = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!;
        var completed = files.Single(file => file.RelativePath == "200.dwg");
        var selected = files.Single(file => file.RelativePath == "201.dwg");
        Directory.CreateDirectory(_output);
        File.WriteAllText(completed.OutputPath, "completed-200");
        File.WriteAllText(selected.OutputPath, "must-not-reuse-selected-output");
        var completedJob = Guid.NewGuid();
        SaveCompletedJob(completedJob, completed, HashFile(completed.OutputPath));
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        SaveBatch(new AgentBatchDocument(batchId, 12, "CompletedWithFailures", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(completed.RelativePath, completed.Sha256, completed.OutputPath, AgentBatchFileState.Reviewing, completedJob),
             new AgentBatchFileProgress(selected.RelativePath, selected.Sha256, selected.OutputPath, AgentBatchFileState.Failed, ErrorCode: "WORKFLOW_BACKGROUND_STALLED")],
            StartIdempotencyKey: "original-start"));

        var recovery = service.RecoveryPlan(new(batchId, [selected.RelativePath]));
        var failed = service.StartRecovery(RecoveryStartRequest(recovery, "selected-output-present")).Error!;

        Assert.AreEqual("BATCH_RECOVERY_OUTPUT_UNSAFE", failed.Code);
        StringAssert.Contains(failed.Message, "SELECTED_OUTPUT_PRESENT_OR_INVALID");
    }

    [TestMethod]
    public void RecoveryRejectsTamperedOrUntrackedSiblingOutput()
    {
        foreach (var name in new[] { "200.dwg", "201.dwg" }) File.WriteAllText(Path.Combine(_source, name), name);
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask);
        var plan = service.Plan(Request()).Data!.AsObject();
        var files = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!;
        var completed = files.Single(file => file.RelativePath == "200.dwg");
        var selected = files.Single(file => file.RelativePath == "201.dwg");
        Directory.CreateDirectory(_output);
        File.WriteAllText(completed.OutputPath, "completed-200");
        var completedJob = Guid.NewGuid();
        SaveCompletedJob(completedJob, completed, HashFile(completed.OutputPath));
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        SaveBatch(new AgentBatchDocument(batchId, 12, "CompletedWithFailures", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(completed.RelativePath, completed.Sha256, completed.OutputPath, AgentBatchFileState.Reviewing, completedJob),
             new AgentBatchFileProgress(selected.RelativePath, selected.Sha256, selected.OutputPath, AgentBatchFileState.Failed, ErrorCode: "WORKFLOW_BACKGROUND_STALLED")],
            StartIdempotencyKey: "original-start"));

        File.WriteAllText(completed.OutputPath, "tampered-200");
        var tampered = service.RecoveryPlan(new(batchId, [selected.RelativePath]));
        var tamperedFailure = service.StartRecovery(RecoveryStartRequest(tampered, "tampered-sibling")).Error!;
        Assert.AreEqual("BATCH_RECOVERY_OUTPUT_UNSAFE", tamperedFailure.Code);
        StringAssert.Contains(tamperedFailure.Message, "OUTPUT_HASH_MISMATCH");

        File.Delete(completed.OutputPath);
        File.Delete(Path.Combine(_workspace, completedJob.ToString("D"), "job.json"));
        File.WriteAllText(completed.OutputPath, "untracked-200");
        var untracked = service.RecoveryPlan(new(batchId, [selected.RelativePath]));
        var untrackedFailure = service.StartRecovery(RecoveryStartRequest(untracked, "untracked-sibling")).Error!;
        Assert.AreEqual("BATCH_RECOVERY_OUTPUT_UNSAFE", untrackedFailure.Code);
        StringAssert.Contains(untrackedFailure.Message, "UNEXPECTED_OUTPUT");
    }

    [TestMethod]
    public void RecoveryAcceptsVerifiedOutputsAcrossMultipleDirectRecoveryDescendants()
    {
        var fixture = CreateDescendantOutputFixture();
        var firstSibling = fixture.Service.RecoveryPlan(new(fixture.ParentBatchId, [fixture.Selected.RelativePath]));
        Assert.IsTrue(fixture.Service.StartRecovery(RecoveryStartRequest(firstSibling, "first-sibling")).Success);

        var next = fixture.Service.RecoveryPlan(new(fixture.ParentBatchId, [fixture.Selected.RelativePath]));
        var started = fixture.Service.StartRecovery(RecoveryStartRequest(next, "second-sibling"));

        Assert.IsTrue(started.Success, started.Error?.Message);
        Assert.IsFalse(File.Exists(fixture.Selected.OutputPath));
        Assert.AreEqual(fixture.CompletedHash, HashFile(fixture.Completed.OutputPath));
    }

    [TestMethod]
    public void RecoveryAcceptsHashVerifiedTransitiveDescendantOutputs()
    {
        foreach (var name in new[] { "200.dwg", "202.dwg", "205.dwg" }) File.WriteAllText(Path.Combine(_source, name), name);
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask);
        var planned = service.Plan(Request()).Data!.AsObject();
        var files = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(planned["files"]!.ToJsonString(), WebJson)!;
        var parentId = Guid.Parse(planned["batchId"]!.GetValue<string>());
        var selected = files.Single(file => file.RelativePath == "200.dwg");
        var firstCompleted = files.Single(file => file.RelativePath == "202.dwg");
        var secondCompleted = files.Single(file => file.RelativePath == "205.dwg");
        Directory.CreateDirectory(_output);
        var selectedJobId = Guid.NewGuid();
        SaveReviewRequiredJob(selectedJobId, selected);
        SaveBatch(new AgentBatchDocument(parentId, 1, "Suspended", planned["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(selected.RelativePath, selected.Sha256, selected.OutputPath, AgentBatchFileState.Reviewing, selectedJobId),
             new AgentBatchFileProgress(firstCompleted.RelativePath, firstCompleted.Sha256, firstCompleted.OutputPath, AgentBatchFileState.Failed, ErrorCode: "WORKFLOW_BACKGROUND_STALLED"),
             new AgentBatchFileProgress(secondCompleted.RelativePath, secondCompleted.Sha256, secondCompleted.OutputPath, AgentBatchFileState.Failed, ErrorCode: "WORKFLOW_BACKGROUND_STALLED")],
            StartIdempotencyKey: "original-start"));

        var directPlan = service.RecoveryPlan(new(parentId, [firstCompleted.RelativePath, secondCompleted.RelativePath]));
        var directStart = service.StartRecovery(RecoveryStartRequest(directPlan, "direct-descendant"));
        Assert.IsTrue(directStart.Success, directStart.Error?.Message);
        var directId = Guid.Parse(directStart.Data!["batchId"]!.GetValue<string>());
        File.WriteAllText(firstCompleted.OutputPath, "completed-202");
        var firstHash = HashFile(firstCompleted.OutputPath);
        SaveBatch(new AgentBatchDocument(directId, 2, "CompletedWithFailures", directPlan.Data!["recoveryManifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(firstCompleted.RelativePath, firstCompleted.Sha256, firstCompleted.OutputPath, AgentBatchFileState.Completed, OutputHash: firstHash),
             new AgentBatchFileProgress(secondCompleted.RelativePath, secondCompleted.Sha256, secondCompleted.OutputPath, AgentBatchFileState.Failed, ErrorCode: "WORKFLOW_BACKGROUND_STALLED")],
            StartIdempotencyKey: "direct-descendant", OriginalBatchId: parentId,
            RecoveryPlanId: directPlan.Data["recoveryPlanId"]!.GetValue<string>()));

        var transitivePlan = service.RecoveryPlan(new(directId, [secondCompleted.RelativePath]));
        var transitiveStart = service.StartRecovery(RecoveryStartRequest(transitivePlan, "transitive-descendant"));
        Assert.IsTrue(transitiveStart.Success, transitiveStart.Error?.Message);
        var transitiveId = Guid.Parse(transitiveStart.Data!["batchId"]!.GetValue<string>());
        File.WriteAllText(secondCompleted.OutputPath, "completed-205");
        var secondHash = HashFile(secondCompleted.OutputPath);
        SaveBatch(new AgentBatchDocument(transitiveId, 2, "Completed", transitivePlan.Data!["recoveryManifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(secondCompleted.RelativePath, secondCompleted.Sha256, secondCompleted.OutputPath, AgentBatchFileState.Completed, OutputHash: secondHash)],
            StartIdempotencyKey: "transitive-descendant", OriginalBatchId: directId,
            RecoveryPlanId: transitivePlan.Data["recoveryPlanId"]!.GetValue<string>()));

        var finalPlan = service.RecoveryPlan(new(parentId, [selected.RelativePath]));
        var finalStart = service.StartRecovery(RecoveryStartRequest(finalPlan, "selected-output-absent"));

        Assert.IsTrue(finalStart.Success, finalStart.Error?.Message);
        Assert.IsFalse(File.Exists(selected.OutputPath));
        Assert.AreEqual(firstHash, HashFile(firstCompleted.OutputPath));
        Assert.AreEqual(secondHash, HashFile(secondCompleted.OutputPath));
    }

    [TestMethod]
    public void RecoveryRejectsForeignOrHashMismatchedDescendantOutput()
    {
        var foreign = CreateDescendantOutputFixture();
        File.WriteAllText(Path.Combine(_output, "foreign.dwg"), "foreign");
        var foreignPlan = foreign.Service.RecoveryPlan(new(foreign.ParentBatchId, [foreign.Selected.RelativePath]));
        var foreignFailure = foreign.Service.StartRecovery(RecoveryStartRequest(foreignPlan, "foreign-output")).Error!;
        Assert.AreEqual("BATCH_RECOVERY_OUTPUT_UNSAFE", foreignFailure.Code);
        StringAssert.Contains(foreignFailure.Message, "UNEXPECTED_OUTPUT");
        StringAssert.Contains(foreignFailure.Message, "foreign.dwg");

        Cleanup();
        Initialize();
        var tampered = CreateDescendantOutputFixture();
        File.WriteAllText(tampered.Completed.OutputPath, "tampered");
        var tamperedPlan = tampered.Service.RecoveryPlan(new(tampered.ParentBatchId, [tampered.Selected.RelativePath]));
        Assert.AreEqual("BATCH_RECOVERY_OUTPUT_UNSAFE", tampered.Service.StartRecovery(RecoveryStartRequest(tamperedPlan, "hash-mismatch")).Error!.Code);
    }

    [TestMethod]
    public void RecoveryRejectsAmbiguousRecoveryLineage()
    {
        var fixture = CreateDescendantOutputFixture();
        SaveBatch(fixture.Child with { BatchId = Guid.NewGuid() });

        var recovery = fixture.Service.RecoveryPlan(new(fixture.ParentBatchId, [fixture.Selected.RelativePath]));
        Assert.AreEqual("BATCH_RECOVERY_OUTPUT_UNSAFE", fixture.Service.StartRecovery(RecoveryStartRequest(recovery, "ambiguous-lineage")).Error!.Code);
    }

    [TestMethod]
    public void RecoveryCandidateRequiresExactFailedJobEvidence()
    {
        var valid = CreateCandidateFixture(hashMatches: true);
        var validPlan = valid.Service.RecoveryPlan(new(valid.ParentBatchId, [valid.Selected.RelativePath]));
        var activeCandidate = valid.Service.StartRecovery(RecoveryStartRequest(validPlan, "valid-candidate")).Error!;
        Assert.AreEqual("BATCH_RECOVERY_OUTPUT_UNSAFE", activeCandidate.Code);
        StringAssert.Contains(activeCandidate.Message, "ACTIVE_CANDIDATE_OR_STAGING");

        Cleanup();
        Initialize();
        var invalid = CreateCandidateFixture(hashMatches: false);
        var invalidPlan = invalid.Service.RecoveryPlan(new(invalid.ParentBatchId, [invalid.Selected.RelativePath]));
        Assert.AreEqual("BATCH_RECOVERY_OUTPUT_UNSAFE", invalid.Service.StartRecovery(RecoveryStartRequest(invalidPlan, "invalid-candidate")).Error!.Code);
    }

    [TestMethod]
    public async Task ReviewReconciliationRefreshesMetricsClearsErrorsAndIssuesVersionBoundApproval()
    {
        File.WriteAllText(Path.Combine(_source, "a.dwg"), "a");
        var processor = new FakeProcessor();
        var service = Service(processor);
        var planned = service.Plan(Request()).Data!.AsObject();
        var batchId = Guid.Parse(planned["batchId"]!.GetValue<string>());
        var manifest = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(planned["files"]!.ToJsonString(), WebJson)!.Single();
        Directory.CreateDirectory(_output);
        SaveBatch(new AgentBatchDocument(batchId, 7, "ReviewRequired", planned["manifestHash"]!.GetValue<string>(),
            _source, _output, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(manifest.RelativePath, manifest.Sha256, manifest.OutputPath,
                AgentBatchFileState.Reviewing, Guid.NewGuid(), ErrorCode: "OPENAI_TIMEOUT", Retryable: true)],
            StartIdempotencyKey: "original-start"));

        var request = new AgentBatchReconcileReviewRequest(batchId, 7, "reconcile-1");
        var started = service.ReconcileReview(request);
        Assert.IsTrue(started.Success, started.Error?.Code);
        await WaitAsync(() => service.Status(batchId).Data!["state"]!.GetValue<string>() == "ReviewRequired");
        var summary = service.ReviewSummary(batchId).Data!.AsObject();
        var file = summary["files"]!.AsArray().Single()!.AsObject();
        Assert.AreEqual("Reviewing", file["state"]!.GetValue<string>());
        Assert.IsNull(file["errorCode"]);
        Assert.IsFalse(file["retryable"]!.GetValue<bool>());
        Assert.AreEqual(1, processor.ReconcileCalls);
        var approval = summary["approval"]!.AsObject();
        Assert.IsTrue(approval["approval"]!.GetValue<string>().EndsWith(":V10", StringComparison.Ordinal));
        Assert.IsTrue(approval["singleUse"]!.GetValue<bool>());

        var replay = service.ReconcileReview(request);
        Assert.IsTrue(replay.Success);
        Assert.IsTrue(replay.Data!["idempotentReplay"]!.GetValue<bool>());
        Assert.AreEqual("IDEMPOTENCY_CONFLICT", service.ReconcileReview(request with { IdempotencyKey = "reconcile-2" }).Error!.Code);

        var generated = service.ApproveAndGenerate(new(batchId, summary["batchVersion"]!.GetValue<long>(),
            approval["approvalId"]!.GetValue<string>(), approval["approval"]!.GetValue<string>(), "generate-reconciled"));
        Assert.IsTrue(generated.Success, generated.Error?.Code);
        await WaitAsync(() => service.Status(batchId).Data!["state"]!.GetValue<string>() == "Completed");
    }

    [TestMethod]
    public async Task TwoCadStallsOpenCircuitAndRestartNeverAutocontinues()
    {
        foreach (var name in new[] { "a.dwg", "b.dwg", "c.dwg" }) File.WriteAllText(Path.Combine(_source, name), name);
        var processor = new FakeProcessor(fail: "a.dwg", failureCode: "WORKFLOW_BACKGROUND_STALLED") { SecondStall = "b.dwg" };
        var service = Service(processor);
        var plan = service.Plan(Request()).Data!.AsObject();
        var approval = plan["approval"]!.AsObject();
        Assert.IsTrue(service.Start(new(plan["planId"]!.GetValue<string>(), plan["manifestHash"]!.GetValue<string>(),
            approval["approvalId"]!.GetValue<string>(), approval["consent"]!.GetValue<string>(), "circuit-start")).Success);
        var batchId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        await WaitAsync(() => service.Status(batchId).Data!["state"]!.GetValue<string>() == "Suspended");
        Assert.AreEqual("BATCH_CAD_CIRCUIT_OPEN", service.Status(batchId).Data!["errorCode"]!.GetValue<string>());
        Assert.AreEqual(2, processor.PrepareCalls);

        var restarted = new AgentBatchService(new AgentBetaConfiguration("dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true,
            _workspace, Path.Combine(_root, "logs"), AllowedDwgRoot: _input, BatchOutputRoots: [_output]), processor);
        Assert.AreEqual(0, restarted.ReconcileRunningBatches());
        Assert.AreEqual("Suspended", restarted.Status(batchId).Data!["state"]!.GetValue<string>());
    }

    private AgentBatchService Service(
        FakeProcessor processor,
        Func<Func<Task>, Task>? schedule = null,
        Func<DateTimeOffset>? utcNow = null) => new(new AgentBetaConfiguration(
        "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true, _workspace, Path.Combine(_root, "logs"),
        AllowedDwgRoot: _input, BatchOutputRoots: [_output]), processor, utcNow: utcNow, schedule: schedule);
    private AgentBatchPlanRequest Request() => new(_source, _output, "en-US", "Auto",
        ArchitecturalMepTerminologyPolicy.Version, AgentBatchPolicy.PolicyVersion);
    private void SaveBatch(AgentBatchDocument document)
    {
        var path = Path.Combine(_workspace, "batches", document.BatchId.ToString("D"), "batch.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(document, WebJson));
    }
    private void TamperPlan(string planId, Action<JsonObject> tamper)
    {
        var path = Path.Combine(_workspace, "batches", "plans", planId + ".json");
        var plan = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        tamper(plan);
        File.WriteAllText(path, plan.ToJsonString(WebJson));
    }
    private void SaveCompletedJob(Guid jobId, AgentBatchManifestEntry file, string outputHash)
    {
        var data = new DwgTranslationJobData(
            new DwgTranslationJobSpecification(file.SourcePath, file.OutputPath, file.Sha256, null, "en-US", "test", null),
            "sha256:" + new string('a', 64), null, [], outputHash,
            new CadExerciseValidationReport("VisualStrictV2", true, false, 1, 0, file.Sha256, outputHash, []));
        var document = new JobDocument(jobId, JobState.Completed, 12, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToNode(data, WebJson)!.AsObject());
        var path = Path.Combine(_workspace, jobId.ToString("D"), "job.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(document, WebJson));
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void SaveFailedGeometryJob(Guid jobId, AgentBatchManifestEntry file, CadInvariantDiagnostics? diagnostics)
    {
        var data = new DwgTranslationJobData(new DwgTranslationJobSpecification(file.SourcePath, file.OutputPath, file.Sha256,
            null, "en-US", "test", null), "sha256:" + new string('a', 64), null, [], null);
        var node = JsonSerializer.SerializeToNode(data, WebJson)!.AsObject();
        var failure = new JsonObject { ["code"] = "GEOMETRY_INVARIANTS_CHANGED" };
        if (diagnostics is not null) failure["invariantDiagnostics"] = JsonSerializer.SerializeToNode(diagnostics, WebJson);
        node["failure"] = failure;
        var path = Path.Combine(_workspace, jobId.ToString("D"), "job.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new JobDocument(jobId, JobState.Failed, 4, DateTimeOffset.UtcNow, node), WebJson));
    }

    private void SaveReviewRequiredJob(Guid jobId, AgentBatchManifestEntry file)
    {
        var data = new DwgTranslationJobData(new DwgTranslationJobSpecification(
            file.SourcePath, file.OutputPath, file.Sha256, null, "en-US", "test", null),
            "sha256:" + new string('a', 64), null, [], null);
        var path = Path.Combine(_workspace, jobId.ToString("D"), "job.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(
            new JobDocument(jobId, JobState.ReviewRequired, 4, DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToNode(data, WebJson)!.AsObject()), WebJson));
    }

    private void SaveFailedTranslationJob(
        Guid jobId,
        AgentBatchManifestEntry file,
        bool retryable,
        string stage,
        bool agentFailure)
    {
        var data = new DwgTranslationJobData(new DwgTranslationJobSpecification(
            file.SourcePath, file.OutputPath, file.Sha256, null, "en-US", "test", null),
            "sha256:" + new string('a', 64), null, [], null);
        var node = JsonSerializer.SerializeToNode(data, WebJson)!.AsObject();
        node[agentFailure ? "agentFailure" : "failure"] = new JsonObject
        {
            ["code"] = "OPENAI_TIMEOUT",
            ["retryable"] = retryable,
            ["stage"] = stage
        };
        var path = Path.Combine(_workspace, jobId.ToString("D"), "job.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(
            new JobDocument(jobId, JobState.Failed, 4, DateTimeOffset.UtcNow, node), WebJson));
    }

    private static CadInvariantDiagnostics TolerableDiagnostics()
    {
        var before = new CadInvariantDiagnosticRow
        {
            EntityHandle = "E1",
            OwnerHandle = "B1",
            DxfType = "ELLIPSE",
            RuntimeClass = "AcDbEllipse",
            OwnerBlockName = "*D1",
            OwnerClass = "AcDbBlockTableRecord",
            IsTargetText = false,
            IsAnonymousDimensionBlockName = true,
            ReferencedByDimensionCount = 1,
            ReferencedByNonDimensionCount = 0,
            DerivedDimensionGraphicsCandidate = true,
            Layer = "P-PIPE",
            ColorIndex = 256,
            LinetypeHandle = "BYLAYER",
            Lineweight = -1,
            Extents = new() { Minimum = "0,0,0", Maximum = "1,2,3" },
            InvariantRowFingerprint = "before"
        };
        var after = before with { Extents = new CadInvariantExtents { Minimum = "0,0,0", Maximum = "1,2.000000000000001,3" }, InvariantRowFingerprint = "after" };
        return new CadInvariantDiagnostics
        {
            Schema = CadInvariantDiagnosticsLimits.Schema,
            AddedCount = 0,
            RemovedCount = 0,
            ChangedCount = 1,
            Truncated = false,
            Rows = [new CadInvariantDifference { InvariantKey = "B1|E1", ChangeKind = "Changed", FieldsChanged = ["extents", "invariantRowFingerprint"], Before = before, After = after,
                ExtentsDelta = new CadInvariantExtentsDelta { MinimumX = "0", MinimumY = "0", MinimumZ = "0",
                    MaximumX = "0", MaximumY = "8.881784197001252E-16", MaximumZ = "0" } }]
        };
    }
    private static async Task WaitAsync(Func<bool> condition)
    { for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10); Assert.IsTrue(condition()); }

    private DescendantFixture CreateDescendantOutputFixture()
    {
        File.WriteAllText(Path.Combine(_source, "a.dwg"), "a");
        File.WriteAllText(Path.Combine(_source, "b.dwg"), "b");
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask);
        var plan = service.Plan(Request()).Data!.AsObject();
        var files = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!;
        var parentId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        Directory.CreateDirectory(_output);
        var selectedJobId = Guid.NewGuid();
        SaveReviewRequiredJob(selectedJobId, files[0]);
        SaveBatch(new AgentBatchDocument(parentId, 1, "Preparing", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(files[0].RelativePath, files[0].Sha256, files[0].OutputPath, AgentBatchFileState.Reviewing, selectedJobId),
             new AgentBatchFileProgress(files[1].RelativePath, files[1].Sha256, files[1].OutputPath, AgentBatchFileState.Failed, ErrorCode: "WORKFLOW_BACKGROUND_STALLED")],
            StartIdempotencyKey: "parent-start"));

        var childPlan = service.RecoveryPlan(new(parentId, [files[1].RelativePath]));
        var childStarted = service.StartRecovery(RecoveryStartRequest(childPlan, "child-start"));
        Assert.IsTrue(childStarted.Success, childStarted.Error?.Code);
        var childId = Guid.Parse(childStarted.Data!["batchId"]!.GetValue<string>());
        File.WriteAllText(files[1].OutputPath, "descendant-completed");
        var hash = HashFile(files[1].OutputPath);
        var child = new AgentBatchDocument(childId, 3, "Completed", childPlan.Data!["recoveryManifestHash"]!.GetValue<string>(),
            _source, _output, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(files[1].RelativePath, files[1].Sha256, files[1].OutputPath, AgentBatchFileState.Completed, OutputHash: hash)],
            StartIdempotencyKey: "child-start", OriginalBatchId: parentId,
            RecoveryPlanId: childPlan.Data["recoveryPlanId"]!.GetValue<string>(),
            RecoveryEntries: [new AgentBatchRecoveryEntry(files[1].RelativePath, AgentBatchRecoveryAction.RetryCadFresh, null, files[1].Sha256, files[1].OutputPath)]);
        SaveBatch(child);
        return new(service, parentId, files[0], files[1], hash, child);
    }

    private CandidateFixture CreateCandidateFixture(bool hashMatches)
    {
        File.WriteAllText(Path.Combine(_source, "a.dwg"), "a");
        File.WriteAllText(Path.Combine(_source, "b.dwg"), "b");
        var service = Service(new FakeProcessor(), _ => Task.CompletedTask);
        var plan = service.Plan(Request()).Data!.AsObject();
        var files = JsonSerializer.Deserialize<AgentBatchManifestEntry[]>(plan["files"]!.ToJsonString(), WebJson)!;
        var parentId = Guid.Parse(plan["batchId"]!.GetValue<string>());
        Directory.CreateDirectory(_output);
        var failedJobId = Guid.NewGuid();
        var selectedJobId = Guid.NewGuid();
        SaveReviewRequiredJob(selectedJobId, files[0]);
        var candidatePath = CandidatePath(files[1].OutputPath, failedJobId);
        File.WriteAllText(candidatePath, "durable-candidate");
        var durableHash = HashFile(candidatePath);
        SaveBatch(new AgentBatchDocument(parentId, 1, "Preparing", plan["manifestHash"]!.GetValue<string>(), _source, _output,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AgentBatchFileProgress(files[0].RelativePath, files[0].Sha256, files[0].OutputPath, AgentBatchFileState.Reviewing, selectedJobId),
             new AgentBatchFileProgress(files[1].RelativePath, files[1].Sha256, files[1].OutputPath, AgentBatchFileState.Failed,
                 failedJobId, OutputHash: hashMatches ? durableHash : "sha256:" + new string('0', 64), ErrorCode: "GEOMETRY_INVARIANTS_CHANGED")],
            StartIdempotencyKey: "parent-start"));
        return new(service, parentId, files[0]);
    }

    private static AgentBatchRecoveryStartRequest RecoveryStartRequest(AgentEnvelope recovery, string idempotencyKey)
    {
        var data = recovery.Data!.AsObject();
        var approval = data["approval"]!.AsObject();
        return new(data["recoveryPlanId"]!.GetValue<string>(), data["recoveryManifestHash"]!.GetValue<string>(),
            approval["approvalId"]!.GetValue<string>(), approval["consent"]!.GetValue<string>(), idempotencyKey);
    }

    private static string CandidatePath(string finalPath, Guid jobId) =>
        Path.Combine(Path.GetDirectoryName(finalPath)!, $".{Path.GetFileNameWithoutExtension(finalPath)}.candidate-{jobId:N}{Path.GetExtension(finalPath)}");

    private sealed record DescendantFixture(AgentBatchService Service, Guid ParentBatchId, AgentBatchManifestEntry Selected,
        AgentBatchManifestEntry Completed, string CompletedHash, AgentBatchDocument Child);
    private sealed record CandidateFixture(AgentBatchService Service, Guid ParentBatchId, AgentBatchManifestEntry Selected);

    private sealed class FakeProcessor(string? fail = null, string? failureCode = null, string? reconcileFailure = null) : IAgentBatchFileProcessor
    {
        public int PrepareCalls, GenerateCalls, ReconcileCalls;
        public string? SecondStall { get; init; }
        public Task<AgentBatchFileProgress> PrepareAsync(AgentBatchPlan plan, AgentBatchManifestEntry file, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            var failed = file.RelativePath == fail || file.RelativePath == SecondStall;
            return Task.FromResult(new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath,
                failed ? AgentBatchFileState.Failed : AgentBatchFileState.Reviewing,
                ErrorCode: failed ? failureCode ?? "FAKE_FAILURE" : null, TextCount: 2, MTextCount: 3,
                InputTokens: 10, OutputTokens: 5, ProviderRequests: 2, TerminologyMatches: 1));
        }
        public Task<AgentBatchFileProgress> ApproveAndGenerateAsync(AgentBatchPlan plan, AgentBatchFileProgress file, CancellationToken cancellationToken)
        {
            GenerateCalls++; File.WriteAllText(file.OutputPath, "synthetic");
            return Task.FromResult(file with { State = AgentBatchFileState.Completed, OutputHash = "sha256:" + new string('a', 64), VisualReviewPending = true });
        }
        public Task<AgentBatchFileProgress> ReconcileReviewAsync(AgentBatchPlan plan, AgentBatchFileProgress file,
            ReviewAutomationScopeTransition? scopeTransition, CancellationToken cancellationToken)
        {
            ReconcileCalls++;
            if (reconcileFailure is not null)
                return Task.FromResult(file with { State = AgentBatchFileState.Failed, ErrorCode = reconcileFailure, Retryable = false });
            return Task.FromResult(file with
            {
                State = AgentBatchFileState.Reviewing,
                ErrorCode = null,
                Retryable = false,
                TextCount = 2,
                MTextCount = 3,
                InputTokens = 10,
                OutputTokens = 5,
                ProviderRequests = 2,
                EffectiveModel = "gpt-5.6-terra"
            });
        }
        public Task<AgentBatchFileProgress> ResumeTranslationMissingOnlyAsync(AgentBatchPlan plan, AgentBatchFileProgress file,
            ReviewAutomationScopeTransition scopeTransition, CancellationToken cancellationToken) =>
            Task.FromResult(file with
            {
                State = AgentBatchFileState.Reviewing,
                ErrorCode = null,
                Retryable = false,
                TextCount = 2,
                MTextCount = 3,
                InputTokens = 10,
                OutputTokens = 5,
                ProviderRequests = 2,
                EffectiveModel = "gpt-5.6-terra"
            });
    }
}
