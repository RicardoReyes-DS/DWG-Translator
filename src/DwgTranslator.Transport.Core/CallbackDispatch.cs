namespace DwgTranslator.Transport.Core;

public static class CallbackDispatch
{
    public static async Task<T> CompleteFromCallbackAsync<T>(
        Action<Action> dispatcher,
        Func<T> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(callback);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(), completion);

        _ = Task.Run(() =>
        {
            try
            {
                dispatcher(() =>
                {
                    if (completion.Task.IsCompleted) return;
                    try { completion.TrySetResult(callback()); }
                    catch (OperationCanceledException) { completion.TrySetCanceled(); }
                    catch (Exception exception) { completion.TrySetException(exception); }
                });
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }, CancellationToken.None);

        return await completion.Task.ConfigureAwait(false);
    }
}
