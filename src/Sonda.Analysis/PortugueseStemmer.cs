namespace Sonda.Analysis;

public interface IStemmer
{
    Language Language { get; }

    string Stem(string word);
}

public sealed class PortugueseStemmer : IStemmer
{
    private sealed record Rule(string Suffix, int MinStem, string Replacement, HashSet<string> Exceptions);

    private static Rule R(string suffix, int minStem, string replacement = "", params string[] exceptions)
        => new(
            CharFolding.Fold(suffix),
            minStem,
            CharFolding.Fold(replacement),
            new HashSet<string>(exceptions.Select(CharFolding.Fold), StringComparer.Ordinal));

    private static readonly Rule[] Plural =
    [
        R("ns", 1, "m"),
        R("ões", 3, "ão"),
        R("ães", 1, "ão", "mãe"),
        R("ais", 1, "al", "cais", "mais"),
        R("éis", 2, "el"),
        R("eis", 2, "el"),
        R("óis", 2, "ol", "depois"),
        R("is", 2, "il", "lápis", "cais", "mais", "crúcis", "biquínis", "pois", "depois", "dois", "leis"),
        R("les", 3, "l"),
        R("res", 3, "r", "árvores"),
        R("s", 2, "", "aliás", "pires", "lápis", "cais", "mais", "mas", "menos", "férias", "fezes", "pêsames",
            "crúcis", "gás", "atrás", "moisés", "através", "convés", "ês", "país", "após", "ambas", "ambos",
            "messias", "depois"),
    ];

    private static readonly Rule[] Feminine =
    [
        R("ona", 3, "ão", "abandona", "lona", "iona", "cortisona", "monótona", "maratona", "acetona", "detona", "carona"),
        R("ora", 3, "or"),
        R("na", 4, "no", "carona", "abandona", "lona", "iona", "cortisona", "monótona", "maratona", "acetona",
            "detona", "guiana", "campana", "grana", "caravana", "banana", "paisana"),
        R("inha", 3, "inho", "rainha", "linha", "minha"),
        R("esa", 3, "ês", "mesa", "obesa", "princesa", "turquesa", "ilesa", "pesa", "presa"),
        R("osa", 3, "oso", "mucosa", "prosa"),
        R("íaca", 3, "íaco"),
        R("ica", 3, "ico", "dica"),
        R("ada", 2, "ado", "pitada"),
        R("ida", 3, "ido", "vida", "dúvida"),
        R("ída", 3, "ido", "recaída", "saída"),
        R("ima", 3, "imo", "vítima"),
        R("iva", 3, "ivo", "saliva", "oliva"),
        R("eira", 3, "eiro", "beira", "cadeira", "frigideira", "bandeira", "feira", "capoeira", "barreira",
            "fronteira", "besteira", "poeira"),
    ];

    private static readonly Rule[] Augmentative =
    [
        R("díssimo", 5),
        R("abilíssimo", 5),
        R("íssimo", 3),
        R("ésimo", 3),
        R("érrimo", 4),
        R("zinho", 2),
        R("quinho", 4, "c"),
        R("uinho", 4),
        R("adinho", 3),
        R("inho", 3, "", "caminho", "cominho"),
        R("alhão", 4),
        R("uça", 4),
        R("aço", 4, "", "antebraço"),
        R("aça", 4),
        R("adão", 4),
        R("idão", 4),
        R("ázio", 3, "", "topázio"),
        R("arraz", 4),
        R("zarrão", 3),
        R("arrão", 4),
        R("zão", 2, "", "coalizão"),
        R("ão", 3, "", "camarão", "chimarrão", "canção", "coração", "embrião", "grotão", "glutão", "ficção",
            "fogão", "feição", "furacão", "gamão", "lampião", "leão", "macacão", "nação", "órfão", "orgão",
            "patrão", "portão", "quinhão", "rincão", "tração", "falcão", "espião", "mamão", "folião", "cordão",
            "aptidão", "campeão", "colchão", "limão", "leilão", "melão", "barão", "milhão", "bilhão", "fusão",
            "cristão", "ilusão", "capitão", "estação", "senão"),
    ];

    private static readonly Rule[] Adverb =
    [
        R("mente", 4, "", "experimente"),
    ];

