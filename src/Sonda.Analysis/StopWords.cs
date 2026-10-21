namespace Sonda.Analysis;

public static class StopWords
{
    public static readonly FrozenWordSet Portuguese = new(
    [
        "a", "à", "ao", "aos", "aquela", "aquelas", "aquele", "aqueles", "aquilo", "as", "às", "até", "com", "como",
        "da", "das", "de", "dela", "delas", "dele", "deles", "depois", "do", "dos", "e", "é", "ela", "elas", "ele",
        "eles", "em", "entre", "era", "eram", "éramos", "essa", "essas", "esse", "esses", "esta", "está", "estamos",
        "estão", "estar", "estas", "estava", "estavam", "estávamos", "este", "esteja", "estejam", "estejamos",
        "estes", "esteve", "estive", "estivemos", "estiver", "estivera", "estiveram", "estiverem", "estivermos",
        "estivesse", "estivessem", "estou", "eu", "foi", "fomos", "for", "fora", "foram", "forem", "formos",
        "fosse", "fossem", "fui", "há", "haja", "hajam", "hajamos", "hão", "havemos", "haver", "hei", "houve",
        "houvemos", "houver", "houvera", "houveram", "houverei", "houverem", "houveremos", "houveria",
        "houveriam", "houvermos", "houvesse", "houvessem", "isso", "isto", "já", "lhe", "lhes", "mais", "mas",
        "me", "mesmo", "meu", "meus", "minha", "minhas", "muito", "na", "não", "nas", "nem", "no", "nos", "nós", "nossa",
        "nossas", "nosso", "nossos", "num", "numa", "o", "os", "ou", "para", "pela", "pelas", "pelo", "pelos",
        "por", "qual", "quando", "que", "quem", "são", "se", "seja", "sejam", "sejamos", "sem", "ser", "será",
        "serão", "serei", "seremos", "seria", "seriam", "seríamos", "seu", "seus", "só", "somos", "sou", "sua",
        "suas", "também", "te", "tem", "têm", "temos", "tenha", "tenham", "tenhamos", "tenho", "terá", "terão",
        "terei", "teremos", "teria", "teriam", "teríamos", "teu", "teus", "teve", "tinha", "tinham", "tínhamos",
        "tive", "tivemos", "tiver", "tivera", "tiveram", "tiverem", "tivermos", "tivesse", "tivessem", "tu",
        "tua", "tuas", "um", "uma", "umas", "uns", "você", "vocês", "vos",
    ]);

    public static readonly FrozenWordSet English = new(
    [
        "a", "about", "above", "after", "again", "against", "all", "am", "an", "and", "any", "are", "as", "at",
        "be", "because", "been", "before", "being", "below", "between", "both", "but", "by", "can", "could", "did",
        "do", "does", "doing", "down", "during", "each", "few", "for", "from", "further", "had", "has", "have",
        "having", "he", "her", "here", "hers", "herself", "him", "himself", "his", "how", "i", "if", "in", "into",
        "is", "it", "its", "itself", "just", "me", "more", "most", "my", "myself", "no", "nor", "not", "now", "of",
        "off", "on", "once", "only", "or", "other", "our", "ours", "ourselves", "out", "over", "own", "same", "she",
        "should", "so", "some", "such", "than", "that", "the", "their", "theirs", "them", "themselves", "then",
        "there", "these", "they", "this", "those", "through", "to", "too", "under", "until", "up", "very", "was",
        "we", "were", "what", "when", "where", "which", "while", "who", "whom", "why", "will", "with", "would",
        "you", "your", "yours", "yourself", "yourselves",
    ]);

    internal static readonly FrozenWordSet PortugueseMarkers = new(
    [
        "de", "que", "não", "para", "com", "uma", "os", "das", "dos", "ao", "pela", "pelo", "são", "está",
        "também", "mais", "como", "mas", "foi", "ele", "ela", "isso", "entre", "sem", "quando", "muito", "já",
        "seu", "sua", "ou", "ser", "este", "esta", "nas", "nos", "às", "é", "em", "um", "por", "se", "na",
    ]);

    internal static readonly FrozenWordSet EnglishMarkers = new(
    [
        "the", "and", "of", "to", "is", "that", "it", "with", "for", "was", "on", "are", "be", "this", "by",
        "from", "have", "not", "but", "which", "or", "an", "they", "you", "at", "were", "has", "their", "will",
        "can", "been", "would", "in", "what", "when", "there", "its", "we",
    ]);

    private static readonly FrozenWordSet PortugueseFolded = Portuguese.Folded();

    private static readonly FrozenWordSet EnglishFolded = English.Folded();

    public static FrozenWordSet For(Language language) => language switch
    {
        Language.English => English,
        _ => Portuguese,
    };

    public static FrozenWordSet FoldedFor(Language language) => language switch
    {
        Language.English => EnglishFolded,
        _ => PortugueseFolded,
    };
}

public sealed class FrozenWordSet
{
    private readonly HashSet<string> _words;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public FrozenWordSet(IEnumerable<string> words)
    {
        _words = new HashSet<string>(words, StringComparer.Ordinal);
        _lookup = _words.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public FrozenWordSet Folded() => new(_words.Select(CharFolding.Fold));

    public int Count => _words.Count;

    public bool Contains(ReadOnlySpan<char> word) => _lookup.Contains(word);

    public bool Contains(string word) => _words.Contains(word);

    public IEnumerable<string> Words => _words;
}
