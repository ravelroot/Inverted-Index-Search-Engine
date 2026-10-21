using System.Globalization;
using Sonda.Storage;

namespace Sonda.Engine.Search;

public sealed record Explanation(float Value, string Description, IReadOnlyList<Explanation> Details)
{
    public static Explanation Of(float value, string description, params Explanation[] details) => new(value, description, details);

    public override string ToString() => Format(0);

    private string Format(int depth)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(' ', depth * 2)
            .Append(Value.ToString("0.####", CultureInfo.InvariantCulture))
            .Append(" = ")
            .Append(Description)
            .Append('\n');
        foreach (var detail in Details)
        {
            builder.Append(detail.Format(depth + 1));
        }

        return builder.ToString();
    }
}

public readonly record struct CollectionStatistics(int Field, long MaxDoc, long DocCount, long SumTotalTermFreq)
{
    public double AverageLength => DocCount <= 0 ? 1 : Math.Max(1d, (double)SumTotalTermFreq / DocCount);
}

public readonly record struct TermStatistics(string Term, long DocFreq, long TotalTermFreq);

public abstract class Similarity
{
    public abstract string Name { get; }

    public abstract SimScorer CreateScorer(float boost, CollectionStatistics collection, IReadOnlyList<TermStatistics> terms);

    public static Similarity FromName(string? name, double k1 = 1.2, double b = 0.75)
        => name?.Trim().ToLowerInvariant() switch
        {
            "tfidf" or "tf-idf" or "classic" => new TfIdfSimilarity(),
            _ => new Bm25Similarity(k1, b),
        };
}

public abstract class SimScorer
{
    public abstract float Score(float freq, byte norm);

    public abstract Explanation Explain(float freq, byte norm);
}

public sealed class Bm25Similarity(double k1 = 1.2, double b = 0.75) : Similarity
{
    public double K1 { get; } = k1;

    public double B { get; } = b;

    public override string Name => "BM25";

    public static double Idf(long docFreq, long docCount) => Math.Log(1 + ((docCount - docFreq + 0.5) / (docFreq + 0.5)));

    public override SimScorer CreateScorer(float boost, CollectionStatistics collection, IReadOnlyList<TermStatistics> terms)
    {
        var docCount = Math.Max(collection.DocCount, terms.Count == 0 ? 1 : terms.Max(t => t.DocFreq));
        var idfs = terms.Select(t => Idf(t.DocFreq, docCount)).ToArray();
        return new Scorer(this, boost, collection, terms, idfs, docCount);
    }

    private sealed class Scorer : SimScorer
    {
        private readonly Bm25Similarity _owner;
        private readonly float _boost;
        private readonly CollectionStatistics _collection;
        private readonly IReadOnlyList<TermStatistics> _terms;
        private readonly double[] _idfs;
        private readonly long _docCount;
        private readonly double _weight;
        private readonly double[] _normCache = new double[256];

        public Scorer(Bm25Similarity owner, float boost, CollectionStatistics collection, IReadOnlyList<TermStatistics> terms, double[] idfs, long docCount)
        {
            _owner = owner;
            _boost = boost;
            _collection = collection;
            _terms = terms;
            _idfs = idfs;
            _docCount = docCount;
            _weight = boost * idfs.Sum();
            var average = collection.AverageLength;
            for (var i = 0; i < 256; i++)
            {
                var length = NormEncoding.Decode((byte)i);
                _normCache[i] = owner.K1 * (1 - owner.B + (owner.B * length / average));
            }
        }

        public override float Score(float freq, byte norm)
        {
            var tf = freq * (_owner.K1 + 1) / (freq + _normCache[norm]);
            return (float)(_weight * tf);
        }

