using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sonda.Analysis;
using Sonda.Engine;
using Sonda.Engine.Crawling;
using Sonda.Engine.Export;
using Sonda.Engine.Search;
using Sonda.Server;
using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Services.Configure<SondaSettings>(builder.Configuration.GetSection(SondaSettings.Section));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton(provider =>
{
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    return new EventHub(json);
});
builder.Services.AddSingleton<SondaService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SondaService>());
builder.Services.AddSingleton<SearchAnalytics>();
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<WebhookDispatcher>());
builder.Services.AddProblemDetails();

var settings = builder.Configuration.GetSection(SondaSettings.Section).Get<SondaSettings>() ?? new SondaSettings();
if (settings.Api.AllowedOrigins.Count > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins([.. settings.Api.AllowedOrigins]).AllowAnyHeader().WithMethods("GET", "POST", "DELETE")));
}

var app = builder.Build();
var sonda = app.Services.GetRequiredService<SondaService>();
var analytics = app.Services.GetRequiredService<SearchAnalytics>();
var webhooks = app.Services.GetRequiredService<WebhookDispatcher>();
var version = typeof(SondaEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next();
});

if (settings.Api.AllowedOrigins.Count > 0)
{
    app.UseCors();
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

var apiKey = settings.Api.ApiKey;
var keyBytes = string.IsNullOrWhiteSpace(apiKey) ? null : Encoding.UTF8.GetBytes(apiKey);

bool Authorized(HttpContext context)
{
    if (keyBytes is null)
    {
        return true;
    }

    var provided = context.Request.Headers["X-Sonda-Key"].ToString();
    return provided.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), keyBytes);
}

