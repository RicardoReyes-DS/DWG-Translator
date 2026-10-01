using DwgTranslator.Agent;

namespace DwgTranslator.Agent.Tests;

[TestClass]
public sealed class HistoricalIncidentRecoveryPolicyTests
{
    [TestMethod]
    public void HistoricalRuntimeAdaptersAreUnavailableInSourceCandidate()
    {
        Assert.IsFalse(HistoricalIncidentRecoveryPolicy.TryGetRecovery25GenerationWriteAdapter(out var first));
        Assert.AreEqual(string.Empty, first);
        Assert.IsFalse(HistoricalIncidentRecoveryPolicy.TryGetFresh600GenerationWriteAdapter(out var second));
        Assert.AreEqual(string.Empty, second);
    }

    [TestMethod]
    public void BootstrapIncidentCannotBeReconciled()
    {
        Assert.IsFalse(CadBootstrapTrustFailureReconciliationPolicy.Instance.IsExactFailure(
            Guid.NewGuid(), 8, "CAD_BOOTSTRAP_TRUST_RESTORE_TIMEOUT", "Security",
            true, false, "Writing", null, null));
    }
}
