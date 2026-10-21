using System.Diagnostics;
using System.Text;
using Sonda.Analysis;
using Sonda.Engine.Crawling;
using Sonda.Engine.Index;
using Sonda.Engine.Search;
using Sonda.Storage;
using Sonda.Storage.Formats;

namespace Sonda.Engine;

public sealed record SondaOptions
{
    public string IndexPath { get; init; } = Path.Combine("dados", "indice");

    public IndexWriterOptions Writer { get; init; } = new();

    public IndexingOptions Indexing { get; init; } = new();

    public string Similarity { get; init; } = "BM25";

    public double K1 { get; init; } = 1.2;

    public double B { get; init; } = 0.75;

    public float TitleBoost { get; init; } = 2f;

    public DefaultOperator DefaultOperator { get; init; } = DefaultOperator.And;

    public int MaxResultWindow { get; init; } = 1000;

    public int SnippetLength { get; init; } = 240;
}

public sealed record SearchRequest
{
    public string Query { get; init; } = string.Empty;

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public SortMode Sort { get; init; } = SortMode.Relevance;

    public string? Similarity { get; init; }

    public DefaultOperator? Operator { get; init; }

    public bool Explain { get; init; }

    public bool Facets { get; init; } = true;

    public bool ExactTotal { get; init; } = true;

    public bool Snippets { get; init; } = true;

    public string? Extension { get; init; }

    public string? Language { get; init; }

    public string? Source { get; init; }
}

public sealed record SearchHit(
    string Key,
    string Title,
    string Path,
    string Source,
    string Extension,
    string Language,
    long Size,
    DateTimeOffset Modified,
    float Score,
    IReadOnlyList<HighlightFragment> TitleFragments,
    IReadOnlyList<HighlightFragment> Snippet,
    string? Description,
    Explanation? Explanation);

public sealed record SearchResponse(
    string Query,
    string ParsedQuery,
    long Total,
    bool TotalIsLowerBound,
    int Page,
    int PageSize,
    double ElapsedMs,
    string Similarity,
    bool Relaxed,
    long Evaluated,
    IReadOnlyList<SearchHit> Hits,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Facets);

public sealed record SegmentStatistics(string Name, int Documents, int Deleted, long SizeBytes, string Origin, DateTimeOffset CreatedAt);

public sealed record FieldStatistics(string Field, long Terms, long Documents, long Tokens, double AverageLength);

public sealed record IndexStatistics(
    string Path,
    long Generation,
    int Documents,
    int Deleted,
    long SizeBytes,
    DateTimeOffset? LastCommit,
    IReadOnlyList<SegmentStatistics> Segments,
    IReadOnlyList<FieldStatistics> Fields,
    IReadOnlyDictionary<string, int> BySource,
    IReadOnlyDictionary<string, int> ByLanguage,
    IReadOnlyDictionary<string, int> ByExtension,
    WriterStatistics? Writer);

public sealed record TermEntry(string Term, long DocFreq, long TotalTermFreq, double Idf);

public sealed record PostingEntry(string Key, string Title, string Path, int Freq, IReadOnlyList<int> Positions);

public sealed record SegmentTermInfo(string Segment, int DocFreq, long TotalTermFreq, bool Singleton, int SkipBlocks, long DocPointer, long PositionPointer, int MaxFreq, int MinLength);

public sealed record TermPostings(string Field, string Term, long DocFreq, long TotalTermFreq, double Idf, IReadOnlyList<SegmentTermInfo> Segments, IReadOnlyList<PostingEntry> Postings);

public sealed record AnalysisStep(string Token, int Position, int Start, int End, string Lowercase, bool StopWord, string? Portuguese, string? English, string? Indexed);

public sealed record AnalysisResult(string Language, IReadOnlyList<AnalysisStep> Tokens);

public sealed record DocumentView(StoredDocument Document, IReadOnlyList<HighlightFragment> Body, bool Truncated, int Length);

public sealed record VerificationResult(int Files, int Valid, IReadOnlyList<string> Problems, double ElapsedMs);

public sealed class SondaEngine : IDisposable
{
    private readonly SemaphoreSlim _indexingGate = new(1, 1);
    private readonly QueryParser _parser;
    private readonly string _indexPath;
    private int _disposed;

