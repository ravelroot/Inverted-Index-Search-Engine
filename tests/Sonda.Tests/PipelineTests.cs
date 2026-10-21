using Sonda.Engine;
using Sonda.Engine.Crawling;

namespace Sonda.Tests;

public class PipelineTests
{
    [Fact]
    public async Task Indexacao_incremental_detecta_novos_alterados_e_removidos()
    {
        using var temp = new TempDirectory();
        var folder = temp.Combine("docs");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        Directory.CreateDirectory(Path.Combine(folder, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "Primeiro\nconteúdo sobre faróis");
        await File.WriteAllTextAsync(Path.Combine(folder, "sub", "b.md"), "# Segundo\n\nconteúdo sobre navios");
        await File.WriteAllTextAsync(Path.Combine(folder, "c.html"), "<html><head><title>Terceiro</title></head><body><p>conteúdo sobre portos</p></body></html>");
        await File.WriteAllTextAsync(Path.Combine(folder, "ignorado.csv"), "não deve entrar");
        await File.WriteAllTextAsync(Path.Combine(folder, "node_modules", "x.txt"), "não deve entrar");

        using var engine = SondaEngine.Open(Docs.Options(temp.Combine("indice")));
        var source = new SourceDefinition { Name = "docs", Path = folder };
        var first = await engine.IndexAsync(source, IndexingMode.Incremental);
        Assert.Equal(3, first.Added);
        Assert.Equal(0, first.Failed);
        Assert.Equal(3, engine.Search(new SearchRequest { Query = "conteúdo" }).Total);
        Assert.Equal("Segundo", engine.Search(new SearchRequest { Query = "navios" }).Hits.Single().Title);

        var second = await engine.IndexAsync(source, IndexingMode.Incremental);
        Assert.Equal(3, second.Unchanged);
        Assert.Equal(0, second.Added + second.Updated);

        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "Primeiro\nconteúdo sobre lanternas e mais texto");
        File.SetLastWriteTimeUtc(Path.Combine(folder, "a.txt"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(folder, "c.html"));
        var third = await engine.IndexAsync(source, IndexingMode.Incremental);
        Assert.Equal(1, third.Updated);
        Assert.Equal(1, third.Deleted);
        Assert.Equal(0, engine.Search(new SearchRequest { Query = "faróis" }).Total);
        Assert.Equal(1, engine.Search(new SearchRequest { Query = "lanternas" }).Total);
        Assert.Equal(0, engine.Search(new SearchRequest { Query = "portos" }).Total);

        var full = await engine.IndexAsync(source, IndexingMode.Full);
        Assert.Equal(2, full.Updated);
        Assert.Equal(2, engine.GetStatistics().Documents);
        Assert.Equal(2, engine.RemoveSource("docs"));
        Assert.Equal(0, engine.GetStatistics().Documents);
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("pasta/b.MD", true)]
    [InlineData("pasta/node_modules/c.txt", false)]
    [InlineData(".git/config.txt", false)]
    [InlineData("imagem.png", false)]
    public void Padroes_de_inclusao_e_exclusao(string path, bool expected)
    {
        var matcher = new GlobMatcher(SourceDefinition.DefaultInclude, SourceDefinition.DefaultExclude);
        Assert.Equal(expected, matcher.IsMatch(path));
    }
}
