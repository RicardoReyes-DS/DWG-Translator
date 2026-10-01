using System.Text.RegularExpressions;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static partial class CadInspectionResultPolicy
{
    private static readonly HashSet<string> Classifications = new(StringComparer.Ordinal)
    {
        "PlannedEntity", "FieldBacked", "SharedBlockDefinition", "ExternalReference",
        "DerivedGeometry", "CustomObject", "Unknown"
    };

    public static Result<CadInspectResponsePayload> Validate(WireEnvelope response, string expectedSourceHash)
    {
        if (response is null)
            return Failure("CAD_INSPECTION_RESPONSE_NULL", ErrorCategory.Contract, "Inspection response is required.");
        if (expectedSourceHash is null || !ContractPatterns.Sha256().IsMatch(expectedSourceHash))
            return Failure("CAD_INSPECTION_EXPECTATION_INVALID", ErrorCategory.Contract, "A valid expected source hash is required.");
        if (response.Status != OperationStatus.Succeeded)
            return response.Error is null
                ? Failure("CAD_INSPECTION_RESPONSE_STATUS_INVALID", ErrorCategory.Contract, "A successful inspection response is required.")
                : Results.Failure<CadInspectResponsePayload>(response.Error);

        var decoded = EnvelopeCodec.DecodePayload<CadInspectResponsePayload>(response, MessageTypes.InspectResponse);
        if (!decoded.IsSuccess)
            return Results.Failure<CadInspectResponsePayload>(decoded.Error!);
        var payload = decoded.Value!;

        if (payload.SourceHash is null || payload.Autocad is null || payload.DrawingFingerprint is null ||
            payload.Inventory is null || payload.Unsupported is null || payload.Warnings is null)
        {
            return Failure("CAD_INSPECTION_PAYLOAD_INVALID", ErrorCategory.Contract, "Inspection payload contains null required values.");
        }
        if (!string.Equals(payload.SourceHash, expectedSourceHash, StringComparison.Ordinal))
            return Failure("SOURCE_CHANGED", ErrorCategory.Integrity, "Inspection source hash differs from the selected file.");
        if (!ContractPatterns.Sha256().IsMatch(payload.DrawingFingerprint))
            return Failure("CAD_DRAWING_FINGERPRINT_INVALID", ErrorCategory.Integrity, "Drawing fingerprint is not a v1 SHA-256 value.");
        if (payload.Autocad.Product != "AutoCAD" || payload.Autocad.Year < 2026 ||
            string.IsNullOrWhiteSpace(payload.Autocad.ApiVersion) || payload.Autocad.ApiVersion.Length > 120)
        {
            return Failure("CAD_HOST_DESCRIPTOR_INVALID", ErrorCategory.Environment, "AutoCAD host descriptor is unsupported.");
        }
        if (payload.SupportedCount < 0 || payload.Inventory.Count > 1_000 ||
            payload.Inventory.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0))
        {
            return Failure("CAD_INVENTORY_INVALID", ErrorCategory.Contract, "Inventory keys and counts must be bounded and non-negative.");
        }

        var inventoriedTextTotal = (long)Value(payload.Inventory, "TEXT") + Value(payload.Inventory, "MTEXT");

        if (payload.Unsupported.Count > 100_000)
            return Failure("CAD_UNSUPPORTED_SUMMARY_INVALID", ErrorCategory.Contract, "Unsupported summary count exceeds the contract limit.");

        var summaries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unsupported in payload.Unsupported)
        {
            if (unsupported is null || unsupported.Classification is null || unsupported.EntityType is null || unsupported.ReasonCode is null ||
                !Classifications.Contains(unsupported.Classification) || string.IsNullOrWhiteSpace(unsupported.EntityType) ||
                unsupported.EntityType.Length > 80 || unsupported.Count < 1 || !ReasonCode().IsMatch(unsupported.ReasonCode) ||
                unsupported.OriginalClassification?.Length > 120)
            {
                return Failure("CAD_UNSUPPORTED_SUMMARY_INVALID", ErrorCategory.Contract, "Unsupported inventory summary is invalid.");
            }

            var key = $"{unsupported.Classification}\u001f{unsupported.EntityType}\u001f{unsupported.ReasonCode}";
            if (!summaries.Add(key))
                return Failure("CAD_UNSUPPORTED_SUMMARY_DUPLICATE", ErrorCategory.Integrity, "Unsupported summaries must be unique.");
        }

        var excludedTextTotal = payload.Unsupported
            .Where(item => item.EntityType is "TEXT" or "MTEXT")
            .Sum(item => (long)item.Count);
        if ((long)payload.SupportedCount + excludedTextTotal != inventoriedTextTotal)
            return Failure("CAD_SUPPORTED_COUNT_INVALID", ErrorCategory.Integrity, "Every inventoried TEXT/MTEXT entity must be supported or explicitly excluded exactly once.");

        if (payload.Warnings.Count > 10_000 || payload.Warnings.Any(warning => warning is null || warning.Length > 500))
            return Failure("CAD_INSPECTION_WARNING_INVALID", ErrorCategory.Contract, "Inspection warnings are outside contract limits.");

        return Results.Success(payload);
    }

    private static int Value(Dictionary<string, int> inventory, string key) =>
        inventory.TryGetValue(key, out var count) ? count : 0;

    [GeneratedRegex("^[A-Z][A-Z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonCode();

    private static Result<CadInspectResponsePayload> Failure(string code, ErrorCategory category, string message) =>
        Results.Failure<CadInspectResponsePayload>(new ContractError(code, category, message, false));
}
