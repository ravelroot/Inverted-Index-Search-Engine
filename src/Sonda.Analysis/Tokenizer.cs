using System.Globalization;

namespace Sonda.Analysis;

public readonly record struct TokenSpan(int Start, int Length);

public static class Tokenizer
{
    public const int MaxTokenLength = 64;

    public static void Tokenize(ReadOnlySpan<char> text, List<TokenSpan> output)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !IsWordChar(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            var start = i;
            while (i < text.Length && (IsWordChar(text[i]) || IsNumericJoiner(text, i)))
            {
                i++;
            }

            var length = i - start;
            output.Add(new TokenSpan(start, Math.Min(length, MaxTokenLength)));
        }
    }

    public static List<TokenSpan> Tokenize(string text)
    {
        var output = new List<TokenSpan>();
        Tokenize(text.AsSpan(), output);
        return output;
    }

    public static bool IsWordChar(char c)
    {
        if (c < 128)
        {
            return (uint)(c - 'a') <= 'z' - 'a' || (uint)(c - 'A') <= 'Z' - 'A' || (uint)(c - '0') <= 9;
        }

        if (char.IsLetterOrDigit(c))
        {
            return true;
        }

        var category = char.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    private static bool IsNumericJoiner(ReadOnlySpan<char> text, int index)
    {
        var c = text[index];
        if (c != '.' && c != ',')
        {
            return false;
        }

        return index > 0
            && index + 1 < text.Length
            && char.IsAsciiDigit(text[index - 1])
            && char.IsAsciiDigit(text[index + 1]);
    }
}
