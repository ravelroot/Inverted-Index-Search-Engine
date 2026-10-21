using System.Collections.Concurrent;
using System.Threading.Channels;
using Sonda.Engine;
using Sonda.Engine.Crawling;
using Sonda.Storage;
using Microsoft.Extensions.Options;

namespace Sonda.Server;

public sealed record SourceView(
    string Name,
    string Path,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    bool Watch,
    bool FromConfiguration,
    bool Exists,
    int Documents,
    IndexingRunResult? LastRun,
    bool Queued,
    bool Running);

public sealed record IndexingState(bool Running, string? Source, IndexingMode? Mode, IndexingProgress? Progress, IReadOnlyList<string> Queue);

public sealed class SondaService : IHostedService, IDisposable
{
    private readonly SondaSettings _settings;
    private readonly EventHub _events;
    private readonly ILogger<SondaService> _logger;
    private readonly Catalog _catalog;
    private readonly Channel<(string Source, IndexingMode Mode)> _queue = Channel.CreateUnbounded<(string, IndexingMode)>();
    private readonly ConcurrentDictionary<string, IndexingMode> _queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Timer> _debounce = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private CancellationTokenSource? _currentRun;
    private Task _worker = Task.CompletedTask;
    private volatile IndexingProgress? _progress;
    private volatile string? _runningSource;
    private IndexingMode? _runningMode;

    public SondaService(IOptions<SondaSettings> settings, EventHub events, ILogger<SondaService> logger)
    {
        _settings = settings.Value;
        _events = events;
        _logger = logger;
        _catalog = new Catalog(PathResolver.Resolve(_settings.DataPath));
        var options = _settings.ToEngineOptions();
        try
        {
            Engine = SondaEngine.Open(options);
        }
        catch (IndexLockedException ex)
        {
            _logger.LogWarning("{Message} O painel vai abrir em modo somente leitura.", ex.Message);
            Engine = SondaEngine.OpenReadOnly(options);
        }

        if (Engine.Writer is not null)
        {
            Engine.Writer.Committed += commit => _events.Publish("indice", new { geracao = commit.Generation, documentos = commit.DocCount });
        }
    }

    public SondaEngine Engine { get; }

    public SondaSettings Settings => _settings;

    public IReadOnlyList<IndexingRunResult> Runs => _catalog.Runs;

    public IndexingState State => new(
        _runningSource is not null,
        _runningSource,
        _runningMode,
        _progress,
        [.. _queued.Keys]);

    public IReadOnlyList<SourceDefinition> Sources()
    {
        var sources = new Dictionary<string, SourceDefinition>(StringComparer.Ordinal);
        foreach (var source in _settings.Sources.Where(s => SourceDefinition.IsValidName(s.Name.Trim().ToLowerInvariant())))
        {
            var definition = source.ToDefinition(true);
            sources[definition.Name] = definition;
        }

        foreach (var source in _catalog.Sources)
        {
            sources.TryAdd(source.Name, new SourceDefinition
            {
                Name = source.Name,
                Path = source.Path,
                Include = source.Include,
                Exclude = source.Exclude,
                Watch = source.Watch,
            });
        }

        return [.. sources.Values.OrderBy(s => s.Name, StringComparer.Ordinal)];
    }

    public IReadOnlyList<SourceView> SourceViews()
    {
        var counts = Engine.GetStatistics().BySource;
        var runs = _catalog.Runs;
        return Sources().Select(s => new SourceView(
            s.Name,
            DisplayPath(s.Path),
            s.Include,
            s.Exclude,
            s.Watch,
            s.FromConfiguration,
            Directory.Exists(s.Path),
            counts.GetValueOrDefault(s.Name),
            runs.FirstOrDefault(r => r.Source == s.Name),
            _queued.ContainsKey(s.Name),
            _runningSource == s.Name)).ToList();
    }

