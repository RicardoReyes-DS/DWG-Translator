using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Contracts;
using Microsoft.Win32.SafeHandles;

namespace DwgTranslator.Agent;

public sealed record AgentDwgSnapshot(string Path, string Hash, long Bytes, DateTimeOffset LastWriteTimeUtc);

public sealed class AgentDwgPathPolicy
{
    private readonly string _inputRoot;
    private readonly string[] _outputRoots;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public AgentDwgPathPolicy(string inputRoot, string outputRoot, IReadOnlyList<string>? additionalOutputRoots = null)
    {
        _inputRoot = NormalizeRoot(inputRoot, true);
        _outputRoots = new[] { NormalizeRoot(outputRoot, false) }
            .Concat((additionalOutputRoots ?? []).Select(root => NormalizeRoot(root, false)))
            .Distinct(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    public (string? Path, AgentError? Error) ValidateSource(string sourcePath)
    {
        var result = ValidatePath(sourcePath, _inputRoot, mustExist: true, "SOURCE");
        if (result.Error is not null) return result;
        try
        {
            using var handle = File.OpenHandle(result.Path!, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (OperatingSystem.IsWindows() && WindowsLinks.HasMultipleLinks(handle))
                return (null, Error("SOURCE_HARDLINK_FORBIDDEN", "Hard-linked source DWGs are not allowed."));
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, Error("SOURCE_ACCESS_DENIED", "The source DWG cannot be opened safely."));
        }
    }

    public (string? Path, AgentError? Error) ValidateNewOutput(string outputPath)
    {
        var accepted = _outputRoots.Select(root => ValidatePath(outputPath, root, mustExist: false, "OUTPUT"))
            .Where(candidate => candidate.Error is null).ToArray();
        if (accepted.Length == 0)
            return (null, Error("OUTPUT_PATH_FORBIDDEN", "The DWG path is outside every configured output root."));
        var result = accepted[0];
        if (result.Error is not null) return result;
        if (File.Exists(result.Path!) || Directory.Exists(result.Path!))
            return (null, Error("OUTPUT_ALREADY_EXISTS", "The requested output already exists."));
        var parent = Path.GetDirectoryName(result.Path!);
        return parent is not null && Directory.Exists(parent)
            ? result
            : (null, Error("OUTPUT_DIRECTORY_NOT_FOUND", "The output directory does not exist."));
    }

    public string DefaultOutput(string sourcePath, string targetLanguage)
    {
        var safeLanguage = targetLanguage.Replace('-', '_');
        return Path.Combine(_outputRoots[0], $"{Path.GetFileNameWithoutExtension(sourcePath)}-{safeLanguage}.dwg");
    }

    public static async Task<(AgentDwgSnapshot? Snapshot, AgentError? Error)> SnapshotAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var before = new FileInfo(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            before.Refresh();
            return (new AgentDwgSnapshot(path, "sha256:" + Convert.ToHexString(hash).ToLowerInvariant(),
                before.Length, before.LastWriteTimeUtc), null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, Error("SOURCE_ACCESS_DENIED", "The DWG cannot be read."));
        }
        catch (IOException)
        {
            return (null, Error("SOURCE_IO_ERROR", "The DWG could not be read."));
        }
    }

    private static (string? Path, AgentError? Error) ValidatePath(
        string value,
        string root,
        bool mustExist,
        string kind)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
            return (null, Error($"{kind}_PATH_INVALID", "An absolute local DWG path is required."));
        if (value.Length > 2 && value.AsSpan(2).Contains(':'))
            return (null, Error($"{kind}_ADS_FORBIDDEN", "Alternate data streams are not allowed."));
        try
        {
            var path = Path.GetFullPath(value);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) ||
                !string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase))
                return (null, Error($"{kind}_PATH_FORBIDDEN", "The DWG path is outside its configured root."));
            if (mustExist && !File.Exists(path))
                return (null, Error($"{kind}_NOT_FOUND", "The DWG does not exist."));
            if (HasExistingReparsePoint(root, path))
                return (null, Error($"{kind}_REPARSE_POINT", "Reparse points are not allowed in DWG paths."));
            return (path, null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return (null, Error($"{kind}_PATH_INVALID", "The DWG path could not be validated."));
        }
    }

    private static string NormalizeRoot(string root, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("AGENT_DWG_ROOT_INVALID", nameof(root));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if ((mustExist && !Directory.Exists(full)) || (Directory.Exists(full) && HasExistingReparsePoint(full, full)))
            throw new ArgumentException("AGENT_DWG_ROOT_INVALID", nameof(root));
        if (!Directory.Exists(full))
        {
            var parent = Path.GetDirectoryName(full);
            if (parent is null || !Directory.Exists(parent) || HasExistingReparsePoint(parent, parent))
                throw new ArgumentException("AGENT_DWG_ROOT_INVALID", nameof(root));
        }
        return full;
    }

    private static bool HasExistingReparsePoint(string root, string target)
    {
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var segment in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static AgentError Error(string code, string message) => new(code, message);

    private static class WindowsLinks
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        internal static bool HasMultipleLinks(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw new IOException("SOURCE_LINK_INFORMATION_UNAVAILABLE", Marshal.GetLastPInvokeError());
            return information.NumberOfLinks != 1;
        }
    }
}

