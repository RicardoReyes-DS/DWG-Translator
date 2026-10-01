using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;
using DwgTranslator.Translation.OpenAI;

namespace DwgTranslator.Shell;

public sealed record DesktopReviewRow(
    string SegmentId,
    string OriginalText,
    string ProposedText,
    string FinalText,
    string Context,
    string? WarningCode,
    string RiskSeverity = "none",
    string? EffectiveModel = null,
    bool Escalated = false);

public sealed record DesktopReviewSession(
    Guid JobId,
    IReadOnlyList<DesktopReviewRow> Rows,
    string? RequestedMode = null,
    string? BaseModel = null,
    string? EffectiveModelSummary = null,
    int EscalatedSegmentCount = 0);
public sealed record DesktopCompletion(string OutputPath, string OutputHash, CadExerciseValidationReport? ValidationReport = null);
public sealed record DesktopProgressUpdate(double Percentage, string Stage, string Detail, bool IsIndeterminate = false);

public interface IDesktopProgressSource
{
    event EventHandler<DesktopProgressUpdate>? ProgressChanged;
}

public interface IDesktopTranslationWorkflow
{
    Task<Result<DesktopReviewSession>> StartAsync(
        string sourcePath,
        string outputPath,
        string targetLanguage,
        IReadOnlyList<TranslationGlossaryEntry> glossary,
        CancellationToken cancellationToken);

    Task<Result<DesktopCompletion>> CompleteAsync(
        Guid jobId,
        IReadOnlyList<ReviewDecisionInput> decisions,
        CancellationToken cancellationToken);
}

