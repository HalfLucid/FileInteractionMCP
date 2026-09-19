using System.Text;
using System.Text.RegularExpressions;

namespace FileInteractionMCP.Text;

/// <summary>
/// A text file loaded with its encoding, byte-order mark and dominant line ending detected, so that
/// edits write back exactly the same flavor of file. Saves are atomic (temp file + rename).
/// </summary>
public sealed partial class TextDocument
{
    [GeneratedRegex(@"\r\n|\n|\r")]
    private static partial Regex LineBreak();

    public string FilePath { get; }
    public Encoding Encoding { get; }
    public string NewLine { get; }
    public string Text { get; private set; }

    public int CrlfCount { get; }
    public int LfCount { get; }
    public int CrCount { get; }

    private TextDocument(string filePath, Encoding encoding, string newLine, string text, (int Crlf, int Lf, int Cr) counts)
    {
        FilePath = filePath;
        Encoding = encoding;
        NewLine = newLine;
        Text = text;
        (CrlfCount, LfCount, CrCount) = counts;
    }

    public bool HasBom => Encoding.GetPreamble().Length > 0;

    public bool MixedLineEndings =>
        (CrlfCount > 0 ? 1 : 0) + (LfCount > 0 ? 1 : 0) + (CrCount > 0 ? 1 : 0) > 1;

    public string EncodingName => NameOf(Encoding);

    public string NewLineName => NameOf(NewLine);

    public bool EndsWithNewline => Text.Length > 0 && (Text[^1] == '\n' || Text[^1] == '\r');

    /// <summary>
    /// Logical lines without the trailing empty element a final newline would otherwise produce.
    /// An empty file yields zero lines.
    /// </summary>
    public List<string> Lines()
    {
        if (Text.Length == 0)
        {
            return [];
        }

        var parts = LineBreak().Split(Text).ToList();
        if (EndsWithNewline)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    /// <summary>Splits arbitrary text on any line-break flavor. "a\nb\n" gives ["a","b",""].</summary>
    public static string[] SplitLines(string text) => LineBreak().Split(text);

    /// <summary>Rewrites every line break in <paramref name="content"/> to this document's own.</summary>
    public string Normalize(string content) => NormalizeTo(content, NewLine);

    public static string NormalizeTo(string content, string newLine) => LineBreak().Replace(content, newLine);

    /// <summary>Rewrites every line break to LF, for regex work where CR must not get in the way.</summary>
    public static string ToLf(string content) => NormalizeTo(content, "\n");

    public void SetText(string text) => Text = text;

    /// <summary>A copy of this document re-targeted to another encoding and/or line ending.</summary>
    public TextDocument WithFormat(Encoding encoding, string newLine)
    {
        var text = NormalizeTo(Text, newLine);
        return new TextDocument(FilePath, encoding, newLine, text, CountLineEndings(text));
    }

    public static TextDocument CreateNew(string realPath) =>
        new(realPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), Environment.NewLine, string.Empty, (0, 0, 0));

    public static TextDocument Load(string realPath, long maxBytes)
    {
        var info = new FileInfo(realPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"File not found: {realPath}");
        }

        if (info.Length > maxBytes)
        {
            throw new InvalidOperationException(
                $"File is {info.Length:N0} bytes, over the {maxBytes:N0} byte limit for text operations.");
        }

        var bytes = File.ReadAllBytes(realPath);
        var (encoding, preamble) = DetectEncoding(bytes);

        if (encoding is not UnicodeEncoding && LooksBinary(bytes))
        {
            throw new InvalidOperationException(
                "File appears to be binary (contains NUL bytes); text tools refuse to edit it.");
        }

        var text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        var counts = CountLineEndings(text);
        return new TextDocument(realPath, encoding, PickNewLine(counts), text, counts);
    }

    public void Save()
    {
        if (ReferenceEquals(Encoding, Encoding.Latin1) && Text.Any(c => c > 0xFF))
        {
            throw new InvalidOperationException(
                "File is Latin-1 encoded and the new content contains characters outside Latin-1. " +
                "Remove those characters or convert the file to UTF-8 first (fs_convert).");
        }

        var preamble = Encoding.GetPreamble();
        var body = Encoding.GetBytes(Text);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        Sandbox.FileOps.WriteAtomic(FilePath, bytes);
    }

    public static Encoding ParseEncoding(string name) => name.Trim().ToLowerInvariant() switch
    {
        "utf-8" or "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        "utf-8-bom" or "utf8-bom" or "utf-8-sig" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
        "utf-16-le" or "utf-16" or "utf16" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
        "utf-16-be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
        "latin1" or "latin-1" or "iso-8859-1" => Encoding.Latin1,
        _ => throw new ArgumentException($"Unknown encoding '{name}'. Use utf-8, utf-8-bom, utf-16-le, utf-16-be or latin1."),
    };

    public static string ParseNewLine(string name) => name.Trim().ToLowerInvariant() switch
    {
        "crlf" or "\r\n" or "windows" => "\r\n",
        "lf" or "\n" or "unix" => "\n",
        "cr" or "\r" => "\r",
        _ => throw new ArgumentException($"Unknown line ending '{name}'. Use crlf, lf or cr."),
    };

    public static string NameOf(Encoding encoding) => encoding switch
    {
        UTF8Encoding u => u.GetPreamble().Length > 0 ? "utf-8-bom" : "utf-8",
        UnicodeEncoding u => u.GetPreamble() is [0xFE, 0xFF] ? "utf-16-be" : "utf-16-le",
        _ when ReferenceEquals(encoding, Encoding.Latin1) => "latin1",
        _ => encoding.WebName,
    };

    public static string NameOf(string newLine) => newLine switch
    {
        "\r\n" => "crlf",
        "\n" => "lf",
        "\r" => "cr",
        _ => "?",
    };

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        }

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            strictUtf8.GetString(bytes);
            return (strictUtf8, 0);
        }
        catch (DecoderFallbackException)
        {
            // Latin-1 round-trips every byte, so nothing is lost on save.
            return (Encoding.Latin1, 0);
        }
    }

    private static bool LooksBinary(byte[] bytes)
    {
        var probe = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < probe; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static (int Crlf, int Lf, int Cr) CountLineEndings(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        return (crlf, lf, cr);
    }

    private static string PickNewLine((int Crlf, int Lf, int Cr) counts)
    {
        var (crlf, lf, cr) = counts;
        if (crlf == 0 && lf == 0 && cr == 0)
        {
            return Environment.NewLine;
        }

        if (crlf >= lf && crlf >= cr)
        {
            return "\r\n";
        }

        return lf >= cr ? "\n" : "\r";
    }
}
