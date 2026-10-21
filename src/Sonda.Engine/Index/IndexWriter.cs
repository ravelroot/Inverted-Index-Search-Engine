using System.Globalization;
using System.Text;
using Sonda.Analysis;
using Sonda.Storage;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Index;

public sealed record IndexWriterOptions
{
    public int RamBufferMb { get; init; } = 128;

    public int MergeFactor { get; init; } = 10;

    public double DeletesMergeRatio { get; init; } = 0.3;

    public bool StoreBody { get; init; } = true;

    public int MaxIndexedChars { get; init; } = 4_000_000;

    public bool AutoMerge { get; init; } = true;

    public AnalyzerOptions Analyzer { get; init; } = new();
}

public sealed record WriterStatistics(
    int BufferedDocuments,
    long BufferedBytes,
    int FlushedSegments,
    int PendingDeletes,
    bool Merging,
    long CommitGeneration,
    DateTimeOffset? LastCommit);

internal sealed class SegmentState(SegmentCore core, FixedBitSet deleted, int delCount, long delGen, bool dirty, bool committed)
{
    public Lock Gate { get; } = new();

    public SegmentCore Core { get; } = core;

    public FixedBitSet Deleted { get; } = deleted;

    public FixedBitSet? CommittedDeleted { get; set; }

    public int DelCount { get; set; } = delCount;

    public int CommittedDelCount { get; set; }

    public long DelGen { get; set; } = delGen;

    public bool Dirty { get; set; } = dirty;

    public bool Committed { get; set; } = committed;

    public bool Merging { get; set; }

    public int LiveDocs => Core.MaxDoc - DelCount;
}

public sealed class IndexWriter : IDisposable
{
    private readonly IndexDirectory _directory;
    private readonly IDisposable _writeLock;
    private readonly ReaderWriterLockSlim _swap = new(LockRecursionPolicy.NoRecursion);
    private readonly Lock _flushGate = new();
    private readonly Lock _commitGate = new();
    private readonly SemaphoreSlim _mergeGate = new(1, 1);
    private readonly List<SegmentState> _segments = [];
    private readonly List<MemorySegment> _frozen = [];
    private readonly HashSet<string> _mergingOutputs = new(StringComparer.Ordinal);
    private readonly long _ramLimit;
    private MemorySegment _active;
    private CommitPoint _commit;
    private long _counter;
    private int _disposed;
    private int _changes;

    private IndexWriter(IndexDirectory directory, IDisposable writeLock, IndexWriterOptions options, CommitPoint commit)
    {
        _directory = directory;
        _writeLock = writeLock;
        Options = options;
        _commit = commit;
        _counter = commit.Counter;
        _ramLimit = Math.Max(4, options.RamBufferMb) * 1024L * 1024L;
        Analyzer = new TextAnalyzer(options.Analyzer);
        Builder = new DocumentBuilder(Analyzer, options.StoreBody, options.MaxIndexedChars);
        _active = new MemorySegment(NextSegmentName());
    }

    public IndexWriterOptions Options { get; }

    public TextAnalyzer Analyzer { get; }

    public DocumentBuilder Builder { get; }

    public IndexDirectory Directory => _directory;

    public CommitPoint LastCommit => Volatile.Read(ref _commit);

    public event Action<CommitPoint>? Committed;

    public static IndexWriter Open(string path, IndexWriterOptions? options = null)
    {
        options ??= new IndexWriterOptions();
        var directory = new IndexDirectory(path);
        var writeLock = directory.ObtainWriteLock();
        try
        {
            var commit = CommitFormat.ReadLatest(directory);
            var writer = new IndexWriter(directory, writeLock, options, commit);
            foreach (var entry in commit.Segments)
            {
                var core = SegmentCore.Open(directory, entry.Name);
                var deleted = entry.DelGen > 0
                    ? LiveDocsFormat.Read(directory, entry.Name, entry.DelGen)
                    : new FixedBitSet(core.MaxDoc);
                var state = new SegmentState(core, deleted, deleted.Cardinality(), entry.DelGen, false, true)
                {
                    CommittedDeleted = entry.DelGen > 0 ? deleted.Clone() : null,
                };
                state.CommittedDelCount = state.DelCount;
                writer._segments.Add(state);
            }

            writer.DeleteUnreferencedFiles();
            return writer;
        }
        catch
        {
            writeLock.Dispose();
            throw;
        }
    }

