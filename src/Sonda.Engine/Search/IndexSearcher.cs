using System.Collections.Concurrent;
using Sonda.Engine.Index;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Search;

public enum SortMode
{
    Relevance,
    Recent,
}

public readonly record struct ScoreDoc(int Doc, float Score);

public sealed record TopDocs(
    long TotalHits,
    bool TotalIsLowerBound,
    IReadOnlyList<ScoreDoc> Hits,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Facets,
    long Evaluated);

public sealed record StoredDocument(
    string Key,
    string Title,
    string Path,
    string Source,
    string Extension,
    string Language,
    long Size,
    DateTimeOffset Modified,
    string? Description,
    string? Body);

public sealed class IndexSearcher
{
    private readonly ConcurrentDictionary<(int, string), TermStatistics> _termStats = new();
    private readonly ConcurrentDictionary<int, CollectionStatistics> _collectionStats = new();

    public IndexSearcher(IndexSnapshot snapshot, Similarity? similarity = null)
    {
        Snapshot = snapshot;
        Similarity = similarity ?? new Bm25Similarity();
        var leaves = new List<LeafContext>(snapshot.Segments.Count);
        var docBase = 0;
        for (var i = 0; i < snapshot.Segments.Count; i++)
        {
            var reader = snapshot.Segments[i];
            leaves.Add(new LeafContext(reader, docBase, i));
            docBase += reader.MaxDoc;
        }

        Leaves = leaves;
        MaxDoc = docBase;
        NumDocs = snapshot.Segments.Sum(s => s.NumDocs);
    }

    private IndexSearcher(IndexSearcher source, Similarity similarity)
    {
        Snapshot = source.Snapshot;
        Leaves = source.Leaves;
        MaxDoc = source.MaxDoc;
        NumDocs = source.NumDocs;
        Similarity = similarity;
        _termStats = source._termStats;
        _collectionStats = source._collectionStats;
    }

    public IndexSnapshot Snapshot { get; }

    public IReadOnlyList<LeafContext> Leaves { get; }

    public Similarity Similarity { get; }

    public int MaxDoc { get; }

    public int NumDocs { get; }

    public IndexSearcher WithSimilarity(Similarity similarity) => new(this, similarity);

    public CollectionStatistics CollectionStatistics(int field)
    {
        return _collectionStats.GetOrAdd(field, f =>
        {
            long docCount = 0;
            long sumTotalTermFreq = 0;
            foreach (var leaf in Leaves)
            {
                var terms = leaf.Reader.Terms(f);
                if (terms is null)
                {
                    continue;
                }

                docCount += terms.Stats.DocCount;
                sumTotalTermFreq += terms.Stats.SumTotalTermFreq;
            }

            return new CollectionStatistics(f, MaxDoc, docCount, sumTotalTermFreq);
        });
    }

    public TermStatistics TermStatistics(int field, string term)
    {
        return _termStats.GetOrAdd((field, term), key =>
        {
            long docFreq = 0;
            long totalTermFreq = 0;
            var bytes = System.Text.Encoding.UTF8.GetBytes(key.Item2);
            foreach (var leaf in Leaves)
            {
                var terms = leaf.Reader.Terms(key.Item1);
                if (terms is not null && terms.TryGetTerm(bytes, out var meta))
                {
                    docFreq += meta.DocFreq;
                    totalTermFreq += meta.TotalTermFreq;
                }
            }

            return new TermStatistics(key.Item2, docFreq, totalTermFreq);
        });
    }

    public Query Rewrite(Query query)
    {
        var current = query;
        for (var i = 0; i < 16; i++)
        {
            var next = current.Rewrite(this);
            if (ReferenceEquals(next, current))
            {
                return current;
            }

            current = next;
        }

        return current;
    }

