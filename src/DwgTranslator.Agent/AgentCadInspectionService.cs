using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

public sealed record AgentCadInspectionRequest(
    string SourcePath,
    string ExpectedSourceHash,
    string Approval);

public sealed record AgentCadInspectionPlanRequest(string SourcePath);

public interface IAgentCadReadExecutor
{
    Task<Result<CadReadResult>> InspectAndExtractAsync(
        Guid jobId,
        string sourcePath,
        string expectedSourceHash,
        CancellationToken cancellationToken);
}

public sealed class CadReadWorkflowAgentExecutor(CadReadWorkflow workflow) : IAgentCadReadExecutor
{
    public Task<Result<CadReadResult>> InspectAndExtractAsync(
        Guid jobId,
        string sourcePath,
        string expectedSourceHash,
        CancellationToken cancellationToken) =>
        workflow.InspectAndExtractAsync(jobId, sourcePath, expectedSourceHash, cancellationToken);
}

public sealed class AgentCadInspectionService
{
    public const string ApprovalPhrase = "I_APPROVE_READ_ONLY_DWG_INSPECTION";
    internal const int MaximumReturnedSegments = 512;
    private const int MaximumReturnedTextCharacters = 500_000;
    private static readonly SemaphoreSlim CadGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AgentBetaConfiguration _configuration;
    private readonly IAgentCadReadExecutor _executor;
    private readonly Func<DateTimeOffset> _utcNow;

    public AgentCadInspectionService(
        AgentBetaConfiguration configuration,
        IAgentCadReadExecutor executor,
        Func<DateTimeOffset>? utcNow = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<AgentEnvelope> CreatePlanAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var validated = ValidateSource(sourcePath);
        if (validated.Error is not null)
            return Failure("cad inspection plan", validated.Error.Code, validated.Error.Message);

        var snapshot = await SnapshotAsync(validated.Path!, cancellationToken).ConfigureAwait(false);
        if (snapshot.Error is not null)
            return Failure("cad inspection plan", snapshot.Error.Code, snapshot.Error.Message);

        return Success("cad inspection plan", new JsonObject
        {
            ["sourcePath"] = validated.Path,
            ["sourceHash"] = snapshot.Hash,
            ["sourceBytes"] = snapshot.Length,
            ["lastWriteTimeUtc"] = snapshot.LastWriteTimeUtc?.ToString("O"),
            ["approvalRequired"] = ApprovalPhrase,
            ["opensAutoCad"] = false,
            ["expiresWhenSourceChanges"] = true
        });
    }