    private static readonly Rule[] Noun =
    [
        R("encialista", 4),
        R("alista", 5),
        R("agem", 3, "", "coragem", "chantagem", "vantagem", "carruagem"),
        R("iamento", 4),
        R("amento", 3, "", "firmamento", "fundamento", "departamento"),
        R("imento", 3),
        R("mento", 6, "", "firmamento", "elemento", "complemento", "instrumento", "departamento"),
        R("alizado", 4),
        R("atizado", 4),
        R("tizado", 4, "", "alfabetizado"),
        R("izado", 5, "", "organizado", "pulverizado"),
        R("ativo", 4, "", "pejorativo", "relativo"),
        R("tivo", 4, "", "relativo"),
        R("ivo", 4, "", "passivo", "possessivo", "pejorativo", "positivo"),
        R("ado", 2, "", "grado"),
        R("ido", 3, "", "cândido", "consolido", "rápido", "decido", "tímido", "duvido", "marido"),
        R("ador", 3),
        R("edor", 3),
        R("idor", 4, "", "ouvidor"),
        R("dor", 4, "", "ouvidor"),
        R("sor", 4, "", "assessor"),
        R("atoria", 5),
        R("tor", 3, "", "benfeitor", "leitor", "editor", "pastor", "produtor", "promotor", "consultor"),
        R("ior", 2, "", "exterior", "superior", "anterior", "interior", "inferior"),
        R("abilidade", 5),
        R("icionista", 4),
        R("cionista", 5),
        R("ionista", 5),
        R("ionar", 5),
        R("ional", 4),
        R("ência", 3),
        R("ância", 4, "", "ambulância"),
        R("edouro", 3),
        R("queiro", 3, "c"),
        R("adeiro", 4, "", "desfiladeiro"),
        R("eiro", 3, "", "desfiladeiro", "pioneiro", "mosteiro"),
        R("uoso", 3),
        R("oso", 3, "", "precioso"),
        R("alizaç", 5),
        R("atizaç", 5),
        R("tizaç", 5),
        R("izaç", 5, "", "organizaç"),
        R("aç", 3, "", "equaç", "relaç"),
        R("iç", 3, "", "eleiç"),
        R("ário", 3, "", "voluntário", "salário", "aniversário", "diário", "lionário", "armário"),
        R("atório", 3),
        R("rio", 5, "", "voluntário", "salário", "aniversário", "diário", "compulsório", "lionário", "próprio",
            "stério", "armário"),
        R("ério", 6),
        R("ês", 4),
        R("eza", 3),
        R("ez", 4),
        R("esco", 4),
        R("ante", 2, "", "gigante", "elefante", "adiante", "possante", "instante", "restaurante"),
        R("ástico", 4, "", "eclesiástico"),
        R("alístico", 3),
        R("áutico", 4),
        R("êutico", 4),
        R("tico", 3, "", "político", "eclesiástico", "diagnostico", "prático", "doméstico", "diagnóstico",
            "idêntico", "alopático", "artístico", "autêntico", "eclético", "crítico", "critico"),
        R("ico", 4, "", "tico", "público", "explico"),
        R("ividade", 5),
        R("idade", 4, "", "autoridade", "comunidade"),
        R("oria", 4, "", "categoria"),
        R("encial", 5),
        R("ista", 4),
        R("auta", 5),
        R("quice", 4, "c"),
        R("ice", 4, "", "cúmplice"),
        R("íaco", 3),
        R("ente", 4, "", "freqüente", "alimente", "acrescente", "permanente", "oriente", "aparente"),
        R("ense", 5),
        R("inal", 3),
        R("ano", 4),
        R("ável", 2, "", "afável", "razoável", "potável", "vulnerável"),
        R("ível", 3, "", "possível"),
        R("vel", 5, "", "possível", "vulnerável", "solúvel"),
        R("bil", 3, "vel"),
        R("ura", 4, "", "imatura", "acupuntura", "costura"),
        R("ural", 4),
        R("ual", 3, "", "bissexual", "virtual", "visual", "pontual"),
        R("ial", 3),
        R("al", 4, "", "afinal", "animal", "estatal", "bissexual", "desleal", "fiscal", "formal", "pessoal",
            "liberal", "postal", "virtual", "visual", "pontual", "sideral", "sucursal"),
        R("alismo", 4),
        R("ivismo", 4),
        R("ismo", 3, "", "cinismo"),
    ];

