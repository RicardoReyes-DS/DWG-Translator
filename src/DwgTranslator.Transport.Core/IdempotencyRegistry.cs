using System.Collections.Concurrent;
using DwgTranslator.Contracts;

namespace DwgTranslator.Transport.Core;

public enum IdempotencyDisposition
{
    Stored,
    Replayed
}

public sealed record IdempotencyResult(IdempotencyDisposition Disposition, byte[] Response);

public sealed class IdempotencyRegistry
{
    private sealed record Entry(string Fingerprint, byte[] Response);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Result<IdempotencyResult> Register(string key, string fingerprint, ReadOnlySpan<byte> response)
    {
        if (!ContractPatterns.Sha256().IsMatch(key) || !ContractPatterns.Sha256().IsMatch(fingerprint) || response.IsEmpty)
            return Failure<IdempotencyResult>("IDEMPOTENCY_INPUT_INVALID", "Key, fingerprint and response are required.");
        if (!string.Equals(key, fingerprint, StringComparison.Ordinal))
            return _entries.ContainsKey(key)
                ? Failure<IdempotencyResult>("IDEMPOTENCY_CONFLICT", "The key was reused with different canonical content.")
                : Failure<IdempotencyResult>("IDEMPOTENCY_KEY_MISMATCH", "The key must equal the canonical request fingerprint.");

        var candidate = new Entry(fingerprint, response.ToArray());
        var actual = _entries.GetOrAdd(key, candidate);
        if (!string.Equals(actual.Fingerprint, fingerprint, StringComparison.Ordinal))
            return Failure<IdempotencyResult>("IDEMPOTENCY_CONFLICT", "The key was reused with different canonical content.");

        var disposition = ReferenceEquals(actual, candidate)
            ? IdempotencyDisposition.Stored
            : IdempotencyDisposition.Replayed;
        return Results.Success(new IdempotencyResult(disposition, actual.Response.ToArray()));
    }

    public Result<byte[]?> Find(string key, string fingerprint)
    {
        if (!ContractPatterns.Sha256().IsMatch(key) || !ContractPatterns.Sha256().IsMatch(fingerprint))
            return Failure<byte[]?>("IDEMPOTENCY_INPUT_INVALID", "Key and fingerprint are required.");
        if (!_entries.TryGetValue(key, out var entry))
            return Results.Success<byte[]?>(null);
        return string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? Results.Success<byte[]?>(entry.Response.ToArray())
            : Failure<byte[]?>("IDEMPOTENCY_CONFLICT", "The key was reused with different canonical content.");
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Results.Failure<T>(new ContractError(code, ErrorCategory.Contract, message, false));
}
