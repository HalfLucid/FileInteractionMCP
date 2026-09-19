using System.IO.Enumeration;

namespace FileInteractionMCP.Sandbox;

/// <summary>
/// Every tool resolves its paths through this class. A path is accepted only if, after following
/// every symlink or junction along the way, it lands inside an allowed root, outside the trash
/// directory, and matches no deny pattern.
/// </summary>
public sealed class PathGuard(SandboxOptions options)
{
    public static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public SandboxOptions Options => options;

    /// <summary>Resolves a path that must already exist. Returns the real, symlink-free path.</summary>
    public string ResolveExisting(string path)
    {
        var real = Resolve(path);
        if (!File.Exists(real) && !Directory.Exists(real))
        {
            throw new FileNotFoundException($"Path not found: {path}");
        }

        return real;
    }

    /// <summary>Resolves a path that may not exist yet. Existing ancestors are symlink-resolved.</summary>
    public string ResolveForWrite(string path) => Resolve(path);

    public bool IsRoot(string realPath) => options.Roots.Any(r => PathComparer.Equals(r, realPath));

    public string RootFor(string realPath) =>
        options.Roots.FirstOrDefault(r => IsWithin(realPath, r))
        ?? throw new UnauthorizedAccessException($"'{realPath}' is outside the allowed roots.");

    /// <summary>Path relative to its root, for diff labels and messages.</summary>
    public string Display(string realPath)
    {
        var root = options.Roots.FirstOrDefault(r => IsWithin(realPath, r));
        return root is null ? realPath : Path.GetRelativePath(root, realPath);
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.");
        }

        // Relative paths are interpreted against the first root, never the process cwd.
        var full = Path.GetFullPath(path, options.Roots[0]);
        var real = RealPath(full);

        if (!options.Roots.Any(r => IsWithin(real, r)))
        {
            throw new UnauthorizedAccessException(
                $"Path '{path}' resolves to '{real}', which is outside the allowed roots.");
        }

        if (IsWithin(real, options.TrashDirectory))
        {
            throw new UnauthorizedAccessException(
                $"Path '{path}' is inside the trash directory, which tools may not touch directly.");
        }

        EnsureNotDenied(real, path);
        return real;
    }

    private void EnsureNotDenied(string real, string display)
    {
        var root = RootFor(real);
        var relative = Path.GetRelativePath(root, real);
        if (relative == ".")
        {
            return;
        }

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            foreach (var pattern in options.DenyPatterns)
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, segment, ignoreCase: true))
                {
                    throw new UnauthorizedAccessException(
                        $"Path '{display}' is denied: segment '{segment}' matches deny pattern '{pattern}'.");
                }
            }
        }
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies beneath it.</summary>
    public static bool IsWithin(string path, string root)
    {
        if (path.Equals(root, PathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// Follows symlinks and junctions segment by segment. Segments that do not exist yet are kept verbatim,
    /// so the result is usable for paths about to be created.
    /// </summary>
    public static string RealPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);

            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);

            if (!info.Exists)
            {
                for (int j = i + 1; j < segments.Length; j++)
                {
                    current = Path.Combine(current, segments[j]);
                }

                break;
            }

            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    current = target.FullName;
                }
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}
