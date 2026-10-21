using Sonda.Analysis;

namespace Sonda.Engine.Search;

public sealed record HighlightFragment(string Text, bool Match);

public static class Highlighter
{
    private const int MaxAnalyzedChars = 400_000;

    public static IReadOnlyList<HighlightFragment> Highlight(
        TextAnalyzer analyzer,
        string text,
        Language language,
        IReadOnlySet<string> terms,
        int fragmentLength = 240,
        bool wholeText = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var analyzedLength = Math.Min(text.Length, MaxAnalyzedChars);
        var matches = new List<(int Start, int End, string Term)>();
        if (terms.Count > 0)
        {
            var tokens = new List<AnalyzedToken>();
            analyzer.Analyze(text.AsSpan(0, analyzedLength), language, tokens);
            foreach (var token in tokens)
            {
                if (terms.Contains(token.Term))
                {
                    matches.Add((token.StartOffset, token.EndOffset, token.Term));
                }
            }
        }

        if (wholeText)
        {
            return Build(text, 0, text.Length, matches, false, false, keepLines: true);
        }

        if (matches.Count == 0)
        {
            var end = WordBoundaryForward(text, Math.Min(text.Length, fragmentLength));
            return Build(text, 0, end, matches, false, end < text.Length);
        }

        var bestStart = 0;
        var bestScore = -1d;
        for (var i = 0; i < matches.Count; i++)
        {
            var windowStart = Math.Max(0, matches[i].Start - (fragmentLength / 4));
            var windowEnd = windowStart + fragmentLength;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;
            for (var j = i; j < matches.Count && matches[j].End <= windowEnd; j++)
            {
                distinct.Add(matches[j].Term);
                total++;
            }

            var score = (distinct.Count * 10) + total - (i * 0.001);
            if (score > bestScore)
            {
                bestScore = score;
                bestStart = windowStart;
            }
        }

        var start = WordBoundaryBackward(text, bestStart);
        var stop = WordBoundaryForward(text, Math.Min(text.Length, start + fragmentLength));
        return Build(text, start, stop, matches, start > 0, stop < text.Length);
    }

    private static List<HighlightFragment> Build(string text, int start, int end, List<(int Start, int End, string Term)> matches, bool leadingEllipsis, bool trailingEllipsis, bool keepLines = false)
    {
        var fragments = new List<HighlightFragment>();
        var cursor = start;
        if (leadingEllipsis)
        {
            fragments.Add(new HighlightFragment("… ", false));
        }

        foreach (var match in matches)
        {
            if (match.Start < cursor || match.End > end)
            {
                continue;
            }

            if (match.Start > cursor)
            {
                fragments.Add(new HighlightFragment(keepLines ? text[cursor..match.Start] : Clean(text[cursor..match.Start]), false));
            }

            fragments.Add(new HighlightFragment(text[match.Start..match.End], true));
            cursor = match.End;
        }

        if (cursor < end)
        {
            fragments.Add(new HighlightFragment(keepLines ? text[cursor..end] : Clean(text[cursor..end]), false));
        }

        if (trailingEllipsis)
        {
            fragments.Add(new HighlightFragment(" …", false));
        }

        return fragments;
    }

    private static string Clean(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    private static int WordBoundaryBackward(string text, int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        var limit = Math.Max(0, index - 30);
        for (var i = index; i > limit; i--)
        {
            if (char.IsWhiteSpace(text[i - 1]))
            {
                return i;
            }
        }

        return limit == 0 ? 0 : index;
    }

    private static int WordBoundaryForward(string text, int index)
    {
        if (index >= text.Length)
        {
            return text.Length;
        }

        var limit = Math.Min(text.Length, index + 30);
        for (var i = index; i < limit; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return index;
    }
}
