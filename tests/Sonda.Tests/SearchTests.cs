using Sonda.Analysis;
using Sonda.Engine;
using Sonda.Engine.Index;
using Sonda.Engine.Search;

namespace Sonda.Tests;

public class SearchTests
{
    private static SourceDocument[] Corpus() =>
    [
        Docs.Make("motor.txt", "Motores de busca", "Um motor de busca usa um índice invertido. O índice invertido acelera as buscas.", modified: 3000),
        Docs.Make("bolo.txt", "Bolo de cenoura", "Bata as cenouras com ovos e asse o bolo. Cobertura de chocolate.", modified: 2000),
        Docs.Make("bm25.txt", "Relevância", "O BM25 calcula a relevância usando a frequência do termo e o tamanho do documento.", modified: 1000),
        Docs.Make("engine.html", "Search engines", "Search engines rank documents with an inverted index and BM25.", Language.English, ".html", 4000),
        Docs.Make("longo.txt", "Documento longo", string.Join(' ', Enumerable.Repeat("texto comum sem relação", 60)) + " índice", modified: 500),
    ];

    [Fact]
    public void Encontra_documentos_por_radical_e_sem_acento()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var response = engine.Search(new SearchRequest { Query = "indice invertidos" });
        Assert.Equal("motor.txt", response.Hits[0].Path);
        Assert.Equal(1, response.Total);
        var crossLanguage = engine.Search(new SearchRequest { Query = "inverted" });
        Assert.Equal(2, crossLanguage.Total);
        Assert.Contains("engine.html", Docs.Keys(crossLanguage));
    }

    [Fact]
    public void Pontuacao_bm25_confere_com_a_formula()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        using var lease = engine.Searchers.Acquire();
        var searcher = lease.Searcher;
        var term = engine.Analyzer.AnalyzeWord("cenouras", Language.Portuguese)!;
        var query = new TermQuery(Schema.Body, term);
        var top = searcher.Search(query, 10);
        Assert.Single(top.Hits);

        var collection = searcher.CollectionStatistics(Schema.Body);
        var stats = searcher.TermStatistics(Schema.Body, term);
        var leaf = searcher.LeafFor(top.Hits[0].Doc);
        var norm = leaf.Reader.Norms(Schema.Body)![top.Hits[0].Doc - leaf.DocBase];
        var length = Sonda.Storage.NormEncoding.Decode(norm);
        var idf = Math.Log(1 + ((collection.DocCount - stats.DocFreq + 0.5) / (stats.DocFreq + 0.5)));
        const double freq = 1;
        var tf = freq * 2.2 / (freq + (1.2 * (1 - 0.75 + (0.75 * length / collection.AverageLength))));
        Assert.Equal(idf * tf, top.Hits[0].Score, 4);
    }

    [Fact]
    public void Titulo_pesa_mais_que_o_corpo()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path,
            Docs.Make("a.txt", "Receita de pão", "Texto qualquer sobre cozinha e farinha."),
            Docs.Make("b.txt", "Texto qualquer", "Receita de bolo com farinha, mas o assunto é cozinha."));
        var response = engine.Search(new SearchRequest { Query = "receita" });
        Assert.Equal("a.txt", response.Hits[0].Path);
    }

    [Fact]
    public void Frase_exata_respeita_a_ordem_e_frase_com_distancia_aceita_intervalos()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path,
            Docs.Make("a.txt", "A", "o índice invertido do motor"),
            Docs.Make("b.txt", "B", "invertido foi o índice do motor"),
            Docs.Make("c.txt", "C", "índice muito bem invertido"));
        var exact = engine.Search(new SearchRequest { Query = "\"índice invertido\"" });
        Assert.Equal(new[] { "a.txt" }, Docs.Keys(exact));
        var sloppy = engine.Search(new SearchRequest { Query = "\"índice invertido\"~3" });
        Assert.Equal(new[] { "a.txt", "c.txt" }, Docs.Keys(sloppy));
        var reversed = engine.Search(new SearchRequest { Query = "\"índice invertido\"~4" });
        Assert.Equal(3, reversed.Total);
    }

    [Fact]
    public void Prefixo_aproximada_exclusao_e_ou()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        Assert.Equal(new[] { "bolo.txt" }, Docs.Keys(engine.Search(new SearchRequest { Query = "cenou*" })));
        Assert.Equal(new[] { "bolo.txt" }, Docs.Keys(engine.Search(new SearchRequest { Query = "chocolat~" })));
        Assert.Equal(new[] { "engine.html" }, Docs.Keys(engine.Search(new SearchRequest { Query = "bm25 -relevancia" })));
        Assert.Equal(2, engine.Search(new SearchRequest { Query = "cenoura OR chocolate OR engines" }).Total);
    }

    [Fact]
    public void Filtros_por_tipo_e_idioma()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        Assert.Equal(new[] { "engine.html" }, Docs.Keys(engine.Search(new SearchRequest { Query = "bm25 tipo:html" })));
        Assert.Equal(new[] { "bm25.txt" }, Docs.Keys(engine.Search(new SearchRequest { Query = "bm25", Language = "pt" })));
        var facets = engine.Search(new SearchRequest { Query = "bm25" }).Facets;
        Assert.Equal(1, facets["ext"]["html"]);
        Assert.Equal(1, facets["lang"]["en"]);
    }

    [Fact]
    public void Busca_ampliada_quando_nenhum_documento_tem_todos_os_termos()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var response = engine.Search(new SearchRequest { Query = "chocolate engines" });
        Assert.True(response.Relaxed);
        Assert.Equal(2, response.Total);
    }

    [Fact]
    public void Ordena_por_mais_recentes()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var response = engine.Search(new SearchRequest { Query = "indice OR bm25", Operator = DefaultOperator.Or, Sort = SortMode.Recent });
        var dates = response.Hits.Select(h => h.Modified).ToList();
        Assert.Equal(dates.OrderByDescending(d => d).ToList(), dates);
    }

    [Fact]
    public void Trecho_destaca_os_termos_encontrados()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var hit = engine.Search(new SearchRequest { Query = "cenoura" }).Hits.Single();
        Assert.Contains(hit.Snippet, f => f.Match && f.Text == "cenouras");
        Assert.Contains(hit.TitleFragments, f => f.Match && f.Text == "cenoura");
    }

    [Fact]
    public void Explicacao_soma_o_valor_da_pontuacao()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var hit = engine.Search(new SearchRequest { Query = "motor busca", Explain = true }).Hits[0];
        Assert.NotNull(hit.Explanation);
        Assert.Equal(hit.Score, hit.Explanation!.Value, 3);
    }

    [Fact]
    public void Tfidf_tambem_ordena_por_relevancia()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        var response = engine.Search(new SearchRequest { Query = "indice", Similarity = "tfidf" });
        Assert.Equal("TF-IDF", response.Similarity);
        Assert.Equal("motor.txt", response.Hits[0].Path);
    }

    [Fact]
    public void Consultas_invalidas_geram_erro_claro()
    {
        using var temp = new TempDirectory();
        using var engine = Docs.EngineWith(temp.Path, Corpus());
        Assert.Throws<QueryParseException>(() => engine.Search(new SearchRequest { Query = "(motor busca" }));
        Assert.Empty(engine.Search(new SearchRequest { Query = "de para com" }).Hits);
    }
}