    public void Add(SourceDocument document)
    {
        var analyzed = Builder.Build(document);
        _swap.EnterReadLock();
        try
        {
            _active.Add(analyzed);
        }
        finally
        {
            _swap.ExitReadLock();
        }

        Interlocked.Increment(ref _changes);
        FlushIfNeeded();
    }

    public void Update(SourceDocument document)
    {
        var analyzed = Builder.Build(document);
        _swap.EnterReadLock();
        try
        {
            DeleteKeyLocked(document.Key);
            _active.Add(analyzed);
        }
        finally
        {
            _swap.ExitReadLock();
        }

        Interlocked.Increment(ref _changes);
        FlushIfNeeded();
    }

    public int Delete(string key)
    {
        int deleted;
        _swap.EnterReadLock();
        try
        {
            deleted = DeleteKeyLocked(key);
        }
        finally
        {
            _swap.ExitReadLock();
        }

        if (deleted > 0)
        {
            Interlocked.Increment(ref _changes);
        }

        return deleted;
    }

    public int DeleteByKeyPrefix(string prefix)
    {
        var deleted = 0;
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        _swap.EnterReadLock();
        try
        {
            foreach (var state in _segments)
            {
                var terms = state.Core.Terms.Field(Schema.Id);
                if (terms is null)
                {
                    continue;
                }

                var iterator = terms.SeekCeil(prefixBytes);
                while (iterator.MoveNext() && iterator.TermBytes.StartsWith(prefixBytes))
                {
                    var postings = state.Core.PostingsFile.Postings(iterator.Meta, IndexOptions.Docs, false);
                    while (postings.NextDoc() != PostingsEnum.NoMoreDocs)
                    {
                        if (MarkDeleted(state, postings.DocId))
                        {
                            deleted++;
                        }
                    }
                }
            }

            foreach (var frozen in _frozen)
            {
                deleted += frozen.DeleteByPrefix(prefix);
            }

            deleted += _active.DeleteByPrefix(prefix);
        }
        finally
        {
            _swap.ExitReadLock();
        }

        if (deleted > 0)
        {
            Interlocked.Increment(ref _changes);
        }

        return deleted;
    }

    private int DeleteKeyLocked(string key)
    {
        var count = 0;
        foreach (var state in _segments)
        {
            foreach (var doc in state.Core.DocsForKey(key))
            {
                if (MarkDeleted(state, doc))
                {
                    count++;
                }
            }
        }

        foreach (var frozen in _frozen)
        {
            if (frozen.DeleteKey(key))
            {
                count++;
            }
        }

        if (_active.DeleteKey(key))
        {
            count++;
        }

        return count;
    }

    private static bool MarkDeleted(SegmentState state, int doc)
    {
        lock (state.Gate)
        {
            if (!state.Deleted.Set(doc))
            {
                return false;
            }

            state.DelCount++;
            state.Dirty = true;
            return true;
        }
    }

    private void FlushIfNeeded()
    {
        if (_active.RamBytes >= _ramLimit && _flushGate.TryEnter())
        {
            try
            {
                FlushLocked();
            }
            finally
            {
                _flushGate.Exit();
            }
        }
    }

