namespace Sonda.Storage.Formats;

public sealed record FieldTermStats(long TermCount, long SumDocFreq, long SumTotalTermFreq, int DocCount);

public sealed class TermsWriter : IDisposable
{
    public const string DictionaryExtension = "tim";
    public const string IndexExtension = "tip";
    internal const string DictionaryCodec = "SondaTermDictionary";
    internal const string IndexCodec = "SondaTermIndex";
    internal const int Version = 1;
    public const int BlockSize = 32;

    private readonly IndexOutput _tim;
    private readonly IndexOutput _tip;
    private readonly List<FieldSection> _sections = [];
    private readonly List<(byte[] Term, TermMeta Meta)> _pending = new(BlockSize);

    private FieldSection? _current;
    private byte[] _lastTerm = [];

    private sealed class FieldSection(FieldInfo field)
    {
        public FieldInfo Field { get; } = field;
        public List<byte[]> BlockFirstTerms { get; } = [];
        public List<long> BlockPointers { get; } = [];
        public long TermCount { get; set; }
        public FieldTermStats? Stats { get; set; }
    }

    public TermsWriter(IndexDirectory directory, string segment)
    {
        _tim = directory.CreateOutput($"{segment}.{DictionaryExtension}");
        _tip = directory.CreateOutput($"{segment}.{IndexExtension}");
        CodecUtil.WriteHeader(_tim, DictionaryCodec, Version);
        CodecUtil.WriteHeader(_tip, IndexCodec, Version);
    }

    public void StartField(FieldInfo field)
    {
        _current = new FieldSection(field);
        _sections.Add(_current);
        _lastTerm = [];
        _pending.Clear();
    }

    public void AddTerm(byte[] term, in TermMeta meta)
    {
        var section = _current ?? throw new InvalidOperationException("Chame StartField antes de AddTerm.");
        if (section.TermCount > 0 && term.AsSpan().SequenceCompareTo(_lastTerm) <= 0)
        {
            throw new InvalidOperationException("Termos precisam ser adicionados em ordem crescente.");
        }

        _pending.Add((term, meta));
        _lastTerm = term;
        section.TermCount++;
        if (_pending.Count == BlockSize)
        {
            WriteBlock();
        }
    }

    public void FinishField(FieldTermStats stats)
    {
        if (_pending.Count > 0)
        {
            WriteBlock();
        }

        _current!.Stats = stats;
        _current = null;
    }

    private void WriteBlock()
    {
        var section = _current!;
        var field = section.Field;
        section.BlockFirstTerms.Add(_pending[0].Term);
        section.BlockPointers.Add(_tim.FilePointer);

        _tim.WriteVInt(_pending.Count);
        ReadOnlySpan<byte> previous = [];
        long previousDocStart = 0;
        long previousPosStart = 0;
        foreach (var (term, meta) in _pending)
        {
            var prefix = CommonPrefix(previous, term);
            _tim.WriteVInt(prefix);
            _tim.WriteVInt(term.Length - prefix);
            _tim.WriteBytes(term.AsSpan(prefix));
            _tim.WriteVInt(meta.DocFreq);
            if (field.HasFreqs)
            {
                _tim.WriteVLong(meta.TotalTermFreq - meta.DocFreq);
            }

            if (meta.DocFreq == 1)
            {
                _tim.WriteVInt(meta.SingletonDoc);
            }
            else
            {
                _tim.WriteZLong(meta.DocStart - previousDocStart);
                previousDocStart = meta.DocStart;
            }

            if (field.HasPositions)
            {
                _tim.WriteZLong(meta.PosStart - previousPosStart);
                previousPosStart = meta.PosStart;
            }

            if (meta.DocFreq >= ForUtil.BlockSize)
            {
                _tim.WriteVLong(meta.SkipOffset);
            }

            if (field.HasFreqs)
            {
                _tim.WriteVInt(meta.MaxFreq);
                _tim.WriteByte(meta.MinNorm);
            }

            previous = term;
        }

        _pending.Clear();
    }

    private static int CommonPrefix(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    public void Finish()
    {
        _tip.WriteVInt(_sections.Count);
        foreach (var section in _sections)
        {
            var stats = section.Stats ?? new FieldTermStats(0, 0, 0, 0);
            _tip.WriteVInt(section.Field.Number);
            _tip.WriteVLong(section.TermCount);
            _tip.WriteVLong(stats.SumDocFreq);
            _tip.WriteVLong(stats.SumTotalTermFreq);
            _tip.WriteVInt(stats.DocCount);
            _tip.WriteVInt(section.BlockFirstTerms.Count);
            long previous = 0;
            for (var i = 0; i < section.BlockFirstTerms.Count; i++)
            {
                _tip.WriteBytesWithLength(section.BlockFirstTerms[i]);
                _tip.WriteVLong(section.BlockPointers[i] - previous);
                previous = section.BlockPointers[i];
            }
        }

        _tim.WriteVLong(_tim.FilePointer);
        CodecUtil.WriteFooter(_tim);
        CodecUtil.WriteFooter(_tip);
        _tim.Sync();
        _tip.Sync();
    }

    public void Dispose()
    {
        _tim.Dispose();
        _tip.Dispose();
    }
}
