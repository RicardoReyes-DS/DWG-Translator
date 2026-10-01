using DwgTranslator.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DwgTranslator.Agent.Tests;

[TestClass]
public sealed class CadDeadlinePolicyTests
{
    [TestMethod]
    public void LargeDwgGetsAtLeastThirtyMinutesAndIsBounded()
    {
        var configuration = new AgentBetaConfiguration("dwg-agent-beta-bootstrap/1.0", "AgentBeta", true, true, "c:\\jobs", "c:\\logs",
            CadExchangeSeconds: 600, CadCleanupGraceSeconds: 120, CadLargeDwgThresholdMiB: 5,
            CadExchangeSecondsPerMiB: 10, CadExchangeMaxSeconds: 3600, WorkflowDeadlineSeconds: 3720);
        Assert.IsTrue(CadDeadlinePolicy.ForSourceBytes(configuration, 130L * 1024 * 1024) >= TimeSpan.FromSeconds(1800));
        Assert.AreEqual(TimeSpan.FromSeconds(3600), CadDeadlinePolicy.ForSourceBytes(configuration, 9_999L * 1024 * 1024));
        Assert.IsTrue(configuration.WorkflowDeadlineSeconds >= configuration.CadExchangeMaxSeconds + configuration.CadCleanupGraceSeconds);
    }
}
