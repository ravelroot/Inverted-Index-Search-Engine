using Sonda.Analysis;
using Sonda.Engine.Search;

namespace Sonda.Tests;

public class QueryParserTests
{
    private static readonly QueryParser Parser = new(new TextAnalyzer(new AnalyzerOptions { Stemming = StemmingMode.Portuguese }));

    [Theory]
    [InlineData("motores busca", "+(title:motor^2 body:motor) +(title:busc^2 body:busc)")]
    [InlineData("motor OR busca", "(title:motor^2 body:motor) (title:busc^2 body:busc)")]
    [InlineData("motor -busca", "+(title:motor^2 body:motor) -(title:busc^2 body:busc)")]
    [InlineData("titulo:farol", "title:farol")]
    [InlineData("tipo:HTML farol", "#ext:html +(title:farol^2 body:farol)")]
    [InlineData("\"índice invertido\"~2", "title:\"indic invert\"~2^2 body:\"indic invert\"~2")]
    [InlineData("busc*", "title:busc*^2 body:busc*")]
    [InlineData("farol^3", "title:farol^6 body:farol^3")]
    [InlineData("motor E (busca OU indice)", "+(title:motor^2 body:motor) +((title:busc^2 body:busc) (title:indic^2 body:indic))")]
    public void Interpreta_a_sintaxe(string text, string expected)
    {
        Assert.Equal(expected, Parser.Parse(text).Query.ToString());
    }

    [Fact]
    public void Operador_padrao_ou_vira_clausulas_opcionais()
    {
        Assert.Equal("(title:motor^2 body:motor) (title:busc^2 body:busc)", Parser.Parse("motor busca", DefaultOperator.Or).Query.ToString());
    }

    [Fact]
    public void Somente_stopwords_resulta_em_consulta_vazia()
    {
        Assert.IsType<MatchNoDocsQuery>(Parser.Parse("de para com").Query);
    }

    [Theory]
    [InlineData("(motor")]
    [InlineData("motor OR")]
    [InlineData("titulo:")]
    public void Sintaxe_invalida_gera_excecao(string text)
    {
        Assert.ThrowsAny<QueryParseException>(() => Parser.Parse(text));
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("busca", "bsuca", 1)]
    [InlineData("indice", "indice", 0)]
    public void Distancia_de_edicao_com_transposicao(string a, string b, int expected)
    {
        Assert.Equal(expected, EditDistance.Compute(a, b, 3));
    }
}