    public async Task<AgentEnvelope> InspectAsync(
        AgentCadInspectionRequest request,
        CancellationToken cancellationToken)
    {
        const string command = "cad inspect dry-run";
        if (!_configuration.ExecutionEnabled)
            return Failure(command, "AGENT_CAD_EXECUTION_DISABLED", "Agent Beta CAD execution is disabled.");
        if (!string.Equals(request.Approval, ApprovalPhrase, StringComparison.Ordinal))
            return Failure(command, "AGENT_CAD_APPROVAL_REQUIRED", "The exact read-only inspection approval is required.");

        var validated = ValidateSource(request.SourcePath);
        if (validated.Error is not null)
            return Failure(command, validated.Error.Code, validated.Error.Message);
        var before = await SnapshotAsync(validated.Path!, cancellationToken).ConfigureAwait(false);
        if (before.Error is not null)
            return Failure(command, before.Error.Code, before.Error.Message);
        if (!string.Equals(before.Hash, request.ExpectedSourceHash, StringComparison.Ordinal))
            return Failure(command, "AGENT_SOURCE_HASH_MISMATCH", "The DWG no longer matches the approved inspection plan.");
        if (!await CadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Failure(command, "AGENT_CAD_BUSY", "Another Agent Beta CAD inspection is already running.");

        try
        {
            var jobId = Guid.NewGuid();
            var result = await _executor.InspectAndExtractAsync(
                jobId,
                validated.Path!,
                before.Hash!,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
                return Failure(command, result.Error!.Code, result.Error.Message);

            var after = await SnapshotAsync(validated.Path!, cancellationToken).ConfigureAwait(false);
            if (after.Error is not null || !SnapshotsEqual(before, after))
                return Failure(command, "AGENT_SOURCE_CHANGED", "The source DWG changed during read-only inspection.");

            var read = result.Value!;
            var returned = new List<DwgTranslator.Contracts.CadTextSegment>();
            var returnedCharacters = 0;
            foreach (var segment in read.Segments)
            {
                if (returned.Count == MaximumReturnedSegments ||
                    segment.SourceText.Length > MaximumReturnedTextCharacters - returnedCharacters)
                    break;
                returned.Add(segment);
                returnedCharacters += segment.SourceText.Length;
            }
            return Success(command, new JsonObject
            {
                ["jobId"] = jobId.ToString("D"),
                ["sourcePath"] = validated.Path,
                ["sourceHash"] = before.Hash,
                ["sourceUnchanged"] = true,
                ["readOnly"] = true,
                ["inspection"] = JsonSerializer.SerializeToNode(read.Inspection, Json),
                ["excludedFieldCount"] = read.ExcludedFieldCount,
                ["segmentCount"] = read.Segments.Count,
                ["returnedSegmentCount"] = returned.Count,
                ["returnedTextCharacters"] = returnedCharacters,
                ["truncated"] = returned.Count != read.Segments.Count,
                ["segments"] = JsonSerializer.SerializeToNode(returned, Json)
            });
        }
        finally
        {
            CadGate.Release();
        }
    }

    private (string? Path, AgentError? Error) ValidateSource(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(_configuration.AllowedDwgRoot) ||
            !Path.IsPathFullyQualified(_configuration.AllowedDwgRoot))
            return (null, new("AGENT_DWG_ROOT_NOT_CONFIGURED", "The Agent Beta DWG input root is not configured."));
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            return (null, new("AGENT_SOURCE_PATH_INVALID", "An absolute DWG path is required."));

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_configuration.AllowedDwgRoot));
            var path = Path.GetFullPath(sourcePath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
                !string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase))
                return (null, new("AGENT_SOURCE_PATH_FORBIDDEN", "The source must be a DWG below the configured Agent Beta input root."));
            if (!File.Exists(path))
                return (null, new("AGENT_SOURCE_NOT_FOUND", "The source DWG does not exist."));
            if (HasReparsePoint(root, path))
                return (null, new("AGENT_SOURCE_REPARSE_POINT", "Reparse points are not allowed in the Agent Beta DWG path."));
            return (path, null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return (null, new("AGENT_SOURCE_PATH_INVALID", "The source DWG path could not be validated."));
        }
    }

    private static bool HasReparsePoint(string root, string path)
    {
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static async Task<SourceSnapshot> SnapshotAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            info.Refresh();
            return new("sha256:" + Convert.ToHexString(hash).ToLowerInvariant(), info.Length, info.LastWriteTimeUtc, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(null, null, null, new("AGENT_SOURCE_ACCESS_DENIED", "The source DWG cannot be read."));
        }
        catch (IOException)
        {
            return new(null, null, null, new("AGENT_SOURCE_IO_ERROR", "The source DWG could not be read."));
        }
    }

    private static bool SnapshotsEqual(SourceSnapshot left, SourceSnapshot right) =>
        string.Equals(left.Hash, right.Hash, StringComparison.Ordinal) &&
        left.Length == right.Length && left.LastWriteTimeUtc == right.LastWriteTimeUtc;

    private AgentEnvelope Success(string command, JsonNode data) =>
        new(AgentQueryService.ResponseSchema, true, command, _utcNow(), data, null);

    private AgentEnvelope Failure(string command, string code, string message) =>
        new(AgentQueryService.ResponseSchema, false, command, _utcNow(), null, new(code, message));

    private sealed record SourceSnapshot(
        string? Hash,
        long? Length,
        DateTimeOffset? LastWriteTimeUtc,
        AgentError? Error);
}
