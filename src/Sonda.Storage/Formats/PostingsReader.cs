namespace Sonda.Storage.Formats;

public sealed class PostingsReader : IDisposable
{
    private readonly IndexFile _docFile;
    private readonly IndexFile _posFile;

    public PostingsReader(IndexDirectory directory, string segment)
    {
        _docFile = directory.OpenFile($"{segment}.{PostingsWriter.DocExtension}");
        try
        {
            _posFile = directory.OpenFile($"{segment}.{PostingsWriter.PosExtension}");
        }
        catch
        {
            _docFile.Dispose();
            throw;
        }

        var docInput = _docFile.CreateInput(128);
        CodecUtil.CheckHeader(docInput, PostingsWriter.DocCodec, PostingsWriter.Version, PostingsWriter.Version);
        var posInput = _posFile.CreateInput(128);
        CodecUtil.CheckHeader(posInput, PostingsWriter.PosCodec, PostingsWriter.Version, PostingsWriter.Version);
    }

    public long SizeInBytes => _docFile.Length + _posFile.Length;

    public PostingsEnum Postings(in TermMeta meta, IndexOptions options, bool needsPositions)
    {
        var docInput = meta.DocFreq > 1 ? _docFile.CreateInput(8192) : null;
        var posInput = needsPositions && options >= IndexOptions.DocsFreqsAndPositions ? _posFile.CreateInput(4096) : null;
        return new PostingsEnum(meta, options, docInput, posInput);
    }

    public IEnumerable<IndexFile> Files => [_docFile, _posFile];

    public void Dispose()
    {
        _docFile.Dispose();
        _posFile.Dispose();
    }
}

public sealed class PostingsEnum
{
    public const int NoMoreDocs = int.MaxValue;

    private readonly TermMeta _meta;
    private readonly IndexInput? _doc;
    private readonly IndexInput? _pos;
    private readonly bool _hasFreqs;
    private readonly int[] _docs = new int[ForUtil.BlockSize];
    private readonly int[] _freqs = new int[ForUtil.BlockSize];
    private readonly int _fullBlocks;

    private int _bufferCount;
    private int _bufferUpto;
    private int _docsRead;
    private int _blocksRead;
    private int _accumulator;
    private long _posPendingCount;
    private int _positionsLeft;
    private int _position;

    private int[]? _skipLastDoc;
    private long[]? _skipDocEnd;
    private long[]? _skipPosEnd;
    private int[]? _skipMaxFreq;
    private byte[]? _skipMinNorm;

    internal PostingsEnum(in TermMeta meta, IndexOptions options, IndexInput? doc, IndexInput? pos)
    {
        _meta = meta;
        _doc = doc;
        _pos = pos;
        _hasFreqs = options >= IndexOptions.DocsAndFreqs;
        _fullBlocks = meta.DocFreq >= ForUtil.BlockSize ? meta.DocFreq / ForUtil.BlockSize : 0;
        _accumulator = -1;
        DocId = -1;
        _doc?.Seek(meta.DocStart);
        _pos?.Seek(meta.PosStart);
    }

    public int DocId { get; private set; }

    public int Freq { get; private set; }

    public long Cost => _meta.DocFreq;

    public int DocFreq => _meta.DocFreq;

    public int TermMaxFreq => _meta.MaxFreq;

    public byte TermMinNorm => _meta.MinNorm;

    public int NextDoc()
    {
        if (_bufferUpto >= _bufferCount)
        {
            if (_docsRead >= _meta.DocFreq)
            {
                DocId = NoMoreDocs;
                return NoMoreDocs;
            }

            Refill();
        }

        if (_pos is not null && DocId != -1 && DocId != NoMoreDocs)
        {
            _posPendingCount += _positionsLeft;
        }

        DocId = _docs[_bufferUpto];
        Freq = _freqs[_bufferUpto];
        _bufferUpto++;
        _positionsLeft = Freq;
        _position = 0;
        return DocId;
    }

    public int Advance(int target)
    {
        if (target <= DocId)
        {
            return DocId;
        }

        if (_fullBlocks > 0 && _meta.SkipOffset >= 0)
        {
            var lastBuffered = _bufferCount > 0 ? _docs[_bufferCount - 1] : -1;
            if (target > lastBuffered)
            {
                SkipTo(target);
            }
        }

        while (true)
        {
            var doc = NextDoc();
            if (doc >= target)
            {
                return doc;
            }
        }
    }

    public int NextPosition()
    {
        if (_pos is null)
        {
            throw new InvalidOperationException("Este campo não guarda posições.");
        }

        if (_posPendingCount > 0)
        {
            for (long i = 0; i < _posPendingCount; i++)
            {
                _pos.ReadVInt();
            }

            _posPendingCount = 0;
        }

        _position += _pos.ReadVInt();
        _positionsLeft--;
        return _position;
    }

