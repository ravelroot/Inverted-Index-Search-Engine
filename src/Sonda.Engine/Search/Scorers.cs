using Sonda.Storage;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Search;

public abstract class Scorer
{
    public const int NoMoreDocs = int.MaxValue;

    public abstract int DocId { get; }

    public abstract long Cost { get; }

    public abstract float MaxScore { get; }

    public abstract int NextDoc();

    public abstract int Advance(int target);

    public abstract float Score();

    public virtual void SetMinCompetitiveScore(float minScore)
    {
    }
}

internal sealed class TermScorer(PostingsEnum postings, SimScorer similarity, byte[]? norms) : Scorer
{
    public override int DocId => postings.DocId;

    public override long Cost => postings.Cost;

    public override float MaxScore => similarity.Score(postings.TermMaxFreq, norms is null ? (byte)1 : postings.TermMinNorm);

    public int Freq => postings.Freq;

    public override int NextDoc() => postings.NextDoc();

    public override int Advance(int target) => postings.Advance(target);

    public override float Score() => similarity.Score(postings.Freq, norms is null ? (byte)1 : norms[postings.DocId]);
}

internal sealed class PositionSource
{
    private readonly PostingsEnum[] _variants;
    private int _doc = -1;

    public PositionSource(PostingsEnum[] variants)
    {
        _variants = variants;
        Cost = variants.Sum(v => v.Cost);
        MaxFreq = variants.Sum(v => v.TermMaxFreq);
        MinNorm = variants.Min(v => v.TermMinNorm);
    }

    public int DocId => _doc;

    public long Cost { get; }

    public int MaxFreq { get; }

    public byte MinNorm { get; }

    public int Freq
    {
        get
        {
            var freq = 0;
            foreach (var variant in _variants)
            {
                if (variant.DocId == _doc)
                {
                    freq += variant.Freq;
                }
            }

            return freq;
        }
    }

    public int NextDoc() => Advance(_doc + 1);

    public int Advance(int target)
    {
        var min = PostingsEnum.NoMoreDocs;
        foreach (var variant in _variants)
        {
            var doc = variant.DocId < target ? variant.Advance(target) : variant.DocId;
            if (doc < min)
            {
                min = doc;
            }
        }

        _doc = min;
        return min;
    }

    public void ReadPositions(List<int> output)
    {
        output.Clear();
        foreach (var variant in _variants)
        {
            if (variant.DocId != _doc)
            {
                continue;
            }

            for (var i = 0; i < variant.Freq; i++)
            {
                output.Add(variant.NextPosition());
            }
        }

        if (_variants.Length > 1)
        {
            output.Sort();
        }
    }
}

internal sealed class SynonymScorer(PositionSource source, SimScorer similarity, byte[]? norms) : Scorer
{
    public override int DocId => source.DocId;

    public override long Cost => source.Cost;

    public override float MaxScore => similarity.Score(source.MaxFreq, norms is null ? (byte)1 : source.MinNorm);

    public int Freq => source.Freq;

    public override int NextDoc() => source.NextDoc();

    public override int Advance(int target) => source.Advance(target);

    public override float Score() => similarity.Score(source.Freq, norms is null ? (byte)1 : norms[source.DocId]);
}

internal sealed class ConjunctionScorer : Scorer
{
    private readonly Scorer[] _all;
    private readonly Scorer[] _scoring;
    private int _doc = -1;

    public ConjunctionScorer(IReadOnlyList<Scorer> scoring, IReadOnlyList<Scorer> filters)
    {
        _scoring = [.. scoring];
        _all = [.. scoring.Concat(filters).OrderBy(s => s.Cost)];
    }

    public override int DocId => _doc;

    public override long Cost => _all[0].Cost;

    public override float MaxScore
    {
        get
        {
            var sum = 0f;
            foreach (var scorer in _scoring)
            {
                sum += scorer.MaxScore;
            }

            return sum;
        }
    }

    public override int NextDoc() => Align(_all[0].NextDoc());

    public override int Advance(int target) => Align(_all[0].Advance(target));

    private int Align(int target)
    {
        while (true)
        {
            if (target == NoMoreDocs)
            {
                _doc = NoMoreDocs;
                return _doc;
            }

            var aligned = true;
            for (var i = 1; i < _all.Length; i++)
            {
                var other = _all[i];
                var doc = other.DocId < target ? other.Advance(target) : other.DocId;
                if (doc > target)
                {
                    target = _all[0].Advance(doc);
                    aligned = false;
                    break;
                }
            }

            if (aligned)
            {
                _doc = target;
                return _doc;
            }
        }
    }

    public override float Score()
    {
        var sum = 0f;
        foreach (var scorer in _scoring)
        {
            sum += scorer.Score();
        }

        return sum;
    }
}

internal sealed class DisjunctionScorer(IReadOnlyList<Scorer> scorers) : Scorer
{
    private readonly Scorer[] _scorers = [.. scorers];
    private int _doc = -1;

