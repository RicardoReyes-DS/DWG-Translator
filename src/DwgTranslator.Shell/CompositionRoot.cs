using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Shell;

public static class CompositionRoot
{
    public static MainViewModel CreateMainViewModel() => new(new UnavailableDesktopTranslationWorkflow(), false, null, null);

    public static MainViewModel CreateConfigurationViewModel(IOpenAiSettingsService openAiSettings, string? startupErrorCode = null)
    {
        var viewModel = new MainViewModel(new UnavailableDesktopTranslationWorkflow(), false, null, openAiSettings);
        if (!string.IsNullOrWhiteSpace(startupErrorCode)) viewModel.ReportStartupFailure(startupErrorCode);
        return viewModel;
    }

    public static MainViewModel CreateMainViewModel(
        IDesktopTranslationWorkflow workflow,
        IVisualReviewWorkflow? visualReview = null,
        IOpenAiSettingsService? openAiSettings = null,
        IDesktopJobAdministration? jobAdministration = null) => new(workflow, true, visualReview, openAiSettings, jobAdministration);
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string? _sourceFile;
    private string? _outputFile;
    private LanguageOption _selectedLanguage;
    private readonly IDesktopTranslationWorkflow _workflow;
    private readonly IVisualReviewWorkflow? _visualReview;
    private readonly IOpenAiSettingsService? _openAiSettings;
    private readonly IDesktopJobAdministration? _jobAdministration;
    private readonly bool _servicesConfigured;
    private readonly string _title;
    private string _status;
    private string _searchText = string.Empty;
    private ReviewFilterOption _selectedReviewFilter;
    private ICollectionView? _reviewView;
    private bool _isBusy;
    private bool _isCompleted;
    private Guid? _currentJobId;
    private int _selectedResultsTab;
    private Guid? _completedJobId;
    private string? _completedOutputPath;
    private string? _completedOutputHash;
    private Guid? _visualReviewSessionId;
    private string? _visualReviewDecision;
    private string _selectedOpenAiModel = string.Empty;
    private OpenAiRoutingModeOption? _selectedRoutingMode;
    private string _effectiveModelSummary = "Modelo efectivo disponible después de traducir.";
    private string _openAiTierAccessSummary = "Acceso Terra/Luna/Sol aún no consultado.";
    private int _escalatedSegmentCount;
    private string _openAiCredentialStatus = "No disponible en esta configuración.";
    private bool _openAiCredentialReady;
    private bool _openAiUsageConfirmed;
    private double _progressValue;
    private bool _progressIsIndeterminate;
    private string _progressDetail = "Listo para iniciar un trabajo.";
    private DesktopJobSummary? _selectedJob;
    private int _retentionDays = 30;
    private string _qualitySummary = "Las métricas estarán disponibles después de completar una revisión.";

    internal MainViewModel(IDesktopTranslationWorkflow workflow, bool servicesConfigured, IVisualReviewWorkflow? visualReview, IOpenAiSettingsService? openAiSettings, IDesktopJobAdministration? jobAdministration = null)
    {
        _workflow = workflow;
        _visualReview = visualReview;
        _openAiSettings = openAiSettings;
        _jobAdministration = jobAdministration;
        _servicesConfigured = servicesConfigured;
        _title = "DWG Translator Internal";
        _status = _servicesConfigured
            ? "Listo para extraer y traducir con revisión humana."
            : "fail-closed — AutoCAD y OpenAI aún no están configurados en esta composición; seleccionar rutas no abre ni modifica archivos.";
        Languages = [new("en", "Inglés"), new("es-MX", "Español (México)"), new("fr", "Francés"), new("de", "Alemán"), new("pt-BR", "Portugués (Brasil)")];
        ExclusionReasons = ["NOT_TRANSLATABLE", "KEEP_ORIGINAL", "REQUIRES_SPECIALIST"];
        SelectedExclusionReason = ExclusionReasons[0];
        _selectedLanguage = Languages[1];
        ReviewFilters = [new("all", "Todos"), new("pending", "Pendientes"), new("approved", "Aprobados"), new("excluded", "Excluidos"), new("warning", "Con advertencia")];
        _selectedReviewFilter = ReviewFilters[0];
        OpenAiModelOptions = new ObservableCollection<string>(openAiSettings?.ModelOptions ?? []);
        OpenAiRoutingModes = openAiSettings?.RoutingModes ?? [];
        if (openAiSettings is not null)
        {
            _selectedOpenAiModel = openAiSettings.Model;
            _selectedRoutingMode = OpenAiRoutingModes.FirstOrDefault(item => item.Value == openAiSettings.RoutingMode) ??
                (OpenAiRoutingModes.Count > 0 ? OpenAiRoutingModes[0] : null);
            _openAiCredentialStatus = "Clave sin verificar. Guarda una nueva o prueba la conexión existente.";
        }
        ReviewRows.CollectionChanged += ReviewRowsChanged;
        GlossaryEntries.CollectionChanged += GlossaryEntriesChanged;
        if (workflow is IDesktopProgressSource progressSource) progressSource.ProgressChanged += WorkflowProgressChanged;
    }