    public TopDocs Search(Query query, int top, SortMode sort = SortMode.Relevance, bool exactTotal = true, bool facets = false, CancellationToken cancellationToken = default)
    {
        top = Math.Max(1, top);
        var mode = sort == SortMode.Relevance && !exactTotal && !facets ? ScoreMode.TopScores : ScoreMode.Complete;
        var rewritten = Rewrite(query);
        var weight = rewritten.CreateWeight(this, mode, 1f);
        var heap = new TopHeap(top, sort == SortMode.Recent);
        long total = 0;
        long evaluated = 0;
        var facetCounts = facets ? new Dictionary<int, Dictionary<string, int>>() : null;

        foreach (var leaf in Leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scorer = weight.CreateScorer(leaf);
            if (scorer is null)
            {
                continue;
            }

            var deleted = leaf.Reader.Deleted;
            var modified = sort == SortMode.Recent ? leaf.Reader.Numerics(Schema.Modified) : null;
            var ordinalCounts = facets ? Schema.KeywordFields.Select(f => leaf.Reader.Keywords(f)).ToArray() : null;
            var leafCounts = ordinalCounts?.Select(k => k is null ? [] : new int[k.Values.Length]).ToArray();

            int doc;
            var checks = 0;
            while ((doc = scorer.NextDoc()) != Scorer.NoMoreDocs)
            {
                if (deleted is not null && deleted.Get(doc))
                {
                    continue;
                }

                total++;
                if ((++checks & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (ordinalCounts is not null)
                {
                    for (var f = 0; f < ordinalCounts.Length; f++)
                    {
                        var values = ordinalCounts[f];
                        if (values is null)
                        {
                            continue;
                        }

                        var ordinal = values.Ordinals[doc];
                        if (ordinal >= 0)
                        {
                            leafCounts![f][ordinal]++;
                        }
                    }
                }

                var score = scorer.Score();
                var key = modified?[doc] ?? 0;
                if (heap.TryInsert(score, key, leaf.DocBase + doc) && mode == ScoreMode.TopScores && heap.IsFull)
                {
                    scorer.SetMinCompetitiveScore(heap.MinScore);
                }
            }

            if (scorer is WandScorer wand)
            {
                evaluated += wand.Evaluated;
            }
            else
            {
                evaluated = total;
            }

            if (facetCounts is not null)
            {
                for (var f = 0; f < Schema.KeywordFields.Length; f++)
                {
                    var values = ordinalCounts![f];
                    if (values is null)
                    {
                        continue;
                    }

                    var field = Schema.KeywordFields[f];
                    if (!facetCounts.TryGetValue(field, out var counts))
                    {
                        counts = new Dictionary<string, int>(StringComparer.Ordinal);
                        facetCounts[field] = counts;
                    }

                    for (var o = 0; o < values.Values.Length; o++)
                    {
                        if (leafCounts![f][o] > 0)
                        {
                            counts[values.Values[o]] = counts.GetValueOrDefault(values.Values[o]) + leafCounts[f][o];
                        }
                    }
                }
            }
        }

        var facetResult = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        if (facetCounts is not null)
        {
            foreach (var (field, counts) in facetCounts)
            {
                facetResult[Schema.NameOf(field)] = counts
                    .OrderByDescending(c => c.Value)
                    .ThenBy(c => c.Key, StringComparer.Ordinal)
                    .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
            }
        }

        return new TopDocs(total, mode == ScoreMode.TopScores, heap.Drain(), facetResult, evaluated);
    }

    public Explanation Explain(Query query, int globalDoc)
    {
        var leaf = LeafFor(globalDoc);
        var weight = Rewrite(query).CreateWeight(this, ScoreMode.Complete, 1f);
        return weight.Explain(leaf, globalDoc - leaf.DocBase);
    }

    public LeafContext LeafFor(int globalDoc)
    {
        var low = 0;
        var high = Leaves.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) >>> 1;
            var leaf = Leaves[mid];
            if (globalDoc < leaf.DocBase)
            {
                high = mid - 1;
            }
            else if (globalDoc >= leaf.DocBase + leaf.Reader.MaxDoc)
            {
                low = mid + 1;
            }
            else
            {
                return leaf;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(globalDoc));
    }

    public StoredDocument Document(int globalDoc)
    {
        var leaf = LeafFor(globalDoc);
        return ToStored(leaf.Reader.Core.Stored.Document(globalDoc - leaf.DocBase));
    }

    public int? FindByKey(string key)
    {
        foreach (var leaf in Leaves)
        {
            foreach (var doc in leaf.Reader.Core.DocsForKey(key))
            {
                if (!leaf.Reader.IsDeleted(doc))
                {
                    return leaf.DocBase + doc;
                }
            }
        }

        return null;
    }

    public static StoredDocument ToStored(IReadOnlyList<StoredValue> values)
    {
        string key = string.Empty, title = string.Empty, path = string.Empty, source = string.Empty, extension = string.Empty, language = string.Empty;
        string? description = null;
        string? body = null;
        long size = 0;
        long modified = 0;
        foreach (var value in values)
        {
            switch (value.Field)
            {
                case Schema.Id: key = value.Text ?? key; break;
                case Schema.Title: title = value.Text ?? title; break;
                case Schema.Path: path = value.Text ?? path; break;
                case Schema.Source: source = value.Text ?? source; break;
                case Schema.Extension: extension = value.Text ?? extension; break;
                case Schema.Language: language = value.Text ?? language; break;
                case Schema.Description: description = value.Text; break;
                case Schema.Body: body = value.Text; break;
                case Schema.Size: size = value.Number; break;
                case Schema.Modified: modified = value.Number; break;
            }
        }

        return new StoredDocument(key, title, path, source, extension, language, size, DateTimeOffset.FromUnixTimeMilliseconds(modified), description, body);
    }

    private sealed class TopHeap(int capacity, bool byKey)
    {
        private readonly (float Score, long Key, int Doc)[] _items = new (float, long, int)[capacity];
        private int _count;

        public bool IsFull => _count == capacity;

        public float MinScore => _count == 0 ? 0 : _items[0].Score;

        public bool TryInsert(float score, long key, int doc)
        {
            var entry = (score, key, doc);
            if (_count < capacity)
            {
                _items[_count] = entry;
                SiftUp(_count++);
                return true;
            }

            if (!Better(entry, _items[0]))
            {
                return false;
            }

            _items[0] = entry;
            SiftDown(0);
            return true;
        }

        public ScoreDoc[] Drain()
        {
            var result = new (float Score, long Key, int Doc)[_count];
            Array.Copy(_items, result, _count);
            Array.Sort(result, (a, b) => Better(a, b) ? -1 : Better(b, a) ? 1 : 0);
            return result.Select(r => new ScoreDoc(r.Doc, r.Score)).ToArray();
        }

        private bool Better((float Score, long Key, int Doc) a, (float Score, long Key, int Doc) b)
        {
            if (byKey)
            {
                if (a.Key != b.Key)
                {
                    return a.Key > b.Key;
                }
            }
            else if (a.Score != b.Score)
            {
                return a.Score > b.Score;
            }

            return a.Doc < b.Doc;
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                var parent = (index - 1) >> 1;
                if (!Better(_items[parent], _items[index]))
                {
                    break;
                }

                (_items[parent], _items[index]) = (_items[index], _items[parent]);
                index = parent;
            }
        }

        private void SiftDown(int index)
        {
            while (true)
            {
                var left = (index * 2) + 1;
                if (left >= _count)
                {
                    return;
                }

                var right = left + 1;
                var worst = right < _count && Better(_items[left], _items[right]) ? right : left;
                if (!Better(_items[index], _items[worst]))
                {
                    return;
                }

                (_items[index], _items[worst]) = (_items[worst], _items[index]);
                index = worst;
            }
        }
    }
}

