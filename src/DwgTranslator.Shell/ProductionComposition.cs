using System.IO;
using System.Net.Http;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Infrastructure.Local;
using DwgTranslator.Infrastructure.Windows;
using DwgTranslator.Translation.OpenAI;

namespace DwgTranslator.Shell;

public sealed record ProductionCompositionOptions(
    string AllowedDataRoot,
    string WorkspaceRoot,
    string AutoCadExecutablePath,
    string ReadOnlyBundleDirectory,
    string WriteBundleDirectory,
    Uri OpenAiEndpoint,
    string OpenAiModel,
    string OpenAiCredentialReference,
    string PromptTemplateVersion,
    TimeSpan CadStartupTimeout,
    TimeSpan CadExchangeTimeout,
    TimeSpan CadCooperativeCloseTimeout,
    TimeSpan OpenAiTimeout,
    int TranslationMaxSegments,
    int TranslationMaxCharacters,
    int OpenAiMaxOutputTokens,
    int OpenAiMaxInputTokens,
    int OpenAiMaxResponseCharacters,
    int JobMaxTranslationRequests,
    int JobMaxInputTokens,
    int JobMaxOutputTokens,
    TranslationRoutingPolicy? DefaultRoutingPolicy = null);

public sealed record ProductionCompositionGraph(
    MainViewModel ViewModel,
    IDesktopTranslationWorkflow Workflow,
    DwgTranslationJobCoordinator Coordinator,
    IJobStore JobStore,
    ITranslationReviewStore ReviewStore,
    ICadGatewaySessionFactory CadSessions,
    IVisualReviewWorkflow VisualReview,
    ITranslationGateway TranslationGateway,
    ISecretStore SecretStore,
    IOpenAiSettingsService OpenAiSettings,
    IDesktopJobAdministration JobAdministration);

public interface IProductionCompositionDependencies
{
    IClock CreateClock();
    ICadGatewaySessionFactory CreateCadSessionFactory(AutoCadProcessSessionOptions options);
    ISecretStore CreateSecretStore();
    HttpClient CreateHttpClient();
}

public static class ProductionCompositionFactory
{
    private static readonly Uri SupportedOpenAiEndpoint = new("https://api.openai.com/v1/", UriKind.Absolute);

    public static Result<ProductionCompositionGraph> Create(
        ProductionCompositionOptions? options,
        IProductionCompositionDependencies? dependencies = null)
    {
        var validated = Validate(options);
        if (!validated.IsSuccess)
            return Results.Failure<ProductionCompositionGraph>(validated.Error!);

        var configuration = validated.Value!;
        dependencies ??= new DefaultProductionCompositionDependencies();
        try
        {
            var bootstraps = CreateCadBootstraps(configuration);
            if (!bootstraps.IsSuccess)
                return Results.Failure<ProductionCompositionGraph>(bootstraps.Error!);
            var paths = new WorkspacePaths(configuration.WorkspaceRoot);
            var jobs = new LocalJobStore(paths);
            var reviews = new LocalTranslationReviewStore(paths);
            var clock = dependencies.CreateClock();
            var cad = dependencies.CreateCadSessionFactory(new AutoCadProcessSessionOptions(
                configuration.AutoCadExecutablePath,
                configuration.ReadOnlyBundleDirectory,
                configuration.WriteBundleDirectory,
                configuration.CadStartupTimeout,
                configuration.CadExchangeTimeout,
                configuration.CadCooperativeCloseTimeout,
                bootstraps.Value!.ReadOnly,
                bootstraps.Value.Write));
            var secrets = dependencies.CreateSecretStore();
            var client = dependencies.CreateHttpClient();
            client.BaseAddress = configuration.OpenAiEndpoint;
            client.Timeout = configuration.OpenAiTimeout;
            var openAiSettings = new OpenAiSettingsService(
                secrets,
                client,
                configuration.CredentialReference,
                configuration.OpenAiModel,
                new OpenAiUsageLimits(
                    configuration.JobMaxTranslationRequests,
                    configuration.JobMaxInputTokens,
                    configuration.JobMaxOutputTokens,
                    configuration.OpenAiMaxOutputTokens),
                configuration.DefaultRoutingPolicy);
            var translation = new OpenAIRoutedTranslationGateway(
                client,
                secrets,
                configuration.CredentialReference,
                openAiSettings,
                new OpenAITranslationGateway.Limits(configuration.OpenAiMaxOutputTokens, configuration.OpenAiMaxInputTokens, configuration.OpenAiMaxResponseCharacters));
            var coordinator = new DwgTranslationJobCoordinator(
                jobs,
                reviews,
                new CadReadWorkflow(cad, clock),
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
                new CadWriteWorkflow(cad, clock),
                clock);
            var workflow = new CoordinatedDesktopTranslationWorkflow(
                coordinator,
                jobs,
                reviews,
                configuration.PromptTemplateVersion,
                openAiSettings);
            var visualReview = new LocalVisualReviewWorkflow(jobs, clock, configuration.AutoCadExecutablePath);
            var jobAdministration = new LocalDesktopJobAdministration(paths, jobs, reviews, coordinator, clock);
            return Results.Success(new ProductionCompositionGraph(
                CompositionRoot.CreateMainViewModel(workflow, visualReview, openAiSettings, jobAdministration),
                workflow,
                coordinator,
                jobs,
                reviews,
                cad,
                visualReview,
                translation,
                secrets,
                openAiSettings,
                jobAdministration));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure("PRODUCTION_COMPOSITION_FAILED", "The production service graph could not be constructed.");
        }
    }