var api = app.MapGroup("/api");
api.AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    var writing = !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method);
    var exempt = http.Request.Path.StartsWithSegments("/api/info");
    if ((writing || settings.Api.ProtectReads) && !exempt && !Authorized(http))
    {
        return Results.Problem(
            title: "Chave de acesso ausente ou inválida.",
            detail: "Envie o cabeçalho X-Sonda-Key com a chave definida em Sonda:Api:ApiKey.",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    try
    {
        return await next(context);
    }
    catch (QueryParseException ex)
    {
        return Results.Problem(title: "Consulta inválida.", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(title: "Dados inválidos.", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
    catch (KeyNotFoundException)
    {
        return Results.Problem(title: "Não encontrado.", statusCode: StatusCodes.Status404NotFound);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(title: "Operação indisponível.", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
});

api.MapGet("/info", () =>
{
    var stats = sonda.Engine.GetStatistics();
    return Results.Ok(new
    {
        nome = "Sonda",
        versao = version,
        somenteLeitura = sonda.Engine.ReadOnly,
        protegido = keyBytes is not null,
        leituraProtegida = settings.Api.ProtectReads && keyBytes is not null,
        similaridade = sonda.Engine.Options.Similarity.ToUpperInvariant() == "TFIDF" ? "TF-IDF" : "BM25",
        operador = sonda.Engine.Options.DefaultOperator.ToString(),
        documentos = stats.Documents,
        segmentos = stats.Segments.Count,
        idiomaAnalise = settings.Analysis.Language,
    });
});

api.MapGet("/busca", (string? q, int? pagina, int? tamanho, string? ordem, string? similaridade, string? operador, bool? explicar, string? tipo, string? idioma, string? fonte, bool? facetas, bool? exato, CancellationToken cancellationToken) =>
{
    var request = BuildRequest(q, ordem, similaridade, operador, tipo, idioma, fonte) with
    {
        Page = Math.Clamp(pagina ?? 1, 1, 1000),
        PageSize = Math.Clamp(tamanho ?? 10, 1, 100),
        Explain = explicar ?? false,
        Facets = facetas ?? true,
        ExactTotal = exato ?? true,
    };
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    var response = sonda.Engine.Search(request, timeout.Token);
    if (request.Page == 1)
    {
        analytics.Record(request.Query, response.Total, response.ElapsedMs);
    }

    return Results.Ok(response);
});

api.MapGet("/busca/exportar", (string? q, string? formato, int? limite, string? ordem, string? similaridade, string? operador, string? tipo, string? idioma, string? fonte, CancellationToken cancellationToken) =>
{
    if (!SearchExport.TryParseFormat(formato, out var format))
    {
        throw new ArgumentException("Formato desconhecido. Use csv, json ou ndjson.");
    }

    var request = BuildRequest(q, ordem, similaridade, operador, tipo, idioma, fonte) with { Facets = false, ExactTotal = true };
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(60));
    var batch = sonda.Engine.SearchMany(request, Math.Clamp(limite ?? 500, 1, 1000), timeout.Token);
    var rows = SearchExport.Rows(batch.Hits).ToList();
    var bytes = SearchExport.Encode(format, request.Query, batch.First.Total, rows, DateTimeOffset.UtcNow);
    return Results.File(bytes, SearchExport.ContentType(format), $"sonda-busca.{SearchExport.Extension(format)}");
});

api.MapGet("/busca/feed", (HttpContext context, string? q, string? ordem, string? similaridade, string? operador, string? tipo, string? idioma, string? fonte, CancellationToken cancellationToken) =>
{
    var request = BuildRequest(q, ordem, similaridade, operador, tipo, idioma, fonte) with { PageSize = 20, Facets = false, ExactTotal = false };
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    var response = sonda.Engine.Search(request, timeout.Token);
    var baseUrl = Formats.BaseUrl(context.Request);
    var feedUrl = baseUrl + context.Request.Path + context.Request.QueryString;
    return Results.Text(Formats.Atom(baseUrl, feedUrl, response), "application/atom+xml; charset=utf-8");
});

api.MapGet("/sugestoes", (string? q, int? limite, string? formato, CancellationToken cancellationToken) =>
{
    var text = (q ?? string.Empty).Trim();
    var opensearch = string.Equals(formato, "opensearch", StringComparison.OrdinalIgnoreCase);
    var documents = new List<object>();
    var titles = new List<string>();
    if (text.Length > 0 && text.Length <= 200)
    {
        var lastWord = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        var query = (q ?? string.Empty).EndsWith(' ') || lastWord.Length < 2 || !lastWord.All(Tokenizer.IsWordChar)
            ? text
            : text + "*";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var response = sonda.Engine.Search(new SearchRequest
            {
                Query = query,
                PageSize = Math.Clamp(limite ?? 6, 1, 10),
                Facets = false,
                ExactTotal = false,
                Snippets = false,
            }, timeout.Token);
            foreach (var hit in response.Hits)
            {
                documents.Add(new { chave = hit.Key, titulo = hit.Title, fonte = hit.Source, caminho = hit.Path, tipo = hit.Extension });
                titles.Add(hit.Title);
            }
        }
        catch (QueryParseException)
        {
        }
    }

    return opensearch
        ? Results.Text(JsonSerializer.Serialize(new object[] { text, titles }), "application/x-suggestions+json; charset=utf-8")
        : Results.Ok(new { consulta = text, documentos = documents });
});

api.MapGet("/documentos/{chave}", Results<Ok<DocumentView>, NotFound, BadRequest> (string chave, string? q) =>
{
    string key;
    try
    {
        key = Encoding.UTF8.GetString(Base64UrlDecode(chave));
    }
    catch (FormatException)
    {
        return TypedResults.BadRequest();
    }

    var view = sonda.Engine.GetDocumentView(key, q);
    return view is null ? TypedResults.NotFound() : TypedResults.Ok(view);
});

api.MapGet("/documentos/{chave}/semelhantes", (string chave, int? limite, CancellationToken cancellationToken) =>
{
    var key = DecodeKey(chave);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    var similar = sonda.Engine.MoreLikeThis(key, Math.Clamp(limite ?? 5, 1, 20), cancellationToken: timeout.Token) ?? throw new KeyNotFoundException();
    return Results.Ok(new { termos = similar.Terms, hits = similar.Hits });
});

api.MapGet("/documentos/{chave}/texto", (string chave) =>
{
    var document = sonda.Engine.GetDocument(DecodeKey(chave)) ?? throw new KeyNotFoundException();
    var name = Path.GetFileNameWithoutExtension(document.Path.Replace('\\', '/').Split('/')[^1]);
    var safe = new string([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')]).Trim('-');
    var text = $"{document.Title}\n\n{document.Body ?? document.Description ?? string.Empty}";
    return Results.File(Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", $"{(safe.Length == 0 ? "documento" : safe)}.txt");
});

api.MapGet("/indice", () =>
{
    var stats = sonda.Engine.GetStatistics();
    return Results.Ok(stats with { Path = SondaService.DisplayPath(stats.Path) });
});

api.MapPost("/indice/otimizar", async (int? segmentos, CancellationToken cancellationToken) =>
{
    var merges = await sonda.Engine.OptimizeAsync(Math.Clamp(segmentos ?? 1, 1, 100), cancellationToken);
    return Results.Ok(new { mesclagens = merges, indice = sonda.Engine.GetStatistics() });
});

api.MapPost("/indice/verificar", () => Results.Ok(sonda.Engine.Verify()));

api.MapGet("/termos", (string? campo, string? prefixo, int? limite, string? ordem) =>
{
    var field = ResolveField(campo);
    var byFrequency = !string.Equals(ordem, "alfabetica", StringComparison.OrdinalIgnoreCase);
    return Results.Ok(sonda.Engine.Terms(field, prefixo, limite ?? 60, byFrequency));
});

api.MapGet("/termos/postagens", (string? campo, string termo, int? limite) =>
{
    var postings = sonda.Engine.Postings(ResolveField(campo), termo, limite ?? 50);
    return postings is null ? Results.NotFound() : Results.Ok(postings);
});

api.MapGet("/analise", (string? texto) => Results.Ok(sonda.Engine.Analyze((texto ?? string.Empty)[..Math.Min(texto?.Length ?? 0, 4000)])));

api.MapGet("/fontes", () => Results.Ok(sonda.SourceViews()));

api.MapPost("/fontes", (NewSource body) =>
{
    var source = sonda.AddSource(body.Nome ?? string.Empty, body.Caminho ?? string.Empty, body.Incluir, body.Excluir, body.Observar);
    return Results.Created($"/api/fontes/{source.Name}", sonda.SourceViews().First(s => s.Name == source.Name));
});

api.MapDelete("/fontes/{nome}", (string nome) => Results.Ok(new { removidos = sonda.RemoveSource(nome) }));

api.MapPost("/fontes/{nome}/indexar", (string nome, string? modo) =>
{
    var mode = string.Equals(modo, "completa", StringComparison.OrdinalIgnoreCase) || string.Equals(modo, "full", StringComparison.OrdinalIgnoreCase)
        ? IndexingMode.Full
        : IndexingMode.Incremental;
    return sonda.Enqueue(nome, mode)
        ? Results.Accepted("/api/indexacao", sonda.State)
        : Results.Problem(title: "Não foi possível agendar a indexação.", detail: "Fonte inexistente ou índice aberto só para leitura.", statusCode: StatusCodes.Status409Conflict);
});

api.MapGet("/indexacao", () => Results.Ok(sonda.State));

api.MapPost("/indexacao/cancelar", () => Results.Ok(new { cancelada = sonda.CancelCurrent() }));

api.MapGet("/execucoes", (int? limite) => Results.Ok(sonda.Runs.Take(Math.Clamp(limite ?? 30, 1, 100))));

api.MapGet("/buscas/estatisticas", () => Results.Ok(analytics.Snapshot()));

api.MapGet("/integracoes", () => Results.Ok(new
{
    versao = version,
    opensearch = "/opensearch.xml",
    feed = "/api/busca/feed",
    metricas = "/metrics",
    exportar = SearchExport.FormatNames,
    webhooks = new
    {
        quantidade = webhooks.Count,
        eventos = webhooks.Events,
        assinado = webhooks.Signed,
        enviados = webhooks.Sent,
        falhas = webhooks.Failed,
        ultimoErro = webhooks.LastError,
    },
    protegido = keyBytes is not null,
    leituraProtegida = settings.Api.ProtectReads && keyBytes is not null,
}));

api.MapGet("/eventos", async (HttpContext context, EventHub events, CancellationToken cancellationToken) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    var (id, reader) = events.Subscribe();
    try
    {
        await context.Response.WriteAsync(": conectado\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var item = await reader.ReadAsync(heartbeat.Token);
                await context.Response.WriteAsync($"event: {item.Name}\ndata: {item.Data}\n\n", cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await context.Response.WriteAsync(": ativo\n\n", cancellationToken);
            }

            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
        events.Unsubscribe(id);
    }
});

app.MapGet("/opensearch.xml", (HttpContext context) =>
    Results.Text(Formats.OpenSearch(Formats.BaseUrl(context.Request)), "application/opensearchdescription+xml; charset=utf-8"));

app.MapGet("/metrics", (HttpContext context) =>
{
    if (settings.Api.ProtectReads && !Authorized(context))
    {
        return Results.Problem(
            title: "Chave de acesso ausente ou inválida.",
            detail: "Envie o cabeçalho X-Sonda-Key com a chave definida em Sonda:Api:ApiKey.",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    return Results.Text(Formats.Metrics(sonda, analytics, webhooks), "text/plain; version=0.0.4; charset=utf-8");
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", documentos = sonda.Engine.Searchers.Generation >= 0 }));

app.MapFallbackToFile("index.html");

app.Run();

static int ResolveField(string? name)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return Schema.Body;
    }

    return Schema.TryResolveField(name, out var field) ? field : throw new ArgumentException($"Campo desconhecido: {name}.");
}

static SearchRequest BuildRequest(string? q, string? ordem, string? similaridade, string? operador, string? tipo, string? idioma, string? fonte) => new()
{
    Query = q ?? string.Empty,
    Sort = string.Equals(ordem, "recentes", StringComparison.OrdinalIgnoreCase) ? SortMode.Recent : SortMode.Relevance,
    Similarity = similaridade,
    Operator = operador?.ToLowerInvariant() switch
    {
        "ou" or "or" => DefaultOperator.Or,
        "e" or "and" => DefaultOperator.And,
        _ => null,
    },
    Extension = tipo,
    Language = idioma,
    Source = fonte,
};

static string DecodeKey(string value)
{
    try
    {
        return Encoding.UTF8.GetString(Base64UrlDecode(value));
    }
    catch (FormatException)
    {
        throw new ArgumentException("Chave de documento inválida (use Base64 URL).");
    }
}

static byte[] Base64UrlDecode(string value)
{
    var padded = value.Replace('-', '+').Replace('_', '/');
    padded += (padded.Length % 4) switch
    {
        2 => "==",
        3 => "=",
        _ => string.Empty,
    };
    return Convert.FromBase64String(padded);
}

internal sealed record NewSource(string? Nome, string? Caminho, List<string>? Incluir, List<string>? Excluir, bool Observar);
