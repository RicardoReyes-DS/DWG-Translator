using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

internal static class CadSessionRunner
{
    public static async Task<Result<T>> RunAsync<T>(
        ICadGatewaySessionFactory sessions,
        CadSessionPurpose purpose,
        Func<ICadGateway, Task<Result<T>>> operation,
        CancellationToken cancellationToken,
        TimeSpan? exchangeTimeout = null)
    {
        var attempts = purpose == CadSessionPurpose.ReadOnly ? 2 : 1;
        Result<T>? lastFailure = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var opened = exchangeTimeout is { } deadline
                ? await sessions.OpenAsync(purpose, deadline, cancellationToken)
                : await sessions.OpenAsync(purpose, cancellationToken);
            if (!opened.IsSuccess)
            {
                lastFailure = Results.Failure<T>(opened.Error!);
                if (!ShouldRetry(purpose, attempt, attempts, opened.Error!)) return lastFailure;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            await using var lease = opened.Value!;

            Result<T> result;
            try
            {
                result = await operation(lease.Gateway);
            }
            catch
            {
                await lease.CloseAsync(CancellationToken.None);
                throw;
            }

            var closed = await lease.CloseAsync(CancellationToken.None);
            // A non-transport result came from a decoded CAD response and is the authoritative operation
            // outcome, even when the exact child subsequently requires separate termination approval. The
            // host records/fences that cleanup condition through its lifecycle receipt. Replacing a detailed
            // integrity failure here would discard its bounded invariant evidence. A transport failure has no
            // validated response, so cleanup remains authoritative for that case.
            var responseWasValidated = result.IsSuccess || result.Error?.Category != ErrorCategory.Transport;
            var completed = closed.IsSuccess ||
                            (responseWasValidated && closed.Error?.Code == "CAD_PROCESS_EXIT_REQUIRED")
                ? result
                : Results.Failure<T>(closed.Error!);
            if (!completed.IsSuccess && ShouldRetry(purpose, attempt, attempts, completed.Error!))
            {
                lastFailure = completed;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            return completed;
        }
        return lastFailure ?? Results.Failure<T>(new ContractError(
            "CAD_SESSION_RETRY_EXHAUSTED", ErrorCategory.Transport, "The read-only CAD session could not be established.", true));
    }

    private static bool ShouldRetry(CadSessionPurpose purpose, int attempt, int attempts, ContractError error) =>
        purpose == CadSessionPurpose.ReadOnly && attempt < attempts && error.Retryable;
}