    public void Flush()
    {
        lock (_flushGate)
        {
            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        MemorySegment frozen;
        _swap.EnterWriteLock();
        try
        {
            if (_active.DocCount == 0)
            {
                return;
            }

            frozen = _active;
            _frozen.Add(frozen);
            _active = new MemorySegment(NextSegmentName());
        }
        finally
        {
            _swap.ExitWriteLock();
        }

        SegmentCore core;
        try
        {
            SegmentWriter.Write(_directory, frozen);
            core = SegmentCore.Open(_directory, frozen.Name);
        }
        catch
        {
            _swap.EnterWriteLock();
            try
            {
                _frozen.Remove(frozen);
            }
            finally
            {
                _swap.ExitWriteLock();
            }

            throw;
        }

        _swap.EnterWriteLock();
        try
        {
            var deleted = frozen.BuildDeleted();
            var count = deleted.Cardinality();
            _segments.Add(new SegmentState(core, deleted, count, 0, count > 0, false));
            _frozen.Remove(frozen);
        }
        finally
        {
            _swap.ExitWriteLock();
        }
    }

    public bool HasUncommittedChanges => Volatile.Read(ref _changes) > 0;

    public CommitPoint Commit(IReadOnlyDictionary<string, string>? userData = null)
    {
        CommitPoint commit;
        var dropped = new List<SegmentState>();
        lock (_commitGate)
        {
            Flush();
            _swap.EnterWriteLock();
            try
            {
                foreach (var state in _segments)
                {
                    if (state.Dirty)
                    {
                        var generation = state.DelGen + 1;
                        LiveDocsFormat.Write(_directory, state.Core.Name, generation, state.Deleted);
                        state.DelGen = generation;
                        state.Dirty = false;
                    }

                    state.CommittedDeleted = state.DelCount > 0 ? state.Deleted.Clone() : null;
                    state.CommittedDelCount = state.DelCount;
                    state.Committed = true;
                }

                foreach (var state in _segments.Where(s => s.DelCount >= s.Core.MaxDoc && !s.Merging).ToList())
                {
                    _segments.Remove(state);
                    dropped.Add(state);
                }

                var data = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, value) in _commit.UserData)
                {
                    data[key] = value;
                }

                if (userData is not null)
                {
                    foreach (var (key, value) in userData)
                    {
                        data[key] = value;
                    }
                }

                data["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                var entries = _segments
                    .Select(s => new SegmentCommitEntry(s.Core.Name, s.Core.MaxDoc, s.DelCount, s.DelGen))
                    .ToList();
                commit = new CommitPoint(_commit.Generation + 1, _commit.Version + 1, Interlocked.Read(ref _counter), entries, data);
                CommitFormat.Write(_directory, commit);
                Volatile.Write(ref _commit, commit);
                Interlocked.Exchange(ref _changes, 0);
            }
            finally
            {
                _swap.ExitWriteLock();
            }

            foreach (var state in dropped)
            {
                state.Core.DecRef();
            }

            DeleteUnreferencedFiles();
        }

        Committed?.Invoke(commit);
        return commit;
    }

    public IndexSnapshot AcquireSnapshot()
    {
        using var commitScope = _commitGate.EnterScope();
        _swap.EnterReadLock();
        try
        {
            var readers = new List<SegmentReader>();
            foreach (var state in _segments)
            {
                if (!state.Committed)
                {
                    continue;
                }

                state.Core.IncRef();
                readers.Add(new SegmentReader(state.Core, state.CommittedDeleted));
            }

            return new IndexSnapshot(_commit, readers);
        }
        finally
        {
            _swap.ExitReadLock();
        }
    }

    public async Task<int> MaybeMergeAsync(CancellationToken cancellationToken = default)
    {
        if (!await _mergeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var merges = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var selected = SelectMerge(force: false, maxSegments: 0);
                if (selected is null)
                {
                    break;
                }

                await Task.Run(() => RunMerge(selected, cancellationToken), cancellationToken).ConfigureAwait(false);
                merges++;
            }
        }
        finally
        {
            _mergeGate.Release();
        }

