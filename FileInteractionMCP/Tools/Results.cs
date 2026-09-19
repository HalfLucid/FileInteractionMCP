using System.ComponentModel;

namespace FileInteractionMCP.Tools;

public sealed record ToolError(bool Ok, string Error, string Message);

public sealed record RootsResult(
    IReadOnlyList<string> Roots,
    string TrashDirectory,
    IReadOnlyList<string> DenyPatterns,
    long MaxTextFileBytes,
    int MaxBulkFiles,
    string PathNote);

public sealed record MkdirResult(bool Ok, string Path, bool Created);

public sealed record DeleteResult(bool Ok, string Path, string Kind, bool Permanent, string? TrashedTo, int? UndoId);

public sealed record MoveResult(bool Ok, string Source, string Destination, string Kind, int? UndoId);

public sealed record CopyResult(bool Ok, string Source, string Destination, string Kind, int SkippedLinks);

public sealed record AppendResult(bool Ok, string Path, bool Created, int LinesAppended, string NewLine, string Encoding, int? UndoId);

public sealed record FileInfoResult(
    bool Ok,
    string Path,
    string Kind,
    long? SizeBytes,
    DateTime ModifiedUtc,
    int? FileCount,
    int? DirectoryCount,
    bool? IsText,
    string? Encoding,
    bool? HasBom,
    string? NewLine,
    bool? MixedLineEndings,
    int? CrlfCount,
    int? LfCount,
    int? CrCount,
    int? LineCount,
    bool? EndsWithNewline,
    string? Note);

public sealed record ConvertResult(
    bool Ok,
    string Path,
    bool Applied,
    bool DryRun,
    string EncodingBefore,
    string EncodingAfter,
    string NewLineBefore,
    string NewLineAfter,
    bool TrailingNewlineAdded,
    int? UndoId);

public sealed record EditResult(
    bool Ok,
    string Path,
    bool Applied,
    bool DryRun,
    int LinesBefore,
    int LinesAfter,
    string Diff,
    int? UndoId);

public sealed record BatchEditResult(
    bool Ok,
    string Path,
    bool Applied,
    bool DryRun,
    IReadOnlyList<EditPairOutcome> Edits,
    string Diff,
    int? UndoId);

public sealed record EditPairOutcome(int Index, int Occurrences, bool NoOp);

public sealed record RegexReplaceResult(
    bool Ok,
    bool Applied,
    bool DryRun,
    int FilesScanned,
    int FilesChanged,
    int TotalReplacements,
    IReadOnlyList<RegexFileResult> Files,
    IReadOnlyList<string> Skipped,
    int? UndoId);

public sealed record RegexFileResult(string Path, int Replacements, string Diff);

public sealed record ReadAroundResult(
    bool Ok,
    string Path,
    int TotalLines,
    string Encoding,
    string NewLine,
    int TotalMatches,
    int MatchesShown,
    IReadOnlyList<ReadWindow> Windows);

public sealed record ReadWindow(int StartLine, int EndLine, IReadOnlyList<int> MatchLines, string Text);

public sealed record ReadTailResult(bool Ok, string Path, int TotalLines, int StartLine, int EndLine, string Text);

public sealed record UndoListResult(bool Ok, IReadOnlyList<UndoEntryInfo> Entries);

public sealed record UndoEntryInfo(int Id, string Tool, string Summary, DateTime Timestamp, IReadOnlyList<string> Paths);

public sealed record UndoResult(bool Ok, int UndoneId, string Tool, string Summary, IReadOnlyList<string> Paths, int? RedoId);

/// <summary>One search/replace pair for edit_batch.</summary>
public sealed record EditPair(
    [property: Description("Exact text to find. Whitespace and indentation must match; line endings are matched against the file's own.")]
    string OldText,
    [property: Description("Replacement text. Empty string deletes the match.")]
    string NewText,
    [property: Description("How many occurrences must exist. All of them are replaced. Default 1.")]
    int? ExpectedCount = null);

internal static class ToolSupport
{
    /// <summary>Runs a tool body and turns any failure into a structured error the model can read.</summary>
    public static object Run(Func<object> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolError(false, ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>"12: text" style listing of a 1-based inclusive line range.</summary>
    public static string Numbered(IReadOnlyList<string> lines, int start, int end, ISet<int>? marked = null)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = start; i <= end && i <= lines.Count; i++)
        {
            sb.Append(i).Append(marked is not null && marked.Contains(i) ? "> " : ": ").Append(lines[i - 1]).Append('\n');
        }

        return sb.ToString();
    }
}
