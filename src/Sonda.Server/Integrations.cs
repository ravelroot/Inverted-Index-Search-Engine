using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml;
using Microsoft.Extensions.Options;
using Sonda.Engine;
using Sonda.Engine.Search;

namespace Sonda.Server;

public sealed record SearchLogEntry(string Consulta, long Total, double Ms, DateTimeOffset Momento);

public sealed record QueryCount(string Consulta, int Vezes, long UltimoTotal);

public sealed record SearchStatistics(
    long Total,
    long SemResultado,
    double MediaMs,
    double P95Ms,
    IReadOnlyList<QueryCount> Principais,
    IReadOnlyList<QueryCount> SemResultados,
    IReadOnlyList<SearchLogEntry> Recentes);

/// <summary>Guarda em memória as últimas 500 buscas do painel e da API para mostrar o que se procura e o que não se encontra.</summary>
public sealed class SearchAnalytics
{
    private const int Capacity = 500;
    private readonly Queue<SearchLogEntry> _recent = new(Capacity);
    private readonly Lock _gate = new();
    private long _total;
    private long _empty;
    private double _sumMs;

    public long Total => Interlocked.Read(ref _total);

    public long Empty => Interlocked.Read(ref _empty);

    public double SumMs
    {
        get
        {
            lock (_gate)
            {
                return _sumMs;
            }
        }
    }

    public void Record(string query, long total, double elapsedMs)
    {
        var normalized = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            return;
        }

        if (normalized.Length > 200)
        {
            normalized = normalized[..200];
        }

        Interlocked.Increment(ref _total);
        if (total == 0)
        {
            Interlocked.Increment(ref _empty);
        }

        lock (_gate)
        {
            _sumMs += elapsedMs;
            if (_recent.Count == Capacity)
            {
                _recent.Dequeue();
            }

            _recent.Enqueue(new SearchLogEntry(normalized, total, Math.Round(elapsedMs, 2), DateTimeOffset.UtcNow));
        }
    }

    public SearchStatistics Snapshot(int top = 10)
    {
        SearchLogEntry[] entries;
        lock (_gate)
        {
            entries = [.. _recent];
        }

        var latencies = entries.Select(e => e.Ms).Order().ToArray();
        var p95 = latencies.Length == 0 ? 0 : latencies[Math.Clamp((int)Math.Ceiling(latencies.Length * 0.95) - 1, 0, latencies.Length - 1)];
        var groups = entries
            .GroupBy(e => e.Consulta.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(g =>
            {
                var last = g.MaxBy(e => e.Momento)!;
                return new QueryCount(last.Consulta, g.Count(), last.Total);
            })
            .ToList();
        return new SearchStatistics(
            Total,
            Empty,
            latencies.Length == 0 ? 0 : Math.Round(latencies.Average(), 2),
            Math.Round(p95, 2),
            [.. groups.OrderByDescending(g => g.Vezes).ThenBy(g => g.Consulta, StringComparer.Ordinal).Take(top)],
            [.. groups.Where(g => g.UltimoTotal == 0).OrderByDescending(g => g.Vezes).ThenBy(g => g.Consulta, StringComparer.Ordinal).Take(top)],
            [.. entries.Reverse().Take(20)]);
    }
}

/// <summary>
/// Envia os eventos do índice para URLs configuradas em Sonda:Webhooks, em segundo plano e sem travar a indexação.
/// Com Secret definido, o corpo é assinado com HMAC-SHA256 no cabeçalho X-Sonda-Assinatura.
/// </summary>
public sealed class WebhookDispatcher : BackgroundService
{
    private readonly WebhookSettings _settings;
    private readonly IReadOnlyList<Uri> _urls;
    private readonly HashSet<string> _events;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly HttpClient _http;
    private readonly Channel<(string Name, string Body)> _pending = Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private long _sent;
    private long _failed;