    private SondaEngine(SondaOptions options, IndexWriter? writer, SearcherManager searchers, TextAnalyzer analyzer)
    {
        Options = options;
        Writer = writer;
        Searchers = searchers;
        Analyzer = analyzer;
        _indexPath = Path.GetFullPath(options.IndexPath);
        _parser = new QueryParser(analyzer, [(Schema.Title, options.TitleBoost), (Schema.Body, 1f)]);
        if (writer is not null)
        {
            writer.Committed += commit =>
            {
                searchers.Refresh();
                if (writer.Options.AutoMerge)
                {
                    _ = MergeInBackgroundAsync();
                }
            };
        }
    }

    public SondaOptions Options { get; }

    public IndexWriter? Writer { get; }

    public SearcherManager Searchers { get; }

    public TextAnalyzer Analyzer { get; }

    public bool ReadOnly => Writer is null;

    public bool IsIndexing => _indexingGate.CurrentCount == 0;

    public string? LastBackgroundError { get; private set; }

    public static SondaEngine Open(SondaOptions options)
    {
        var writer = IndexWriter.Open(options.IndexPath, options.Writer);
        var searchers = new SearcherManager(writer.AcquireSnapshot);
        return new SondaEngine(options, writer, searchers, writer.Analyzer);
    }

    public static SondaEngine OpenReadOnly(SondaOptions options)
    {
        var path = options.IndexPath;
        var searchers = new SearcherManager(() => IndexSnapshot.OpenReadOnly(path));
        return new SondaEngine(options, null, searchers, new TextAnalyzer(options.Writer.Analyzer));
    }

    private async Task MergeInBackgroundAsync()
    {
        try
        {
            await Writer!.MaybeMergeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            LastBackgroundError = ex.Message;
        }
    }

    public SearchResponse Search(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var page = Math.Max(1, request.Page);
        var top = Math.Min(Options.MaxResultWindow, page * pageSize);
        var defaultOperator = request.Operator ?? Options.DefaultOperator;
        var similarity = Similarity.FromName(request.Similarity ?? Options.Similarity, Options.K1, Options.B);

        using var lease = Searchers.Acquire();
        var searcher = lease.Searcher.WithSimilarity(similarity);
        var parsed = _parser.Parse(request.Query, defaultOperator);
        var query = WithFilters(parsed.Query, request);
        var results = searcher.Search(query, top, request.Sort, request.ExactTotal, request.Facets, cancellationToken);
        var relaxed = false;
        if (results.TotalHits == 0 && defaultOperator == DefaultOperator.And && parsed.CanRelax)
        {
            var orQuery = WithFilters(_parser.Parse(request.Query, DefaultOperator.Or).Query, request);
            var orResults = searcher.Search(orQuery, top, request.Sort, request.ExactTotal, request.Facets, cancellationToken);
            if (orResults.TotalHits > 0)
            {
                query = orQuery;
                results = orResults;
                relaxed = true;
            }
        }

        var rewritten = searcher.Rewrite(query);
        var terms = new HashSet<(int Field, string Term)>();
        rewritten.ExtractTerms(terms);
        var titleTerms = terms.Where(t => t.Field == Schema.Title).Select(t => t.Term).ToHashSet(StringComparer.Ordinal);
        var bodyTerms = terms.Where(t => t.Field == Schema.Body).Select(t => t.Term).ToHashSet(StringComparer.Ordinal);

        var hits = new List<SearchHit>();
        foreach (var scoreDoc in results.Hits.Skip((page - 1) * pageSize).Take(pageSize))
        {
            var stored = searcher.Document(scoreDoc.Doc);
            var language = LanguageCodes.FromCode(stored.Language);
            var titleFragments = request.Snippets
                ? Highlighter.Highlight(Analyzer, stored.Title, language, titleTerms, wholeText: true)
                : [new HighlightFragment(stored.Title, false)];
            var snippetSource = stored.Body ?? stored.Description ?? string.Empty;
            var snippet = request.Snippets
                ? Highlighter.Highlight(Analyzer, snippetSource, language, bodyTerms, Options.SnippetLength)
                : [];
            var explanation = request.Explain ? searcher.Explain(query, scoreDoc.Doc) : null;
            hits.Add(new SearchHit(
                stored.Key,
                stored.Title,
                stored.Path,
                stored.Source,
                stored.Extension,
                stored.Language,
                stored.Size,
                stored.Modified,
                scoreDoc.Score,
                titleFragments,
                snippet,
                stored.Description,
                explanation));
        }

        return new SearchResponse(
            request.Query,
            query.ToString() ?? string.Empty,
            results.TotalHits,
            results.TotalIsLowerBound,
            page,
            pageSize,
            stopwatch.Elapsed.TotalMilliseconds,
            similarity.Name,
            relaxed,
            results.Evaluated,
            hits,
            results.Facets);
    }