    public string Title => _title;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public bool ProgressIsIndeterminate { get => _progressIsIndeterminate; private set => Set(ref _progressIsIndeterminate, value); }
    public string ProgressDetail { get => _progressDetail; private set => Set(ref _progressDetail, value); }
    public string QualitySummary { get => _qualitySummary; private set => Set(ref _qualitySummary, value); }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) NotifyCommandState(); } }
    public bool IsCompleted { get => _isCompleted; private set { if (Set(ref _isCompleted, value)) NotifyReviewState(); } }
    public int SelectedResultsTab { get => _selectedResultsTab; set => Set(ref _selectedResultsTab, value); }
    public string ProgressSummary => IsBusy ? "Procesando…" : ReviewRows.Count == 0 ? "Sin segmentos cargados" : ReviewCoverageComplete ? "Revisión completa" : $"{PendingCount} segmentos requieren decisión";
    public string CoverageSummary => ReviewRows.Count == 0 ? "0 de 0 revisados" : $"{ApprovedCount + ExcludedCount} de {TotalCount} revisados";
    public string CurrentStage => IsCompleted ? "Revisión visual pendiente" : _currentJobId.HasValue ? "Revisión humana" : "Configuración";
    public ObservableCollection<ReviewRowViewModel> ReviewRows { get; } = [];
    public ObservableCollection<ValidationRowViewModel> ValidationRows { get; } = [];
    public ObservableCollection<GlossaryEntryViewModel> GlossaryEntries { get; } = [];
    public ObservableCollection<DesktopJobSummary> Jobs { get; } = [];
    public ICollectionView ReviewView => _reviewView ??= CreateReviewView();
    public IReadOnlyList<LanguageOption> Languages { get; }
    public IReadOnlyList<ReviewFilterOption> ReviewFilters { get; }
    public IReadOnlyList<string> ExclusionReasons { get; }
    public string SelectedExclusionReason { get; set; }
    public bool HasReviewRows => ReviewRows.Count > 0;
    public bool HasValidationRows => ValidationRows.Count > 0;
    public bool HasVisibleRows => ReviewRows.Any(FilterReviewRow);
    public bool CanReview => !IsBusy && HasReviewRows && !IsCompleted;
    public bool CanCancel => IsBusy;
    public bool HasJobAdministration => _jobAdministration is not null;
    public bool CanRefreshJobs => HasJobAdministration && !IsBusy;
    public bool CanResumeSelectedJob => HasJobAdministration && !IsBusy && SelectedJob?.CanResume == true;
    public IReadOnlyList<int> RetentionOptions { get; } = [7, 14, 30, 60, 90];
    public int RetentionDays { get => _retentionDays; set { if (Set(ref _retentionDays, value)) NotifyCommandState(); } }
    public DesktopJobSummary? SelectedJob
    {
        get => _selectedJob;
        set { if (Set(ref _selectedJob, value)) Notify(nameof(CanResumeSelectedJob)); }
    }
    public int TotalCount => ReviewRows.Count;
    public int PendingCount => ReviewRows.Count(row => row.State is not (SegmentState.Approved or SegmentState.Excluded));
    public int ApprovedCount => ReviewRows.Count(row => row.State == SegmentState.Approved);
    public int ExcludedCount => ReviewRows.Count(row => row.State == SegmentState.Excluded);
    public int WarningCount => ReviewRows.Count(row => !string.IsNullOrWhiteSpace(row.Warning));
    public bool ReviewCoverageComplete => HasReviewRows && ReviewRows.All(row => row.State is SegmentState.Approved or SegmentState.Excluded);
    public bool CanConfigure => !IsBusy && !_currentJobId.HasValue;
    public bool CanEditGlossary => CanConfigure;
    public bool HasOpenAiSettings => _openAiSettings is not null;
    public bool CanManageOpenAiSettings => HasOpenAiSettings && CanConfigure && !IsBusy;
    public bool CanDiscoverOpenAiModels => CanManageOpenAiSettings;
    public bool CanTestOpenAiConnection => CanManageOpenAiSettings && ValidOpenAiModel(OpenAiTestModel);
    public bool OpenAiCredentialReady => _openAiCredentialReady;
    public ObservableCollection<string> OpenAiModelOptions { get; }
    public IReadOnlyList<OpenAiRoutingModeOption> OpenAiRoutingModes { get; }
    public OpenAiRoutingModeOption? SelectedRoutingMode
    {
        get => _selectedRoutingMode;
        set
        {
            if (!Set(ref _selectedRoutingMode, value) || value is null) return;
            var selected = _openAiSettings?.SelectRouting(value.Value, SelectedOpenAiModel);
            _openAiCredentialReady = false;
            _openAiUsageConfirmed = false;
            OpenAiCredentialStatus = selected?.IsSuccess == true
                ? $"Modo {value.DisplayName} seleccionado; valida acceso a {OpenAiTestModel}."
                : "La selección de routing no es válida.";
            NotifyOpenAiState();
            Notify(nameof(RoutingBehaviorSummary));
        }
    }
    public string RoutingBehaviorSummary => SelectedRoutingMode?.Description ?? "Selecciona un modo de calidad.";
    public string EffectiveModelSummary { get => _effectiveModelSummary; private set => Set(ref _effectiveModelSummary, value); }
    public string OpenAiTierAccessSummary { get => _openAiTierAccessSummary; private set => Set(ref _openAiTierAccessSummary, value); }
    public int EscalatedSegmentCount { get => _escalatedSegmentCount; private set => Set(ref _escalatedSegmentCount, value); }
    public string OpenAiCredentialStatus { get => _openAiCredentialStatus; private set => Set(ref _openAiCredentialStatus, value); }
    public string OpenAiLimitsSummary => _openAiSettings is null
        ? "Los límites de OpenAI no están configurados."
        : $"Máximo por trabajo: {_openAiSettings.Limits.MaxRequestsPerJob} solicitudes, {_openAiSettings.Limits.MaxInputTokensPerJob:N0} tokens de entrada y {_openAiSettings.Limits.MaxOutputTokensPerJob:N0} de salida. Máximo por solicitud: {_openAiSettings.Limits.MaxOutputTokensPerRequest:N0} de salida.";
    public string SelectedOpenAiModel
    {
        get => _selectedOpenAiModel;
        set
        {
            if (!Set(ref _selectedOpenAiModel, value?.Trim() ?? string.Empty)) return;
            if (SelectedRoutingMode?.Value == TranslationRouting.Manual)
                _openAiSettings?.SelectRouting(TranslationRouting.Manual, _selectedOpenAiModel);
            _openAiCredentialReady = false;
            _openAiUsageConfirmed = false;
            OpenAiCredentialStatus = "Modelo cambiado; prueba la conexión antes de traducir.";
            NotifyOpenAiState();
        }
    }
    public bool OpenAiUsageConfirmed
    {
        get => _openAiUsageConfirmed;
        set { if (Set(ref _openAiUsageConfirmed, value)) NotifyCommandState(); }
    }
    public bool CanReset => !IsBusy && (HasReviewRows || !string.IsNullOrWhiteSpace(SourceFile) || !string.IsNullOrWhiteSpace(OutputFile));
    public bool CanAttemptTranslation => _servicesConfigured && CanConfigure && !HasReviewRows;
    public bool CanStartTranslation => _servicesConfigured && CanConfigure && !HasReviewRows && GlossaryIsValid() &&
        (!HasOpenAiSettings || OpenAiCredentialReady && OpenAiUsageConfirmed) && !string.IsNullOrWhiteSpace(SourceFile) && !string.IsNullOrWhiteSpace(OutputFile) &&
        !SamePath(SourceFile, OutputFile);
    public string TranslationReadinessSummary
    {
        get
        {
            if (!_servicesConfigured) return "La configuración productiva todavía no está activa.";
            if (string.IsNullOrWhiteSpace(SourceFile)) return "Selecciona el archivo DWG original.";
            if (string.IsNullOrWhiteSpace(OutputFile)) return "Elige el nombre y la carpeta de la nueva copia.";
            if (SamePath(SourceFile, OutputFile)) return "La nueva copia debe tener una ruta diferente al archivo original.";
            if (!GlossaryIsValid()) return "Corrige o elimina los términos incompletos o duplicados del glosario.";
            if (HasOpenAiSettings && !OpenAiCredentialReady) return "Pulsa “Probar conexión” para validar la clave y el modelo.";
            if (HasOpenAiSettings && !OpenAiUsageConfirmed) return "Marca “Autorizo usar la API de OpenAI para este trabajo”.";
            return "Todo listo para extraer y traducir.";
        }
    }
    public bool CanGenerate => _servicesConfigured && !IsBusy && _currentJobId.HasValue && ReviewCoverageComplete;
    public bool CanAttemptGenerate => _servicesConfigured && !IsBusy && _currentJobId.HasValue && HasReviewRows;
    public string GenerationReadinessSummary => !HasReviewRows || !_currentJobId.HasValue
        ? string.Empty
        : ReviewCoverageComplete
            ? "Todo revisado. Pulsa “Generar copia DWG” para guardar y validar el plano traducido."
            : $"Para guardar la copia, aprueba o excluye los {PendingCount} segmentos pendientes.";
    public bool CanOpenVisualReview => _visualReview is not null && IsCompleted && !IsBusy && _completedJobId.HasValue && _visualReviewSessionId is null && _visualReviewDecision is null;
    public bool CanRecordVisualReview => _visualReview is not null && IsCompleted && !IsBusy && _visualReviewSessionId.HasValue && _visualReviewDecision is null;
    public string VisualReviewSummary => _visualReviewDecision is null
        ? _visualReviewSessionId.HasValue ? "AutoCAD abierto · registra PASS o FAIL después de revisar el plano" : IsCompleted ? "Revisión visual pendiente" : "Disponible después de generar y validar"
        : $"Revisión visual registrada: {_visualReviewDecision}";

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? string.Empty)) RefreshReviewView(); }
    }
    public ReviewFilterOption SelectedReviewFilter
    {
        get => _selectedReviewFilter;
        set { if (Set(ref _selectedReviewFilter, value)) RefreshReviewView(); }
    }

    public string? SourceFile
    {
        get => _sourceFile;
        set
        {
            if (Set(ref _sourceFile, value)) ConfigurationChanged();
        }
    }
    public string? OutputFile
    {
        get => _outputFile;
        set
        {
            if (Set(ref _outputFile, value)) ConfigurationChanged();
        }
    }
    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set { if (Set(ref _selectedLanguage, value)) ConfigurationChanged(); }
    }

    public async Task StartTranslationAsync(CancellationToken cancellationToken = default)
    {
        if (HasOpenAiSettings && !OpenAiCredentialReady && TranslationFilesAndGlossaryAreReady())
        {
            await TestOpenAiConnectionAsync(cancellationToken);
            if (OpenAiCredentialReady)
                Status = "Conexión verificada. Marca “Autorizo usar la API de OpenAI para este trabajo” y vuelve a pulsar “Extraer y traducir”.";
            else
                Status = "No se pudo validar la conexión. Revisa el estado de OpenAI antes de traducir.";
            return;
        }
        if (!CanStartTranslation)
        {
            Status = "Antes de traducir: " + TranslationReadinessSummary;
            return;
        }
        var glossary = GlossaryEntries
            .Select(entry => new TranslationGlossaryEntry { Source = entry.Source.Trim(), Target = entry.Target.Trim(), CaseSensitive = entry.CaseSensitive })
            .ToArray();
        SetProgress(10, true, "Paso 1 de 4 · AutoCAD inspecciona y extrae; después OpenAI prepara las propuestas.");
        IsBusy = true;
        Status = "Inspeccionando el DWG y preparando propuestas…";
        ReviewRows.Clear();
        ValidationRows.Clear();
        SelectedResultsTab = 0;
        _currentJobId = null;
        IsCompleted = false;
        try
        {
            var result = await _workflow.StartAsync(SourceFile!, OutputFile!, SelectedLanguage.Tag, glossary, cancellationToken);
            if (!result.IsSuccess)
            {
                Status = SafeError(result.Error!);
                SetProgress(0, false, "Trabajo detenido antes de la revisión. Inicia un nuevo trabajo para reintentar.");
                return;
            }
            _currentJobId = result.Value!.JobId;
            foreach (var row in result.Value.Rows)
                ReviewRows.Add(new ReviewRowViewModel
                {
                    SegmentId = row.SegmentId,
                    OriginalText = row.OriginalText,
                    ProposedText = row.ProposedText,
                    FinalText = row.FinalText,
                    Context = row.Context,
                    State = SegmentState.Proposed,
                    Warning = row.WarningCode,
                    RiskSeverity = row.RiskSeverity,
                    EffectiveModel = row.EffectiveModel,
                    Escalated = row.Escalated
                });
            EffectiveModelSummary = result.Value.EffectiveModelSummary is null
                ? $"Modo {result.Value.RequestedMode ?? "legado"}; modelo efectivo no informado."
                : $"Modelo efectivo: {result.Value.EffectiveModelSummary}";
            EscalatedSegmentCount = result.Value.EscalatedSegmentCount;
            Status = "Revisa, edita y aprueba o excluye cada segmento antes de generar el DWG.";
            SetProgress(55, false, "Paso 2 de 4 · Traducción lista. Completa la revisión humana.");
        }
        catch (OperationCanceledException)
        {
            Status = "La operación fue cancelada. El archivo original permanece intacto.";
            SetProgress(0, false, "Trabajo cancelado en un límite seguro.");
        }
        catch (Exception)
        {
            Status = "No se pudo continuar (DESKTOP_UNEXPECTED_FAILURE). El archivo original permanece intacto.";
            SetProgress(0, false, "Trabajo detenido por un error inesperado.");
        }
        finally
        {
            IsBusy = false;
            NotifyReviewState();
        }
    }

    public async Task SaveOpenAiSettingsAsync(char[] apiKey, char[] confirmation, CancellationToken cancellationToken = default)
    {
        if (!CanManageOpenAiSettings)
        {
            Array.Clear(apiKey);
            Array.Clear(confirmation);
            return;
        }
        IsBusy = true;
        OpenAiCredentialStatus = "Guardando la clave en Windows Credential Manager…";
        try
        {
            var result = await _openAiSettings!.SaveAsync(apiKey, confirmation, SelectedOpenAiModel, cancellationToken);
            _openAiCredentialReady = false;
            _openAiUsageConfirmed = false;
            if (!result.IsSuccess)
            {
                OpenAiCredentialStatus = OpenAiSettingsError(result.Error!);
                return;
            }

            OpenAiCredentialStatus = "Clave guardada. Consultando modelos disponibles…";
            var discovery = await _openAiSettings.DiscoverModelsAsync(cancellationToken);
            if (!discovery.IsSuccess)
            {
                OpenAiCredentialStatus = OpenAiSettingsError(discovery.Error!);
                return;
            }

            OpenAiModelOptions.Clear();
            foreach (var availableModel in discovery.Value!.Models) OpenAiModelOptions.Add(availableModel);
            OpenAiTierAccessSummary = TierAccessSummary(discovery.Value);
            SelectedOpenAiModel = discovery.Value.RecommendedModel;
            OpenAiCredentialStatus = $"Clave guardada. Validando {OpenAiTestModel}…";
            var tested = await _openAiSettings.TestAsync(OpenAiTestModel, cancellationToken);
            _openAiCredentialReady = tested.IsSuccess;
            OpenAiCredentialStatus = tested.IsSuccess
                ? $"Conexión y Structured Outputs válidos. Modelo activo: {_openAiSettings.Model}.{OpenAiDiagnosticSuffix()} Solo falta autorizar el uso para este trabajo."
                : OpenAiSettingsError(tested.Error!) + OpenAiDiagnosticSuffix();
        }
        catch (OperationCanceledException) { OpenAiCredentialStatus = "El guardado de la clave fue cancelado."; }
        catch (Exception) { OpenAiCredentialStatus = "No se pudo guardar la clave (OPENAI_SETTINGS_UNEXPECTED_FAILURE)."; }
        finally
        {
            Array.Clear(apiKey);
            Array.Clear(confirmation);
            IsBusy = false;
            NotifyOpenAiState();
        }
    }

    public async Task TestOpenAiConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!CanTestOpenAiConnection) return;
        IsBusy = true;
        OpenAiCredentialStatus = "Probando credencial y acceso al modelo seleccionado…";
        try
        {
            var result = await _openAiSettings!.TestAsync(OpenAiTestModel, cancellationToken);
            _openAiCredentialReady = result.IsSuccess;
            _openAiUsageConfirmed = false;
            OpenAiCredentialStatus = result.IsSuccess
                ? $"Conexión y Structured Outputs válidos. Modelo activo: {_openAiSettings.Model}.{OpenAiDiagnosticSuffix()}"
                : OpenAiSettingsError(result.Error!) + OpenAiDiagnosticSuffix();
        }
        catch (OperationCanceledException) { _openAiCredentialReady = false; OpenAiCredentialStatus = "La prueba de conexión fue cancelada."; }
        catch (Exception) { _openAiCredentialReady = false; OpenAiCredentialStatus = "No se pudo probar la conexión (OPENAI_SETTINGS_UNEXPECTED_FAILURE)."; }
        finally { IsBusy = false; NotifyOpenAiState(); }
    }

    public async Task DiscoverOpenAiModelsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanDiscoverOpenAiModels) return;
        IsBusy = true;
        OpenAiCredentialStatus = "Consultando los modelos disponibles para esta cuenta…";
        try
        {
            var result = await _openAiSettings!.DiscoverModelsAsync(cancellationToken);
            _openAiCredentialReady = false;
            _openAiUsageConfirmed = false;
            if (!result.IsSuccess)
            {
                OpenAiCredentialStatus = OpenAiSettingsError(result.Error!);
                return;
            }

            OpenAiModelOptions.Clear();
            foreach (var model in result.Value!.Models) OpenAiModelOptions.Add(model);
            OpenAiTierAccessSummary = TierAccessSummary(result.Value);
            SelectedOpenAiModel = result.Value.RecommendedModel;
            OpenAiCredentialStatus = $"{result.Value.Models.Count} modelos de texto disponibles. Seleccionamos {result.Value.RecommendedModel}; prueba la conexión para activarlo.";
        }
        catch (OperationCanceledException) { OpenAiCredentialStatus = "La consulta de modelos fue cancelada."; }
        catch (Exception) { OpenAiCredentialStatus = "No se pudieron consultar los modelos (OPENAI_SETTINGS_UNEXPECTED_FAILURE)."; }
        finally { IsBusy = false; NotifyOpenAiState(); }
    }

    public async Task GenerateAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGenerate)
        {
            if (CanAttemptGenerate)
                Status = $"Antes de generar: aprueba o excluye los {PendingCount} segmentos pendientes. El original permanece intacto.";
            return;
        }
        IsBusy = true;
        Status = "Generando y validando la copia traducida con AutoCAD…";
        SetProgress(80, true, "Paso 3 de 4 · AutoCAD escribe un candidato, lo reabre y valida antes de promoverlo.");
        try
        {
            var decisions = ReviewRows.Select(row => new ReviewDecisionInput(
                row.SegmentId,
                row.State == SegmentState.Approved ? row.FinalText : null,
                row.State == SegmentState.Excluded ? row.ExclusionReason : null)).ToArray();
            var result = await _workflow.CompleteAsync(_currentJobId!.Value, decisions, cancellationToken);
            if (result.IsSuccess)
            {
                ApplyCompletion(_currentJobId!.Value, result.Value!);
            }
            else
            {
                _currentJobId = null;
                Status = SafeError(result.Error!) + " Pulsa “Nuevo trabajo” para reintentar.";
                SetProgress(0, false, $"Trabajo detenido durante escritura o validación ({result.Error!.Code}).");
            }
        }
        catch (OperationCanceledException)
        {
            _currentJobId = null;
            Status = "La operación fue cancelada en un punto seguro. El archivo original permanece intacto.";
            SetProgress(0, false, "Generación cancelada. Inicia un nuevo trabajo para reintentar.");
        }
        catch (Exception)
        {
            _currentJobId = null;
            Status = "No se pudo continuar (DESKTOP_UNEXPECTED_FAILURE). El archivo original permanece intacto.";
            SetProgress(0, false, "Trabajo detenido durante escritura o validación.");
        }
        finally
        {
            IsBusy = false;
            NotifyReviewState();
        }
    }

    public async Task OpenVisualReviewAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOpenVisualReview) return;
        IsBusy = true;
        Status = "Abriendo la copia validada en AutoCAD para inspección visual…";
        try
        {
            var result = await _visualReview!.OpenAsync(_completedJobId!.Value, _completedOutputPath!, _completedOutputHash!, cancellationToken);
            if (!result.IsSuccess) { Status = SafeError(result.Error!); return; }
            _visualReviewSessionId = result.Value!.SessionId;
            Status = "Revisa visualmente textos, ubicación, saltos, estilos y límites en AutoCAD; después registra PASS o FAIL.";
        }
        catch (OperationCanceledException) { Status = "La apertura para revisión visual fue cancelada."; }
        catch (Exception) { Status = "No se pudo abrir la revisión visual (DESKTOP_UNEXPECTED_FAILURE)."; }
        finally { IsBusy = false; NotifyReviewState(); }
    }

    public async Task RecordVisualReviewAsync(VisualReviewDecision decision, CancellationToken cancellationToken = default)
    {
        if (!CanRecordVisualReview) return;
        IsBusy = true;
        Status = "Registrando la decisión visual ligada al DWG validado…";
        try
        {
            var result = await _visualReview!.RecordAsync(_visualReviewSessionId!.Value, decision, cancellationToken);
            if (!result.IsSuccess) { Status = SafeError(result.Error!); _visualReviewSessionId = null; return; }
            _visualReviewSessionId = null;
            _visualReviewDecision = decision.ToString().ToUpperInvariant();
            Status = decision == VisualReviewDecision.Pass
                ? "Ejercicio aceptado: validación automática PASS y revisión visual PASS registradas."
                : "Revisión visual FAIL registrada. La copia no debe considerarse aceptada; el original permanece intacto.";
        }
        catch (OperationCanceledException) { Status = "El registro de revisión visual fue cancelado."; }
        catch (Exception) { Status = "No se pudo registrar la revisión visual (DESKTOP_UNEXPECTED_FAILURE)."; }
        finally { IsBusy = false; NotifyReviewState(); }
    }

    public void ApproveVisible()
    {
        var visibleRows = ReviewView.Cast<ReviewRowViewModel>()
            .Where(row => row.RiskSeverity != "high")
            .ToArray();
        foreach (var row in visibleRows)
            row.Approve();
        NotifyReviewState();
    }

    public void Approve(IEnumerable<ReviewRowViewModel> rows)
    {
        foreach (var row in rows.ToArray()) row.Approve();
        NotifyReviewState();
    }

    public void RestoreProposal(IEnumerable<ReviewRowViewModel> rows)
    {
        foreach (var row in rows.ToArray()) row.RestoreProposal();
        NotifyReviewState();
    }

    public void Exclude(IEnumerable<ReviewRowViewModel> rows)
    {
        foreach (var row in rows.ToArray())
            row.Exclude(SelectedExclusionReason);
        NotifyReviewState();
    }

    public void Reset()
    {
        if (IsBusy) return;
        _currentJobId = null;
        _completedJobId = null;
        _completedOutputPath = null;
        _completedOutputHash = null;
        _visualReviewSessionId = null;
        _visualReviewDecision = null;
        QualitySummary = "Las métricas estarán disponibles después de completar una revisión.";
        _openAiUsageConfirmed = false;
        IsCompleted = false;
        ReviewRows.Clear();
        ValidationRows.Clear();
        SelectedResultsTab = 0;
        GlossaryEntries.Clear();
        SourceFile = null;
        OutputFile = null;
        SearchText = string.Empty;
        SelectedReviewFilter = ReviewFilters[0];
        Status = _servicesConfigured ? "Selecciona un DWG y el idioma de destino." : "AutoCAD y OpenAI todavía no están configurados; no se abrirá ningún archivo.";
        SetProgress(0, false, "Listo para iniciar un trabajo.");
        NotifyCommandState();
    }

    public void ReportCancellationRequested()
    {
        if (!IsBusy) return;
        Status = "Cancelación solicitada. La operación se detendrá en el siguiente límite seguro.";
        ProgressDetail = "Esperando que AutoCAD u OpenAI alcance un límite seguro de cancelación…";
    }

    public async Task RefreshJobsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefreshJobs) return;
        IsBusy = true;
        Status = "Consultando trabajos locales…";
        try
        {
            var result = await _jobAdministration!.ListAsync(cancellationToken);
            if (!result.IsSuccess) { Status = SafeError(result.Error!); return; }
            var selectedId = SelectedJob?.JobId;
            Jobs.Clear();
            foreach (var job in result.Value!) Jobs.Add(job);
            SelectedJob = Jobs.FirstOrDefault(job => job.JobId == selectedId) ?? Jobs.FirstOrDefault();
            Status = $"{Jobs.Count} trabajos locales encontrados. Selecciona uno para revisar su estado o recuperarlo.";
        }
        catch (OperationCanceledException) { Status = "La consulta de trabajos fue cancelada."; }
        finally { IsBusy = false; NotifyCommandState(); }
    }

    public async Task ResumeSelectedJobAsync(CancellationToken cancellationToken = default)
    {
        if (!CanResumeSelectedJob) return;
        var selected = SelectedJob!;
        IsBusy = true;
        Status = $"Recuperando el trabajo {selected.JobId:D} desde un estado seguro…";
        SetProgress(75, true, "Validando fuente, revisión y checkpoint antes de reanudar.");
        try
        {
            var result = await _jobAdministration!.ResumeAsync(selected.JobId, cancellationToken);
            if (!result.IsSuccess)
            {
                Status = SafeError(result.Error!);
                SetProgress(0, false, $"Recuperación detenida ({result.Error!.Code}).");
                return;
            }
            if (result.Value!.Review is not null)
            {
                _sourceFile = selected.SourcePath;
                _outputFile = selected.OutputPath;
                Notify(nameof(SourceFile));
                Notify(nameof(OutputFile));
                _currentJobId = selected.JobId;
                _completedJobId = null;
                IsCompleted = false;
                ReviewRows.Clear();
                ValidationRows.Clear();
                foreach (var row in result.Value.Review.Rows) ReviewRows.Add(ToReviewRow(row));
                SelectedResultsTab = 0;
                Status = "Revisión recuperada. Confirma, edita y vuelve a aprobar o excluir cada segmento.";
                SetProgress(55, false, "Paso 2 de 4 · Revisión recuperada desde almacenamiento local.");
            }
            else if (result.Value.Completion is not null)
            {
                _sourceFile = selected.SourcePath;
                _outputFile = selected.OutputPath;
                Notify(nameof(SourceFile));
                Notify(nameof(OutputFile));
                ApplyCompletion(selected.JobId, result.Value.Completion);
            }
            await RefreshJobsAfterOperationAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Status = "La recuperación fue cancelada en un límite seguro.";
            SetProgress(0, false, "Recuperación cancelada.");
        }
        finally { IsBusy = false; NotifyReviewState(); NotifyCommandState(); }
    }

    public Task<Result<DesktopRetentionPreview>> PreviewRetentionAsync(CancellationToken cancellationToken = default) =>
        _jobAdministration is null
            ? Task.FromResult(Results.Failure<DesktopRetentionPreview>(new ContractError("JOB_ADMINISTRATION_UNAVAILABLE", ErrorCategory.Configuration, "Job administration is unavailable.", false)))
            : _jobAdministration.PreviewRetentionAsync(RetentionDays, cancellationToken);

    public async Task DeleteExpiredJobsAsync(bool explicitlyConfirmed, CancellationToken cancellationToken = default)
    {
        if (_jobAdministration is null || IsBusy) return;
        IsBusy = true;
        Status = "Eliminando únicamente datos locales expirados y sin archivos DWG…";
        try
        {
            var result = await _jobAdministration.DeleteExpiredAsync(RetentionDays, explicitlyConfirmed, cancellationToken);
            Status = result.IsSuccess
                ? $"Retención completada: {result.Value} trabajos locales eliminados. Ningún DWG fue eliminado."
                : SafeError(result.Error!);
            await RefreshJobsAfterOperationAsync(cancellationToken);
        }
        catch (OperationCanceledException) { Status = "La limpieza de trabajos fue cancelada."; }
        finally { IsBusy = false; NotifyCommandState(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal void ReportStartupFailure(string code) =>
        Status = $"fail-closed — la configuración productiva no pudo activarse ({code}). Puedes configurar OpenAI, pero no se abrirá ningún DWG.";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void ReviewRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ReviewRowViewModel row in e.OldItems) row.PropertyChanged -= ReviewRowChanged;
        if (e.NewItems is not null)
            foreach (ReviewRowViewModel row in e.NewItems) row.PropertyChanged += ReviewRowChanged;
        NotifyReviewState();
    }
    private void ReviewRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        _reviewView?.Refresh();
        NotifyReviewState();
    }
    private void NotifyReviewState()
    {
        UpdateReviewProgress();
        Notify(nameof(HasReviewRows));
        Notify(nameof(HasValidationRows));
        Notify(nameof(HasVisibleRows));
        Notify(nameof(CanReview));
        Notify(nameof(CanCancel));
        Notify(nameof(CanRefreshJobs));
        Notify(nameof(CanResumeSelectedJob));
        Notify(nameof(TotalCount));
        Notify(nameof(PendingCount));
        Notify(nameof(ApprovedCount));
        Notify(nameof(ExcludedCount));
        Notify(nameof(WarningCount));
        Notify(nameof(ReviewCoverageComplete));
        Notify(nameof(CanGenerate));
        Notify(nameof(CanAttemptGenerate));
        Notify(nameof(GenerationReadinessSummary));
        Notify(nameof(CanConfigure));
        Notify(nameof(CanAttemptTranslation));
        Notify(nameof(TranslationReadinessSummary));
        Notify(nameof(CanEditGlossary));
        Notify(nameof(CanReset));
        Notify(nameof(CanReview));
        Notify(nameof(CanCancel));
        Notify(nameof(CanRefreshJobs));
        Notify(nameof(CanResumeSelectedJob));
        Notify(nameof(ProgressSummary));
        Notify(nameof(CoverageSummary));
        Notify(nameof(CurrentStage));
        Notify(nameof(CanOpenVisualReview));
        Notify(nameof(CanRecordVisualReview));
        Notify(nameof(VisualReviewSummary));
    }

    private void NotifyCommandState()
    {
        Notify(nameof(CanCancel));
        Notify(nameof(CanRefreshJobs));
        Notify(nameof(CanResumeSelectedJob));
        Notify(nameof(CanStartTranslation));
        Notify(nameof(CanAttemptTranslation));
        Notify(nameof(TranslationReadinessSummary));
        Notify(nameof(CanGenerate));
        Notify(nameof(CanAttemptGenerate));
        Notify(nameof(GenerationReadinessSummary));
        Notify(nameof(CanConfigure));
        Notify(nameof(CanEditGlossary));
        Notify(nameof(CanReset));
        Notify(nameof(ProgressSummary));
        Notify(nameof(CanOpenVisualReview));
        Notify(nameof(CanRecordVisualReview));
        Notify(nameof(VisualReviewSummary));
        Notify(nameof(CanManageOpenAiSettings));
        Notify(nameof(CanDiscoverOpenAiModels));
        Notify(nameof(CanTestOpenAiConnection));
        Notify(nameof(OpenAiCredentialReady));
    }

    private void ConfigurationChanged()
    {
        if (_currentJobId.HasValue || HasReviewRows)
        {
            _currentJobId = null;
            IsCompleted = false;
            ReviewRows.Clear();
            Status = "La configuración cambió. Inicia una nueva extracción para evitar usar una revisión anterior.";
        }
        NotifyCommandState();
    }

    private void UpdateReviewProgress()
    {
        if (IsBusy || !HasReviewRows || !_currentJobId.HasValue) return;
        var reviewed = ApprovedCount + ExcludedCount;
        ProgressValue = 55 + 20d * reviewed / TotalCount;
        ProgressDetail = ReviewCoverageComplete
            ? "Paso 2 de 4 · Revisión completa. Ya puedes generar la copia DWG."
            : $"Paso 2 de 4 · Revisión humana: {reviewed} de {TotalCount} segmentos decididos.";
        ProgressIsIndeterminate = false;
    }

    private void SetProgress(double value, bool indeterminate, string detail)
    {
        ProgressValue = Math.Clamp(value, 0, 100);
        ProgressIsIndeterminate = indeterminate;
        ProgressDetail = detail;
    }

    private void WorkflowProgressChanged(object? sender, DesktopProgressUpdate update)
    {
        if (!IsBusy) return;
        SetProgress(update.Percentage, update.IsIndeterminate, $"{update.Stage} · {update.Detail}");
    }

    private void NotifyOpenAiState()
    {
        Notify(nameof(SelectedOpenAiModel));
        Notify(nameof(SelectedRoutingMode));
        Notify(nameof(RoutingBehaviorSummary));
        Notify(nameof(OpenAiCredentialStatus));
        Notify(nameof(OpenAiCredentialReady));
        Notify(nameof(OpenAiUsageConfirmed));
        Notify(nameof(CanManageOpenAiSettings));
        Notify(nameof(CanDiscoverOpenAiModels));
        Notify(nameof(CanTestOpenAiConnection));
        Notify(nameof(CanStartTranslation));
        Notify(nameof(CanAttemptTranslation));
        Notify(nameof(TranslationReadinessSummary));
    }

    private static bool ValidOpenAiModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Length <= 120 && !model.Any(char.IsWhiteSpace) && model != "gpt-5.6";

    private string OpenAiTestModel => SelectedRoutingMode?.Value switch
    {
        TranslationRouting.Auto => TranslationRouting.Terra,
        TranslationRouting.Economy => TranslationRouting.Luna,
        TranslationRouting.MaximumQuality => TranslationRouting.Sol,
        _ => SelectedOpenAiModel
    };

    private static string TierAccessSummary(OpenAiModelDiscovery discovery)
    {
        if (discovery.TierAccess is null) return "Acceso por tier no informado por esta configuración.";
        static string State(IReadOnlyDictionary<string, bool> values, string model) => values.TryGetValue(model, out var allowed) && allowed ? "disponible" : "sin acceso";
        return $"Terra: {State(discovery.TierAccess, TranslationRouting.Terra)} · Luna: {State(discovery.TierAccess, TranslationRouting.Luna)} · Sol: {State(discovery.TierAccess, TranslationRouting.Sol)}";
    }

    private static string OpenAiSettingsError(ContractError error) => error.Code switch
    {
        "OPENAI_CREDENTIAL_CONFIRMATION_INVALID" => "Las claves no coinciden o el formato no es válido.",
        "OPENAI_CREDENTIAL_UNAVAILABLE" => "No hay una clave guardada para este usuario de Windows.",
        "OPENAI_AUTHENTICATION_FAILED" => "OpenAI rechazó la clave guardada.",
        "OPENAI_MODEL_UNAVAILABLE" => "La cuenta no tiene acceso al modelo seleccionado.",
        "OPENAI_MODEL_CAPABILITY_UNSUPPORTED" => "El modelo existe, pero no admite la combinación Responses API y Structured Outputs requerida.",
        "OPENAI_CAPABILITY_RESPONSE_INVALID" => "El modelo no completó correctamente la prueba estructurada requerida.",
        "OPENAI_NO_TRANSLATION_MODELS" => "La cuenta no reportó modelos de texto compatibles para traducción.",
        "OPENAI_MODELS_RESPONSE_INVALID" or "OPENAI_MODELS_RESPONSE_TOO_LARGE" => "OpenAI devolvió una lista de modelos que no se pudo validar.",
        "OPENAI_MODEL_DISCOVERY_FAILED" => "OpenAI rechazó la consulta de modelos disponibles.",
        "OPENAI_RATE_LIMITED" => "OpenAI limitó temporalmente la prueba. Intenta de nuevo más tarde.",
        "OPENAI_TIMEOUT" or "OPENAI_UNAVAILABLE" => "No fue posible contactar OpenAI. Revisa internet e intenta nuevamente.",
        "OPENAI_MODEL_INVALID" => "Escribe un identificador de modelo válido.",
        _ => $"No se pudo completar la configuración ({error.Code})."
    };

    private string OpenAiDiagnosticSuffix() => string.IsNullOrWhiteSpace(_openAiSettings?.LastRequestId)
        ? string.Empty
        : $" ID de diagnóstico: {_openAiSettings.LastRequestId}.";

    public void AddGlossaryEntry()
    {
        if (!CanEditGlossary) return;
        GlossaryEntries.Add(new GlossaryEntryViewModel());
        Status = "Completa el término original y su traducción. El glosario se aplicará a todo el plano.";
    }

    public void RemoveGlossaryEntries(IEnumerable<GlossaryEntryViewModel> entries)
    {
        if (!CanEditGlossary) return;
        foreach (var entry in entries.ToArray()) GlossaryEntries.Remove(entry);
    }

    private void GlossaryEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (GlossaryEntryViewModel entry in e.OldItems) entry.PropertyChanged -= GlossaryEntryChanged;
        if (e.NewItems is not null)
            foreach (GlossaryEntryViewModel entry in e.NewItems) entry.PropertyChanged += GlossaryEntryChanged;
        ConfigurationChanged();
    }

    private void GlossaryEntryChanged(object? sender, PropertyChangedEventArgs e) => ConfigurationChanged();

    private bool GlossaryIsValid()
    {
        if (GlossaryEntries.Any(entry => string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target) ||
            entry.Source.Trim().Length > 500 || entry.Target.Trim().Length > 500)) return false;
        return GlossaryEntries.Select(entry => entry.Source.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == GlossaryEntries.Count;
    }

    private bool TranslationFilesAndGlossaryAreReady() =>
        _servicesConfigured && CanConfigure && !HasReviewRows && GlossaryIsValid() &&
        !string.IsNullOrWhiteSpace(SourceFile) && !string.IsNullOrWhiteSpace(OutputFile) && !SamePath(SourceFile, OutputFile);

    private bool FilterReviewRow(object item)
    {
        if (item is not ReviewRowViewModel row) return false;
        var stateMatch = SelectedReviewFilter.Id switch
        {
            "pending" => row.State is not (SegmentState.Approved or SegmentState.Excluded),
            "approved" => row.State == SegmentState.Approved,
            "excluded" => row.State == SegmentState.Excluded,
            "warning" => !string.IsNullOrWhiteSpace(row.Warning),
            _ => true
        };
        if (!stateMatch || string.IsNullOrWhiteSpace(SearchText)) return stateMatch;
        return row.OriginalText.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
            row.ProposedText.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
            row.FinalText.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
            row.Context.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshReviewView()
    {
        ReviewView.Refresh();
        Notify(nameof(HasVisibleRows));
    }

    private ICollectionView CreateReviewView()
    {
        var view = CollectionViewSource.GetDefaultView(ReviewRows);
        view.Filter = FilterReviewRow;
        return view;
    }

    private static bool SamePath(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
    }

    private static string SafeError(DwgTranslator.Contracts.ContractError error) => error.Code switch
    {
        "IPC_TIMEOUT" => "AutoCAD no alcanzó a conectar con el adaptador local (IPC_TIMEOUT). El archivo original permanece intacto.",
        "IPC_PEER_DISCONNECTED" => "AutoCAD cerró el canal antes de terminar (IPC_PEER_DISCONNECTED). El archivo original permanece intacto.",
        "CAD_BUSY" => "AutoCAD ya está abierto o ocupado (CAD_BUSY). Ciérralo antes de iniciar otro trabajo.",
        "CANDIDATE_VALIDATION_FAILED" or "WRITE_VALIDATION_FAILED" => $"La copia candidata no superó la validación estricta ({error.Code}). El original permanece intacto.",
        "DOCUMENT_LOCKED" or "CAD_FILE_IO_FAILED" or "CAD_FILE_ACCESS_DENIED" => $"AutoCAD no pudo escribir en la ruta seleccionada ({error.Code}). El original permanece intacto.",
        "JOB_STATE_CONFLICT" => "El trabajo anterior ya terminó o falló (JOB_STATE_CONFLICT). Inicia un nuevo trabajo.",
        "RECOVERY_CANDIDATE_REQUIRES_INSPECTION" => "Existe una copia candidata de una ejecución anterior. Debe inspeccionarse antes de reintentar para no perder evidencia.",
        "RECOVERY_SOURCE_CHANGED" => "El DWG original cambió desde la aprobación; no es seguro recuperar este trabajo.",
        "RECOVERY_JOB_NOT_RETRYABLE" or "RECOVERY_CHECKPOINT_MISMATCH" or "RECOVERY_APPROVED_CHECKPOINT_MISSING" => $"El trabajo no tiene un punto seguro de recuperación ({error.Code}).",
        "RETENTION_TREE_CHANGED" => "Los datos locales cambiaron después de la vista previa; la limpieza fue cancelada.",
        _ => $"No se pudo continuar ({error.Code}). El archivo original permanece intacto."
    };

    private static ReviewRowViewModel ToReviewRow(DesktopReviewRow row) => new()
    {
        SegmentId = row.SegmentId,
        OriginalText = row.OriginalText,
        ProposedText = row.ProposedText,
        FinalText = row.FinalText,
        Context = row.Context,
        State = SegmentState.Proposed,
        Warning = row.WarningCode
    };

    private void ApplyCompletion(Guid jobId, DesktopCompletion completion)
    {
        _completedJobId = jobId;
        _completedOutputPath = completion.OutputPath;
        _completedOutputHash = completion.OutputHash;
        _currentJobId = null;
        IsCompleted = true;
        UpdateQualitySummary(jobId);
        ValidationRows.Clear();
        if (completion.ValidationReport is not null)
        {
            foreach (var entity in completion.ValidationReport.Entities)
                ValidationRows.Add(new ValidationRowViewModel(
                    entity.Handle, entity.EntityType,
                    entity.Space == "PaperSpace" ? $"{entity.Layout} · {entity.Layer}" : $"ModelSpace · {entity.Layer}",
                    $"{entity.PreservedPropertyCount}/{entity.PreservedPropertyCount}",
                    entity.BoundsChanged ? "Cambió — revisar" : "Sin cambio",
                    "PASS automático · revisión visual pendiente"));
            SelectedResultsTab = 1;
        }
        Status = completion.ValidationReport is null
            ? $"Archivo validado y guardado en: {completion.OutputPath}"
            : $"Validación automática PASS ({completion.ValidationReport.EntityCount} textos; {completion.ValidationReport.BoundsChangedCount} cambiaron límites). Revisión visual pendiente. Guardado en: {completion.OutputPath}";
        SetProgress(100, false, "Paso 4 de 4 · Copia guardada y validada. Revisión visual pendiente.");
    }

    private void UpdateQualitySummary(Guid jobId)
    {
        if (ReviewRows.Count == 0)
        {
            QualitySummary = "Trabajo recuperado sin la revisión cargada; consulta su snapshot para métricas lingüísticas.";
            return;
        }
        var snapshot = new TranslationReviewSnapshot(
            jobId, 0, SelectedLanguage.Tag, DwgTranslator.Translation.OpenAI.OpenAITranslationGateway.SupportedPromptTemplateVersion,
            1, 1, DateTimeOffset.UtcNow,
            ReviewRows.Select(row => new ReviewRowSnapshot(
                row.SegmentId, row.OriginalText, row.ProposedText, row.FinalText, row.State, row.ExclusionReason, row.Warning)).ToArray());
        var glossary = GlossaryEntries.Select(entry => new TranslationGlossaryEntry
        {
            Source = entry.Source,
            Target = entry.Target,
            CaseSensitive = entry.CaseSensitive
        }).ToArray();
        var metrics = TranslationQualityEvaluator.Evaluate(snapshot, glossary);
        QualitySummary = metrics.IsSuccess
            ? $"Calidad: {metrics.Value!.AcceptedWithoutEdit}/{metrics.Value.ApprovedSegments} aceptados sin edición ({metrics.Value.AcceptanceWithoutEditRate:P0}); " +
              $"saltos preservados {metrics.Value.LineBreaksPreserved}/{metrics.Value.ApprovedSegments}; glosario {metrics.Value.GlossaryCompliant}/{metrics.Value.ApprovedSegments}; " +
              $"expansión máxima {metrics.Value.MaximumExpansionRatio:N2}×."
            : $"Métricas lingüísticas no disponibles ({metrics.Error!.Code}).";
    }

    private async Task RefreshJobsAfterOperationAsync(CancellationToken cancellationToken)
    {
        if (_jobAdministration is null) return;
        var result = await _jobAdministration.ListAsync(cancellationToken);
        if (!result.IsSuccess) return;
        var selectedId = SelectedJob?.JobId;
        Jobs.Clear();
        foreach (var job in result.Value!) Jobs.Add(job);
        SelectedJob = Jobs.FirstOrDefault(job => job.JobId == selectedId) ?? Jobs.FirstOrDefault();
    }
}