public sealed class CoordinatedDesktopTranslationWorkflow : IDesktopTranslationWorkflow, IDesktopProgressSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    private readonly DwgTranslationJobCoordinator _coordinator;
    private readonly IJobStore _jobs;
    private readonly ITranslationReviewStore _reviews;
    private readonly string _promptTemplateVersion;
    private readonly IOpenAiRoutingProvider? _routing;
    public event EventHandler<DesktopProgressUpdate>? ProgressChanged;

    public CoordinatedDesktopTranslationWorkflow(
        DwgTranslationJobCoordinator coordinator,
        IJobStore jobs,
        ITranslationReviewStore reviews,
        string promptTemplateVersion,
        IOpenAiRoutingProvider? routing = null)
    {
        _coordinator = coordinator;
        _jobs = jobs;
        _reviews = reviews;
        _promptTemplateVersion = string.IsNullOrWhiteSpace(promptTemplateVersion)
            ? throw new ArgumentException("Prompt template version is required.", nameof(promptTemplateVersion))
            : promptTemplateVersion;
        _routing = routing;
    }

    public async Task<Result<DesktopReviewSession>> StartAsync(
        string sourcePath,
        string outputPath,
        string targetLanguage,
        IReadOnlyList<TranslationGlossaryEntry> glossary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        Report(4, "Preflight", "Validando rutas y protegiendo el archivo original.");
        if (!Path.IsPathFullyQualified(sourcePath) || !File.Exists(sourcePath) ||
            !Path.IsPathFullyQualified(outputPath) || File.Exists(outputPath) ||
            !Directory.Exists(Path.GetDirectoryName(outputPath)))
            return Failure<DesktopReviewSession>("DESKTOP_PATH_PREFLIGHT_FAILED", ErrorCategory.Input, "Select an existing source and a new output path.");

        Report(8, "HashSource", "Calculando la huella SHA-256 del DWG original.");
        var sourceHash = await TryFileHashAsync(sourcePath, cancellationToken);
        if (!sourceHash.IsSuccess) return Results.Failure<DesktopReviewSession>(sourceHash.Error!);
        TranslationRoutingPolicy? routingPolicy;
        try { routingPolicy = _routing?.CreatePolicyForNewJob(); }
        catch (InvalidOperationException)
        {
            return Failure<DesktopReviewSession>("OPENAI_ROUTING_CONFIGURATION_INVALID", ErrorCategory.Configuration, "The selected linguistic routing mode is invalid.");
        }
        var specification = new DwgTranslationJobSpecification(
            sourcePath,
            outputPath,
            sourceHash.Value!,
            null,
            targetLanguage,
            _promptTemplateVersion,
            glossary,
            routingPolicy);
        Report(12, "CreateJob", "Creando el trabajo durable y su auditoría local.");
        var created = await _coordinator.CreateAsync(specification, cancellationToken);
        if (!created.IsSuccess) return Results.Failure<DesktopReviewSession>(created.Error!);
        Report(18, "CadInspect", "AutoCAD abre el DWG e inspecciona entidades compatibles.", true);
        var extracted = await _coordinator.InspectAndExtractAsync(created.Value!.JobId, cancellationToken);
        if (!extracted.IsSuccess) return Results.Failure<DesktopReviewSession>(extracted.Error!);
        var extractedState = RequireState<DesktopReviewSession>(extracted.Value!, JobState.Extracted);
        if (!extractedState.IsSuccess) return extractedState;
        Report(34, "SourceIntegrity", "Verificando que AutoCAD no modificó el archivo original.");
        var currentHash = await TryFileHashAsync(sourcePath, cancellationToken);
        if (!currentHash.IsSuccess) return Results.Failure<DesktopReviewSession>(currentHash.Error!);
        if (!string.Equals(currentHash.Value, sourceHash.Value, StringComparison.Ordinal))
            return Failure<DesktopReviewSession>("SOURCE_CHANGED", ErrorCategory.Integrity, "The source changed after extraction.");
        Report(40, "OpenAiTranslation", "OpenAI prepara propuestas estructuradas y se validan tokens y terminología.", true);
        var translated = await _coordinator.TranslateAsync(created.Value.JobId, cancellationToken);
        if (!translated.IsSuccess) return Results.Failure<DesktopReviewSession>(translated.Error!);
        var translatedState = RequireState<DesktopReviewSession>(translated.Value!, JobState.ReviewRequired);
        if (!translatedState.IsSuccess) return translatedState;

        Report(52, "LoadReview", "Cargando la revisión humana durable.");
        var review = await _reviews.LoadAsync(created.Value.JobId, cancellationToken);
        if (!review.IsSuccess) return Results.Failure<DesktopReviewSession>(review.Error!);
        var job = await _jobs.LoadAsync(created.Value.JobId, cancellationToken);
        if (!job.IsSuccess) return Results.Failure<DesktopReviewSession>(job.Error!);
        var data = job.Value!.Data.Deserialize<DwgTranslationJobData>(Json);
        if (data?.Segments is null)
            return Failure<DesktopReviewSession>("JOB_SEGMENTS_MISSING", ErrorCategory.Storage, "Extracted segment context is unavailable.");
        var segments = data.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var rows = review.Value!.Rows.Select(row =>
        {
            var segment = segments[row.SegmentId];
            var context = segment.Entity.Space == "PaperSpace"
                ? $"{segment.Entity.Layout ?? "Layout"} · {segment.Entity.Layer}"
                : $"ModelSpace · {segment.Entity.Layer}";
            return new DesktopReviewRow(row.SegmentId, row.OriginalText, row.ProposedText, row.FinalText, context, row.WarningCode,
                row.RiskSeverity, row.EffectiveModel, row.Escalated);
        }).ToArray();
        Report(55, "ReviewRequired", "Las propuestas están listas para revisión humana.");
        var effectiveModels = rows.Select(row => row.EffectiveModel).Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.Ordinal).ToArray();
        return Results.Success(new DesktopReviewSession(
            created.Value.JobId,
            rows,
            review.Value.RequestedMode,
            review.Value.BaseModel,
            effectiveModels.Length == 0 ? null : string.Join(" + ", effectiveModels),
            rows.Count(row => row.Escalated)));
    }

    public async Task<Result<DesktopCompletion>> CompleteAsync(
        Guid jobId,
        IReadOnlyList<ReviewDecisionInput> decisions,
        CancellationToken cancellationToken)
    {
        Report(62, "ReviewValidation", "Validando cobertura y decisiones humanas.");
        var current = await _jobs.LoadAsync(jobId, cancellationToken);
        if (!current.IsSuccess) return Results.Failure<DesktopCompletion>(current.Error!);
        if (current.Value!.State == JobState.ReviewRequired)
        {
            var approved = await _coordinator.ApproveAsync(jobId, decisions, cancellationToken);
            if (!approved.IsSuccess) return Results.Failure<DesktopCompletion>(approved.Error!);
            var approvedState = RequireState<DesktopCompletion>(approved.Value!, JobState.Approved);
            if (!approvedState.IsSuccess) return approvedState;
        }
        else if (current.Value.State != JobState.Approved)
        {
            return RequireState<DesktopCompletion>(current.Value, JobState.Approved);
        }
        Report(78, "CadWrite", "AutoCAD escribe una copia candidata sin modificar el original.", true);
        var completed = await _coordinator.GenerateAsync(jobId, cancellationToken);
        if (!completed.IsSuccess) return Results.Failure<DesktopCompletion>(completed.Error!);
        var completedState = RequireState<DesktopCompletion>(completed.Value!, JobState.Completed);
        if (!completedState.IsSuccess) return completedState;
        var data = completed.Value!.Data.Deserialize<DwgTranslationJobData>(Json);
        Report(98, "CadValidation", "La copia fue reabierta y sus invariantes fueron verificadas.");
        return data?.OutputHash is not null
            ? Results.Success(new DesktopCompletion(data.Specification.OutputPath, data.OutputHash, data.ValidationReport))
            : Failure<DesktopCompletion>("COMPLETED_OUTPUT_MISSING", ErrorCategory.Storage, "The completed output metadata is unavailable.");
    }

    private static async Task<Result<string>> TryFileHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Results.Success("sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<string>("DESKTOP_OPERATION_CANCELLED", ErrorCategory.Transport, "The operation was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure<string>("DESKTOP_SOURCE_UNREADABLE", ErrorCategory.Input, "The selected source cannot be read.");
        }
    }

    private static Result<T> RequireState<T>(JobDocument document, JobState expected)
    {
        if (document.State == expected) return Results.Success(default(T)!);
        var failure = document.Data["failure"] as JsonObject;
        var code = failure?["code"]?.GetValue<string>() ?? $"JOB_DID_NOT_REACH_{expected.ToString().ToUpperInvariant()}";
        var categoryText = failure?["category"]?.GetValue<string>();
        var category = Enum.TryParse<ErrorCategory>(categoryText, out var parsed) ? parsed : ErrorCategory.Storage;
        var retryable = failure?["retryable"]?.GetValue<bool>() ?? false;
        return Results.Failure<T>(new ContractError(code, category, "The job stopped before reaching the required state.", retryable));
    }

    private static Result<T> Failure<T>(string code, ErrorCategory category, string message) =>
        Results.Failure<T>(new ContractError(code, category, message, false));

    private void Report(double percentage, string stage, string detail, bool indeterminate = false) =>
        ProgressChanged?.Invoke(this, new DesktopProgressUpdate(percentage, stage, detail, indeterminate));
}

internal sealed class UnavailableDesktopTranslationWorkflow : IDesktopTranslationWorkflow
{
    private static readonly ContractError Error = new("DESKTOP_WORKFLOW_NOT_CONFIGURED", ErrorCategory.Configuration, "The desktop workflow is not configured.", false);

    public Task<Result<DesktopReviewSession>> StartAsync(string sourcePath, string outputPath, string targetLanguage, IReadOnlyList<TranslationGlossaryEntry> glossary, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<DesktopReviewSession>(Error));

    public Task<Result<DesktopCompletion>> CompleteAsync(Guid jobId, IReadOnlyList<ReviewDecisionInput> decisions, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<DesktopCompletion>(Error));
}
