namespace DwgTranslator.Application;

/// <summary>
/// Fail-closed cleanup decision for a temporary normalized CAD baseline.
/// Delegates keep filesystem access in the CAD adapter and make every failure
/// mode testable without opening a drawing.
/// </summary>
public static class CadNormalizedBaselineCleanupPolicy
{
    public static bool TryRemove(
        Action delete,
        Func<bool> fileExists,
        Func<bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(delete);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);

        try
        {
            delete();
            return !fileExists() && !directoryExists();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
