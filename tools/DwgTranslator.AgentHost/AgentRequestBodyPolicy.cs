using System.Buffers;
using System.Text.Json;
using DwgTranslator.Agent;
using Microsoft.AspNetCore.Http;

namespace DwgTranslator.AgentHost;

internal enum AgentRequestBodyReadStatus
{
    Success,
    Invalid,
    TooLarge
}

internal sealed record AgentRequestBodyReadResult<T>(AgentRequestBodyReadStatus Status, T? Request)
    where T : class;

internal sealed record AgentRequestDispatchResult<T>(AgentRequestBodyReadStatus Status, T? Response)
    where T : class;

internal static class AgentRequestBodyPolicy
{
    internal const int DefaultMaxBytes = 64 * 1024;
    internal const int TranslationReviewApplyMaxBytes = 1024 * 1024;
    internal const int TooLargeStatusCode = StatusCodes.Status413PayloadTooLarge;
    internal const string TooLargeErrorCode = "AGENT_REQUEST_TOO_LARGE";
    internal const string TooLargeErrorMessage = "The Agent Host request exceeded the allowed size.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static int MaxBytesFor(string operation) =>
        string.Equals(operation, AgentHostOperations.TranslationReviewApply, StringComparison.Ordinal)
            ? TranslationReviewApplyMaxBytes
            : DefaultMaxBytes;

    internal static async Task<AgentRequestBodyReadResult<T>> ReadJsonAsync<T>(
        HttpRequest request,
        string operation,
        CancellationToken cancellationToken,
        ArrayPool<byte>? bufferPool = null)
        where T : class
    {
        var maxBytes = MaxBytesFor(operation);
        if (request.ContentLength is { } contentLength && contentLength > maxBytes)
            return new(AgentRequestBodyReadStatus.TooLarge, null);

        var pool = bufferPool ?? ArrayPool<byte>.Shared;
        var buffer = pool.Rent(maxBytes + 1);
        var bytesRead = 0;
        try
        {
            while (bytesRead <= maxBytes)
            {
                var read = await request.Body.ReadAsync(
                    buffer.AsMemory(bytesRead, maxBytes + 1 - bytesRead), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                bytesRead += read;
            }

            if (bytesRead > maxBytes)
                return new(AgentRequestBodyReadStatus.TooLarge, null);
            if (!request.HasJsonContentType())
                return new(AgentRequestBodyReadStatus.Invalid, null);

            try
            {
                var value = JsonSerializer.Deserialize<T>(buffer.AsSpan(0, bytesRead), Json);
                return value is null
                    ? new(AgentRequestBodyReadStatus.Invalid, null)
                    : new(AgentRequestBodyReadStatus.Success, value);
            }
            catch (JsonException)
            {
                return new(AgentRequestBodyReadStatus.Invalid, null);
            }
        }
        finally
        {
            Array.Clear(buffer);
            pool.Return(buffer);
        }
    }

    internal static async Task<AgentRequestDispatchResult<TResponse>> ReadAndDispatchAsync<TRequest, TResponse>(
        HttpRequest request,
        string operation,
        Func<TRequest, Task<TResponse>> action,
        CancellationToken cancellationToken,
        ArrayPool<byte>? bufferPool = null)
        where TRequest : class
        where TResponse : class
    {
        var requestRead = await ReadJsonAsync<TRequest>(
            request, operation, cancellationToken, bufferPool).ConfigureAwait(false);
        if (requestRead.Status != AgentRequestBodyReadStatus.Success || requestRead.Request is null)
            return new(requestRead.Status, null);

        return new(AgentRequestBodyReadStatus.Success,
            await action(requestRead.Request).ConfigureAwait(false));
    }
}
