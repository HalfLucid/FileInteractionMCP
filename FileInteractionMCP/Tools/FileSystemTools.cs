using System.ComponentModel;
using System.Text;
using FileInteractionMCP.Sandbox;
using FileInteractionMCP.Text;
using ModelContextProtocol.Server;
using static FileInteractionMCP.Tools.ToolSupport;

namespace FileInteractionMCP.Tools;

/// <summary>
/// Whole-file operations the built-in tools lack: delete (to trash), move, copy, mkdir, append,
/// plus format inspection and conversion.
/// </summary>
[McpServerToolType]
internal sealed class FileSystemTools(PathGuard guard, UndoJournal journal)
{
    [McpServerTool(Name = "fs_roots", ReadOnly = true, Idempotent = true)]
    [Description("Show the sandbox configuration: allowed root directories, trash location, deny patterns and size limits. Relative paths given to any tool resolve against the first root.")]
    public object Roots() => new RootsResult(
        guard.Options.Roots,
        guard.Options.TrashDirectory,
        guard.Options.DenyPatterns,
        guard.Options.MaxTextFileBytes,
        guard.Options.MaxBulkFiles,
        "Relative paths resolve against the first root. Symlinks are followed and the final target must be inside a root.");

    [McpServerTool(Name = "fs_info", ReadOnly = true, Idempotent = true)]
    [Description("Inspect a file or directory without reading it into context: size, timestamp, and for text files the encoding, BOM, line-ending style (with counts, so mixed endings are visible), line count and whether it ends with a newline.")]
    public object Info([Description("File or directory path.")] string path) => Run(() =>
    {
        var real = guard.ResolveExisting(path);

        if (Directory.Exists(real))
        {
            var dir = new DirectoryInfo(real);
            return new FileInfoResult(
                true, real, "directory", null, dir.LastWriteTimeUtc,
                dir.EnumerateFiles().Count(), dir.EnumerateDirectories().Count(),
                null, null, null, null, null, null, null, null, null, null, null);
        }

        var file = new FileInfo(real);
        try
        {
            var doc = TextDocument.Load(real, guard.Options.MaxTextFileBytes);
            return new FileInfoResult(
                true, real, "file", file.Length, file.LastWriteTimeUtc, null, null,
                true, doc.EncodingName, doc.HasBom, doc.NewLineName, doc.MixedLineEndings,
                doc.CrlfCount, doc.LfCount, doc.CrCount, doc.Lines().Count, doc.EndsWithNewline, null);
        }
        catch (InvalidOperationException ex)
        {
            return new FileInfoResult(
                true, real, "file", file.Length, file.LastWriteTimeUtc, null, null,
                false, null, null, null, null, null, null, null, null, null, ex.Message);
        }
    });

    [McpServerTool(Name = "fs_convert")]
    [Description("Change a text file's encoding and/or line endings. encoding: utf-8, utf-8-bom, utf-16-le, utf-16-be, latin1. newLine: crlf, lf, cr. Omitted settings are kept. Also normalizes mixed line endings to the chosen (or current dominant) style. Undoable.")]
    public object Convert(
        [Description("File path.")] string path,
        [Description("Target encoding, or omit to keep.")] string? encoding = null,
        [Description("Target line ending, or omit to keep the dominant one.")] string? newLine = null,
        [Description("Add a final newline if the file lacks one.")] bool ensureTrailingNewline = false,
        [Description("Report without writing.")] bool dryRun = false) => Run(() =>
    {
        var real = guard.ResolveExisting(path);
        var doc = TextDocument.Load(real, guard.Options.MaxTextFileBytes);

        var targetEncoding = encoding is null ? doc.Encoding : TextDocument.ParseEncoding(encoding);
        var targetNewLine = newLine is null ? doc.NewLine : TextDocument.ParseNewLine(newLine);
        var converted = doc.WithFormat(targetEncoding, targetNewLine);

        var addedNewline = false;
        if (ensureTrailingNewline && converted.Text.Length > 0 && !converted.EndsWithNewline)
        {
            converted.SetText(converted.Text + targetNewLine);
            addedNewline = true;
        }

        var changed = converted.Text != doc.Text
            || TextDocument.NameOf(targetEncoding) != doc.EncodingName;

        int? undoId = null;
        if (!dryRun && changed)
        {
            var entry = journal.Begin("fs_convert", $"{guard.Display(real)} -> {converted.EncodingName}/{converted.NewLineName}");
            entry.CaptureContent(real);
            converted.Save();
            undoId = journal.Commit(entry);
        }

        return new ConvertResult(
            true, real, !dryRun && changed, dryRun,
            doc.EncodingName, converted.EncodingName, doc.NewLineName, converted.NewLineName,
            addedNewline, undoId);
    });

