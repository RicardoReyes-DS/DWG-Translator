using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Application;

namespace DwgTranslator.Agent;

public sealed record AgentBetaConfiguration(
    string SchemaVersion,
    string Environment,
    bool Enabled,
    bool ExecutionEnabled,
    string WorkspaceRoot,
    string LogRoot,
    string HostUrl = "http://127.0.0.1:47831",
    string CredentialReference = "credential-manager:dwg-translator/agent-beta-host",
    IReadOnlyList<string>? AllowedOperations = null,
    string HostExecutablePath = "",
    string AllowedDwgRoot = "",
    string AutoCadExecutablePath = "",
    string ReadOnlyBundleDirectory = "",
    string ReadOnlyAdapterAssemblyPath = "",
    int CadStartupSeconds = 180,
    int CadExchangeSeconds = 120,
    int CadCloseSeconds = 120,
    int CadCleanupGraceSeconds = 120,
    int CadLargeDwgThresholdMiB = 5,
    int CadExchangeSecondsPerMiB = 10,
    int CadExchangeMaxSeconds = 3600,
    bool OpenAiEnabled = false,
    string OutputDwgRoot = "",
    string WriteBundleDirectory = "",
    string WriteAdapterAssemblyPath = "",
    string OpenAiEndpoint = "https://api.openai.com/v1/",
    string OpenAiModel = "gpt-5.6-terra",
    string OpenAiCredentialReference = "credential-manager:dwg-translator/openai",
    string PromptTemplateVersion = TranslationReviewWorkflow.ContextualPromptTemplateVersion,
    int OpenAiTimeoutSeconds = 120,
    int TranslationMaxSegments = 25,
    int TranslationMaxCharacters = 50_000,
    int OpenAiMaxOutputTokens = 16_384,
    int OpenAiMaxInputTokens = 250_000,
    int OpenAiMaxResponseCharacters = 1_000_000,
    int JobMaxTranslationRequests = 100,
    int JobMaxInputTokens = 10_000_000,
    int JobMaxOutputTokens = 1_000_000,
    int ApprovalLifetimeMinutes = 30,
    IReadOnlyList<string>? ConfiguredAccessibleModels = null,
    int WorkflowHeartbeatSeconds = 5,
    int WorkflowDeadlineSeconds = 3720,
    IReadOnlyList<string>? BatchOutputRoots = null,
    bool BatchExecutionEnabled = false,
    [property: JsonIgnore] string? ActiveConfigurationPath = null,
    [property: JsonIgnore] string? ActiveConfigurationHash = null);

public sealed record AgentError(string Code, string Message, bool Retryable = false);

public sealed record AgentEnvelope(
    string SchemaVersion,
    bool Success,
    string Command,
    DateTimeOffset TimestampUtc,
    JsonNode? Data,
    AgentError? Error);
