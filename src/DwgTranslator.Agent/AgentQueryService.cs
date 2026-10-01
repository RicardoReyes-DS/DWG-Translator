using System.Text.Json;
using System.Text.Json.Nodes;

namespace DwgTranslator.Agent;

public sealed class AgentQueryService
{
    public const string ResponseSchema = "dwg-agent-cli/1.0";
    private static readonly JsonSerializerOptions JobListJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };
    private readonly AgentBetaConfiguration _configuration;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly AgentHostRuntimeIdentity _identity;

    public AgentQueryService(AgentBetaConfiguration configuration, Func<DateTimeOffset>? utcNow = null,
        AgentHostRuntimeIdentity? identity = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _identity = identity ?? AgentHostRuntimeIdentity.Create(configuration);
    }

    public AgentEnvelope Health()
    {
        var exists = Directory.Exists(_configuration.WorkspaceRoot);
        var readable = exists && CanEnumerate(_configuration.WorkspaceRoot);
        return Success("health", new JsonObject
        {
            ["status"] = exists && readable ? "ready" : "degraded",
            ["environment"] = _configuration.Environment,
            ["readOnly"] = !_configuration.OpenAiEnabled,
            ["executionEnabled"] = _configuration.ExecutionEnabled,
            ["openAiEnabled"] = _configuration.OpenAiEnabled,
            ["identity"] = new JsonObject
            {
                ["instanceId"] = _identity.InstanceId,
                ["runtimeVersion"] = _identity.RuntimeVersion,
                ["configurationFingerprint"] = _identity.ConfigurationFingerprint,
                ["processId"] = _identity.ProcessId
            },
            ["workspace"] = new JsonObject
            {
                ["path"] = _configuration.WorkspaceRoot,
                ["exists"] = exists,
                ["readable"] = readable
            }
        });
    }

    public AgentEnvelope Capabilities() => Success("capabilities", new JsonObject
    {
        ["environment"] = _configuration.Environment,
        ["readOnly"] = !_configuration.OpenAiEnabled,
        ["cadExecution"] = _configuration.ExecutionEnabled,
        ["openAiExecution"] = _configuration.OpenAiEnabled,
        ["batch"] = new JsonObject
        {
            ["configured"] = _configuration.BatchOutputRoots is { Count: > 0 },
            ["executionEnabled"] = _configuration.BatchExecutionEnabled,
            ["processor"] = _configuration.BatchExecutionEnabled ? "production-workflow-adapter/1.0" : "fail-closed"
        },
        ["workflowSchemaVersion"] = AgentWorkflowContract.SchemaVersion,
        ["commands"] = JsonSerializer.SerializeToNode(EffectiveCommands())
    });

    private List<string> EffectiveCommands()
    {
        var commands = new List<string> { "health", "capabilities", "jobs list", "jobs get", "cad inspection plan" };
        if (_configuration.ExecutionEnabled)
            commands.AddRange(["cad inspect dry-run", "invariant diff plan", "invariant diff run"]);
        if (_configuration.OpenAiEnabled)
        {
            commands.AddRange([
                "translation plan", "translation prepare", "translation review get", "translation review apply",
                "generation plan", "generation reconciliation plan", "generation reconciliation apply", "generation execute",
                "workflow job status", "workflow job next action", "workflow cancel"
            ]);
            if (_configuration.BatchOutputRoots is { Count: > 0 })
                commands.AddRange(["batch plan", "batch start", "batch status", "batch next action",
                    "batch review summary", "batch approve and generate", "batch report", "batch cancel",
                    "batch recovery plan", "batch recovery start", "batch reconcile review"]);
        }
        return commands;
    }

    public AgentEnvelope ListJobs()
    {
        if (!Directory.Exists(_configuration.WorkspaceRoot))
            return Failure("jobs list", "AGENT_WORKSPACE_NOT_FOUND", "The Agent Beta workspace does not exist.");

        try
        {
            var jobs = new JsonArray();
            foreach (var directory in Directory.EnumerateDirectories(_configuration.WorkspaceRoot).OrderBy(static value => value, StringComparer.Ordinal))
            {
                if (!Guid.TryParse(Path.GetFileName(directory), out var jobId))
                    continue;
                var result = ReadJobSummary(jobId);
                if (result is not null)
                    jobs.Add(result);
            }

            return Success("jobs list", new JsonObject { ["count"] = jobs.Count, ["jobs"] = jobs });
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("jobs list", "AGENT_WORKSPACE_ACCESS_DENIED", "The Agent Beta workspace cannot be read.");
        }
        catch (IOException)
        {
            return Failure("jobs list", "AGENT_WORKSPACE_IO_ERROR", "The Agent Beta workspace cannot be read.");
        }
    }

    public AgentEnvelope GetJob(Guid jobId)
    {
        if (jobId == Guid.Empty)
            return Failure("jobs get", "AGENT_JOB_ID_INVALID", "A non-empty job ID is required.");
        if (!Directory.Exists(_configuration.WorkspaceRoot))
            return Failure("jobs get", "AGENT_WORKSPACE_NOT_FOUND", "The Agent Beta workspace does not exist.");

        try
        {
            var job = ReadJob(jobId, includeSpecification: true);
            return job is null
                ? Failure("jobs get", "AGENT_JOB_NOT_FOUND", "The job does not exist or its document is invalid.")
                : Success("jobs get", job);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("jobs get", "AGENT_JOB_ACCESS_DENIED", "The job document cannot be read.");
        }
        catch (IOException)
        {
            return Failure("jobs get", "AGENT_JOB_IO_ERROR", "The job document cannot be read.");
        }
    }

    private JsonObject? ReadJobSummary(Guid jobId)
    {
        var directory = Path.GetFullPath(Path.Combine(_configuration.WorkspaceRoot, jobId.ToString("D")));
        if (!IsUnderWorkspace(directory))
            return null;
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            return null;
        var path = Path.Combine(directory, "job.json");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<JobListDocument>(stream, JobListJson);
            if (document is null)
                return null;

            var result = new JsonObject
            {
                ["jobId"] = document.JobId ?? jobId.ToString("D"),
                ["state"] = document.State,
                ["version"] = document.Version,
                ["updatedAtUtc"] = document.UpdatedAtUtc
            };
            if (document.Data?.OutputHash is { } outputHash)
                result["outputHash"] = outputHash;
            if (document.Data?.Failure is { } failure)
                result["failure"] = SelectFailure(failure);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private JsonObject? ReadJob(Guid jobId, bool includeSpecification)
    {
        var directory = Path.GetFullPath(Path.Combine(_configuration.WorkspaceRoot, jobId.ToString("D")));
        if (!IsUnderWorkspace(directory))
            return null;
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            return null;
        var path = Path.Combine(directory, "job.json");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var result = new JsonObject
            {
                ["jobId"] = ReadString(root, "jobId") ?? jobId.ToString("D"),
                ["state"] = ReadString(root, "state"),
                ["version"] = ReadInt64(root, "version"),
                ["updatedAtUtc"] = ReadString(root, "updatedAtUtc")
            };
            if (root.TryGetProperty("data", out var data))
            {
                if (includeSpecification && data.TryGetProperty("specification", out var specification))
                    result["specification"] = JsonNode.Parse(specification.GetRawText());
                if (data.TryGetProperty("outputHash", out var outputHash) && outputHash.ValueKind == JsonValueKind.String)
                    result["outputHash"] = outputHash.GetString();
                if (data.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.Object)
                    result["failure"] = SelectFailure(failure);
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject SelectFailure(JsonElement failure) => new()
    {
        ["code"] = ReadString(failure, "code"),
        ["category"] = ReadString(failure, "category"),
        ["retryable"] = ReadBoolean(failure, "retryable"),
        ["stage"] = ReadString(failure, "stage"),
        ["diagnosticId"] = ReadString(failure, "diagnosticId"),
        ["invariantDiagnostics"] = failure.TryGetProperty("invariantDiagnostics", out var diagnostics)
            ? CadInvariantDiagnosticsView.Select(diagnostics)
            : null
    };

    private static JsonObject SelectFailure(JobListFailure failure) => new()
    {
        ["code"] = failure.Code,
        ["category"] = failure.Category,
        ["retryable"] = failure.Retryable,
        ["stage"] = failure.Stage,
        ["diagnosticId"] = failure.DiagnosticId,
        ["invariantDiagnostics"] = failure.InvariantDiagnostics is { } diagnostics
            ? CadInvariantDiagnosticsView.Select(diagnostics)
            : null
    };

    private bool IsUnderWorkspace(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_configuration.WorkspaceRoot));
        return path.StartsWith(root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool CanEnumerate(string path)
    {
        try { _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToArray(); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private AgentEnvelope Success(string command, JsonNode data) =>
        new(ResponseSchema, true, command, _utcNow(), data, null);

    private AgentEnvelope Failure(string command, string code, string message) =>
        new(ResponseSchema, false, command, _utcNow(), null, new(code, message));

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ReadInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private static bool? ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private sealed class JobListDocument
    {
        public string? JobId { get; init; }
        public string? State { get; init; }
        public long? Version { get; init; }
        public string? UpdatedAtUtc { get; init; }
        public JobListData? Data { get; init; }
    }

    private sealed class JobListData
    {
        public string? OutputHash { get; init; }
        public JobListFailure? Failure { get; init; }
    }

    private sealed class JobListFailure
    {
        public string? Code { get; init; }
        public string? Category { get; init; }
        public bool? Retryable { get; init; }
        public string? Stage { get; init; }
        public string? DiagnosticId { get; init; }
        public JsonElement? InvariantDiagnostics { get; init; }
    }
}
