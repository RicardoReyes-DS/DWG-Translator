using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DwgTranslator.Agent;

public sealed record AgentBatchRecoveryOutputSafety(bool IsSafe, string? Category = null, string? Path = null)
{
    public static AgentBatchRecoveryOutputSafety Safe() => new(true);
    public static AgentBatchRecoveryOutputSafety Unsafe(string category, string? path) => new(false, category, path);
}

/// <summary>
/// Durable evidence for a final output admitted into a shared recovery root.
/// The byte count is retained alongside the hash so an operator can diagnose a
/// mismatch without treating a path alone as ownership evidence.
/// </summary>
public sealed record AgentBatchRecoveryOutputEvidence(string Hash, long Bytes);

public static class AgentBatchFileSystemPolicy
{
    public static bool IsSafeEmptyOutputDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(Canonical(path));
            return directory.Exists && !IsReparse(directory) && !HasAlternateDataStreams(directory.FullName) &&
                !directory.EnumerateFileSystemInfos().Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsSafeOwnedOutputDirectory(string path, IReadOnlyDictionary<string, string> expectedFiles)
    {
        try
        {
            var directory = new DirectoryInfo(Canonical(path));
            if (!directory.Exists || IsReparse(directory) || HasAlternateDataStreams(directory.FullName)) return false;
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in expectedFiles)
            {
                var full = Canonical(item.Key);
                if (string.Equals(full, directory.FullName, StringComparison.OrdinalIgnoreCase) || !Within(full, directory.FullName) ||
                    string.IsNullOrWhiteSpace(item.Value) || !expected.TryAdd(full, item.Value)) return false;
            }

            var found = 0;
            foreach (var item in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (IsReparse(item) || HasAlternateDataStreams(item.FullName)) return false;
                if (item is not System.IO.FileInfo file) continue;
                var full = Canonical(file.FullName);
                if (!expected.TryGetValue(full, out var expectedHash) || HasMultipleHardLinks(full)) return false;
                using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(hash, expectedHash, StringComparison.Ordinal)) return false;
                found++;
            }
            return found == expected.Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates an iterated recovery root. Completed outputs are mandatory and
    /// hash- and size-bound. Active candidates and staging files are never
    /// reusable. Every other entry fails closed and selected CreateNew final
    /// paths must remain absent.
    /// </summary>
    public static AgentBatchRecoveryOutputSafety IsSafeRecoveryOutputDirectory(
        string path,
        IReadOnlyDictionary<string, AgentBatchRecoveryOutputEvidence> expectedCompletedFiles,
        IReadOnlyDictionary<string, string> expectedCandidateFiles,
        IReadOnlyCollection<string> requiredAbsentFiles)
    {
        try
        {
            var directory = new DirectoryInfo(Canonical(path));
            if (!directory.Exists || IsReparse(directory) || HasAlternateDataStreams(directory.FullName))
                return AgentBatchRecoveryOutputSafety.Unsafe("OUTPUT_DIRECTORY_UNSAFE", directory.FullName);

            var completed = CanonicalizeCompleted(directory, expectedCompletedFiles);
            var candidates = CanonicalizeExpected(directory, expectedCandidateFiles);
            var requiredAbsent = requiredAbsentFiles.Select(Canonical).ToArray();
            if (requiredAbsent.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requiredAbsent.Length ||
                requiredAbsent.Any(item => string.Equals(item, directory.FullName, StringComparison.OrdinalIgnoreCase) || !Within(item, directory.FullName)) ||
                requiredAbsent.Any(item => completed.ContainsKey(item) || File.Exists(item) || Directory.Exists(item)))
                return AgentBatchRecoveryOutputSafety.Unsafe("SELECTED_OUTPUT_PRESENT_OR_INVALID", requiredAbsent.FirstOrDefault());
            if (candidates.Keys.Any(completed.ContainsKey))
                return AgentBatchRecoveryOutputSafety.Unsafe("CANDIDATE_COMPLETED_COLLISION", candidates.Keys.First(completed.ContainsKey));

            var foundCompleted = 0;
            foreach (var item in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (IsReparse(item) || HasAlternateDataStreams(item.FullName))
                    return AgentBatchRecoveryOutputSafety.Unsafe("OUTPUT_REPARSE_OR_ADS", item.FullName);
                if (item is not System.IO.FileInfo file) continue;
                var full = Canonical(file.FullName);
                if (candidates.ContainsKey(full) || IsCandidateOrStaging(file.Name))
                    return AgentBatchRecoveryOutputSafety.Unsafe("ACTIVE_CANDIDATE_OR_STAGING", full);
                if (!completed.TryGetValue(full, out var expected))
                    return AgentBatchRecoveryOutputSafety.Unsafe("UNEXPECTED_OUTPUT", full);
                if (HasMultipleHardLinks(full))
                    return AgentBatchRecoveryOutputSafety.Unsafe("OUTPUT_HARDLINKED", full);
                using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(hash, expected.Hash, StringComparison.Ordinal) || file.Length != expected.Bytes)
                    return AgentBatchRecoveryOutputSafety.Unsafe("OUTPUT_HASH_MISMATCH", full);
                if (completed.ContainsKey(full)) foundCompleted++;
            }
            return foundCompleted == completed.Count
                ? AgentBatchRecoveryOutputSafety.Safe()
                : AgentBatchRecoveryOutputSafety.Unsafe("COMPLETED_OUTPUT_MISSING", directory.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or CryptographicException)
        {
            return AgentBatchRecoveryOutputSafety.Unsafe("OUTPUT_DIRECTORY_UNREADABLE", path);
        }
    }

