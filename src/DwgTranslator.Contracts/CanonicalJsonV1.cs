using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DwgTranslator.Contracts;

public static class CanonicalJsonV1
{
    public static byte[] Serialize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
            Write(element, writer);
        return stream.ToArray();
    }

    public static string Fingerprint(JsonElement element) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Serialize(element))).ToLowerInvariant();

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object: WriteObject(element, writer); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(NormalizeNumber(element), skipInputValidation: false); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidDataException("CANONICAL_JSON_VALUE_INVALID");
        }
    }

    private static void WriteObject(JsonElement element, Utf8JsonWriter writer)
    {
        var properties = element.EnumerateObject().ToArray();
        if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw new InvalidDataException("CANONICAL_JSON_DUPLICATE_PROPERTY");
        writer.WriteStartObject();
        foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            Write(property.Value, writer);
        }
        writer.WriteEndObject();
    }

    private static string NormalizeNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var signed)) return signed.ToString(CultureInfo.InvariantCulture);
        if (element.TryGetUInt64(out var unsigned)) return unsigned.ToString(CultureInfo.InvariantCulture);
        if (!element.TryGetDecimal(out var decimalValue)) throw new InvalidDataException("CANONICAL_JSON_NUMBER_OUT_OF_RANGE");
        if (decimalValue == decimal.Zero) return "0";
        return decimalValue.ToString("G29", CultureInfo.InvariantCulture).Replace("E+", "e", StringComparison.Ordinal).Replace("E", "e", StringComparison.Ordinal);
    }
}
