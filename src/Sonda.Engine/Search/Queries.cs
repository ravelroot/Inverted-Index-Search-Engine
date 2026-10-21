using System.Globalization;
using System.Text;
using Sonda.Engine.Index;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Search;

public enum ScoreMode
{
    Complete,
    CompleteNoScores,
    TopScores,
}

public enum Occur
{
    Must,
    Should,
    MustNot,
    Filter,
}

public sealed record LeafContext(SegmentReader Reader, int DocBase, int Ordinal);

public abstract class Weight
{
    public abstract Scorer? CreateScorer(LeafContext leaf);

    public abstract Explanation Explain(LeafContext leaf, int doc);
}

public abstract class Query
{
    public abstract Weight CreateWeight(IndexSearcher searcher, ScoreMode mode, float boost);

    public virtual Query Rewrite(IndexSearcher searcher) => this;

    public virtual void ExtractTerms(ISet<(int Field, string Term)> terms)
    {
    }

    protected static string FormatBoost(float boost)
        => Math.Abs(boost - 1f) < 1e-6 ? string.Empty : "^" + boost.ToString("0.##", CultureInfo.InvariantCulture);
}

public sealed class MatchNoDocsQuery(string reason = "") : Query
{
    public string Reason { get; } = reason;

    public override Weight CreateWeight(IndexSearcher searcher, ScoreMode mode, float boost) => new NoWeight();

    public override string ToString() => "(nenhum)";

    private sealed class NoWeight : Weight
    {
        public override Scorer? CreateScorer(LeafContext leaf) => null;

        public override Explanation Explain(LeafContext leaf, int doc) => Explanation.Of(0, "consulta vazia");
    }
}

public sealed class MatchAllQuery(float boost = 1f) : Query
{
    public float Boost { get; } = boost;

    public override Weight CreateWeight(IndexSearcher searcher, ScoreMode mode, float boost) => new AllWeight(Boost * boost);

    public override string ToString() => "*:*" + FormatBoost(Boost);

    private sealed class AllWeight(float score) : Weight
    {
        public override Scorer? CreateScorer(LeafContext leaf) => new MatchAllScorer(leaf.Reader.MaxDoc, score);

        public override Explanation Explain(LeafContext leaf, int doc) => Explanation.Of(score, "todos os documentos");
    }
}

public sealed class TermQuery : Query
{
    public TermQuery(int field, IReadOnlyList<string> variants, float boost = 1f)
    {
        Field = field;
        Variants = variants.Distinct(StringComparer.Ordinal).ToArray();
        Boost = boost;
    }

    public TermQuery(int field, string term, float boost = 1f)
        : this(field, [term], boost)
    {
    }

    public int Field { get; }

    public IReadOnlyList<string> Variants { get; }

    public float Boost { get; }

    public override void ExtractTerms(ISet<(int Field, string Term)> terms)
    {
        foreach (var variant in Variants)
        {
            terms.Add((Field, variant));
        }
    }

    public override Weight CreateWeight(IndexSearcher searcher, ScoreMode mode, float boost)
    {
        var stats = Variants.Select(v => searcher.TermStatistics(Field, v)).ToArray();
        var combined = new TermStatistics(
            string.Join('|', Variants),
            stats.Max(s => s.DocFreq),
            stats.Sum(s => s.TotalTermFreq));
        var collection = searcher.CollectionStatistics(Field);
        var similarity = searcher.Similarity.CreateScorer(Boost * boost, collection, [combined]);
        return new TermWeight(this, similarity);
    }

    public override string ToString()
    {
        var name = Schema.NameOf(Field);
        var terms = Variants.Count == 1 ? Variants[0] : "(" + string.Join(" | ", Variants) + ")";
        return $"{name}:{terms}{FormatBoost(Boost)}";
    }

    private sealed class TermWeight(TermQuery query, SimScorer similarity) : Weight
    {
        public override Scorer? CreateScorer(LeafContext leaf)
        {
            var reader = leaf.Reader;
            var norms = reader.Norms(query.Field);
            if (query.Variants.Count == 1)
            {
                var postings = reader.Postings(query.Field, query.Variants[0], false);
                return postings is null ? null : new TermScorer(postings, similarity, norms);
            }

            var enums = query.Variants
                .Select(v => reader.Postings(query.Field, v, false))
                .Where(p => p is not null)
                .Cast<PostingsEnum>()
                .ToArray();
            if (enums.Length == 0)
            {
                return null;
            }

            return new SynonymScorer(new PositionSource(enums), similarity, norms);
        }

