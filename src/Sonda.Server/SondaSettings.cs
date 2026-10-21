using Sonda.Analysis;
using Sonda.Engine;
using Sonda.Engine.Crawling;
using Sonda.Engine.Index;
using Sonda.Engine.Search;

namespace Sonda.Server;

public sealed class SondaSettings
{
    public const string Section = "Sonda";

    public string IndexPath { get; set; } = Path.Combine("dados", "indice");

    public string DataPath { get; set; } = "dados";

    public AnalysisSettings Analysis { get; set; } = new();

    public IndexingSettings Indexing { get; set; } = new();

    public SearchSettings Search { get; set; } = new();

    public ApiSettings Api { get; set; } = new();

    public List<SourceSettings> Sources { get; set; } = [];

    public List<string> AllowedRoots { get; set; } = [];

    public SondaOptions ToEngineOptions() => new()
    {
        IndexPath = PathResolver.Resolve(IndexPath),
        Writer = new IndexWriterOptions
        {
            RamBufferMb = Math.Max(8, Indexing.RamBufferMb),
            MergeFactor = Math.Max(2, Indexing.MergeFactor),
            StoreBody = Indexing.StoreBody,
            AutoMerge = Indexing.AutoMerge,
            Analyzer = new AnalyzerOptions
            {
                Stemming = Analysis.Language?.Trim().ToLowerInvariant() switch
                {
                    "pt" or "portugues" or "português" or "portuguese" => StemmingMode.Portuguese,
                    "en" or "ingles" or "inglês" or "english" => StemmingMode.English,
                    "nenhum" or "none" => StemmingMode.None,
                    _ => StemmingMode.Auto,
                },
                DefaultLanguage = LanguageCodes.FromCode(Analysis.DefaultLanguage) is var language && language != Sonda.Analysis.Language.Unknown ? language : Sonda.Analysis.Language.Portuguese,
                RemoveStopWords = Analysis.StopWords,
                FoldAccents = Analysis.FoldAccents,
            },
        },
        Indexing = new IndexingOptions
        {
            Parallelism = Indexing.Parallelism,
            MaxFileBytes = Math.Max(1, Indexing.MaxFileMb) * 1024L * 1024L,
        },
        Similarity = Search.Similarity,
        K1 = Search.K1,
        B = Search.B,
        TitleBoost = Search.TitleBoost,
        DefaultOperator = string.Equals(Search.DefaultOperator, "or", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Search.DefaultOperator, "ou", StringComparison.OrdinalIgnoreCase)
            ? DefaultOperator.Or
            : DefaultOperator.And,
        MaxResultWindow = Math.Clamp(Search.MaxResultWindow, 10, 10_000),
        SnippetLength = Math.Clamp(Search.SnippetLength, 80, 1000),
    };
}

public sealed class AnalysisSettings
{
    public string Language { get; set; } = "auto";

    public string DefaultLanguage { get; set; } = "pt";

    public bool StopWords { get; set; } = true;

    public bool FoldAccents { get; set; } = true;
}

public sealed class IndexingSettings
{
    public int RamBufferMb { get; set; } = 128;

    public int Parallelism { get; set; }

    public int MaxFileMb { get; set; } = 32;

    public bool StoreBody { get; set; } = true;

    public int MergeFactor { get; set; } = 10;

    public bool AutoMerge { get; set; } = true;

    public bool IndexOnStartup { get; set; } = true;

    public int WatchDebounceSeconds { get; set; } = 3;
}

public sealed class SearchSettings
{
    public string Similarity { get; set; } = "BM25";

    public double K1 { get; set; } = 1.2;

    public double B { get; set; } = 0.75;

    public float TitleBoost { get; set; } = 2f;

    public string DefaultOperator { get; set; } = "And";

    public int MaxResultWindow { get; set; } = 1000;

    public int SnippetLength { get; set; } = 240;
}

public sealed class ApiSettings
{
    public string? ApiKey { get; set; }

    public bool ProtectReads { get; set; }

    public List<string> AllowedOrigins { get; set; } = [];
}

public sealed class SourceSettings
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public List<string>? Include { get; set; }

    public List<string>? Exclude { get; set; }

    public bool Watch { get; set; }

    public SourceDefinition ToDefinition(bool fromConfiguration) => new()
    {
        Name = Name.Trim().ToLowerInvariant(),
        Path = PathResolver.Resolve(Path),
        Include = Include is { Count: > 0 } ? Include : SourceDefinition.DefaultInclude,
        Exclude = Exclude is { Count: > 0 } ? Exclude : SourceDefinition.DefaultExclude,
        Watch = Watch,
        FromConfiguration = fromConfiguration,
    };
}

public static class PathResolver
{
    public static string BaseDirectory { get; } = FindBase();

    public static string Resolve(string path)
        => System.IO.Path.IsPathRooted(path) ? System.IO.Path.GetFullPath(path) : System.IO.Path.GetFullPath(path, BaseDirectory);

    private static string FindBase()
    {
        var configured = Environment.GetEnvironmentVariable("SONDA_BASE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return System.IO.Path.GetFullPath(configured);
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var level = 0; level < 6 && current is not null; level++)
        {
            if (File.Exists(System.IO.Path.Combine(current.FullName, "Sonda.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
