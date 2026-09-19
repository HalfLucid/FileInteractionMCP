using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using FileInteractionMCP.Sandbox;
using FileInteractionMCP.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using ModelContextProtocol.Server;
using static FileInteractionMCP.Tools.ToolSupport;

namespace FileInteractionMCP.Tools;

/// <summary>
/// Content edits with addressing modes the built-in Edit tool lacks: line ranges, atomic batches,
/// and regex substitution across files. Every edit returns a unified diff, supports dry runs, and is undoable.
/// </summary>
[McpServerToolType]
internal sealed class EditTools(PathGuard guard, UndoJournal journal)
{
    private static readonly string[] DefaultExcludes =
    [
        "**/.git/**", "**/.vs/**", "**/bin/**", "**/obj/**", "**/node_modules/**",
    ];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [McpServerTool(Name = "edit_lines")]
    [Description("Edit a text file by line number: replace or delete a 1-based inclusive range, or insert before/after an anchor line. Pass `expected` (the current text of the target lines) so the edit is rejected if the file has changed since you read it; on mismatch the response shows the actual lines and, if the expected text moved, where it is now. Returns a unified diff. Encoding and line endings are preserved. Undoable.")]
    public object EditLines(
        [Description("File path.")] string path,
        [Description("One of: replace, delete, insert_before, insert_after.")] string operation,
        [Description("1-based line number. For replace/delete: first line of the range. For insert_before: the line the content goes above (lineCount+1 appends). For insert_after: the line it goes below (0 prepends).")] int startLine,
        [Description("1-based inclusive last line for replace/delete. Defaults to startLine. Ignored for inserts.")] int? endLine = null,
        [Description("Text for replace/insert. May span lines; line endings are converted to the file's own. Ignored for delete. Empty content on replace acts like delete.")] string? content = null,
        [Description("Strongly recommended. The exact current text of the lines being replaced/deleted, or of the anchor line for inserts. Mismatch aborts the edit and returns the actual text.")] string? expected = null,
        [Description("Return the diff without writing.")] bool dryRun = false) => Run(() =>
    {
        var real = guard.ResolveExisting(path);
        var doc = TextDocument.Load(real, guard.Options.MaxTextFileBytes);
        var lines = doc.Lines();
        var count = lines.Count;
        var op = operation.Trim().ToLowerInvariant();
        var newLines = ContentToLines(content);
        string summary;

        switch (op)
        {
            case "replace":
            case "delete":
            {
                var end = endLine ?? startLine;
                if (startLine < 1 || end < startLine || end > count)
                {
                    throw new ArgumentOutOfRangeException(nameof(startLine),
                        $"Range {startLine}-{end} is invalid for a file with {count} lines.");
                }

                if (op == "replace" && content is null)
                {
                    throw new ArgumentException("content is required for replace.");
                }

                CheckExpected(expected, lines, startLine, end);
                lines.RemoveRange(startLine - 1, end - startLine + 1);
                if (op == "replace")
                {
                    lines.InsertRange(startLine - 1, newLines);
                }

                summary = $"{op} lines {startLine}-{end}";
                break;
            }

            case "insert_before":
            {
                if (startLine < 1 || startLine > count + 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(startLine),
                        $"insert_before accepts 1..{count + 1} for a file with {count} lines.");
                }

                RequireContent(content);
                if (expected is not null && startLine <= count)
                {
                    CheckExpected(expected, lines, startLine, startLine);
                }

                lines.InsertRange(startLine - 1, newLines);
                summary = $"insert {newLines.Length} line(s) before {startLine}";
                break;
            }

            case "insert_after":
            {
                if (startLine < 0 || startLine > count)
                {
                    throw new ArgumentOutOfRangeException(nameof(startLine),
                        $"insert_after accepts 0..{count} for a file with {count} lines.");
                }

                RequireContent(content);
                if (expected is not null && startLine >= 1)
                {
                    CheckExpected(expected, lines, startLine, startLine);
                }

                lines.InsertRange(startLine, newLines);
                summary = $"insert {newLines.Length} line(s) after {startLine}";
                break;
            }

            default:
                throw new ArgumentException($"Unknown operation '{operation}'. Use replace, delete, insert_before or insert_after.");
        }

        var keepTrailingNewline = doc.EndsWithNewline || doc.Text.Length == 0;
        var newText = string.Join(doc.NewLine, lines);
        if (keepTrailingNewline && lines.Count > 0)
        {
            newText += doc.NewLine;
        }

        var diff = UnifiedDiff.Create(doc.Text, newText, guard.Display(real));
        var apply = !dryRun && diff.Length > 0;
        int? undoId = null;

        if (apply)
        {
            var entry = journal.Begin("edit_lines", $"{guard.Display(real)}: {summary}");
            entry.CaptureContent(real);
            doc.SetText(newText);
            doc.Save();
            undoId = journal.Commit(entry);
        }

        return new EditResult(true, real, apply, dryRun, count, lines.Count, diff, undoId);
    });

    [McpServerTool(Name = "edit_batch")]
    [Description("Apply several exact search/replace edits to one file atomically: either every edit matches its expected occurrence count and all are written, or nothing is. Edits are applied in order to the in-memory text. When a match fails, the response locates the closest text in the file (ignoring whitespace) and shows how it differs, so you can retry with the exact text. Returns a unified diff. Undoable.")]
    public object EditBatch(
        [Description("File path.")] string path,
        [Description("Edits to apply in order.")] EditPair[] edits,
        [Description("Return the diff without writing.")] bool dryRun = false) => Run(() =>
    {
        if (edits is null || edits.Length == 0)
        {
            throw new ArgumentException("At least one edit is required.");
        }

        var real = guard.ResolveExisting(path);
        var doc = TextDocument.Load(real, guard.Options.MaxTextFileBytes);
        var text = doc.Text;
        var outcomes = new List<EditPairOutcome>(edits.Length);

        for (int i = 0; i < edits.Length; i++)
        {
            var edit = edits[i];
            if (string.IsNullOrEmpty(edit.OldText))
            {
                throw new ArgumentException($"Edit {i}: oldText must not be empty.");
            }

            var oldText = doc.Normalize(edit.OldText);
            var newText = doc.Normalize(edit.NewText ?? string.Empty);
            var expected = edit.ExpectedCount ?? 1;
            if (expected < 1)
            {
                throw new ArgumentException($"Edit {i}: expectedCount must be at least 1.");
            }

            var found = CountOccurrences(text, oldText);
            if (found != expected)
            {
                var detail = found == 0
                    ? NearMiss(text, oldText)
                    : $"Found {found} occurrence(s), expected {expected}. Add more surrounding context to oldText or set expectedCount.";
                throw new InvalidOperationException($"Edit {i} failed; no changes written. {detail}");
            }

            text = text.Replace(oldText, newText);
            outcomes.Add(new EditPairOutcome(i, found, oldText == newText));
        }

        var diff = UnifiedDiff.Create(doc.Text, text, guard.Display(real));
        var apply = !dryRun && diff.Length > 0;
        int? undoId = null;

        if (apply)
        {
            var entry = journal.Begin("edit_batch", $"{guard.Display(real)}: {edits.Length} edit(s)");
            entry.CaptureContent(real);
            doc.SetText(text);
            doc.Save();
            undoId = journal.Commit(entry);
        }

        return new BatchEditResult(true, real, apply, dryRun, outcomes, diff, undoId);
    });

    [McpServerTool(Name = "regex_replace")]
    [Description("Regex search-and-replace (.NET syntax; $1 style substitutions) over one file or over files matched by a glob. Dry run by default: call once to review the diffs, then again with dryRun=false. Nothing is written if more files match than maxFiles. The regex runs against LF-normalized text, so use \\n for line breaks; the file's own line endings are restored on save. Binary, oversized and denied files are skipped and listed. Undoable as one unit.")]
    public object RegexReplace(
        [Description(".NET regular expression.")] string pattern,
        [Description("Replacement text. Use $1, ${name} for groups and $$ for a literal dollar.")] string replacement,
        [Description("A single file to edit. Mutually exclusive with glob.")] string? path = null,
        [Description("Glob such as **/*.cs, relative to directory. Mutually exclusive with path.")] string? glob = null,
        [Description("Directory the glob is relative to. Defaults to the first sandbox root.")] string? directory = null,
        [Description("Globs to exclude. Default: **/.git/**, **/.vs/**, **/bin/**, **/obj/**, **/node_modules/**.")] string[]? excludeGlobs = null,
        [Description("Preview only. Default true.")] bool dryRun = true,
        [Description("Abort if more files than this match the glob. Default 20; the server's hard cap also applies.")] int maxFiles = 20,
        [Description("Case-insensitive matching.")] bool ignoreCase = false,
        [Description("^ and $ match at line boundaries.")] bool multiline = false,
        [Description(". matches newlines.")] bool singleline = false) => Run(() =>
    {
        if ((path is null) == (glob is null))
        {
            throw new ArgumentException("Provide exactly one of path or glob.");
        }

        var options = RegexOptions.CultureInvariant
            | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)
            | (multiline ? RegexOptions.Multiline : RegexOptions.None)
            | (singleline ? RegexOptions.Singleline : RegexOptions.None);
        var regex = new Regex(pattern, options, RegexTimeout);

        var skipped = new List<string>();
        var files = path is not null
            ? [guard.ResolveExisting(path)]
            : EnumerateGlob(directory, glob!, excludeGlobs ?? DefaultExcludes, skipped);

        var cap = Math.Min(maxFiles, guard.Options.MaxBulkFiles);
        if (files.Count > cap)
        {
            throw new InvalidOperationException(
                $"{files.Count} files match, over the limit of {cap}. Narrow the glob or raise maxFiles (hard cap {guard.Options.MaxBulkFiles}). Nothing was changed.");
        }

        var results = new List<RegexFileResult>();
        var pending = new List<(TextDocument Doc, string NewText)>();
        var totalReplacements = 0;
        var lfReplacement = TextDocument.ToLf(replacement);

        foreach (var file in files)
        {
            TextDocument doc;
            try
            {
                doc = TextDocument.Load(file, guard.Options.MaxTextFileBytes);
            }
            catch (InvalidOperationException ex)
            {
                skipped.Add($"{guard.Display(file)}: {ex.Message}");
                continue;
            }

            var n = 0;
            var replaced = regex.Replace(TextDocument.ToLf(doc.Text), m =>
            {
                n++;
                return m.Result(lfReplacement);
            });

            if (n == 0)
            {
                continue;
            }

            var newText = doc.Normalize(replaced);
            results.Add(new RegexFileResult(file, n, UnifiedDiff.Create(doc.Text, newText, guard.Display(file))));
            pending.Add((doc, newText));
            totalReplacements += n;
        }

        int? undoId = null;
        if (!dryRun && pending.Count > 0)
        {
            var entry = journal.Begin("regex_replace", $"/{pattern}/ in {pending.Count} file(s)");
            foreach (var (doc, _) in pending)
            {
                entry.CaptureContent(doc.FilePath);
            }

            foreach (var (doc, newText) in pending)
            {
                doc.SetText(newText);
                doc.Save();
            }

            undoId = journal.Commit(entry);
        }

        return new RegexReplaceResult(
            true, !dryRun && pending.Count > 0, dryRun, files.Count, results.Count, totalReplacements, results, skipped, undoId);
    });

    private List<string> EnumerateGlob(string? directory, string glob, string[] excludes, List<string> skipped)
    {
        var dir = guard.ResolveExisting(directory ?? guard.Options.Roots[0]);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"{dir} is not a directory.");
        }

        var matcher = new Matcher(PathGuard.PathComparison);
        matcher.AddInclude(glob);
        foreach (var exclude in excludes)
        {
            matcher.AddExclude(exclude);
        }

        var matched = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(dir)));
        var files = new List<string>();
        foreach (var match in matched.Files)
        {
            var full = Path.Combine(dir, match.Path);
            try
            {
                files.Add(guard.ResolveExisting(full));
            }
            catch (UnauthorizedAccessException ex)
            {
                skipped.Add($"{match.Path}: {ex.Message}");
            }
        }

        files.Sort(PathGuard.PathComparer);
        return files;
    }

    private static string[] ContentToLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var parts = TextDocument.SplitLines(content);
        // A trailing newline in content means "end the last line", not "add an empty line".
        return content[^1] is '\n' or '\r' ? parts[..^1] : parts;
    }

    private static void RequireContent(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            throw new ArgumentException("content is required for inserts.");
        }
    }

    private static string[] TrimTrailingEmpty(string[] lines)
    {
        var n = lines.Length;
        while (n > 0 && lines[n - 1].Length == 0)
        {
            n--;
        }

        return lines[..n];
    }

    private static void CheckExpected(string? expected, List<string> lines, int start, int end)
    {
        if (expected is null)
        {
            return;
        }

        var wantedLines = TrimTrailingEmpty(TextDocument.SplitLines(TextDocument.ToLf(expected)));
        var actual = string.Join("\n", lines.Skip(start - 1).Take(end - start + 1));
        if (actual == string.Join("\n", wantedLines))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.Append($"Lines {start}-{end} do not match `expected`; no changes written. Actual content:\n");
        sb.Append(Numbered(lines, start, end));

        var relocated = FuzzyMatch.FindClosest(lines, wantedLines, max: 1)
            .FirstOrDefault(c => c.WhitespaceOnly && c.StartLine != start);
        if (relocated is not null)
        {
            sb.Append($"The expected text is now at lines {relocated.StartLine}-{relocated.EndLine}; the file has shifted. Retry with startLine={relocated.StartLine}.");
        }

        throw new InvalidOperationException(sb.ToString());
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>When an exact match fails, locate the nearest text and explain the difference.</summary>
    private static string NearMiss(string text, string needle)
    {
        var needleLines = TrimTrailingEmpty(TextDocument.SplitLines(TextDocument.ToLf(needle)));
        var lines = TextDocument.SplitLines(text);
        var candidates = FuzzyMatch.FindClosest(lines, needleLines);

        if (candidates.Count == 0)
        {
            return "oldText not found and nothing similar exists in the file. Re-read the file before retrying.";
        }

        var best = candidates[0];
        var sb = new StringBuilder("oldText not found. ");

        if (best.WhitespaceOnly)
        {
            sb.Append($"The same text exists at lines {best.StartLine}-{best.EndLine} but with different whitespace or indentation. Use this exact text as oldText:\n");
            sb.Append(Numbered(lines, best.StartLine, best.EndLine));
        }
        else
        {
            var actual = string.Join("\n", lines.Skip(best.StartLine - 1).Take(best.EndLine - best.StartLine + 1));
            sb.Append($"Closest match is at lines {best.StartLine}-{best.EndLine} ({best.Score:P0} similar). Diff of your oldText (-) against the file (+):\n");
            sb.Append(UnifiedDiff.Create(string.Join("\n", needleLines), actual, "oldText", context: 1));
        }

        if (candidates.Count > 1)
        {
            sb.Append("Other candidates: ");
            sb.Append(string.Join(", ", candidates.Skip(1).Select(c => $"lines {c.StartLine}-{c.EndLine} ({c.Score:P0})")));
            sb.Append('.');
        }

        return sb.ToString();
    }
}
