using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static class WriteSafetyPolicy
{
    public static Result<bool> Validate(WireEnvelope envelope)
    {
        var envelopeResult = EnvelopeCodec.Validate(envelope);
        if (!envelopeResult.IsSuccess)
            return Results.Failure<bool>(envelopeResult.Error!);
        if (!string.Equals(envelope.MessageType, MessageTypes.WriteRequest, StringComparison.Ordinal))
            return Fail("WRITE_OPERATION_REQUIRED", "Expected a CAD write request.");

        try
        {
            var payload = envelope.Payload!;
            var source = NormalizeWindowsDwgPath(RequiredString(payload, "sourcePath"));
            var candidate = NormalizeWindowsDwgPath(RequiredString(payload, "candidatePath"));
            var final = NormalizeWindowsDwgPath(RequiredString(payload, "finalPath"));
            if (source == candidate || source == final || candidate == final)
                return Fail("INPUT_OUTPUT_SAME", "Source, candidate and final paths must be distinct.");
            if (!string.Equals(Volume(candidate), Volume(final), StringComparison.OrdinalIgnoreCase))
                return Fail("CROSS_VOLUME_NOT_ATOMIC", "Candidate and final output must share a volume.");
            if (!string.Equals(RequiredString(payload, "overwritePolicy"), "FailIfExists", StringComparison.Ordinal))
                return Fail("OVERWRITE_POLICY_UNSAFE", "MVP requires FailIfExists.");
            if (!ContractPatterns.Sha256().IsMatch(RequiredString(payload, "expectedSourceHash")))
                return Fail("SOURCE_HASH_INVALID", "A valid expected source hash is required.");
            if (!string.Equals(RequiredString(payload, "validationPolicy"), "VisualStrictV2", StringComparison.Ordinal))
                return Fail("WRITE_VALIDATION_POLICY_INVALID", "Real drawing exercises require visual strict v2 validation.");

            var coverage = payload["approvalCoverage"]?.AsObject() ?? throw new InvalidDataException();
            var selected = RequiredInt(coverage, "selected");
            var approved = RequiredInt(coverage, "approved");
            var excluded = RequiredInt(coverage, "excluded");
            var complete = coverage["complete"]?.GetValue<bool>() ?? false;
            var mappings = payload["mappings"]?.AsArray() ?? throw new InvalidDataException();
            if (!complete || selected <= 0 || approved <= 0 || excluded < 0 || selected != approved + excluded || mappings.Count != approved || mappings.Count > 100_000)
                return Fail("APPROVAL_COVERAGE_INVALID", "Approval coverage must be complete and match mappings.");

            var segmentIds = new HashSet<string>(StringComparer.Ordinal);
            var handles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mappingNode in mappings)
            {
                var mapping = mappingNode?.AsObject() ?? throw new InvalidDataException();
                var segmentId = RequiredString(mapping, "segmentId");
                var handle = RequiredString(mapping, "handle");
                if (!ContractPatterns.SegmentId().IsMatch(segmentId))
                    return Fail("WRITE_SEGMENT_ID_INVALID", "A deterministic v1 segment ID is required.");
                if (handle.Length is < 1 or > 32 || handle.Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
                    return Fail("WRITE_HANDLE_INVALID", "A bounded uppercase hexadecimal entity handle is required.");
                if (!segmentIds.Add(segmentId) || !handles.Add(handle))
                    return Fail("WRITE_MAPPING_DUPLICATE", "Mappings require unique segment IDs and entity handles.");
                var finalTextHash = RequiredString(mapping, "approvedFinalTextHash");
                if (!ContractPatterns.Sha256().IsMatch(RequiredString(mapping, "expectedSourceTextHash")) ||
                    !ContractPatterns.Sha256().IsMatch(finalTextHash))
                    return Fail("WRITE_MAPPING_HASH_INVALID", "Mapping hashes must be valid SHA-256 values.");
                var approvedText = RequiredString(mapping, "approvedFinalText");
                if (string.IsNullOrEmpty(approvedText) || approvedText.Length > 65_535)
                    return Fail("WRITE_MAPPING_TEXT_REQUIRED", "Approved final text is required.");
                var computedHash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(approvedText))).ToLowerInvariant();
                if (!string.Equals(finalTextHash, computedHash, StringComparison.Ordinal))
                    return Fail("WRITE_MAPPING_HASH_MISMATCH", "Approved final text must match its content hash.");
                var entityType = RequiredString(mapping, "expectedEntityType");
                var space = RequiredString(mapping, "expectedSpace");
                var layer = RequiredString(mapping, "expectedLayer");
                if (entityType is not ("TEXT" or "MTEXT") || space is not ("ModelSpace" or "PaperSpace") ||
                    string.IsNullOrWhiteSpace(layer) || layer.Length > 255)
                    return Fail("WRITE_MAPPING_VISUAL_SCOPE_INVALID", "Mapping visual identity fields are invalid.");
                var layout = mapping["expectedLayout"];
                if ((space == "ModelSpace" && layout is not null) ||
                    (space == "PaperSpace" && (layout is null || string.IsNullOrWhiteSpace(layout.GetValue<string>()))))
                    return Fail("WRITE_MAPPING_VISUAL_SCOPE_INVALID", "Mapping layout must match its drawing space.");
            }

            return Results.Success(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or FormatException)
        {
            return Fail("WRITE_PAYLOAD_INVALID", "Write payload fields are missing or invalid.");
        }
    }

    private static string RequiredString(JsonObject value, string name) =>
        value[name]?.GetValue<string>() ?? throw new InvalidDataException();

    private static int RequiredInt(JsonObject value, string name) =>
        value[name]?.GetValue<int>() ?? throw new InvalidDataException();

    private static string NormalizeWindowsDwgPath(string value)
    {
        var normalized = value.Replace('/', '\\').Trim();
        if (normalized.Length is < 1 or > 1024 || !IsAbsoluteWindowsPath(normalized) || normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..") ||
            !normalized.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        return normalized.TrimEnd('\\').ToUpperInvariant();
    }

    private static string Volume(string path)
    {
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            return path[..2];
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"\\\\{parts[0]}\\{parts[1]}" : throw new InvalidDataException();
    }

    private static bool IsAbsoluteWindowsPath(string path)
    {
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            return true;
        return path.StartsWith("\\\\", StringComparison.Ordinal) && path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    }

    private static Result<bool> Fail(string code, string message) =>
        Results.Failure<bool>(new ContractError(code, ErrorCategory.Security, message, false));

}
