using DwgTranslator.Transport.Core;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CallbackDispatchTests
{
    [TestMethod]
    public async Task CompletesWhenDispatcherBlocksAfterCallback()
    {
        using var release = new ManualResetEventSlim();
        var task = CallbackDispatch.CompleteFromCallbackAsync<int>(callback =>
        {
            callback();
            release.Wait();
        }, () => 42);

        Assert.AreEqual(42, await task.WaitAsync(TimeSpan.FromSeconds(2)));
        release.Set();
    }

    [TestMethod]
    public async Task PropagatesDispatcherExceptionBeforeCallback()
    {
        var task = CallbackDispatch.CompleteFromCallbackAsync<int>(
            _ => throw new InvalidOperationException("synthetic"), () => 42);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task);
    }

    [TestMethod]
    public async Task CancellationCompletesFailClosed()
    {
        using var cancellation = new CancellationTokenSource();
        var task = CallbackDispatch.CompleteFromCallbackAsync<int>(
            _ => Thread.Sleep(Timeout.Infinite), () => 42, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
    }

    [TestMethod]
    public async Task DoubleCallbackUsesFirstCompletionOnly()
    {
        var calls = 0;
        var result = await CallbackDispatch.CompleteFromCallbackAsync<int>(callback =>
        {
            callback();
            callback();
        }, () => Interlocked.Increment(ref calls));

        Assert.AreEqual(1, result);
        Assert.AreEqual(1, calls);
    }
}
