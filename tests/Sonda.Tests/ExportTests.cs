using Sonda.Engine;
using Sonda.Engine.Export;

namespace Sonda.Tests;

public class ExportTests
{
    [Fact]
    public void Csv_neutraliza_formulas_e_escapa_separadores()
    {
        Assert.Equal("simples", SearchExport.CsvCell("simples"));
        Assert.Equal("\"a, b\"", SearchExport.CsvCell("a, b"));
        Assert.Equal("'@soma", SearchExport.CsvCell("@soma"));
        Assert.Equal("'+1", SearchExport.CsvCell("+1"));
        Assert.Equal("\"'=HIPERLINK(\"\"x\"\")\"", SearchExport.CsvCell("=HIPERLINK(\"x\")"));
        Assert.Equal(string.Empty, SearchExport.CsvCell(null));
    }

    [Fact]
    public void Formatos_de_exportacao_sao_reconhecidos()
    {
        Assert.True(SearchExport.TryParseFormat("CSV", out var csv));
        Assert.Equal(ExportFormat.Csv, csv);
        Assert.True(SearchExport.TryParseFormat("jsonl", out var ndjson));
        Assert.Equal(ExportFormat.NdJson, ndjson);
        Assert.False(SearchExport.TryParseFormat("xls", out _));
    }

    [Fact]
    public void Exporta_resultados_em_csv_com_bom_e_cabecalho()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path,
            Docs.Make("motor.txt", "Motores de busca", "Um motor de busca usa um índice invertido."),
            Docs.Make("bolo.txt", "Bolo de cenoura", "Bata as cenouras com ovos e asse o bolo."));
        var batch = engine.SearchMany(new SearchRequest { Query = "motor" }, 10);
        var bytes = SearchExport.Encode(ExportFormat.Csv, "motor", batch.First.Total, [.. SearchExport.Rows(batch.Hits)], DateTimeOffset.UnixEpoch);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Contains("posicao,pontuacao,titulo", text);
        Assert.Contains("motor.txt", text);
        Assert.DoesNotContain("bolo.txt", text);
    }

    [Fact]
    public void Mais_como_este_encontra_o_documento_parecido_e_ignora_o_proprio()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path,
            Docs.Make("motor.txt", "Motores de busca", "O motor de busca usa índice invertido e relevância para ordenar documentos."),
            Docs.Make("indice.txt", "Índice invertido", "Um índice invertido guarda cada termo; o motor de busca calcula a relevância."),
            Docs.Make("bolo.txt", "Bolo de cenoura", "Bata cenouras, ovos e açúcar; asse o bolo e cubra com chocolate."),
            Docs.Make("pao.txt", "Pão de queijo", "Misture polvilho, queijo e ovos; asse até dourar."));
        var similar = engine.MoreLikeThis(SourceDocument.MakeKey("teste", "motor.txt"), 3);
        Assert.NotNull(similar);
        Assert.NotEmpty(similar.Terms);
        Assert.Equal("indice.txt", similar.Hits[0].Path);
        Assert.DoesNotContain(similar.Hits, h => h.Path == "motor.txt");
        Assert.Null(engine.MoreLikeThis("teste:nao-existe.txt"));
    }
}