public sealed class SearcherManager : IDisposable
{
    private readonly Func<IndexSnapshot> _factory;
    private readonly Lock _refreshGate = new();
    private Holder _current;
    private int _disposed;

    public SearcherManager(Func<IndexSnapshot> factory)
    {
        _factory = factory;
        _current = new Holder(new IndexSearcher(factory()));
    }

    public long Generation => Volatile.Read(ref _current).Searcher.Snapshot.Commit.Generation;

    public SearcherLease Acquire()
    {
        while (true)
        {
            var holder = Volatile.Read(ref _current);
            if (holder.TryIncRef())
            {
                return new SearcherLease(holder);
            }
        }
    }

    public void Refresh()
    {
        lock (_refreshGate)
        {
            if (_disposed != 0)
            {
                return;
            }

            var next = new Holder(new IndexSearcher(_factory()));
            var previous = Interlocked.Exchange(ref _current, next);
            previous.DecRef();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _current.DecRef();
        }
    }

    internal sealed class Holder(IndexSearcher searcher)
    {
        private int _references = 1;

        public IndexSearcher Searcher { get; } = searcher;

        public bool TryIncRef()
        {
            while (true)
            {
                var current = Volatile.Read(ref _references);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void DecRef()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                Searcher.Snapshot.Dispose();
            }
        }
    }
}

public readonly struct SearcherLease : IDisposable
{
    private readonly SearcherManager.Holder _holder;

    internal SearcherLease(SearcherManager.Holder holder) => _holder = holder;

    public IndexSearcher Searcher => _holder.Searcher;

    public void Dispose() => _holder.DecRef();
}
