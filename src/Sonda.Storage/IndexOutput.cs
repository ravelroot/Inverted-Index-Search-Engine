using System.Buffers.Binary;
using System.Text;

namespace Sonda.Storage;

public sealed class IndexOutput : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly FileStream _stream;
    private readonly byte[] _buffer = new byte[BufferSize];
    private int _count;
    private long _flushed;
    private uint _crc;
    private bool _disposed;

    internal IndexOutput(string name, string fullPath)
    {
        Name = name;
        _stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.None);
    }

    public string Name { get; }

    public long FilePointer => _flushed + _count;

    public uint Checksum
    {
        get
        {
            FlushBuffer();
            return _crc;
        }
    }

    public void WriteByte(byte value)
    {
        if (_count == BufferSize)
        {
            FlushBuffer();
        }

        _buffer[_count++] = value;
    }

    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length > BufferSize - _count)
        {
            FlushBuffer();
            if (data.Length >= BufferSize)
            {
                _crc = Crc32.Append(_crc, data);
                _stream.Write(data);
                _flushed += data.Length;
                return;
            }
        }

        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        WriteBytes(bytes);
    }

    public void WriteInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        WriteBytes(bytes);
    }

    public void WriteVInt(int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            WriteByte((byte)(v | 0x80));
            v >>= 7;
        }

        WriteByte((byte)v);
    }

    public void WriteVLong(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "VLong não aceita valores negativos.");
        }

        WriteVarULong((ulong)value);
    }

    public void WriteZLong(long value) => WriteVarULong((ulong)((value << 1) ^ (value >> 63)));

    private void WriteVarULong(ulong v)
    {
        while (v >= 0x80)
        {
            WriteByte((byte)(v | 0x80));
            v >>= 7;
        }

        WriteByte((byte)v);
    }

    public void WriteString(string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        WriteVInt(length);
        if (length <= 1024)
        {
            Span<byte> bytes = stackalloc byte[length];
            Encoding.UTF8.GetBytes(value, bytes);
            WriteBytes(bytes);
        }
        else
        {
            WriteBytes(Encoding.UTF8.GetBytes(value));
        }
    }

    public void WriteBytesWithLength(ReadOnlySpan<byte> data)
    {
        WriteVInt(data.Length);
        WriteBytes(data);
    }

    public void Sync()
    {
        FlushBuffer();
        _stream.Flush(true);
    }

    private void FlushBuffer()
    {
        if (_count == 0)
        {
            return;
        }

        var span = _buffer.AsSpan(0, _count);
        _crc = Crc32.Append(_crc, span);
        _stream.Write(span);
        _flushed += _count;
        _count = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FlushBuffer();
        _stream.Dispose();
    }
}
