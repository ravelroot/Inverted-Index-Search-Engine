using Sonda.Storage;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Index;

internal sealed record MergeResult(SegmentInfo Info, int[][] DocMaps);

internal static class SegmentMerger
{
    private sealed class TermKeyComparer : IComparer<(byte[] Term, int Source)>
    {
        public static readonly TermKeyComparer Instance = new();

        public int Compare((byte[] Term, int Source) x, (byte[] Term, int Source) y)
        {
            var comparison = x.Term.AsSpan().SequenceCompareTo(y.Term);
            return comparison != 0 ? comparison : x.Source.CompareTo(y.Source);
        }
    }

    public static MergeResult Merge(IndexDirectory directory, string name, IReadOnlyList<SegmentReader> sources, CancellationToken cancellationToken)
    {
        var docMaps = new int[sources.Count][];
        var docCount = 0;
        for (var s = 0; s < sources.Count; s++)
        {
            var source = sources[s];
            var map = new int[source.MaxDoc];
            for (var d = 0; d < source.MaxDoc; d++)
            {
                map[d] = source.IsDeleted(d) ? -1 : docCount++;
            }

            docMaps[s] = map;
        }

        try
        {
            using (var stored = new StoredFieldsWriter(directory, name))
            {
                for (var s = 0; s < sources.Count; s++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sources[s];
                    for (var d = 0; d < source.MaxDoc; d++)
                    {
                        if (docMaps[s][d] >= 0)
                        {
                            stored.AddDocument(source.Core.Stored.Document(d));
                        }
                    }
                }

                stored.Finish();
            }

            var norms = new Dictionary<int, byte[]>();
            foreach (var field in Schema.NormFields)
            {
                var merged = new byte[docCount];
                for (var s = 0; s < sources.Count; s++)
                {
                    var values = sources[s].Norms(field);
                    var map = docMaps[s];
                    for (var d = 0; d < map.Length; d++)
                    {
                        if (map[d] >= 0)
                        {
                            merged[map[d]] = values?[d] ?? 0;
                        }
                    }
                }

                norms[field] = merged;
            }

            NormsFormat.Write(directory, name, docCount, norms);
            var docValues = MergeDocValues(sources, docMaps, docCount);
            DocValuesFormat.Write(directory, name, docCount, docValues);

            using (var postings = new PostingsWriter(directory, name))
            using (var terms = new TermsWriter(directory, name))
            {
                foreach (var fieldNumber in Schema.IndexedFields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MergeField(fieldNumber, sources, docMaps, norms.GetValueOrDefault(fieldNumber), docValues, docCount, postings, terms);
                }

                postings.Finish();
                terms.Finish();
            }

            var info = new SegmentInfo(name, docCount, "merge", DateTimeOffset.UtcNow, Schema.Fields, SegmentInfoFormat.FilesFor(name));
            SegmentInfoFormat.Write(directory, info);
            return new MergeResult(info, docMaps);
        }
        catch
        {
            SegmentWriter.DeleteSegmentFiles(directory, name);
            throw;
        }
    }

