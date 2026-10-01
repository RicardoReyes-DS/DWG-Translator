using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

public sealed record AgentInvariantDiffPair(string SourcePath, string CandidatePath, string? JobId = null);
public sealed record AgentInvariantDiffPlanRequest(IReadOnlyList<AgentInvariantDiffPair>? Pairs);
public sealed record AgentInvariantDiffRunRequest(string PlanId, string PlanHash, string ApprovalId, string Consent, string IdempotencyKey);

public interface IAgentInvariantDiffExecutor
{
    Task<Result<CadInvariantDiffResponsePayload>> CompareAsync(Guid operationId, string sourcePath, string sourceHash,
        string candidatePath, string candidateHash, CancellationToken cancellationToken);
}

public sealed class CadInvariantDiffAgentExecutor(CadInvariantDiffWorkflow workflow) : IAgentInvariantDiffExecutor
{
    public Task<Result<CadInvariantDiffResponsePayload>> CompareAsync(Guid operationId, string sourcePath, string sourceHash,
        string candidatePath, string candidateHash, CancellationToken cancellationToken) =>
        workflow.CompareAsync(operationId, sourcePath, sourceHash, candidatePath, candidateHash, cancellationToken);
}

/// <summary>Approval-bound, read-only source/candidate diagnostic. It has no path to promotion or translation.</summary>
public sealed class AgentInvariantDiffService
{
    public const string ContractVersion = "invariant-diff/1.1";
    private const int MaximumPairs = 3;
    private const long MaximumPairBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly AgentBetaConfiguration _configuration;
    private readonly IAgentInvariantDiffExecutor _executor;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _plans;
    private readonly string _evidence;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public AgentInvariantDiffService(AgentBetaConfiguration configuration, IAgentInvariantDiffExecutor executor, Func<DateTimeOffset>? now = null)
    {
        _configuration = configuration;
        _executor = executor;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _plans = Path.Combine(configuration.WorkspaceRoot, "invariant-diff-plans");
        _evidence = Path.Combine(configuration.WorkspaceRoot, "invariant-diff-evidence");
    }

