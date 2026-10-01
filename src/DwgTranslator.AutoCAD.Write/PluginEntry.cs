using Autodesk.AutoCAD.Runtime;
using DwgTranslator.Transport.Core;
using DwgTranslator.Transport.Windows;

namespace DwgTranslator.AutoCAD.Write;

public sealed class PluginEntry : IExtensionApplication, IDisposable
{
    internal const string PipeEnvironmentVariable = "DWT_PHASE4B_PIPE";
    internal const string NonceEnvironmentVariable = "DWT_PHASE4B_NONCE";
    private CancellationTokenSource? _shutdown;

    public void Initialize()
    {
        var pipeName = Environment.GetEnvironmentVariable(PipeEnvironmentVariable);
        var encodedNonce = Environment.GetEnvironmentVariable(NonceEnvironmentVariable);
        Environment.SetEnvironmentVariable(PipeEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(NonceEnvironmentVariable, null);
        if (!LaunchChannelPolicy.IsValidPipeName(pipeName)) return;
        var binding = SessionBinding.ImportFromTrustedLauncher(encodedNonce);
        if (!binding.IsSuccess) return;
        _shutdown = new CancellationTokenSource();
        var server = new WindowsPipeServer(pipeName!, binding.Value!, CadWriteRequestHandler.HandleAsync);
        _ = Task.Run(async () => { await using (server) await server.ServeSessionAsync(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10), _shutdown.Token); }, _shutdown.Token);
    }

    public void Terminate() => Dispose();
    public void Dispose() { _shutdown?.Cancel(); _shutdown?.Dispose(); _shutdown = null; GC.SuppressFinalize(this); }
}

internal static class LaunchChannelPolicy
{
    public static bool IsValidPipeName(string? value) => value is { Length: >= 32 and <= 180 } &&
        value.StartsWith("dwgtranslator-v1-", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-');
}
