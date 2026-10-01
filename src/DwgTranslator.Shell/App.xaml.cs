using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DwgTranslator.Shell;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep the desktop shell visible on Windows hosts where WPF's hardware path presents a blank surface.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
        var viewModel = ProductionBootstrap.CreateStartupViewModel();
        StartupPrefill.Apply(viewModel, e.Args);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
    }
}
