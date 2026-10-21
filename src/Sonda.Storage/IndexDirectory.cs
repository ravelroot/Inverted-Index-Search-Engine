using System.Collections.Concurrent;

namespace Sonda.Storage;

public sealed class IndexDirectory
{
    public const string LockFileName = "write.lock";

    private readonly ConcurrentDictionary<string, byte> _pendingDeletes = new(StringComparer.Ordinal);

    public IndexDirectory(string path)
    {
        FullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public string Resolve(string name) => Path.Combine(FullPath, name);

    public IndexOutput CreateOutput(string name) => new(name, Resolve(name));

    public IndexFile OpenFile(string name) => IndexFile.Open(name, Resolve(name));

    public bool FileExists(string name) => File.Exists(Resolve(name));

    public long FileLength(string name) => new FileInfo(Resolve(name)).Length;

    public IReadOnlyList<string> ListAll()
        => Directory.EnumerateFiles(FullPath).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToList();

    public void Rename(string from, string to) => File.Move(Resolve(from), Resolve(to), overwrite: false);

    public bool DeleteFile(string name)
    {
        try
        {
            var path = Resolve(name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            _pendingDeletes.TryRemove(name, out _);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pendingDeletes.TryAdd(name, 0);
            return false;
        }
    }

    public int RetryPendingDeletes()
    {
        var remaining = 0;
        foreach (var name in _pendingDeletes.Keys)
        {
            if (!DeleteFile(name))
            {
                remaining++;
            }
        }

        return remaining;
    }

    public IDisposable ObtainWriteLock()
    {
        try
        {
            var stream = new FileStream(Resolve(LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return stream;
        }
        catch (IOException ex)
        {
            throw new IndexLockedException($"O índice em {FullPath} já está aberto para gravação por outro processo.", ex);
        }
    }

    public long SizeInBytes()
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(FullPath))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
        }

        return total;
    }
}

public sealed class IndexLockedException(string message, Exception inner) : IOException(message, inner);