    private static void MergeField(
        int fieldNumber,
        IReadOnlyList<SegmentReader> sources,
        int[][] docMaps,
        byte[]? norms,
        DocValuesData docValues,
        int docCount,
        PostingsWriter postings,
        TermsWriter terms)
    {
        var field = Schema.Fields[fieldNumber]!;
        terms.StartField(field);
        var enums = new TermsEnum?[sources.Count];
        var queue = new PriorityQueue<int, (byte[] Term, int Source)>(TermKeyComparer.Instance);
        for (var s = 0; s < sources.Count; s++)
        {
            var fieldTerms = sources[s].Terms(fieldNumber);
            if (fieldTerms is null)
            {
                continue;
            }

            var iterator = fieldTerms.GetEnumerator();
            if (iterator.MoveNext())
            {
                enums[s] = iterator;
                queue.Enqueue(s, (iterator.TermCopy(), s));
            }
        }

        long termCount = 0;
        long sumDocFreq = 0;
        long sumTotalTermFreq = 0;
        var group = new List<int>(sources.Count);
        while (queue.TryDequeue(out var first, out var key))
        {
            group.Clear();
            group.Add(first);
            while (queue.TryPeek(out var next, out var nextKey) && nextKey.Term.AsSpan().SequenceEqual(key.Term))
            {
                queue.Dequeue();
                group.Add(next);
            }

            postings.StartTerm(field.IndexOptions);
            foreach (var s in group)
            {
                var iterator = enums[s]!;
                var meta = iterator.Meta;
                var source = postings;
                var enumerator = sources[s].Core.PostingsFile.Postings(meta, field.IndexOptions, field.HasPositions);
                var map = docMaps[s];
                while (enumerator.NextDoc() != PostingsEnum.NoMoreDocs)
                {
                    var mapped = map[enumerator.DocId];
                    if (mapped < 0)
                    {
                        continue;
                    }

                    source.StartDoc(mapped, enumerator.Freq, norms is null ? (byte)1 : norms[mapped]);
                    if (field.HasPositions)
                    {
                        for (var p = 0; p < enumerator.Freq; p++)
                        {
                            source.AddPosition(enumerator.NextPosition());
                        }
                    }

                    source.FinishDoc();
                }
            }

            var merged = postings.FinishTerm();
            if (merged.DocFreq > 0)
            {
                terms.AddTerm(key.Term, merged);
                termCount++;
                sumDocFreq += merged.DocFreq;
                sumTotalTermFreq += merged.TotalTermFreq;
            }

            foreach (var s in group)
            {
                var iterator = enums[s]!;
                if (iterator.MoveNext())
                {
                    queue.Enqueue(s, (iterator.TermCopy(), s));
                }
            }
        }

        var docsWithField = 0;
        if (norms is not null)
        {
            foreach (var value in norms)
            {
                if (value != 0)
                {
                    docsWithField++;
                }
            }
        }
        else if (docValues.Keywords.TryGetValue(fieldNumber, out var keywords))
        {
            foreach (var ordinal in keywords.Ordinals)
            {
                if (ordinal >= 0)
                {
                    docsWithField++;
                }
            }
        }
        else
        {
            docsWithField = docCount;
        }

        terms.FinishField(new FieldTermStats(termCount, sumDocFreq, sumTotalTermFreq, docsWithField));
    }

    private static DocValuesData MergeDocValues(IReadOnlyList<SegmentReader> sources, int[][] docMaps, int docCount)
    {
        var data = new DocValuesData();
        foreach (var field in Schema.KeywordFields)
        {
            var distinct = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var values = source.Keywords(field);
                if (values is not null)
                {
                    distinct.UnionWith(values.Values);
                }
            }

            var merged = distinct.ToArray();
            var lookup = new Dictionary<string, int>(merged.Length, StringComparer.Ordinal);
            for (var i = 0; i < merged.Length; i++)
            {
                lookup[merged[i]] = i;
            }

            var ordinals = new int[docCount];
            for (var s = 0; s < sources.Count; s++)
            {
                var values = sources[s].Keywords(field);
                var map = docMaps[s];
                for (var d = 0; d < map.Length; d++)
                {
                    if (map[d] < 0)
                    {
                        continue;
                    }

                    var value = values?.Get(d);
                    ordinals[map[d]] = value is null ? -1 : lookup[value];
                }
            }

            data.Keywords[field] = new KeywordDocValues(merged, ordinals);
        }

        foreach (var field in new[] { Schema.Modified, Schema.Size })
        {
            var merged = new long[docCount];
            for (var s = 0; s < sources.Count; s++)
            {
                var values = sources[s].Numerics(field);
                var map = docMaps[s];
                for (var d = 0; d < map.Length; d++)
                {
                    if (map[d] >= 0 && values is not null)
                    {
                        merged[map[d]] = values[d];
                    }
                }
            }

            data.Numerics[field] = merged;
        }

        return data;
    }
}
