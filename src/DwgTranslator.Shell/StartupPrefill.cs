using System.IO;

namespace DwgTranslator.Shell;

public static class StartupPrefill
{
    public static void Apply(MainViewModel viewModel, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(arguments);

        for (var index = 0; index + 1 < arguments.Count; index += 2)
        {
            var option = arguments[index];
            var value = arguments[index + 1];
            if (string.IsNullOrWhiteSpace(value)) continue;

            if (string.Equals(option, "--source", StringComparison.Ordinal))
            {
                var path = AbsolutePath(value);
                if (path is not null && File.Exists(path)) viewModel.SourceFile = path;
            }
            else if (string.Equals(option, "--output", StringComparison.Ordinal))
            {
                var path = AbsolutePath(value);
                if (path is not null) viewModel.OutputFile = path;
            }
            else if (string.Equals(option, "--language", StringComparison.Ordinal))
            {
                var language = viewModel.Languages.SingleOrDefault(candidate =>
                    string.Equals(candidate.Tag, value, StringComparison.Ordinal));
                if (language is not null) viewModel.SelectedLanguage = language;
            }
        }
    }

    private static string? AbsolutePath(string value)
    {
        if (!Path.IsPathFullyQualified(value)) return null;
        try { return Path.GetFullPath(value); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
