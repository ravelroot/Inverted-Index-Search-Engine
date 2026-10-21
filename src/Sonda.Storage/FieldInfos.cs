namespace Sonda.Storage;

public enum IndexOptions : byte
{
    None = 0,
    Docs = 1,
    DocsAndFreqs = 2,
    DocsFreqsAndPositions = 3,
}

public enum DocValuesKind : byte
{
    None = 0,
    Keyword = 1,
    Numeric = 2,
}

public sealed record FieldInfo(int Number, string Name, IndexOptions IndexOptions, bool HasNorms, DocValuesKind DocValues)
{
    public bool IsIndexed => IndexOptions != IndexOptions.None;

    public bool HasFreqs => IndexOptions >= IndexOptions.DocsAndFreqs;

    public bool HasPositions => IndexOptions >= IndexOptions.DocsFreqsAndPositions;
}

public sealed class FieldInfos
{
    private readonly FieldInfo[] _byNumber;
    private readonly Dictionary<string, FieldInfo> _byName;

    public FieldInfos(IEnumerable<FieldInfo> fields)
    {
        var list = fields.OrderBy(f => f.Number).ToArray();
        var max = list.Length == 0 ? -1 : list[^1].Number;
        _byNumber = new FieldInfo[max + 1];
        _byName = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        foreach (var field in list)
        {
            _byNumber[field.Number] = field;
            _byName[field.Name] = field;
        }

        All = list;
    }

    public IReadOnlyList<FieldInfo> All { get; }

    public FieldInfo? this[int number] => number >= 0 && number < _byNumber.Length ? _byNumber[number] : null;

    public FieldInfo? this[string name] => _byName.GetValueOrDefault(name);

    public void Write(IndexOutput output)
    {
        output.WriteVInt(All.Count);
        foreach (var field in All)
        {
            output.WriteVInt(field.Number);
            output.WriteString(field.Name);
            output.WriteByte((byte)field.IndexOptions);
            output.WriteByte(field.HasNorms ? (byte)1 : (byte)0);
            output.WriteByte((byte)field.DocValues);
        }
    }

    public static FieldInfos Read(IndexInput input)
    {
        var count = input.ReadVInt();
        var fields = new List<FieldInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var number = input.ReadVInt();
            var name = input.ReadString();
            var options = (IndexOptions)input.ReadByte();
            var norms = input.ReadByte() == 1;
            var docValues = (DocValuesKind)input.ReadByte();
            fields.Add(new FieldInfo(number, name, options, norms, docValues));
        }

        return new FieldInfos(fields);
    }
}

public sealed class FixedBitSet
{
    private readonly ulong[] _words;

    public FixedBitSet(int length)
    {
        Length = length;
        _words = new ulong[(length + 63) >> 6];
    }

    private FixedBitSet(int length, ulong[] words)
    {
        Length = length;
        _words = words;
    }

    public int Length { get; }

    public bool Get(int index) => (_words[index >> 6] & (1UL << (index & 63))) != 0;

    public bool Set(int index)
    {
        ref var word = ref _words[index >> 6];
        var mask = 1UL << (index & 63);
        var wasSet = (word & mask) != 0;
        word |= mask;
        return !wasSet;
    }

    public void Clear(int index) => _words[index >> 6] &= ~(1UL << (index & 63));

    public void SetAll()
    {
        Array.Fill(_words, ulong.MaxValue);
        var extra = (_words.Length << 6) - Length;
        if (extra > 0 && _words.Length > 0)
        {
            _words[^1] >>= extra;
        }
    }

    public int Cardinality()
    {
        var count = 0;
        foreach (var word in _words)
        {
            count += System.Numerics.BitOperations.PopCount(word);
        }

        return count;
    }

    public FixedBitSet Clone() => new(Length, (ulong[])_words.Clone());

    public void Write(IndexOutput output)
    {
        output.WriteVInt(Length);
        output.WriteVInt(_words.Length);
        foreach (var word in _words)
        {
            output.WriteInt64((long)word);
        }
    }

    public static FixedBitSet Read(IndexInput input)
    {
        var length = input.ReadVInt();
        var count = input.ReadVInt();
        var words = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            words[i] = (ulong)input.ReadInt64();
        }

        return new FixedBitSet(length, words);
    }
}
