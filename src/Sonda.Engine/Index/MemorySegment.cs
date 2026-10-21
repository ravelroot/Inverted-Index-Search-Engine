using System.Collections.Concurrent;
using Sonda.Storage;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Index;

internal sealed class TermBuffer
{
    private readonly Lock _lock = new();
    private int[] _docs = new int[2];
    private int[] _freqs = new int[2];
    private int[] _positionStarts = new int[2];
    private int[] _positions = [];
    private int _count;
    private int _positionCount;

    public int Count => _count;

    public long Add(int doc, ReadOnlySpan<int> positions, bool storePositions)
    {
        lock (_lock)
        {
            if (_count == _docs.Length)
            {
                var size = _docs.Length * 2;
                Array.Resize(ref _docs, size);
                Array.Resize(ref _freqs, size);
                Array.Resize(ref _positionStarts, size);
            }

            _docs[_count] = doc;
            _freqs[_count] = positions.Length;
            _positionStarts[_count] = _positionCount;
            _count++;

            if (storePositions)
            {
                if (_positionCount + positions.Length > _positions.Length)
                {
                    Array.Resize(ref _positions, Math.Max(_positionCount + positions.Length, Math.Max(4, _positions.Length * 2)));
                }

                positions.CopyTo(_positions.AsSpan(_positionCount));
                _positionCount += positions.Length;
            }

            return 12 + (storePositions ? positions.Length * 4L : 0);
        }
    }

    public void WriteTo(PostingsWriter writer, FieldInfo field, byte[]? norms)
    {
        var order = new int[_count];
        var keys = new int[_count];
        for (var i = 0; i < _count; i++)
        {
            order[i] = i;
            keys[i] = _docs[i];
        }

        Array.Sort(keys, order);
        writer.StartTerm(field.IndexOptions);
        for (var i = 0; i < order.Length; i++)
        {
            var slot = order[i];
            var doc = _docs[slot];
            writer.StartDoc(doc, _freqs[slot], norms is null ? (byte)1 : norms[doc]);
            if (field.HasPositions)
            {
                var start = _positionStarts[slot];
                for (var p = 0; p < _freqs[slot]; p++)
                {
                    writer.AddPosition(_positions[start + p]);
                }
            }

            writer.FinishDoc();
        }
    }
}

internal sealed class MemorySegment
{
    private readonly ConcurrentDictionary<string, TermBuffer>?[] _fields;
    private readonly ConcurrentDictionary<int, BufferedDocument> _documents = new();
    private readonly ConcurrentDictionary<string, int> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, byte> _deleted = new();
    private int _lastDoc = -1;
    private long _ramBytes;

    public MemorySegment(string name)
    {
        Name = name;
        _fields = new ConcurrentDictionary<string, TermBuffer>?[Schema.Fields.All.Count];
        foreach (var field in Schema.IndexedFields)
        {
            _fields[field] = new ConcurrentDictionary<string, TermBuffer>(Environment.ProcessorCount * 2, 1024, StringComparer.Ordinal);
        }
    }

    public string Name { get; }

    public int DocCount => Volatile.Read(ref _lastDoc) + 1;

    public long RamBytes => Interlocked.Read(ref _ramBytes);

    public int DeletedCount => _deleted.Count;

    public int Add(AnalyzedDocument document)
    {
        var doc = Interlocked.Increment(ref _lastDoc);
        long bytes = 160 + document.StoredBytes;
        var lengths = new int[Schema.Fields.All.Count];

        foreach (var fieldNumber in Schema.IndexedFields)
        {
            var analyzed = document.Fields[fieldNumber];
            if (analyzed is null)
            {
                continue;
            }

            var info = Schema.Fields[fieldNumber]!;
            var dictionary = _fields[fieldNumber]!;
            lengths[fieldNumber] = analyzed.Length;
            foreach (var (term, positions) in analyzed.Terms)
            {
                if (!dictionary.TryGetValue(term, out var buffer))
                {
                    var created = new TermBuffer();
                    buffer = dictionary.GetOrAdd(term, created);
                    if (ReferenceEquals(buffer, created))
                    {
                        bytes += 96 + (term.Length * 2L);
                    }
                }

                bytes += buffer.Add(doc, positions.Items, info.HasPositions);
            }
        }

        _documents[doc] = new BufferedDocument(document.Stored, lengths, document.Keywords, document.Numerics);
        if (_keys.TryGetValue(document.Source.Key, out var previous))
        {
            _deleted.TryAdd(previous, 0);
        }

        _keys[document.Source.Key] = doc;
        Interlocked.Add(ref _ramBytes, bytes);
        return doc;
    }

    public bool DeleteKey(string key)
    {
        if (_keys.TryRemove(key, out var doc))
        {
            _deleted.TryAdd(doc, 0);
            return true;
        }

        return false;
    }

    public int DeleteByPrefix(string prefix)
    {
        var count = 0;
        foreach (var key in _keys.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && DeleteKey(key))
            {
                count++;
            }
        }

        return count;
    }

    public bool ContainsKey(string key) => _keys.ContainsKey(key);

    public IEnumerable<KeyValuePair<string, TermBuffer>> Terms(int field)
        => _fields[field] ?? Enumerable.Empty<KeyValuePair<string, TermBuffer>>();

    public BufferedDocument Document(int doc) => _documents.TryGetValue(doc, out var value)
        ? value
        : throw new InvalidOperationException($"Documento {doc} ausente no buffer {Name}.");

    public FixedBitSet BuildDeleted()
    {
        var bits = new FixedBitSet(DocCount);
        foreach (var doc in _deleted.Keys)
        {
            bits.Set(doc);
        }

        return bits;
    }
}
