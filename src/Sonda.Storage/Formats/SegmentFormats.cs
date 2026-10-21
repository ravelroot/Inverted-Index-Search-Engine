using System.Globalization;

namespace Sonda.Storage.Formats;

public sealed record SegmentInfo(
    string Name,
    int DocCount,
    string Origin,
    DateTimeOffset CreatedAt,
    FieldInfos Fields,
    IReadOnlyList<string> Files);

public static class SegmentInfoFormat
{
    public const string Extension = "si";
    private const string Codec = "SondaSegmentInfo";
    private const int Version = 1;

    public static void Write(IndexDirectory directory, SegmentInfo info)
    {
        using var output = directory.CreateOutput($"{info.Name}.{Extension}");
        CodecUtil.WriteHeader(output, Codec, Version);
        output.WriteString(info.Name);
        output.WriteVInt(info.DocCount);
        output.WriteString(info.Origin);
        output.WriteInt64(info.CreatedAt.ToUnixTimeMilliseconds());
        info.Fields.Write(output);
        output.WriteVInt(info.Files.Count);
        foreach (var file in info.Files)
        {
            output.WriteString(file);
        }

        CodecUtil.WriteFooter(output);
        output.Sync();
    }

    public static SegmentInfo Read(IndexDirectory directory, string segment)
    {
        using var file = directory.OpenFile($"{segment}.{Extension}");
        CodecUtil.VerifyFile(file);
        var input = file.CreateInput(4096);
        CodecUtil.CheckHeader(input, Codec, Version, Version);
        var name = input.ReadString();
        var docCount = input.ReadVInt();
        var origin = input.ReadString();
        var created = DateTimeOffset.FromUnixTimeMilliseconds(input.ReadInt64());
        var fields = FieldInfos.Read(input);
        var count = input.ReadVInt();
        var files = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            files.Add(input.ReadString());
        }

        return new SegmentInfo(name, docCount, origin, created, fields, files);
    }

    public static IReadOnlyList<string> FilesFor(string segment) =>
    [
        $"{segment}.{Extension}",
        $"{segment}.{TermsWriter.DictionaryExtension}",
        $"{segment}.{TermsWriter.IndexExtension}",
        $"{segment}.{PostingsWriter.DocExtension}",
        $"{segment}.{PostingsWriter.PosExtension}",
        $"{segment}.{NormsFormat.Extension}",
        $"{segment}.{StoredFieldsWriter.DataExtension}",
        $"{segment}.{StoredFieldsWriter.IndexExtension}",
        $"{segment}.{DocValuesFormat.Extension}",
    ];
}

public sealed record SegmentCommitEntry(string Name, int DocCount, int DelCount, long DelGen)
{
    public int LiveDocs => DocCount - DelCount;

    public string? LiveDocsFile => DelGen > 0 ? LiveDocsFormat.FileName(Name, DelGen) : null;
}

public sealed record CommitPoint(
    long Generation,
    long Version,
    long Counter,
    IReadOnlyList<SegmentCommitEntry> Segments,
    IReadOnlyDictionary<string, string> UserData)
{
    public const string Prefix = "segments_";

    public string FileName => FileNameFor(Generation);

    public static string FileNameFor(long generation) => Prefix + generation.ToString(CultureInfo.InvariantCulture);

    public static CommitPoint Empty { get; } = new(0, 0, 0, [], new Dictionary<string, string>());

    public int DocCount => Segments.Sum(s => s.LiveDocs);

    public IEnumerable<string> ReferencedFiles()
    {
        yield return FileName;
        foreach (var segment in Segments)
        {
            foreach (var file in SegmentInfoFormat.FilesFor(segment.Name))
            {
                yield return file;
            }

            if (segment.LiveDocsFile is { } live)
            {
                yield return live;
            }
        }
    }
}

public static class CommitFormat
{
    private const string Codec = "SondaCommit";
    private const int Version = 1;

    public static void Write(IndexDirectory directory, CommitPoint commit)
    {
        var pending = "pending_" + commit.FileName;
        directory.DeleteFile(pending);
        using (var output = directory.CreateOutput(pending))
        {
            CodecUtil.WriteHeader(output, Codec, Version);
            output.WriteVLong(commit.Generation);
            output.WriteVLong(commit.Version);
            output.WriteVLong(commit.Counter);
            output.WriteVInt(commit.Segments.Count);
            foreach (var segment in commit.Segments)
            {
                output.WriteString(segment.Name);
                output.WriteVInt(segment.DocCount);
                output.WriteVInt(segment.DelCount);
                output.WriteVLong(segment.DelGen);
            }

            output.WriteVInt(commit.UserData.Count);
            foreach (var (key, value) in commit.UserData)
            {
                output.WriteString(key);
                output.WriteString(value);
            }

            CodecUtil.WriteFooter(output);
            output.Sync();
        }

        directory.Rename(pending, commit.FileName);
    }

    public static CommitPoint Read(IndexDirectory directory, string fileName)
    {
        using var file = directory.OpenFile(fileName);
        CodecUtil.VerifyFile(file);
        var input = file.CreateInput(4096);
        CodecUtil.CheckHeader(input, Codec, Version, Version);
        var generation = input.ReadVLong();
        var version = input.ReadVLong();
        var counter = input.ReadVLong();
        var count = input.ReadVInt();
        var segments = new List<SegmentCommitEntry>(count);
        for (var i = 0; i < count; i++)
        {
            segments.Add(new SegmentCommitEntry(input.ReadString(), input.ReadVInt(), input.ReadVInt(), input.ReadVLong()));
        }

        var userCount = input.ReadVInt();
        var userData = new Dictionary<string, string>(userCount, StringComparer.Ordinal);
        for (var i = 0; i < userCount; i++)
        {
            userData[input.ReadString()] = input.ReadString();
        }

        return new CommitPoint(generation, version, counter, segments, userData);
    }

    public static CommitPoint ReadLatest(IndexDirectory directory)
    {
        var candidates = directory.ListAll()
            .Where(f => f.StartsWith(CommitPoint.Prefix, StringComparison.Ordinal))
            .Select(f => (File: f, Generation: long.TryParse(f.AsSpan(CommitPoint.Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var g) ? g : -1))
            .Where(c => c.Generation >= 0)
            .OrderByDescending(c => c.Generation)
            .ToList();

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return Read(directory, candidate.File);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
            }
        }

        if (lastError is not null && candidates.Count > 0)
        {
            throw new CorruptIndexException($"Nenhum ponto de gravação válido em {directory.FullPath}: {lastError.Message}");
        }

        return CommitPoint.Empty;
    }
}
