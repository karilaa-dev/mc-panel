namespace McPanel.Api.Infrastructure;

public sealed class SafePathResolver
{
    public string Resolve(string root, string relativePath, bool allowMissing = true)
    {
        if (string.IsNullOrWhiteSpace(root) || relativePath is null)
            throw new PanelException(400, "PATH_OUTSIDE_SERVER", "The path is invalid.");
        if (relativePath.IndexOf('\0') >= 0 || Path.IsPathRooted(relativePath))
            throw new PanelException(400, "PATH_OUTSIDE_SERVER", "The path is outside the server directory.");

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot, comparison) && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), comparison))
            throw new PanelException(400, "PATH_OUTSIDE_SERVER", "The path is outside the server directory.");

        RejectLinks(fullRoot.TrimEnd(Path.DirectorySeparatorChar), full, allowMissing);
        return full;
    }

    public string Relative(string root, string fullPath) =>
        Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(fullPath)).Replace(Path.DirectorySeparatorChar, '/');

    private static void RejectLinks(string root, string target, bool allowMissing)
    {
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".") return;
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                if (allowMissing) return;
                throw PanelProblems.NotFound("Path");
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new PanelException(400, "PATH_OUTSIDE_SERVER", "Symbolic links are not accessible through the panel.");
        }
    }
}
