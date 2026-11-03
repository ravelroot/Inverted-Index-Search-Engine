using System.IO.Compression;
using Sonda.Analysis;
using Sonda.Engine;
using Sonda.Engine.Index;
using Sonda.Engine.Search;

namespace Sonda.Tests;

public class RecursosTests
{
    private static SourceDocument[] Corpus() =>
    [
        Docs.Make("motor.txt", "Motores de busca", "Um motor de busca usa um índice invertido. O índice invertido acelera as buscas.", modified: 3000),
        Docs.Make("bolo.txt", "Bolo de cenoura", "Bata as cenouras com ovos e asse o bolo. Cobertura de chocolate.", modified: 2000),
        Docs.Make("bm25.txt", "Relevância", "O BM25 calcula a relevância usando a frequência do termo e o tamanho do documento.", modified: 1000),
        Docs.Make("engine.html", "Search engines", "Search engines rank documents with an inverted index and BM25.", Language.English, ".html", 4000),
        Docs.Make("farol.txt", "Faróis da costa", "Os faróis orientam a navegação e o farol de Santa Marta fica em Laguna.", modified: 5000),
    ];

    [Fact]
    public void Sugere_correcao_quando_a_palavra_nao_existe_no_indice()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var response = engine.Search(new SearchRequest { Query = "cenoras" });
        Assert.Equal(0, response.Total);
        Assert.NotNull(response.Suggestion);
        Assert.Equal("cenouras", response.Suggestion!.Corrected);
        Assert.Equal(1, response.Suggestion.Total);
        var correction = Assert.Single(response.Suggestion.Corrections);
        Assert.Equal("cenoras", correction.Original);
        Assert.Equal(1, correction.Distance);
    }

    [Fact]
    public void Nao_sugere_nada_quando_todas_as_palavras_existem()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        Assert.Null(engine.Search(new SearchRequest { Query = "bolo cenoura" }).Suggestion);
        Assert.Null(engine.Search(new SearchRequest { Query = "busca", Page = 2 }).Suggestion);
        Assert.Null(engine.Search(new SearchRequest { Query = "xpto", Spelling = false }).Suggestion);
    }

    [Fact]
    public void Correcao_preserva_operadores_campos_e_filtros()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var suggestion = engine.Suggest("titulo:Bolo OR Chocolatte tipo:txt");
        Assert.NotNull(suggestion);
        Assert.Equal("titulo:Bolo OR Chocolate tipo:txt", suggestion!.Corrected);
        Assert.Null(engine.Suggest("tipo:htmll"));
        Assert.Null(engine.Suggest("bolo -chocolatte"));
        Assert.Null(engine.Suggest("cenor*"));
    }

    [Fact]
    public void Correcao_devolve_a_forma_escrita_com_acentos()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var suggestion = engine.Suggest("navegasao");
        Assert.NotNull(suggestion);
        Assert.Equal("navegação", suggestion!.Corrected);
    }

    [Fact]
    public void Comparacao_mostra_as_duas_listas_e_a_mudanca_de_posicao()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var comparison = engine.Compare(new SearchRequest { Query = "indice OR bm25 OR busca", Operator = DefaultOperator.Or }, 5);
        Assert.Equal("BM25", comparison.Bm25.Similarity);
        Assert.NotEqual(comparison.Bm25.Similarity, comparison.TfIdf.Similarity);
        Assert.Equal(comparison.Bm25.Total, comparison.TfIdf.Total);
        Assert.True(comparison.Overlap > 0 && comparison.Overlap <= comparison.Top);
        foreach (var hit in comparison.Bm25.Hits.Where(h => h.OtherRank is not null))
        {
            var other = comparison.TfIdf.Hits.Single(h => h.Key == hit.Key);
            Assert.Equal(hit.Rank, other.OtherRank);
            Assert.Equal(other.Rank, hit.OtherRank);
        }

        Assert.Equal(comparison.Overlap, comparison.Bm25.Hits.Count(h => h.OtherRank is not null));
    }

    [Fact]
    public void Copia_de_seguranca_restaura_o_indice_com_os_mesmos_resultados()
    {
        using var temp = new TempDirectory();
        var original = temp.Combine("indice");
        var zip = temp.Combine("copia.zip");
        long total;
        BackupManifest manifest;
        using (var engine = Docs.EngineWith(original, Corpus()))
        {
            engine.Writer!.Add(Docs.Make("extra.txt", "Extra", "Um segundo segmento com a palavra farol."));
            engine.Writer.Commit();
            total = engine.Search(new SearchRequest { Query = "farol" }).Total;
            using var stream = File.Create(zip);
            manifest = engine.Backup(stream);
        }

        Assert.Equal(6, manifest.Documents);
        Assert.Contains(manifest.Files, f => f.Name.StartsWith("segments_", StringComparison.Ordinal));
        Assert.Equal(manifest.Generation, IndexBackup.ReadManifest(zip).Generation);

        var restoredPath = temp.Combine("restaurado");
        var result = IndexBackup.Restore(zip, restoredPath, replace: false);
        Assert.Equal(6, result.Documents);
        Assert.Null(result.PreviousPath);
        using (var restored = SondaEngine.OpenReadOnly(Docs.Options(restoredPath)))
        {
            Assert.Equal(total, restored.Search(new SearchRequest { Query = "farol" }).Total);
            Assert.Empty(restored.Verify().Problems);
        }

        Assert.Throws<InvalidOperationException>(() => IndexBackup.Restore(zip, restoredPath, replace: false));
        var replaced = IndexBackup.Restore(zip, restoredPath, replace: true);
        Assert.NotNull(replaced.PreviousPath);
        Assert.True(Directory.Exists(replaced.PreviousPath));
        Assert.True(File.Exists(Path.Combine(restoredPath, manifest.Files[0].Name)));
    }

    [Fact]
    public void Restauracao_recusa_arquivos_fora_do_padrao()
    {
        using var temp = new TempDirectory();
        var original = temp.Combine("indice");
        var zip = temp.Combine("copia.zip");
        using (var engine = Docs.EngineWith(original, Corpus()))
        {
            using var stream = File.Create(zip);
            engine.Backup(stream);
        }

        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../fora.txt").Open());
            writer.Write("x");
        }

        var target = temp.Combine("destino");
        Assert.Throws<InvalidDataException>(() => IndexBackup.Restore(zip, target, replace: false));
        Assert.False(File.Exists(temp.Combine("fora.txt")));
        Assert.Empty(Directory.GetDirectories(temp.Path, "destino.restaurando-*"));
    }
}
