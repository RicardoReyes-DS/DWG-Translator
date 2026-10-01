using System.Text;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Infrastructure.Windows;

public static class CadNetloadBootstrapScript
{
    public static bool IsValid(CadNetloadBootstrapOptions options)
    {
        if (options is null || !Path.IsPathFullyQualified(options.AssemblyPath) || !Path.IsPathFullyQualified(options.EvidenceDirectory)) return false;
        var assembly = Path.GetFullPath(options.AssemblyPath);
        var evidence = Path.GetFullPath(options.EvidenceDirectory);
        return File.Exists(assembly) && string.Equals(Path.GetExtension(assembly), ".dll", StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(evidence) && !IsReparsePoint(evidence) && !IsReparsePoint(Path.GetDirectoryName(assembly)!) &&
               IsScriptSafe(assembly) && IsScriptSafe(evidence);
    }

    public static Result<CadNetloadBootstrapArtifact?> Create(CadNetloadBootstrapOptions options)
    {
        if (!IsValid(options))
            return Results.Failure<CadNetloadBootstrapArtifact?>(Error("CAD_BOOTSTRAP_CONFIGURATION_INVALID", "The NETLOAD bootstrap requires an existing assembly and evidence directory without reparse points."));

        var evidence = Path.GetFullPath(options.EvidenceDirectory);
        var id = Guid.NewGuid().ToString("N");
        var artifact = CadNetloadBootstrapEvidencePolicy.Create(options.AssemblyPath, evidence, id);
        if (artifact is null)
            return Results.Failure<CadNetloadBootstrapArtifact?>(Error("CAD_BOOTSTRAP_CONFIGURATION_INVALID", "The NETLOAD bootstrap paths are invalid."));
        try
        {
            using var stream = new FileStream(artifact.ScriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(artifact.ScriptContent);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return Results.Success<CadNetloadBootstrapArtifact?>(new CadNetloadBootstrapArtifact(
                artifact.ScriptPath, artifact.OriginalTrustedPathsPath, artifact.RestoredMarkerPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Failure<CadNetloadBootstrapArtifact?>(Error("CAD_BOOTSTRAP_EVIDENCE_WRITE_FAILED", "The NETLOAD bootstrap evidence could not be created."));
        }
    }

    private static bool IsReparsePoint(string path) => (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
    private static bool IsScriptSafe(string value) => value.IndexOfAny(['\r', '\n', '"', ';']) < 0;
    private static ContractError Error(string code, string message) => new(code, ErrorCategory.Security, message, false);
}
