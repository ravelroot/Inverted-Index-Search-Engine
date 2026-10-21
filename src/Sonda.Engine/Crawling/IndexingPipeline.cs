using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Sonda.Analysis.Extraction;
using Sonda.Engine.Index;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Crawling;

public enum IndexingMode
{
    Incremental,
    Full,
}

public sealed record IndexingProgress(
    string Source,
    string Phase,
    long Discovered,
    long Processed,
    long Added,
    long Updated,
    long Unchanged,
    long Deleted,
    long Failed,
    long Bytes,
    double ElapsedSeconds,
    string? CurrentFile)
{
    public double FilesPerSecond => ElapsedSeconds <= 0 ? 0 : Processed / ElapsedSeconds;

    public double MegabytesPerSecond => ElapsedSeconds <= 0 ? 0 : Bytes / 1024d / 1024d / ElapsedSeconds;
}

public sealed record IndexingError(string Path, string Message);

public sealed record IndexingRunResult(
    string Id,
    string Source,
    IndexingMode Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long Discovered,
    long Added,
    long Updated,
    long Unchanged,
    long Deleted,
    long Failed,
    long Bytes,
    string Status,
    string? Message,
    IReadOnlyList<IndexingError> Errors)
{
    public double ElapsedSeconds => (FinishedAt - StartedAt).TotalSeconds;
}

public sealed record IndexingOptions
{
    public int Parallelism { get; init; }

    public long MaxFileBytes { get; init; } = 32L * 1024 * 1024;

    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(300);
}

public sealed class IndexingPipeline(IndexWriter writer, IndexingOptions? options = null)
{
    private readonly IndexingOptions _options = options ?? new IndexingOptions();

    private sealed class Counters
    {
        public long Discovered;
        public long Processed;
        public long Added;
        public long Updated;
        public long Unchanged;
        public long Deleted;
        public long Failed;
        public long Bytes;
        public string? Current;
        public string Phase = "Varrendo";
    }

