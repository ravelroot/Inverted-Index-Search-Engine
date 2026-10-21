using System.Text;

namespace Sonda.Storage.Formats;

public sealed class TermsReader : IDisposable
{
    private readonly IndexFile _tim;
    private readonly Dictionary<int, FieldTerms> _fields = [];

    public TermsReader(IndexDirectory directory, string segment, FieldInfos fieldInfos)
    {
        _tim = directory.OpenFile($"{segment}.{TermsWriter.DictionaryExtension}");
        try
        {
            var timHeader = _tim.CreateInput(128);
            CodecUtil.CheckHeader(timHeader, TermsWriter.DictionaryCodec, TermsWriter.Version, TermsWriter.Version);

            using var tipFile = directory.OpenFile($"{segment}.{TermsWriter.IndexExtension}");
            CodecUtil.VerifyFile(tipFile);
            var tip = tipFile.CreateInput(16 * 1024);
            CodecUtil.CheckHeader(tip, TermsWriter.IndexCodec, TermsWriter.Version, TermsWriter.Version);
            var fieldCount = tip.ReadVInt();
            for (var f = 0; f < fieldCount; f++)
            {
                var number = tip.ReadVInt();
                var info = fieldInfos[number] ?? throw new CorruptIndexException($"Campo {number} desconhecido no dicionário de {segment}.");
                var termCount = tip.ReadVLong();
                var sumDocFreq = tip.ReadVLong();
                var sumTotalTermFreq = tip.ReadVLong();
                var docCount = tip.ReadVInt();
                var blockCount = tip.ReadVInt();
                var firstTerms = new byte[blockCount][];
                var pointers = new long[blockCount];
                long pointer = 0;
                for (var b = 0; b < blockCount; b++)
                {
                    firstTerms[b] = tip.ReadBytesWithLength();
                    pointer += tip.ReadVLong();
                    pointers[b] = pointer;
                }

                _fields[number] = new FieldTerms(this, info, new FieldTermStats(termCount, sumDocFreq, sumTotalTermFreq, docCount), firstTerms, pointers);
            }
        }
        catch
        {
            _tim.Dispose();
            throw;
        }
    }

    public FieldTerms? Field(int number) => _fields.GetValueOrDefault(number);

    public IEnumerable<FieldTerms> Fields => _fields.Values;

    public long SizeInBytes => _tim.Length;

    internal IndexInput OpenDictionary() => _tim.CreateInput(4096);

    public void Dispose() => _tim.Dispose();
}

public sealed class FieldTerms
{
    private readonly TermsReader _reader;
    private readonly byte[][] _firstTerms;
    private readonly long[] _pointers;

    internal FieldTerms(TermsReader reader, FieldInfo field, FieldTermStats stats, byte[][] firstTerms, long[] pointers)
    {
        _reader = reader;
        Field = field;
        Stats = stats;
        _firstTerms = firstTerms;
        _pointers = pointers;
    }

    public FieldInfo Field { get; }

    public FieldTermStats Stats { get; }

    public int BlockCount => _pointers.Length;

    public bool TryGetTerm(ReadOnlySpan<byte> term, out TermMeta meta)
    {
        meta = default;
        var block = FindBlock(term);
        if (block < 0)
        {
            return false;
        }

        var terms = new TermsEnum(this, _reader.OpenDictionary(), block);
        while (terms.MoveNext())
        {
            var comparison = terms.TermBytes.SequenceCompareTo(term);
            if (comparison == 0)
            {
                meta = terms.Meta;
                return true;
            }

            if (comparison > 0 || terms.Block != block)
            {
                return false;
            }
        }

        return false;
    }

    public bool TryGetTerm(string term, out TermMeta meta) => TryGetTerm(Encoding.UTF8.GetBytes(term), out meta);

    public TermsEnum GetEnumerator() => new(this, _reader.OpenDictionary(), 0);

