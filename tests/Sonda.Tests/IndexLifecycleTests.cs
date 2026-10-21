using Sonda.Engine;
using Sonda.Engine.Index;
using Sonda.Storage.Formats;

namespace Sonda.Tests;

public class IndexLifecycleTests
{
    [Fact]
    public void Documentos_so_aparecem_para_outros_leitores_depois_do_commit()
    {
        using var temp = new TempDirectory();
        using var writer = IndexWriter.Open(temp.Path);
        writer.Add(Docs.Make("a.txt", "Primeiro", "conteúdo do primeiro documento"));
        using (var before = IndexSnapshot.OpenReadOnly(temp.Path))
        {
            Assert.Equal(0, before.Commit.DocCount);
        }

        writer.Commit();
        using var after = IndexSnapshot.OpenReadOnly(temp.Path);
        Assert.Equal(1, after.Commit.DocCount);
    }

    [Fact]
    public void Indice_persiste_e_reabre()
    {
        using var temp = new TempDirectory();
        using (var engine = Docs.EngineWith(temp.Path, Docs.Make("a.txt", "Farol", "O farol guia os navios à noite.")))
        {
            Assert.Equal(1, engine.Search(new SearchRequest { Query = "farol" }).Total);
        }

        using var reopened = SondaEngine.Open(Docs.Options(temp.Path));
        Assert.Equal(1, reopened.Search(new SearchRequest { Query = "navios" }).Total);
        Assert.Empty(reopened.Verify().Problems);
    }

    [Fact]
    public void Atualizar_substitui_e_excluir_remove()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path,
            Docs.Make("a.txt", "Versão antiga", "conteúdo sobre girassol"),
            Docs.Make("b.txt", "Outro", "conteúdo sobre orquídea"));
        engine.Writer!.Update(Docs.Make("a.txt", "Versão nova", "conteúdo sobre margarida"));
        engine.Writer.Delete(SourceDocument.MakeKey("teste", "b.txt"));
        engine.Writer.Commit();
        Assert.Equal(0, engine.Search(new SearchRequest { Query = "girassol" }).Total);
        Assert.Equal(0, engine.Search(new SearchRequest { Query = "orquídea" }).Total);
        Assert.Equal("Versão nova", engine.Search(new SearchRequest { Query = "margarida" }).Hits.Single().Title);
        Assert.Equal(1, engine.GetStatistics().Documents);
    }

    [Fact]
    public async Task Mesclagem_preserva_resultados_e_remove_excluidos()
    {
        using var temp = new TempDirectory();
        using var engine = SondaEngine.Open(Docs.Options(temp.Path));
        var writer = engine.Writer!;
        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < 300; i++)
            {
                writer.Add(Docs.Make($"r{round}-{i}.txt", $"Documento {round} {i}", $"rodada{round} item{i} conteúdo comum palavra{i % 7}"));
            }

            writer.Commit();
        }

        for (var i = 0; i < 300; i += 3)
        {
            writer.Delete(SourceDocument.MakeKey("teste", $"r1-{i}.txt"));
        }

        writer.Commit();
        var before = engine.Search(new SearchRequest { Query = "palavra3", PageSize = 100 });
        Assert.Equal(4, engine.GetStatistics().Segments.Count);

        await engine.OptimizeAsync(1);
        var stats = engine.GetStatistics();
        Assert.Single(stats.Segments);
        Assert.Equal(0, stats.Deleted);
        Assert.Equal(1100, stats.Documents);

        var after = engine.Search(new SearchRequest { Query = "palavra3", PageSize = 100 });
        Assert.Equal(before.Total, after.Total);
        Assert.Equal(before.Hits.Select(h => h.Key).Order(), after.Hits.Select(h => h.Key).Order());
        Assert.Equal(1, engine.Search(new SearchRequest { Query = "+rodada2 +item10" }).Total);
        Assert.Equal(0, engine.Search(new SearchRequest { Query = "+rodada1 +item3" }).Total);
    }

    [Fact]
    public void Listas_longas_usam_blocos_e_saltos_corretamente()
    {
        using var temp = new TempDirectory();
        using var engine = SondaEngine.Open(Docs.Options(temp.Path));
        for (var i = 0; i < 2000; i++)
        {
            var extra = i % 250 == 0 ? " raro" : string.Empty;
            engine.Writer!.Add(Docs.Make($"d{i}.txt", $"Documento {i}", $"comum{extra} número{i % 10}"));
        }

        engine.Writer!.Commit();
        var postings = engine.Postings(Schema.Body, "comum", 10);
        Assert.NotNull(postings);
        Assert.Equal(2000, postings!.DocFreq);
        Assert.Equal(15, postings.Segments.Single().SkipBlocks);
        Assert.Equal(8, engine.Search(new SearchRequest { Query = "comum raro" }).Total);
        Assert.Equal(200, engine.Search(new SearchRequest { Query = "comum número7" }).Total);
    }

    [Fact]
    public void Segundo_escritor_no_mesmo_indice_e_recusado()
    {
        using var temp = new TempDirectory();
        using var first = IndexWriter.Open(temp.Path);
        Assert.Throws<Sonda.Storage.IndexLockedException>(() => IndexWriter.Open(temp.Path));
    }

    [Fact]
    public async Task Arquivos_antigos_sao_apagados_depois_do_commit()
    {
        using var temp = new TempDirectory();
        using var engine = SondaEngine.Open(Docs.Options(temp.Path));
        for (var round = 0; round < 3; round++)
        {
            engine.Writer!.Add(Docs.Make($"{round}.txt", "Título", "texto"));
            engine.Writer.Commit();
        }

        await engine.OptimizeAsync(1);
        var commits = Directory.GetFiles(temp.Path, CommitPoint.Prefix + "*");
        Assert.Single(commits);
        var segments = Directory.GetFiles(temp.Path, "*.si");
        Assert.Single(segments);
    }
}
