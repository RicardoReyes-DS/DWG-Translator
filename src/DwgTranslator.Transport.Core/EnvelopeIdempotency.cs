using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DwgTranslator.Contracts;

namespace DwgTranslator.Transport.Core;

public static class EnvelopeIdempotency
{
    public static Result<string> Fingerprint(WireEnvelope request)
    {
        var validation = EnvelopeCodec.Validate(request);
        if (!validation.IsSuccess || request.Status is not null)
        {
            return Results.Failure<string>(new ContractError(
                "IDEMPOTENCY_ENVELOPE_INVALID", ErrorCategory.Contract, "A valid request envelope is required.", false));
        }

        var parameters = new JsonObject
        {
            ["jobId"] = request.JobId.ToString("D"),
            ["payload"] = request.Payload!.DeepClone()
        };
        return IdempotencyMaterial.Fingerprint(
            request.MessageType,
            request.SchemaVersion,
            new Dictionary<string, string>(),
            parameters);
    }
}

public sealed class EnvelopeReplayRegistry
{
    private sealed record Entry(
        string Fingerprint,
        string MessageType,
        OperationStatus Status,
        JsonObject? Payload,
        ContractError? Error);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Result<bool> Store(WireEnvelope request, string fingerprint, WireEnvelope response)
    {
        if (!string.Equals(request.IdempotencyKey, fingerprint, StringComparison.Ordinal))
            return Failure<bool>("IDEMPOTENCY_KEY_MISMATCH", "The key must equal the canonical request fingerprint.");
        var responseValidation = EnvelopeCodec.Validate(response);
        if (!responseValidation.IsSuccess || response.Status is null || response.JobId != request.JobId ||
            response.CorrelationId != request.CorrelationId ||
            !string.Equals(response.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
        {
            return Failure<bool>("IDEMPOTENCY_RESPONSE_INVALID", "The response does not match the request identity.");
        }

        var candidate = new Entry(
            fingerprint,
            response.MessageType,
            response.Status.Value,
            response.Payload?.DeepClone().AsObject(),
            response.Error);
        var actual = _entries.GetOrAdd(request.IdempotencyKey, candidate);
        return string.Equals(actual.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? Results.Success(ReferenceEquals(actual, candidate))
            : Failure<bool>("IDEMPOTENCY_CONFLICT", "The key was reused with different canonical content.");
    }

    public Result<WireEnvelope?> Replay(WireEnvelope request, string fingerprint, DateTimeOffset sentAtUtc)
    {
        if (!_entries.TryGetValue(request.IdempotencyKey, out var entry))
            return Results.Success<WireEnvelope?>(null);
        if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            return Failure<WireEnvelope?>("IDEMPOTENCY_CONFLICT", "The key was reused with different canonical content.");

        return Results.Success<WireEnvelope?>(new WireEnvelope
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = entry.MessageType,
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            SentAtUtc = sentAtUtc,
            Status = entry.Status,
            Payload = entry.Payload?.DeepClone().AsObject(),
            Error = entry.Error
        });
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Results.Failure<T>(new ContractError(code, ErrorCategory.Contract, message, false));
}