    public WebhookDispatcher(IOptions<SondaSettings> settings, EventHub events, ILogger<WebhookDispatcher> logger)
    {
        _settings = settings.Value.Webhooks;
        _urls = _settings.ValidUrls;
        _events = new HashSet<string>(_settings.EffectiveEvents, StringComparer.Ordinal);
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 1, 120)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Sonda/{typeof(SondaEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");
        if (_urls.Count > 0)
        {
            events.Published += OnPublished;
        }

        var configured = _settings.Urls.Count(u => !string.IsNullOrWhiteSpace(u));
        if (_urls.Count != configured)
        {
            _logger.LogWarning("Sonda:Webhooks: {Count} URL(s) ignorada(s) por não serem http ou https válidas.", configured - _urls.Count);
        }
    }

    public int Count => _urls.Count;

    public IReadOnlyList<string> Events => [.. _events.Order(StringComparer.Ordinal)];

    public bool Signed => !string.IsNullOrEmpty(_settings.Secret);

    public long Sent => Interlocked.Read(ref _sent);

    public long Failed => Interlocked.Read(ref _failed);

    public string? LastError { get; private set; }

    public static string Sign(string secret, byte[] body)
        => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    public static string Envelope(string name, string data, DateTimeOffset moment)
        => $"{{\"evento\":{JsonSerializer.Serialize(name)},\"momento\":{JsonSerializer.Serialize(moment)},\"dados\":{data}}}";

    private void OnPublished(string name, string data)
    {
        if (_events.Contains(name))
        {
            _pending.Writer.TryWrite((name, Envelope(name, data, DateTimeOffset.UtcNow)));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (name, body) in _pending.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                foreach (var url in _urls)
                {
                    await SendAsync(url, name, bytes, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(Uri url, string name, byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Add("X-Sonda-Evento", name);
            if (Signed)
            {
                request.Headers.Add("X-Sonda-Assinatura", Sign(_settings.Secret!, body));
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _sent);
                return;
            }

            Fail(url, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Fail(url, ex.Message);
        }
    }

    private void Fail(Uri url, string message)
    {
        Interlocked.Increment(ref _failed);
        LastError = $"{url.Host}: {message}";
        _logger.LogWarning("Webhook para {Host} falhou: {Message}", url.Host, message);
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}

/// <summary>Formatos de integração: Atom, OpenSearch e métricas no formato do Prometheus.</summary>
public static class Formats
{
    public static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    public static string SearchUrl(string baseUrl, string query) => $"{baseUrl}/#/?q={Uri.EscapeDataString(query)}";

    public static string OpenSearch(string baseUrl)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("OpenSearchDescription", "http://a9.com/-/spec/opensearch/1.1/");
            writer.WriteElementString("ShortName", "Sonda");
            writer.WriteElementString("Description", "Busca de texto completo nos documentos indexados pela Sonda.");
            writer.WriteElementString("InputEncoding", "UTF-8");
            writer.WriteElementString("OutputEncoding", "UTF-8");
            writer.WriteStartElement("Image");
            writer.WriteAttributeString("type", "image/svg+xml");
            writer.WriteString($"{baseUrl}/img/icone.svg");
            writer.WriteEndElement();
            WriteUrl(writer, "text/html", $"{baseUrl}/#/?q={{searchTerms}}");
            WriteUrl(writer, "application/x-suggestions+json", $"{baseUrl}/api/sugestoes?q={{searchTerms}}&formato=opensearch");
            WriteUrl(writer, "application/atom+xml", $"{baseUrl}/api/busca/feed?q={{searchTerms}}");
            writer.WriteStartElement("Url");
            writer.WriteAttributeString("type", "application/opensearchdescription+xml");
            writer.WriteAttributeString("rel", "self");
            writer.WriteAttributeString("template", $"{baseUrl}/opensearch.xml");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return builder.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }

    private static void WriteUrl(XmlWriter writer, string type, string template)
    {
        writer.WriteStartElement("Url");
        writer.WriteAttributeString("type", type);
        writer.WriteAttributeString("method", "get");
        writer.WriteAttributeString("template", template);
        writer.WriteEndElement();
    }

    public static string Atom(string baseUrl, string feedUrl, SearchResponse response)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            const string ns = "http://www.w3.org/2005/Atom";
            var updated = response.Hits.Count > 0 ? response.Hits.Max(h => h.Modified) : DateTimeOffset.UtcNow;
            writer.WriteStartDocument();
            writer.WriteStartElement("feed", ns);
            writer.WriteElementString("title", ns, $"Sonda: {response.Query}");
            writer.WriteElementString("subtitle", ns, $"{response.Total} resultado(s) · {response.Similarity}");
            writer.WriteElementString("id", ns, feedUrl);
            writer.WriteElementString("updated", ns, Rfc3339(updated));
            writer.WriteElementString("generator", ns, "Sonda");
            Link(writer, ns, "self", feedUrl, "application/atom+xml");
            Link(writer, ns, "alternate", SearchUrl(baseUrl, response.Query), "text/html");
            foreach (var hit in response.Hits)
            {
                writer.WriteStartElement("entry", ns);
                writer.WriteElementString("title", ns, hit.Title);
                writer.WriteElementString("id", ns, $"urn:sonda:{hit.Source}:{Uri.EscapeDataString(hit.Path)}");
                writer.WriteElementString("updated", ns, Rfc3339(hit.Modified));
                Link(writer, ns, "alternate", SearchUrl(baseUrl, response.Query), "text/html");
                writer.WriteStartElement("category", ns);
                writer.WriteAttributeString("term", hit.Source);
                writer.WriteEndElement();
                writer.WriteStartElement("summary", ns);
                writer.WriteAttributeString("type", "text");
                writer.WriteString(string.Concat(hit.Snippet.Select(f => f.Text)).ReplaceLineEndings(" ").Trim());
                writer.WriteEndElement();
                writer.WriteStartElement("content", ns);
                writer.WriteAttributeString("type", "text");
                writer.WriteString($"{hit.Source}:{hit.Path} · pontuação {hit.Score.ToString("0.###", CultureInfo.InvariantCulture)}");
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return builder.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }

    private static void Link(XmlWriter writer, string ns, string rel, string href, string type)
    {
        writer.WriteStartElement("link", ns);
        writer.WriteAttributeString("rel", rel);
        writer.WriteAttributeString("type", type);
        writer.WriteAttributeString("href", href);
        writer.WriteEndElement();
    }

    private static string Rfc3339(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static string Metrics(SondaService sonda, SearchAnalytics analytics, WebhookDispatcher webhooks)
    {
        var stats = sonda.Engine.GetStatistics();
        var state = sonda.State;
        var version = typeof(SondaEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var builder = new StringBuilder();
        void Metric(string name, string type, string help, IEnumerable<(string Labels, double Value)> values)
        {
            builder.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            builder.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
            foreach (var (labels, value) in values)
            {
                builder.Append(name).Append(labels).Append(' ').Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        static (string, double)[] One(double value) => [(string.Empty, value)];

        Metric("sonda_info", "gauge", "Versão da Sonda.", [($"{{versao=\"{Label(version)}\"}}", 1)]);
        Metric("sonda_documentos", "gauge", "Documentos pesquisáveis no índice.", One(stats.Documents));
        Metric("sonda_documentos_excluidos", "gauge", "Documentos excluídos aguardando mesclagem.", One(stats.Deleted));
        Metric("sonda_segmentos", "gauge", "Segmentos publicados.", One(stats.Segments.Count));
        Metric("sonda_indice_bytes", "gauge", "Tamanho do índice em disco.", One(stats.SizeBytes));
        Metric("sonda_buscas_total", "counter", "Buscas atendidas desde o início do processo.", One(analytics.Total));
        Metric("sonda_buscas_sem_resultado_total", "counter", "Buscas sem nenhum resultado.", One(analytics.Empty));
        Metric("sonda_busca_duracao_ms_soma", "counter", "Soma do tempo das buscas, em milissegundos.", One(Math.Round(analytics.SumMs, 3)));
        Metric("sonda_busca_duracao_ms_contagem", "counter", "Quantidade de buscas somadas na duração.", One(analytics.Total));
        Metric("sonda_execucoes_total", "counter", "Execuções de indexação no histórico, por situação.",
            sonda.Runs.GroupBy(r => r.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => ($"{{situacao=\"{Label(g.Key)}\"}}", (double)g.Count())));
        Metric("sonda_fila_indexacao", "gauge", "Fontes aguardando indexação.", One(state.Queue.Count));
        Metric("sonda_indexando", "gauge", "1 enquanto uma indexação está rodando.", One(state.Running ? 1 : 0));
        Metric("sonda_webhooks_enviados_total", "counter", "Webhooks entregues com sucesso.", One(webhooks.Sent));
        Metric("sonda_webhooks_falhas_total", "counter", "Webhooks que falharam.", One(webhooks.Failed));
        return builder.ToString();
    }

    private static string Label(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
