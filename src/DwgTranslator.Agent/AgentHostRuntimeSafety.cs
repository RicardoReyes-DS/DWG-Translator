using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DwgTranslator.Agent;

public sealed record AgentHostRuntimeIdentity(
    string InstanceId,
    string RuntimeVersion,
    string ConfigurationFingerprint,
    int ProcessId)
{
    public static AgentHostRuntimeIdentity Create(AgentBetaConfiguration configuration) => new(
        Guid.NewGuid().ToString("D"),
        (typeof(AgentHostRuntimeIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?
            .Split('+', 2)[0]) ??
        typeof(AgentHostRuntimeIdentity).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
        ConfigurationFingerprintFor(configuration),
        Environment.ProcessId);

    public static string ConfigurationFingerprintFor(AgentBetaConfiguration configuration)
    {
        var material = JsonSerializer.Serialize(new
        {
            configuration.SchemaVersion,
            configuration.Environment,
            configuration.HostUrl,
            WorkspaceRoot = Path.GetFullPath(configuration.WorkspaceRoot),
            LogRoot = Path.GetFullPath(configuration.LogRoot),
            configuration.ExecutionEnabled,
            configuration.OpenAiEnabled
        });
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}

public sealed class AgentHostOwnershipLease : IDisposable
{
    private readonly FileStream _stream;
    private AgentHostOwnershipLease(FileStream stream) => _stream = stream;

    public static AgentHostOwnershipLease? TryAcquire(AgentBetaConfiguration configuration)
    {
        var directory = Path.Combine(Path.GetFullPath(configuration.LogRoot), "workflow", "ownership");
        Directory.CreateDirectory(directory);
        var scope = configuration.Environment + "\n" + configuration.HostUrl;
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ".lock";
        try
        {
            var stream = new FileStream(Path.Combine(directory, name), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return new(stream);
        }
        catch (IOException) { return null; }
    }

    public void Dispose() => _stream.Dispose();
}

public sealed record AgentWorkflowSupervisionOptions(TimeSpan HeartbeatInterval, TimeSpan OperationDeadline,
    TimeSpan? CadCleanupGrace = null)
{
    public static AgentWorkflowSupervisionOptions Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
    // Production composition passes the independently-configured grace.  Fakes and legacy callers get a
    // bounded single-heartbeat grace instead of an untestable implicit two-minute wait.
    public TimeSpan EffectiveCadCleanupGrace => CadCleanupGrace ?? HeartbeatInterval;
    public bool IsValid => HeartbeatInterval > TimeSpan.Zero && OperationDeadline > HeartbeatInterval &&
                           EffectiveCadCleanupGrace > TimeSpan.Zero;
}
