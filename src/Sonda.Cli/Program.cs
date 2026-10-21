using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sonda.Analysis;
using Sonda.Cli;
using Sonda.Engine;
using Sonda.Engine.Crawling;
using Sonda.Engine.Index;
using Sonda.Engine.Search;
using Sonda.Storage;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "ajuda";
var arguments = new Arguments(args.Skip(1));
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    return command switch
    {
        "indexar" or "index" => await Commands.IndexAsync(arguments, cancellation.Token),
        "buscar" or "search" => Commands.Search(arguments),
        "estatisticas" or "estatísticas" or "stats" => Commands.Stats(arguments),
        "termos" or "terms" => Commands.Terms(arguments),
        "postagens" or "postings" => Commands.Postings(arguments),
        "analisar" or "analyze" => Commands.Analyze(arguments),
        "otimizar" or "optimize" => await Commands.OptimizeAsync(arguments, cancellation.Token),
        "verificar" or "check" => Commands.Verify(arguments),
        "remover-fonte" or "remove-source" => Commands.RemoveSource(arguments),
        "benchmark" or "bench" => await Benchmark.RunAsync(arguments, cancellation.Token),
        "versao" or "versão" or "version" => Commands.Version(),
        _ => Commands.Help(),
    };
}
catch (IndexLockedException ex)
{
    Output.Error(ex.Message + " Feche o servidor ou use os comandos de leitura (buscar, estatisticas, termos).");
    return 3;
}
catch (QueryParseException ex)
{
    Output.Error("Consulta inválida: " + ex.Message);
    return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
{
    Output.Error(ex.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Output.Error("Operação cancelada.");
    return 130;
}

internal static class Commands
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static SondaOptions Options(Arguments args)
    {
        var language = args.Get("idioma", "language")?.ToLowerInvariant() switch
        {
            "pt" or "portugues" or "português" => StemmingMode.Portuguese,
            "en" or "ingles" or "inglês" or "english" => StemmingMode.English,
            "nenhum" or "none" => StemmingMode.None,
            _ => StemmingMode.Auto,
        };

        return new SondaOptions
        {
            IndexPath = args.Get("indice", "índice", "index") ?? Environment.GetEnvironmentVariable("SONDA_INDICE") ?? Path.Combine("dados", "indice"),
            Writer = new IndexWriterOptions
            {
                RamBufferMb = args.GetInt(128, "ram"),
                StoreBody = !args.Flag("sem-trechos", "no-snippets"),
                Analyzer = new AnalyzerOptions { Stemming = language },
            },
            Indexing = new IndexingOptions
            {
                Parallelism = args.GetInt(0, "paralelismo", "parallelism"),
                MaxFileBytes = args.GetInt(32, "max-mb") * 1024L * 1024L,
            },
        };
    }

    private static SondaEngine OpenForReading(Arguments args)
    {
        var options = Options(args);
        if (!Directory.Exists(options.IndexPath))
        {
            throw new DirectoryNotFoundException($"Índice não encontrado em {Path.GetFullPath(options.IndexPath)}. Rode \"sonda indexar <pasta>\" primeiro.");
        }

        return SondaEngine.OpenReadOnly(options);
    }

    public static async Task<int> IndexAsync(Arguments args, CancellationToken cancellationToken)
    {
        var folder = args.Positional(0) ?? throw new ArgumentException("Informe a pasta: sonda indexar <pasta> [--fonte nome]");
        var options = Options(args);
        var name = args.Get("fonte", "source") ?? Slug(Path.GetFileName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (!SourceDefinition.IsValidName(name))
        {
            throw new ArgumentException("O nome da fonte aceita só letras minúsculas, números, hífen e sublinhado (até 40 caracteres).");
        }

        var source = new SourceDefinition
        {
            Name = name,
            Path = folder,
            Include = args.GetList(SourceDefinition.DefaultInclude, "incluir", "include"),
            Exclude = args.GetList(SourceDefinition.DefaultExclude, "excluir", "exclude"),
        };
        var mode = args.Flag("completo", "full") ? IndexingMode.Full : IndexingMode.Incremental;

        using var engine = SondaEngine.Open(options);
        Output.Title($"Indexando \"{source.Name}\" ({(mode == IndexingMode.Full ? "completa" : "incremental")})");
        Output.Line($"Pasta:  {Path.GetFullPath(folder)}");
        Output.Line($"Índice: {Path.GetFullPath(options.IndexPath)}");
        Output.Line();

        var progress = new Progress<IndexingProgress>(p =>
        {
            if (!Console.IsOutputRedirected)
            {
                var line = $"  {p.Phase,-18} {p.Processed,9:N0} lidos  {p.Added + p.Updated,9:N0} indexados  {p.Unchanged,8:N0} sem mudança  {p.FilesPerSecond,7:N0} arq/s  {p.MegabytesPerSecond,6:N1} MB/s";
                Console.Write("\r" + line.PadRight(Math.Max(0, Console.WindowWidth - 1)));
            }
        });

        var result = await engine.IndexAsync(source, mode, progress, cancellationToken);
        Console.WriteLine();
        Output.Line();
        Output.Table(
            ["Encontrados", "Novos", "Atualizados", "Sem mudança", "Removidos", "Falhas", "Lidos", "Tempo"],
            [[
                result.Discovered.ToString("N0"),
                result.Added.ToString("N0"),
                result.Updated.ToString("N0"),
                result.Unchanged.ToString("N0"),
                result.Deleted.ToString("N0"),
                result.Failed.ToString("N0"),
                Output.Bytes(result.Bytes),
                $"{result.ElapsedSeconds:N2} s",
            ]]);

        foreach (var error in result.Errors.Take(10))
        {
            Output.Warning($"{error.Path}: {error.Message}");
        }

        if (result.Message is not null)
        {
            Output.Warning(result.Message);
        }

        return result.Status == "falhou" ? 1 : 0;
    }

    public static int Search(Arguments args)
    {
        var text = string.Join(' ', args.Positionals);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Informe a consulta: sonda buscar \"motor de busca\"");
        }

        using var engine = OpenForReading(args);
        var request = new SearchRequest
        {
            Query = text,
            Page = args.GetInt(1, "pagina", "página", "page"),
            PageSize = args.GetInt(10, "top", "quantidade", "size"),
            Similarity = args.Flag("tfidf") ? "tfidf" : args.Get("similaridade", "similarity"),
            Operator = args.Flag("ou", "or") ? DefaultOperator.Or : args.Flag("e", "and") ? DefaultOperator.And : null,
            Explain = args.Flag("explicar", "explain"),
            Sort = args.Flag("recentes", "recent") ? SortMode.Recent : SortMode.Relevance,
            Extension = args.Get("tipo", "ext"),
            Language = args.Get("idioma-doc", "lang"),
            Source = args.Get("fonte", "source"),
            Facets = true,
        };

        var response = engine.Search(request);
        if (args.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(response, Json));
            return 0;
        }

        var total = response.TotalIsLowerBound ? $"{response.Total:N0}+" : response.Total.ToString("N0");
        Output.Title($"{total} resultado(s) em {response.ElapsedMs:N1} ms · {response.Similarity}");
        Output.Dim($"Consulta interpretada: {response.ParsedQuery}");
        if (response.Relaxed)
        {
            Output.Warning("Nenhum documento tinha todos os termos; mostrando os que têm parte deles.");
        }

        Output.Line();
        var rank = ((response.Page - 1) * response.PageSize) + 1;
        foreach (var hit in response.Hits)
        {
            Console.Write($"{rank,3}. ");
            Output.Fragments(hit.TitleFragments, ConsoleColor.White);
            Console.WriteLine();
            Output.Dim($"     {hit.Source}:{hit.Path} · {hit.Extension} · {hit.Language} · {Output.Bytes(hit.Size)} · pontuação {hit.Score:0.###}");
            if (hit.Snippet.Count > 0)
            {
                Console.Write("     ");
                Output.Fragments(hit.Snippet, ConsoleColor.Gray);
                Console.WriteLine();
            }

            if (hit.Explanation is not null)
            {
                foreach (var line in hit.Explanation.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Output.Dim("       " + line);
                }
            }

            Output.Line();
            rank++;
        }

        if (response.Facets.Count > 0)
        {
            foreach (var (field, counts) in response.Facets.Where(f => f.Value.Count > 0))
            {
                Output.Dim($"{field}: " + string.Join("  ", counts.Take(8).Select(c => $"{c.Key} ({c.Value})")));
            }
        }

        return 0;
    }

    public static int Stats(Arguments args)
    {
        using var engine = OpenForReading(args);
        var stats = engine.GetStatistics();
        if (args.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(stats, Json));
            return 0;
        }

        Output.Title("Índice");
        Output.Line($"Pasta:            {stats.Path}");
        Output.Line($"Documentos:       {stats.Documents:N0} ({stats.Deleted:N0} excluídos aguardando limpeza)");
        Output.Line($"Tamanho em disco: {Output.Bytes(stats.SizeBytes)}");
        Output.Line($"Geração:          segments_{stats.Generation}");
        Output.Line($"Última gravação:  {stats.LastCommit?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "nunca"}");
        Output.Line();
        Output.Table(
            ["Segmento", "Documentos", "Excluídos", "Tamanho", "Origem", "Criado em"],
            stats.Segments.Select(s => new[] { s.Name, s.Documents.ToString("N0"), s.Deleted.ToString("N0"), Output.Bytes(s.SizeBytes), s.Origin, s.CreatedAt.ToLocalTime().ToString("dd/MM HH:mm:ss") }).ToList());
        Output.Line();
        Output.Table(
            ["Campo", "Termos", "Documentos", "Tokens", "Tamanho médio"],
            stats.Fields.Select(f => new[] { f.Field, f.Terms.ToString("N0"), f.Documents.ToString("N0"), f.Tokens.ToString("N0"), f.AverageLength.ToString("N1") }).ToList());
        Output.Line();
        Output.Dim("Por fonte:   " + string.Join("  ", stats.BySource.Select(c => $"{c.Key} ({c.Value:N0})")));
        Output.Dim("Por idioma:  " + string.Join("  ", stats.ByLanguage.Select(c => $"{c.Key} ({c.Value:N0})")));
        Output.Dim("Por tipo:    " + string.Join("  ", stats.ByExtension.Select(c => $"{c.Key} ({c.Value:N0})")));
        return 0;
    }

    private static int ResolveField(Arguments args, int fallback)
    {
        var name = args.Get("campo", "field");
        if (name is null)
        {
            return fallback;
        }

        return Schema.TryResolveField(name, out var field) ? field : throw new ArgumentException($"Campo desconhecido: {name}.");
    }

    public static int Terms(Arguments args)
    {
        using var engine = OpenForReading(args);
        var field = ResolveField(args, Schema.Body);
        var byFrequency = args.Flag("frequencia", "frequency");
        var terms = engine.Terms(field, args.Get("prefixo", "prefix"), args.GetInt(30, "limite", "limit"), byFrequency);
        Output.Title($"Dicionário do campo {Schema.NameOf(field)}{(byFrequency ? " (mais frequentes)" : string.Empty)}");
        Output.Table(
            ["Termo", "Documentos (df)", "Ocorrências", "IDF (BM25)"],
            terms.Select(t => new[] { t.Term, t.DocFreq.ToString("N0"), t.TotalTermFreq.ToString("N0"), t.Idf.ToString("0.000") }).ToList());
        return 0;
    }

    public static int Postings(Arguments args)
    {
        var term = args.Positional(0) ?? throw new ArgumentException("Informe o termo: sonda postagens <termo> [--campo corpo]");
        using var engine = OpenForReading(args);
        var field = ResolveField(args, Schema.Body);
        var postings = engine.Postings(field, term, args.GetInt(20, "limite", "limit"));
        if (postings is null)
        {
            Output.Warning($"O termo \"{term}\" não existe no campo {Schema.NameOf(field)}. Use \"sonda analisar\" para ver como uma palavra é indexada.");
            return 1;
        }

        Output.Title($"{postings.Field}:{postings.Term} · df {postings.DocFreq:N0} · ocorrências {postings.TotalTermFreq:N0} · idf {postings.Idf:0.000}");
        Output.Table(
            ["Segmento", "df", "Ocorrências", "Blocos", "Ponteiro .doc", "Ponteiro .pos", "Freq. máx.", "Menor campo"],
            postings.Segments.Select(s => new[] { s.Segment, s.DocFreq.ToString("N0"), s.TotalTermFreq.ToString("N0"), s.Singleton ? "único" : s.SkipBlocks.ToString("N0"), s.DocPointer.ToString("N0"), s.PositionPointer.ToString("N0"), s.MaxFreq.ToString("N0"), s.MinLength.ToString("N0") }).ToList());
        Output.Line();
        Output.Table(
            ["Documento", "Freq.", "Posições"],
            postings.Postings.Select(p => new[] { $"{p.Key}", p.Freq.ToString("N0"), string.Join(", ", p.Positions.Take(12)) + (p.Positions.Count > 12 ? " …" : string.Empty) }).ToList());
        return 0;
    }

    public static int Analyze(Arguments args)
    {
        var text = string.Join(' ', args.Positionals);
        var analyzer = new TextAnalyzer(Options(args).Writer.Analyzer);
        var language = analyzer.ResolveLanguage(text, Language.Unknown);
        Output.Title($"Análise ({language.ToCode()})");
        var spans = Tokenizer.Tokenize(text);
        var rows = new List<string[]>();
        for (var i = 0; i < spans.Count; i++)
        {
            var original = text.Substring(spans[i].Start, spans[i].Length);
            var lower = original.ToLowerInvariant();
            var indexed = analyzer.AnalyzeWord(original, language);
            rows.Add(
            [
                i.ToString(CultureInfo.InvariantCulture),
                original,
                lower,
                indexed is null ? "sim" : "não",
                analyzer.AnalyzeWord(original, Language.Portuguese) ?? "—",
                analyzer.AnalyzeWord(original, Language.English) ?? "—",
                indexed ?? "(descartado)",
            ]);
        }

        Output.Table(["Pos.", "Token", "Minúsculas", "Stopword", "Radical PT (RSLP)", "Radical EN (Porter)", "Termo indexado"], rows);
        return 0;
    }

    public static async Task<int> OptimizeAsync(Arguments args, CancellationToken cancellationToken)
    {
        using var engine = SondaEngine.Open(Options(args));
        var before = engine.GetStatistics();
        var merges = await engine.OptimizeAsync(args.GetInt(1, "segmentos", "segments"), cancellationToken);
        var after = engine.GetStatistics();
        Output.Line($"{merges} mesclagem(ns). Segmentos: {before.Segments.Count} → {after.Segments.Count}. Tamanho: {Output.Bytes(before.SizeBytes)} → {Output.Bytes(after.SizeBytes)}.");
        return 0;
    }

    public static int Verify(Arguments args)
    {
        using var engine = OpenForReading(args);
        var result = engine.Verify();
        Output.Line($"{result.Valid}/{result.Files} arquivos com checksum CRC32 válido em {result.ElapsedMs:N0} ms.");
        foreach (var problem in result.Problems)
        {
            Output.Error(problem);
        }

        return result.Problems.Count == 0 ? 0 : 4;
    }

    public static int RemoveSource(Arguments args)
    {
        var name = args.Positional(0) ?? throw new ArgumentException("Informe a fonte: sonda remover-fonte <nome>");
        using var engine = SondaEngine.Open(Options(args));
        var removed = engine.RemoveSource(name);
        Output.Line($"{removed:N0} documento(s) da fonte \"{name}\" removido(s).");
        return 0;
    }

    public static int Version()
    {
        var version = typeof(SondaEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        Output.Line($"Sonda {version}");
        return 0;
    }

    public static int Help()
    {
        Output.Title("Sonda, motor de busca de texto completo");
        Output.Line("Uso: sonda <comando> [opções]");
        Output.Line();
        Output.Table(
            ["Comando", "O que faz"],
            [
                ["indexar <pasta>", "Indexa .txt, .md e .html da pasta (incremental). --fonte, --incluir, --excluir, --completo, --paralelismo, --idioma"],
                ["buscar \"consulta\"", "Busca com BM25. --top, --pagina, --tfidf, --ou, --explicar, --recentes, --tipo, --fonte, --json"],
                ["estatisticas", "Documentos, segmentos, campos e tamanho do índice. --json"],
                ["termos", "Lista o dicionário de termos. --campo, --prefixo, --limite, --frequencia"],
                ["postagens <termo>", "Mostra a lista invertida de um termo: segmentos, ponteiros, documentos e posições"],
                ["analisar \"texto\"", "Mostra tokenização, stopwords e radicais RSLP e Porter"],
                ["otimizar", "Mescla os segmentos e limpa excluídos. --segmentos"],
                ["verificar", "Confere o checksum CRC32 de todos os arquivos do índice"],
                ["remover-fonte <nome>", "Remove do índice todos os documentos de uma fonte"],
                ["benchmark", "Gera um corpus sintético e mede indexação e consultas. --docs, --consultas"],
            ]);
        Output.Line();
        Output.Line("Opção comum: --indice <pasta> (padrão: dados/indice ou a variável SONDA_INDICE).");
        return 0;
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in CharFolding.Fold(value))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "documentos" : slug[..Math.Min(40, slug.Length)];
    }
}
