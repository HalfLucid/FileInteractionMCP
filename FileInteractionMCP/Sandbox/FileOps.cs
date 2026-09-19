namespace FileInteractionMCP.Sandbox;

/// <summary>Low-level file operations shared by the tools and the undo journal. No sandbox checks here.</summary>
internal static class FileOps
{
    public static void Move(string src, string dst, bool isDir, bool overwrite)
    {
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
