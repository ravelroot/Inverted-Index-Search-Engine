namespace Sonda.Storage.Formats;

public static class NormsFormat
{
    public const string Extension = "nrm";
    private const string Codec = "SondaNorms";
    private const int Version = 1;

    public static void Write(IndexDirectory directory, string segment, int docCount, IReadOnlyDictionary<int, byte[]> norms)
    {
        using var output = directory.CreateOutput($"{segment}.{Extension}");
        CodecUtil.WriteHeader(output, Codec, Version);
        output.WriteVInt(docCount);
        output.WriteVInt(norms.Count);
        foreach (var (field, values) in norms.OrderBy(n => n.Key))
        {
            if (values.Length != docCount)
            {
                throw new InvalidOperationException("Quantidade de normas diferente da quantidade de documentos.");
            }

            output.WriteVInt(field);
            output.WriteBytes(values);
        }

        CodecUtil.WriteFooter(output);
        output.Sync();
    }

    public static Dictionary<int, byte[]> Read(IndexDirectory directory, string segment)
    {
        using var file = directory.OpenFile($"{segment}.{Extension}");
        CodecUtil.VerifyFile(file);
        var input = file.CreateInput(64 * 1024);
        CodecUtil.CheckHeader(input, Codec, Version, Version);
        var docCount = input.ReadVInt();
        var count = input.ReadVInt();
        var result = new Dictionary<int, byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var field = input.ReadVInt();
            var values = new byte[docCount];
            input.ReadBytes(values);
            result[field] = values;
        }

        return result;
    }
}

public static class LiveDocsFormat
{
    public const string Extension = "liv";
    private const string Codec = "SondaLiveDocs";
    private const int Version = 1;

    public static string FileName(string segment, long generation) => $"{segment}_{generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}.{Extension}";

    public static void Write(IndexDirectory directory, string segment, long generation, FixedBitSet deleted)
    {
        using var output = directory.CreateOutput(FileName(segment, generation));
        CodecUtil.WriteHeader(output, Codec, Version);
        deleted.Write(output);
        CodecUtil.WriteFooter(output);
        output.Sync();
    }

    public static FixedBitSet Read(IndexDirectory directory, string segment, long generation)
    {
        using var file = directory.OpenFile(FileName(segment, generation));
        CodecUtil.VerifyFile(file);
        var input = file.CreateInput(64 * 1024);
        CodecUtil.CheckHeader(input, Codec, Version, Version);
        return FixedBitSet.Read(input);
    }
}

public sealed class KeywordDocValues(string[] values, int[] ordinals)
{
    public string[] Values { get; } = values;

    public int[] Ordinals { get; } = ordinals;

    public string? Get(int doc)
    {
        var ordinal = Ordinals[doc];
        return ordinal < 0 ? null : Values[ordinal];
    }
}

public sealed class DocValuesData
{
    public Dictionary<int, KeywordDocValues> Keywords { get; } = [];

    public Dictionary<int, long[]> Numerics { get; } = [];
}

public static class DocValuesFormat
{
    public const string Extension = "dv";
    private const string Codec = "SondaDocValues";
    private const int Version = 1;

    public static void Write(IndexDirectory directory, string segment, int docCount, DocValuesData data)
    {
        using var output = directory.CreateOutput($"{segment}.{Extension}");
        CodecUtil.WriteHeader(output, Codec, Version);
        output.WriteVInt(docCount);
        output.WriteVInt(data.Keywords.Count + data.Numerics.Count);

        foreach (var (field, keyword) in data.Keywords.OrderBy(k => k.Key))
        {
            output.WriteVInt(field);
            output.WriteByte((byte)DocValuesKind.Keyword);
            output.WriteVInt(keyword.Values.Length);
            foreach (var value in keyword.Values)
            {
                output.WriteString(value);
            }

            var width = keyword.Values.Length < byte.MaxValue ? 1 : keyword.Values.Length < ushort.MaxValue ? 2 : 4;
            output.WriteByte((byte)width);
            foreach (var ordinal in keyword.Ordinals)
            {
                var stored = ordinal + 1;
                switch (width)
                {
                    case 1:
                        output.WriteByte((byte)stored);
                        break;
                    case 2:
                        output.WriteByte((byte)stored);
                        output.WriteByte((byte)(stored >> 8));
                        break;
                    default:
                        output.WriteInt32(stored);
                        break;
                }
            }
        }

        foreach (var (field, values) in data.Numerics.OrderBy(k => k.Key))
        {
            output.WriteVInt(field);
            output.WriteByte((byte)DocValuesKind.Numeric);
            var min = values.Length == 0 ? 0 : values.Min();
            output.WriteZLong(min);
            var max = values.Length == 0 ? 0 : values.Max();
            var range = (ulong)(max - min);
            var bits = range == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(range);
            var width = bits == 0 ? 0 : bits <= 8 ? 1 : bits <= 16 ? 2 : bits <= 32 ? 4 : 8;
            output.WriteByte((byte)width);
            foreach (var value in values)
            {
                var delta = (ulong)(value - min);
                for (var b = 0; b < width; b++)
                {
                    output.WriteByte((byte)(delta >> (8 * b)));
                }
            }
        }

        CodecUtil.WriteFooter(output);
        output.Sync();
    }

    public static DocValuesData Read(IndexDirectory directory, string segment)
    {
        using var file = directory.OpenFile($"{segment}.{Extension}");
        CodecUtil.VerifyFile(file);
        var input = file.CreateInput(64 * 1024);
        CodecUtil.CheckHeader(input, Codec, Version, Version);
        var docCount = input.ReadVInt();
        var count = input.ReadVInt();
        var data = new DocValuesData();
        for (var i = 0; i < count; i++)
        {
            var field = input.ReadVInt();
            var kind = (DocValuesKind)input.ReadByte();
            if (kind == DocValuesKind.Keyword)
            {
                var valueCount = input.ReadVInt();
                var values = new string[valueCount];
                for (var v = 0; v < valueCount; v++)
                {
                    values[v] = input.ReadString();
                }

                var width = input.ReadByte();
                var ordinals = new int[docCount];
                for (var d = 0; d < docCount; d++)
                {
                    var stored = width switch
                    {
                        1 => input.ReadByte(),
                        2 => input.ReadByte() | (input.ReadByte() << 8),
                        _ => input.ReadInt32(),
                    };
                    ordinals[d] = stored - 1;
                }

                data.Keywords[field] = new KeywordDocValues(values, ordinals);
            }
            else
            {
                var min = input.ReadZLong();
                var width = input.ReadByte();
                var values = new long[docCount];
                for (var d = 0; d < docCount; d++)
                {
                    ulong delta = 0;
                    for (var b = 0; b < width; b++)
                    {
                        delta |= (ulong)input.ReadByte() << (8 * b);
                    }

                    values[d] = min + (long)delta;
                }

                data.Numerics[field] = values;
            }
        }

        return data;
    }
}