public sealed record LanguageOption(string Tag, string DisplayName);
public sealed record ReviewFilterOption(string Id, string DisplayName);
public sealed record ValidationRowViewModel(
    string Handle,
    string EntityType,
    string Location,
    string PreservedProperties,
    string BoundsStatus,
    string Result);

public sealed class GlossaryEntryViewModel : INotifyPropertyChanged
{
    private string _source = string.Empty;
    private string _target = string.Empty;
    private bool _caseSensitive;

    public string Source { get => _source; set => Set(ref _source, value ?? string.Empty); }
    public string Target { get => _target; set => Set(ref _target, value ?? string.Empty); }
    public bool CaseSensitive { get => _caseSensitive; set => Set(ref _caseSensitive, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class ReviewRowViewModel : INotifyPropertyChanged
{
    private string _finalText = string.Empty;
    private SegmentState _state;
    public string SegmentId { get; init; } = string.Empty;
    public required string OriginalText { get; init; }
    public required string ProposedText { get; init; }
    public required string FinalText { get => _finalText; set { if (_finalText == value) return; _finalText = value; if (_state == SegmentState.Approved) State = SegmentState.Edited; Notify(); } }
    public required string Context { get; init; }
    public SegmentState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Notify();
        }
    }
    public string? Warning { get; init; }
    public string RiskSeverity { get; init; } = "none";
    public string? EffectiveModel { get; init; }
    public bool Escalated { get; init; }
    public string? ExclusionReason { get; private set; }

    public bool Approve()
    {
        if (string.IsNullOrEmpty(FinalText) || RiskSeverity == "high") return false;
        ExclusionReason = null;
        State = SegmentState.Approved;
        Notify(nameof(ExclusionReason));
        return true;
    }

    public void Exclude(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("An exclusion reason is required.", nameof(reason));
        ExclusionReason = reason;
        State = SegmentState.Excluded;
        Notify(nameof(ExclusionReason));
    }

    public void RestoreProposal()
    {
        FinalText = ProposedText;
        ExclusionReason = null;
        State = SegmentState.Proposed;
        Notify(nameof(ExclusionReason));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
