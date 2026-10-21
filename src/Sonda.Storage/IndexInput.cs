using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sonda.Storage;

public sealed class IndexFile : IDisposable
{
    private readonly SafeFileHandle _handle;
    private int _disposed;

    private IndexFile(string name, SafeFileHandle handle)
    {
        Name = name;
        _handle = handle;
        Length = RandomAccess.GetLength(handle);
    }

    public string Name { get; }

    public long Length { get; }

    internal SafeFileHandle Handle => _handle;

    public static IndexFile Open(string name, string fullPath)
    {
        var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        return new IndexFile(name, handle);
    }

    public IndexInput CreateInput(int bufferSize = 4096) => new(this, 0, Length, bufferSize);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }
    }
}

public sealed class IndexInput
{
    private readonly IndexFile _file;
    private readonly long _sliceStart;
    private readonly byte[] _buffer;
    private long _bufferStart;
    private int _bufferLength;
    private int _bufferPosition;

    internal IndexInput(IndexFile file, long sliceStart, long length, int bufferSize)
    {
        _file = file;
        _sliceStart = sliceStart;
        Length = length;
        _buffer = new byte[Math.Max(64, bufferSize)];
    }

    public string Name => _file.Name;

    public long Length { get; }

    public long Position => _bufferStart + _bufferPosition;

    public void Seek(long position)
    {
        if (position < 0 || position > Length)
        {
            throw new CorruptIndexException($"Posição {position} fora do arquivo {Name} ({Length} bytes).");
        }

        if (position >= _bufferStart && position < _bufferStart + _bufferLength)
        {
            _bufferPosition = (int)(position - _bufferStart);
            return;
        }

        _bufferStart = position;
        _bufferLength = 0;
        _bufferPosition = 0;
    }

    public void SkipBytes(long count) => Seek(Position + count);

    public byte ReadByte()
    {
        if (_bufferPosition >= _bufferLength)
        {
            Refill();
        }

        return _buffer[_bufferPosition++];
    }

    public void ReadBytes(Span<byte> destination)
    {
        var available = _bufferLength - _bufferPosition;
        if (destination.Length <= available)
        {
            _buffer.AsSpan(_bufferPosition, destination.Length).CopyTo(destination);
            _bufferPosition += destination.Length;
            return;
        }

        if (available > 0)
        {
            _buffer.AsSpan(_bufferPosition, available).CopyTo(destination);
            _bufferPosition += available;
            destination = destination[available..];
        }

        var position = Position;
        if (destination.Length >= _buffer.Length)
        {
            ReadFully(destination, position);
            _bufferStart = position + destination.Length;
            _bufferLength = 0;
            _bufferPosition = 0;
            return;
        }

        Refill();
        if (_bufferLength < destination.Length)
        {
            throw new CorruptIndexException($"Fim inesperado do arquivo {Name}.");
        }

        _buffer.AsSpan(0, destination.Length).CopyTo(destination);
        _bufferPosition = destination.Length;
    }

    public short ReadInt16()
    {
        Span<byte> bytes = stackalloc byte[2];
        ReadBytes(bytes);
        return BinaryPrimitives.ReadInt16LittleEndian(bytes);
    }

    public int ReadInt32()
    {
        if (_bufferLength - _bufferPosition >= 4)
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_bufferPosition, 4));
            _bufferPosition += 4;
            return value;
        }

        Span<byte> bytes = stackalloc byte[4];
        ReadBytes(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public long ReadInt64()
    {
        if (_bufferLength - _bufferPosition >= 8)
        {
            var value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(_bufferPosition, 8));
            _bufferPosition += 8;
            return value;
        }

        Span<byte> bytes = stackalloc byte[8];
        ReadBytes(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    public int ReadVInt()
    {
        uint result = 0;
        var shift = 0;
        while (true)
        {
            var b = ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return (int)result;
            }

            shift += 7;
            if (shift > 28)
            {
                var last = ReadByte();
                if ((last & 0xF0) != 0)
                {
                    throw new CorruptIndexException($"VInt inválido em {Name}.");
                }

                return (int)(result | ((uint)last << 28));
            }
        }
    }

    public long ReadVLong() => (long)ReadVarULong();

    public long ReadZLong()
    {
        var z = ReadVarULong();
        return (long)(z >> 1) ^ -(long)(z & 1);
    }

    private ulong ReadVarULong()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new CorruptIndexException($"VLong inválido em {Name}.");
            }
        }
    }

    public string ReadString()
    {
        var length = ReadVInt();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length <= 1024)
        {
            Span<byte> bytes = stackalloc byte[length];
            ReadBytes(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        var array = new byte[length];
        ReadBytes(array);
        return Encoding.UTF8.GetString(array);
    }

    public byte[] ReadBytesWithLength()
    {
        var length = ReadVInt();
        var bytes = new byte[length];
        ReadBytes(bytes);
        return bytes;
    }

    public IndexInput Clone()
    {
        var clone = new IndexInput(_file, _sliceStart, Length, _buffer.Length);
        clone.Seek(Position);
        return clone;
    }

    public IndexInput Slice(long offset, long length, int bufferSize = 4096)
    {
        if (offset < 0 || length < 0 || offset + length > Length)
        {
            throw new CorruptIndexException($"Fatia inválida em {Name}.");
        }

        return new IndexInput(_file, _sliceStart + offset, length, bufferSize);
    }

    private void Refill()
    {
        var start = _bufferStart + _bufferPosition;
        var remaining = Length - start;
        if (remaining <= 0)
        {
            throw new CorruptIndexException($"Leitura além do fim do arquivo {Name}.");
        }

        var toRead = (int)Math.Min(_buffer.Length, remaining);
        ReadFully(_buffer.AsSpan(0, toRead), start);
        _bufferStart = start;
        _bufferLength = toRead;
        _bufferPosition = 0;
    }

    private void ReadFully(Span<byte> destination, long position)
    {
        if (position + destination.Length > Length)
        {
            throw new CorruptIndexException($"Leitura além do fim do arquivo {Name}.");
        }

        var offset = _sliceStart + position;
        while (destination.Length > 0)
        {
            var read = RandomAccess.Read(_file.Handle, destination, offset);
            if (read <= 0)
            {
                throw new CorruptIndexException($"Fim inesperado do arquivo {Name}.");
            }

            destination = destination[read..];
            offset += read;
        }
    }
}

public sealed class CorruptIndexException(string message) : IOException(message);