    [McpServerTool(Name = "fs_mkdir", Idempotent = true)]
    [Description("Create a directory and any missing parents inside the sandbox. Succeeds if it already exists.")]
    public object MakeDirectory(
        [Description("Directory path, absolute or relative to the first root.")] string path) => Run(() =>
    {
        var real = guard.ResolveForWrite(path);
        if (File.Exists(real))
        {
            throw new IOException($"A file already exists at {real}.");
        }

        var existed = Directory.Exists(real);
        Directory.CreateDirectory(real);
        return new MkdirResult(true, real, !existed);
    });

    [McpServerTool(Name = "fs_delete", Destructive = true)]
    [Description("Delete a file or directory. By default it is moved to the server's trash directory (returned as trashedTo) and can be restored with undo; permanent=true removes it outright. Non-empty directories require recursive=true. Sandbox roots cannot be deleted.")]
    public object Delete(
        [Description("File or directory path.")] string path,
        [Description("Required to delete a non-empty directory.")] bool recursive = false,
        [Description("Skip the trash and remove permanently. Not undoable.")] bool permanent = false) => Run(() =>
    {
        var real = guard.ResolveExisting(path);
        if (guard.IsRoot(real))
        {
            throw new InvalidOperationException("Refusing to delete a sandbox root.");
        }

        var isDir = Directory.Exists(real);
        var kind = isDir ? "directory" : "file";

        if (isDir && !recursive && Directory.EnumerateFileSystemEntries(real).Any())
        {
            throw new InvalidOperationException("Directory is not empty. Pass recursive=true to delete it with its contents.");
        }

        if (permanent)
        {
            if (isDir)
            {
                Directory.Delete(real, recursive: true);
            }
            else
            {
                File.Delete(real);
            }

            return new DeleteResult(true, real, kind, true, null, null);
        }

        var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var trashPath = Path.Combine(guard.Options.TrashDirectory, stamp, Path.GetFileName(real));
        Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);

        var entry = journal.Begin("fs_delete", $"{guard.Display(real)} ({kind}) to trash");
        FileOps.Move(real, trashPath, isDir, overwrite: false);
        entry.RecordMove(real, trashPath);
        var undoId = journal.Commit(entry);

        return new DeleteResult(true, real, kind, false, trashPath, undoId);
    });

    [McpServerTool(Name = "fs_move")]
    [Description("Move or rename a file or directory. destination is the full new path, not a parent folder. Fails if destination exists unless overwrite=true; a rename that changes only letter case is allowed. Works across drives. Does not tell git: it detects ordinary renames by content, but a case-only rename needs `git mv` afterwards when core.ignorecase is true. Undoable unless overwrite replaced something.")]
    public object Move(
        [Description("Existing file or directory.")] string source,
        [Description("Full new path for the item.")] string destination,
        [Description("Replace an existing destination.")] bool overwrite = false) => Run(() =>
    {
        var src = guard.ResolveExisting(source);
        var dst = guard.ResolveForWrite(destination);

        if (guard.IsRoot(src))
        {
            throw new InvalidOperationException("Refusing to move a sandbox root.");
        }

        if (string.Equals(src, dst, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Source and destination are the same path.");
        }

        // Renaming Readme.md to README.md: on a case-insensitive volume the destination looks
        // occupied and looks like the source's own subtree, but it is simply the source.
        var caseOnlyRename = FileOps.IsCaseOnlyRename(src, dst);

        var isDir = Directory.Exists(src);
        if (isDir && !caseOnlyRename && PathGuard.IsWithin(dst, src))
        {
            throw new InvalidOperationException("Cannot move a directory into itself.");
        }

        var destinationExisted = !caseOnlyRename && (File.Exists(dst) || Directory.Exists(dst));
        if (!overwrite && destinationExisted)
        {
            throw new IOException($"Destination already exists: {dst}. Pass overwrite=true to replace it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        var entry = journal.Begin("fs_move", $"{guard.Display(src)} -> {guard.Display(dst)}");
        FileOps.Move(src, dst, isDir, overwrite);
        int? undoId = null;
        if (!destinationExisted)
        {
            entry.RecordMove(src, dst);
            undoId = journal.Commit(entry);
        }

        return new MoveResult(true, src, dst, isDir ? "directory" : "file", undoId);
    });

    [McpServerTool(Name = "fs_copy")]
    [Description("Copy a file, or a directory recursively. destination is the full new path. Fails if destination exists unless overwrite=true. Symbolic links inside a copied directory are skipped and counted.")]
    public object Copy(
        [Description("Existing file or directory.")] string source,
        [Description("Full path of the copy.")] string destination,
        [Description("Replace existing files at the destination.")] bool overwrite = false) => Run(() =>
    {
        var src = guard.ResolveExisting(source);
        var dst = guard.ResolveForWrite(destination);
        var isDir = Directory.Exists(src);

        if (isDir && PathGuard.IsWithin(dst, src))
        {
            throw new InvalidOperationException("Cannot copy a directory into itself.");
        }

        if (!overwrite && (File.Exists(dst) || Directory.Exists(dst)))
        {
            throw new IOException($"Destination already exists: {dst}. Pass overwrite=true to replace it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        var skippedLinks = 0;
        if (isDir)
        {
            FileOps.CopyDirectory(src, dst, overwrite, ref skippedLinks);
        }
        else
        {
            File.Copy(src, dst, overwrite);
        }

        return new CopyResult(true, src, dst, isDir ? "directory" : "file", skippedLinks);
    });

    [McpServerTool(Name = "fs_append")]
    [Description("Append text to the end of a file, creating it if missing. Line endings in content are converted to the file's own. By default a newline is inserted first if the file does not end with one, and one is added after the content. Undoable.")]
    public object Append(
        [Description("File path.")] string path,
        [Description("Text to append.")] string content,
        [Description("Insert a newline before the content if the file does not already end with one.")] bool ensureNewlineBefore = true,
        [Description("Make sure the appended content ends with a newline.")] bool ensureTrailingNewline = true) => Run(() =>
    {
        var real = guard.ResolveForWrite(path);
        if (Directory.Exists(real))
        {
            throw new IOException($"{real} is a directory.");
        }

        var exists = File.Exists(real);
        var doc = exists
            ? TextDocument.Load(real, guard.Options.MaxTextFileBytes)
            : TextDocument.CreateNew(real);

        var chunk = doc.Normalize(content);
        var sb = new StringBuilder(doc.Text);

        if (ensureNewlineBefore && doc.Text.Length > 0 && !doc.EndsWithNewline)
        {
            sb.Append(doc.NewLine);
        }

        sb.Append(chunk);

        if (ensureTrailingNewline && chunk.Length > 0 && !chunk.EndsWith(doc.NewLine))
        {
            sb.Append(doc.NewLine);
        }

        var linesAppended = chunk.Length == 0
            ? 0
            : TextDocument.SplitLines(chunk.TrimEnd('\r', '\n')).Length;

        var entry = journal.Begin("fs_append", $"{guard.Display(real)}: +{linesAppended} line(s)");
        entry.CaptureContent(real);
        doc.SetText(sb.ToString());
        doc.Save();
        var undoId = journal.Commit(entry);

        return new AppendResult(true, real, !exists, linesAppended, doc.NewLineName, doc.EncodingName, undoId);
    });
}
