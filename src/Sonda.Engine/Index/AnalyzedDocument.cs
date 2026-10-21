using Sonda.Analysis;
using Sonda.Storage;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Index;

internal sealed class PositionList
{
    private int[] _items = new int[2];

    public int Count { get; private set; }

    public ReadOnlySpan<int> Items => _items.AsSpan(0, Count);

    public void Add(int position)
    {
        if (Count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[Count++] = position;
    }
}

internal sealed class AnalyzedField
{
    public int Length { get; set; }

    public Dictionary<string, PositionList> Terms { get; } = new(StringComparer.Ordinal);

    public void Add(string term, int position)
    {
        if (!Terms.TryGetValue(term, out var list))
        {
            list = new PositionList();
            Terms[term] = list;
        }

        list.Add(position);
        Length++;
    }
}

internal sealed class AnalyzedDocument
{
    public required SourceDocument Source { get; init; }

    public required Language Language { get; init; }

    public required AnalyzedField?[] Fields { get; init; }

    public required List<StoredValue> Stored { get; init; }

    public required string[] Keywords { get; init; }

    public required long[] Numerics { get; init; }

    public long StoredBytes { get; init; }
}

public sealed class DocumentBuilder(TextAnalyzer analyzer, bool storeBody, int maxIndexedChars)
{
    public TextAnalyzer Analyzer { get; } = analyzer;

    internal AnalyzedDocument Build(SourceDocument document)
    {
        var body = document.Body.Length > maxIndexedChars ? document.Body[..maxIndexedChars] : document.Body;
        var sample = body.Length > 40_000 ? body.AsSpan(0, 40_000) : body.AsSpan();
        var language = Analyzer.ResolveLanguage(sample, document.LanguageHint);
        var fields = new AnalyzedField?[Schema.Fields.All.Count];
        var tokens = new List<AnalyzedToken>(Math.Min(body.Length / 5 + 16, 1 << 16));

        var title = new AnalyzedField();
        Analyzer.Analyze(document.Title, language, tokens);
        foreach (var token in tokens)
        {
            title.Add(token.Term, token.Position);
        }

        fields[Schema.Title] = title;

        tokens.Clear();
        var bodyField = new AnalyzedField();
        Analyzer.Analyze(body, language, tokens);
        foreach (var token in tokens)
        {
            bodyField.Add(token.Term, token.Position);
        }

        fields[Schema.Body] = bodyField;

        tokens.Clear();
        var path = new AnalyzedField();
        Analyzer.AnalyzePlain(document.Path, tokens);
        foreach (var token in tokens)
        {
            path.Add(token.Term, token.Position);
        }

        fields[Schema.Path] = path;

        var extension = NormalizeExtension(document.Extension);
        var languageCode = language.ToCode();
        var source = TextAnalyzer.NormalizeKeyword(document.Source);
        fields[Schema.Id] = Keyword(document.Key);
        fields[Schema.Extension] = extension.Length > 0 ? Keyword(extension) : null;
        fields[Schema.Language] = Keyword(languageCode);
        fields[Schema.Source] = Keyword(source);

        var stored = new List<StoredValue>(10)
        {
            StoredValue.Of(Schema.Id, document.Key),
            StoredValue.Of(Schema.Title, document.Title),
            StoredValue.Of(Schema.Path, document.Path),
            StoredValue.Of(Schema.Source, document.Source),
            StoredValue.Of(Schema.Extension, extension),
            StoredValue.Of(Schema.Language, languageCode),
            StoredValue.Of(Schema.Size, document.Size),
            StoredValue.Of(Schema.Modified, document.Modified.ToUnixTimeMilliseconds()),
        };

        if (!string.IsNullOrWhiteSpace(document.Description))
        {
            stored.Add(StoredValue.Of(Schema.Description, document.Description));
        }

        if (storeBody)
        {
            stored.Add(StoredValue.Of(Schema.Body, body));
        }

        var keywords = new string[Schema.Fields.All.Count];
        keywords[Schema.Extension] = extension;
        keywords[Schema.Language] = languageCode;
        keywords[Schema.Source] = source;

        var numerics = new long[Schema.Fields.All.Count];
        numerics[Schema.Modified] = document.Modified.ToUnixTimeMilliseconds();
        numerics[Schema.Size] = document.Size;

        var storedBytes = (document.Title.Length + document.Path.Length + document.Key.Length + (storeBody ? body.Length : 0)) * 2L;
        return new AnalyzedDocument
        {
            Source = document,
            Language = language,
            Fields = fields,
            Stored = stored,
            Keywords = keywords,
            Numerics = numerics,
            StoredBytes = storedBytes,
        };
    }

    public static string NormalizeExtension(string extension)
    {
        var value = extension.Trim().TrimStart('.');
        return value.Length == 0 ? string.Empty : CharFolding.Fold(value);
    }

    private static AnalyzedField Keyword(string value)
    {
        var field = new AnalyzedField();
        field.Add(value, 0);
        return field;
    }
}

internal sealed class BufferedDocument(List<StoredValue> stored, int[] lengths, string[] keywords, long[] numerics)
{
    public List<StoredValue> Stored { get; } = stored;

    public int[] Lengths { get; } = lengths;

    public string[] Keywords { get; } = keywords;

    public long[] Numerics { get; } = numerics;
}

internal static class FieldExtensions
{
    public static bool Indexes(this FieldInfo field) => field.IndexOptions != IndexOptions.None;
}
