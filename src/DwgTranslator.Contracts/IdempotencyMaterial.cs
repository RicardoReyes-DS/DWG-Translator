using System.Text.Json;
using System.Text.Json.Nodes;

namespace DwgTranslator.Contracts;

public static class IdempotencyMaterial
{
    public static Result<string> Fingerprint(
        string operation,
        string contractVersion,
        IReadOnlyDictionary<string, string> inputHashes,
        JsonObject parameters)
    {
        if (!MessageTypes.IsSupported(operation) || !string.Equals(contractVersion, ContractV1.SchemaVersion, StringComparison.Ordinal) ||
            inputHashes is null || parameters is null || inputHashes.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !ContractPatterns.Sha256().IsMatch(pair.Value)))
            return Failure("IDEMPOTENCY_MATERIAL_INVALID", "Operation, contract version, hashes and parameters must be valid.");

        var hashes = new JsonObject();
        foreach (var pair in inputHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal)) hashes[pair.Key] = pair.Value;
        var material = new JsonObject
        {
            ["operation"] = operation,
            ["contractVersion"] = contractVersion,
            ["inputHashes"] = hashes,
            ["parameters"] = parameters.DeepClone()
        };
        try
        {
            using var document = JsonDocument.Parse(material.ToJsonString());
            return Results.Success(CanonicalJsonV1.Fingerprint(document.RootElement));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
        {
            return Failure("IDEMPOTENCY_MATERIAL_INVALID", "The canonical material contains an unsupported value.");
        }
    }

    private static Result<string> Failure(string code, string message) =>
        Results.Failure<string>(new ContractError(code, ErrorCategory.Contract, message, false));
}