    private static readonly Rule[] Verb =
    [
        R("aríamo", 2), R("ássemo", 2), R("eríamo", 2), R("êssemo", 2), R("iríamo", 3), R("íssemo", 3),
        R("áramo", 2), R("árei", 2), R("aremo", 2), R("ariam", 2), R("aríei", 2), R("ássei", 2), R("assem", 2),
        R("ávamo", 2), R("êramo", 3), R("eremo", 3), R("eriam", 3), R("eríei", 3), R("êssei", 3), R("essem", 3),
        R("íramo", 3), R("iremo", 3), R("iriam", 3), R("iríei", 3), R("íssei", 3), R("issem", 3),
        R("ando", 2), R("endo", 3), R("indo", 3), R("ondo", 3), R("aram", 2), R("arão", 2), R("arde", 2),
        R("arei", 2), R("arem", 2), R("aria", 2), R("armo", 2), R("asse", 2), R("aste", 2),
        R("avam", 2, "", "agravam"), R("ávei", 2), R("eram", 3), R("erão", 3), R("erde", 3), R("erei", 3),
        R("êrei", 3), R("erem", 3), R("eria", 3), R("ermo", 3), R("esse", 3), R("este", 3, "", "faroeste", "agreste"),
        R("íamo", 3), R("iram", 3), R("íram", 3), R("irão", 2), R("irde", 2), R("irei", 3, "", "admirei"),
        R("irem", 3, "", "adquirem"), R("iria", 3), R("irmo", 3), R("isse", 3), R("iste", 4),
        R("iava", 4, "", "ampliava"), R("amo", 2), R("iona", 3), R("ara", 2, "", "arara", "prepara"),
        R("ará", 2, "", "alvará"), R("are", 2, "", "prepare"), R("ava", 2, "", "agrava"), R("emo", 2),
        R("era", 3, "", "acelera", "espera"), R("erá", 3), R("ere", 3, "", "espere"),
        R("iam", 3, "", "enfiam", "ampliam", "elogiam", "ensaiam"), R("íei", 3),
        R("imo", 3, "", "reprimo", "intimo", "íntimo", "nimo", "queimo", "ximo"), R("ira", 3, "", "fronteira", "sátira"),
        R("ído", 3), R("irá", 3), R("tizar", 4, "", "alfabetizar"), R("izar", 5, "", "organizar"),
        R("itar", 5, "", "acreditar", "explicitar", "estreitar"), R("ire", 3, "", "adquire"), R("omo", 3),
        R("ai", 2), R("am", 2), R("ear", 4, "", "alardear", "nuclear"), R("ar", 2, "", "azar", "bazaar", "patamar"),
        R("uei", 3), R("uía", 5, "u"), R("ei", 3), R("guem", 3, "g"), R("em", 2, "", "alem", "virgem"),
        R("er", 2, "", "éter", "pier"), R("eu", 3, "", "chapeu"),
        R("ia", 3, "", "estória", "fatia", "acia", "praia", "elogia", "mania", "lábia", "aprecia", "polícia",
            "arredia", "cheia", "ásia"),
        R("ir", 3, "", "freir"), R("iu", 3), R("eou", 5), R("ou", 3), R("i", 3),
    ];

    private static readonly Rule[] Vowel =
    [
        R("bil", 2, "vel"),
        R("gue", 2, "g", "gangue", "jegue"),
        R("á", 3),
        R("ê", 3, "", "bebê"),
        R("a", 3, "", "ásia"),
        R("e", 3),
        R("o", 3, "", "ão"),
    ];

    public Language Language => Language.Portuguese;

    public string Stem(string word)
    {
        word = CharFolding.Fold(word);
        if (word.Length < 3)
        {
            return word;
        }

        if (word[^1] == 's')
        {
            word = Apply(word, Plural);
        }

        if (word.Length > 0 && word[^1] == 'a')
        {
            word = Apply(word, Feminine);
        }

        word = Apply(word, Augmentative);
        word = Apply(word, Adverb);

        var beforeNoun = word;
        word = Apply(word, Noun);
        if (ReferenceEquals(word, beforeNoun))
        {
            var beforeVerb = word;
            word = Apply(word, Verb);
            if (ReferenceEquals(word, beforeVerb))
            {
                word = Apply(word, Vowel);
            }
        }

        return word.Length == 0 ? beforeNoun : word;
    }

    private static string Apply(string word, Rule[] rules)
    {
        foreach (var rule in rules)
        {
            var suffix = rule.Suffix;
            if (word.Length < suffix.Length + rule.MinStem || !word.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.Exceptions.Count > 0 && rule.Exceptions.Contains(word))
            {
                continue;
            }

            return string.Concat(word.AsSpan(0, word.Length - suffix.Length), rule.Replacement);
        }

        return word;
    }
}
