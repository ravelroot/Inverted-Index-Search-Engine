using System.Net;
using System.Text;

namespace Sonda.Analysis.Extraction;

public static class HtmlTextExtractor
{
    private static readonly HashSet<string> SkippedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "math",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p",
        "pre", "section", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul", "summary", "details",
        "option", "caption", "body", "html",
    };

    public static ExtractedContent Extract(string html, string fallbackTitle)
    {
        var output = new StringBuilder(html.Length / 2);
        string? title = null;
        string? heading = null;
        string? description = null;
        var languageHint = Language.Unknown;
        var headingStart = -1;
        var i = 0;

        while (i < html.Length)
        {
            var c = html[i];
            if (c == '<')
            {
                if (Matches(html, i, "<!--"))
                {
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? html.Length : end + 3;
                    continue;
                }

                if (i + 1 < html.Length && (html[i + 1] == '!' || html[i + 1] == '?'))
                {
                    var end = html.IndexOf('>', i + 1);
                    i = end < 0 ? html.Length : end + 1;
                    continue;
                }

                var tag = ParseTag(html, i);
                if (tag is null)
                {
                    AppendChar(output, c);
                    i++;
                    continue;
                }

                i = tag.End;
                var name = tag.Name;

                if (!tag.Closing && SkippedElements.Contains(name) && !tag.SelfClosing)
                {
                    i = SkipElement(html, i, name);
                    continue;
                }

                if (!tag.Closing && name.Equals("title", StringComparison.OrdinalIgnoreCase))
                {
                    var end = IndexOfClosing(html, i, "title");
                    var raw = end < 0 ? html[i..] : html[i..end];
                    title ??= Collapse(WebUtility.HtmlDecode(StripTags(raw)));
                    i = end < 0 ? html.Length : SkipToTagEnd(html, end);
                    continue;
                }

                if (!tag.Closing && name.Equals("meta", StringComparison.OrdinalIgnoreCase))
                {
                    if (tag.Attributes.TryGetValue("name", out var metaName)
                        && metaName.Equals("description", StringComparison.OrdinalIgnoreCase)
                        && tag.Attributes.TryGetValue("content", out var content))
                    {
                        description ??= Collapse(WebUtility.HtmlDecode(content));
                    }

                    continue;
                }

                if (!tag.Closing && name.Equals("html", StringComparison.OrdinalIgnoreCase)
                    && tag.Attributes.TryGetValue("lang", out var lang))
                {
                    languageHint = LanguageCodes.FromCode(lang);
                }

                if (name.Equals("h1", StringComparison.OrdinalIgnoreCase))
                {
                    if (!tag.Closing)
                    {
                        headingStart = output.Length;
                    }
                    else if (headingStart >= 0 && heading is null)
                    {
                        heading = Collapse(output.ToString(headingStart, output.Length - headingStart));
                        headingStart = -1;
                    }
                }

                if (!tag.Closing && name.Equals("img", StringComparison.OrdinalIgnoreCase)
                    && tag.Attributes.TryGetValue("alt", out var alt) && alt.Length > 0)
                {
                    AppendText(output, WebUtility.HtmlDecode(alt));
                    AppendChar(output, ' ');
                }

                if (BlockElements.Contains(name))
                {
                    AppendNewLine(output);
                }

                continue;
            }

            if (c == '&')
            {
                var end = html.IndexOf(';', i + 1, Math.Min(12, html.Length - i - 1));
                if (end > i)
                {
                    var decoded = WebUtility.HtmlDecode(html.Substring(i, end - i + 1));
                    AppendText(output, decoded);
                    i = end + 1;
                    continue;
                }
            }

            AppendChar(output, c);
            i++;
        }

        var body = output.ToString().Trim();
        var finalTitle = !string.IsNullOrWhiteSpace(title) ? title
            : !string.IsNullOrWhiteSpace(heading) ? heading
            : fallbackTitle;
        return new ExtractedContent(finalTitle!, body, description, languageHint);
    }

    private static void AppendText(StringBuilder output, string text)
    {
        foreach (var ch in text)
        {
            AppendChar(output, ch);
        }
    }

    private static void AppendChar(StringBuilder output, char c)
    {
        if (char.IsWhiteSpace(c))
        {
            if (output.Length > 0 && output[^1] != ' ' && output[^1] != '\n')
            {
                output.Append(' ');
            }

            return;
        }

        output.Append(c);
    }

    private static void AppendNewLine(StringBuilder output)
    {
        while (output.Length > 0 && output[^1] == ' ')
        {
            output.Length--;
        }

        if (output.Length > 0 && output[^1] != '\n')
        {
            output.Append('\n');
        }
    }

    private static bool Matches(string text, int index, string value)
        => string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static int SkipElement(string html, int from, string name)
    {
        var end = IndexOfClosing(html, from, name);
        return end < 0 ? html.Length : SkipToTagEnd(html, end);
    }

    private static int SkipToTagEnd(string html, int from)
    {
        var end = html.IndexOf('>', from);
        return end < 0 ? html.Length : end + 1;
    }

    private static int IndexOfClosing(string html, int from, string name)
    {
        var needle = "</" + name;
        var index = from;
        while (true)
        {
            var found = html.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return -1;
            }

            var after = found + needle.Length;
            if (after >= html.Length || html[after] == '>' || char.IsWhiteSpace(html[after]))
            {
                return found;
            }

            index = after;
        }
    }

    private static string StripTags(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var inside = false;
        foreach (var ch in raw)
        {
            if (ch == '<')
            {
                inside = true;
            }
            else if (ch == '>')
            {
                inside = false;
            }
            else if (!inside)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim();
    }

    private sealed record HtmlTag(string Name, bool Closing, bool SelfClosing, int End, Dictionary<string, string> Attributes);

    private static HtmlTag? ParseTag(string html, int start)
    {
        var i = start + 1;
        var closing = false;
        if (i < html.Length && html[i] == '/')
        {
            closing = true;
            i++;
        }

        var nameStart = i;
        while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] == '-' || html[i] == ':'))
        {
            i++;
        }

        if (i == nameStart || !char.IsAsciiLetter(html[nameStart]))
        {
            return null;
        }

        var name = html[nameStart..i];
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selfClosing = false;

        while (i < html.Length)
        {
            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i >= html.Length)
            {
                break;
            }

            if (html[i] == '>')
            {
                i++;
                return new HtmlTag(name, closing, selfClosing, i, attributes);
            }

            if (html[i] == '/')
            {
                selfClosing = true;
                i++;
                continue;
            }

            var attrStart = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '=' && html[i] != '>' && html[i] != '/')
            {
                i++;
            }

            var attrName = html[attrStart..i];
            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            var value = string.Empty;
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                if (i < html.Length && (html[i] == '"' || html[i] == '\''))
                {
                    var quote = html[i];
                    var valueEnd = html.IndexOf(quote, i + 1);
                    if (valueEnd < 0)
                    {
                        return null;
                    }

                    value = html[(i + 1)..valueEnd];
                    i = valueEnd + 1;
                }
                else
                {
                    var valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }

                    value = html[valueStart..i];
                }
            }

            if (attrName.Length > 0)
            {
                attributes.TryAdd(attrName, value);
            }
            else
            {
                i++;
            }
        }

        return new HtmlTag(name, closing, selfClosing, html.Length, attributes);
    }
}
