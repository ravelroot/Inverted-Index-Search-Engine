using Sonda.Analysis;
using Sonda.Storage;

namespace Sonda.Engine;

public static class Schema
{
    public const int Id = 0;
    public const int Title = 1;
    public const int Body = 2;
    public const int Path = 3;
    public const int Extension = 4;
    public const int Language = 5;
    public const int Source = 6;
    public const int Modified = 7;
    public const int Size = 8;
    public const int Description = 9;

    public static readonly FieldInfos Fields = new(
    [
        new FieldInfo(Id, "id", IndexOptions.Docs, false, DocValuesKind.None),
        new FieldInfo(Title, "title", IndexOptions.DocsFreqsAndPositions, true, DocValuesKind.None),
        new FieldInfo(Body, "body", IndexOptions.DocsFreqsAndPositions, true, DocValuesKind.None),
        new FieldInfo(Path, "path", IndexOptions.DocsFreqsAndPositions, true, DocValuesKind.None),
        new FieldInfo(Extension, "ext", IndexOptions.Docs, false, DocValuesKind.Keyword),
        new FieldInfo(Language, "lang", IndexOptions.Docs, false, DocValuesKind.Keyword),
        new FieldInfo(Source, "source", IndexOptions.Docs, false, DocValuesKind.Keyword),
        new FieldInfo(Modified, "modified", IndexOptions.None, false, DocValuesKind.Numeric),
        new FieldInfo(Size, "size", IndexOptions.None, false, DocValuesKind.Numeric),
        new FieldInfo(Description, "description", IndexOptions.None, false, DocValuesKind.None),
    ]);

    public static readonly int[] IndexedFields = [Id, Title, Body, Path, Extension, Language, Source];

    public static readonly int[] NormFields = [Title, Body, Path];

    public static readonly int[] TextFields = [Title, Body];

    public static readonly int[] KeywordFields = [Extension, Language, Source];

    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = Title,
        ["titulo"] = Title,
        ["título"] = Title,
        ["body"] = Body,
        ["corpo"] = Body,
        ["texto"] = Body,
        ["conteudo"] = Body,
        ["conteúdo"] = Body,
        ["path"] = Path,
        ["caminho"] = Path,
        ["pasta"] = Path,
        ["arquivo"] = Path,
        ["ext"] = Extension,
        ["tipo"] = Extension,
        ["extensao"] = Extension,
        ["extensão"] = Extension,
        ["lang"] = Language,
        ["idioma"] = Language,
        ["lingua"] = Language,
        ["língua"] = Language,
        ["source"] = Source,
        ["fonte"] = Source,
        ["id"] = Id,
        ["chave"] = Id,
    };

    public static bool TryResolveField(string name, out int field) => Aliases.TryGetValue(name, out field);

    public static string NameOf(int field) => Fields[field]?.Name ?? field.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsKeyword(int field) => field is Id or Extension or Language or Source;
}

public sealed record SourceDocument
{
    public required string Key { get; init; }

    public required string Source { get; init; }

    public required string Path { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public string? Description { get; init; }

    public string Extension { get; init; } = string.Empty;

    public Language LanguageHint { get; init; } = Language.Unknown;

    public long Size { get; init; }

    public DateTimeOffset Modified { get; init; } = DateTimeOffset.UnixEpoch;

    public static string MakeKey(string source, string relativePath) => source + ":" + relativePath.Replace('\\', '/');
}
