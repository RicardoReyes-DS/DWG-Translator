using DwgTranslator.Agent;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Infrastructure.Local;
using DwgTranslator.Infrastructure.Windows;
using DwgTranslator.Shell;
using DwgTranslator.Translation.OpenAI;

namespace DwgTranslator.AgentHost;

public sealed record AgentWorkflowRuntime(
    AgentWorkflowService? Service,
    IAgentBatchFileProcessor BatchProcessor,
    IDesktopJobAdministration? JobAdministration,
    string? Error);

public static class AgentWorkflowRuntimeFactory
{
    public static AgentWorkflowRuntime Create(AgentBetaConfiguration configuration)
    {
        if (!configuration.OpenAiEnabled) return new(null, new FailClosedAgentBatchFileProcessor(), null, null);
        try
        {
            var paths = new WorkspacePaths(configuration.WorkspaceRoot);
            var jobs = new LocalJobStore(paths);
            var reviews = new LocalTranslationReviewStore(paths);
            var clock = new SystemClock();
            var evidenceRoot = Path.Combine(configuration.LogRoot, "cad-bootstrap", "workflow");
            var readEvidence = Path.Combine(evidenceRoot, "read-only");
            var writeEvidence = Path.Combine(evidenceRoot, "write");
            Directory.CreateDirectory(readEvidence);
            Directory.CreateDirectory(writeEvidence);
            var readBootstrap = new CadNetloadBootstrapOptions(configuration.ReadOnlyAdapterAssemblyPath, readEvidence);
            var writeBootstrap = new CadNetloadBootstrapOptions(configuration.WriteAdapterAssemblyPath, writeEvidence);
            if (!CadNetloadBootstrapScript.IsValid(readBootstrap) || !CadNetloadBootstrapScript.IsValid(writeBootstrap))
                return new(null, new FailClosedAgentBatchFileProcessor(), null, "AGENT_CAD_BOOTSTRAP_INVALID");
            var cad = new AutoCadProcessSessionFactory(new AutoCadProcessSessionOptions(
                configuration.AutoCadExecutablePath,
                configuration.ReadOnlyBundleDirectory,
                configuration.WriteBundleDirectory,
                TimeSpan.FromSeconds(configuration.CadStartupSeconds),
                TimeSpan.FromSeconds(configuration.CadExchangeSeconds),
                TimeSpan.FromSeconds(configuration.CadCloseSeconds),
                readBootstrap,
                writeBootstrap,
                TimeSpan.FromSeconds(configuration.CadCleanupGraceSeconds)),
                new AgentCadLifecycleObserver(configuration.LogRoot));
            TimeSpan CadDeadlineForImmutableSource(string sourcePath)
            {
                var source = new FileInfo(sourcePath);
                if (!source.Exists)
                    throw new IOException("Immutable source snapshot is unavailable.");
                return CadDeadlinePolicy.ForSourceBytes(configuration, source.Length);
            }
            var secretReference = SecretReference.Create(configuration.OpenAiCredentialReference);
            if (!secretReference.IsSuccess) return new(null, new FailClosedAgentBatchFileProcessor(), null, "AGENT_OPENAI_CREDENTIAL_REFERENCE_INVALID");
            var secrets = new WindowsCredentialSecretStore();
            var client = new HttpClient
            {
                BaseAddress = new Uri(configuration.OpenAiEndpoint, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(configuration.OpenAiTimeoutSeconds)
            };
            var models = new ConfiguredAgentModelProvider(
                configuration.OpenAiModel,
                configuration.ConfiguredAccessibleModels ?? []);
            var translation = new OpenAIRoutedTranslationGateway(
                client,
                secrets,
                secretReference.Value!,
                models,
                new OpenAITranslationGateway.Limits(
                    configuration.OpenAiMaxOutputTokens,
                    configuration.OpenAiMaxInputTokens,
                    configuration.OpenAiMaxResponseCharacters));
            var coordinator = new DwgTranslationJobCoordinator(
                jobs,
                reviews,
                new CadReadWorkflow(cad, clock, CadDeadlineForImmutableSource),
                new TranslationReviewWorkflow(
                    translation,
                    reviews,
                    clock,
                    configuration.TranslationMaxSegments,
                    configuration.TranslationMaxCharacters,
                    configuration.JobMaxTranslationRequests,
                    configuration.JobMaxInputTokens,
                    configuration.JobMaxOutputTokens,
                    configuration.OpenAiMaxInputTokens,
                    configuration.OpenAiMaxOutputTokens),
                new CadWriteWorkflow(cad, clock, CadDeadlineForImmutableSource),
                clock);
            var administration = new LocalDesktopJobAdministration(paths, jobs, reviews, coordinator, clock);
            var backend = new AgentTranslationWorkflowBackend(coordinator, jobs, reviews, clock, configuration.WorkspaceRoot);
            var service = new AgentWorkflowService(configuration, backend, supervision: new AgentWorkflowSupervisionOptions(
                TimeSpan.FromSeconds(configuration.WorkflowHeartbeatSeconds),
                TimeSpan.FromSeconds(configuration.WorkflowDeadlineSeconds),
                TimeSpan.FromSeconds(configuration.CadCleanupGraceSeconds)));
            IAgentBatchFileProcessor processor = configuration.BatchExecutionEnabled
                ? new ProductionAgentBatchFileProcessor(service, Path.Combine(configuration.LogRoot, "batch-child-bindings"),
                    deadline: TimeSpan.FromSeconds(configuration.WorkflowDeadlineSeconds))
                : new FailClosedAgentBatchFileProcessor();
            return new(service, processor, administration, null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(null, new FailClosedAgentBatchFileProcessor(), null, "AGENT_WORKFLOW_COMPOSITION_FAILED");
        }
    }

    private sealed class ConfiguredAgentModelProvider(string model, IReadOnlyList<string> accessible) : IOpenAiModelProvider
    {
        private readonly HashSet<string> _accessible = accessible.ToHashSet(StringComparer.Ordinal);
        public string Model { get; } = model;
        public bool IsModelAccessible(string candidate) => _accessible.Contains(candidate);
    }
}