    private static Query WithFilters(Query query, SearchRequest request)
    {
        var filters = new List<(Query, Occur)>();
        if (!string.IsNullOrWhiteSpace(request.Extension))
        {
            filters.Add((new TermQuery(Schema.Extension, DocumentBuilder.NormalizeExtension(request.Extension)), Occur.Filter));
        }

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            var language = LanguageCodes.FromCode(request.Language);
            filters.Add((new TermQuery(Schema.Language, language == Language.Unknown ? request.Language.Trim().ToLowerInvariant() : language.ToCode()), Occur.Filter));
        }

        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            filters.Add((new TermQuery(Schema.Source, TextAnalyzer.NormalizeKeyword(request.Source)), Occur.Filter));
        }

        if (filters.Count == 0)
        {
            return query;
        }

        filters.Insert(0, (query, Occur.Must));
        return new BooleanQuery(filters);
    }

    public StoredDocument? GetDocument(string key)
    {
        using var lease = Searchers.Acquire();
        var doc = lease.Searcher.FindByKey(key);
        return doc is null ? null : lease.Searcher.Document(doc.Value);
    }

    public DocumentView? GetDocumentView(string key, string? query, int maxChars = 60_000)
    {
        using var lease = Searchers.Acquire();
        var searcher = lease.Searcher;
        var doc = searcher.FindByKey(key);
        if (doc is null)
        {
            return null;
        }

        var stored = searcher.Document(doc.Value);
        var body = stored.Body ?? stored.Description ?? string.Empty;
        var truncated = body.Length > maxChars;
        if (truncated)
        {
            body = body[..maxChars];
        }

        var terms = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(query))
        {
            try
            {
                var extracted = new HashSet<(int Field, string Term)>();
                searcher.Rewrite(_parser.Parse(query, DefaultOperator.Or).Query).ExtractTerms(extracted);
                terms.UnionWith(extracted.Where(t => t.Field == Schema.Body).Select(t => t.Term));
            }
            catch (QueryParseException)
            {
            }
        }

        var fragments = Highlighter.Highlight(Analyzer, body, LanguageCodes.FromCode(stored.Language), terms, wholeText: true);
        return new DocumentView(stored with { Body = null }, fragments, truncated, stored.Body?.Length ?? 0);
    }

    public IndexStatistics GetStatistics()
    {
        using var lease = Searchers.Acquire();
        var searcher = lease.Searcher;
        var directory = new IndexDirectory(_indexPath);
        var segments = searcher.Leaves.Select(l => new SegmentStatistics(
            l.Reader.Name,
            l.Reader.MaxDoc,
            l.Reader.DelCount,
            SegmentSize(directory, l.Reader.Name),
            l.Reader.Core.Info.Origin,
            l.Reader.Core.Info.CreatedAt)).ToList();

        var fields = new List<FieldStatistics>();
        foreach (var field in new[] { Schema.Title, Schema.Body, Schema.Path, Schema.Id })
        {
            long termCount = 0;
            foreach (var leaf in searcher.Leaves)
            {
                termCount += leaf.Reader.Terms(field)?.Stats.TermCount ?? 0;
            }

            var collection = searcher.CollectionStatistics(field);
            fields.Add(new FieldStatistics(Schema.NameOf(field), termCount, collection.DocCount, collection.SumTotalTermFreq, collection.AverageLength));
        }

        DateTimeOffset? lastCommit = searcher.Snapshot.Commit.UserData.TryGetValue("timestamp", out var raw)
            && long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

        return new IndexStatistics(
            _indexPath,
            searcher.Snapshot.Commit.Generation,
            searcher.NumDocs,
            searcher.MaxDoc - searcher.NumDocs,
            directory.SizeInBytes(),
            lastCommit,
            segments,
            fields,
            CountKeyword(searcher, Schema.Source),
            CountKeyword(searcher, Schema.Language),
            CountKeyword(searcher, Schema.Extension),
            Writer?.GetStatistics());
    }

    private static long SegmentSize(IndexDirectory directory, string segment)
    {
        long total = 0;
        foreach (var file in SegmentInfoFormat.FilesFor(segment))
        {
            if (directory.FileExists(file))
            {
                total += directory.FileLength(file);
            }
        }

        return total;
    }

    private static Dictionary<string, int> CountKeyword(IndexSearcher searcher, int field)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var leaf in searcher.Leaves)
        {
            var values = leaf.Reader.Keywords(field);
            if (values is null)
            {
                continue;
            }

            for (var doc = 0; doc < leaf.Reader.MaxDoc; doc++)
            {
                if (leaf.Reader.IsDeleted(doc))
                {
                    continue;
                }

                var value = values.Get(doc);
                if (value is not null)
                {
                    counts[value] = counts.GetValueOrDefault(value) + 1;
                }
            }
        }

        return counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
    }

    public IReadOnlyList<TermEntry> Terms(int field, string? prefix, int limit, bool byFrequency)
    {
        limit = Math.Clamp(limit, 1, 500);
        using var lease = Searchers.Acquire();
        var searcher = lease.Searcher;
        var collection = searcher.CollectionStatistics(field);
        var docCount = Math.Max(1, collection.DocCount);
        var prefixBytes = Encoding.UTF8.GetBytes(prefix is null ? string.Empty : CharFolding.Fold(prefix.Trim()));
        var totals = new Dictionary<string, (long DocFreq, long Total)>(StringComparer.Ordinal);
        const int scanLimit = 300_000;
        foreach (var leaf in searcher.Leaves)
        {
            var terms = leaf.Reader.Terms(field);
            if (terms is null)
            {
                continue;
            }

            var iterator = terms.SeekCeil(prefixBytes);
            var scanned = 0;
            while (iterator.MoveNext() && iterator.TermBytes.StartsWith(prefixBytes))
            {
                var term = iterator.Term;
                var current = totals.GetValueOrDefault(term);
                totals[term] = (current.DocFreq + iterator.Meta.DocFreq, current.Total + iterator.Meta.TotalTermFreq);
                if (++scanned >= (byFrequency ? scanLimit : limit * 4))
                {
                    break;
                }
            }
        }

        var ordered = byFrequency
            ? totals.OrderByDescending(t => t.Value.DocFreq).ThenBy(t => t.Key, StringComparer.Ordinal)
            : totals.OrderBy(t => Encoding.UTF8.GetBytes(t.Key), Utf8Order.Instance);
        return ordered.Take(limit)
            .Select(t => new TermEntry(t.Key, t.Value.DocFreq, t.Value.Total, Bm25Similarity.Idf(t.Value.DocFreq, docCount)))
            .ToList();
    }

    private sealed class Utf8Order : IComparer<byte[]>
    {
        public static readonly Utf8Order Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }

    public TermPostings? Postings(int field, string term, int limit)
    {
        limit = Math.Clamp(limit, 1, 200);
        using var lease = Searchers.Acquire();
        var searcher = lease.Searcher;
        var info = Schema.Fields[field];
        if (info is null || !info.IsIndexed)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(term);
        var segments = new List<SegmentTermInfo>();
        var postings = new List<PostingEntry>();
        long docFreq = 0;
        long total = 0;
        foreach (var leaf in searcher.Leaves)
        {
            var terms = leaf.Reader.Terms(field);
            if (terms is null || !terms.TryGetTerm(bytes, out var meta))
            {
                continue;
            }

            docFreq += meta.DocFreq;
            total += meta.TotalTermFreq;
            segments.Add(new SegmentTermInfo(
                leaf.Reader.Name,
                meta.DocFreq,
                meta.TotalTermFreq,
                meta.DocFreq == 1,
                meta.DocFreq / ForUtil.BlockSize,
                meta.DocStart,
                meta.PosStart,
                meta.MaxFreq,
                NormEncoding.Decode(meta.MinNorm)));

            var enumerator = leaf.Reader.Core.PostingsFile.Postings(meta, info.IndexOptions, info.HasPositions);
            while (postings.Count < limit && enumerator.NextDoc() != PostingsEnum.NoMoreDocs)
            {
                var doc = enumerator.DocId;
                var positions = new List<int>();
                if (info.HasPositions)
                {
                    for (var i = 0; i < enumerator.Freq; i++)
                    {
                        positions.Add(enumerator.NextPosition());
                    }
                }

                if (leaf.Reader.IsDeleted(doc))
                {
                    continue;
                }

                var stored = IndexSearcher.ToStored(leaf.Reader.Core.Stored.Document(doc));
                postings.Add(new PostingEntry(stored.Key, stored.Title, stored.Path, enumerator.Freq, positions));
            }
        }

        if (segments.Count == 0)
        {
            return null;
        }

        var collection = searcher.CollectionStatistics(field);
        return new TermPostings(Schema.NameOf(field), term, docFreq, total, Bm25Similarity.Idf(docFreq, Math.Max(docFreq, collection.DocCount)), segments, postings);
    }

    public AnalysisResult Analyze(string text)
    {
        var language = Analyzer.ResolveLanguage(text, Language.Unknown);
        var spans = Tokenizer.Tokenize(text);
        var steps = new List<AnalysisStep>(spans.Count);
        foreach (var span in spans.Take(200))
        {
            var original = text.Substring(span.Start, span.Length);
            var lower = original.ToLowerInvariant();
            var stop = StopWords.For(language).Contains(lower);
            var portuguese = Analyzer.AnalyzeWord(original, Language.Portuguese);
            var english = Analyzer.AnalyzeWord(original, Language.English);
            var indexed = Analyzer.AnalyzeWord(original, language);
            steps.Add(new AnalysisStep(original, steps.Count, span.Start, span.Start + span.Length, lower, stop, portuguese, english, indexed));
        }

        return new AnalysisResult(language.ToCode(), steps);
    }

    public async Task<IndexingRunResult> IndexAsync(SourceDefinition source, IndexingMode mode, IProgress<IndexingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var writer = Writer ?? throw new InvalidOperationException("O índice foi aberto apenas para leitura.");
        await _indexingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pipeline = new IndexingPipeline(writer, Options.Indexing);
            return await pipeline.RunAsync(source, mode, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexingGate.Release();
        }
    }

    public int RemoveSource(string sourceName)
    {
        var writer = Writer ?? throw new InvalidOperationException("O índice foi aberto apenas para leitura.");
        var removed = writer.DeleteByKeyPrefix(sourceName + ":");
        writer.Commit();
        return removed;
    }

    public async Task<int> OptimizeAsync(int maxSegments, CancellationToken cancellationToken = default)
    {
        var writer = Writer ?? throw new InvalidOperationException("O índice foi aberto apenas para leitura.");
        return await writer.ForceMergeAsync(maxSegments, cancellationToken).ConfigureAwait(false);
    }

    public VerificationResult Verify()
    {
        var stopwatch = Stopwatch.StartNew();
        var directory = new IndexDirectory(_indexPath);
        var problems = new List<string>();
        var files = 0;
        var valid = 0;
        using var lease = Searchers.Acquire();
        var referenced = lease.Searcher.Snapshot.Commit.ReferencedFiles().ToList();
        foreach (var name in referenced)
        {
            files++;
            try
            {
                using var file = directory.OpenFile(name);
                CodecUtil.VerifyFile(file);
                valid++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{name}: {ex.Message}");
            }
        }

        return new VerificationResult(files, valid, problems, stopwatch.Elapsed.TotalMilliseconds);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Writer?.Dispose();
        Searchers.Dispose();
        _indexingGate.Dispose();
    }
}
