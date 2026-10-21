using System.Text.Json;
using System.Text.Json.Serialization;
using Sonda.Engine.Crawling;

namespace Sonda.Server;

public sealed record CatalogSource(string Name, string Path, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, bool Watch, DateTimeOffset CreatedAt);

public sealed class CatalogData
{
    public List<CatalogSource> Sources { get; set; } = [];

    public List<IndexingRunResult> Runs { get; set; } = [];
}

public sealed class Catalog
{
    private const int MaxRuns = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private CatalogData _data;

    public Catalog(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _path = Path.Combine(dataPath, "catalogo.json");
        _data = Load(_path);
    }

    public IReadOnlyList<CatalogSource> Sources
    {
        get
        {
            lock (_gate)
            {
                return [.. _data.Sources];
            }
        }
    }

    public IReadOnlyList<IndexingRunResult> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _data.Runs];
            }
        }
    }

    public void AddSource(CatalogSource source)
    {
        lock (_gate)
        {
            _data.Sources.RemoveAll(s => s.Name == source.Name);
            _data.Sources.Add(source);
            Save();
        }
    }

    public bool RemoveSource(string name)
    {
        lock (_gate)
        {
            var removed = _data.Sources.RemoveAll(s => s.Name == name) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    public void AddRun(IndexingRunResult run)
    {
        lock (_gate)
        {
            _data.Runs.Insert(0, run);
            if (_data.Runs.Count > MaxRuns)
            {
                _data.Runs.RemoveRange(MaxRuns, _data.Runs.Count - MaxRuns);
            }

            Save();
        }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_data, Json));
        File.Move(temporary, _path, overwrite: true);
    }

    private static CatalogData Load(string path)
    {
        if (!File.Exists(path))
        {
            return new CatalogData();
        }

        try
        {
            return JsonSerializer.Deserialize<CatalogData>(File.ReadAllText(path), Json) ?? new CatalogData();
        }
        catch (JsonException)
        {
            File.Move(path, path + ".invalido", overwrite: true);
            return new CatalogData();
        }
    }
}
