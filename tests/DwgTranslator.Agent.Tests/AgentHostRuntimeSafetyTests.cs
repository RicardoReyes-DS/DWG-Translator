using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Agent;
using DwgTranslator.Application;

namespace DwgTranslator.Agent.Tests;

[TestClass]
public sealed class AgentHostRuntimeSafetyTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void SecondHostCannotOwnSameEnvironmentAndEndpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "dwg-host-owner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = Configuration(root);
            using var first = AgentHostOwnershipLease.TryAcquire(configuration);
            using var second = AgentHostOwnershipLease.TryAcquire(configuration);
            Assert.IsNotNull(first);
            Assert.IsNull(second);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void HealthPublishesNonSecretRuntimeIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "dwg-host-health-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = Configuration(root);
            var identity = new AgentHostRuntimeIdentity("instance", "1.2.3", "sha256:test", 42);
            var health = new AgentQueryService(configuration, identity: identity).Health();
            var data = health.Data!.AsObject()["identity"]!.AsObject();
            Assert.AreEqual("instance", data["instanceId"]!.GetValue<string>());
            Assert.AreEqual(42, data["processId"]!.GetValue<int>());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExplicitBatchAllowlistPermitsEveryBatchOperationAndPreservesSingleFileOperations()
    {
        var operations = new[]
        {
            AgentHostOperations.Health, AgentHostOperations.Capabilities, AgentHostOperations.TranslationPlan,
            AgentHostOperations.BatchPlan, AgentHostOperations.BatchStart, AgentHostOperations.BatchStatus,
            AgentHostOperations.BatchNextAction, AgentHostOperations.BatchReviewSummary,
            AgentHostOperations.BatchApproveAndGenerate, AgentHostOperations.BatchReport, AgentHostOperations.BatchCancel
        };

        var policy = new AgentHostPolicy(operations);

        foreach (var operation in operations) Assert.IsTrue(policy.Allows(operation), operation);
        Assert.IsFalse(policy.Allows("batch.unknown"));
        Assert.IsTrue(policy.Allows(AgentHostOperations.TranslationPlan));
    }

    [TestMethod]
    public void UnknownAllowlistOperationIsRejected()
    {
        try
        {
            _ = new AgentHostPolicy(new[] { AgentHostOperations.BatchPlan, "batch.unknown" });
            Assert.Fail("Expected an allowlist validation failure.");
        }
        catch (ArgumentException exception)
        {
            StringAssert.StartsWith(exception.Message, "AGENT_ALLOWLIST_INVALID");
        }
    }

    [TestMethod]
    public void NewAgentConfigurationsUseTheValidatedOpenAiTimeoutCeiling()
    {
        var configuration = new AgentBetaConfiguration(
            "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, false,
            "c:\\jobs", "c:\\logs");

        Assert.AreEqual(120, configuration.OpenAiTimeoutSeconds);
    }

    [TestMethod]
    public void ConfigurationLoaderAcceptsBoundedCadStartupAndOpenAiTimeoutCeilings()
    {
        var root = Path.Combine(Path.GetTempPath(), "dwg-agent-config-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            var input = Directory.CreateDirectory(Path.Combine(root, "input")).FullName;
            var output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
            var readBundle = Directory.CreateDirectory(Path.Combine(root, "read.bundle")).FullName;
            var writeBundle = Directory.CreateDirectory(Path.Combine(root, "write.bundle")).FullName;
            var host = Path.Combine(root, "host.exe");
            var acad = Path.Combine(root, "acad.exe");
            var readAdapter = Path.Combine(readBundle, "read.dll");
            var writeAdapter = Path.Combine(writeBundle, "write.dll");
            foreach (var filePath in new[]
                     {
                         host, acad, readAdapter, writeAdapter,
                         Path.Combine(readBundle, "PackageContents.xml"),
                         Path.Combine(writeBundle, "PackageContents.xml")
                     })
                File.WriteAllText(filePath, "synthetic");
            var configurationPath = Path.Combine(root, "agent.json");
            var configuration = new AgentBetaConfiguration(
                "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true,
                workspace, logs,
                HostExecutablePath: host,
                AllowedDwgRoot: input,
                AutoCadExecutablePath: acad,
                ReadOnlyBundleDirectory: readBundle,
                ReadOnlyAdapterAssemblyPath: readAdapter,
                OpenAiEnabled: true,
                OutputDwgRoot: output,
                WriteBundleDirectory: writeBundle,
                WriteAdapterAssemblyPath: writeAdapter,
                CadStartupSeconds: 1800,
                OpenAiTimeoutSeconds: 120,
                ConfiguredAccessibleModels: ["gpt-5.6-terra"]);
            File.WriteAllText(configurationPath, JsonSerializer.Serialize(configuration, WebJson));

            var accepted = AgentConfigurationLoader.Load(configurationPath);

            Assert.IsNull(accepted.Error);
            Assert.AreEqual(1800, accepted.Configuration!.CadStartupSeconds);
            Assert.AreEqual(120, accepted.Configuration!.OpenAiTimeoutSeconds);
            Assert.AreEqual(30, accepted.Configuration.ApprovalLifetimeMinutes);
            Assert.AreEqual(TranslationReviewWorkflow.ContextualPromptTemplateVersion,
                accepted.Configuration.PromptTemplateVersion);

            File.WriteAllText(configurationPath, JsonSerializer.Serialize(
                configuration with { CadStartupSeconds = 1801 }, WebJson));
            var excessiveCadStartup = AgentConfigurationLoader.Load(configurationPath);
            Assert.AreEqual("AGENT_CAD_CONFIGURATION_INVALID", excessiveCadStartup.Error!.Code);

            File.WriteAllText(configurationPath, JsonSerializer.Serialize(
                configuration with { PromptTemplateVersion = "translate-cad-text/1.1" }, WebJson));
            var legacyPrompt = AgentConfigurationLoader.Load(configurationPath);
            Assert.AreEqual("AGENT_WORKFLOW_CONFIGURATION_INVALID", legacyPrompt.Error!.Code);

            File.WriteAllText(configurationPath, JsonSerializer.Serialize(
                configuration with { OpenAiTimeoutSeconds = 121 }, WebJson));
            var rejected = AgentConfigurationLoader.Load(configurationPath);
            Assert.AreEqual("AGENT_WORKFLOW_CONFIGURATION_INVALID", rejected.Error!.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static AgentBetaConfiguration Configuration(string root) => new(
        "dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true, root, root,
        HostUrl: "http://127.0.0.1:47831");
}
