using Sonda.Analysis;
using Sonda.Analysis.Extraction;

namespace Sonda.Tests;

public class AnalysisTests
{
    [Fact]
    public void Tokenizer_separa_palavras_e_mantem_numeros_decimais()
    {
        const string text = "Preço: R$ 3,14 por 1.500 unidades, ação-reação!";
        var tokens = Tokenizer.Tokenize(text).Select(t => text.Substring(t.Start, t.Length)).ToList();
        Assert.Equal(new[] { "Preço", "R", "3,14", "por", "1.500", "unidades", "ação", "reação" }, tokens);
    }

    [Fact]
    public void Folding_remove_acentos_e_poe_em_minusculas()
    {
        Assert.Equal("acao indice coracao pinguim", CharFolding.Fold("AÇÃO Índice Coração Pingüim"));
    }

    [Theory]
    [InlineData("caresses", "caress")]
    [InlineData("ponies", "poni")]
    [InlineData("ties", "ti")]
    [InlineData("agreed", "agre")]
    [InlineData("plastered", "plaster")]
    [InlineData("motoring", "motor")]
    [InlineData("conflated", "conflat")]
    [InlineData("hopping", "hop")]
    [InlineData("falling", "fall")]
    [InlineData("filing", "file")]
    [InlineData("happy", "happi")]
    [InlineData("sky", "sky")]
    [InlineData("relational", "relat")]
    [InlineData("generalizations", "gener")]
    [InlineData("oscillators", "oscil")]
    public void Porter_reduz_palavras_inglesas_como_o_algoritmo_original(string word, string expected)
    {
        Assert.Equal(expected, new EnglishStemmer().Stem(word));
    }

    [Theory]
    [InlineData("casas", "casa")]
    [InlineData("meninos", "menina")]
    [InlineData("professora", "professor")]
    [InlineData("canções", "canção")]
    [InlineData("buscando", "buscas")]
    [InlineData("indexação", "indexar")]
    [InlineData("motores", "motor")]
    public void Rslp_leva_formas_da_mesma_palavra_ao_mesmo_radical(string a, string b)
    {
        var stemmer = new PortugueseStemmer();
        Assert.Equal(stemmer.Stem(a), stemmer.Stem(b));
    }

    [Theory]
    [InlineData("rapidamente", "rapid")]
    [InlineData("papéis", "papel")]
    [InlineData("livrinho", "livr")]
    [InlineData("cantaríamos", "cant")]
    public void Rslp_remove_sufixos_de_adverbio_plural_diminutivo_e_verbo(string word, string expected)
    {
        Assert.Equal(expected, new PortugueseStemmer().Stem(word));
    }

    [Fact]
    public void Rslp_respeita_excecoes()
    {
        var stemmer = new PortugueseStemmer();
        Assert.Equal("lapis", stemmer.Stem("lápis"));
        Assert.Equal(stemmer.Stem("relevância"), stemmer.Stem("relevancia"));
        Assert.Equal(stemmer.Stem("ações"), stemmer.Stem("acoes"));
        Assert.Equal(stemmer.Stem("mesa"), stemmer.Stem("mesas"));
        Assert.NotEqual(stemmer.Stem("mesa"), stemmer.Stem("mês"));
    }

    [Fact]
    public void Analisador_descarta_stopwords_mas_conta_a_posicao()
    {
        var analyzer = new TextAnalyzer();
        var tokens = analyzer.Analyze("O motor de busca encontrou os documentos", Language.Portuguese);
        Assert.Equal(new[] { "motor", "busc", "encontr", "document" }, tokens.Select(t => t.Term));
        Assert.Equal(new[] { 1, 3, 4, 6 }, tokens.Select(t => t.Position));
    }

    [Fact]
    public void Analisador_gera_variantes_para_portugues_e_ingles_no_modo_automatico()
    {
        var analyzer = new TextAnalyzer();
        var variants = analyzer.QueryVariants("searching");
        Assert.Contains("search", variants);
        Assert.Contains("searching", variants);
        Assert.Empty(analyzer.QueryVariants("the"));
        Assert.Empty(analyzer.QueryVariants("para"));
    }

    [Theory]
    [InlineData("Os motores de busca usam um índice invertido para encontrar documentos.", Language.Portuguese)]
    [InlineData("Search engines use an inverted index to find the documents that match.", Language.English)]
    public void Detector_de_idioma_reconhece_portugues_e_ingles(string text, Language expected)
    {
        Assert.Equal(expected, LanguageDetector.Detect(text));
    }

    [Fact]
    public void Extrator_html_ignora_scripts_estilos_e_comentarios()
    {
        const string html = """
            <html lang="en"><head><title>Título &amp; teste</title><meta name="description" content="Resumo">
            <style>p { color: red }</style><script>var segredo = "não indexar";</script></head>
            <body><h1>Cabeçalho</h1><p>Primeiro&nbsp;parágrafo com <b>negrito</b>.</p><!-- oculto --><p>Segundo</p>
            <img src="x.png" alt="Descrição da imagem"></body></html>
            """;
        var content = HtmlTextExtractor.Extract(html, "arquivo");
        Assert.Equal("Título & teste", content.Title);
        Assert.Equal("Resumo", content.Description);
        Assert.Equal(Language.English, content.LanguageHint);
        Assert.Contains("Primeiro parágrafo com negrito.", content.Body);
        Assert.Contains("Descrição da imagem", content.Body);
        Assert.DoesNotContain("segredo", content.Body);
        Assert.DoesNotContain("color", content.Body);
        Assert.DoesNotContain("oculto", content.Body);
    }

    [Fact]
    public void Extrator_markdown_usa_o_primeiro_titulo_e_limpa_a_marcacao()
    {
        const string markdown = "# Guia rápido\n\nUm **texto** com [link](https://example.com) e `código`.\n\n- item da lista";
        var content = MarkdownTextExtractor.Extract(markdown, "arquivo");
        Assert.Equal("Guia rápido", content.Title);
        Assert.Contains("Um texto com link e código.", content.Body);
        Assert.Contains("item da lista", content.Body);
        Assert.DoesNotContain("https://", content.Body);
    }

    [Fact]
    public void Decodificador_aceita_utf8_e_latin1()
    {
        Assert.Equal("ação", TextDecoder.Decode(System.Text.Encoding.UTF8.GetBytes("ação")));
        Assert.Equal("ação", TextDecoder.Decode(System.Text.Encoding.Latin1.GetBytes("ação")));
        Assert.Equal("ação", TextDecoder.Decode([0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("ação")]));
    }
}
