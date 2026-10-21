using System.Text;
using Sonda.Storage;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Index;

public sealed class SegmentCore
{
    private int _references = 1;
    private int _closed;

    private SegmentCore(SegmentInfo info, TermsReader terms, PostingsReader postings, StoredFieldsReader stored, Dictionary<int, byte[]> norms, DocValuesData docValues)
    {
        Info = info;
        Terms = terms;
        PostingsFile = postings;
        Stored = stored;
        Norms = norms;
        DocValues = docValues;
    }

    public SegmentInfo Info { get; }

    public string Name => Info.Name;

    public int MaxDoc => Info.DocCount;

    public TermsReader Terms { get; }

    public PostingsReader PostingsFile { get; }

    public StoredFieldsReader Stored { get; }

    public Dictionary<int, byte[]> Norms { get; }

    public DocValuesData DocValues { get; }

    public long SizeInBytes => Terms.SizeInBytes + PostingsFile.SizeInBytes + Stored.SizeInBytes + (Norms.Count * (long)MaxDoc);

    public int References => Volatile.Read(ref _references);

    public static SegmentCore Open(IndexDirectory directory, string name)
    {
        var info = SegmentInfoFormat.Read(directory, name);
        TermsReader? terms = null;
        PostingsReader? postings = null;
        StoredFieldsReader? stored = null;
        try
        {
            terms = new TermsReader(directory, name, info.Fields);
            postings = new PostingsReader(directory, name);
            stored = new StoredFieldsReader(directory, name);
            var norms = NormsFormat.Read(directory, name);
            var docValues = DocValuesFormat.Read(directory, name);
            return new SegmentCore(info, terms, postings, stored, norms, docValues);
        }
        catch
        {
            terms?.Dispose();
            postings?.Dispose();
            stored?.Dispose();
            throw;
        }
    }

    public bool TryIncRef()
    {
        while (true)
        {
            var current = Volatile.Read(ref _references);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void IncRef()
    {
        if (!TryIncRef())
        {
            throw new ObjectDisposedException(Name, "O segmento já foi fechado.");
        }
    }

    public void DecRef()
    {
        if (Interlocked.Decrement(ref _references) == 0 && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Terms.Dispose();
            PostingsFile.Dispose();
            Stored.Dispose();
        }
    }

    public PostingsEnum? Postings(int field, ReadOnlySpan<byte> term, bool needsPositions)
    {
        var fieldTerms = Terms.Field(field);
        if (fieldTerms is null || !fieldTerms.TryGetTerm(term, out var meta))
        {
            return null;
        }

        return PostingsFile.Postings(meta, fieldTerms.Field.IndexOptions, needsPositions);
    }

    public PostingsEnum? Postings(int field, string term, bool needsPositions)
        => Postings(field, Encoding.UTF8.GetBytes(term), needsPositions);

    public IEnumerable<int> DocsForKey(string key)
    {
        var postings = Postings(Schema.Id, key, false);
        if (postings is null)
        {
            yield break;
        }

        while (postings.NextDoc() != PostingsEnum.NoMoreDocs)
        {
            yield return postings.DocId;
        }
    }
}

public sealed class SegmentReader
{
    public SegmentReader(SegmentCore core, FixedBitSet? deleted)
    {
        Core = core;
        Deleted = deleted;
        DelCount = deleted?.Cardinality() ?? 0;
    }

    public SegmentCore Core { get; }

    public FixedBitSet? Deleted { get; }

    public string Name => Core.Name;

    public int MaxDoc => Core.MaxDoc;

    public int DelCount { get; }

    public int NumDocs => MaxDoc - DelCount;

    public bool IsDeleted(int doc) => Deleted is not null && Deleted.Get(doc);

    public FieldTerms? Terms(int field) => Core.Terms.Field(field);

    public byte[]? Norms(int field) => Core.Norms.GetValueOrDefault(field);

    public KeywordDocValues? Keywords(int field) => Core.DocValues.Keywords.GetValueOrDefault(field);

    public long[]? Numerics(int field) => Core.DocValues.Numerics.GetValueOrDefault(field);

    public PostingsEnum? Postings(int field, string term, bool needsPositions) => Core.Postings(field, term, needsPositions);
}
