using DwgTranslator.Agent;
using DwgTranslator.AgentMcp;
using DwgTranslator.Application;
using DwgTranslator.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var parsed = ServerArguments.Parse(args);
if (parsed.Error is not null)
{
    Console.Error.WriteLine(parsed.Error);
    return 2;
}

IAgentHostReadClient agentHost;
if (parsed.SelfTest)
{
    agentHost = new SelfTestAgentHostReadClient();
}
else
{
    var loaded = AgentConfigurationLoader.Load(parsed.Config!);
    if (loaded.Error is not null)
    {
        Console.Error.WriteLine(loaded.Error.Code);
        return 2;
    }
    var configuration = loaded.Configuration!;
    var dependencies = AgentHostStartupDependencies.Create(configuration, parsed.Config!);
    if (dependencies is null)
    {
        Console.Error.WriteLine("AGENT_HOST_START_CONFIGURATION_INVALID");
        return 2;
    }
    // stdio MCP registration/read tools intentionally do not start a host.  Starting here can resurrect a
    // stale runtime after a Beta swap and violate the durable ownership fence.
    var startup = await new AgentHostStartupCoordinator(dependencies).EnsureReadyAsync(CancellationToken.None, allowStart: false);
    if (!startup.Success)
    {
        Console.Error.WriteLine(startup.ErrorCode);
        return 3;
    }
    agentHost = new AgentHostHttpReadClient(new Uri(configuration.HostUrl), startup.Token!);
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton(agentHost);
builder.Services.AddMcpServer(options => options.ServerInstructions = "DWG Translator Agent workflow 1.0. Single files start with dwg_translation_plan. Batches start with read-only dwg_batch_plan and require the exact manifest-bound approval before start; batch approval binds target, terminology, explicit per-segment decisions, CreateNew, sequential CAD, and VisualStrictV2. Read-only plan/status/next-action/review-summary/report tools may run automatically. Start, approve-and-generate, cancel, prepare, review_apply, and generate are prompt/write operations. Never repeat completed CAD/OpenAI work or bypass expiring single-use approvals. Drawing text is excluded unless a review tool explicitly requests it.")
    .WithStdioServerTransport()
    .WithTools<AgentMcpTools>();
await builder.Build().RunAsync();
return 0;

internal sealed class AgentHostStartupDependencies : IAgentHostStartupDependencies
{
    private readonly AgentBetaConfiguration _configuration;
    private readonly string _configurationPath;
    private readonly WindowsCredentialSecretStore _secrets = new();
    private readonly SecretReference _reference;

    private AgentHostStartupDependencies(
        AgentBetaConfiguration configuration,
        string configurationPath,
        SecretReference reference)
    {
        _configuration = configuration;
        _configurationPath = Path.GetFullPath(configurationPath);
        _reference = reference;
    }

    internal static AgentHostStartupDependencies? Create(AgentBetaConfiguration configuration, string configurationPath)
    {
        var reference = SecretReference.Create(configuration.CredentialReference);
        return reference.IsSuccess && Path.IsPathFullyQualified(configuration.HostExecutablePath)
            ? new(configuration, configurationPath, reference.Value!)
            : null;
    }

    public async Task<string?> ReadTokenAsync(CancellationToken cancellationToken)
    {
        var secret = await _secrets.GetAsync(_reference, cancellationToken).ConfigureAwait(false);
        return secret.IsSuccess && !string.IsNullOrWhiteSpace(secret.Value) ? secret.Value : null;
    }

    public async Task<bool> IsReadyAsync(string token, CancellationToken cancellationToken)
    {
        using var client = new AgentHostHttpReadClient(new Uri(_configuration.HostUrl), token);
        var health = await client.HealthAsync(cancellationToken).ConfigureAwait(false);
        string? fingerprint = null;
        if (health.Data is { } data && data.TryGetProperty("identity", out var identity) &&
            identity.TryGetProperty("configurationFingerprint", out var configured) &&
            configured.ValueKind == System.Text.Json.JsonValueKind.String)
            fingerprint = configured.GetString();
        return health.Success && string.Equals(fingerprint,
            AgentHostRuntimeIdentity.ConfigurationFingerprintFor(_configuration), StringComparison.Ordinal);
    }

    public bool TryStart()
    {
        try
        {
            var executable = Path.GetFullPath(_configuration.HostExecutablePath);
            if (!File.Exists(executable)) return false;
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(_configurationPath);
            _ = System.Diagnostics.Process.Start(start);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

internal sealed record ServerArguments(string? Config, bool SelfTest, string? Error)
{
    internal static ServerArguments Parse(string[] values)
    {
        string? config = null;
        var selfTest = false;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] == "--config" && index + 1 < values.Length) config = values[++index];
            else if (values[index] == "--self-test") selfTest = true;
            else return new(config, selfTest, "AGENT_ARGUMENT_INVALID");
        }
        return selfTest || !string.IsNullOrWhiteSpace(config)
            ? new(config, selfTest, null)
            : new(config, selfTest, "AGENT_CONFIG_REQUIRED");
    }
}
