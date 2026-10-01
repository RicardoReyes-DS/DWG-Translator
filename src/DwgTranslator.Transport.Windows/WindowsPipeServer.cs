using System.IO.Pipes;
using DwgTranslator.Contracts;
using DwgTranslator.Transport.Core;

namespace DwgTranslator.Transport.Windows;

public sealed class WindowsPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly SessionBinding _sessionBinding;
    private readonly Func<WireEnvelope, CancellationToken, Task<Result<WireEnvelope>>> _handler;
    private readonly EnvelopeReplayRegistry _replays = new();
    private NamedPipeServerStream? _server;

    public WindowsPipeServer(
        string pipeName,
        SessionBinding sessionBinding,
        Func<WireEnvelope, CancellationToken, Task<Result<WireEnvelope>>> handler)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("PIPE_NAME_REQUIRED", nameof(pipeName));
        _pipeName = pipeName;
        _sessionBinding = sessionBinding;
        _handler = handler;
    }

    public async Task ServeSessionAsync(
        TimeSpan connectTimeout,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows named pipes require Windows.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(connectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);

        _server = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using (var connectSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectSource.CancelAfter(connectTimeout);
            await _server.WaitForConnectionAsync(connectSource.Token);
        }

        try
        {
            var first = true;
            while (_server.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                byte[] requestBytes;
                try
                {
                    requestBytes = await LengthPrefixedFrame.ReadAsync(_server, idleTimeout, cancellationToken);
                }
                catch (EndOfStreamException)
                {
                    return;
                }

                var decoded = EnvelopeCodec.Decode(requestBytes);
                if (!decoded.IsSuccess)
                    throw new InvalidDataException(decoded.Error!.Code);
                var request = decoded.Value!;

                if (first)
                {
                    var handshake = _sessionBinding.ValidateInitialHandshake(request);
                    if (!handshake.IsSuccess)
                        throw new InvalidDataException(handshake.Error!.Code);
                    first = false;
                }
                else if (string.Equals(request.MessageType, MessageTypes.CapabilitiesRequest, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("IPC_HANDSHAKE_REPLAY");
                }

                var response = await ProcessAsync(request, cancellationToken);
                TechnicalCheckpoint.Emit("pipe-frame-write-start");
                await LengthPrefixedFrame.WriteAsync(_server, EnvelopeCodec.Encode(response), cancellationToken);
                TechnicalCheckpoint.Emit("pipe-frame-write-complete");
            }
        }
        finally
        {
            if (_server.IsConnected)
                _server.Disconnect();
        }
    }

    private async Task<WireEnvelope> ProcessAsync(WireEnvelope request, CancellationToken cancellationToken)
    {
        if (!CadPipeProtocol.TryResponseType(request.MessageType, out var responseType))
            throw new InvalidDataException("IPC_OPERATION_UNSUPPORTED");

        var fingerprintResult = EnvelopeIdempotency.Fingerprint(request);
        if (!fingerprintResult.IsSuccess)
            return Failure(request, responseType, fingerprintResult.Error!);
        var fingerprint = fingerprintResult.Value!;
        if (!string.Equals(request.IdempotencyKey, fingerprint, StringComparison.Ordinal))
            return Failure(request, responseType, CadPipeProtocol.Error("IDEMPOTENCY_KEY_MISMATCH", ErrorCategory.Contract, "The key does not match canonical request content."));

        var replay = _replays.Replay(request, fingerprint, DateTimeOffset.UtcNow);
        if (!replay.IsSuccess)
            return Failure(request, responseType, replay.Error!);
        if (replay.Value is not null)
            return replay.Value;

        var handled = await _handler(request, cancellationToken);
        var response = handled.IsSuccess
            ? handled.Value!
            : Failure(request, responseType, handled.Error!);
        if (response.JobId != request.JobId || response.CorrelationId != request.CorrelationId ||
            !string.Equals(response.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(response.MessageType, responseType, StringComparison.Ordinal) ||
            !EnvelopeCodec.Validate(response).IsSuccess)
        {
            throw new InvalidDataException("IPC_RESPONSE_IDENTITY_INVALID");
        }

        var stored = _replays.Store(request, fingerprint, response);
        return stored.IsSuccess ? response : Failure(request, responseType, stored.Error!);
    }

    private static WireEnvelope Failure(WireEnvelope request, string responseType, ContractError error) => new()
    {
        SchemaVersion = ContractV1.SchemaVersion,
        MessageType = responseType,
        JobId = request.JobId,
        CorrelationId = request.CorrelationId,
        IdempotencyKey = request.IdempotencyKey,
        SentAtUtc = DateTimeOffset.UtcNow,
        Status = OperationStatus.Failed,
        Error = error
    };

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        _sessionBinding.Dispose();
    }
}