    public int BlockCount => _fullBlocks;

    public bool TryGetBlockImpact(int target, out int blockLastDoc, out int maxFreq, out byte minNorm)
    {
        LoadSkips();
        if (_skipLastDoc is null)
        {
            blockLastDoc = NoMoreDocs;
            maxFreq = _meta.MaxFreq;
            minNorm = _meta.MinNorm;
            return false;
        }

        var index = LowerBound(_skipLastDoc, target);
        if (index >= _skipLastDoc.Length)
        {
            blockLastDoc = NoMoreDocs;
            maxFreq = _meta.MaxFreq;
            minNorm = _meta.MinNorm;
            return false;
        }

        blockLastDoc = _skipLastDoc[index];
        maxFreq = _skipMaxFreq![index];
        minNorm = _skipMinNorm![index];
        return true;
    }

    private void SkipTo(int target)
    {
        LoadSkips();
        if (_skipLastDoc is null)
        {
            return;
        }

        var block = LowerBound(_skipLastDoc, target);
        if (block <= _blocksRead - 1 || block == 0)
        {
            return;
        }

        if (block > _skipLastDoc.Length)
        {
            block = _skipLastDoc.Length;
        }

        var previous = block - 1;
        if (previous < _blocksRead)
        {
            return;
        }

        _doc!.Seek(_meta.DocStart + _skipDocEnd![previous]);
        _pos?.Seek(_meta.PosStart + _skipPosEnd![previous]);
        _accumulator = _skipLastDoc[previous];
        _blocksRead = block;
        _docsRead = block * ForUtil.BlockSize;
        _bufferCount = 0;
        _bufferUpto = 0;
        _posPendingCount = 0;
        _positionsLeft = 0;
        DocId = _accumulator;
    }

    private static int LowerBound(int[] values, int target)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (values[mid] < target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private void LoadSkips()
    {
        if (_skipLastDoc is not null || _fullBlocks == 0 || _meta.SkipOffset < 0 || _doc is null)
        {
            return;
        }

        var input = _doc.Clone();
        input.Seek(_meta.DocStart + _meta.SkipOffset);
        var count = input.ReadVInt();
        var lastDoc = new int[count];
        var docEnd = new long[count];
        var posEnd = new long[count];
        var maxFreq = new int[count];
        var minNorm = new byte[count];
        var doc = 0;
        long docPointer = 0;
        long posPointer = 0;
        for (var i = 0; i < count; i++)
        {
            doc += input.ReadVInt();
            docPointer += input.ReadVLong();
            posPointer += input.ReadVLong();
            lastDoc[i] = doc;
            docEnd[i] = docPointer;
            posEnd[i] = posPointer;
            maxFreq[i] = input.ReadVInt();
            minNorm[i] = input.ReadByte();
        }

        _skipDocEnd = docEnd;
        _skipPosEnd = posEnd;
        _skipMaxFreq = maxFreq;
        _skipMinNorm = minNorm;
        _skipLastDoc = lastDoc;
    }

    private void Refill()
    {
        if (_meta.DocFreq == 1)
        {
            _docs[0] = _meta.SingletonDoc;
            _freqs[0] = (int)Math.Max(1, _meta.TotalTermFreq);
            _bufferCount = 1;
            _bufferUpto = 0;
            _docsRead = 1;
            return;
        }

        var input = _doc!;
        if (_blocksRead < _fullBlocks)
        {
            ForUtil.Decode(input, _docs);
            if (_hasFreqs)
            {
                ForUtil.Decode(input, _freqs);
                for (var i = 0; i < ForUtil.BlockSize; i++)
                {
                    _freqs[i]++;
                }
            }
            else
            {
                Array.Fill(_freqs, 1);
            }

            var accumulator = _accumulator;
            for (var i = 0; i < ForUtil.BlockSize; i++)
            {
                accumulator += _docs[i];
                _docs[i] = accumulator;
            }

            _accumulator = accumulator;
            _blocksRead++;
            _bufferCount = ForUtil.BlockSize;
        }
        else
        {
            var remaining = _meta.DocFreq - _docsRead;
            var accumulator = _accumulator;
            for (var i = 0; i < remaining; i++)
            {
                if (_hasFreqs)
                {
                    var code = input.ReadVInt();
                    accumulator += code >>> 1;
                    _freqs[i] = (code & 1) != 0 ? 1 : input.ReadVInt();
                }
                else
                {
                    accumulator += input.ReadVInt();
                    _freqs[i] = 1;
                }

                _docs[i] = accumulator;
            }

            _accumulator = accumulator;
            _bufferCount = remaining;
        }

        _docsRead += _bufferCount;
        _bufferUpto = 0;
    }
}
