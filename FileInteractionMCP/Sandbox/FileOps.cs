namespace FileInteractionMCP.Sandbox;

/// <summary>Low-level file operations shared by the tools and the undo journal. No sandbox checks here.</summary>
internal static class FileOps
{
    public static void Move(string src, string dst, bool isDir, bool overwrite)
    {
        if (IsCaseOnlyRename(src, dst))
        {
            // The destination "exists" only because it is the source under another spelling.
            // There is nothing to replace, and deleting it first would delete the source, so the
            // overwrite flag is ignored. Both File.Move and Directory.Move rename in place here.
            if (isDir)
            {
                Directory.Move(src, dst);
            }
            else
            {
                File.Move(src, dst);
            }

            return;
        }

        if (!isDir)
        {
            if (overwrite && Directory.Exists(dst))
            {
                Directory.Delete(dst, recursive: true);
            }

            File.Move(src, dst, overwrite);
            return;
        }

        if (Directory.Exists(dst) || File.Exists(dst))
        {
            if (!overwrite)
            {
                throw new IOException($"Destination already exists: {dst}");
            }

            if (Directory.Exists(dst))
            {
                Directory.Delete(dst, recursive: true);
            }
            else
            {
                File.Delete(dst);
            }
        }

        var sameVolume = string.Equals(
            Path.GetPathRoot(src), Path.GetPathRoot(dst), PathGuard.PathComparison);

        if (sameVolume)
        {
            Directory.Move(src, dst);
            return;
        }

        // Directory.Move cannot cross volumes; copy then remove.
        var skipped = 0;
        CopyDirectory(src, dst, overwrite: false, ref skipped);
        Directory.Delete(src, recursive: true);
    }

    /// <summary>
    /// True when <paramref name="dst"/> names the same directory entry as <paramref name="src"/> with
    /// different letter case, e.g. <c>Readme.md</c> to <c>README.md</c>. On a case-insensitive volume an
    /// existence check on <paramref name="dst"/> then reports the source itself, not a conflict.
    /// </summary>
    public static bool IsCaseOnlyRename(string src, string dst)
    {
        var srcName = Path.GetFileName(src);
        var dstName = Path.GetFileName(dst);
        if (srcName.Length == 0 || dstName.Length == 0
            || string.Equals(srcName, dstName, StringComparison.Ordinal)
            || !string.Equals(srcName, dstName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var srcParent = Path.GetDirectoryName(src);
        var dstParent = Path.GetDirectoryName(dst);
        if (srcParent is null || dstParent is null
            || !string.Equals(srcParent, dstParent, PathGuard.PathComparison)
            || !Directory.Exists(srcParent))
        {
            return false;
        }

        // The names differ only in case. Whether that is one entry or two depends on the volume,
        // not the OS (NTFS folders can be case-sensitive, Linux mounts can be case-insensitive),
        // so ask the directory what it actually holds under that name.
        string? found = null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(srcParent))
        {
            var name = Path.GetFileName(entry);
            if (!string.Equals(name, dstName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null)
            {
                return false; // Both spellings exist: a real conflict on a case-sensitive volume.
            }

            found = name;
        }

        return string.Equals(found, srcName, StringComparison.Ordinal);
    }

    public static void CopyDirectory(string src, string dst, bool overwrite, ref int skippedLinks)
    {
        Directory.CreateDirectory(dst);

        foreach (var file in Directory.EnumerateFiles(src))
        {
            if (new FileInfo(file).LinkTarget is not null)
            {
                skippedLinks++;
                continue;
            }

            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite);
        }

        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null)
            {
                skippedLinks++;
                continue;
            }

            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)), overwrite, ref skippedLinks);
        }
    }

    /// <summary>Writes bytes through a temp file in the same directory, then renames over the target.</summary>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Cannot determine directory of {path}");
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
