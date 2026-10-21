using System.Diagnostics;
using System.Text;
using Sonda.Engine;
using Sonda.Engine.Index;
using Sonda.Engine.Search;

namespace Sonda.Cli;

internal static class Benchmark
{
    private static readonly string[] Syllables =
    [
        "ba", "be", "bi", "bo", "ca", "ce", "co", "cu", "da", "de", "di", "do", "fa", "fe", "fi", "fo", "ga", "go",
        "la", "le", "li", "lo", "lu", "ma", "me", "mi", "mo", "na", "ne", "ni", "no", "pa", "pe", "pi", "po", "ra",
        "re", "ri", "ro", "sa", "se", "si", "so", "ta", "te", "ti", "to", "va", "ve", "vi", "vo", "tra", "pro", "cri",
    ];

    private static readonly string[] Endings = ["", "", "", "s", "r", "ção", "mente", "dade", "ado", "ar", "or", "ente"];

    public static async Task<int> RunAsync(Arguments args, CancellationToken cancellationToken)
    {
        var docCount = Math.Max(1000, args.GetInt(50_000, "docs"));
        var queryCount = Math.Max(10, args.GetInt(500, "consultas", "queries"));
        var keep = args.Flag("manter", "keep");
        var path = args.Get("indice", "index") ?? Path.Combine(Path.GetTempPath(), "sonda-benchmark-" + Guid.NewGuid().ToString("N")[..8]);
        var parallelism = args.GetInt(Environment.ProcessorCount, "paralelismo", "parallelism");

        var vocabulary = BuildVocabulary(new Random(7), 40_000);
        var sampler = new ZipfSampler(vocabulary.Length, 1.05);

        Output.Title($"Benchmark: {docCount:N0} documentos sintéticos, {queryCount:N0} consultas, {parallelism} threads");
        Output.Dim($"Índice temporário: {path}");
        Output.Line();

        var options = new SondaOptions
        {
            IndexPath = path,
            Writer = new IndexWriterOptions { RamBufferMb = args.GetInt(256, "ram"), AutoMerge = false },
        };

        long characters = 0;
        double indexingSeconds;
        double commitSeconds;
        IndexStatistics stats;
        using (var engine = SondaEngine.Open(options))
        {
            var writer = engine.Writer!;
            var stopwatch = Stopwatch.StartNew();
            await Task.Run(() => Parallel.For(0, docCount, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, i =>
            {
                var document = Generate(i, vocabulary, sampler);
                writer.Add(document);
                Interlocked.Add(ref characters, document.Body.Length + document.Title.Length);
            }), cancellationToken);
            indexingSeconds = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
            writer.Commit();
            commitSeconds = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
            await engine.OptimizeAsync(1, cancellationToken);
            var mergeSeconds = stopwatch.Elapsed.TotalSeconds;
            stats = engine.GetStatistics();

            var megabytes = characters / 1024d / 1024d;
            Output.Table(
                ["Indexação", "Docs/s", "MB/s", "Gravação", "Mesclagem", "Índice", "Termos (corpo)"],
                [[
                    $"{indexingSeconds:N2} s",
                    (docCount / indexingSeconds).ToString("N0"),
                    (megabytes / indexingSeconds).ToString("N1"),
                    $"{commitSeconds:N2} s",
                    $"{mergeSeconds:N2} s",
                    Output.Bytes(stats.SizeBytes),
                    stats.Fields.First(f => f.Field == "body").Terms.ToString("N0"),
                ]]);
            Output.Line();

            var random = new Random(99);
            var queries = Enumerable.Range(0, queryCount).Select(_ =>
            {
                var words = 1 + random.Next(3);
                return string.Join(' ', Enumerable.Range(0, words).Select(_ => vocabulary[sampler.Sample(random, 30, 4000)]));
            }).ToArray();

            var rows = new List<string[]>();
            foreach (var (label, request) in new (string, SearchRequest)[]
            {
                ("OR exato (contagem + facetas)", new SearchRequest { Operator = DefaultOperator.Or, Facets = true, ExactTotal = true, Snippets = false }),
                ("OR top-10 com WAND", new SearchRequest { Operator = DefaultOperator.Or, Facets = false, ExactTotal = false, Snippets = false }),
                ("AND (conjunção com saltos)", new SearchRequest { Operator = DefaultOperator.And, Facets = false, ExactTotal = true, Snippets = false }),
                ("AND com trechos destacados", new SearchRequest { Operator = DefaultOperator.And, Facets = true, ExactTotal = true, Snippets = true }),
            })
            {
                foreach (var query in queries.Take(20))
                {
                    engine.Search(request with { Query = query }, cancellationToken);
                }

                var latencies = new double[queries.Length];
                long evaluated = 0;
                long hits = 0;
                for (var i = 0; i < queries.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var watch = Stopwatch.StartNew();
                    var response = engine.Search(request with { Query = queries[i] }, cancellationToken);
                    latencies[i] = watch.Elapsed.TotalMilliseconds;
                    evaluated += response.Evaluated;
                    hits += response.Total;
                }

                Array.Sort(latencies);
                rows.Add(
                [
                    label,
                    Percentile(latencies, 0.50).ToString("N2") + " ms",
                    Percentile(latencies, 0.95).ToString("N2") + " ms",
                    Percentile(latencies, 0.99).ToString("N2") + " ms",
                    (queries.Length / (latencies.Sum() / 1000)).ToString("N0"),
                    (evaluated / queries.Length).ToString("N0"),
                ]);
            }

            Output.Table(["Consultas", "p50", "p95", "p99", "Consultas/s", "Docs avaliados (média)"], rows);
        }

        if (!keep)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                Output.Dim($"Não foi possível apagar {path}; apague manualmente.");
            }
        }

        return 0;
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static SourceDocument Generate(int index, string[] vocabulary, ZipfSampler sampler)
    {
        var random = new Random(index * 7919);
        var title = new StringBuilder();
        var titleWords = 3 + random.Next(5);
        for (var i = 0; i < titleWords; i++)
        {
            if (i > 0)
            {
                title.Append(' ');
            }

            title.Append(vocabulary[sampler.Sample(random, 10, vocabulary.Length)]);
        }

        var body = new StringBuilder();
        var words = 80 + random.Next(520);
        for (var i = 0; i < words; i++)
        {
            body.Append(vocabulary[sampler.Sample(random)]);
            body.Append(i % 14 == 13 ? ". " : " ");
        }

        return new SourceDocument
        {
            Key = SourceDocument.MakeKey("benchmark", $"doc-{index:D7}.txt"),
            Source = "benchmark",
            Path = $"doc-{index:D7}.txt",
            Title = title.ToString(),
            Body = body.ToString(),
            Extension = ".txt",
            LanguageHint = Sonda.Analysis.Language.Portuguese,
            Size = body.Length,
            Modified = DateTimeOffset.UnixEpoch.AddDays(index % 3650),
        };
    }

    private static string[] BuildVocabulary(Random random, int size)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        while (words.Count < size)
        {
            var builder = new StringBuilder();
            var count = 2 + random.Next(3);
            for (var i = 0; i < count; i++)
            {
                builder.Append(Syllables[random.Next(Syllables.Length)]);
            }

            builder.Append(Endings[random.Next(Endings.Length)]);
            words.Add(builder.ToString());
        }

        return [.. words];
    }

    private sealed class ZipfSampler
    {
        private readonly double[] _cumulative;

        public ZipfSampler(int size, double exponent)
        {
            _cumulative = new double[size];
            double total = 0;
            for (var i = 0; i < size; i++)
            {
                total += 1 / Math.Pow(i + 1, exponent);
                _cumulative[i] = total;
            }

            for (var i = 0; i < size; i++)
            {
                _cumulative[i] /= total;
            }
        }

        public int Sample(Random random, int minRank = 0, int maxRank = int.MaxValue)
        {
            while (true)
            {
                var index = Array.BinarySearch(_cumulative, random.NextDouble());
                if (index < 0)
                {
                    index = ~index;
                }

                index = Math.Min(index, _cumulative.Length - 1);
                if (index >= minRank && index < maxRank)
                {
                    return index;
                }
            }
        }
    }
}