internal sealed record WorkflowPlanDocument(
    string SchemaVersion,
    string PlanId,
    string Purpose,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string ApprovalHash,
    bool Consumed,
    DateTimeOffset? ConsumedAtUtc,
    JsonObject Binding);

internal sealed record WorkflowPlanEvidence(WorkflowPlanDocument Document, string ArtifactHash);
internal sealed record WorkflowOperationEvidence(AgentWorkflowOperation Document, string ArtifactHash);
internal sealed record WorkflowIdempotencyEvidence(
    JsonObject Document,
    string ArtifactHash,
    string ArtifactName);
internal sealed record WorkflowArtifactScan<T>(IReadOnlyList<T> Items, bool HadInvalidArtifact);

internal enum IdempotencyBeginKind { Acquired, Completed, Pending, Conflict }
internal sealed record IdempotencyBeginResult(IdempotencyBeginKind Kind, JsonObject? Response = null);

internal sealed class AgentWorkflowFileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;

    public AgentWorkflowFileStore(string logRoot)
    {
        _root = Path.Combine(Path.GetFullPath(logRoot), "workflow");
        Directory.CreateDirectory(Path.Combine(_root, "plans"));
        Directory.CreateDirectory(Path.Combine(_root, "idempotency"));
        Directory.CreateDirectory(Path.Combine(_root, "operations"));
        Directory.CreateDirectory(Path.Combine(_root, "leases"));
    }

    public async Task<(WorkflowPlanDocument Document, string ApprovalId)> CreatePlanAsync(
        string purpose,
        JsonObject binding,
        DateTimeOffset now,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var planId = OpaqueToken(24);
        var approvalId = OpaqueToken(32);
        var document = new WorkflowPlanDocument(
            AgentWorkflowContract.SchemaVersion, planId, purpose, now, now.Add(lifetime),
            HashToken(approvalId), false, null, binding);
        await WriteCreateNewAsync(PlanPath(planId), document, cancellationToken).ConfigureAwait(false);
        return (document, approvalId);
    }

    public async Task<(WorkflowPlanDocument? Plan, AgentError? Error)> LoadPlanAsync(
        string planId,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (!ValidOpaque(planId)) return (null, new("APPROVAL_REQUIRED", "A valid plan-bound approval artifact is required."));
        var path = PlanPath(planId);
        if (!File.Exists(path)) return (null, new("APPROVAL_REQUIRED", "The plan-bound approval artifact does not exist."));
        try
        {
            var plan = JsonSerializer.Deserialize<WorkflowPlanDocument>(await File.ReadAllTextAsync(path, cancellationToken), Json);
            return plan is not null && plan.SchemaVersion == AgentWorkflowContract.SchemaVersion && plan.Purpose == purpose
                ? (plan, null)
                : (null, new("APPROVAL_REQUIRED", "The approval artifact is invalid for this operation."));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, new("APPROVAL_REQUIRED", "The approval artifact could not be validated."));
        }
    }

    public async Task<IReadOnlyList<WorkflowPlanEvidence>> LoadPlanEvidenceAsync(
        string purpose,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkflowPlanEvidence>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "plans"), "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var plan = JsonSerializer.Deserialize<WorkflowPlanDocument>(bytes, Json);
                if (plan?.Purpose == purpose &&
                    string.Equals(plan.Binding["jobId"]?.GetValue<string>(), jobId.ToString("D"), StringComparison.Ordinal))
                    result.Add(new(plan, Sha256(bytes)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result;
    }

    /// <summary>
    /// Strict evidence scan used by generation-failure reconciliation.  A corrupt,
    /// inaccessible or reparse-point artifact makes absence/uniqueness unknowable and
    /// therefore fails the caller closed.
    /// </summary>
    public async Task<WorkflowArtifactScan<WorkflowPlanEvidence>> LoadPlanEvidenceStrictAsync(
        string purpose,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkflowPlanEvidence>();
        var invalid = false;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(_root, "plans"), "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { invalid = true; continue; }
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    if (WorkflowEvidenceJson.HasDuplicateJsonProperties(bytes)) { invalid = true; continue; }
                    var plan = JsonSerializer.Deserialize<WorkflowPlanDocument>(bytes, Json);
                    if (!WorkflowEvidenceValidator.ValidPlanEvidence(plan, Path.GetFileName(path))) { invalid = true; continue; }
                    if (plan!.Purpose == purpose &&
                        string.Equals(plan.Binding["jobId"]?.GetValue<string>(), jobId.ToString("D"), StringComparison.Ordinal))
                        result.Add(new(plan, Sha256(bytes)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    invalid = true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            invalid = true;
        }
        return new(result, invalid);
    }

    public async Task<AgentError?> ConsumePlanAsync(
        WorkflowPlanDocument expected,
        string approvalId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ApprovalMatches(expected, approvalId))
            return new("APPROVAL_REQUIRED", "The exact opaque approval artifact is required.");
        if (expected.ExpiresAtUtc <= now) return new("APPROVAL_EXPIRED", "The approval artifact expired.");
        var path = PlanPath(expected.PlanId);
        await using var processLock = await AcquireLockAsync(path + ".lock", cancellationToken).ConfigureAwait(false);
        var loaded = await LoadPlanAsync(expected.PlanId, expected.Purpose, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null) return loaded.Error;
        if (loaded.Plan!.Consumed) return new("APPROVAL_REPLAYED", "The approval artifact was already consumed.");
        if (loaded.Plan.ExpiresAtUtc <= now) return new("APPROVAL_EXPIRED", "The approval artifact expired.");
        var consumed = loaded.Plan with { Consumed = true, ConsumedAtUtc = now };
        await ReplaceAsync(path, consumed, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<IdempotencyBeginResult> BeginIdempotencyAsync(
        string key,
        string requestHash,
        CancellationToken cancellationToken)
    {
        if (!ValidIdempotencyKey(key) || requestHash.Length != 64 || !requestHash.All(Uri.IsHexDigit))
            return new(IdempotencyBeginKind.Conflict);
        var path = IdempotencyPath(key);
        await using var processLock = await AcquireLockAsync(path + ".lock", cancellationToken).ConfigureAwait(false);
        if (File.Exists(path))
        {
            var current = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject();
            if (!string.Equals(current["requestHash"]?.GetValue<string>(), requestHash, StringComparison.OrdinalIgnoreCase))
                return new(IdempotencyBeginKind.Conflict);
            return current["state"]?.GetValue<string>() == "Completed"
                ? new(IdempotencyBeginKind.Completed, current["response"]?.AsObject())
                : new(IdempotencyBeginKind.Pending);
        }
        await WriteCreateNewAsync(path, new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["requestHash"] = requestHash,
            ["state"] = "Pending"
        }, cancellationToken).ConfigureAwait(false);
        return new(IdempotencyBeginKind.Acquired);
    }

    public async Task CompleteIdempotencyAsync(string key, JsonObject response, CancellationToken cancellationToken)
    {
        var path = IdempotencyPath(key);
        await using var processLock = await AcquireLockAsync(path + ".lock", cancellationToken).ConfigureAwait(false);
        var current = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject();
        current["state"] = "Completed";
        current["response"] = response.DeepClone();
        await ReplaceAsync(path, current, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveOperationAsync(AgentWorkflowOperation operation, CancellationToken cancellationToken) =>
        File.Exists(OperationPath(operation.OperationId))
            ? ReplaceAsync(OperationPath(operation.OperationId), operation, cancellationToken)
            : WriteCreateNewAsync(OperationPath(operation.OperationId), operation, cancellationToken);

    public async Task<AgentWorkflowOperation?> LoadOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!ValidOpaque(operationId)) return null;
        var path = OperationPath(operationId);
        if (!File.Exists(path)) return null;
        try { return await ReadSharedJsonAsync<AgentWorkflowOperation>(path, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task<IReadOnlyList<WorkflowIdempotencyEvidence>> LoadCompletedIdempotencyEvidenceAsync(
        Guid jobId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkflowIdempotencyEvidence>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "idempotency"), "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var document = JsonNode.Parse(bytes)?.AsObject();
                var response = document?["response"] as JsonObject;
                if (document?["state"]?.GetValue<string>() == "Completed" &&
                    string.Equals(response?["jobId"]?.GetValue<string>(), jobId.ToString("D"), StringComparison.Ordinal) &&
                    string.Equals(response?["operationId"]?.GetValue<string>(), operationId, StringComparison.Ordinal))
                    result.Add(new(document!, Sha256(bytes), Path.GetFileName(path)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result;
    }

    public async Task<WorkflowArtifactScan<WorkflowIdempotencyEvidence>> LoadCompletedIdempotencyEvidenceStrictAsync(
        Guid jobId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkflowIdempotencyEvidence>();
        var invalid = false;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(_root, "idempotency"), "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { invalid = true; continue; }
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    var document = JsonNode.Parse(bytes)?.AsObject();
                    if (!WorkflowEvidenceValidator.ValidIdempotencyEvidence(document, Path.GetFileName(path))) { invalid = true; continue; }
                    var response = document!["response"] as JsonObject;
                    if (document["state"]?.GetValue<string>() == "Completed" &&
                        string.Equals(response?["jobId"]?.GetValue<string>(), jobId.ToString("D"), StringComparison.Ordinal) &&
                        string.Equals(response?["operationId"]?.GetValue<string>(), operationId, StringComparison.Ordinal))
                        result.Add(new(document, Sha256(bytes), Path.GetFileName(path)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    invalid = true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            invalid = true;
        }
        return new(result, invalid);
    }

    public async Task<WorkflowOperationEvidence?> LoadOperationEvidenceAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!ValidOpaque(operationId)) return null;
        var path = OperationPath(operationId);
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var operation = JsonSerializer.Deserialize<AgentWorkflowOperation>(bytes, Json);
            return operation is null ? null : new(operation, Sha256(bytes));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task<AgentWorkflowOperation?> LoadLatestOperationAsync(Guid jobId, CancellationToken cancellationToken)
    {
        AgentWorkflowOperation? latest = null;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "operations"), "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var candidate = await ReadSharedJsonAsync<AgentWorkflowOperation>(path, cancellationToken).ConfigureAwait(false);
                if (candidate?.JobId == jobId && (latest is null || candidate.UpdatedAtUtc > latest.UpdatedAtUtc)) latest = candidate;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return latest;
    }

    public async Task<IReadOnlyList<AgentWorkflowOperation>> LoadOperationsAsync(CancellationToken cancellationToken)
    {
        var operations = new List<AgentWorkflowOperation>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "operations"), "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = await ReadSharedJsonAsync<AgentWorkflowOperation>(path, cancellationToken).ConfigureAwait(false);
                if (value is not null) operations.Add(value);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return operations;
    }

    public async Task<WorkflowArtifactScan<AgentWorkflowOperation>> LoadOperationsStrictAsync(CancellationToken cancellationToken)
    {
        var operations = new List<AgentWorkflowOperation>();
        var invalid = false;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(_root, "operations"), "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { invalid = true; continue; }
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    var value = JsonSerializer.Deserialize<AgentWorkflowOperation>(bytes, Json);
                    if (!WorkflowEvidenceValidator.ValidOperationEvidence(value, Path.GetFileName(path))) invalid = true;
                    else operations.Add(value!);
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    invalid = true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            invalid = true;
        }
        return new(operations, invalid);
    }


    public async Task<bool> HasActiveOperationAsync(CancellationToken cancellationToken) =>
        (await LoadOperationsAsync(cancellationToken).ConfigureAwait(false)).Any(operation => operation.State == "Running");

    public async Task<bool> TryTransitionOperationAsync(
        AgentWorkflowOperation replacement,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var path = OperationPath(replacement.OperationId);
        await using var processLock = await AcquireLockAsync(path + ".lock", cancellationToken).ConfigureAwait(false);
        var current = await LoadOperationAsync(replacement.OperationId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.JobId != replacement.JobId || current.State != expectedState) return false;
        await ReplaceAsync(path, replacement, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IAsyncDisposable?> TryAcquireLeaseAsync(string scope, CancellationToken cancellationToken)
    {
        if (scope == "cad:global" && File.Exists(CadBlockedPath())) return null;
        var path = Path.Combine(_root, "leases", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ".lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch (IOException) { return null; }
    }

    /// <summary>Read-only lease probe. It never creates a lease file.</summary>
    public bool IsLeaseActive(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return true;
        var path = Path.Combine(_root, "leases", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ".lock");
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public Task MarkCadBlockedAsync(AgentCadLifecycleReceipt receipt, CancellationToken cancellationToken) =>
        ReplaceAsync(CadBlockedPath(), receipt, cancellationToken);

    public AgentCadLifecycleReceipt? LoadCadBlocked()
    {
        try { return File.Exists(CadBlockedPath()) ? JsonSerializer.Deserialize<AgentCadLifecycleReceipt>(File.ReadAllText(CadBlockedPath()), Json) : null; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static string RequestHash(JsonNode value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString()))).ToLowerInvariant();

    internal static string TextHash(string value) => WorkflowEvidenceJson.TextHash(value);

    internal static string Sha256(ReadOnlySpan<byte> value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    internal static string OpaqueToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool ApprovalMatches(WorkflowPlanDocument plan, string approvalId) =>
        ValidOpaque(approvalId) && ContractPatterns.Sha256().IsMatch("sha256:" + plan.ApprovalHash) &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(plan.ApprovalHash), Convert.FromHexString(HashToken(approvalId)));

    private string PlanPath(string id) => Path.Combine(_root, "plans", id + ".json");
    private string IdempotencyPath(string key) => Path.Combine(_root, "idempotency", HashToken(key) + ".json");
    private string OperationPath(string id) => Path.Combine(_root, "operations", id + ".json");
    private string CadBlockedPath() => Path.Combine(_root, "leases", "cad-cleanup-required.json");
    private static string HashToken(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static bool ValidOpaque(string value) => WorkflowEvidenceJson.ValidOpaque(value);
    private static bool ValidIdempotencyKey(string value) => !string.IsNullOrWhiteSpace(value) && value.Length is >= 8 and <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException) when (attempt < 49) { await Task.Delay(20, cancellationToken).ConfigureAwait(false); }
        }
        throw new IOException("WORKFLOW_LOCK_UNAVAILABLE");
    }

    private static async Task<T?> ReadSharedJsonAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCreateNewAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteCreateNewAsync(temporary, value, cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 49 &&
                    exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
