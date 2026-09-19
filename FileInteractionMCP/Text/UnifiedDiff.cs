using System.Text;
using DiffPlex;
using DiffPlex.Chunkers;

namespace FileInteractionMCP.Text;

/// <summary>Produces a standard unified diff between two texts, with hunks and context lines.</summary>
public static class UnifiedDiff
{
    public const int DefaultContext = 3;
    public const int MaxDiffChars = 24_000;

    public static string Create(string oldText, string newText, string label, int context = DefaultContext)
    {
        // Compare on LF-only text so CR never shows up as a phantom difference.
        var result = Differ.Instance.CreateDiffs(
            TextDocument.ToLf(oldText),
            TextDocument.ToLf(newText),
            ignoreWhiteSpace: false,
            ignoreCase: false,
            LineChunker.Instance);

        var a = result.PiecesOld;
        var b = result.PiecesNew;
        var blocks = result.DiffBlocks;

        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(label).Append('\n');
        sb.Append("+++ b/").Append(label).Append('\n');

        int i = 0;
        while (i < blocks.Count)
        {
            // Merge blocks whose context windows would overlap into a single hunk.
            int j = i;
            while (j + 1 < blocks.Count &&
                   blocks[j + 1].DeleteStartA - (blocks[j].DeleteStartA + blocks[j].DeleteCountA) <= 2 * context)
            {
                j++;
            }

            var first = blocks[i];
            var last = blocks[j];

            int aStart = Math.Max(0, first.DeleteStartA - context);
            int aEnd = Math.Min(a.Count, last.DeleteStartA + last.DeleteCountA + context);
            int lead = first.DeleteStartA - aStart;
            int trail = aEnd - (last.DeleteStartA + last.DeleteCountA);
            int bStart = first.InsertStartB - lead;
            int bEnd = last.InsertStartB + last.InsertCountB + trail;

            sb.Append("@@ -").Append(HunkRange(aStart, aEnd - aStart))
              .Append(" +").Append(HunkRange(bStart, bEnd - bStart)).Append(" @@\n");

            int aPos = aStart;
            for (int k = i; k <= j; k++)
            {
                var block = blocks[k];
                for (; aPos < block.DeleteStartA; aPos++)
                {
                    sb.Append(' ').Append(a[aPos]).Append('\n');
                }

                for (int d = 0; d < block.DeleteCountA; d++)
                {
                    sb.Append('-').Append(a[block.DeleteStartA + d]).Append('\n');
                }

                for (int n = 0; n < block.InsertCountB; n++)
                {
                    sb.Append('+').Append(b[block.InsertStartB + n]).Append('\n');
                }

                aPos = block.DeleteStartA + block.DeleteCountA;
            }

            for (; aPos < aEnd; aPos++)
            {
                sb.Append(' ').Append(a[aPos]).Append('\n');
            }

            i = j + 1;

            if (sb.Length > MaxDiffChars)
            {
                sb.Append("... diff truncated ...\n");
                break;
            }
        }

        return sb.ToString();
    }

    private static string HunkRange(int zeroBasedStart, int count) =>
        count == 0 ? $"{zeroBasedStart},0" : count == 1 ? $"{zeroBasedStart + 1}" : $"{zeroBasedStart + 1},{count}";
}
