using Sonda.Analysis;
using Sonda.Engine;
using Sonda.Engine.Index;

namespace Sonda.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sonda-testes-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class Docs
{
    public static SourceDocument Make(string key, string title, string body, Language language = Language.Portuguese, string extension = ".txt", long modified = 0) => new()
    {
        Key = SourceDocument.MakeKey("teste", key),
        Source = "teste",
        Path = key,
        Title = title,
        Body = body,
        Extension = extension,
        LanguageHint = language,
        Size = body.Length,
        Modified = DateTimeOffset.FromUnixTimeMilliseconds(modified),
    };

    public static SondaOptions Options(string path, int ramMb = 64, bool autoMerge = false) => new()
    {
        IndexPath = path,
        Writer = new IndexWriterOptions { RamBufferMb = ramMb, AutoMerge = autoMerge },
    };

    public static SondaEngine EngineWith(string path, params SourceDocument[] documents)
    {
        var engine = SondaEngine.Open(Options(path));
        foreach (var document in documents)
        {
            engine.Writer!.Add(document);
        }

        engine.Writer!.Commit();
        return engine;
    }

    public static List<string> Keys(SearchResponse response) => response.Hits.Select(h => h.Path).ToList();
}