        public override Explanation Explain(float freq, byte norm)
        {
            var length = NormEncoding.Decode(norm);
            var average = _collection.AverageLength;
            var tf = freq * (_owner.K1 + 1) / (freq + _normCache[norm]);
            var idfDetails = new List<Explanation>();
            for (var i = 0; i < _terms.Count; i++)
            {
                var term = _terms[i];
                idfDetails.Add(Explanation.Of((float)_idfs[i], $"idf(\"{term.Term}\") = ln(1 + (N - df + 0.5) / (df + 0.5))",
                    Explanation.Of(term.DocFreq, "df, documentos que contêm o termo"),
                    Explanation.Of(_docCount, "N, documentos com o campo")));
            }

            var idf = Explanation.Of((float)_idfs.Sum(), "idf", [.. idfDetails]);
            var tfExplanation = Explanation.Of((float)tf, "tf = freq × (k1 + 1) / (freq + k1 × (1 − b + b × dl / avgdl))",
                Explanation.Of(freq, "freq, ocorrências no documento"),
                Explanation.Of((float)_owner.K1, "k1, saturação da frequência"),
                Explanation.Of((float)_owner.B, "b, normalização pelo tamanho"),
                Explanation.Of(length, "dl, tamanho do campo no documento"),
                Explanation.Of((float)average, "avgdl, tamanho médio do campo"));
            return Explanation.Of(Score(freq, norm), $"BM25 no campo {Schema.NameOf(_collection.Field)} = boost × idf × tf",
                Explanation.Of(_boost, "boost"),
                idf,
                tfExplanation);
        }
    }
}

public sealed class TfIdfSimilarity : Similarity
{
    public override string Name => "TF-IDF";

    public static double Idf(long docFreq, long docCount) => 1 + Math.Log((docCount + 1d) / (docFreq + 1d));

    public override SimScorer CreateScorer(float boost, CollectionStatistics collection, IReadOnlyList<TermStatistics> terms)
    {
        var docCount = Math.Max(collection.DocCount, terms.Count == 0 ? 1 : terms.Max(t => t.DocFreq));
        var idfs = terms.Select(t => Idf(t.DocFreq, docCount)).ToArray();
        return new Scorer(boost, collection, terms, idfs, docCount);
    }

    private sealed class Scorer : SimScorer
    {
        private readonly float _boost;
        private readonly CollectionStatistics _collection;
        private readonly IReadOnlyList<TermStatistics> _terms;
        private readonly double[] _idfs;
        private readonly long _docCount;
        private readonly double _weight;
        private readonly double[] _normCache = new double[256];

        public Scorer(float boost, CollectionStatistics collection, IReadOnlyList<TermStatistics> terms, double[] idfs, long docCount)
        {
            _boost = boost;
            _collection = collection;
            _terms = terms;
            _idfs = idfs;
            _docCount = docCount;
            var idf = idfs.Sum();
            _weight = boost * idf * idf;
            for (var i = 0; i < 256; i++)
            {
                var length = Math.Max(1, NormEncoding.Decode((byte)i));
                _normCache[i] = 1d / Math.Sqrt(length);
            }
        }

        public override float Score(float freq, byte norm) => (float)(_weight * Math.Sqrt(freq) * _normCache[norm]);

        public override Explanation Explain(float freq, byte norm)
        {
            var idfDetails = new List<Explanation>();
            for (var i = 0; i < _terms.Count; i++)
            {
                var term = _terms[i];
                idfDetails.Add(Explanation.Of((float)_idfs[i], $"idf(\"{term.Term}\") = 1 + ln((N + 1) / (df + 1))",
                    Explanation.Of(term.DocFreq, "df, documentos que contêm o termo"),
                    Explanation.Of(_docCount, "N, documentos com o campo")));
            }

            var idf = _idfs.Sum();
            return Explanation.Of(Score(freq, norm), $"TF-IDF no campo {Schema.NameOf(_collection.Field)} = boost × √freq × idf² × 1/√dl",
                Explanation.Of(_boost, "boost"),
                Explanation.Of((float)Math.Sqrt(freq), "√freq", Explanation.Of(freq, "freq, ocorrências no documento")),
                Explanation.Of((float)(idf * idf), "idf²", [.. idfDetails]),
                Explanation.Of((float)_normCache[norm], "1/√dl", Explanation.Of(NormEncoding.Decode(norm), "dl, tamanho do campo no documento")));
        }
    }
}
