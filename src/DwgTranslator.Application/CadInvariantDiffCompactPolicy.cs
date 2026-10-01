using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

/// <summary>
/// Produces and validates complete, bounded invariant-diff evidence. The full
/// snapshots are compared inside the CAD adapter; only changed rows cross IPC.
/// </summary>
public static class CadInvariantDiffCompactPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Result<CadInvariantDiffResponsePayload> Create(
        string sourceHashAfter,
        string candidateHashAfter,
        IReadOnlyList<CadInvariantDiffEntity> sourceEntities,
        IReadOnlyList<CadInvariantDiffEntity> candidateEntities)
    {
        if (!IsHash(sourceHashAfter) || !IsHash(candidateHashAfter) ||
            sourceEntities is null || candidateEntities is null)
        {
            return Failure("CAD_INVARIANT_DIFF_RESULT_INVALID", "Invariant differential inputs are invalid.");
        }

        var sourceMap = BuildMap(sourceEntities);
        var candidateMap = BuildMap(candidateEntities);
        if (!sourceMap.IsSuccess || !candidateMap.IsSuccess)
        {
            return Failure("CAD_INVARIANT_DIFF_RESULT_INVALID", "An invariant snapshot is invalid or contains duplicate identities.");
        }

        var differences = new List<CadInvariantDiffDifference>();
        var matched = 0;
        var unchanged = 0;
        var added = 0;
        var removed = 0;
        var changed = 0;
        foreach (var key in sourceMap.Value!.Keys.Union(candidateMap.Value!.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            sourceMap.Value.TryGetValue(key, out var source);
            candidateMap.Value.TryGetValue(key, out var candidate);
            if (source is null)
            {
                added++;
                differences.Add(new CadInvariantDiffDifference
                {
                    InvariantKey = key,
                    ChangeKind = "Added",
                    FieldsChanged = ["added"],
                    Candidate = candidate
                });
            }
            else if (candidate is null)
            {
                removed++;
                differences.Add(new CadInvariantDiffDifference
                {
                    InvariantKey = key,
                    ChangeKind = "Removed",
                    FieldsChanged = ["removed"],
                    Source = source
                });
            }
            else
            {
                matched++;
                var fields = Changed(source, candidate);
                if (fields.Count == 0)
                {
                    unchanged++;
                    continue;
                }

                changed++;
                differences.Add(new CadInvariantDiffDifference
                {
                    InvariantKey = key,
                    ChangeKind = "Changed",
                    FieldsChanged = fields,
                    Source = source,
                    Candidate = candidate
                });
            }

            if (differences.Count > CadInvariantDiffContract.MaximumDifferences)
            {
                return Failure("CAD_INVARIANT_DIFF_RESULT_TOO_LARGE", "The complete invariant difference exceeds the bounded evidence contract.", ErrorCategory.Transport);
            }
        }

        var response = new CadInvariantDiffResponsePayload
        {
            Schema = CadInvariantDiffContract.ResponseSchema,
            SourceHashAfter = sourceHashAfter,
            CandidateHashAfter = candidateHashAfter,
            SourceFingerprint = SnapshotFingerprint(sourceEntities),
            CandidateFingerprint = SnapshotFingerprint(candidateEntities),
            SourceEntityCount = sourceEntities.Count,
            CandidateEntityCount = candidateEntities.Count,
            MatchedEntityCount = matched,
            UnchangedEntityCount = unchanged,
            AddedEntityCount = added,
            RemovedEntityCount = removed,
            ChangedEntityCount = changed,
            DifferencesComplete = true,
            Differences = differences,
            ComparisonFingerprint = string.Empty
        };
        response = response with { ComparisonFingerprint = ComparisonFingerprint(response) };

        var validation = Validate(response, sourceHashAfter, candidateHashAfter);
        if (!validation.IsSuccess) return validation;
        if (JsonSerializer.SerializeToUtf8Bytes(response, Json).Length > CadInvariantDiffContract.MaximumSerializedPayloadBytes)
        {
            return Failure("CAD_INVARIANT_DIFF_RESULT_TOO_LARGE", "The complete invariant difference exceeds the bounded evidence contract.", ErrorCategory.Transport);
        }

        return Results.Success(response);
    }

    public static Result<CadInvariantDiffResponsePayload> Validate(
        CadInvariantDiffResponsePayload? value,
        string expectedSourceHash,
        string expectedCandidateHash)
    {
        if (value is null || !string.Equals(value.Schema, CadInvariantDiffContract.ResponseSchema, StringComparison.Ordinal) ||
            !IsHash(expectedSourceHash) || !IsHash(expectedCandidateHash) ||
            !string.Equals(value.SourceHashAfter, expectedSourceHash, StringComparison.Ordinal) ||
            !string.Equals(value.CandidateHashAfter, expectedCandidateHash, StringComparison.Ordinal) ||
            !IsHash(value.SourceFingerprint) || !IsHash(value.CandidateFingerprint) || !IsHash(value.ComparisonFingerprint) ||
            !value.DifferencesComplete || value.Differences is null ||
            value.Differences.Count > CadInvariantDiffContract.MaximumDifferences ||
            value.SourceEntityCount < 0 || value.CandidateEntityCount < 0 || value.MatchedEntityCount < 0 ||
            value.UnchangedEntityCount < 0 || value.AddedEntityCount < 0 || value.RemovedEntityCount < 0 || value.ChangedEntityCount < 0 ||
            value.AddedEntityCount > CadInvariantDiffContract.MaximumDifferences ||
            value.RemovedEntityCount > CadInvariantDiffContract.MaximumDifferences ||
            value.ChangedEntityCount > CadInvariantDiffContract.MaximumDifferences ||
            value.SourceEntityCount != (long)value.MatchedEntityCount + value.RemovedEntityCount ||
            value.CandidateEntityCount != (long)value.MatchedEntityCount + value.AddedEntityCount ||
            value.MatchedEntityCount != (long)value.UnchangedEntityCount + value.ChangedEntityCount ||
            value.Differences.Count != (long)value.AddedEntityCount + value.RemovedEntityCount + value.ChangedEntityCount ||
            JsonSerializer.SerializeToUtf8Bytes(value, Json).Length > CadInvariantDiffContract.MaximumSerializedPayloadBytes)
        {
            return Failure("CAD_INVARIANT_DIFF_RESPONSE_INVALID", "Invariant differential evidence is incomplete or inconsistent.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var removed = 0;
        var changed = 0;
        foreach (var difference in value.Differences)
        {
            if (!ValidateDifference(difference) || !seen.Add(difference.InvariantKey))
            {
                return Failure("CAD_INVARIANT_DIFF_RESPONSE_INVALID", "Invariant differential evidence is incomplete or inconsistent.");
            }

            if (difference.ChangeKind == "Added") added++;
            else if (difference.ChangeKind == "Removed") removed++;
            else changed++;
        }

        if (added != value.AddedEntityCount || removed != value.RemovedEntityCount || changed != value.ChangedEntityCount ||
            !string.Equals(value.ComparisonFingerprint, ComparisonFingerprint(value), StringComparison.Ordinal))
        {
            return Failure("CAD_INVARIANT_DIFF_RESPONSE_INVALID", "Invariant differential evidence failed its completeness fingerprint.");
        }

        return Results.Success(value);
    }

    private static Result<Dictionary<string, CadInvariantDiffEntity>> BuildMap(IReadOnlyList<CadInvariantDiffEntity> entities)
    {
        var result = new Dictionary<string, CadInvariantDiffEntity>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (!ValidateEntity(entity) || !result.TryAdd(Key(entity), entity))
            {
                return Results.Failure<Dictionary<string, CadInvariantDiffEntity>>(new ContractError(
                    "CAD_INVARIANT_DIFF_RESULT_INVALID", ErrorCategory.Integrity, "An invariant snapshot row is invalid.", false));
            }
        }

        return Results.Success(result);
    }

    private static bool ValidateDifference(CadInvariantDiffDifference? value)
    {
        if (value is null || !IsSafe(value.InvariantKey) || value.FieldsChanged is null ||
            value.FieldsChanged.Count is < 1 or > 16 || value.FieldsChanged.Any(field => !IsSafe(field)))
        {
            return false;
        }

        return value.ChangeKind switch
        {
            "Added" => value.Source is null && ValidateEntity(value.Candidate) &&
                value.InvariantKey == Key(value.Candidate!) && value.FieldsChanged.SequenceEqual(["added"], StringComparer.Ordinal),
            "Removed" => value.Candidate is null && ValidateEntity(value.Source) &&
                value.InvariantKey == Key(value.Source!) && value.FieldsChanged.SequenceEqual(["removed"], StringComparer.Ordinal),
            "Changed" => ValidateEntity(value.Source) && ValidateEntity(value.Candidate) &&
                value.InvariantKey == Key(value.Source!) && value.InvariantKey == Key(value.Candidate!) &&
                value.FieldsChanged.SequenceEqual(Changed(value.Source!, value.Candidate!), StringComparer.Ordinal),
            _ => false
        };
    }

    private static bool ValidateEntity(CadInvariantDiffEntity? value) =>
        value is not null && IsSafeHandle(value.OwnerHandle) && IsSafeHandle(value.EntityHandle) &&
        IsSafe(value.DxfType) && IsSafe(value.RuntimeClass) && IsSafe(value.Layer) && IsSafeHandle(value.LinetypeHandle) &&
        (value.Extents is null || (IsSafe(value.Extents.Minimum) && IsSafe(value.Extents.Maximum))) &&
        (value.TextPayloadHash is null || IsHash(value.TextPayloadHash)) && IsHash(value.Fingerprint);

    private static List<string> Changed(CadInvariantDiffEntity source, CadInvariantDiffEntity candidate)
    {
        var result = new List<string>();
        Different(source.OwnerHandle, candidate.OwnerHandle, "ownerHandle", result);
        Different(source.EntityHandle, candidate.EntityHandle, "entityHandle", result);
        Different(source.DxfType, candidate.DxfType, "dxfType", result);
        Different(source.RuntimeClass, candidate.RuntimeClass, "runtimeClass", result);
        if (source.IsTextEntity != candidate.IsTextEntity) result.Add("isTextEntity");
        Different(source.Layer, candidate.Layer, "layer", result);
        if (source.ColorIndex != candidate.ColorIndex) result.Add("colorIndex");
        Different(source.LinetypeHandle, candidate.LinetypeHandle, "linetypeHandle", result);
        if (source.Lineweight != candidate.Lineweight) result.Add("lineweight");
        if (!Equals(source.Extents, candidate.Extents)) result.Add("extents");
        Different(source.TextPayloadHash, candidate.TextPayloadHash, "textPayloadHash", result);
        Different(source.Fingerprint, candidate.Fingerprint, "fingerprint", result);
        return result;
    }

    private static string SnapshotFingerprint(IEnumerable<CadInvariantDiffEntity> entities) =>
        HashText(string.Join('\n', entities.OrderBy(Key, StringComparer.Ordinal).Select(entity => entity.Fingerprint)));

    private static string ComparisonFingerprint(CadInvariantDiffResponsePayload value)
    {
        var material = value with { ComparisonFingerprint = string.Empty };
        return CanonicalJsonV1.Fingerprint(JsonSerializer.SerializeToElement(material, Json));
    }

    private static string Key(CadInvariantDiffEntity value) => value.OwnerHandle + "|" + value.EntityHandle;
    private static void Different(string? left, string? right, string field, List<string> output)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal)) output.Add(field);
    }

    private static bool IsSafeHandle(string? value) => IsSafe(value) && !value!.Contains('|', StringComparison.Ordinal);
    private static bool IsSafe(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CadInvariantDiffContract.MaximumStringLength;
    private static bool IsHash(string? value) => value is not null && ContractPatterns.Sha256().IsMatch(value);
    private static string HashText(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Result<CadInvariantDiffResponsePayload> Failure(string code, string message, ErrorCategory category = ErrorCategory.Integrity) =>
        Results.Failure<CadInvariantDiffResponsePayload>(new ContractError(code, category, message, false));
}
