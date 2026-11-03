using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Sonda.Storage;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Index;

public sealed record BackupFile(string Name, long Bytes);

public sealed record BackupManifest(string Format, string Version, long Generation, int Documents, DateTimeOffset CreatedAt, IReadOnlyList<BackupFile> Files);

public sealed record RestoreResult(string IndexPath, long Generation, int Documents, int Files, string? PreviousPath);

public static class IndexBackup
{
    public const string ManifestName = "sonda-backup.json";
    private const string FormatName = "sonda-indice";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static BackupManifest Write(IndexSnapshot snapshot, string indexPath, Stream destination, CancellationToken cancellationToken = default)
    {
        var directory = new IndexDirectory(indexPath);
        var names = snapshot.Commit.ReferencedFiles().Distinct(StringComparer.Ordinal).ToList();
        var files = new List<BackupFile>(names.Count);
        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var source = new FileStream(directory.Resolve(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = entry.Open();
                source.CopyTo(target);
                files.Add(new BackupFile(name, source.Length));
            }

            var manifest = new BackupManifest(
                FormatName,
                typeof(IndexBackup).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                snapshot.Commit.Generation,
                snapshot.Commit.DocCount,
                DateTimeOffset.UtcNow,
                files);
            var manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Fastest);
            using (var stream = manifestEntry.Open())
            {
                JsonSerializer.Serialize(stream, manifest, Json);
            }

            return manifest;
        }
    }

    public static BackupManifest ReadManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return ReadManifest(archive);
    }

    private static BackupManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("O arquivo não é uma cópia de segurança da Sonda: falta o sonda-backup.json.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json) ?? throw new InvalidDataException("Manifesto da cópia de segurança vazio.");
        if (!string.Equals(manifest.Format, FormatName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Formato de cópia de segurança desconhecido.");
        }

        return manifest;
    }

    public static RestoreResult Restore(string zipPath, string indexPath, bool replace)
    {
        var target = Path.GetFullPath(indexPath);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !replace)
        {
            throw new InvalidOperationException($"O índice em {target} já tem dados. Use --substituir para guardar o atual ao lado e restaurar a cópia.");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var staging = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".restaurando-" + stamp;
        Directory.CreateDirectory(staging);
        try
        {
            BackupManifest manifest;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                manifest = ReadManifest(archive);
                var expected = manifest.Files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName == ManifestName)
                    {
                        continue;
                    }

                    if (!SafeName(entry.FullName) || !expected.Contains(entry.FullName))
                    {
                        throw new InvalidDataException($"Entrada inesperada na cópia de segurança: {entry.FullName}.");
                    }

                    entry.ExtractToFile(Path.Combine(staging, entry.FullName), overwrite: false);
                }

                foreach (var file in manifest.Files)
                {
                    var path = Path.Combine(staging, file.Name);
                    if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes)
                    {
                        throw new InvalidDataException($"Arquivo ausente ou incompleto na cópia de segurança: {file.Name}.");
                    }
                }
            }

            int documents;
            using (var snapshot = IndexSnapshot.OpenReadOnly(staging))
            {
                var directory = new IndexDirectory(staging);
                foreach (var name in snapshot.Commit.ReferencedFiles())
                {
                    using var file = directory.OpenFile(name);
                    CodecUtil.VerifyFile(file);
                }

                documents = snapshot.Commit.DocCount;
            }

            string? previous = null;
            if (Directory.Exists(target))
            {
                using (new IndexDirectory(target).ObtainWriteLock())
                {
                }

                if (Directory.EnumerateFileSystemEntries(target).Any())
                {
                    previous = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".anterior-" + stamp;
                    Directory.Move(target, previous);
                }
                else
                {
                    Directory.Delete(target);
                }
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            Directory.Move(staging, target);
            return new RestoreResult(target, manifest.Generation, documents, manifest.Files.Count, previous);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }

    private static bool SafeName(string name)
        => name.Length is > 0 and <= 128
            && !name.Contains('/', StringComparison.Ordinal)
            && !name.Contains('\\', StringComparison.Ordinal)
            && !name.Contains("..", StringComparison.Ordinal)
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}