    public async Task<IndexingRunResult> RunAsync(
        SourceDefinition source,
        IndexingMode mode,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var counters = new Counters();
        var errors = new ConcurrentQueue<IndexingError>();
        var root = System.IO.Path.GetFullPath(source.Path);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"A pasta da fonte \"{source.Name}\" não existe: {source.Path}");
        }

        IndexingProgress Snapshot() => new(
            source.Name,
            Volatile.Read(ref counters.Phase),
            Interlocked.Read(ref counters.Discovered),
            Interlocked.Read(ref counters.Processed),
            Interlocked.Read(ref counters.Added),
            Interlocked.Read(ref counters.Updated),
            Interlocked.Read(ref counters.Unchanged),
            Interlocked.Read(ref counters.Deleted),
            Interlocked.Read(ref counters.Failed),
            Interlocked.Read(ref counters.Bytes),
            stopwatch.Elapsed.TotalSeconds,
            Volatile.Read(ref counters.Current));

        using var reporterCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reporter = progress is null ? Task.CompletedTask : Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_options.ProgressInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(reporterCancellation.Token).ConfigureAwait(false))
                {
                    progress.Report(Snapshot());
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        var status = "concluida";
        string? message = null;
        try
        {
            var existing = LoadExisting(source);
            var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var matcher = new GlobMatcher(source.Include, source.Exclude);
            var channel = Channel.CreateBounded<FileInfo>(new BoundedChannelOptions(4096)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (var file in Enumerate(root, matcher, cancellationToken))
                    {
                        Interlocked.Increment(ref counters.Discovered);
                        await channel.Writer.WriteAsync(file, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, cancellationToken);

            Volatile.Write(ref counters.Phase, "Indexando");
            var parallelism = _options.Parallelism > 0 ? _options.Parallelism : Environment.ProcessorCount;
            await Parallel.ForEachAsync(
                channel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (file, token) =>
                {
                    var relative = System.IO.Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                    var key = SourceDocument.MakeKey(source.Name, relative);
                    seen.TryAdd(key, 0);
                    Volatile.Write(ref counters.Current, relative);
                    var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                    var modifiedMs = modified.ToUnixTimeMilliseconds();
                    var known = existing.TryGetValue(key, out var previous);
                    try
                    {
                        if (known && mode == IndexingMode.Incremental && previous.Modified == modifiedMs && previous.Size == file.Length)
                        {
                            Interlocked.Increment(ref counters.Unchanged);
                            return;
                        }

                        if (file.Length > _options.MaxFileBytes)
                        {
                            Interlocked.Increment(ref counters.Failed);
                            errors.Enqueue(new IndexingError(relative, $"Arquivo maior que o limite de {_options.MaxFileBytes / 1024 / 1024} MB."));
                            return;
                        }

                        var bytes = await File.ReadAllBytesAsync(file.FullName, token).ConfigureAwait(false);
                        var text = TextDecoder.Decode(bytes);
                        var content = ContentExtractor.Extract(file.Name, text);
                        var document = new SourceDocument
                        {
                            Key = key,
                            Source = source.Name,
                            Path = relative,
                            Title = content.Title,
                            Body = content.Body,
                            Description = content.Description,
                            Extension = file.Extension,
                            LanguageHint = content.LanguageHint,
                            Size = file.Length,
                            Modified = DateTimeOffset.FromUnixTimeMilliseconds(modifiedMs),
                        };

                        if (known)
                        {
                            writer.Update(document);
                            Interlocked.Increment(ref counters.Updated);
                        }
                        else
                        {
                            writer.Add(document);
                            Interlocked.Increment(ref counters.Added);
                        }

                        Interlocked.Add(ref counters.Bytes, bytes.Length);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidDataException)
                    {
                        Interlocked.Increment(ref counters.Failed);
                        if (errors.Count < 200)
                        {
                            errors.Enqueue(new IndexingError(relative, ex.Message));
                        }
                    }
                    finally
                    {
                        Interlocked.Increment(ref counters.Processed);
                    }
                }).ConfigureAwait(false);

            await producer.ConfigureAwait(false);

            Volatile.Write(ref counters.Phase, "Removendo ausentes");
            foreach (var key in existing.Keys)
            {
                if (!seen.ContainsKey(key) && writer.Delete(key) > 0)
                {
                    Interlocked.Increment(ref counters.Deleted);
                }
            }

            Volatile.Write(ref counters.Phase, "Gravando");
            writer.Commit(new Dictionary<string, string> { ["ultima-fonte"] = source.Name });
        }
        catch (OperationCanceledException)
        {
            status = "cancelada";
            message = "Indexação cancelada. O que já foi processado será gravado.";
            writer.Commit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status = "falhou";
            message = ex.Message;
        }
        finally
        {
            Volatile.Write(ref counters.Phase, status == "concluida" ? "Concluída" : status == "cancelada" ? "Cancelada" : "Falhou");
            await reporterCancellation.CancelAsync().ConfigureAwait(false);
            await reporter.ConfigureAwait(false);
            progress?.Report(Snapshot());
        }

        var final = Snapshot();
        return new IndexingRunResult(
            id,
            source.Name,
            mode,
            started,
            DateTimeOffset.UtcNow,
            final.Discovered,
            final.Added,
            final.Updated,
            final.Unchanged,
            final.Deleted,
            final.Failed,
            final.Bytes,
            status,
            message,
            errors.Take(50).ToList());
    }

    private Dictionary<string, (long Modified, long Size)> LoadExisting(SourceDefinition source)
    {
        var result = new Dictionary<string, (long Modified, long Size)>(StringComparer.Ordinal);
        var prefix = Encoding.UTF8.GetBytes(source.KeyPrefix);
        using var snapshot = writer.AcquireSnapshot();
        foreach (var reader in snapshot.Segments)
        {
            var terms = reader.Terms(Schema.Id);
            if (terms is null)
            {
                continue;
            }

            var modified = reader.Numerics(Schema.Modified);
            var sizes = reader.Numerics(Schema.Size);
            var iterator = terms.SeekCeil(prefix);
            while (iterator.MoveNext() && iterator.TermBytes.StartsWith(prefix))
            {
                var key = iterator.Term;
                var postings = reader.Core.PostingsFile.Postings(iterator.Meta, Storage.IndexOptions.Docs, false);
                while (postings.NextDoc() != PostingsEnum.NoMoreDocs)
                {
                    var doc = postings.DocId;
                    if (!reader.IsDeleted(doc))
                    {
                        result[key] = (modified?[doc] ?? 0, sizes?[doc] ?? 0);
                    }
                }
            }
        }

        return result;
    }

    public static IEnumerable<FileInfo> Enumerate(string root, GlobMatcher matcher, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var entry in entries)
            {
                var relative = System.IO.Path.GetRelativePath(root, entry.FullName);
                if (entry is DirectoryInfo child)
                {
                    if (!matcher.IsExcludedDirectory(relative))
                    {
                        pending.Push(child);
                    }
                }
                else if (entry is FileInfo file && matcher.IsMatch(relative))
                {
                    yield return file;
                }
            }
        }
    }
}
