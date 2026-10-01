namespace DwgTranslator.Infrastructure.Local;

public static class AtomicFileWriter
{
    private const int ReplaceAttempts = 5;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(25);

    public static async Task WriteAsync(string destination, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("DESTINATION_DIRECTORY_REQUIRED", nameof(destination));
        Directory.CreateDirectory(directory);
        RejectLink(destination);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            await ReplaceAsync(temporary, destination, ReplaceDestinationAtomically, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static async Task ReplaceAsync(
        string temporary,
        string destination,
        Action<string, string> replace,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                replace(temporary, destination);
                return;
            }
            catch (IOException exception) when (attempt < ReplaceAttempts && IsSharingOrLockViolation(exception))
            {
                await Task.Delay(ReplaceRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingOrLockViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static void ReplaceDestinationAtomically(string temporary, string destination)
    {
        // File.Move(..., overwrite: true) returns ERROR_ACCESS_DENIED on Windows
        // while the destination has an open reader, even when that reader allows
        // delete sharing. File.Replace honors delete sharing and preserves the
        // already-open reader's snapshot.
        if (File.Exists(destination))
        {
            File.Replace(temporary, destination, destinationBackupFileName: null);
            return;
        }

        // Creation is intentionally non-overwriting. If another actor creates the
        // destination after the existence check, Move fails closed rather than
        // changing mechanisms and overwriting the raced-in file.
        File.Move(temporary, destination);
    }

    private static void RejectLink(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("DESTINATION_REPARSE_POINT_REJECTED");
    }
}
