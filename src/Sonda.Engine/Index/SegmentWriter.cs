using System.Text;
using Sonda.Storage;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Index;

internal static class SegmentWriter
{
    private sealed class Utf8Comparer : IComparer<byte[]>
    {
        public static readonly Utf8Comparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }

    public static SegmentInfo Write(IndexDirectory directory, MemorySegment segment)
    {
        var name = segment.Name;
        var docCount = segment.DocCount;
        try
        {
            var documents = new BufferedDocument[docCount];
            for (var i = 0; i < docCount; i++)
            {
                documents[i] = segment.Document(i);
            }

            using (var stored = new StoredFieldsWriter(directory, name))
            {
                foreach (var document in documents)
                {
                    stored.AddDocument(document.Stored);
                }

                stored.Finish();
            }

            var norms = new Dictionary<int, byte[]>();
            foreach (var field in Schema.NormFields)
            {
                var values = new byte[docCount];
                for (var d = 0; d < docCount; d++)
                {
                    values[d] = NormEncoding.Encode(documents[d].Lengths[field]);
                }

                norms[field] = values;
            }

            NormsFormat.Write(directory, name, docCount, norms);
            DocValuesFormat.Write(directory, name, docCount, BuildDocValues(documents));

            using (var postings = new PostingsWriter(directory, name))
            using (var terms = new TermsWriter(directory, name))
            {
                foreach (var fieldNumber in Schema.IndexedFields)
                {
                    var field = Schema.Fields[fieldNumber]!;
                    var entries = segment.Terms(fieldNumber)
                        .Select(kv => (Bytes: Encoding.UTF8.GetBytes(kv.Key), Buffer: kv.Value))
                        .ToArray();
                    var keys = entries.Select(e => e.Bytes).ToArray();
                    Array.Sort(keys, entries, Utf8Comparer.Instance);

                    terms.StartField(field);
                    long sumDocFreq = 0;
                    long sumTotalTermFreq = 0;
                    var fieldNorms = norms.GetValueOrDefault(fieldNumber);
                    foreach (var (bytes, buffer) in entries)
                    {
                        buffer.WriteTo(postings, field, fieldNorms);
                        var meta = postings.FinishTerm();
                        terms.AddTerm(bytes, meta);
                        sumDocFreq += meta.DocFreq;
                        sumTotalTermFreq += meta.TotalTermFreq;
                    }

                    var docsWithField = 0;
                    for (var d = 0; d < docCount; d++)
                    {
                        if (documents[d].Lengths[fieldNumber] > 0)
                        {
                            docsWithField++;
                        }
                    }

                    terms.FinishField(new FieldTermStats(entries.Length, sumDocFreq, sumTotalTermFreq, docsWithField));
                }

                postings.Finish();
                terms.Finish();
            }

            var info = new SegmentInfo(name, docCount, "flush", DateTimeOffset.UtcNow, Schema.Fields, SegmentInfoFormat.FilesFor(name));
            SegmentInfoFormat.Write(directory, info);
            return info;
        }
        catch
        {
            DeleteSegmentFiles(directory, name);
            throw;
        }
    }

    public static void DeleteSegmentFiles(IndexDirectory directory, string name)
    {
        foreach (var file in SegmentInfoFormat.FilesFor(name))
        {
            directory.DeleteFile(file);
        }
    }

    private static DocValuesData BuildDocValues(BufferedDocument[] documents)
    {
        var data = new DocValuesData();
        foreach (var field in Schema.KeywordFields)
        {
            var distinct = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var document in documents)
            {
                if (!string.IsNullOrEmpty(document.Keywords[field]))
                {
                    distinct.Add(document.Keywords[field]);
                }
            }

            var values = distinct.ToArray();
            var lookup = new Dictionary<string, int>(values.Length, StringComparer.Ordinal);
            for (var i = 0; i < values.Length; i++)
            {
                lookup[values[i]] = i;
            }

            var ordinals = new int[documents.Length];
            for (var d = 0; d < documents.Length; d++)
            {
                var value = documents[d].Keywords[field];
                ordinals[d] = string.IsNullOrEmpty(value) ? -1 : lookup[value];
            }

            data.Keywords[field] = new KeywordDocValues(values, ordinals);
        }

        foreach (var field in new[] { Schema.Modified, Schema.Size })
        {
            var values = new long[documents.Length];
            for (var d = 0; d < documents.Length; d++)
            {
                values[d] = documents[d].Numerics[field];
            }

            data.Numerics[field] = values;
        }

        return data;
    }
}