    public override int DocId => _doc;

    public override long Cost => _scorers.Sum(s => s.Cost);

    public override float MaxScore => _scorers.Sum(s => s.MaxScore);

    public override int NextDoc()
    {
        var min = NoMoreDocs;
        foreach (var scorer in _scorers)
        {
            var doc = scorer.DocId;
            if (doc == _doc || doc == -1)
            {
                doc = scorer.NextDoc();
            }

            if (doc < min)
            {
                min = doc;
            }
        }

        _doc = min;
        return min;
    }

    public override int Advance(int target)
    {
        var min = NoMoreDocs;
        foreach (var scorer in _scorers)
        {
            var doc = scorer.DocId < target ? scorer.Advance(target) : scorer.DocId;
            if (doc < min)
            {
                min = doc;
            }
        }

        _doc = min;
        return min;
    }

    public override float Score()
    {
        var sum = 0f;
        foreach (var scorer in _scorers)
        {
            if (scorer.DocId == _doc)
            {
                sum += scorer.Score();
            }
        }

        return sum;
    }
}

internal sealed class ReqExclScorer(Scorer required, Scorer excluded) : Scorer
{
    private int _doc = -1;

    public override int DocId => _doc;

    public override long Cost => required.Cost;

    public override float MaxScore => required.MaxScore;

    public override int NextDoc() => SkipExcluded(required.NextDoc());

    public override int Advance(int target) => SkipExcluded(required.Advance(target));

    private int SkipExcluded(int doc)
    {
        while (doc != NoMoreDocs)
        {
            var excludedDoc = excluded.DocId < doc ? excluded.Advance(doc) : excluded.DocId;
            if (excludedDoc != doc)
            {
                break;
            }

            doc = required.NextDoc();
        }

        _doc = doc;
        return doc;
    }

    public override float Score() => required.Score();
}

internal sealed class ReqOptScorer(Scorer required, Scorer optional) : Scorer
{
    public override int DocId => required.DocId;

    public override long Cost => required.Cost;

    public override float MaxScore => required.MaxScore + optional.MaxScore;

    public override int NextDoc() => required.NextDoc();

    public override int Advance(int target) => required.Advance(target);

    public override float Score()
    {
        var doc = required.DocId;
        var score = required.Score();
        var optionalDoc = optional.DocId < doc ? optional.Advance(doc) : optional.DocId;
        if (optionalDoc == doc)
        {
            score += optional.Score();
        }

        return score;
    }
}

internal sealed class MatchAllScorer(int maxDoc, float score) : Scorer
{
    private int _doc = -1;

    public override int DocId => _doc;

    public override long Cost => maxDoc;

    public override float MaxScore => score;

    public override int NextDoc() => Advance(_doc + 1);

    public override int Advance(int target)
    {
        _doc = target >= maxDoc ? NoMoreDocs : target;
        return _doc;
    }

    public override float Score() => score;
}

internal sealed class ConstantScorer(Scorer inner, float score) : Scorer
{
    public override int DocId => inner.DocId;

    public override long Cost => inner.Cost;

    public override float MaxScore => score;

    public override int NextDoc() => inner.NextDoc();

    public override int Advance(int target) => inner.Advance(target);

    public override float Score() => score;
}

internal sealed class WandScorer : Scorer
{
    private readonly Scorer[] _cursors;
    private readonly float[] _maxScores;
    private float _threshold;
    private int _doc = -1;
    private float _score;

    public WandScorer(IReadOnlyList<Scorer> scorers)
    {
        _cursors = [.. scorers];
        _maxScores = _cursors.Select(s => s.MaxScore).ToArray();
        Cost = _cursors.Sum(s => s.Cost);
        MaxScore = _maxScores.Sum();
    }

    public override int DocId => _doc;

    public override long Cost { get; }

    public override float MaxScore { get; }

    public long Evaluated { get; private set; }

    public override void SetMinCompetitiveScore(float minScore)
    {
        if (minScore > _threshold)
        {
            _threshold = minScore;
        }
    }

    public override int NextDoc()
    {
        if (_doc == NoMoreDocs)
        {
            return _doc;
        }

        foreach (var cursor in _cursors)
        {
            if (cursor.DocId == _doc || cursor.DocId == -1)
            {
                cursor.NextDoc();
            }
        }

        return FindNext();
    }

    public override int Advance(int target)
    {
        foreach (var cursor in _cursors)
        {
            if (cursor.DocId < target)
            {
                cursor.Advance(target);
            }
        }

        return FindNext();
    }

