namespace FileInteractionMCP.Sandbox;

/// <summary>
/// Immutable server configuration: which directories tools may touch, where deleted files go,
/// which names are always off-limits, and size caps.
/// </summary>
public sealed class SandboxOptions
{
    public static readonly string[] DefaultDenyPatterns =
    [
        ".git", ".svn", ".hg", ".ssh",
        ".env", ".env.*",
        "*.pem", "*.key", "*.pfx", "*.p12",
        "id_rsa*", "id_ed25519*",
    ];

    /// <summary>Real (symlink-resolved) root directories. All paths must resolve inside one of these.</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>Where fs_delete moves things unless permanent=true. Tools may never operate inside it.</summary>
    public required string TrashDirectory { get; init; }

    /// <summary>Wildcard patterns matched against every path segment below a root.</summary>
    public required IReadOnlyList<string> DenyPatterns { get; init; }

    public long MaxTextFileBytes { get; init; } = 20L * 1024 * 1024;

    public int MaxBulkFiles { get; init; } = 50;

    /// <summary>
    /// Parses: --root &lt;dir&gt; (repeatable), --trash &lt;dir&gt;, --deny &lt;pattern&gt; (repeatable),
    /// --max-file-mb &lt;n&gt;, --max-bulk-files &lt;n&gt;. Env FIMCP_ROOTS (path-separator delimited) and
    /// FIMCP_TRASH are also honored. With no roots at all, the current directory is the single root.
    /// </summary>
    public static SandboxOptions FromArgs(string[] args)
    {
        var roots = new List<string>();
        var deny = new List<string>(DefaultDenyPatterns);
        string? trash = null;
        long maxMb = 20;
        int maxBulk = 50;

        var queue = new Queue<string>(args);
        while (queue.Count > 0)
        {
            var flag = queue.Dequeue();
            string Value() => queue.Count > 0 ? queue.Dequeue() : throw new ArgumentException($"{flag} requires a value.");

            switch (flag)
            {
                case "--root": roots.Add(Value()); break;
                case "--trash": trash = Value(); break;
                case "--deny": deny.Add(Value()); break;
                case "--max-file-mb": maxMb = long.Parse(Value()); break;
                case "--max-bulk-files": maxBulk = int.Parse(Value()); break;
                default: throw new ArgumentException($"Unknown argument '{flag}'.");
            }
        }

        var envRoots = Environment.GetEnvironmentVariable("FIMCP_ROOTS");
        if (!string.IsNullOrWhiteSpace(envRoots))
        {
            roots.AddRange(envRoots.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (roots.Count == 0)
        {
            roots.Add(Environment.CurrentDirectory);
        }

        trash ??= Environment.GetEnvironmentVariable("FIMCP_TRASH");
        trash ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileInteractionMCP", "trash");

        var realRoots = new List<string>();
        foreach (var root in roots)
        {
            var full = Path.GetFullPath(root);
            if (!Directory.Exists(full))
            {
                throw new DirectoryNotFoundException($"Root directory does not exist: {full}");
            }

            var real = PathGuard.RealPath(full);
            if (!realRoots.Contains(real, PathGuard.PathComparer))
            {
                realRoots.Add(real);
            }
        }

        return new SandboxOptions
        {
            Roots = realRoots,
            TrashDirectory = PathGuard.RealPath(Path.GetFullPath(trash)),
            DenyPatterns = deny,
            MaxTextFileBytes = maxMb * 1024 * 1024,
            MaxBulkFiles = maxBulk,
        };
    }
}
