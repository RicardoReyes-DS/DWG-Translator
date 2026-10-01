namespace DwgTranslator.Application;

public sealed record CadNetloadBootstrapEvidence(
    string ScriptPath,
    string OriginalTrustedPathsPath,
    string RestoredMarkerPath,
    string ScriptContent);

/// <summary>Owns the byte-exact NETLOAD script representation shared by launch and recovery.</summary>
public static class CadNetloadBootstrapEvidencePolicy
{
    public const string RestoredMarkerContent = "RESTORED\r\n";

    public static CadNetloadBootstrapEvidence? Create(
        string assemblyPath,
        string evidenceDirectory,
        string identifier)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || string.IsNullOrWhiteSpace(evidenceDirectory) ||
            identifier.Length != 32 || identifier.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return null;

        try
        {
            if (!Path.IsPathFullyQualified(assemblyPath) || !Path.IsPathFullyQualified(evidenceDirectory))
                return null;
            var assembly = Path.GetFullPath(assemblyPath);
            var evidence = Path.GetFullPath(evidenceDirectory);
            if (!IsScriptSafe(assembly) || !IsScriptSafe(evidence)) return null;

            var script = Path.Combine(evidence, $"netload-{identifier}.scr");
            var original = Path.Combine(evidence, $"netload-{identifier}.original-trustedpaths.txt");
            var restored = Path.Combine(evidence, $"netload-{identifier}.restored.marker");
            var assemblyCad = ToAutoCadPath(assembly);
            var trustedDirectory = ToAutoCadPath(Path.GetDirectoryName(assembly)!);
            var originalCad = ToAutoCadPath(original);
            var restoredCad = ToAutoCadPath(restored);
            var content =
                $"(if (= (getvar \"SECURELOAD\") 1) (progn " +
                "(setq dwt_original_trustedpaths (getvar \"TRUSTEDPATHS\")) " +
                $"(setq dwt_original_file (open \"{originalCad}\" \"w\")) " +
                "(write-line dwt_original_trustedpaths dwt_original_file) (close dwt_original_file) " +
                $"(setvar \"TRUSTEDPATHS\" (if (= dwt_original_trustedpaths \"\") \"{trustedDirectory}\" (strcat dwt_original_trustedpaths \";{trustedDirectory}\"))) " +
                $"(command \"_.NETLOAD\" \"{assemblyCad}\") " +
                "(setvar \"TRUSTEDPATHS\" dwt_original_trustedpaths) " +
                $"(if (= (getvar \"TRUSTEDPATHS\") dwt_original_trustedpaths) (progn (setq dwt_restored_file (open \"{restoredCad}\" \"w\")) (write-line \"RESTORED\" dwt_restored_file) (close dwt_restored_file)))))\r\n";
            return new(script, original, restored, content);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool IsOriginalSnapshot(string value) =>
        value.Length is >= 2 and <= 32_768 && value.EndsWith("\r\n", StringComparison.Ordinal) &&
        value[..^2].IndexOfAny(['\r', '\n', '\0']) < 0;

    private static bool IsScriptSafe(string value) => value.IndexOfAny(['\r', '\n', '"', ';']) < 0;
    private static string ToAutoCadPath(string value) => value.Replace('\\', '/');
}