    public static string DisplayPath(string path)
    {
        var relative = Path.GetRelativePath(PathResolver.BaseDirectory, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative.Replace('\\', '/');
    }

    public bool Enqueue(string name, IndexingMode mode)
    {
        if (Engine.ReadOnly || Sources().All(s => s.Name != name))
        {
            return false;
        }

        if (_queued.TryAdd(name, mode))
        {
            _queue.Writer.TryWrite((name, mode));
            _events.Publish("fila", State);
        }
        else if (mode == IndexingMode.Full)
        {
            _queued[name] = IndexingMode.Full;
        }

        return true;
    }

    public bool CancelCurrent()
    {
        var current = _currentRun;
        if (current is null)
        {
            return false;
        }

        current.Cancel();
        return true;
    }

    public SourceDefinition AddSource(string name, string path, IReadOnlyList<string>? include, IReadOnlyList<string>? exclude, bool watch)
    {
        name = name.Trim().ToLowerInvariant();
        if (!SourceDefinition.IsValidName(name))
        {
            throw new ArgumentException("Use só letras minúsculas, números, hífen e sublinhado no nome da fonte (até 40 caracteres).");
        }

        if (Sources().Any(s => s.Name == name && s.FromConfiguration))
        {
            throw new ArgumentException($"A fonte \"{name}\" já está definida no appsettings.");
        }

        var full = PathResolver.Resolve(path.Trim());
        if (!Directory.Exists(full))
        {
            throw new ArgumentException("A pasta informada não existe no servidor.");
        }

        if (_settings.AllowedRoots.Count > 0 && !_settings.AllowedRoots
                .Select(PathResolver.Resolve)
                .Any(root => full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A pasta está fora das raízes permitidas em Sonda:AllowedRoots.");
        }

        var source = new CatalogSource(
            name,
            full,
            include is { Count: > 0 } ? include : SourceDefinition.DefaultInclude,
            exclude is { Count: > 0 } ? exclude : SourceDefinition.DefaultExclude,
            watch,
            DateTimeOffset.UtcNow);
        _catalog.AddSource(source);
        var definition = Sources().First(s => s.Name == name);
        ConfigureWatcher(definition);
        Enqueue(name, IndexingMode.Incremental);
        return definition;
    }

    public int RemoveSource(string name)
    {
        var source = Sources().FirstOrDefault(s => s.Name == name) ?? throw new KeyNotFoundException();
        if (source.FromConfiguration)
        {
            throw new ArgumentException("Fontes definidas no appsettings são removidas editando o arquivo de configuração.");
        }

        StopWatcher(name);
        _catalog.RemoveSource(name);
        return Engine.ReadOnly ? 0 : Engine.RemoveSource(name);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => WorkerAsync(_stopping.Token), CancellationToken.None);
        if (Engine.ReadOnly)
        {
            return Task.CompletedTask;
        }

        foreach (var source in Sources())
        {
            ConfigureWatcher(source);
            if (_settings.Indexing.IndexOnStartup && Directory.Exists(source.Path))
            {
                Enqueue(source.Name, IndexingMode.Incremental);
            }
        }

        return Task.CompletedTask;
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (name, initialMode) in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var mode = _queued.TryRemove(name, out var requested) ? requested : initialMode;
                var source = Sources().FirstOrDefault(s => s.Name == name);
                if (source is null)
                {
                    continue;
                }

                using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _currentRun = run;
                _runningSource = name;
                _runningMode = mode;
                _progress = null;
                _events.Publish("fila", State);
                try
                {
                    var progress = new Progress<IndexingProgress>(p =>
                    {
                        _progress = p;
                        _events.Publish("progresso", p);
                    });
                    var result = await Engine.IndexAsync(source, mode, progress, run.Token).ConfigureAwait(false);
                    _catalog.AddRun(result);
                    _events.Publish("execucao", result);
                    _logger.LogInformation(
                        "Fonte {Source}: {Added} novos, {Updated} atualizados, {Deleted} removidos, {Failed} falhas em {Seconds:N1} s.",
                        result.Source, result.Added, result.Updated, result.Deleted, result.Failed, result.ElapsedSeconds);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    var failed = new IndexingRunResult(Guid.NewGuid().ToString("N")[..12], name, mode, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, 0, "falhou", ex.Message, []);
                    _catalog.AddRun(failed);
                    _events.Publish("execucao", failed);
                    _logger.LogError(ex, "Falha ao indexar a fonte {Source}.", name);
                }
                finally
                {
                    _currentRun = null;
                    _runningSource = null;
                    _runningMode = null;
                    _events.Publish("fila", State);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ConfigureWatcher(SourceDefinition source)
    {
        StopWatcher(source.Name);
        if (!source.Watch || !Directory.Exists(source.Path) || Engine.ReadOnly)
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(source.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler changed = (_, _) => Debounce(source.Name);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => Debounce(source.Name);
            watcher.Error += (_, _) => Debounce(source.Name);
            watcher.EnableRaisingEvents = true;
            _watchers[source.Name] = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _logger.LogWarning("Não foi possível observar a pasta da fonte {Source}: {Message}", source.Name, ex.Message);
        }
    }

    private void StopWatcher(string name)
    {
        if (_watchers.TryRemove(name, out var watcher))
        {
            watcher.Dispose();
        }

        if (_debounce.TryRemove(name, out var timer))
        {
            timer.Dispose();
        }
    }

    private void Debounce(string name)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _settings.Indexing.WatchDebounceSeconds));
        var timer = _debounce.GetOrAdd(name, n => new Timer(_ => Enqueue(n, IndexingMode.Incremental), null, Timeout.Infinite, Timeout.Infinite));
        timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        _currentRun?.Cancel();
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("A indexação não terminou em 30 segundos.");
        }
    }

    public void Dispose()
    {
        foreach (var name in _watchers.Keys)
        {
            StopWatcher(name);
        }

        Engine.Dispose();
        _stopping.Dispose();
    }
}
