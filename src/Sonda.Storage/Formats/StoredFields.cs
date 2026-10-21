using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Sonda.Storage.Formats;

public readonly record struct StoredValue(int Field, string? Text, long Number)
{
    public static StoredValue Of(int field, string text) => new(field, text, 0);

    public static StoredValue Of(int field, long number) => new(field, null, number);

    public bool IsText => Text is not null;
}

public sealed class StoredFieldsWriter : IDisposable
{
    public const string DataExtension = "fdt";
    public const string IndexExtension = "fdx";
    internal const string DataCodec = "SondaStoredData";
    internal const string IndexCodec = "SondaStoredIndex";
    internal const int Version = 1;
    private const int ChunkBytes = 48 * 1024;
    private const int ChunkDocs = 128;

    private readonly IndexOutput _data;
    private readonly IndexOutput _index;
    private readonly ArrayBufferWriter<byte> _buffer = new(ChunkBytes * 2);
    private readonly List<(int DocBase, long Pointer)> _chunks = [];
    private int _chunkDocBase;
    private int _chunkDocs;
    private int _docCount;

    public StoredFieldsWriter(IndexDirectory directory, string segment)
    {
        _data = directory.CreateOutput($"{segment}.{DataExtension}");
        _index = directory.CreateOutput($"{segment}.{IndexExtension}");
        CodecUtil.WriteHeader(_data, DataCodec, Version);
        CodecUtil.WriteHeader(_index, IndexCodec, Version);
    }

    public int DocCount => _docCount;

    public void AddDocument(IReadOnlyList<StoredValue> values)
    {
        WriteVInt(values.Count);
        foreach (var value in values)
        {
            WriteVInt(value.Field);
            if (value.Text is not null)
            {
                _buffer.GetSpan(1)[0] = 0;
                _buffer.Advance(1);
                var byteCount = Encoding.UTF8.GetByteCount(value.Text);
                WriteVInt(byteCount);
                var span = _buffer.GetSpan(byteCount);
                Encoding.UTF8.GetBytes(value.Text, span);
                _buffer.Advance(byteCount);
            }
            else
            {
                _buffer.GetSpan(1)[0] = 1;
                _buffer.Advance(1);
                var span = _buffer.GetSpan(8);
                BinaryPrimitives.WriteInt64LittleEndian(span, value.Number);
                _buffer.Advance(8);
            }
        }

        _chunkDocs++;
        _docCount++;
        if (_buffer.WrittenCount >= ChunkBytes || _chunkDocs >= ChunkDocs)
        {
            FlushChunk();
        }
    }

    private void WriteVInt(int value)
    {
        var v = (uint)value;
        var span = _buffer.GetSpan(5);
        var i = 0;
        while (v >= 0x80)
        {
            span[i++] = (byte)(v | 0x80);
            v >>= 7;
        }

        span[i++] = (byte)v;
        _buffer.Advance(i);
    }

