using DwgTranslator.Application;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadNormalizedBaselineCleanupPolicyTests
{
    [TestMethod]
    public void SuccessfulVerifiedRemovalPasses()
    {
        var deleteCalled = false;
        Assert.IsTrue(CadNormalizedBaselineCleanupPolicy.TryRemove(
            () => deleteCalled = true,
            () => false,
            () => false));
        Assert.IsTrue(deleteCalled);
    }

    [TestMethod]
    public void FileOrDirectoryRemainingFailsClosed()
    {
        Assert.IsFalse(CadNormalizedBaselineCleanupPolicy.TryRemove(() => { }, () => true, () => false));
        Assert.IsFalse(CadNormalizedBaselineCleanupPolicy.TryRemove(() => { }, () => false, () => true));
    }

    [TestMethod]
    public void IoAndAccessFailuresFailClosed()
    {
        Assert.IsFalse(CadNormalizedBaselineCleanupPolicy.TryRemove(
            () => throw new IOException("synthetic"),
            () => false,
            () => false));
        Assert.IsFalse(CadNormalizedBaselineCleanupPolicy.TryRemove(
            () => throw new UnauthorizedAccessException("synthetic"),
            () => false,
            () => false));
    }

    [TestMethod]
    public void UnexpectedFailuresAreNotMisclassifiedAsSuccessfulCleanup()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CadNormalizedBaselineCleanupPolicy.TryRemove(
                () => throw new InvalidOperationException("synthetic"),
                () => false,
                () => false));
    }
}
