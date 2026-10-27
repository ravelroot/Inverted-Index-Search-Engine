using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sonda.Engine.Search;

namespace Sonda.Engine.Export;

public enum ExportFormat
{
    Csv,
    Json,
    NdJson,
}

public sealed record ExportRow(
    [property: JsonPropertyName("posicao")] int Position,
    [property: JsonPropertyName("pontuacao")] double Score,
    [property: JsonPropertyName("titulo")] string Title,
    [property: JsonPropertyName("fonte")] string Source,
    [property: JsonPropertyName("caminho")] string Path,
    [property: JsonPropertyName("tipo")] string Extension,
    [property: JsonPropertyName("idioma")] string Language,
    [property: JsonPropertyName("tamanho")] long Size,
    [property: JsonPropertyName("modificado")] DateTimeOffset Modified,
    [property: JsonPropertyName("trecho")] string Snippet);

/// <summary>Exporta resultados de busca em CSV, JSON ou NDJSON (uma linha JSON por resultado).</summary>
public static class SearchExport
{
    public static readonly IReadOnlyList<string> CsvColumns =
        ["posicao", "pontuacao", "titulo", "fonte", "caminho", "tipo", "idioma", "tamanho", "modificado", "trecho"];

    public static readonly IReadOnlyList<string> FormatNames = ["csv", "json", "ndjson"];

    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool TryParseFormat(string? value, out ExportFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "csv":
                format = ExportFormat.Csv;
                return true;
            case "json":
                format = ExportFormat.Json;
                return true;
            case "ndjson" or "jsonl" or "jsonlines":
                format = ExportFormat.NdJson;
                return true;
            default:
                format = ExportFormat.Csv;
                return false;
        }
    }

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Json => "json",
        ExportFormat.NdJson => "ndjson",
        _ => "csv",
    };

    public static string ContentType(ExportFormat format) => format switch
    {
        ExportFormat.Json => "application/json; charset=utf-8",
        ExportFormat.NdJson => "application/x-ndjson; charset=utf-8",
        _ => "text/csv; charset=utf-8",
    };

    public static IEnumerable<ExportRow> Rows(IEnumerable<SearchHit> hits, int firstPosition = 1)
    {
        var position = firstPosition;
        foreach (var hit in hits)
        {
            yield return new ExportRow(
                position++,
                Math.Round(hit.Score, 6),
                hit.Title,
                hit.Source,
                hit.Path,
                hit.Extension,
                hit.Language,
                hit.Size,
                hit.Modified,
                string.Concat(hit.Snippet.Select(f => f.Text)).ReplaceLineEndings(" ").Trim());
        }
    }

    /// <summary>Célula CSV com aspas quando preciso e apóstrofo antes de =, +, -, @, tab ou CR, para o Excel não executar fórmulas.</summary>
    public static string CsvCell(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    public static string ToCsv(IEnumerable<ExportRow> rows)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', CsvColumns)).Append("\r\n");
        foreach (var row in rows)
        {
            builder
                .Append(row.Position.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Score.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvCell(row.Title)).Append(',')
                .Append(CsvCell(row.Source)).Append(',')
                .Append(CsvCell(row.Path)).Append(',')
                .Append(CsvCell(row.Extension)).Append(',')
                .Append(CsvCell(row.Language)).Append(',')
                .Append(row.Size.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Modified.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvCell(row.Snippet)).Append("\r\n");
        }

        return builder.ToString();
    }

    public static string ToNdJson(IEnumerable<ExportRow> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.Append(JsonSerializer.Serialize(row, Compact)).Append('\n');
        }

        return builder.ToString();
    }

    public static string ToJson(string query, long total, IReadOnlyList<ExportRow> rows, DateTimeOffset generatedAt)
        => JsonSerializer.Serialize(new { consulta = query, total, exportados = rows.Count, geradoEm = generatedAt, resultados = rows }, Indented);

    /// <summary>Bytes prontos para gravar: CSV ganha BOM UTF-8 para abrir com acentos no Excel.</summary>
    public static byte[] Encode(ExportFormat format, string query, long total, IReadOnlyList<ExportRow> rows, DateTimeOffset generatedAt)
    {
        var text = format switch
        {
            ExportFormat.Json => ToJson(query, total, rows, generatedAt),
            ExportFormat.NdJson => ToNdJson(rows),
            _ => ToCsv(rows),
        };
        var body = Encoding.UTF8.GetBytes(text);
        return format == ExportFormat.Csv ? [.. Encoding.UTF8.Preamble, .. body] : body;
    }
}
