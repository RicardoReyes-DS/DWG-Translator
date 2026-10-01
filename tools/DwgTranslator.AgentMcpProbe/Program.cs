using ModelContextProtocol.Client;
using System.Text.Json;
using DwgTranslator.AgentMcpProbe;

var parsed = Parse(args);
var stage = "arguments";
try
{
    if (parsed.Error is not null) throw new InvalidOperationException(parsed.Error);
    var launch = McpServerLaunchResolver.Resolve(parsed.Server!, parsed.Config, parsed.ExpectedRuntime!);

    stage = "connect";
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "DWG Translator Agent Beta Probe",
        Command = launch.Command,
        Arguments = launch.Arguments.ToList(),
        InheritEnvironmentVariables = false,
        EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
        ShutdownTimeout = TimeSpan.FromSeconds(5)
    });

    await using var client = await McpClient.CreateAsync(transport);
    stage = "list-tools";
    var tools = await client.ListToolsAsync();
    var actual = tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();
    if (!McpProbeToolContract.IsExact(actual))
        throw new InvalidOperationException("MCP_TOOL_LIST_INVALID");
    var plan = tools.Single(tool => tool.Name == "dwg_invariant_diff_plan");
    var run = tools.Single(tool => tool.Name == "dwg_invariant_diff_run");
    if (string.IsNullOrWhiteSpace(plan.Description) || string.IsNullOrWhiteSpace(run.Description))
        throw new InvalidOperationException("MCP_INVARIANT_DIFF_SCHEMA_INVALID");

    stage = "health";
    var health = await client.CallToolAsync("dwg_health");
    stage = "capabilities";
    var capabilities = await client.CallToolAsync("dwg_capabilities");
    stage = "jobs-list";
    var jobs = await client.CallToolAsync("dwg_jobs_list");
    stage = "job-get-missing";
    var missing = await client.CallToolAsync("dwg_job_get", new Dictionary<string, object?>
    {
        ["jobId"] = Guid.NewGuid().ToString("D")
    });
    var jobStatus = parsed.JobId is null ? null : await client.CallToolAsync("dwg_job_status", new Dictionary<string, object?>
    {
        ["jobId"] = parsed.JobId
    });
    var batchStatus = parsed.BatchId is null ? null : await client.CallToolAsync("dwg_batch_status", new Dictionary<string, object?>
    {
        ["batchId"] = parsed.BatchId
    });
    var passed = health.IsError is not true && capabilities.IsError is not true && jobs.IsError is not true &&
        missing.IsError is true && missing.StructuredContent is not null &&
        (jobStatus is null || jobStatus.IsError is not true) &&
        (batchStatus is null || batchStatus.IsError is not true);
    var evidence = JsonSerializer.Serialize(new
    {
        schemaVersion = "dwg-agent-mcp-probe/1.0",
        passed,
        toolCount = tools.Count,
        tools = actual,
        checkedJobStatus = parsed.JobId is not null,
        checkedBatchStatus = parsed.BatchId is not null,
        mode = parsed.Config is null ? "self-test" : "agent-host"
    });
    await WriteEvidenceAsync(parsed.Evidence, evidence);
    Console.Out.WriteLine(evidence);
    return passed ? 0 : 4;
}
catch (Exception exception)
{
    var diagnostic = JsonSerializer.Serialize(new
    {
        schemaVersion = "dwg-agent-mcp-probe/1.0",
        passed = false,
        mode = parsed.Config is null ? "self-test" : "agent-host",
        stage,
        errorType = exception.GetType().FullName,
        errorMessage = exception.Message
    });
    await WriteEvidenceAsync(parsed.Evidence, diagnostic);
    Console.Error.WriteLine(diagnostic);
    return 5;
}

static async Task WriteEvidenceAsync(string? path, string evidence)
{
    if (path is null) return;
    var evidencePath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
    await File.WriteAllTextAsync(evidencePath, evidence);
}

static (string? Server, string? Config, string? ExpectedRuntime, string? Evidence, string? JobId, string? BatchId, string? Error) Parse(string[] values)
{
    string? server = null;
    string? config = null;
    string? expectedRuntime = null;
    string? evidence = null;
    string? jobId = null;
    string? batchId = null;
    for (var index = 0; index < values.Length; index++)
    {
        if (values[index] == "--server" && index + 1 < values.Length) server = values[++index];
        else if (values[index] == "--config" && index + 1 < values.Length) config = values[++index];
        else if (values[index] == "--expected-runtime" && index + 1 < values.Length) expectedRuntime = values[++index];
        else if (values[index] == "--evidence" && index + 1 < values.Length) evidence = values[++index];
        else if (values[index] == "--job-id" && index + 1 < values.Length && Guid.TryParse(values[++index], out _)) jobId = values[index];
        else if (values[index] == "--batch-id" && index + 1 < values.Length && Guid.TryParse(values[++index], out _)) batchId = values[index];
        else return (server, config, expectedRuntime, evidence, jobId, batchId, "MCP_PROBE_ARGUMENT_INVALID");
    }
    return string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(expectedRuntime)
        ? (server, config, expectedRuntime, evidence, jobId, batchId, "MCP_PROBE_SERVER_AND_RUNTIME_REQUIRED")
        : (server, config, expectedRuntime, evidence, jobId, batchId, null);
}
