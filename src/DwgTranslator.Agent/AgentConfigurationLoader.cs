using System.Text.Json;
using System.Security.Cryptography;
using DwgTranslator.Application;

namespace DwgTranslator.Agent;

public static class AgentConfigurationLoader
{
    private const string ExpectedSchema = "dwg-agent-beta-bootstrap/1.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static (AgentBetaConfiguration? Configuration, AgentError? Error) Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, new("AGENT_CONFIG_REQUIRED", "A configuration file is required."));

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return (null, new("AGENT_CONFIG_NOT_FOUND", "The configuration file does not exist."));

            var configurationBytes = File.ReadAllBytes(fullPath);
            var configuration = JsonSerializer.Deserialize<AgentBetaConfiguration>(configurationBytes, Json);
            if (configuration is null ||
                configuration.SchemaVersion != ExpectedSchema ||
                configuration.Environment != "AgentBeta" ||
                string.IsNullOrWhiteSpace(configuration.WorkspaceRoot) ||
                string.IsNullOrWhiteSpace(configuration.LogRoot))
                return (null, new("AGENT_CONFIG_INVALID", "The Agent Beta configuration is invalid."));
            if (!configuration.Enabled)
                return (null, new("AGENT_ENVIRONMENT_DISABLED", "The Agent Beta environment is disabled."));
            if (!Uri.TryCreate(configuration.HostUrl, UriKind.Absolute, out var hostUri) ||
                hostUri.Scheme != Uri.UriSchemeHttp || !hostUri.IsLoopback || hostUri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(hostUri.Query) || !string.IsNullOrEmpty(hostUri.Fragment))
                return (null, new("AGENT_HOST_URL_INVALID", "The Agent Host URL must be an HTTP loopback origin."));
            if (!configuration.CredentialReference.StartsWith("credential-manager:dwg-translator/", StringComparison.Ordinal))
                return (null, new("AGENT_CREDENTIAL_REFERENCE_INVALID", "The Agent Host credential reference is invalid."));
            if (string.IsNullOrWhiteSpace(configuration.HostExecutablePath))
                return (null, new("AGENT_HOST_EXECUTABLE_REQUIRED", "The Agent Host executable path is required."));
            if (configuration.ExecutionEnabled && !ValidCadExecution(configuration))
                return (null, new("AGENT_CAD_CONFIGURATION_INVALID", "Agent Beta CAD execution configuration is invalid."));
            if (configuration.OpenAiEnabled && !ValidWorkflowExecution(configuration))
                return (null, new("AGENT_WORKFLOW_CONFIGURATION_INVALID", "Agent Beta translation workflow configuration is invalid."));
            if (configuration.BatchOutputRoots is { Count: > 0 } batchRoots &&
                (batchRoots.Count > 16 || batchRoots.Any(root => !Path.IsPathFullyQualified(root)) ||
                 batchRoots.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != batchRoots.Count))
                return (null, new("AGENT_BATCH_OUTPUT_ROOTS_INVALID", "Batch output roots must be distinct absolute paths."));
            if (configuration.BatchExecutionEnabled &&
                (!configuration.OpenAiEnabled || !configuration.ExecutionEnabled || configuration.BatchOutputRoots is not { Count: > 0 }))
                return (null, new("AGENT_BATCH_CONFIGURATION_INVALID", "Enabled batch execution requires configured CAD, OpenAI and an exact output allowlist."));