        public override Explanation Explain(LeafContext leaf, int doc)
        {
            var scorer = CreateScorer(leaf);
            if (scorer is null || scorer.Advance(doc) != doc)
            {
                return Explanation.Of(0, $"{query} não aparece no documento");
            }

            var freq = scorer switch
            {
                TermScorer term => term.Freq,
                SynonymScorer synonym => synonym.Freq,
                _ => 1,
            };
            var norms = leaf.Reader.Norms(query.Field);
            var norm = norms is null ? (byte)1 : norms[doc];
            var detail = similarity.Explain(freq, norm);
            return Explanation.Of(detail.Value, $"termo {query}", detail);
        }
    }
}

public sealed class BooleanQuery(IReadOnlyList<(Query Query, Occur Occur)> clauses, float boost = 1f) : Query
{
    public IReadOnlyList<(Query Query, Occur Occur)> Clauses { get; } = clauses;

    public float Boost { get; } = boost;

    public override void ExtractTerms(ISet<(int Field, string Term)> terms)
    {
        foreach (var (query, occur) in Clauses)
        {
            if (occur != Occur.MustNot)
            {
                query.ExtractTerms(terms);
            }
        }
    }

    public override Query Rewrite(IndexSearcher searcher)
    {
        var rewritten = new List<(Query Query, Occur Occur)>(Clauses.Count);
        var changed = false;
        foreach (var (query, occur) in Clauses)
        {
            var next = query.Rewrite(searcher);
            if (!ReferenceEquals(next, query))
            {
                changed = true;
            }

            if (next is MatchNoDocsQuery)
            {
                if (occur is Occur.Must or Occur.Filter)
                {
                    return new MatchNoDocsQuery("cláusula obrigatória sem resultados");
                }

                changed = true;
                continue;
            }

            if (occur == Occur.Should && next is BooleanQuery inner && Math.Abs(inner.Boost - 1f) < 1e-6
                && inner.Clauses.All(c => c.Occur == Occur.Should) && Clauses.All(c => c.Occur == Occur.Should))
            {
                rewritten.AddRange(inner.Clauses);
                changed = true;
                continue;
            }

            rewritten.Add((next, occur));
        }

        if (rewritten.Count == 0)
        {
            return new MatchNoDocsQuery("nenhuma cláusula");
        }

        if (rewritten.All(c => c.Occur == Occur.MustNot))
        {
            rewritten.Insert(0, (new MatchAllQuery(), Occur.Must));
            changed = true;
        }

        if (rewritten.Count == 1 && rewritten[0].Occur is Occur.Must or Occur.Should && Math.Abs(Boost - 1f) < 1e-6)
        {
            return rewritten[0].Query;
        }

        return changed ? new BooleanQuery(rewritten, Boost) : this;
    }

    public override Weight CreateWeight(IndexSearcher searcher, ScoreMode mode, float boost)
    {
        var weights = Clauses
            .Select(c => (Weight: c.Query.CreateWeight(searcher, c.Occur is Occur.MustNot or Occur.Filter ? ScoreMode.CompleteNoScores : mode, Boost * boost), c.Occur))
            .ToList();
        return new BooleanWeight(weights, mode);
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        var nested = Clauses.Count > 1;
        foreach (var (query, occur) in Clauses)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(occur switch
            {
                Occur.Must => "+",
                Occur.MustNot => "-",
                Occur.Filter => "#",
                _ => string.Empty,
            });
            var text = query.ToString();
            builder.Append(query is BooleanQuery && nested ? "(" + text + ")" : text);
        }

        var result = builder.ToString();
        return Math.Abs(Boost - 1f) < 1e-6 ? result : "(" + result + ")" + FormatBoost(Boost);
    }

    private sealed class BooleanWeight(List<(Weight Weight, Occur Occur)> weights, ScoreMode mode) : Weight
    {
        public override Scorer? CreateScorer(LeafContext leaf)
        {
            var required = new List<Scorer>();
            var filters = new List<Scorer>();
            var optional = new List<Scorer>();
            var prohibited = new List<Scorer>();
            foreach (var (weight, occur) in weights)
            {
                var scorer = weight.CreateScorer(leaf);
                switch (occur)
                {
                    case Occur.Must:
                        if (scorer is null)
                        {
                            return null;
                        }

                        required.Add(scorer);
                        break;
                    case Occur.Filter:
                        if (scorer is null)
                        {
                            return null;
                        }

                        filters.Add(scorer);
                        break;
                    case Occur.Should:
                        if (scorer is not null)
                        {
                            optional.Add(scorer);
                        }

                        break;
                    default:
                        if (scorer is not null)
                        {
                            prohibited.Add(scorer);
                        }

                        break;
                }
            }

            Scorer? result;
            if (required.Count == 0 && filters.Count == 0)
            {
                if (optional.Count == 0)
                {
                    return null;
                }

                result = optional.Count == 1
                    ? optional[0]
                    : mode == ScoreMode.TopScores && prohibited.Count == 0
                        ? new WandScorer(optional)
                        : new DisjunctionScorer(optional);
            }
            else
            {
                if (required.Count == 0)
                {
                    result = new ConstantScorer(filters.Count == 1 ? filters[0] : new ConjunctionScorer([], filters), 1f);
                }
                else
                {
                    result = required.Count == 1 && filters.Count == 0 ? required[0] : new ConjunctionScorer(required, filters);
                }

                if (optional.Count > 0)
                {
                    result = new ReqOptScorer(result, optional.Count == 1 ? optional[0] : new DisjunctionScorer(optional));
                }
            }

            if (prohibited.Count > 0)
            {
                result = new ReqExclScorer(result, prohibited.Count == 1 ? prohibited[0] : new DisjunctionScorer(prohibited));
            }

            return result;
        }

        public override Explanation Explain(LeafContext leaf, int doc)
        {
            var details = new List<Explanation>();
            var sum = 0f;
            foreach (var (weight, occur) in weights)
            {
                var explanation = weight.Explain(leaf, doc);
                if (occur is Occur.Must or Occur.Should && explanation.Value > 0)
                {
                    sum += explanation.Value;
                    details.Add(explanation);
                }
            }

            return Explanation.Of(sum, "soma das cláusulas que combinaram", [.. details]);
        }
    }
}

