using System.Text;
using System.Text.RegularExpressions;

namespace Sonda.Analysis.Extraction;

public static partial class MarkdownTextExtractor
{
    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"(\*\*|__|~~|`)")]
    private static partial Regex EmphasisPattern();

    public static ExtractedContent Extract(string markdown, string fallbackTitle)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        string? title = null;
        var body = new StringBuilder(text.Length);
        var inFrontMatter = false;
        var lineNumber = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            lineNumber++;
            if (lineNumber == 1 && line == "---")
            {
                inFrontMatter = true;
                continue;
            }

            if (inFrontMatter)
            {
                if (line == "---")
                {
                    inFrontMatter = false;
                }
                else if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                {
                    title ??= line[6..].Trim().Trim('"', '\'');
                }

                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                {
                    level++;
                }

                trimmed = trimmed[level..].Trim();
                if (level == 1 && title is null)
                {
                    title = Clean(trimmed);
                    continue;
                }
            }
            else
            {
                trimmed = trimmed.TrimStart('>', ' ', '-', '*', '+');
                if (trimmed.Length > 0 && char.IsAsciiDigit(trimmed[0]))
                {
                    var dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
                    if (dot is > 0 and < 4)
                    {
                        trimmed = trimmed[(dot + 2)..];
                    }
                }
            }

            if (trimmed.StartsWith('|'))
            {
                trimmed = trimmed.Replace('|', ' ');
                if (trimmed.Trim().Trim('-', ':', ' ').Length == 0)
                {
                    continue;
                }
            }

            body.Append(Clean(trimmed)).Append('\n');
        }

        var finalTitle = string.IsNullOrWhiteSpace(title) ? fallbackTitle : title;
        return new ExtractedContent(finalTitle, body.ToString().Trim(), null, Language.Unknown);
    }

    private static string Clean(string line)
    {
        line = ImagePattern().Replace(line, "$1");
        line = LinkPattern().Replace(line, "$1");
        line = HtmlTagPattern().Replace(line, " ");
        line = EmphasisPattern().Replace(line, string.Empty);
        return line.Trim();
    }
}
