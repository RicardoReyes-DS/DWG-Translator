using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Infrastructure.Windows;
using DwgTranslator.Translation.OpenAI;

namespace DwgTranslator.Shell;

public interface IBootstrapFileSecurity
{
    bool IsSecure(string path);
}

public static class ProductionBootstrap
{
    public const string SchemaVersion = "1.1.0";
    public const string LegacySchemaVersion = "1.0.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string ConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DwgTranslator", "bootstrap.v1.json");

    public static MainViewModel CreateStartupViewModel(
        string? path = null,
        IBootstrapFileSecurity? security = null,
        IProductionCompositionDependencies? dependencies = null)
    {
        var loaded = Load(path ?? ConfigurationPath, security ?? new WindowsBootstrapFileSecurity());
        if (!loaded.IsSuccess)
            return CreateConfigurationViewModel(loaded.Error!.Code);
        if (loaded.Value is null || !loaded.Value.Enabled)
            return CreateConfigurationViewModel(loaded.Value is null ? "BOOTSTRAP_MISSING" : "BOOTSTRAP_DISABLED");
        var graph = ProductionCompositionFactory.Create(loaded.Value.ToOptions(), dependencies);
        return graph.IsSuccess ? graph.Value!.ViewModel : CreateConfigurationViewModel(graph.Error!.Code);
    }

    public static Result<BootstrapDocument?> Load(string path, IBootstrapFileSecurity security)
    {
        if (!File.Exists(path)) return Results.Success<BootstrapDocument?>(null);
        if (!security.IsSecure(path)) return Failure("BOOTSTRAP_ACL_INSECURE");
        try
        {
            if (new FileInfo(path).Length is <= 0 or > 64 * 1024) return Failure("BOOTSTRAP_SIZE_INVALID");
            // ReadAllText performs BOM detection. Deployment tooling on Windows commonly emits
            // UTF-8 with a BOM, while the byte-span JSON overload expects BOM-free UTF-8.
            var document = JsonSerializer.Deserialize<BootstrapDocument>(File.ReadAllText(path), Json);
            if (document is null || document.SchemaVersion is not (SchemaVersion or LegacySchemaVersion)) return Failure("BOOTSTRAP_VERSION_INVALID");
            if (!document.Enabled) return Results.Success<BootstrapDocument?>(document);
            return document.IsComplete() ? Results.Success<BootstrapDocument?>(document) : Failure("BOOTSTRAP_CONFIGURATION_INCOMPLETE");
        }
        catch (JsonException) { return Failure("BOOTSTRAP_JSON_INVALID"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Failure("BOOTSTRAP_UNREADABLE"); }
    }

    private static Result<BootstrapDocument?> Failure(string code) =>
        Results.Failure<BootstrapDocument?>(new ContractError(code, ErrorCategory.Configuration, "Production bootstrap is unavailable.", false));

    private static MainViewModel CreateConfigurationViewModel(string startupErrorCode)
    {
        var reference = SecretReference.Create("credential-manager:dwg-translator/openai").Value!;
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
        var settings = new OpenAiSettingsService(
            new WindowsCredentialSecretStore(),
            client,
            reference,
            "gpt-4.1-mini",
            new OpenAiUsageLimits(20, 200_000, 40_000, 2_048));
        return CompositionRoot.CreateConfigurationViewModel(settings, startupErrorCode);
    }
}

public sealed record BootstrapDocument
{
    [JsonRequired] public required string SchemaVersion { get; init; }
    [JsonRequired] public required bool Enabled { get; init; }
    public string? AllowedDataRoot { get; init; }
    public string? WorkspaceRoot { get; init; }
    public string? AutoCadExecutablePath { get; init; }
    public string? ReadOnlyBundleDirectory { get; init; }
    public string? WriteBundleDirectory { get; init; }
    public string? OpenAiModel { get; init; }
    public string? OpenAiCredentialReference { get; init; }
    public string? PromptTemplateVersion { get; init; }
    public int? CadStartupSeconds { get; init; }
    public int? CadExchangeSeconds { get; init; }
    public int? CadCloseSeconds { get; init; }
    public int? OpenAiTimeoutSeconds { get; init; }
    public int? TranslationMaxSegments { get; init; }
    public int? TranslationMaxCharacters { get; init; }
    public int? OpenAiMaxOutputTokens { get; init; }
    public int? OpenAiMaxInputTokens { get; init; }
    public int? OpenAiMaxResponseCharacters { get; init; }
    public int? JobMaxTranslationRequests { get; init; }
    public int? JobMaxInputTokens { get; init; }
    public int? JobMaxOutputTokens { get; init; }
    public string? TranslationRoutingVersion { get; init; }
    public string? TranslationRoutingMode { get; init; }
    public string? ManualOpenAiModel { get; init; }
    public double? RoutingHighMinLengthRatio { get; init; }
    public double? RoutingHighMaxLengthRatio { get; init; }
    public double? RoutingMediumMinLengthRatio { get; init; }
    public double? RoutingMediumMaxLengthRatio { get; init; }

