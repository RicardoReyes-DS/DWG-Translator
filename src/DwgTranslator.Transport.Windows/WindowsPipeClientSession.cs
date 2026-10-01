using System.IO.Pipes;
using System.Text;
using DwgTranslator.Contracts;
using DwgTranslator.Transport.Core;

namespace DwgTranslator.Transport.Windows;

public sealed class WindowsPipeClientSession : IAsyncDisposable
{
    private readonly NamedPipeClientStream _client;
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);

    private WindowsPipeClientSession(NamedPipeClientStream client) => _client = client;

    public static async Task<Result<WindowsPipeClientSession>> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Failure<WindowsPipeClientSession>("IPC_PLATFORM_UNSUPPORTED", "Windows named pipes require Windows.");
        if (string.IsNullOrWhiteSpace(pipeName) || timeout <= TimeSpan.Zero)
            return Failure<WindowsPipeClientSession>("IPC_CONNECT_INPUT_INVALID", "Pipe name and positive timeout are required.");

        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(timeoutSource.Token);
            return Results.Success(new WindowsPipeClientSession(client));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await client.DisposeAsync();
            return Failure<WindowsPipeClientSession>("IPC_TIMEOUT", "Timed out connecting to the local CAD pipe.", true);
        }
        catch (IOException)
        {
            await client.DisposeAsync();
            return Failure<WindowsPipeClientSession>("IPC_CONNECT_FAILED", "Could not connect to the local CAD pipe.", true);
        }
        catch (UnauthorizedAccessException)
        {
            await client.DisposeAsync();
            return Failure<WindowsPipeClientSession>("IPC_ACCESS_DENIED", "The current Windows user cannot access the local CAD pipe.");
        }
    }

    public async Task<Result<WireEnvelope>> ExchangeAsync(
        WireEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            return Failure<WireEnvelope>("IPC_TIMEOUT_INVALID", "A positive exchange timeout is required.");

        var validation = EnvelopeCodec.Validate(request);
        if (!validation.IsSuccess)
            return Results.Failure<WireEnvelope>(validation.Error!);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var lockAcquired = false;
        try
        {
            await _exchangeLock.WaitAsync(timeoutSource.Token);
            lockAcquired = true;
            await LengthPrefixedFrame.WriteAsync(_client, EnvelopeCodec.Encode(request), timeoutSource.Token);
            var bytes = await LengthPrefixedFrame.ReadAsync(_client, timeout, timeoutSource.Token);
            var response = EnvelopeCodec.Decode(bytes);
            if (!response.IsSuccess)
                return response;
            if (response.Value!.JobId != request.JobId || response.Value.CorrelationId != request.CorrelationId ||
                !string.Equals(response.Value.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
            {
                return Failure<WireEnvelope>("IPC_CORRELATION_MISMATCH", "Response identity differs from the request.");
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure<WireEnvelope>("IPC_TIMEOUT", "The local CAD exchange timed out.", true);
        }
        catch (EndOfStreamException)
        {
            return Failure<WireEnvelope>("IPC_PEER_DISCONNECTED", "The CAD pipe closed before a response was received.", true);
        }
        catch (IOException)
        {
            return Failure<WireEnvelope>("IPC_IO", "The local CAD exchange failed.", true);
        }
        catch (InvalidDataException)
        {
            return Failure<WireEnvelope>("IPC_PROTOCOL_INVALID", "The CAD pipe returned an invalid protocol frame.");
        }
        catch (DecoderFallbackException)
        {
            return Failure<WireEnvelope>("IPC_PROTOCOL_INVALID", "The CAD pipe returned invalid UTF-8 data.");
        }
        finally
        {
            if (lockAcquired)
                _exchangeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _exchangeLock.Dispose();
    }

    private static Result<T> Failure<T>(string code, string message, bool retryable = false) =>
        Results.Failure<T>(CadPipeProtocol.Error(code, ErrorCategory.Transport, message, retryable));
}
