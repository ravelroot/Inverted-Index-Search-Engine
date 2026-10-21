using System.Buffers;
using System.Numerics;

namespace Sonda.Storage;

public static class CodecUtil
{
    public const int HeaderMagic = 0x534F4E44;
    public const int FooterMagic = ~HeaderMagic;
    public const int FooterLength = 16;

    public static void WriteHeader(IndexOutput output, string codec, int version)
    {
        output.WriteInt32(HeaderMagic);
        output.WriteString(codec);
        output.WriteInt32(version);
    }

    public static int CheckHeader(IndexInput input, string codec, int minVersion, int maxVersion)
    {
        var magic = input.ReadInt32();
        if (magic != HeaderMagic)
        {
            throw new CorruptIndexException($"{input.Name} não é um arquivo da Sonda (assinatura inválida).");
        }

        var actual = input.ReadString();
        if (!string.Equals(actual, codec, StringComparison.Ordinal))
        {
            throw new CorruptIndexException($"{input.Name}: formato {actual}, esperado {codec}.");
        }

        var version = input.ReadInt32();
        if (version < minVersion || version > maxVersion)
        {
            throw new CorruptIndexException($"{input.Name}: versão {version} não suportada ({minVersion} a {maxVersion}).");
        }

        return version;
    }

    public static void WriteFooter(IndexOutput output)
    {
        output.WriteInt32(FooterMagic);
        output.WriteInt32(0);
        output.WriteInt64(output.Checksum);
    }

    public static uint ComputeChecksum(IndexFile file)
    {
        if (file.Length < FooterLength)
        {
            throw new CorruptIndexException($"{file.Name} é pequeno demais para ter rodapé.");
        }

        var input = file.CreateInput(64 * 1024);
        var toHash = file.Length - 8;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            uint crc = 0;
            while (toHash > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, toHash);
                input.ReadBytes(buffer.AsSpan(0, chunk));
                crc = Crc32.Append(crc, buffer.AsSpan(0, chunk));
                toHash -= chunk;
            }

            return crc;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static void VerifyFile(IndexFile file)
    {
        var computed = ComputeChecksum(file);
        var input = file.CreateInput();
        input.Seek(file.Length - FooterLength);
        if (input.ReadInt32() != FooterMagic)
        {
            throw new CorruptIndexException($"{file.Name}: rodapé ausente ou arquivo truncado.");
        }

        input.ReadInt32();
        var stored = (uint)input.ReadInt64();
        if (stored != computed)
        {
            throw new CorruptIndexException($"{file.Name}: checksum não confere (gravado {stored:X8}, calculado {computed:X8}).");
        }
    }

    public static int BitsRequired(uint value) => value == 0 ? 0 : 32 - BitOperations.LeadingZeroCount(value);
}

public static class ForUtil
{
    public const int BlockSize = 128;

    public static void Encode(ReadOnlySpan<int> values, IndexOutput output)
    {
        var or = 0;
        for (var i = 0; i < BlockSize; i++)
        {
            or |= values[i];
        }

        var bits = CodecUtil.BitsRequired((uint)or);
        output.WriteByte((byte)bits);
        if (bits == 0)
        {
            return;
        }

        Span<byte> packed = stackalloc byte[BlockSize * 4];
        ulong accumulator = 0;
        var accumulated = 0;
        var position = 0;
        for (var i = 0; i < BlockSize; i++)
        {
            accumulator |= (ulong)(uint)values[i] << accumulated;
            accumulated += bits;
            while (accumulated >= 8)
            {
                packed[position++] = (byte)accumulator;
                accumulator >>= 8;
                accumulated -= 8;
            }
        }

        output.WriteBytes(packed[..position]);
    }

    public static void Decode(IndexInput input, Span<int> values)
    {
        int bits = input.ReadByte();
        if (bits == 0)
        {
            values[..BlockSize].Clear();
            return;
        }

        if (bits > 32)
        {
            throw new CorruptIndexException($"Bloco compactado inválido em {input.Name}.");
        }

        var byteCount = bits * (BlockSize / 8);
        Span<byte> packed = stackalloc byte[byteCount];
        input.ReadBytes(packed);
        var mask = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
        ulong accumulator = 0;
        var accumulated = 0;
        var position = 0;
        for (var i = 0; i < BlockSize; i++)
        {
            while (accumulated < bits)
            {
                accumulator |= (ulong)packed[position++] << accumulated;
                accumulated += 8;
            }

            values[i] = (int)((uint)accumulator & mask);
            accumulator >>= bits;
            accumulated -= bits;
        }
    }

    public static void Skip(IndexInput input)
    {
        int bits = input.ReadByte();
        input.SkipBytes(bits * (BlockSize / 8));
    }
}

public static class NormEncoding
{
    private static readonly int NumFreeValues = 255 - LongToInt4(int.MaxValue);
    private static readonly int[] Table = BuildTable();

    public static byte Encode(int length)
    {
        if (length < 0)
        {
            length = 0;
        }

        return length < NumFreeValues ? (byte)length : (byte)(NumFreeValues + LongToInt4(length - NumFreeValues));
    }

    public static int Decode(byte value) => Table[value];

    private static int[] BuildTable()
    {
        var table = new int[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = i < NumFreeValues ? i : (int)Math.Min(int.MaxValue, NumFreeValues + Int4ToLong(i - NumFreeValues));
        }

        return table;
    }

    private static int LongToInt4(long value)
    {
        var numBits = 64 - BitOperations.LeadingZeroCount((ulong)value);
        if (numBits < 4)
        {
            return (int)value;
        }

        var shift = numBits - 4;
        var encoded = (int)((ulong)value >> shift) & 0x07;
        return encoded | ((shift + 1) << 3);
    }

    private static long Int4ToLong(int value)
    {
        long bits = value & 0x07;
        var shift = (value >> 3) - 1;
        return shift == -1 ? bits : (bits | 0x08) << shift;
    }
}
