using System.Text;
using System.Text.Unicode;

namespace Sonda.Analysis.Extraction;

public sealed record ExtractedContent(string Title, string Body, string? Description, Language LanguageHint);

public static class TextDecoder
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return Encoding.UTF32.GetString(bytes[4..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return Encoding.Latin1.GetString(bytes);
    }
}

public static class ContentExtractor
{
    public static readonly IReadOnlySet<string> HtmlExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".xml",
    };

    public static readonly IReadOnlySet<string> MarkdownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx",
    };

    public static ExtractedContent Extract(string fileName, string text)
    {
        var extension = Path.GetExtension(fileName);
        var fallbackTitle = Path.GetFileNameWithoutExtension(fileName);
        if (HtmlExtensions.Contains(extension))
        {
            return HtmlTextExtractor.Extract(text, fallbackTitle);
        }

        if (MarkdownExtensions.Contains(extension))
        {
            return MarkdownTextExtractor.Extract(text, fallbackTitle);
        }

        return PlainTextExtractor.Extract(text, fallbackTitle);
    }
}

public static class PlainTextExtractor
{
    private const int MaxTitleLength = 120;

    public static ExtractedContent Extract(string text, string fallbackTitle)
    {
        var body = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        var title = fallbackTitle;
        var firstBreak = body.IndexOf('\n', StringComparison.Ordinal);
        if (firstBreak > 0)
        {
            var firstLine = body[..firstBreak].Trim();
            var rest = body[(firstBreak + 1)..].Trim();
            if (firstLine.Length is > 0 and <= MaxTitleLength && rest.Length > 0)
            {
                title = firstLine;
                body = rest;
            }
        }

        return new ExtractedContent(title, body, null, Language.Unknown);
    }
}