    private int FindNext()
    {
        while (true)
        {
            Sort();
            var accumulated = 0f;
            var pivot = -1;
            for (var i = 0; i < _cursors.Length; i++)
            {
                if (_cursors[i].DocId == NoMoreDocs)
                {
                    break;
                }

                accumulated += _maxScores[i];
                if (accumulated >= _threshold)
                {
                    pivot = i;
                    break;
                }
            }

            if (pivot < 0)
            {
                _doc = NoMoreDocs;
                return _doc;
            }

            var pivotDoc = _cursors[pivot].DocId;
            if (_cursors[0].DocId == pivotDoc)
            {
                _doc = pivotDoc;
                var score = 0f;
                foreach (var cursor in _cursors)
                {
                    if (cursor.DocId == pivotDoc)
                    {
                        score += cursor.Score();
                    }
                }

                _score = score;
                Evaluated++;
                return _doc;
            }

            for (var i = 0; i < pivot; i++)
            {
                if (_cursors[i].DocId < pivotDoc)
                {
                    _cursors[i].Advance(pivotDoc);
                }
            }
        }
    }

    private void Sort()
    {
        for (var i = 1; i < _cursors.Length; i++)
        {
            var cursor = _cursors[i];
            var max = _maxScores[i];
            var j = i - 1;
            while (j >= 0 && _cursors[j].DocId > cursor.DocId)
            {
                _cursors[j + 1] = _cursors[j];
                _maxScores[j + 1] = _maxScores[j];
                j--;
            }

            _cursors[j + 1] = cursor;
            _maxScores[j + 1] = max;
        }
    }

    public override float Score() => _score;
}

internal sealed class PhraseScorer : Scorer
{
    private readonly PositionSource[] _parts;
    private readonly int[] _offsets;
    private readonly PositionSource[] _byCost;
    private readonly int _slop;
    private readonly SimScorer _similarity;
    private readonly byte[]? _norms;
    private readonly List<int>[] _positions;
    private int _doc = -1;
    private float _phraseFreq;

    public PhraseScorer(PositionSource[] parts, int[] offsets, int slop, SimScorer similarity, byte[]? norms)
    {
        _parts = parts;
        _offsets = offsets;
        _slop = slop;
        _similarity = similarity;
        _norms = norms;
        _byCost = [.. parts.OrderBy(p => p.Cost)];
        _positions = parts.Select(_ => new List<int>(8)).ToArray();
    }

    public override int DocId => _doc;

    public override long Cost => _byCost[0].Cost;

    public override float MaxScore => _similarity.Score(_parts.Min(p => p.MaxFreq), _norms is null ? (byte)1 : _parts.Min(p => p.MinNorm));

    public float PhraseFreq => _phraseFreq;

    public override int NextDoc() => Search(_byCost[0].NextDoc());

    public override int Advance(int target) => Search(_byCost[0].Advance(target));

    private int Search(int target)
    {
        while (true)
        {
            target = Align(target);
            if (target == NoMoreDocs)
            {
                _doc = NoMoreDocs;
                return _doc;
            }

            _doc = target;
            _phraseFreq = Match();
            if (_phraseFreq > 0)
            {
                return _doc;
            }

            target = _byCost[0].NextDoc();
        }
    }

    private int Align(int target)
    {
        while (true)
        {
            if (target == NoMoreDocs)
            {
                return target;
            }

            var aligned = true;
            for (var i = 1; i < _byCost.Length; i++)
            {
                var part = _byCost[i];
                var doc = part.DocId < target ? part.Advance(target) : part.DocId;
                if (doc > target)
                {
                    target = _byCost[0].Advance(doc);
                    aligned = false;
                    break;
                }
            }

            if (aligned)
            {
                return target;
            }
        }
    }

    private float Match()
    {
        for (var i = 0; i < _parts.Length; i++)
        {
            _parts[i].ReadPositions(_positions[i]);
        }

        var first = _positions[0];
        var freq = 0f;
        foreach (var position in first)
        {
            var start = position - _offsets[0];
            if (_slop == 0)
            {
                var all = true;
                for (var i = 1; i < _parts.Length; i++)
                {
                    if (_positions[i].BinarySearch(start + _offsets[i]) < 0)
                    {
                        all = false;
                        break;
                    }
                }

                if (all)
                {
                    freq += 1;
                }
            }
            else
            {
                var min = start;
                var max = start;
                for (var i = 1; i < _parts.Length; i++)
                {
                    var adjusted = Nearest(_positions[i], start + _offsets[i]) - _offsets[i];
                    min = Math.Min(min, adjusted);
                    max = Math.Max(max, adjusted);
                }

                var span = max - min;
                if (span <= _slop)
                {
                    freq += 1f / (1 + span);
                }
            }
        }

        return freq;
    }

    private static int Nearest(List<int> positions, int target)
    {
        var index = positions.BinarySearch(target);
        if (index >= 0)
        {
            return positions[index];
        }

        index = ~index;
        if (index == 0)
        {
            return positions[0];
        }

        if (index >= positions.Count)
        {
            return positions[^1];
        }

        return target - positions[index - 1] <= positions[index] - target ? positions[index - 1] : positions[index];
    }

    public override float Score() => _similarity.Score(_phraseFreq, _norms is null ? (byte)1 : _norms[_doc]);
}
