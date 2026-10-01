using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DwgTranslator.Agent;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent.Tests;

[TestClass]
public sealed class AgentQueryServiceTests
{
    private string _root = null!;
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 23, 0, 0, TimeSpan.Zero);

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwg-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "jobs"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void HealthReportsReadyReadOnlyEnvironment()
    {
        var response = Service().Health();

        Assert.IsTrue(response.Success);
        Assert.AreEqual("ready", response.Data!["status"]!.GetValue<string>());
        Assert.IsTrue(response.Data["readOnly"]!.GetValue<bool>());
        Assert.IsFalse(response.Data["executionEnabled"]!.GetValue<bool>());
    }

    [TestMethod]
    public void ListJobsReturnsOnlyValidJobDirectories()
    {
        var jobId = Guid.NewGuid();
        WriteJob(jobId, "Completed", includeFailure: false);
        Directory.CreateDirectory(Path.Combine(_root, "jobs", "not-a-job"));

        var response = Service().ListJobs();

        Assert.IsTrue(response.Success);
        Assert.AreEqual(1, response.Data!["count"]!.GetValue<int>());
        Assert.AreEqual(jobId.ToString("D"), response.Data["jobs"]![0]!["jobId"]!.GetValue<string>());
    }

    [TestMethod]
    public void ListJobsProjectsLargeDocumentsWithoutReturningHeavyPayloads()
    {
        var jobId = Guid.NewGuid();
        WriteJob(jobId, "Failed", includeFailure: true);
        var path = Path.Combine(_root, "jobs", jobId.ToString("D"), "job.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["data"]!["specification"]!["largeIgnoredPayload"] = new string('x', 5 * 1024 * 1024);
        File.WriteAllText(path, document.ToJsonString());

        var response = Service().ListJobs();
        var job = response.Data!["jobs"]![0]!;

        Assert.IsTrue(response.Success);
        Assert.AreEqual("abc", job["outputHash"]!.GetValue<string>());
        Assert.AreEqual("IPC_TIMEOUT", job["failure"]!["code"]!.GetValue<string>());
        Assert.IsLessThan(1024, job.ToJsonString().Length);
    }

    [TestMethod]
    public void GetJobReturnsAllowlistedFailureMetadata()
    {
        var jobId = Guid.NewGuid();
        WriteJob(jobId, "Failed", includeFailure: true);

        var response = Service().GetJob(jobId);

        Assert.IsTrue(response.Success);
        Assert.AreEqual("IPC_TIMEOUT", response.Data!["failure"]!["code"]!.GetValue<string>());
        Assert.IsNull(response.Data["failure"]!["exceptionType"]);
        Assert.AreEqual("es-MX", response.Data["specification"]!["targetLanguage"]!.GetValue<string>());
    }

    [TestMethod]
    public void GetJobReturnsStructuredInvariantEvidenceWithoutText()
    {
        var jobId = Guid.NewGuid();
        WriteJob(jobId, "Failed", includeFailure: true);
        var path = Path.Combine(_root, "jobs", jobId.ToString("D"), "job.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["data"]!["failure"]!["invariantDiagnostics"] = new JsonObject
        {
            ["schema"] = "cad-invariant-diagnostics/1.0",
            ["rows"] = new JsonArray(new JsonObject
            {
                ["invariantKey"] = "10|A1",
                ["changeKind"] = "Changed",
                ["fieldsChanged"] = new JsonArray("extents"),
                ["before"] = new JsonObject
                {
                    ["entityHandle"] = "A1",
                    ["textString"] = "must-not-leak",
                    ["contents"] = "must-not-leak"
                }
            })
        };
        File.WriteAllText(path, document.ToJsonString());

        var response = Service().GetJob(jobId);
        var json = response.Data!["failure"]!["invariantDiagnostics"]!.ToJsonString();

        Assert.AreEqual("cad-invariant-diagnostics/1.0", response.Data["failure"]!["invariantDiagnostics"]!["schema"]!.GetValue<string>());
        Assert.IsFalse(json.Contains("TextString", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Contents", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("must-not-leak", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GetJobRejectsMissingJob()
    {
        var response = Service().GetJob(Guid.NewGuid());

        Assert.IsFalse(response.Success);
        Assert.AreEqual("AGENT_JOB_NOT_FOUND", response.Error!.Code);
    }

    private AgentQueryService Service() => new(
        new("dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, false,
            Path.Combine(_root, "jobs"), Path.Combine(_root, "logs")),
        () => Now);

    private void WriteJob(Guid jobId, string state, bool includeFailure)
    {
        var directory = Path.Combine(_root, "jobs", jobId.ToString("D"));
        Directory.CreateDirectory(directory);
        var data = new JsonObject
        {
            ["specification"] = new JsonObject
            {
                ["sourcePath"] = "C:\\Drawings\\source.dwg",
                ["outputPath"] = "C:\\Drawings\\translated.dwg",
                ["targetLanguage"] = "es-MX"
            },
            ["outputHash"] = "abc"
        };
        if (includeFailure)
            data["failure"] = new JsonObject
            {
                ["code"] = "IPC_TIMEOUT",
                ["category"] = "Transport",
                ["retryable"] = true,
                ["stage"] = "Writing",
                ["diagnosticId"] = "diag-1",
                ["exceptionType"] = "Sensitive.Internal.Type"
            };
        var document = new JsonObject
        {
            ["jobId"] = jobId.ToString("D"),
            ["state"] = state,
            ["version"] = 4,
            ["updatedAtUtc"] = Now.ToString("O"),
            ["data"] = data
        };
        File.WriteAllText(Path.Combine(directory, "job.json"), document.ToJsonString());
    }
}

[TestClass]
public sealed class AgentCadInspectionServiceTests
{
    private string _root = null!;
    private string _source = null!;

    [TestMethod]
    public async Task InspectionReturnsAllSegmentsForReviewedDrawingAboveLegacyBound()
    {
        var expectedHash = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_source))).ToLowerInvariant();
        var result = await new AgentCadInspectionService(Configuration(true), new LargeReadExecutor(291))
            .InspectAsync(new(_source, expectedHash, AgentCadInspectionService.ApprovalPhrase), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error?.Code);
        Assert.AreEqual(291, result.Data!["segmentCount"]!.GetValue<int>());
        Assert.AreEqual(291, result.Data["returnedSegmentCount"]!.GetValue<int>());
        Assert.IsFalse(result.Data["truncated"]!.GetValue<bool>());
        Assert.AreEqual(291, result.Data["segments"]!.AsArray().Count);
    }

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwg-agent-cad-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "sample.dwg");
        File.WriteAllText(_source, "SYNTHETIC-NOT-A-REAL-DWG");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task PlanHashesAllowlistedSourceWithoutOpeningCad()
    {
        var executor = new RejectingExecutor();
        var service = new AgentCadInspectionService(Configuration(executionEnabled: false), executor);

        var result = await service.CreatePlanAsync(_source, CancellationToken.None);

        Assert.IsTrue(result.Success);
        StringAssert.StartsWith(result.Data!["sourceHash"]!.GetValue<string>(), "sha256:");
        Assert.AreEqual(AgentCadInspectionService.ApprovalPhrase,
            result.Data["approvalRequired"]!.GetValue<string>());
        Assert.AreEqual(0, executor.Calls);
    }

    [TestMethod]
    public async Task PlanRejectsSourceOutsideConfiguredRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dwg");
        await File.WriteAllTextAsync(outside, "OUTSIDE");
        try
        {
            var result = await new AgentCadInspectionService(Configuration(false), new RejectingExecutor())
                .CreatePlanAsync(outside, CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("AGENT_SOURCE_PATH_FORBIDDEN", result.Error!.Code);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [TestMethod]
    public async Task InspectRequiresExecutionAndExactApprovalBeforeOpeningCad()
    {
        var disabledExecutor = new RejectingExecutor();
        var disabled = await new AgentCadInspectionService(Configuration(false), disabledExecutor)
            .InspectAsync(new(_source, "sha256:" + new string('a', 64), AgentCadInspectionService.ApprovalPhrase), CancellationToken.None);
        var enabledExecutor = new RejectingExecutor();
        var unapproved = await new AgentCadInspectionService(Configuration(true), enabledExecutor)
            .InspectAsync(new(_source, "sha256:" + new string('a', 64), "yes"), CancellationToken.None);

        Assert.AreEqual("AGENT_CAD_EXECUTION_DISABLED", disabled.Error!.Code);
        Assert.AreEqual("AGENT_CAD_APPROVAL_REQUIRED", unapproved.Error!.Code);
        Assert.AreEqual(0, disabledExecutor.Calls);
        Assert.AreEqual(0, enabledExecutor.Calls);
    }

    [TestMethod]
    public async Task InspectRejectsStaleApprovedHashBeforeOpeningCad()
    {
        var executor = new RejectingExecutor();
        var result = await new AgentCadInspectionService(Configuration(true), executor)
            .InspectAsync(new(_source, "sha256:" + new string('a', 64), AgentCadInspectionService.ApprovalPhrase), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("AGENT_SOURCE_HASH_MISMATCH", result.Error!.Code);
        Assert.AreEqual(0, executor.Calls);
    }

    private AgentBetaConfiguration Configuration(bool executionEnabled) => new(
        "dwg-agent-beta-bootstrap/1.0",
        "AgentBeta",
        true,
        executionEnabled,
        Path.Combine(_root, "jobs"),
        Path.Combine(_root, "logs"),
        AllowedDwgRoot: _root);

    private sealed class RejectingExecutor : IAgentCadReadExecutor
    {
        public int Calls { get; private set; }

        public Task<Result<CadReadResult>> InspectAndExtractAsync(
            Guid jobId,
            string sourcePath,
            string expectedSourceHash,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("CAD must not open before all gates pass.");
        }
    }

    private sealed class LargeReadExecutor(int count) : IAgentCadReadExecutor
    {
        public Task<Result<CadReadResult>> InspectAndExtractAsync(
            Guid jobId,
            string sourcePath,
            string expectedSourceHash,
            CancellationToken cancellationToken)
        {
            var segments = Enumerable.Range(1, count).Select(index => new CadTextSegment
            {
                SegmentId = $"segment-{index}",
                Entity = new CadEntityReference
                {
                    Type = "TEXT",
                    Handle = index.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
                    Space = "ModelSpace",
                    Layout = null,
                    BlockPath = [],
                    Layer = "0",
                    SubIndex = 0
                },
                SourceText = "A",
                SourceTextHash = "sha256:" + new string('a', 64),
                LineBreakStyle = "None",
                ProtectedTokens = [],
                FieldClassification = "None",
                State = "Extracted"
            }).ToList();
            var inspection = new CadInspectResponsePayload
            {
                SourceHash = expectedSourceHash,
                Autocad = new CadHostDescriptor { Product = "AutoCAD", Year = 2026, ApiVersion = "25.1" },
                DrawingFingerprint = "sha256:" + new string('b', 64),
                Inventory = new Dictionary<string, int> { ["TEXT"] = count },
                SupportedCount = count,
                Unsupported = [],
                Warnings = []
            };
            return Task.FromResult(Results.Success(new CadReadResult(inspection, segments, 0)));
        }
    }
}

[TestClass]
public sealed class AgentHostSecurityTests
{
    [TestMethod]
    public void BearerAuthenticationRequiresExactToken()
    {
        Assert.IsTrue(AgentBearerAuthenticator.Validate("correct-token", "correct-token"));
        Assert.IsFalse(AgentBearerAuthenticator.Validate("wrong-token", "correct-token"));
        Assert.IsFalse(AgentBearerAuthenticator.Validate(null, "correct-token"));
    }

    [TestMethod]
    public void PolicyRejectsUnknownOrMutationOperations()
    {
        var policy = new AgentHostPolicy(new[] { AgentHostOperations.Health, AgentHostOperations.JobsList });
        var invalidOperations = new[] { "jobs.create" };

        Assert.IsTrue(policy.Allows(AgentHostOperations.Health));
        Assert.IsFalse(policy.Allows("jobs.create"));
        Assert.ThrowsExactly<ArgumentException>(() => new AgentHostPolicy(invalidOperations));
    }

    [TestMethod]
    public async Task IdempotencyRegistryDetectsDuplicateAndConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "dwg-agent-idempotency", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new FileIdempotencyRegistry(root);
            var first = await registry.ClaimAsync("request-1", new string('a', 64), CancellationToken.None);
            var duplicate = await registry.ClaimAsync("request-1", new string('a', 64), CancellationToken.None);
            var conflict = await registry.ClaimAsync("request-1", new string('b', 64), CancellationToken.None);

            Assert.AreEqual(IdempotencyClaimResult.Acquired, first);
            Assert.AreEqual(IdempotencyClaimResult.Duplicate, duplicate);
            Assert.AreEqual(IdempotencyClaimResult.Conflict, conflict);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AuditWritesOnlyAllowlistedMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "dwg-agent-audit", Guid.NewGuid().ToString("N"));
        try
        {
            var sink = new FileAgentAuditSink(root);
            await sink.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "health.read", "Succeeded", 200,
                Guid.NewGuid().ToString("D"), null), CancellationToken.None);

            var content = await File.ReadAllTextAsync(Path.Combine(root, "audit", "agent-host.jsonl"));
            StringAssert.Contains(content, "health.read");
            Assert.IsFalse(content.Contains("Bearer", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