    internal bool IsComplete() => new object?[]
    {
        AllowedDataRoot, WorkspaceRoot, AutoCadExecutablePath, ReadOnlyBundleDirectory, WriteBundleDirectory,
        OpenAiModel, OpenAiCredentialReference, PromptTemplateVersion, CadStartupSeconds, CadExchangeSeconds,
        CadCloseSeconds, OpenAiTimeoutSeconds, TranslationMaxSegments, TranslationMaxCharacters,
        OpenAiMaxOutputTokens, OpenAiMaxInputTokens, OpenAiMaxResponseCharacters, JobMaxTranslationRequests,
        JobMaxInputTokens, JobMaxOutputTokens
    }.All(value => value is not null) && CreateRoutingPolicy().IsSuccess;

    public ProductionCompositionOptions ToOptions() => new(
        AllowedDataRoot!, WorkspaceRoot!, AutoCadExecutablePath!, ReadOnlyBundleDirectory!, WriteBundleDirectory!,
        new Uri("https://api.openai.com/v1/"), OpenAiModel!, OpenAiCredentialReference!, PromptTemplateVersion!,
        TimeSpan.FromSeconds(CadStartupSeconds!.Value), TimeSpan.FromSeconds(CadExchangeSeconds!.Value),
        TimeSpan.FromSeconds(CadCloseSeconds!.Value), TimeSpan.FromSeconds(OpenAiTimeoutSeconds!.Value),
        TranslationMaxSegments!.Value, TranslationMaxCharacters!.Value, OpenAiMaxOutputTokens!.Value,
        OpenAiMaxInputTokens!.Value, OpenAiMaxResponseCharacters!.Value, JobMaxTranslationRequests!.Value,
        JobMaxInputTokens!.Value, JobMaxOutputTokens!.Value, CreateRoutingPolicy().Value!);

    internal Result<TranslationRoutingPolicy> CreateRoutingPolicy()
    {
        if (TranslationRoutingVersion is not null && TranslationRoutingVersion != TranslationRouting.PolicyVersion)
            return RoutingFailure();
        TranslationRoutingPolicy policy;
        try
        {
            policy = (TranslationRoutingMode ?? TranslationRouting.Auto) switch
            {
                TranslationRouting.Auto => TranslationRoutingPolicyFactory.Auto(),
                TranslationRouting.Economy => TranslationRoutingPolicyFactory.Economy(),
                TranslationRouting.MaximumQuality => TranslationRoutingPolicyFactory.MaximumQuality(),
                TranslationRouting.Manual => TranslationRoutingPolicyFactory.Manual(ManualOpenAiModel ?? OpenAiModel ?? string.Empty),
                _ => throw new ArgumentException()
            };
        }
        catch (ArgumentException) { return RoutingFailure(); }
        policy = policy with
        {
            HighMinLengthRatio = RoutingHighMinLengthRatio ?? policy.HighMinLengthRatio,
            HighMaxLengthRatio = RoutingHighMaxLengthRatio ?? policy.HighMaxLengthRatio,
            MediumMinLengthRatio = RoutingMediumMinLengthRatio ?? policy.MediumMinLengthRatio,
            MediumMaxLengthRatio = RoutingMediumMaxLengthRatio ?? policy.MediumMaxLengthRatio
        };
        return TranslationRoutingPolicyFactory.Validate(policy);
    }

    private static Result<TranslationRoutingPolicy> RoutingFailure() => Results.Failure<TranslationRoutingPolicy>(
        new ContractError("BOOTSTRAP_ROUTING_INVALID", ErrorCategory.Configuration, "The linguistic routing bootstrap configuration is invalid.", false));
}

public sealed class WindowsBootstrapFileSecurity : IBootstrapFileSecurity
{
    public bool IsSecure(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var current = WindowsIdentity.GetCurrent().User;
            var acl = new FileInfo(path).GetAccessControl();
            if (current is null || !current.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) return false;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || !CanWrite(rule.FileSystemRights)) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (!sid.Equals(current) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or IdentityNotMappedException) { return false; }
    }

    private static bool CanWrite(FileSystemRights rights) =>
        (rights & (FileSystemRights.Write | FileSystemRights.Modify | FileSystemRights.FullControl | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0;
}
