using System.ComponentModel;
using System.Text.RegularExpressions;
using FileInteractionMCP.Sandbox;
using FileInteractionMCP.Text;
using ModelContextProtocol.Server;
using static FileInteractionMCP.Tools.ToolSupport;

namespace FileInteractionMCP.Tools;

/// <summary>Targeted reads that would otherwise cost a search plus a full read.</summary>
[McpServerToolType]
internal sealed class ReadTools(PathGuard guard)
{
    private const int MaxContext = 100;
    private const int MaxMatches = 10;
    private const int MaxTailLines = 500;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [McpServerTool(Name = "read_around", ReadOnly = true, Idempotent = true)]
    [Description("Show numbered lines around each match of a pattern in one file, so a lookup does not need a separate search plus a full read. Match lines are marked 'N>' instead of 'N:'. Overlapping windows are merged. Returns the total match count so you know if more exist.")]
    public object ReadAround(
        [Description("File path.")] string path,
        [Description("Text to find (literal by default).")] string pattern,
        [Description("Lines of context on each side. Default 10, max 100.")] int context = 10,
        [Description("How many matches to show. Default 3, max 10.")] int maxMatches = 3,
        [Description("Treat pattern as a .NET regex.")] bool isRegex = false,
        [Description("Case-insensitive matching.")] bool ignoreCase = false) => Run(() =>
    {
        context = Math.Clamp(context, 0, MaxContext);
        maxMatches = Math.Clamp(maxMatches, 1, MaxMatches);

        var real = guard.ResolveExisting(path);
        var doc = TextDocument.Load(real, guard.Options.MaxTextFileBytes);
        var lines = doc.Lines();

        Func<string, bool> isMatch;
        if (isRegex)
        {
            var regex = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, RegexTimeout);
            isMatch = regex.IsMatch;
        }
        else
        {
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            isMatch = line => line.Contains(pattern, comparison);
        }

        var shown = new List<int>();
        var total = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!isMatch(lines[i]))
            {
                continue;
            }

            total++;
            if (shown.Count < maxMatches)
            {
                shown.Add(i + 1);
            }
        }

        var windows = new List<ReadWindow>();
        var marked = shown.ToHashSet();
        int? start = null, end = null;
        var matchesInWindow = new List<int>();

        foreach (var line in shown)
        {
            var s = Math.Max(1, line - context);
            var e = Math.Min(lines.Count, line + context);

            if (start is not null && s <= end + 1)
            {
                end = Math.Max(end!.Value, e);
                matchesInWindow.Add(line);
                continue;
            }

            if (start is not null)
            {
                windows.Add(new ReadWindow(start.Value, end!.Value, matchesInWindow.ToArray(), Numbered(lines, start.Value, end.Value, marked)));
            }

            start = s;
            end = e;
            matchesInWindow = [line];
        }

        if (start is not null)
        {
            windows.Add(new ReadWindow(start.Value, end!.Value, matchesInWindow.ToArray(), Numbered(lines, start.Value, end.Value, marked)));
        }

        return new ReadAroundResult(true, real, lines.Count, doc.EncodingName, doc.NewLineName, total, shown.Count, windows);
    });

    [McpServerTool(Name = "read_tail", ReadOnly = true, Idempotent = true)]
    [Description("Show the last N lines of a text file with line numbers, like tail. Useful for logs and for finding where to append.")]
    public object ReadTail(
        [Description("File path.")] string path,
        [Description("Number of lines. Default 50, max 500.")] int lines = 50) => Run(() =>
    {
        lines = Math.Clamp(lines, 1, MaxTailLines);

        var real = guard.ResolveExisting(path);
        var doc = TextDocument.Load(real, guard.Options.MaxTextFileBytes);
        var all = doc.Lines();

        if (all.Count == 0)
        {
            return new ReadTailResult(true, real, 0, 0, 0, string.Empty);
        }

        var start = Math.Max(1, all.Count - lines + 1);
        return new ReadTailResult(true, real, all.Count, start, all.Count, Numbered(all, start, all.Count));
    });
}
