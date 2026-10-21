namespace Sonda.Analysis;

public static class LanguageDetector
{
    private const int MaxTokens = 3000;

    public static Language Detect(ReadOnlySpan<char> text, Language fallback = Language.Portuguese)
    {
        var tokens = new List<TokenSpan>(256);
        var sample = text.Length > 40_000 ? text[..40_000] : text;
        Tokenizer.Tokenize(sample, tokens);

        Span<char> buffer = stackalloc char[Tokenizer.MaxTokenLength];
        var portuguese = 0;
        var english = 0;
        var accented = 0;
        var limit = Math.Min(tokens.Count, MaxTokens);
        for (var i = 0; i < limit; i++)
        {
            var token = tokens[i];
            var word = buffer[..token.Length];
            CharFolding.LowerInto(sample.Slice(token.Start, token.Length), word);
            if (StopWords.PortugueseMarkers.Contains(word))
            {
                portuguese++;
            }

            if (StopWords.EnglishMarkers.Contains(word))
            {
                english++;
            }

            foreach (var c in word)
            {
                if (c is 'ã' or 'õ' or 'ç' or 'á' or 'é' or 'ê' or 'ó' or 'ô' or 'í' or 'ú' or 'à')
                {
                    accented++;
                    break;
                }
            }
        }

        portuguese += accented / 4;
        if (portuguese == 0 && english == 0)
        {
            return fallback;
        }

        return english > portuguese ? Language.English : Language.Portuguese;
    }
}