        return merges;
    }

    public async Task<int> ForceMergeAsync(int maxSegments = 1, CancellationToken cancellationToken = default)
    {
        maxSegments = Math.Max(1, maxSegments);
        Commit();
        await _mergeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var merges = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var selected = SelectMerge(force: true, maxSegments);
                if (selected is null)
                {
                    break;
                }

                await Task.Run(() => RunMerge(selected, cancellationToken), cancellationToken).ConfigureAwait(false);
                merges++;
            }
        }
        finally
        {
            _mergeGate.Release();
        }

        return merges;
    }

    private List<SegmentState>? SelectMerge(bool force, int maxSegments)
    {
        _swap.EnterWriteLock();
        try
        {
            var candidates = _segments.Where(s => s.Committed && !s.Merging).OrderBy(s => s.LiveDocs).ToList();
            List<SegmentState>? selected = null;
            if (force)
            {
                if (candidates.Count > maxSegments)
                {
                    selected = candidates.Take(Math.Min(candidates.Count, Math.Max(2, candidates.Count - maxSegments + 1))).ToList();
                }
                else if (candidates.Count == 1 && candidates[0].DelCount > 0)
                {
                    selected = [candidates[0]];
                }
            }
            else if (candidates.Count >= Math.Max(2, Options.MergeFactor))
            {
                selected = candidates.Take(Options.MergeFactor).ToList();
            }
            else
            {
                var heavy = candidates.FirstOrDefault(s => s.Core.MaxDoc > 0 && s.DelCount > 0
                    && (double)s.DelCount / s.Core.MaxDoc >= Options.DeletesMergeRatio);
                if (heavy is not null)
                {
                    selected = [heavy];
                }
            }

            if (selected is null || selected.Count == 0)
            {
                return null;
            }

            foreach (var state in selected)
            {
                state.Merging = true;
            }

            return selected;
        }
        finally
        {
            _swap.ExitWriteLock();
        }
    }

    private void RunMerge(List<SegmentState> selected, CancellationToken cancellationToken)
    {
        var snapshots = new List<FixedBitSet>(selected.Count);
        var readers = new List<SegmentReader>(selected.Count);
        foreach (var state in selected)
        {
            FixedBitSet snapshot;
            lock (state.Gate)
            {
                snapshot = state.Deleted.Clone();
            }

            snapshots.Add(snapshot);
            readers.Add(new SegmentReader(state.Core, snapshot));
        }

        var name = NextSegmentName();
        lock (_mergingOutputs)
        {
            _mergingOutputs.Add(name);
        }

        try
        {
            if (readers.Sum(r => r.NumDocs) == 0)
            {
                _swap.EnterWriteLock();
                try
                {
                    foreach (var state in selected)
                    {
                        lock (state.Gate)
                        {
                            state.DelCount = state.Core.MaxDoc;
                            state.Dirty = true;
                        }

                        state.Merging = false;
                    }
                }
                finally
                {
                    _swap.ExitWriteLock();
                }

                Commit();
                return;
            }

            var result = SegmentMerger.Merge(_directory, name, readers, cancellationToken);
            var core = SegmentCore.Open(_directory, name);
            using var commitScope = _commitGate.EnterScope();
            _swap.EnterWriteLock();
            try
            {
                var deleted = new FixedBitSet(result.Info.DocCount);
                var newDeletes = 0;
                for (var i = 0; i < selected.Count; i++)
                {
                    var state = selected[i];
                    var snapshot = snapshots[i];
                    var map = result.DocMaps[i];
                    for (var d = 0; d < state.Core.MaxDoc; d++)
                    {
                        if (state.Deleted.Get(d) && !snapshot.Get(d) && map[d] >= 0 && deleted.Set(map[d]))
                        {
                            newDeletes++;
                        }
                    }

                    _segments.Remove(state);
                }

                _segments.Add(new SegmentState(core, deleted, newDeletes, 0, newDeletes > 0, false));
                Interlocked.Increment(ref _changes);
            }
            finally
            {
                _swap.ExitWriteLock();
            }

            foreach (var state in selected)
            {
                state.Core.DecRef();
            }

            Commit();
        }
        catch
        {
            _swap.EnterWriteLock();
            try
            {
                foreach (var state in selected)
                {
                    state.Merging = false;
                }
            }
            finally
            {
                _swap.ExitWriteLock();
            }

            throw;
        }
        finally
        {
            lock (_mergingOutputs)
            {
                _mergingOutputs.Remove(name);
            }
        }
    }

    public WriterStatistics GetStatistics()
    {
        _swap.EnterReadLock();
        try
        {
            var buffered = _active.DocCount - _active.DeletedCount + _frozen.Sum(f => f.DocCount - f.DeletedCount);
            var bytes = _active.RamBytes + _frozen.Sum(f => f.RamBytes);
            var pending = _segments.Sum(s => s.DelCount - s.CommittedDelCount);
            var flushed = _segments.Count(s => !s.Committed);
            DateTimeOffset? last = _commit.UserData.TryGetValue("timestamp", out var raw)
                && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
            bool merging;
            lock (_mergingOutputs)
            {
                merging = _mergingOutputs.Count > 0;
            }

            return new WriterStatistics(buffered, bytes, flushed, pending, merging, _commit.Generation, last);
        }
        finally
        {
            _swap.ExitReadLock();
        }
    }

    private string NextSegmentName()
    {
        var value = Interlocked.Increment(ref _counter);
        return "_" + ToBase36(value);
    }

    private static string ToBase36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0)
        {
            return "0";
        }

        Span<char> buffer = stackalloc char[16];
        var position = buffer.Length;
        while (value > 0)
        {
            buffer[--position] = digits[(int)(value % 36)];
            value /= 36;
        }

        return new string(buffer[position..]);
    }

    private void DeleteUnreferencedFiles()
    {
        HashSet<string> referenced;
        HashSet<string> liveSegments;
        _swap.EnterReadLock();
        try
        {
            referenced = new HashSet<string>(_commit.ReferencedFiles(), StringComparer.Ordinal);
            liveSegments = new HashSet<string>(_segments.Select(s => s.Core.Name), StringComparer.Ordinal)
            {
                _active.Name,
            };
            foreach (var frozen in _frozen)
            {
                liveSegments.Add(frozen.Name);
            }

            foreach (var state in _segments)
            {
                if (state.DelGen > 0)
                {
                    referenced.Add(LiveDocsFormat.FileName(state.Core.Name, state.DelGen));
                }
            }
        }
        finally
        {
            _swap.ExitReadLock();
        }

        lock (_mergingOutputs)
        {
            liveSegments.UnionWith(_mergingOutputs);
        }

        foreach (var file in _directory.ListAll())
        {
            if (file == IndexDirectory.LockFileName || referenced.Contains(file))
            {
                continue;
            }

            if (file.StartsWith(CommitPoint.Prefix, StringComparison.Ordinal) || file.StartsWith("pending_" + CommitPoint.Prefix, StringComparison.Ordinal))
            {
                _directory.DeleteFile(file);
                continue;
            }

            if (!file.StartsWith('_'))
            {
                continue;
            }

            var end = file.IndexOfAny(['.', '_'], 1);
            var segment = end < 0 ? file : file[..end];
            var isLiveDocs = file.EndsWith("." + LiveDocsFormat.Extension, StringComparison.Ordinal);
            if (!liveSegments.Contains(segment) || isLiveDocs)
            {
                _directory.DeleteFile(file);
            }
        }

        _directory.RetryPendingDeletes();
    }

    public void Rollback()
    {
        _swap.EnterWriteLock();
        try
        {
            _active = new MemorySegment(NextSegmentName());
        }
        finally
        {
            _swap.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _mergeGate.Wait(TimeSpan.FromMinutes(5));
            if (HasUncommittedChanges || _active.DocCount > 0)
            {
                Commit();
            }
        }
        finally
        {
            foreach (var state in _segments)
            {
                state.Core.DecRef();
            }

            _segments.Clear();
            _writeLock.Dispose();
            _swap.Dispose();
        }
    }
}

public sealed class IndexSnapshot : IDisposable
{
    private int _disposed;

    internal IndexSnapshot(CommitPoint commit, IReadOnlyList<SegmentReader> segments)
    {
        Commit = commit;
        Segments = segments;
    }

    public CommitPoint Commit { get; }

    public IReadOnlyList<SegmentReader> Segments { get; }

    public static IndexSnapshot OpenReadOnly(string path)
    {
        var directory = new IndexDirectory(path);
        for (var attempt = 0; ; attempt++)
        {
            var commit = CommitFormat.ReadLatest(directory);
            var readers = new List<SegmentReader>();
            try
            {
                foreach (var entry in commit.Segments)
                {
                    var core = SegmentCore.Open(directory, entry.Name);
                    var deleted = entry.DelGen > 0 ? LiveDocsFormat.Read(directory, entry.Name, entry.DelGen) : null;
                    readers.Add(new SegmentReader(core, deleted));
                }

                return new IndexSnapshot(commit, readers);
            }
            catch (IOException) when (attempt < 3)
            {
                foreach (var reader in readers)
                {
                    reader.Core.DecRef();
                }

                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var segment in Segments)
        {
            segment.Core.DecRef();
        }
    }
}