            var normalized = configuration with
            {
                WorkspaceRoot = Path.GetFullPath(configuration.WorkspaceRoot),
                LogRoot = Path.GetFullPath(configuration.LogRoot),
                HostExecutablePath = Path.GetFullPath(configuration.HostExecutablePath),
                AllowedDwgRoot = NormalizeOptional(configuration.AllowedDwgRoot),
                AutoCadExecutablePath = NormalizeOptional(configuration.AutoCadExecutablePath),
                ReadOnlyBundleDirectory = NormalizeOptional(configuration.ReadOnlyBundleDirectory),
                ReadOnlyAdapterAssemblyPath = NormalizeOptional(configuration.ReadOnlyAdapterAssemblyPath),
                OutputDwgRoot = NormalizeOptional(configuration.OutputDwgRoot),
                WriteBundleDirectory = NormalizeOptional(configuration.WriteBundleDirectory),
                WriteAdapterAssemblyPath = NormalizeOptional(configuration.WriteAdapterAssemblyPath),
                BatchOutputRoots = configuration.BatchOutputRoots?.Select(Path.GetFullPath).Distinct(
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray(),
                ActiveConfigurationPath = fullPath,
                ActiveConfigurationHash = "sha256:" + Convert.ToHexString(SHA256.HashData(configurationBytes)).ToLowerInvariant()
            };
            if (PathsEqual(normalized.WorkspaceRoot, normalized.LogRoot))
                return (null, new("AGENT_CONFIG_INVALID", "Workspace and log roots must be separate."));
            return (normalized, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, new("AGENT_CONFIG_ACCESS_DENIED", "The configuration file cannot be read."));
        }
        catch (IOException)
        {
            return (null, new("AGENT_CONFIG_IO_ERROR", "The configuration file cannot be read."));
        }
        catch (JsonException)
        {
            return (null, new("AGENT_CONFIG_INVALID", "The Agent Beta configuration is invalid."));
        }
        catch (ArgumentException)
        {
            return (null, new("AGENT_CONFIG_INVALID", "The Agent Beta configuration contains an invalid path."));
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool ValidCadExecution(AgentBetaConfiguration configuration) =>
        Path.IsPathFullyQualified(configuration.AllowedDwgRoot) && Directory.Exists(configuration.AllowedDwgRoot) &&
        Path.IsPathFullyQualified(configuration.AutoCadExecutablePath) && File.Exists(configuration.AutoCadExecutablePath) &&
        Path.IsPathFullyQualified(configuration.ReadOnlyBundleDirectory) && Directory.Exists(configuration.ReadOnlyBundleDirectory) &&
        File.Exists(Path.Combine(configuration.ReadOnlyBundleDirectory, "PackageContents.xml")) &&
        Path.IsPathFullyQualified(configuration.ReadOnlyAdapterAssemblyPath) && File.Exists(configuration.ReadOnlyAdapterAssemblyPath) &&
        configuration.CadStartupSeconds is >= 1 and <= 1800 &&
        configuration.CadExchangeSeconds is >= 1 and <= 1800 &&
        configuration.CadCloseSeconds is >= 1 and <= 120 &&
        configuration.CadCleanupGraceSeconds is >= 1 and <= 600 &&
        configuration.CadLargeDwgThresholdMiB is >= 1 and <= 1024 &&
        configuration.CadExchangeSecondsPerMiB is >= 1 and <= 120 &&
        configuration.CadExchangeMaxSeconds >= configuration.CadExchangeSeconds &&
        configuration.CadExchangeMaxSeconds <= 7200 &&
        configuration.WorkflowDeadlineSeconds >= configuration.CadExchangeMaxSeconds + configuration.CadCleanupGraceSeconds &&
        configuration.WorkflowDeadlineSeconds <= 7500;

    private static bool ValidWorkflowExecution(AgentBetaConfiguration configuration) =>
        configuration.ExecutionEnabled &&
        Path.IsPathFullyQualified(configuration.OutputDwgRoot) && Directory.Exists(configuration.OutputDwgRoot) &&
        Path.IsPathFullyQualified(configuration.WriteBundleDirectory) && Directory.Exists(configuration.WriteBundleDirectory) &&
        File.Exists(Path.Combine(configuration.WriteBundleDirectory, "PackageContents.xml")) &&
        Path.IsPathFullyQualified(configuration.WriteAdapterAssemblyPath) && File.Exists(configuration.WriteAdapterAssemblyPath) &&
        Uri.TryCreate(configuration.OpenAiEndpoint, UriKind.Absolute, out var endpoint) &&
        endpoint == new Uri("https://api.openai.com/v1/", UriKind.Absolute) &&
        ValidModel(configuration.OpenAiModel) && configuration.OpenAiModel != "gpt-5.6" &&
        configuration.OpenAiCredentialReference.StartsWith("credential-manager:dwg-translator/", StringComparison.Ordinal) &&
        configuration.PromptTemplateVersion == TranslationReviewWorkflow.ContextualPromptTemplateVersion &&
        configuration.OpenAiTimeoutSeconds is >= 1 and <= 120 &&
        configuration.TranslationMaxSegments is >= 1 and <= 1_000 &&
        configuration.TranslationMaxCharacters is >= 1 and <= 1_000_000 &&
        configuration.OpenAiMaxOutputTokens is >= 1 and <= 100_000 &&
        configuration.OpenAiMaxInputTokens is >= 1 and <= 1_000_000 &&
        configuration.OpenAiMaxResponseCharacters is >= 1 and <= 4_000_000 &&
        configuration.JobMaxTranslationRequests is >= 1 and <= 10_000 &&
        configuration.JobMaxInputTokens is >= 1 and <= 10_000_000 &&
        configuration.JobMaxOutputTokens is >= 1 and <= 1_000_000 &&
        configuration.ApprovalLifetimeMinutes is >= 1 and <= 60 &&
        configuration.ConfiguredAccessibleModels is { Count: > 0 and <= 100 } &&
        configuration.ConfiguredAccessibleModels.All(ValidModel) &&
        configuration.ConfiguredAccessibleModels.Distinct(StringComparer.Ordinal).Count() == configuration.ConfiguredAccessibleModels.Count &&
        !configuration.ConfiguredAccessibleModels.Contains("gpt-5.6", StringComparer.Ordinal);

    private static bool ValidModel(string model) =>
        !string.IsNullOrWhiteSpace(model) && model.Length <= 120 && !model.Any(char.IsWhiteSpace);

    private static string NormalizeOptional(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
}