public sealed class PhraseQuery : Query
{
    public PhraseQuery(int field, IReadOnlyList<(int Offset, IReadOnlyList<string> Variants)> parts, int slop = 0, float boost = 1f)
    {
        Field = field;
        Parts = parts;
        Slop = Math.Max(0, slop);
        Boost = boost;
    }

    public int Field { get; }

    public IReadOnlyList<(int Offset, IReadOnlyList<string> Variants)> Parts { get; }

    public int Slop { get; }

    public float Boost { get; }

    public override void ExtractTerms(ISet<(int Field, string Term)> terms)
    {
        foreach (var part in Parts)
        {
            foreach (var variant in part.Variants)
            {
                terms.Add((Field, variant));
            }
        }
    }

    public override Query Rewrite(IndexSearcher searcher)
    {
        if (Parts.Count == 0)
        {
            return new MatchNoDocsQuery("frase vazia");
        }

        if (Parts.Count == 1)
        {
            return new TermQuery(Field, Parts[0].Variants, Boost);
        }

        var field = Schema.Fields[Field];
        if (field is null || !field.HasPositions)
        {
            var clauses = Parts.Select(p => ((Query)new TermQuery(Field, p.Variants), Occur.Must)).ToList();
            return new BooleanQuery(clauses, Boost);
        }

        return this;
    }

    public override Weight CreateWeight(IndexSearcher searcher, ScoreMode mode, float boost)
    {
        var stats = Parts.Select(p =>
        {
            var variantStats = p.Variants.Select(v => searcher.TermStatistics(Field, v)).ToArray();
            return new TermStatistics(string.Join('|', p.Variants), variantStats.Max(s => s.DocFreq), variantStats.Sum(s => s.TotalTermFreq));
        }).ToArray();
        var similarity = searcher.Similarity.CreateScorer(Boost * boost, searcher.CollectionStatistics(Field), stats);
        return new PhraseWeight(this, similarity);
    }

    public override string ToString()
    {
        var words = Parts.Select(p => p.Variants.Count == 1 ? p.Variants[0] : "(" + string.Join('|', p.Variants) + ")");
        var slop = Slop > 0 ? "~" + Slop.ToString(CultureInfo.InvariantCulture) : string.Empty;
        return $"{Schema.NameOf(Field)}:\"{string.Join(' ', words)}\"{slop}{FormatBoost(Boost)}";
    }

    private sealed class PhraseWeight(PhraseQuery query, SimScorer similarity) : Weight
    {
        public override Scorer? CreateScorer(LeafContext leaf)
        {
            var sources = new PositionSource[query.Parts.Count];
            for (var i = 0; i < query.Parts.Count; i++)
            {
                var enums = query.Parts[i].Variants
                    .Select(v => leaf.Reader.Postings(query.Field, v, true))
                    .Where(p => p is not null)
                    .Cast<PostingsEnum>()
                    .ToArray();
                if (enums.Length == 0)
                {
                    return null;
                }

                sources[i] = new PositionSource(enums);
            }

            var offsets = query.Parts.Select(p => p.Offset).ToArray();
            return new PhraseScorer(sources, offsets, query.Slop, similarity, leaf.Reader.Norms(query.Field));
        }

