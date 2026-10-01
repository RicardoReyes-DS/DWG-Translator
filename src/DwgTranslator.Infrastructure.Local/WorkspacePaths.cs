namespace DwgTranslator.Infrastructure.Local;

public sealed class WorkspacePaths
{
    private readonly string _root;
    private readonly StringComparison _comparison;

    public WorkspacePaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("WORKSPACE_ROOT_REQUIRED", nameof(root));
        _root = Path.GetFullPath(root);
        _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public string Root => _root;

    public string JobRoot(Guid jobId)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("JOB_ID_EMPTY", nameof(jobId));
        return Path.Combine(_root, jobId.ToString("D"));
    }

    public string Resolve(Guid jobId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("WORKSPACE_PATH_INVALID", nameof(relativePath));

        var segments = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException("WORKSPACE_PATH_TRAVERSAL", nameof(relativePath));

        var jobRoot = Path.GetFullPath(JobRoot(jobId));
        var resolved = Path.GetFullPath(Path.Combine(jobRoot, relativePath));
        if (!resolved.StartsWith(jobRoot + Path.DirectorySeparatorChar, _comparison))
            throw new ArgumentException("WORKSPACE_PATH_TRAVERSAL", nameof(relativePath));

        RejectExistingLinks(jobRoot, resolved);
        return resolved;
    }

    private static void RejectExistingLinks(string jobRoot, string target)
    {
        var current = jobRoot;
        if (IsLink(current))
            throw new IOException("WORKSPACE_REPARSE_POINT_REJECTED");

        var relative = Path.GetRelativePath(jobRoot, target);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsLink(current))
                throw new IOException("WORKSPACE_REPARSE_POINT_REJECTED");
        }
    }

    private static bool IsLink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }
}