    public async Task<AgentEnvelope> CreatePlanAsync(AgentInvariantDiffPlanRequest request, CancellationToken cancellationToken)
    {
        const string command = "invariant diff plan";
        if (!_configuration.ExecutionEnabled) return Fail(command, "AGENT_CAD_EXECUTION_DISABLED", "CAD execution is disabled.");
        if (!ValidAutoCad()) return Fail(command, "AGENT_AUTOCAD_CONFIGURATION_INVALID", "Only the configured AutoCAD 2026 executable is accepted.");
        if (request.Pairs is null || request.Pairs.Count is < 1 or > MaximumPairs) return Fail(command, "INVARIANT_DIFF_PAIR_COUNT_INVALID", "One to three unique DWG pairs are required.");
        var pairs = new List<PlanPair>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Pairs)
        {
            var source = Validate(item.SourcePath, _configuration.AllowedDwgRoot, "SOURCE");
            var candidate = Validate(item.CandidatePath, _configuration.OutputDwgRoot, "CANDIDATE");
            if (source.Error is not null) return Fail(command, source.Error.Code, source.Error.Message);
            if (candidate.Error is not null) return Fail(command, candidate.Error.Code, candidate.Error.Message);
            if (PathsEqual(source.Path!, candidate.Path!) || !seen.Add(source.Path!) || !seen.Add(candidate.Path!))
                return Fail(command, "INVARIANT_DIFF_PATH_COLLISION", "Each source and candidate must be distinct.");
            var sourceSnapshot = await SnapshotAsync(source.Path!, cancellationToken).ConfigureAwait(false);
            var candidateSnapshot = await SnapshotAsync(candidate.Path!, cancellationToken).ConfigureAwait(false);
            if (sourceSnapshot.Error is not null || candidateSnapshot.Error is not null)
                return Fail(command, sourceSnapshot.Error?.Code ?? candidateSnapshot.Error!.Code, "A DWG could not be hashed.");
            if (sourceSnapshot.Bytes > MaximumPairBytes || candidateSnapshot.Bytes > MaximumPairBytes)
                return Fail(command, "INVARIANT_DIFF_SIZE_LIMIT", "A DWG exceeds the configured read-only diagnostic limit.");
            pairs.Add(new(source.Path!, sourceSnapshot.Hash!, sourceSnapshot.Bytes, candidate.Path!, candidateSnapshot.Hash!, candidateSnapshot.Bytes, item.JobId));
        }
        var executable = await SnapshotAsync(_configuration.AutoCadExecutablePath, cancellationToken).ConfigureAwait(false);
        if (executable.Error is not null) return Fail(command, executable.Error.Code, "AutoCAD executable could not be hashed.");
        var created = _now();
        var planId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var material = new PlanArtifact(ContractVersion, planId, created, created.AddMinutes(_configuration.ApprovalLifetimeMinutes), pairs,
            _configuration.AutoCadExecutablePath, executable.Hash!, executable.Bytes, string.Empty, string.Empty, string.Empty);
        var planHash = HashJson(material with { PlanHash = string.Empty, ApprovalId = string.Empty, Consent = string.Empty });
        var approvalId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var consent = $"I_APPROVE_AUTOCAD_READ_ONLY_INVARIANT_DIFF:{planId}:{planHash}";
        var artifact = material with { PlanHash = planHash, ApprovalId = approvalId, Consent = consent };
        Directory.CreateDirectory(_plans);
        var path = Path.Combine(_plans, planId + ".json");
        await WriteAtomicAsync(path, artifact, cancellationToken).ConfigureAwait(false);
        return Ok(command, JsonSerializer.SerializeToNode(new
        {
            contractVersion = ContractVersion,
            planId,
            planHash,
            expiresAtUtc = artifact.ExpiresAtUtc,
            approvalId,
            consent,
            autoCadExecutablePath = artifact.AutoCadExecutablePath,
            autoCadExecutableHash = artifact.AutoCadExecutableHash,
            pairs = artifact.Pairs.Select(pair => new { pair.SourcePath, pair.SourceHash, pair.CandidatePath, pair.CandidateHash, pair.JobId }),
            opensAutoCad = false,
            readOnly = true
        }, Json)!);
    }

    public async Task<AgentEnvelope> RunAsync(AgentInvariantDiffRunRequest request, CancellationToken cancellationToken)
    {
        const string command = "invariant diff run";
        if (!_configuration.ExecutionEnabled) return Fail(command, "AGENT_CAD_EXECUTION_DISABLED", "CAD execution is disabled.");
        if (!ValidAutoCad()) return Fail(command, "AGENT_AUTOCAD_CONFIGURATION_INVALID", "Only the configured AutoCAD 2026 executable is accepted.");
        if (string.IsNullOrWhiteSpace(request.PlanId) || string.IsNullOrWhiteSpace(request.IdempotencyKey)) return Fail(command, "INVARIANT_DIFF_REQUEST_INVALID", "A plan and idempotency key are required.");
        var artifact = await ReadPlanAsync(request.PlanId, cancellationToken).ConfigureAwait(false);
        if (artifact is null) return Fail(command, "INVARIANT_DIFF_PLAN_NOT_FOUND", "The invariant differential plan was not found.");
        if (!string.Equals(artifact.ContractVersion, ContractVersion, StringComparison.Ordinal)) return Fail(command, "INVARIANT_DIFF_PLAN_VERSION_UNSUPPORTED", "The invariant differential plan belongs to an unsupported contract version.");
        if (_now() >= artifact.ExpiresAtUtc) return Fail(command, "APPROVAL_EXPIRED", "The invariant differential plan expired.");
        if (!string.Equals(request.PlanHash, artifact.PlanHash, StringComparison.Ordinal) || !string.Equals(request.ApprovalId, artifact.ApprovalId, StringComparison.Ordinal) ||
            !string.Equals(request.Consent, artifact.Consent, StringComparison.Ordinal)) return Fail(command, "APPROVAL_REQUIRED", "A current plan-bound read-only approval is required.");
        if (!await ClaimAsync(request.PlanId, request.IdempotencyKey, cancellationToken).ConfigureAwait(false)) return Fail(command, "APPROVAL_REPLAYED", "This invariant differential plan was already consumed.");
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Fail(command, "AGENT_CAD_BUSY", "Another AutoCAD operation is active.");
        try
        {
            var exe = await SnapshotAsync(_configuration.AutoCadExecutablePath, cancellationToken).ConfigureAwait(false);
            if (exe.Error is not null || !string.Equals(exe.Hash, artifact.AutoCadExecutableHash, StringComparison.Ordinal)) return Fail(command, "AUTOCAD_EXECUTABLE_CHANGED", "Configured AutoCAD changed after planning.");
            var results = new List<object>();
            foreach (var pair in artifact.Pairs)
            {
                var source = await SnapshotAsync(pair.SourcePath, cancellationToken).ConfigureAwait(false);
                var candidate = await SnapshotAsync(pair.CandidatePath, cancellationToken).ConfigureAwait(false);
                if (source.Error is not null || candidate.Error is not null || !string.Equals(source.Hash, pair.SourceHash, StringComparison.Ordinal) || !string.Equals(candidate.Hash, pair.CandidateHash, StringComparison.Ordinal))
                    return Fail(command, "INVARIANT_DIFF_HASH_MISMATCH", "A planned source or candidate changed before execution.");
                var operationId = Guid.NewGuid();
                var compared = await _executor.CompareAsync(operationId, pair.SourcePath, pair.SourceHash, pair.CandidatePath, pair.CandidateHash, cancellationToken).ConfigureAwait(false);
                if (!compared.IsSuccess) return Fail(command, compared.Error!.Code, compared.Error.Message);
                var validated = CadInvariantDiffCompactPolicy.Validate(compared.Value, pair.SourceHash, pair.CandidateHash);
                if (!validated.IsSuccess) return Fail(command, validated.Error!.Code, validated.Error.Message);
                var afterSource = await SnapshotAsync(pair.SourcePath, cancellationToken).ConfigureAwait(false);
                var afterCandidate = await SnapshotAsync(pair.CandidatePath, cancellationToken).ConfigureAwait(false);
                if (afterSource.Error is not null || afterCandidate.Error is not null || !string.Equals(afterSource.Hash, pair.SourceHash, StringComparison.Ordinal) || !string.Equals(afterCandidate.Hash, pair.CandidateHash, StringComparison.Ordinal))
                    return Fail(command, "INVARIANT_DIFF_FILE_CHANGED", "A DWG changed during read-only diagnosis.");
                var comparedValue = compared.Value!;
                var diff = Classify(comparedValue);
                results.Add(new
                {
                    pair.JobId,
                    operationId,
                    pair.SourcePath,
                    pair.SourceHash,
                    pair.CandidatePath,
                    pair.CandidateHash,
                    sourceFingerprint = comparedValue.SourceFingerprint,
                    candidateFingerprint = comparedValue.CandidateFingerprint,
                    comparedValue.SourceEntityCount,
                    comparedValue.CandidateEntityCount,
                    comparedValue.MatchedEntityCount,
                    comparedValue.UnchangedEntityCount,
                    comparedValue.AddedEntityCount,
                    comparedValue.RemovedEntityCount,
                    comparedValue.ChangedEntityCount,
                    comparedValue.DifferencesComplete,
                    comparedValue.ComparisonFingerprint,
                    differences = diff,
                    sourceUnchanged = true,
                    candidateUnchanged = true
                });
            }
            var evidence = new { contractVersion = ContractVersion, planId = request.PlanId, planHash = artifact.PlanHash, completedAtUtc = _now(), pairs = results };
            Directory.CreateDirectory(_evidence);
            await WriteAtomicAsync(Path.Combine(_evidence, request.PlanId + ".json"), evidence, cancellationToken).ConfigureAwait(false);
            return Ok(command, JsonSerializer.SerializeToNode(new { contractVersion = ContractVersion, planId = request.PlanId, evidencePath = Path.Combine(_evidence, request.PlanId + ".json"), pairs = results, readOnly = true }, Json)!);
        }
        finally { Gate.Release(); }
    }

    private static object[] Classify(CadInvariantDiffResponsePayload value)
    {
        return value.Differences.Select(difference =>
        {
            var kind = difference.Source is null || difference.Candidate is null
                ? "missing/added entity"
                : Classification(difference.Source, difference.Candidate, difference.FieldsChanged.ToArray());
            return new { invariantKey = difference.InvariantKey, classification = kind, fieldsChanged = difference.FieldsChanged, safeForAutomaticRetry = false };
        }).Cast<object>().ToArray();
    }

    private static string Classification(CadInvariantDiffEntity left, CadInvariantDiffEntity right, string[] fields)
    {
        if (left.IsTextEntity && right.IsTextEntity && fields.All(field => field == "textPayloadHash")) return "text_payload";
        if (left.IsTextEntity && right.IsTextEntity && fields.All(field => field is "textPayloadHash" or "extents" or "fingerprint")) return "text_derived_geometry";
        if (fields.Any(field => field is "extents" or "dxfType" or "runtimeClass" or "ownerHandle")) return "non_text_geometry";
        return fields.Length == 0 ? "unknown" : "metadata/system";
    }

    private bool ValidAutoCad() => Path.GetFileName(_configuration.AutoCadExecutablePath).Equals("acad.exe", StringComparison.OrdinalIgnoreCase) &&
        _configuration.AutoCadExecutablePath.Contains("Autodesk" + Path.DirectorySeparatorChar + "AutoCAD 2026", StringComparison.OrdinalIgnoreCase) &&
        !_configuration.AutoCadExecutablePath.Contains("gstar", StringComparison.OrdinalIgnoreCase);
    private static (string? Path, AgentError? Error) Validate(string path, string root, string role)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root)) return (null, new("INVARIANT_DIFF_PATH_INVALID", "An absolute DWG path is required."));
        var canonical = Path.GetFullPath(path); var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!canonical.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(canonical) || !Path.GetExtension(canonical).Equals(".dwg", StringComparison.OrdinalIgnoreCase)) return (null, new($"INVARIANT_DIFF_{role}_FORBIDDEN", "The DWG is outside its configured allowlist."));
        if ((File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0) return (null, new("INVARIANT_DIFF_REPARSE_FORBIDDEN", "Reparse points are not accepted."));
        return (canonical, null);
    }
    private static async Task<FileSnapshot> SnapshotAsync(string path, CancellationToken token) { try { var info = new FileInfo(path); await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false); return new("sha256:" + Convert.ToHexString(hash).ToLowerInvariant(), info.Length, null); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return new(null, 0, new("INVARIANT_DIFF_FILE_UNREADABLE", "A planned file could not be read.")); } }
    private async Task<PlanArtifact?> ReadPlanAsync(string id, CancellationToken token) { var path = Path.Combine(_plans, id + ".json"); if (!File.Exists(path)) return null; await using var stream = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<PlanArtifact>(stream, Json, token).ConfigureAwait(false); }
    private async Task<bool> ClaimAsync(string id, string key, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(_plans);
            await using var stream = new FileStream(Path.Combine(_plans, id + ".consumed"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 128, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(HashText(key).AsMemory(), token).ConfigureAwait(false);
            return true;
        }
        catch (IOException) { return false; }
    }
    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken token) { var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N"); await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json), token).ConfigureAwait(false); File.Move(temporary, path, false); }
    private static string HashJson(PlanArtifact value) => HashText(JsonSerializer.Serialize(value, Json));
    private static string HashText(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private AgentEnvelope Ok(string command, JsonNode data) => new(AgentQueryService.ResponseSchema, true, command, _now(), data, null);
    private AgentEnvelope Fail(string command, string code, string message) => new(AgentQueryService.ResponseSchema, false, command, _now(), null, new(code, message));
    private sealed record FileSnapshot(string? Hash, long Bytes, AgentError? Error);
    private sealed record PlanPair(string SourcePath, string SourceHash, long SourceBytes, string CandidatePath, string CandidateHash, long CandidateBytes, string? JobId);
    private sealed record PlanArtifact(string ContractVersion, string PlanId, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, List<PlanPair> Pairs, string AutoCadExecutablePath, string AutoCadExecutableHash, long AutoCadExecutableBytes, string PlanHash, string ApprovalId, string Consent);
}