    private void FlushChunk()
    {
        if (_chunkDocs == 0)
        {
            return;
        }

        var source = _buffer.WrittenSpan;
        var maxCompressed = BrotliEncoder.GetMaxCompressedLength(source.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxCompressed);
        try
        {
            if (!BrotliEncoder.TryCompress(source, rented, out var written, 4, 22))
            {
                throw new IOException("Falha ao comprimir um bloco de campos armazenados.");
            }

            _chunks.Add((_chunkDocBase, _data.FilePointer));
            _data.WriteVInt(_chunkDocBase);
            _data.WriteVInt(_chunkDocs);
            _data.WriteVInt(source.Length);
            _data.WriteVInt(written);
            _data.WriteBytes(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        _chunkDocBase = _docCount;
        _chunkDocs = 0;
        _buffer.ResetWrittenCount();
    }

    public void Finish()
    {
        FlushChunk();
        _index.WriteVInt(_docCount);
        _index.WriteVInt(_chunks.Count);
        var previousBase = 0;
        long previousPointer = 0;
        foreach (var (docBase, pointer) in _chunks)
        {
            _index.WriteVInt(docBase - previousBase);
            _index.WriteVLong(pointer - previousPointer);
            previousBase = docBase;
            previousPointer = pointer;
        }

        CodecUtil.WriteFooter(_data);
        CodecUtil.WriteFooter(_index);
        _data.Sync();
        _index.Sync();
    }

    public void Dispose()
    {
        _data.Dispose();
        _index.Dispose();
    }
}

public sealed class StoredFieldsReader : IDisposable
{
    private readonly IndexFile _data;
    private readonly int[] _docBases;
    private readonly long[] _pointers;

    [ThreadStatic]
    private static ChunkCache? _cache;

    private sealed class ChunkCache
    {
        public StoredFieldsReader? Owner;
        public int Chunk = -1;
        public byte[] Bytes = [];
        public int Length;
        public int DocBase;
        public int DocCount;
    }

    public StoredFieldsReader(IndexDirectory directory, string segment)
    {
        _data = directory.OpenFile($"{segment}.{StoredFieldsWriter.DataExtension}");
        try
        {
            var header = _data.CreateInput(128);
            CodecUtil.CheckHeader(header, StoredFieldsWriter.DataCodec, StoredFieldsWriter.Version, StoredFieldsWriter.Version);
            using var indexFile = directory.OpenFile($"{segment}.{StoredFieldsWriter.IndexExtension}");
            CodecUtil.VerifyFile(indexFile);
            var index = indexFile.CreateInput(16 * 1024);
            CodecUtil.CheckHeader(index, StoredFieldsWriter.IndexCodec, StoredFieldsWriter.Version, StoredFieldsWriter.Version);
            DocCount = index.ReadVInt();
            var count = index.ReadVInt();
            _docBases = new int[count];
            _pointers = new long[count];
            var docBase = 0;
            long pointer = 0;
            for (var i = 0; i < count; i++)
            {
                docBase += index.ReadVInt();
                pointer += index.ReadVLong();
                _docBases[i] = docBase;
                _pointers[i] = pointer;
            }
        }
        catch
        {
            _data.Dispose();
            throw;
        }
    }

    public int DocCount { get; }

    public long SizeInBytes => _data.Length;

    public List<StoredValue> Document(int doc)
    {
        if ((uint)doc >= (uint)DocCount)
        {
            throw new ArgumentOutOfRangeException(nameof(doc));
        }

        var chunk = FindChunk(doc);
        var cache = _cache ??= new ChunkCache();
        if (!ReferenceEquals(cache.Owner, this) || cache.Chunk != chunk)
        {
            LoadChunk(chunk, cache);
        }

        var span = cache.Bytes.AsSpan(0, cache.Length);
        var position = 0;
        for (var current = cache.DocBase; current < doc; current++)
        {
            SkipDocument(span, ref position);
        }

        return ReadDocument(span, ref position);
    }

    private void LoadChunk(int chunk, ChunkCache cache)
    {
        var input = _data.CreateInput(8192);
        input.Seek(_pointers[chunk]);
        var docBase = input.ReadVInt();
        var docs = input.ReadVInt();
        var uncompressed = input.ReadVInt();
        var compressed = input.ReadVInt();
        var source = ArrayPool<byte>.Shared.Rent(compressed);
        try
        {
            input.ReadBytes(source.AsSpan(0, compressed));
            if (cache.Bytes.Length < uncompressed)
            {
                cache.Bytes = new byte[Math.Max(uncompressed, 64 * 1024)];
            }

            if (!BrotliDecoder.TryDecompress(source.AsSpan(0, compressed), cache.Bytes.AsSpan(0, uncompressed), out var written) || written != uncompressed)
            {
                throw new CorruptIndexException($"Bloco {chunk} de campos armazenados corrompido em {_data.Name}.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(source);
        }

        cache.Owner = this;
        cache.Chunk = chunk;
        cache.Length = uncompressed;
        cache.DocBase = docBase;
        cache.DocCount = docs;
    }

    private int FindChunk(int doc)
    {
        var low = 0;
        var high = _docBases.Length - 1;
        var result = 0;
        while (low <= high)
        {
            var mid = (low + high) >>> 1;
            if (_docBases[mid] <= doc)
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

    private static int ReadVInt(ReadOnlySpan<byte> span, ref int position)
    {
        uint result = 0;
        var shift = 0;
        while (true)
        {
            var b = span[position++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return (int)result;
            }

            shift += 7;
        }
    }

    private static void SkipDocument(ReadOnlySpan<byte> span, ref int position)
    {
        var count = ReadVInt(span, ref position);
        for (var i = 0; i < count; i++)
        {
            ReadVInt(span, ref position);
            var type = span[position++];
            if (type == 0)
            {
                var length = ReadVInt(span, ref position);
                position += length;
            }
            else
            {
                position += 8;
            }
        }
    }

    private static List<StoredValue> ReadDocument(ReadOnlySpan<byte> span, ref int position)
    {
        var count = ReadVInt(span, ref position);
        var values = new List<StoredValue>(count);
        for (var i = 0; i < count; i++)
        {
            var field = ReadVInt(span, ref position);
            var type = span[position++];
            if (type == 0)
            {
                var length = ReadVInt(span, ref position);
                values.Add(StoredValue.Of(field, Encoding.UTF8.GetString(span.Slice(position, length))));
                position += length;
            }
            else
            {
                values.Add(StoredValue.Of(field, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(position, 8))));
                position += 8;
            }
        }

        return values;
    }

    public void Dispose()
    {
        if (_cache is not null && ReferenceEquals(_cache.Owner, this))
        {
            _cache.Owner = null;
        }

        _data.Dispose();
    }
}
