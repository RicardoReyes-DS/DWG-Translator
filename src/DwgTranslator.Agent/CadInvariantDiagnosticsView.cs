using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

/// <summary>
/// Produces an allowlisted view of durable invariant evidence. A job document is
/// evidence, not a trusted API payload, so this prevents historical or malformed
/// records from leaking textual CAD properties through read-only Agent responses.
/// </summary>
internal static class CadInvariantDiagnosticsView
{
    private static readonly string[] RowStrings =
    [
        "entityHandle", "ownerHandle", "dxfType", "runtimeClass", "ownerBlockName", "ownerClass",
        "layer", "linetypeHandle", "invariantRowFingerprint"
    ];

    private static readonly string[] DeltaFields =
    ["minimumX", "minimumY", "minimumZ", "maximumX", "maximumY", "maximumZ"];

    public static JsonNode? Select(JsonElement source) =>
        source.ValueKind == JsonValueKind.Object ? Select(JsonNode.Parse(source.GetRawText())) : null;

    public static JsonNode? Select(JsonNode? source)
    {
        if (source is not JsonObject input || !string.Equals(ReadString(input, "schema"), CadInvariantDiagnosticsLimits.Schema, StringComparison.Ordinal))
            return null;

        var result = new JsonObject
        {
            ["schema"] = CadInvariantDiagnosticsLimits.Schema,
            ["addedCount"] = ReadInt(input, "addedCount"),
            ["removedCount"] = ReadInt(input, "removedCount"),
            ["changedCount"] = ReadInt(input, "changedCount"),
            ["truncated"] = ReadBool(input, "truncated")
        };
        var rows = new JsonArray();
        if (input["rows"] is JsonArray sourceRows)
        {
            foreach (var row in sourceRows.OfType<JsonObject>().Take(CadInvariantDiagnosticsLimits.MaximumRows))
            {
                var selected = SelectDifference(row);
                if (selected is not null) rows.Add(selected);
            }
        }
        result["rows"] = rows;
        return result;
    }

    private static JsonObject? SelectDifference(JsonObject input)
    {
        var key = ReadString(input, "invariantKey");
        var kind = ReadString(input, "changeKind");
        if (key is null || kind is not ("Added" or "Removed" or "Changed")) return null;
        var fields = new JsonArray();
        if (input["fieldsChanged"] is JsonArray sourceFields)
            foreach (var field in sourceFields.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var text) ? Bounded(text) : null).Where(static value => value is not null).Take(CadInvariantDiagnosticsLimits.MaximumFieldsPerRow))
                fields.Add(field);

        return new JsonObject
        {
            ["invariantKey"] = key,
            ["changeKind"] = kind,
            ["fieldsChanged"] = fields,
            ["before"] = SelectRow(input["before"] as JsonObject),
            ["after"] = SelectRow(input["after"] as JsonObject),
            ["extentsDelta"] = SelectDelta(input["extentsDelta"] as JsonObject)
        };
    }

    private static JsonObject? SelectRow(JsonObject? input)
    {
        if (input is null) return null;
        var result = new JsonObject();
        foreach (var field in RowStrings)
            result[field] = ReadString(input, field);
        result["isTargetText"] = ReadBool(input, "isTargetText");
        result["isAnonymousDimensionBlockName"] = ReadBool(input, "isAnonymousDimensionBlockName");
        result["referencedByDimensionCount"] = ReadInt(input, "referencedByDimensionCount");
        result["referencedByNonDimensionCount"] = ReadInt(input, "referencedByNonDimensionCount");
        result["derivedDimensionGraphicsCandidate"] = ReadBool(input, "derivedDimensionGraphicsCandidate");
        result["colorIndex"] = ReadInt(input, "colorIndex");
        result["lineweight"] = ReadInt(input, "lineweight");
        result["extents"] = SelectExtents(input["extents"] as JsonObject);
        return result;
    }

    private static JsonObject? SelectExtents(JsonObject? input) => input is null ? null : new JsonObject
    {
        ["minimum"] = ReadString(input, "minimum"),
        ["maximum"] = ReadString(input, "maximum")
    };

    private static JsonObject? SelectDelta(JsonObject? input)
    {
        if (input is null) return null;
        var result = new JsonObject();
        foreach (var field in DeltaFields) result[field] = ReadString(input, field);
        return result;
    }

    private static string? ReadString(JsonObject input, string property) => input[property] is JsonValue value && value.TryGetValue<string>(out var text)
        ? Bounded(text)
        : null;
    private static int? ReadInt(JsonObject input, string property) => input[property] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    private static bool? ReadBool(JsonObject input, string property) => input[property] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    private static string Bounded(string value) => value.Length <= CadInvariantDiagnosticsLimits.MaximumStringLength
        ? value
        : value[..(CadInvariantDiagnosticsLimits.MaximumStringLength - 1)] + "…";
}
