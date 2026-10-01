namespace DwgTranslator.AgentMcpProbe;

public sealed record McpServerLaunch(string Command, IReadOnlyList<string> Arguments, string ServerPath);

public static class McpServerLaunchResolver
{
    public static McpServerLaunch Resolve(string serverPath, string? configurationPath, string expectedRuntimePath)
    {
        if (string.IsNullOrWhiteSpace(serverPath) || string.IsNullOrWhiteSpace(expectedRuntimePath))
            throw new InvalidOperationException("MCP_PROBE_RUNTIME_REQUIRED");

        var runtime = Path.GetFullPath(expectedRuntimePath);
        var serverDirectory = Path.GetFullPath(Path.Combine(runtime, "AgentMcpServer"));
        var server = Path.GetFullPath(serverPath);
        if (!Directory.Exists(runtime) || !Within(server, serverDirectory))
            throw new InvalidOperationException("MCP_PROBE_SERVER_OUTSIDE_RUNTIME");

        var executable = Path.Combine(serverDirectory, "DwgTranslator.AgentMcpServer.exe");
        var assembly = Path.Combine(serverDirectory, "DwgTranslator.AgentMcpServer.dll");
        var dependencies = Path.Combine(serverDirectory, "DwgTranslator.AgentMcpServer.deps.json");
        var runtimeConfig = Path.Combine(serverDirectory, "DwgTranslator.AgentMcpServer.runtimeconfig.json");
        if (!File.Exists(executable) || !File.Exists(assembly) || !File.Exists(dependencies) || !File.Exists(runtimeConfig))
            throw new FileNotFoundException("MCP_PROBE_SERVER_ARTIFACT_MISSING");

        var arguments = new List<string>();
        if (PathsEqual(server, executable))
        {
            if (configurationPath is null) arguments.Add("--self-test");
            else { arguments.Add("--config"); arguments.Add(Path.GetFullPath(configurationPath)); }
            return new(executable, arguments, executable);
        }
        if (PathsEqual(server, assembly))
        {
            arguments.Add(assembly);
            if (configurationPath is null) arguments.Add("--self-test");
            else { arguments.Add("--config"); arguments.Add(Path.GetFullPath(configurationPath)); }
            return new("dotnet", arguments, assembly);
        }

        throw new InvalidOperationException("MCP_PROBE_SERVER_ARTIFACT_INVALID");
    }

    private static bool Within(string path, string root) => PathsEqual(path, root) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool PathsEqual(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