        public override Explanation Explain(LeafContext leaf, int doc)
        {
            var scorer = CreateScorer(leaf) as PhraseScorer;
            if (scorer is null || scorer.Advance(doc) != doc)
            {
                return Explanation.Of(0, $"a frase {query} não aparece no documento");
            }

            var norms = leaf.Reader.Norms(query.Field);
            var detail = similarity.Explain(scorer.PhraseFreq, norms is null ? (byte)1 : norms[doc]);
            return Explanation.Of(detail.Value, $"frase {query} (frequência da frase {scorer.PhraseFreq.ToString("0.##", CultureInfo.InvariantCulture)})", detail);
        }
    }
}

public sealed class PrefixQuery(int field, string prefix, float boost = 1f, int maxExpansions = 64) : Query
{
    public int Field { get; } = field;

    public string Prefix { get; } = prefix;

    public float Boost { get; } = boost;

    public override Query Rewrite(IndexSearcher searcher)
    {
        var prefixBytes = Encoding.UTF8.GetBytes(Prefix);
        var expansions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var leaf in searcher.Leaves)
        {
            var terms = leaf.Reader.Terms(Field);
            if (terms is null)
            {
                continue;
            }

            var iterator = terms.SeekCeil(prefixBytes);
            while (iterator.MoveNext() && iterator.TermBytes.StartsWith(prefixBytes))
            {
                var term = iterator.Term;
                expansions[term] = expansions.GetValueOrDefault(term) + iterator.Meta.DocFreq;
            }

            for (var length = Math.Max(3, Prefix.Length - 3); length < Prefix.Length; length++)
            {
                var stem = Prefix[..length];
                if (terms.TryGetTerm(stem, out var meta))
                {
                    expansions[stem] = expansions.GetValueOrDefault(stem) + meta.DocFreq;
                }
            }
        }

        return Expand(Field, expansions.OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(maxExpansions).Select(e => (e.Key, 1f)), Boost);
    }

    internal static Query Expand(int field, IEnumerable<(string Term, float Boost)> terms, float boost)
    {
        var clauses = terms.Select(t => ((Query)new TermQuery(field, [t.Term], t.Boost), Occur.Should)).ToList();
        return clauses.Count switch
        {
            0 => new MatchNoDocsQuery("nenhum termo expandido"),
            _ => new BooleanQuery(clauses, boost),
        };
    }

    public override Weight CreateWeight(IndexSearcher searcher, ScoreMode mode, float boost)
        => Rewrite(searcher).CreateWeight(searcher, mode, boost);

    public override string ToString() => $"{Schema.NameOf(Field)}:{Prefix}*{FormatBoost(Boost)}";
}

public sealed class FuzzyQuery(int field, string term, int maxEdits, float boost = 1f, int prefixLength = 1, int maxExpansions = 50) : Query
{
    public int Field { get; } = field;

    public string Term { get; } = term;

    public int MaxEdits { get; } = Math.Clamp(maxEdits, 0, 2);

    public float Boost { get; } = boost;

    public override Query Rewrite(IndexSearcher searcher)
    {
        var prefix = Term[..Math.Min(prefixLength, Term.Length)];
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var candidates = new Dictionary<string, (int Distance, long DocFreq)>(StringComparer.Ordinal);
        foreach (var leaf in searcher.Leaves)
        {
            var terms = leaf.Reader.Terms(Field);
            if (terms is null)
            {
                continue;
            }

            var iterator = terms.SeekCeil(prefixBytes);
            while (iterator.MoveNext() && iterator.TermBytes.StartsWith(prefixBytes))
            {
                var candidate = iterator.Term;
                if (Math.Abs(candidate.Length - Term.Length) > MaxEdits)
                {
                    continue;
                }

                var distance = EditDistance.Compute(Term, candidate, MaxEdits);
                if (distance > MaxEdits)
                {
                    continue;
                }

                var existing = candidates.GetValueOrDefault(candidate);
                candidates[candidate] = (distance, existing.DocFreq + iterator.Meta.DocFreq);
            }
        }

        var selected = candidates
            .OrderBy(c => c.Value.Distance)
            .ThenByDescending(c => c.Value.DocFreq)
            .Take(maxExpansions)
            .Select(c => (c.Key, Math.Max(0.1f, 1f - ((float)c.Value.Distance / Math.Max(1, Math.Min(Term.Length, c.Key.Length))))));
        return PrefixQuery.Expand(Field, selected, Boost);
    }

    public override Weight CreateWeight(IndexSearcher searcher, ScoreMode mode, float boost)
        => Rewrite(searcher).CreateWeight(searcher, mode, boost);

    public override string ToString() => $"{Schema.NameOf(Field)}:{Term}~{MaxEdits}{FormatBoost(Boost)}";
}

public static class EditDistance
{
    public static int Compute(string a, string b, int limit)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previousPrevious = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, previousPrevious[j - 2] + 1);
                }

                current[j] = value;
                rowMin = Math.Min(rowMin, value);
            }

            if (rowMin > limit)
            {
                return limit + 1;
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        return previous[b.Length];
    }
}
