using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

/// <summary>Side effect free JSON primitives shared by durable evidence validators.</summary>
internal static class WorkflowEvidenceJson
{
    internal static bool HasDuplicateJsonProperties(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        return HasDuplicateJsonProperties(document.RootElement);
    }

    private static bool HasDuplicateJsonProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(HasDuplicateJsonProperties);
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name) || HasDuplicateJsonProperties(property.Value)) return true;
        }
        return false;
    }

    internal static string TextHash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static bool ValidOpaque(string value) => !string.IsNullOrWhiteSpace(value) && value.Length is >= 20 and <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    internal static bool HasExactFields(JsonObject value, string[] fields) =>
        value.Count == fields.Length && fields.All(value.ContainsKey);

    internal static bool TryBoolean(JsonObject value, string name, out bool result)
    {
        result = false;
        return value[name] is JsonValue item && item.TryGetValue<bool>(out result);
    }

    internal static bool TryNullableCadDiagnostic(
        JsonObject value,
        string name,
        Func<string, bool> isSupported,
        out string? result)
    {
        result = null;
        if (!value.ContainsKey(name)) return false;
        if (value[name] is null) return true;
        return TryString(value, name, out result!) && isSupported(result);
    }

    internal static string CanonicalHash(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return CanonicalJsonV1.Fingerprint(document.RootElement);
    }

    internal static bool TryString(JsonObject value, string name, out string result)
    {
        result = string.Empty;
        return value[name] is JsonValue item && item.TryGetValue<string>(out result!) && !string.IsNullOrWhiteSpace(result);
    }

    internal static bool TryInt64(JsonObject value, string name, out long result)
    {
        result = 0;
        return value[name] is JsonValue item && item.TryGetValue<long>(out result);
    }

    internal static bool TryGuidD(JsonObject value, string name, out Guid result)
    {
        result = Guid.Empty;
        return TryString(value, name, out var text) && Guid.TryParseExact(text, "D", out result) && result != Guid.Empty;
    }

    internal static bool TrySha256(JsonObject value, string name) =>
        TryString(value, name, out var text) && ContractPatterns.Sha256().IsMatch(text);
}
