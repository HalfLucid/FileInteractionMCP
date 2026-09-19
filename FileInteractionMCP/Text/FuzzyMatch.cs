using System.Text.RegularExpressions;

namespace FileInteractionMCP.Text;

/// <summary>
/// Finds where a block of text almost occurs in a file, for diagnostics when an exact match fails.
/// Lines are compared with whitespace collapsed, so indentation and tab/space differences score as equal.
/// </summary>
public static partial class FuzzyMatch
{
    /// <summary>A window of the file resembling the needle. Lines are 1-based and inclusive.</summary>
    public sealed record Candidate(int StartLine, int EndLine, double Score, bool WhitespaceOnly);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string Collapse(string s) => Whitespace().Replace(s.Trim(), " ");

    public static List<Candidate> FindClosest(IReadOnlyList<string> fileLines, IReadOnlyList<string> needleLines, int max = 3)
    {
        var needle = needleLines.Select(Collapse).ToArray();
        var n = needle.Length;
        while (n > 0 && needle[n - 1].Length == 0)
        {
            n--;
        }

        if (n == 0 || fileLines.Count == 0)
        {
            return [];
        }

        var file = fileLines.Select(Collapse).ToArray();

        // Only windows that share at least one exact (collapsed) line with the needle are worth scoring.
        var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int k = 0; k < n; k++)
        {
            if (needle[k].Length == 0)
            {
                continue;
            }

            if (!positions.TryGetValue(needle[k], out var list))
            {
                positions[needle[k]] = list = [];
            }

            list.Add(k);
        }

        var starts = new HashSet<int>();
        for (int idx = 0; idx < file.Length; idx++)
        {
            if (positions.TryGetValue(file[idx], out var ks))
            {
                foreach (var k in ks)
                {
                    starts.Add(Math.Max(0, idx - k));
                }
            }
        }

        var scored = new List<(int Start, double Score)>();
        foreach (var start in starts)
        {
            double sum = 0;
            for (int k = 0; k < n && start + k < file.Length; k++)
            {
                sum += LineSimilarity(needle[k], file[start + k]);
            }

            var score = sum / n;
            if (score >= 0.3)
            {
                scored.Add((start, score));
            }
        }

        var result = new List<Candidate>();
        foreach (var (start, score) in scored.OrderByDescending(s => s.Score).ThenBy(s => s.Start))
        {
            var end = Math.Min(file.Length - 1, start + n - 1);
            if (result.Any(c => start <= c.EndLine - 1 && end >= c.StartLine - 1))
            {
                continue;
            }

            result.Add(new Candidate(start + 1, end + 1, score, score >= 0.999));
            if (result.Count == max)
            {
                break;
            }
        }

        return result;
    }

    private static double LineSimilarity(string a, string b)
    {
        if (a == b)
        {
            return 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            return 0.6;
        }

        var ta = a.Split(' ');
        var tb = b.Split(' ');
        var union = ta.Union(tb).Count();
        var jaccard = union == 0 ? 0 : (double)ta.Intersect(tb).Count() / union;
        return jaccard >= 0.5 ? jaccard * 0.8 : 0;
    }
}
