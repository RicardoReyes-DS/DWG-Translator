using System.Text.Json;
using DwgTranslator.Agent;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent.Tests;

[TestClass]
public sealed class AgentInvariantDiffServiceTests
{
    private string _root = null!;
    private string _source = null!;
    private string _candidate = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "DwgTranslator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "input")); Directory.CreateDirectory(Path.Combine(_root, "output")); Directory.CreateDirectory(Path.Combine(_root, "workspace")); Directory.CreateDirectory(Path.Combine(_root, "log"));
        _source = Path.Combine(_root, "input", "201.dwg"); _candidate = Path.Combine(_root, "output", "201.candidate.dwg");
        File.WriteAllBytes(_source, [1, 2, 3]); File.WriteAllBytes(_candidate, [4, 5, 6]);
        Directory.CreateDirectory(Path.Combine(_root, "Autodesk", "AutoCAD 2026")); File.WriteAllBytes(Path.Combine(_root, "Autodesk", "AutoCAD 2026", "acad.exe"), [9]);
    }

    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task PlanBindsPairsAndRunPersistsRedactedReadOnlyEvidence()
    {
        var service = new AgentInvariantDiffService(Config(), new FakeExecutor());
        var plan = await service.CreatePlanAsync(new([new(_source, _candidate, "job-201")]), CancellationToken.None);
        Assert.IsTrue(plan.Success);
        var data = plan.Data!.AsObject();
        var run = await service.RunAsync(new(data["planId"]!.GetValue<string>(), data["planHash"]!.GetValue<string>(), data["approvalId"]!.GetValue<string>(), data["consent"]!.GetValue<string>(), "once"), CancellationToken.None);
        Assert.IsTrue(run.Success); Assert.IsTrue(run.Data!["readOnly"]!.GetValue<bool>());
        StringAssert.Contains(run.Data!["pairs"]![0]!["differences"]![0]!["classification"]!.GetValue<string>(), "text_");
        Assert.IsFalse(run.Data!["pairs"]![0]!["differences"]![0]!["safeForAutomaticRetry"]!.GetValue<bool>());
        Assert.IsTrue(run.Data!["pairs"]![0]!["differencesComplete"]!.GetValue<bool>());
        Assert.AreEqual(1, run.Data!["pairs"]![0]!["changedEntityCount"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task RejectsGstarAndReplayAndHashChange()
    {
        var gstar = new AgentInvariantDiffService(Config() with { AutoCadExecutablePath = Path.Combine(_root, "GstarCAD", "acad.exe") }, new FakeExecutor());
        Assert.AreEqual("AGENT_AUTOCAD_CONFIGURATION_INVALID", (await gstar.CreatePlanAsync(new([new(_source, _candidate)]), default)).Error!.Code);
        var service = new AgentInvariantDiffService(Config(), new FakeExecutor()); var plan = await service.CreatePlanAsync(new([new(_source, _candidate)]), default); var data = plan.Data!.AsObject();
        File.AppendAllText(_candidate, "changed");
        var mismatch = await service.RunAsync(new(data["planId"]!.GetValue<string>(), data["planHash"]!.GetValue<string>(), data["approvalId"]!.GetValue<string>(), data["consent"]!.GetValue<string>(), "once"), default);
        Assert.AreEqual("INVARIANT_DIFF_HASH_MISMATCH", mismatch.Error!.Code);
    }

    private AgentBetaConfiguration Config() => new("dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true, Path.Combine(_root, "workspace"), Path.Combine(_root, "log"), AllowedDwgRoot: Path.Combine(_root, "input"), OutputDwgRoot: Path.Combine(_root, "output"), AutoCadExecutablePath: Path.Combine(_root, "Autodesk", "AutoCAD 2026", "acad.exe"), ApprovalLifetimeMinutes: 30);
    private sealed class FakeExecutor : IAgentInvariantDiffExecutor
    {
        public Task<Result<CadInvariantDiffResponsePayload>> CompareAsync(Guid id, string sourcePath, string sourceHash, string candidatePath, string candidateHash, CancellationToken token) =>
            Task.FromResult(CadInvariantDiffCompactPolicy.Create(sourceHash, candidateHash,
                [Row("sha256:" + new string('c', 64), "sha256:" + new string('e', 64))],
                [Row("sha256:" + new string('d', 64), "sha256:" + new string('f', 64))]));
        private static CadInvariantDiffEntity Row(string textHash, string fingerprint) => new() { OwnerHandle = "1", EntityHandle = "2", DxfType = "TEXT", RuntimeClass = "AcDbText", IsTextEntity = true, Layer = "0", ColorIndex = 7, LinetypeHandle = "3", Lineweight = 0, TextPayloadHash = textHash, Fingerprint = fingerprint };
    }
}