    public static (IReadOnlyList<AgentBatchManifestEntry>? Files, AgentError? Error) Enumerate(
        string sourceDirectory, string outputDirectory, string allowedInputRoot, IReadOnlyList<string> allowedOutputRoots,
        bool requireOutputAbsent = true)
    {
        try
        {
            if (!Path.IsPathFullyQualified(sourceDirectory) || !Path.IsPathFullyQualified(outputDirectory) ||
                sourceDirectory.StartsWith('\\') || outputDirectory.StartsWith('\\') ||
                sourceDirectory.IndexOf(':', 2) >= 0 || outputDirectory.IndexOf(':', 2) >= 0)
                return (null, new("BATCH_PATH_INVALID", "Batch paths must be local absolute paths without ADS syntax."));
            var source = Canonical(sourceDirectory);
            var output = Canonical(outputDirectory);
            var inputRoot = Canonical(allowedInputRoot);
            if (!Within(source, inputRoot)) return (null, new("BATCH_SOURCE_OUTSIDE_ROOT", "The source directory is outside InputRoot."));
            if (!allowedOutputRoots.Select(Canonical).Any(root => string.Equals(output, root, StringComparison.OrdinalIgnoreCase)))
                return (null, new("BATCH_OUTPUT_OUTSIDE_ALLOWLIST", "The output directory is not an exact configured batch root."));
            if (requireOutputAbsent && (Directory.Exists(output) || File.Exists(output))) return (null, new("OUTPUT_ALREADY_EXISTS", "The batch output must be absent."));
            var sourceInfo = new DirectoryInfo(source);
            if (!sourceInfo.Exists || IsReparse(sourceInfo)) return (null, new("BATCH_SOURCE_INVALID", "The source directory is missing or reparse-backed."));

            var files = new List<AgentBatchManifestEntry>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenHashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in sourceInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => string.Equals(file.Extension, ".dwg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var full = Canonical(file.FullName);
                if (!Within(full, source) || IsReparse(file)) return (null, new("BATCH_FILE_UNSAFE", "A source file escaped the root or is reparse-backed."));
                if (!seenPaths.Add(full)) return (null, new("BATCH_DUPLICATE_PATH", "A duplicate canonical source path was found."));
                if (HasMultipleHardLinks(file.FullName)) return (null, new("BATCH_HARDLINK_REJECTED", "Hardlinked source files are not allowed."));
                if (HasAlternateDataStreams(file.FullName)) return (null, new("BATCH_ADS_REJECTED", "Source files with alternate data streams are not allowed."));
                using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!seenHashes.Add(hash)) return (null, new("BATCH_DUPLICATE_HASH", "Duplicate source content is not allowed."));
                var relative = Path.GetRelativePath(source, full);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
                    return (null, new("BATCH_FILE_OUTSIDE_ROOT", "A source file escaped the batch root."));
                var relativeDirectory = Path.GetDirectoryName(relative) ?? string.Empty;
                var outputName = Path.GetFileNameWithoutExtension(relative) + "-ENG.dwg";
                files.Add(new(relative, full, Path.Combine(output, relativeDirectory, outputName), file.Length, hash, file.LastWriteTimeUtc));
            }
            return (files, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, new("BATCH_ENUMERATION_FAILED", "The source snapshot could not be enumerated safely."));
        }
    }

    private static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool Within(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static Dictionary<string, AgentBatchRecoveryOutputEvidence> CanonicalizeCompleted(
        DirectoryInfo directory, IReadOnlyDictionary<string, AgentBatchRecoveryOutputEvidence> expectedFiles)
    {
        var expected = new Dictionary<string, AgentBatchRecoveryOutputEvidence>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in expectedFiles)
        {
            var full = Canonical(item.Key);
            if (string.Equals(full, directory.FullName, StringComparison.OrdinalIgnoreCase) || !Within(full, directory.FullName) ||
                string.IsNullOrWhiteSpace(item.Value.Hash) || item.Value.Bytes <= 0 || !expected.TryAdd(full, item.Value))
                throw new ArgumentException("Recovery output evidence is invalid.");
        }
        return expected;
    }
    private static Dictionary<string, string> CanonicalizeExpected(DirectoryInfo directory, IReadOnlyDictionary<string, string> expectedFiles)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in expectedFiles)
        {
            var full = Canonical(item.Key);
            if (string.Equals(full, directory.FullName, StringComparison.OrdinalIgnoreCase) || !Within(full, directory.FullName) ||
                string.IsNullOrWhiteSpace(item.Value) || !expected.TryAdd(full, item.Value))
                throw new ArgumentException("Recovery candidate evidence is invalid.");
        }
        return expected;
    }
    private static bool IsCandidateOrStaging(string name) =>
        name.StartsWith('.') ||
        name.Contains(".candidate-", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(".staging", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    private static bool IsReparse(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;

    private static bool HasMultipleHardLinks(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return GetFileInformationByHandle(handle, out var info) && info.NumberOfLinks > 1;
    }

    private static bool HasAlternateDataStreams(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == new IntPtr(-1)) return false;
        var count = 0;
        try
        {
            do { if (!string.Equals(data.StreamName, "::$DATA", StringComparison.OrdinalIgnoreCase)) count++; }
            while (FindNextStreamW(handle, out data));
        }
        finally { FindClose(handle); }
        return count > 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime; public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StreamData { public long StreamSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string StreamName; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll")] private static extern bool FindClose(IntPtr handle);
}
