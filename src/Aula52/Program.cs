using Aula52;
using Aula52.Cache;
using Aula52.Fonte;
using Aula52.Search;
using Aula52.Sync;
using Aula52.Write;
using Microsoft.Extensions.Caching.Distributed;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));

var redisConnection = builder.Configuration["Redis:Configuration"] ?? "localhost:56338";
var useMemoryCache = builder.Configuration.GetValue("Redis:UseMemoryCache", false);

if (useMemoryCache)
{
    // Laboratório sem Docker / testes: IDistributedCache em memória com a mesma API.
    builder.Services.AddDistributedMemoryCache();
}
else
{
    builder.Services.AddStackExchangeRedisCache(o =>
    {
        o.Configuration = redisConnection;
        // Prefixo de instância: chave lógica proposta:v1:42 vira aula52:proposta:v1:42 no Redis.
        o.InstanceName = builder.Configuration["Redis:InstanceName"] ?? "aula52:";
    });
}

var fonte = new PropostaFonteMemoria();
builder.Services.AddSingleton(fonte);
builder.Services.AddSingleton<IPropostaFonte>(fonte);
builder.Services.AddSingleton<IPropostaEscrita>(fonte);
builder.Services.AddSingleton<ICacheMetrics, CacheMetrics>();
builder.Services.AddSingleton<IOutboxStore, InMemoryOutboxStore>();
builder.Services.AddScoped<ConsultaProposta>();
builder.Services.AddScoped<AtualizacaoProposta>();
builder.Services.AddScoped<BuscaPropostas>();
builder.Services.AddScoped<IndexadorPropostas>();
builder.Services.AddSingleton<OutboxSyncWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxSyncWorker>());

var elasticsearchUrl = builder.Configuration["Elasticsearch:Url"] ?? "http://localhost:59238/";
builder.Services.AddHttpClient("busca", c =>
{
    c.BaseAddress = new Uri(elasticsearchUrl);
    c.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("Elasticsearch:TimeoutSeconds", 3));
});

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    aula = 52,
    endpoints = new[]
    {
        "GET  /propostas/{id}",
        "PUT  /propostas/{id}",
        "GET  /busca?termo=",
        "GET  /metrics/cache",
        "POST /sync/outbox"
    }
}));

app.MapGet("/propostas/{id:long}", async (
    long id,
    ConsultaProposta consulta,
    CancellationToken ct) =>
{
    var proposta = await consulta.ObterAsync(id, ct);
    return proposta is null
        ? Results.NotFound(new { isSuccess = false, message = $"Proposta {id} não encontrada." })
        : Results.Ok(proposta);
});

app.MapPut("/propostas/{id:long}", async (
    long id,
    PropostaDto body,
    AtualizacaoProposta atualizacao,
    CancellationToken ct) =>
{
    if (id != body.Id)
        return Results.BadRequest(new { message = "Id da rota e do corpo devem coincidir." });

    await atualizacao.AtualizarAsync(body, ct);
    return Results.NoContent();
});

app.MapGet("/busca", async (
    string? termo,
    BuscaPropostas busca,
    CancellationToken ct) =>
{
    var resultado = await busca.BuscarAsync(termo ?? string.Empty, ct);
    if (!resultado.Success)
    {
        return Results.Problem(
            detail: resultado.Error,
            statusCode: resultado.StatusCode ?? StatusCodes.Status502BadGateway);
    }

    return Results.Content(resultado.Json!, "application/json");
});

app.MapGet("/metrics/cache", (
    ICacheMetrics metrics,
    IPropostaFonte fonte,
    IOutboxStore outbox) => Results.Ok(new
{
    cacheHits = metrics.Hits,
    cacheMisses = metrics.Misses,
    cacheErrors = metrics.CacheErrors,
    consultasFonte = fonte.ContagemConsultas,
    outboxPendentes = outbox.PendingCount,
    observacao = "Hit ratio útil exige erros separados e janela definida."
}));

app.MapPost("/sync/outbox", async (
    OutboxSyncWorker worker,
    CancellationToken ct) =>
{
    await worker.ProcessarLoteAsync(ct);
    return Results.Accepted();
});

app.Run();

public partial class Program;
