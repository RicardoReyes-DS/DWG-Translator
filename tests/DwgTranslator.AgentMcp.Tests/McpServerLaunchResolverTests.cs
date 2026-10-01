using DwgTranslator.AgentMcpProbe;

namespace DwgTranslator.AgentMcp.Tests;

[TestClass]
public sealed class McpServerLaunchResolverTests
{
    private static readonly string[] ConfigArguments = ["--config", "C:\\sandbox\\agent-beta.bootstrap.v1.json"];
    private static readonly string[] SelfTestArguments = ["--self-test"];
    private string _root = null!;
    private string _runtime = null!;
    private string _serverDirectory = null!;
    private string _exe = null!;
    private string _dll = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwg-mcp-launch-tests", Guid.NewGuid().ToString("N"));
        _runtime = Path.Combine(_root, "candidate");
        _serverDirectory = Path.Combine(_runtime, "AgentMcpServer");
        Directory.CreateDirectory(_serverDirectory);
        _exe = Path.Combine(_serverDirectory, "DwgTranslator.AgentMcpServer.exe");
        _dll = Path.Combine(_serverDirectory, "DwgTranslator.AgentMcpServer.dll");
        foreach (var path in new[]
        {
            _exe, _dll,
            Path.Combine(_serverDirectory, "DwgTranslator.AgentMcpServer.deps.json"),
            Path.Combine(_serverDirectory, "DwgTranslator.AgentMcpServer.runtimeconfig.json")
        }) File.WriteAllText(path, "fixture");
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void ExecutableIsLaunchedDirectlyAndNeverThroughDotnet()
    {
        var launch = McpServerLaunchResolver.Resolve(_exe, null, _runtime);

        Assert.AreEqual(_exe, launch.Command);
        CollectionAssert.AreEqual(SelfTestArguments, launch.Arguments.ToArray());
        Assert.AreNotEqual("dotnet", launch.Command);
    }

    [TestMethod]
    public void ExecutableReceivesConfigurationWithoutDotnet()
    {
        var launch = McpServerLaunchResolver.Resolve(_exe, "C:\\sandbox\\agent-beta.bootstrap.v1.json", _runtime);

        Assert.AreEqual(_exe, launch.Command);
        CollectionAssert.AreEqual(ConfigArguments, launch.Arguments.ToArray());
    }

    [TestMethod]
    public void AssemblyIsLaunchedOnlyWithDotnet()
    {
        var launch = McpServerLaunchResolver.Resolve(_dll, null, _runtime);

        Assert.AreEqual("dotnet", launch.Command);
        CollectionAssert.AreEqual(new[] { _dll, "--self-test" }, launch.Arguments.ToArray());
    }

    [TestMethod]
    public void RejectsMixedOrUnexpectedExecutableAndWrongRuntimePath()
    {
        var mixed = Path.Combine(_serverDirectory, "DwgTranslator.AgentMcpServer.exe.dll");
        File.WriteAllText(mixed, "fixture");
        var mixedFailure = Capture<InvalidOperationException>(() => McpServerLaunchResolver.Resolve(mixed, null, _runtime));
        Assert.AreEqual("MCP_PROBE_SERVER_ARTIFACT_INVALID", mixedFailure.Message);

        var otherRuntime = Path.Combine(_root, "other");
        Directory.CreateDirectory(Path.Combine(otherRuntime, "AgentMcpServer"));
        var wrongFailure = Capture<InvalidOperationException>(() => McpServerLaunchResolver.Resolve(_exe, null, otherRuntime));
        Assert.AreEqual("MCP_PROBE_SERVER_OUTSIDE_RUNTIME", wrongFailure.Message);
    }

    [TestMethod]
    public void RejectsMissingServerArtifact()
    {
        File.Delete(Path.Combine(_serverDirectory, "DwgTranslator.AgentMcpServer.runtimeconfig.json"));
        var failure = Capture<FileNotFoundException>(() => McpServerLaunchResolver.Resolve(_dll, null, _runtime));
        Assert.AreEqual("MCP_PROBE_SERVER_ARTIFACT_MISSING", failure.Message);
    }

    [TestMethod]
    public void ProbeContractRequiresBothGenerationReconciliationTools()
    {
        var actual = McpProbeToolContract.ExpectedToolNames.ToArray();

        Assert.IsTrue(McpProbeToolContract.IsExact(actual));
        CollectionAssert.Contains(actual, "dwg_generation_reconcile_plan");
        CollectionAssert.Contains(actual, "dwg_generation_reconcile_apply");
        Assert.IsFalse(McpProbeToolContract.IsExact(actual.Where(name => name != "dwg_generation_reconcile_apply").ToArray()));
        Assert.IsFalse(McpProbeToolContract.IsExact(actual.Append("dwg_unexpected").ToArray()));
    }

    private static T Capture<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        Assert.Fail($"Expected {typeof(T).Name}.");
        throw new InvalidOperationException();
    }
}
