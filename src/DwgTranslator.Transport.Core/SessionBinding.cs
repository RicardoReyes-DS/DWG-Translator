using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DwgTranslator.Contracts;

namespace DwgTranslator.Transport.Core;

public sealed class SessionBinding : IDisposable
{
    private byte[] _nonce;
    private bool _consumed;

    private SessionBinding(byte[] nonce) => _nonce = nonce;

    public static SessionBinding Create() => new(RandomNumberGenerator.GetBytes(32));

    public static Result<SessionBinding> ImportFromTrustedLauncher(string? encodedNonce)
    {
        if (string.IsNullOrWhiteSpace(encodedNonce))
            return Results.Failure<SessionBinding>(new ContractError("IPC_SESSION_NONCE_MISSING", ErrorCategory.Security, "The launch nonce is required.", false));
        try
        {
            var nonce = Base64UrlDecode(encodedNonce);
            if (nonce.Length == 32)
                return Results.Success(new SessionBinding(nonce));

            CryptographicOperations.ZeroMemory(nonce);
            return Results.Failure<SessionBinding>(new ContractError("IPC_SESSION_NONCE_INVALID", ErrorCategory.Security, "The launch nonce must contain 256 bits.", false));
        }
        catch (FormatException)
        {
            return Results.Failure<SessionBinding>(new ContractError("IPC_SESSION_NONCE_INVALID", ErrorCategory.Security, "The launch nonce is malformed.", false));
        }
    }

    public string ExportForTrustedLauncher() => Base64UrlEncode(_nonce);

    public Result<bool> ValidateInitialHandshake(WireEnvelope envelope)
    {
        if (_consumed)
            return Failure("IPC_HANDSHAKE_REPLAY", "The session handshake was already consumed.");
        var envelopeValidation = EnvelopeCodec.Validate(envelope);
        if (!envelopeValidation.IsSuccess)
            return Failure("IPC_HANDSHAKE_ENVELOPE_INVALID", "The handshake envelope is invalid.");
        if (!string.Equals(envelope.MessageType, MessageTypes.CapabilitiesRequest, StringComparison.Ordinal))
            return Failure("IPC_HANDSHAKE_REQUIRED", "The first message must be the capabilities handshake.");
        if (envelope.Payload is null || envelope.Payload["sessionNonce"] is not JsonValue nonceNode || !nonceNode.TryGetValue<string>(out var supplied))
            return Failure("IPC_SESSION_NONCE_MISSING", "The session nonce is required.");

        byte[] suppliedBytes;
        try
        {
            suppliedBytes = Base64UrlDecode(supplied);
        }
        catch (FormatException)
        {
            return Failure("IPC_SESSION_NONCE_INVALID", "The session nonce is invalid.");
        }

        bool nonceMatches;
        try
        {
            nonceMatches = suppliedBytes.Length == _nonce.Length && CryptographicOperations.FixedTimeEquals(suppliedBytes, _nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedBytes);
        }

        if (!nonceMatches)
            return Failure("IPC_SESSION_NONCE_INVALID", "The session nonce is invalid.");

        _consumed = true;
        CryptographicOperations.ZeroMemory(_nonce);
        return Results.Success(true);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_nonce);
        _nonce = Array.Empty<byte>();
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private static Result<bool> Failure(string code, string message) =>
        Results.Failure<bool>(new ContractError(code, ErrorCategory.Security, message, false));
}