    private static Result<ValidatedConfiguration> Validate(ProductionCompositionOptions? options)
    {
        if (options is null)
            return Failure<ValidatedConfiguration>("PRODUCTION_CONFIGURATION_MISSING", "Production configuration was not supplied.");

        if (!TryAbsoluteDirectory(options.AllowedDataRoot, mustExist: true, out var allowedRoot) ||
            string.Equals(allowedRoot, Path.GetPathRoot(allowedRoot), StringComparison.OrdinalIgnoreCase) ||
            !TryAbsoluteDirectory(options.WorkspaceRoot, mustExist: false, out var workspaceRoot) ||
            !IsDescendant(workspaceRoot, allowedRoot) ||
            HasExistingReparsePoint(allowedRoot, workspaceRoot))
            return Failure<ValidatedConfiguration>("PRODUCTION_WORKSPACE_INVALID", "The workspace must be an absolute path below the allowed data root.");

        var readBundle = CadManualLoadBundlePolicy.Resolve(
            options.ReadOnlyBundleDirectory,
            "DwgTranslator.AutoCAD.ReadOnly",
            "DwgTranslator.AutoCAD.ReadOnly.dll");
        var writeBundle = CadManualLoadBundlePolicy.Resolve(
            options.WriteBundleDirectory,
            "DwgTranslator.AutoCAD.Write",
            "DwgTranslator.AutoCAD.Write.dll");
        if (!TryAbsoluteFile(options.AutoCadExecutablePath, out var executablePath) ||
            !readBundle.IsSuccess || !writeBundle.IsSuccess ||
            CadManualLoadBundlePolicy.PathsEqual(readBundle.Value!.BundleDirectory, writeBundle.Value!.BundleDirectory))
            return Failure<ValidatedConfiguration>("PRODUCTION_CAD_CONFIGURATION_INVALID", "AutoCAD and both installed adapter bundles are required.");

        if (!ValidEndpoint(options.OpenAiEndpoint) ||
            string.IsNullOrWhiteSpace(options.OpenAiModel) || options.OpenAiModel.Length > 120 ||
            options.OpenAiModel.Any(char.IsWhiteSpace))
            return Failure<ValidatedConfiguration>("PRODUCTION_OPENAI_CONFIGURATION_INVALID", "The OpenAI endpoint and model are invalid.");

        var credential = SecretReference.Create(options.OpenAiCredentialReference);
        if (!credential.IsSuccess)
            return Failure<ValidatedConfiguration>("PRODUCTION_CREDENTIAL_REFERENCE_INVALID", "A Credential Manager reference is required.");

        var routing = options.DefaultRoutingPolicy ?? TranslationRoutingPolicyFactory.Auto();
        if (!TranslationRoutingPolicyFactory.Validate(routing).IsSuccess)
            return Failure<ValidatedConfiguration>("PRODUCTION_ROUTING_CONFIGURATION_INVALID", "The linguistic routing policy is invalid.");

        if (!string.Equals(options.PromptTemplateVersion, OpenAITranslationGateway.SupportedPromptTemplateVersion, StringComparison.Ordinal))
            return Failure<ValidatedConfiguration>("PRODUCTION_PROMPT_VERSION_UNSUPPORTED", "The configured prompt template version is not supported by the OpenAI adapter.");

        if (!Between(options.CadStartupTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)) ||
            !Between(options.CadExchangeTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10)) ||
            !Between(options.CadCooperativeCloseTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2)) ||
            !Between(options.OpenAiTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2)) ||
            options.TranslationMaxSegments is < 1 or > 1_000 ||
            options.TranslationMaxCharacters is < 1 or > 1_000_000 ||
            options.OpenAiMaxOutputTokens is < 1 or > 100_000 || options.OpenAiMaxInputTokens is < 1 or > 1_000_000 ||
            options.OpenAiMaxResponseCharacters is < 1 or > 4_000_000 || options.JobMaxTranslationRequests is < 1 or > 10_000 ||
            options.JobMaxInputTokens is < 1 or > 10_000_000 || options.JobMaxOutputTokens is < 1 or > 1_000_000)
            return Failure<ValidatedConfiguration>("PRODUCTION_LIMITS_INVALID", "Production timeouts or batching limits are outside the supported range.");

        return Results.Success(new ValidatedConfiguration(
            workspaceRoot,
            executablePath,
            readBundle.Value.BundleDirectory,
            writeBundle.Value.BundleDirectory,
            readBundle.Value.AssemblyPath,
            writeBundle.Value.AssemblyPath,
            options.OpenAiEndpoint,
            options.OpenAiModel,
            credential.Value!,
            options.PromptTemplateVersion,
            options.CadStartupTimeout,
            options.CadExchangeTimeout,
            options.CadCooperativeCloseTimeout,
            options.OpenAiTimeout,
            options.TranslationMaxSegments,
            options.TranslationMaxCharacters,
            options.OpenAiMaxOutputTokens,
            options.OpenAiMaxInputTokens,
            options.OpenAiMaxResponseCharacters,
            options.JobMaxTranslationRequests,
            options.JobMaxInputTokens,
            options.JobMaxOutputTokens,
            routing));
    }

    private static bool TryAbsoluteDirectory(string? path, bool mustExist, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try { fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return !mustExist || Directory.Exists(fullPath);
    }

    private static bool TryAbsoluteFile(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return File.Exists(fullPath);
    }

    private static Result<CadBootstrapConfiguration> CreateCadBootstraps(ValidatedConfiguration configuration)
    {
        try
        {
            var technicalRoot = Path.Combine(configuration.WorkspaceRoot, "_technical", "cad-bootstrap");
            var readEvidence = Path.Combine(technicalRoot, "read-only");
            var writeEvidence = Path.Combine(technicalRoot, "write");
            Directory.CreateDirectory(readEvidence);
            Directory.CreateDirectory(writeEvidence);
            if (HasExistingReparsePoint(configuration.WorkspaceRoot, readEvidence) ||
                HasExistingReparsePoint(configuration.WorkspaceRoot, writeEvidence))
                return Failure<CadBootstrapConfiguration>("PRODUCTION_CAD_BOOTSTRAP_INVALID", "CAD bootstrap evidence paths are unsafe.");
            var read = new CadNetloadBootstrapOptions(configuration.ReadOnlyAssemblyPath, readEvidence);
            var write = new CadNetloadBootstrapOptions(configuration.WriteAssemblyPath, writeEvidence);
            if (!CadNetloadBootstrapScript.IsValid(read) || !CadNetloadBootstrapScript.IsValid(write) ||
                CadManualLoadBundlePolicy.PathsEqual(read.EvidenceDirectory, write.EvidenceDirectory))
                return Failure<CadBootstrapConfiguration>("PRODUCTION_CAD_BOOTSTRAP_INVALID", "CAD bootstrap evidence paths are unsafe.");
            return Results.Success(new CadBootstrapConfiguration(read, write));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure<CadBootstrapConfiguration>("PRODUCTION_CAD_BOOTSTRAP_INVALID", "CAD bootstrap evidence paths could not be prepared.");
        }
    }

    private static bool IsDescendant(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool HasExistingReparsePoint(string root, string target)
    {
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return true;
            var current = root;
            foreach (var segment in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current)) break;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool ValidEndpoint(Uri? endpoint) =>
        endpoint == SupportedOpenAiEndpoint;

    private static bool Between(TimeSpan value, TimeSpan minimum, TimeSpan maximum) => value >= minimum && value <= maximum;

    private static Result<T> Failure<T>(string code, string message) =>
        Results.Failure<T>(new ContractError(code, ErrorCategory.Configuration, message, false));

    private static Result<ProductionCompositionGraph> Failure(string code, string message) =>
        Failure<ProductionCompositionGraph>(code, message);

    private sealed record ValidatedConfiguration(
        string WorkspaceRoot,
        string AutoCadExecutablePath,
        string ReadOnlyBundleDirectory,
        string WriteBundleDirectory,
        string ReadOnlyAssemblyPath,
        string WriteAssemblyPath,
        Uri OpenAiEndpoint,
        string OpenAiModel,
        SecretReference CredentialReference,
        string PromptTemplateVersion,
        TimeSpan CadStartupTimeout,
        TimeSpan CadExchangeTimeout,
        TimeSpan CadCooperativeCloseTimeout,
        TimeSpan OpenAiTimeout,
        int TranslationMaxSegments,
        int TranslationMaxCharacters,
        int OpenAiMaxOutputTokens,
        int OpenAiMaxInputTokens,
        int OpenAiMaxResponseCharacters,
        int JobMaxTranslationRequests,
        int JobMaxInputTokens,
        int JobMaxOutputTokens,
        TranslationRoutingPolicy DefaultRoutingPolicy);

    private sealed record CadBootstrapConfiguration(
        CadNetloadBootstrapOptions ReadOnly,
        CadNetloadBootstrapOptions Write);

    private sealed class DefaultProductionCompositionDependencies : IProductionCompositionDependencies
    {
        public IClock CreateClock() => new SystemClock();
        public ICadGatewaySessionFactory CreateCadSessionFactory(AutoCadProcessSessionOptions options) => new AutoCadProcessSessionFactory(options);
        public ISecretStore CreateSecretStore() => new WindowsCredentialSecretStore();
        public HttpClient CreateHttpClient() => new();
    }
}
