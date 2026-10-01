using System.Reflection;
using System.Text.Json;
using DwgTranslator.AgentMcp;
using ModelContextProtocol.Server;

namespace DwgTranslator.AgentMcp.Tests;

[TestClass]
public sealed class AgentMcpToolsTests
{
    private static readonly string[] WriteTools =
        ["dwg_translation_prepare", "dwg_translation_review_apply", "dwg_generation_reconcile_apply", "dwg_generate", "dwg_workflow_cancel",
         "dwg_batch_start", "dwg_batch_approve_and_generate", "dwg_batch_cancel", "dwg_batch_recovery_start",
         "dwg_batch_reconcile_review"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StrictWebJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [TestMethod]
    public async Task HealthReturnsStructuredSuccessfulResult()
    {
        var result = await new AgentMcpTools(new SelfTestAgentHostReadClient()).HealthAsync(CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.IsNotNull(result.StructuredContent);
        Assert.IsTrue(result.StructuredContent.Value.GetProperty("success").GetBoolean());
    }

    [TestMethod]
    public async Task InvalidJobIdReturnsToolErrorWithoutCallingHost()
    {
        var result = await new AgentMcpTools(new SelfTestAgentHostReadClient())
            .GetJobAsync("not-a-guid", CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual("AGENT_JOB_ID_INVALID",
            result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public void ToolSurfaceSeparatesReadAndWriteWorkflowOperations()
    {
        var names = typeof(AgentMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var expected = new[]
        {
            "dwg_batch_approve_and_generate", "dwg_batch_cancel", "dwg_batch_next_action", "dwg_batch_plan",
            "dwg_batch_reconcile_review", "dwg_batch_recovery_plan", "dwg_batch_recovery_start", "dwg_batch_report", "dwg_batch_review_summary", "dwg_batch_start", "dwg_batch_status",
            "dwg_capabilities", "dwg_generate", "dwg_generation_plan", "dwg_generation_reconcile_apply", "dwg_generation_reconcile_plan", "dwg_health", "dwg_inspect_dry_run",
            "dwg_inspection_plan", "dwg_invariant_diff_plan", "dwg_invariant_diff_run", "dwg_job_get", "dwg_job_next_action", "dwg_job_status", "dwg_jobs_list",
            "dwg_translation_plan", "dwg_translation_prepare", "dwg_translation_review_apply",
            "dwg_translation_review_get", "dwg_workflow_cancel"
        };
        CollectionAssert.AreEqual(expected, names);

        var methods = typeof(AgentMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .ToDictionary(attribute => attribute!.Name!, attribute => attribute!, StringComparer.Ordinal);
        foreach (var name in WriteTools)
            Assert.IsFalse(methods[name].ReadOnly);
        foreach (var name in expected.Except(WriteTools))
            Assert.IsTrue(methods[name].ReadOnly);
        Assert.IsTrue(methods["dwg_translation_prepare"].OpenWorld);
        Assert.IsTrue(methods["dwg_generate"].Destructive);
        Assert.IsTrue(methods["dwg_generation_reconcile_apply"].Destructive);
        Assert.IsTrue(methods["dwg_generation_reconcile_plan"].ReadOnly);
        Assert.IsTrue(methods["dwg_batch_start"].OpenWorld);
        Assert.IsTrue(methods["dwg_batch_approve_and_generate"].Destructive);
        Assert.IsTrue(methods["dwg_batch_recovery_plan"].ReadOnly);
        Assert.IsFalse(methods["dwg_batch_recovery_start"].ReadOnly);
        Assert.IsFalse(methods["dwg_batch_reconcile_review"].Destructive);
        Assert.IsFalse(methods["dwg_batch_reconcile_review"].OpenWorld);
    }

    [TestMethod]
    public void HttpBridgeRejectsNonLoopbackOrigins()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AgentHostHttpReadClient(new Uri("https://example.com"), "test-token"));
    }

    [TestMethod]
    public void ReviewApplyKeepsLegacyArgumentsAndAddsOptionalContextAuthority()
    {
        var method = typeof(AgentMcpTools).GetMethod(nameof(AgentMcpTools.ApplyTranslationReviewAsync))!;
        var parameters = method.GetParameters().ToDictionary(parameter => parameter.Name!, StringComparer.Ordinal);

        foreach (var legacy in new[] { "jobId", "expectedJobVersion", "decisions", "bulkApprove", "bulkApproval", "idempotencyKey" })
            Assert.IsTrue(parameters.ContainsKey(legacy));
        Assert.IsTrue(parameters["expectedReviewVersion"].HasDefaultValue);
        Assert.IsTrue(parameters["expectedContextHash"].HasDefaultValue);
        Assert.IsTrue(parameters["automationAuthority"].HasDefaultValue);
        Assert.AreEqual(typeof(AgentMcpReviewAutomationReceipt),
            Nullable.GetUnderlyingType(parameters["automationAuthority"].ParameterType) ?? parameters["automationAuthority"].ParameterType);

        var receipt = new AgentMcpReviewAutomationReceipt(
            "contextual-agent-review-create-new/1.0", Guid.NewGuid().ToString("D"),
            "sha256:" + new string('a', 64), "sha256:" + new string('b', 64),
            "sha256:" + new string('c', 64), "sha256:" + new string('d', 64));
        var json = JsonSerializer.Serialize(receipt, WebJson);
        StringAssert.Contains(json, "\"reviewerReportHash\":\"sha256:");
        StringAssert.Contains(json, "\"qaReportHash\":\"sha256:");
    }

    [TestMethod]
    public void McpResultStrictlyAcceptsAgentHostEnvelope()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var hostEnvelope = new
        {
            schemaVersion = "dwg-agent-cli/1.0",
            success = true,
            command = "health",
            timestampUtc = timestamp,
            data = new { status = "ready" },
            error = (object?)null
        };
        var json = JsonSerializer.Serialize(hostEnvelope, WebJson);

        var result = JsonSerializer.Deserialize<AgentMcpResult>(json, StrictWebJson);

        Assert.IsNotNull(result);
        Assert.AreEqual(timestamp, result.TimestampUtc);
        Assert.AreEqual("ready", result.Data!.Value.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task StartupCoordinatorReusesReadyHostWithoutStartingProcess()
    {
        var dependencies = new FakeStartupDependencies("token", ready: true, startResult: false);

        var result = await new AgentHostStartupCoordinator(dependencies, 1, TimeSpan.FromMilliseconds(1))
            .EnsureReadyAsync(CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("token", result.Token);
        Assert.AreEqual(0, dependencies.StartCalls);
    }

    [TestMethod]
    public async Task StartupCoordinatorStartsHostAndWaitsForCredential()
    {
        var dependencies = new FakeStartupDependencies(null, ready: false, startResult: true)
        {
            TokenAfterStart = "new-token"
        };

        var result = await new AgentHostStartupCoordinator(dependencies, 2, TimeSpan.FromMilliseconds(1))
            .EnsureReadyAsync(CancellationToken.None, allowStart: true);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("new-token", result.Token);
        Assert.AreEqual(1, dependencies.StartCalls);
    }

    [TestMethod]
    public async Task StartupCoordinatorNeverAutostartsForReadClient()
    {
        var dependencies = new FakeStartupDependencies(null, ready: false, startResult: true);
        var result = await new AgentHostStartupCoordinator(dependencies, 1, TimeSpan.FromMilliseconds(1))
            .EnsureReadyAsync(CancellationToken.None);
        Assert.IsFalse(result.Success);
        Assert.AreEqual("AGENT_HOST_UNAVAILABLE", result.ErrorCode);
        Assert.AreEqual(0, dependencies.StartCalls);
    }

    private sealed class FakeStartupDependencies(string? token, bool ready, bool startResult) : IAgentHostStartupDependencies
    {
        private bool _started;
        public string? TokenAfterStart { get; init; }
        public int StartCalls { get; private set; }

        public Task<string?> ReadTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_started ? TokenAfterStart : token);

        public Task<bool> IsReadyAsync(string suppliedToken, CancellationToken cancellationToken) =>
            Task.FromResult(ready || (_started && suppliedToken == TokenAfterStart));

        public bool TryStart()
        {
            StartCalls++;
            _started = startResult;
            return startResult;
        }
    }
}