    public TermsEnum SeekCeil(ReadOnlySpan<byte> target)
    {
        var block = Math.Max(0, FindBlock(target));
        var terms = new TermsEnum(this, _reader.OpenDictionary(), block);
        terms.SkipBefore(target);
        return terms;
    }

    internal long BlockPointer(int block) => _pointers[block];

    private int FindBlock(ReadOnlySpan<byte> term)
    {
        var low = 0;
        var high = _firstTerms.Length - 1;
        var result = -1;
        while (low <= high)
        {
            var mid = (low + high) >>> 1;
            var comparison = _firstTerms[mid].AsSpan().SequenceCompareTo(term);
            if (comparison <= 0)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }
}

public sealed class TermsEnum
{
    private readonly FieldTerms _terms;
    private readonly IndexInput _input;
    private readonly FieldInfo _field;
    private byte[] _term = new byte[64];
    private int _termLength;
    private int _remainingInBlock;
    private long _previousDocStart;
    private long _previousPosStart;
    private bool _peeked;
    private TermMeta _meta;

    internal TermsEnum(FieldTerms terms, IndexInput input, int block)
    {
        _terms = terms;
        _input = input;
        _field = terms.Field;
        Block = block - 1;
        _nextBlock = block;
    }

    private int _nextBlock;

    public int Block { get; private set; }

    public ReadOnlySpan<byte> TermBytes => _term.AsSpan(0, _termLength);

    public string Term => Encoding.UTF8.GetString(_term, 0, _termLength);

    public byte[] TermCopy() => _term.AsSpan(0, _termLength).ToArray();

    public TermMeta Meta => _meta;

    public FieldInfo Field => _field;

    public bool MoveNext()
    {
        if (_peeked)
        {
            _peeked = false;
            return true;
        }

        if (_remainingInBlock == 0)
        {
            if (_nextBlock >= _terms.BlockCount)
            {
                return false;
            }

            _input.Seek(_terms.BlockPointer(_nextBlock));
            Block = _nextBlock;
            _nextBlock++;
            _remainingInBlock = _input.ReadVInt();
            _termLength = 0;
            _previousDocStart = 0;
            _previousPosStart = 0;
        }

        ReadEntry();
        _remainingInBlock--;
        return true;
    }

    internal void SkipBefore(ReadOnlySpan<byte> target)
    {
        while (MoveNext())
        {
            if (TermBytes.SequenceCompareTo(target) >= 0)
            {
                _peeked = true;
                return;
            }
        }
    }

    private void ReadEntry()
    {
        var prefix = _input.ReadVInt();
        var suffix = _input.ReadVInt();
        var length = prefix + suffix;
        if (length > _term.Length)
        {
            Array.Resize(ref _term, Math.Max(length, _term.Length * 2));
        }

        _input.ReadBytes(_term.AsSpan(prefix, suffix));
        _termLength = length;

        var meta = new TermMeta { SkipOffset = -1, SingletonDoc = -1, MinNorm = 1 };
        meta.DocFreq = _input.ReadVInt();
        meta.TotalTermFreq = _field.HasFreqs ? _input.ReadVLong() + meta.DocFreq : meta.DocFreq;
        if (meta.DocFreq == 1)
        {
            meta.SingletonDoc = _input.ReadVInt();
            meta.DocStart = _previousDocStart;
        }
        else
        {
            meta.DocStart = _previousDocStart + _input.ReadZLong();
            _previousDocStart = meta.DocStart;
        }

        if (_field.HasPositions)
        {
            meta.PosStart = _previousPosStart + _input.ReadZLong();
            _previousPosStart = meta.PosStart;
        }

        if (meta.DocFreq >= ForUtil.BlockSize)
        {
            meta.SkipOffset = _input.ReadVLong();
        }

        if (_field.HasFreqs)
        {
            meta.MaxFreq = _input.ReadVInt();
            meta.MinNorm = _input.ReadByte();
        }
        else
        {
            meta.MaxFreq = 1;
        }

        _meta = meta;
    }
}
