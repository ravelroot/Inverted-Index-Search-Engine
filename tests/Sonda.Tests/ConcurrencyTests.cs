using Sonda.Engine;
using Sonda.Engine.Index;
using Sonda.Engine.Search;

namespace Sonda.Tests;

public class ConcurrencyTests
{
    [Fact]
    public void Indexacao_paralela_com_varios_flushes_nao_perde_documentos()
    {
        using var temp = new TempDirectory();
        using var engine = SondaEngine.Open(Docs.Options(temp.Path, ramMb: 4));
        var writer = engine.Writer!;
        const int total = 6000;
        Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            writer.Add(Docs.Make($"doc{i}.txt", $"Título {i}", $"palavra{i % 13} grupo{i % 5} identificador{i} " + string.Join(' ', Enumerable.Repeat("conteúdo repetido para ocupar memória", 12))));
        });
        writer.Commit();

        var stats = engine.GetStatistics();
        Assert.Equal(total, stats.Documents);
        Assert.True(stats.Segments.Count > 1);
        Assert.Equal(total, engine.Search(new SearchRequest { Query = "conteúdo" }).Total);
        Assert.Equal(1, engine.Search(new SearchRequest { Query = "identificador4321" }).Total);
        var expected = Enumerable.Range(0, total).Count(i => i % 13 == 4 && i % 5 == 2);
        Assert.Equal(expected, engine.Search(new SearchRequest { Query = "palavra4 grupo2" }).Total);
    }

    [Fact]
    public async Task Buscas_concorrentes_durante_a_indexacao_nao_falham()
    {
        using var temp = new TempDirectory();
        using var engine = SondaEngine.Open(Docs.Options(temp.Path, ramMb: 4, autoMerge: true));
        var writer = engine.Writer!;
        using var cancellation = new CancellationTokenSource();
        using var started = new CountdownEvent(4);
        var searches = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            var count = 0;
            do
            {
                engine.Search(new SearchRequest { Query = "conteúdo OR palavra3", Operator = DefaultOperator.Or });
                if (count++ == 0)
                {
                    started.Signal();
                }
            }
            while (!cancellation.IsCancellationRequested);

            return count;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));

        await Task.Run(() => Parallel.For(0, 3000, i =>
        {
            writer.Add(Docs.Make($"d{i}.txt", $"Doc {i}", $"conteúdo palavra{i % 9}"));
            if (i % 500 == 0)
            {
                writer.Commit();
            }
        }));
        writer.Commit();
        await cancellation.CancelAsync();
        var executed = await Task.WhenAll(searches);
        Assert.All(executed, c => Assert.True(c > 0));
        Assert.Equal(3000, engine.Search(new SearchRequest { Query = "conteúdo" }).Total);
    }

    [Fact]
    public void Wand_retorna_o_mesmo_top_k_que_a_busca_exaustiva()
    {
        using var temp = new TempDirectory();
        using var engine = SondaEngine.Open(Docs.Options(temp.Path));
        var random = new Random(11);
        string[] vocabulary = [.. Enumerable.Range(0, 300).Select(i => $"termo{i}")];
        for (var d = 0; d < 3000; d++)
        {
            var words = Enumerable.Range(0, 20 + random.Next(80)).Select(_ => vocabulary[(int)Math.Floor(Math.Pow(random.NextDouble(), 2.5) * vocabulary.Length)]);
            engine.Writer!.Add(Docs.Make($"d{d}.txt", $"Documento {d}", string.Join(' ', words)));
        }

        engine.Writer!.Commit();
        using var lease = engine.Searchers.Acquire();
        var parser = new QueryParser(engine.Analyzer);
        foreach (var text in new[] { "termo1 termo50 termo200", "termo3 termo4", "termo0 termo299 termo150 termo75" })
        {
            var query = parser.Parse(text, DefaultOperator.Or).Query;
            var exhaustive = lease.Searcher.Search(query, 10, exactTotal: true, facets: false);
            var wand = lease.Searcher.Search(query, 10, exactTotal: false, facets: false);
            Assert.True(wand.TotalIsLowerBound);
            Assert.True(wand.Evaluated < exhaustive.TotalHits);
            Assert.Equal(exhaustive.Hits.Select(h => h.Doc), wand.Hits.Select(h => h.Doc));
        }
    }
}
