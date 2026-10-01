using System.Windows;
using Microsoft.Win32;

namespace DwgTranslator.Shell;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _operationCancellation;

    public MainWindow() => InitializeComponent();

    private void SelectSource_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFileDialog { Filter = "AutoCAD Drawing (*.dwg)|*.dwg", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) viewModel.SourceFile = dialog.FileName;
    }

    private void SelectOutput_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new SaveFileDialog { Filter = "AutoCAD Drawing (*.dwg)|*.dwg", AddExtension = true, DefaultExt = ".dwg", OverwritePrompt = false };
        if (dialog.ShowDialog(this) == true) viewModel.OutputFile = dialog.FileName;
    }

    private void ApproveVisible_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.ApproveVisible();
    }

    private void ExcludeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.Exclude(ReviewGrid.SelectedItems.Cast<ReviewRowViewModel>());
    }

    private void ApproveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.Approve(ReviewGrid.SelectedItems.Cast<ReviewRowViewModel>());
    }

    private void RestoreProposal_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.RestoreProposal(ReviewGrid.SelectedItems.Cast<ReviewRowViewModel>());
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.Reset();
    }

    private void AddGlossary_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.AddGlossaryEntry();
    }

    private void RemoveGlossary_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.RemoveGlossaryEntries(GlossaryGrid.SelectedItems.Cast<GlossaryEntryViewModel>());
    }

    private async void StartTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await RunOperationAsync(viewModel.StartTranslationAsync);
    }

    private async void SaveOpenAiSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var apiKey = OpenAiApiKeyBox.Password.ToCharArray();
        var confirmation = OpenAiApiKeyConfirmationBox.Password.ToCharArray();
        OpenAiApiKeyBox.Clear();
        OpenAiApiKeyConfirmationBox.Clear();
        await RunOperationAsync(token => viewModel.SaveOpenAiSettingsAsync(apiKey, confirmation, token));
    }

    private async void TestOpenAiConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await RunOperationAsync(viewModel.TestOpenAiConnectionAsync);
    }

    private async void DiscoverOpenAiModels_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await RunOperationAsync(viewModel.DiscoverOpenAiModelsAsync);
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await RunOperationAsync(viewModel.GenerateAsync);
    }

    private async void OpenVisualReview_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await RunOperationAsync(viewModel.OpenVisualReviewAsync);
    }

    private async void PassVisualReview_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await RunOperationAsync(token => viewModel.RecordVisualReviewAsync(VisualReviewDecision.Pass, token));
    }

    private async void FailVisualReview_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await RunOperationAsync(token => viewModel.RecordVisualReviewAsync(VisualReviewDecision.Fail, token));
    }

    private async void RefreshJobs_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await RunOperationAsync(viewModel.RefreshJobsAsync);
    }

    private async void ResumeJob_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await RunOperationAsync(viewModel.ResumeSelectedJobAsync);
    }

    private async void DeleteExpiredJobs_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var preview = await viewModel.PreviewRetentionAsync();
        if (!preview.IsSuccess)
        {
            MessageBox.Show(this, $"No se pudo preparar la limpieza ({preview.Error!.Code}).", "Retención local", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (preview.Value!.JobIds.Count == 0)
        {
            MessageBox.Show(this, "No hay trabajos terminales elegibles. Ningún archivo fue eliminado.", "Retención local", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var size = preview.Value.Bytes / (1024d * 1024d);
        var confirmation = MessageBox.Show(this,
            $"Se eliminarán {preview.Value.JobIds.Count} trabajos locales con más de {preview.Value.RetentionDays} días ({size:N1} MB).\n\nLas carpetas con archivos DWG están excluidas. Esta operación no se puede deshacer. ¿Continuar?",
            "Confirmar limpieza local", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmation == MessageBoxResult.Yes)
            await RunOperationAsync(token => viewModel.DeleteExpiredJobsAsync(true, token));
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested) return;
        if (DataContext is MainViewModel viewModel) viewModel.ReportCancellationRequested();
        _operationCancellation.Cancel();
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_operationCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        try { await operation(cancellation.Token); }
        finally { _operationCancellation = null; }
    }
}
