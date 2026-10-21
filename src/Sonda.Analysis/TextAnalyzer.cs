using System.Collections.Concurrent;

namespace Sonda.Analysis;

public sealed record AnalyzerOptions
{
    public StemmingMode Stemming { get; init; } = StemmingMode.Auto;

    public Language DefaultLanguage { get; init; } = Language.Portuguese;

    public bool RemoveStopWords { get; init; } = true;

    public bool FoldAccents { get; init; } = true;
}

public readonly record struct AnalyzedToken(string Term, int Position, int StartOffset, int EndOffset);

public sealed record QueryToken(string Original, int Position, IReadOnlyList<string> Variants);

public sealed class TextAnalyzer
{
    private const int MaxCacheEntries = 400_000;

    private readonly IStemmer _portuguese = new PortugueseStemmer();
    private readonly IStemmer _english = new EnglishStemmer();
    private readonly ConcurrentDictionary<string, string>[] _caches;
    private readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>>[] _lookups;

    [ThreadStatic]
    private static List<TokenSpan>? _spanBuffer;

    public TextAnalyzer(AnalyzerOptions? options = null)
    {
        Options = options ?? new AnalyzerOptions();
        _caches = new ConcurrentDictionary<string, string>[3];
        _lookups = new ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>>[3];
        for (var i = 0; i < _caches.Length; i++)
        {
            _caches[i] = new ConcurrentDictionary<string, string>(Environment.ProcessorCount, 4096, StringComparer.Ordinal);
            _lookups[i] = _caches[i].GetAlternateLookup<ReadOnlySpan<char>>();
        }
    }

    public AnalyzerOptions Options { get; }

    public IReadOnlyList<Language> QueryLanguages => Options.Stemming switch
    {
        StemmingMode.Portuguese => [Language.Portuguese],
        StemmingMode.English => [Language.English],
        StemmingMode.None => [Language.Unknown],
        _ => [Language.Portuguese, Language.English],
    };

    public Language ResolveLanguage(ReadOnlySpan<char> text, Language hint)
    {
        return Options.Stemming switch
        {
            StemmingMode.Portuguese => Language.Portuguese,
            StemmingMode.English => Language.English,
            StemmingMode.None => hint != Language.Unknown ? hint : LanguageDetector.Detect(text, Options.DefaultLanguage),
            _ => hint != Language.Unknown ? hint : LanguageDetector.Detect(text, Options.DefaultLanguage),
        };
    }

    public void Analyze(ReadOnlySpan<char> text, Language language, List<AnalyzedToken> output)
    {
        var effective = Options.Stemming == StemmingMode.None ? Language.Unknown : language;
        var spans = RentSpans();
        Tokenizer.Tokenize(text, spans);
        Span<char> buffer = stackalloc char[Tokenizer.MaxTokenLength];
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var word = buffer[..span.Length];
            CharFolding.LowerInto(text.Slice(span.Start, span.Length), word);
            var term = AnalyzeLowered(word, effective);
            if (term is not null)
            {
                output.Add(new AnalyzedToken(term, i, span.Start, span.Start + span.Length));
            }
        }

        spans.Clear();
    }

    public List<AnalyzedToken> Analyze(string text, Language language)
    {
        var output = new List<AnalyzedToken>();
        Analyze(text.AsSpan(), language, output);
        return output;
    }

    public void AnalyzePlain(ReadOnlySpan<char> text, List<AnalyzedToken> output)
    {
        var spans = RentSpans();
        Tokenizer.Tokenize(text, spans);
        Span<char> buffer = stackalloc char[Tokenizer.MaxTokenLength];
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var word = buffer[..span.Length];
            text.Slice(span.Start, span.Length).CopyTo(word);
            CharFolding.FoldInPlace(word);
            output.Add(new AnalyzedToken(new string(word), i, span.Start, span.Start + span.Length));
        }

        spans.Clear();
    }

    public string? AnalyzeWord(ReadOnlySpan<char> word, Language language)
    {
        if (word.Length == 0 || word.Length > Tokenizer.MaxTokenLength)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[word.Length];
        CharFolding.LowerInto(word, buffer);
        return AnalyzeLowered(buffer, Options.Stemming == StemmingMode.None ? Language.Unknown : language);
    }

    public IReadOnlyList<string> QueryVariants(ReadOnlySpan<char> word)
    {
        var variants = new List<string>(2);
        foreach (var language in QueryLanguages)
        {
            var term = AnalyzeWord(word, language);
            if (term is null)
            {
                return [];
            }

            if (!variants.Contains(term))
            {
                variants.Add(term);
            }
        }

        return variants;
    }

    public List<QueryToken> AnalyzeQuery(string text)
    {
        var tokens = new List<QueryToken>();
        var spans = Tokenizer.Tokenize(text);
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var word = text.Substring(span.Start, span.Length);
            var variants = QueryVariants(word);
            if (variants.Count > 0)
            {
                tokens.Add(new QueryToken(word, i, variants));
            }
        }

        return tokens;
    }

    public static string NormalizeKeyword(string value) => CharFolding.Fold(value.Trim());

    private string? AnalyzeLowered(ReadOnlySpan<char> lowered, Language language)
    {
        var slot = (int)language;
        if (_lookups[slot].TryGetValue(lowered, out var cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        var key = new string(lowered);
        var result = Compute(key, language);
        var cache = _caches[slot];
        if (cache.Count >= MaxCacheEntries)
        {
            cache.Clear();
        }

        cache.TryAdd(key, result ?? string.Empty);
        return result;
    }

    private string? Compute(string lowered, Language language)
    {
        var folded = CharFolding.Fold(lowered);
        if (Options.RemoveStopWords)
        {
            var stopLanguage = language == Language.Unknown ? Options.DefaultLanguage : language;
            if (StopWords.For(stopLanguage).Contains(lowered) || StopWords.FoldedFor(stopLanguage).Contains(folded))
            {
                return null;
            }
        }

        if (language == Language.Unknown || folded.Length < 3 || !IsAlphabetic(folded))
        {
            return Options.FoldAccents ? folded : lowered;
        }

        return language == Language.English ? _english.Stem(folded) : _portuguese.Stem(folded);
    }

    private static bool IsAlphabetic(string word)
    {
        foreach (var c in word)
        {
            if (!char.IsLetter(c))
            {
                return false;
            }
        }

        return true;
    }

    private static List<TokenSpan> RentSpans()
    {
        var spans = _spanBuffer ??= new List<TokenSpan>(1024);
        spans.Clear();
        if (spans.Capacity > 1 << 20)
        {
            spans.Capacity = 1024;
        }

        return spans;
    }
}
