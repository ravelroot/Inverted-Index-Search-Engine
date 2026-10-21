namespace Sonda.Storage.Formats;

public struct TermMeta
{
    public int DocFreq;
    public long TotalTermFreq;
    public long DocStart;
    public long PosStart;
    public long SkipOffset;
    public int SingletonDoc;
    public int MaxFreq;
    public byte MinNorm;
}

public sealed class PostingsWriter : IDisposable
{
    public const string DocExtension = "doc";
    public const string PosExtension = "pos";
    internal const string DocCodec = "SondaPostingsDocs";
    internal const string PosCodec = "SondaPostingsPositions";
    internal const int Version = 1;

    private readonly IndexOutput _doc;
    private readonly IndexOutput _pos;
    private readonly int[] _docDeltas = new int[ForUtil.BlockSize];
    private readonly int[] _freqs = new int[ForUtil.BlockSize];
    private readonly List<SkipEntry> _skips = [];

    private IndexOptions _options;
    private int _buffered;
    private int _lastDoc;
    private int _docCount;
    private long _totalFreq;
    private long _docStart;
    private long _posStart;
    private int _firstDoc;
    private int _currentFreq;
    private int _lastPosition;
    private int _termMaxFreq;
    private byte _termMinNorm;
    private int _blockMaxFreq;
    private byte _blockMinNorm;

    private readonly record struct SkipEntry(int LastDoc, long DocEnd, long PosEnd, int MaxFreq, byte MinNorm);

    public PostingsWriter(IndexDirectory directory, string segment)
    {
        _doc = directory.CreateOutput($"{segment}.{DocExtension}");
        _pos = directory.CreateOutput($"{segment}.{PosExtension}");
        CodecUtil.WriteHeader(_doc, DocCodec, Version);
        CodecUtil.WriteHeader(_pos, PosCodec, Version);
    }

    public void StartTerm(IndexOptions options)
    {
        _options = options;
        _buffered = 0;
        _lastDoc = -1;
        _docCount = 0;
        _totalFreq = 0;
        _docStart = _doc.FilePointer;
        _posStart = _pos.FilePointer;
        _skips.Clear();
        _termMaxFreq = 0;
        _termMinNorm = byte.MaxValue;
        _blockMaxFreq = 0;
        _blockMinNorm = byte.MaxValue;
    }

    public void StartDoc(int docId, int freq, byte norm)
    {
        if (docId <= _lastDoc)
        {
            throw new InvalidOperationException($"Documentos fora de ordem na lista de postagens ({docId} depois de {_lastDoc}).");
        }

        if (_docCount == 0)
        {
            _firstDoc = docId;
        }

        var hasFreqs = _options >= IndexOptions.DocsAndFreqs;
        var effectiveFreq = hasFreqs ? Math.Max(1, freq) : 1;
        _docDeltas[_buffered] = docId - _lastDoc;
        _freqs[_buffered] = effectiveFreq;
        _buffered++;
        _lastDoc = docId;
        _docCount++;
        _totalFreq += effectiveFreq;
        _currentFreq = effectiveFreq;
        _lastPosition = 0;

        if (effectiveFreq > _termMaxFreq)
        {
            _termMaxFreq = effectiveFreq;
        }

        if (norm < _termMinNorm)
        {
            _termMinNorm = norm;
        }

        if (effectiveFreq > _blockMaxFreq)
        {
            _blockMaxFreq = effectiveFreq;
        }

        if (norm < _blockMinNorm)
        {
            _blockMinNorm = norm;
        }
    }

    public void AddPosition(int position)
    {
        if (position < _lastPosition)
        {
            throw new InvalidOperationException("Posições precisam ser crescentes dentro do documento.");
        }

        _pos.WriteVInt(position - _lastPosition);
        _lastPosition = position;
    }

    public void FinishDoc()
    {
        if (_buffered == ForUtil.BlockSize)
        {
            FlushBlock();
        }
    }

    public TermMeta FinishTerm()
    {
        var hasFreqs = _options >= IndexOptions.DocsAndFreqs;
        var meta = new TermMeta
        {
            DocFreq = _docCount,
            TotalTermFreq = hasFreqs ? _totalFreq : _docCount,
            DocStart = _docStart,
            PosStart = _posStart,
            SkipOffset = -1,
            SingletonDoc = -1,
            MaxFreq = Math.Max(1, _termMaxFreq),
            MinNorm = _termMinNorm,
        };

        if (_docCount == 1)
        {
            meta.SingletonDoc = _firstDoc;
            _buffered = 0;
            return meta;
        }

        for (var i = 0; i < _buffered; i++)
        {
            if (hasFreqs)
            {
                var freq = _freqs[i];
                _doc.WriteVInt((_docDeltas[i] << 1) | (freq == 1 ? 1 : 0));
                if (freq != 1)
                {
                    _doc.WriteVInt(freq);
                }
            }
            else
            {
                _doc.WriteVInt(_docDeltas[i]);
            }
        }

        _buffered = 0;

        if (_skips.Count > 0)
        {
            meta.SkipOffset = _doc.FilePointer - _docStart;
            _doc.WriteVInt(_skips.Count);
            var previousDoc = 0;
            long previousDocEnd = 0;
            long previousPosEnd = 0;
            foreach (var skip in _skips)
            {
                _doc.WriteVInt(skip.LastDoc - previousDoc);
                _doc.WriteVLong(skip.DocEnd - previousDocEnd);
                _doc.WriteVLong(skip.PosEnd - previousPosEnd);
                _doc.WriteVInt(skip.MaxFreq);
                _doc.WriteByte(skip.MinNorm);
                previousDoc = skip.LastDoc;
                previousDocEnd = skip.DocEnd;
                previousPosEnd = skip.PosEnd;
            }
        }

        return meta;
    }

    private void FlushBlock()
    {
        ForUtil.Encode(_docDeltas, _doc);
        if (_options >= IndexOptions.DocsAndFreqs)
        {
            Span<int> minusOne = stackalloc int[ForUtil.BlockSize];
            for (var i = 0; i < ForUtil.BlockSize; i++)
            {
                minusOne[i] = _freqs[i] - 1;
            }

            ForUtil.Encode(minusOne, _doc);
        }

        _skips.Add(new SkipEntry(_lastDoc, _doc.FilePointer - _docStart, _pos.FilePointer - _posStart, _blockMaxFreq, _blockMinNorm));
        _buffered = 0;
        _blockMaxFreq = 0;
        _blockMinNorm = byte.MaxValue;
    }

    public void Finish()
    {
        CodecUtil.WriteFooter(_doc);
        CodecUtil.WriteFooter(_pos);
        _doc.Sync();
        _pos.Sync();
    }

    public void Dispose()
    {
        _doc.Dispose();
        _pos.Dispose();
    }
}
