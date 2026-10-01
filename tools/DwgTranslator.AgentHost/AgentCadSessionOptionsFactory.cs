using DwgTranslator.Agent;
using DwgTranslator.Infrastructure.Windows;

namespace DwgTranslator.AgentHost;

public static class AgentCadSessionOptionsFactory
{
    public static AutoCadProcessSessionOptions CreateReadOnly(AgentBetaConfiguration configuration, string evidenceLeaf)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceLeaf);

        var evidenceRoot = Path.Combine(configuration.LogRoot, "cad-bootstrap", evidenceLeaf);
        var readBootstrap = new CadNetloadBootstrapOptions(
            configuration.ReadOnlyAdapterAssemblyPath,
            Path.Combine(evidenceRoot, "read-only"));
        var writeBootstrap = new CadNetloadBootstrapOptions(
            configuration.WriteAdapterAssemblyPath,
            Path.Combine(evidenceRoot, "write"));

        Directory.CreateDirectory(readBootstrap.EvidenceDirectory);
        Directory.CreateDirectory(writeBootstrap.EvidenceDirectory);

        return new AutoCadProcessSessionOptions(
            configuration.AutoCadExecutablePath,
            configuration.ReadOnlyBundleDirectory,
            configuration.WriteBundleDirectory,
            TimeSpan.FromSeconds(configuration.CadStartupSeconds),
            TimeSpan.FromSeconds(configuration.CadExchangeSeconds),
            TimeSpan.FromSeconds(configuration.CadCloseSeconds),
            readBootstrap,
            writeBootstrap,
            TimeSpan.FromSeconds(configuration.CadCleanupGraceSeconds));
    }
}
