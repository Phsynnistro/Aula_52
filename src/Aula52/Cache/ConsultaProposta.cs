using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Aula52.Cache;

public sealed class CacheOptions
{
    /// <summary>TTL absoluto em segundos (PDF usa 30s no laboratório).</summary>
    public int TtlSeconds { get; set; } = 30;

    /// <summary>Versão do payload na chave. Mude se o DTO mudar de formato.</summary>
    public string PayloadVersion { get; set; } = "v1";

    /// <summary>
    /// Se true, falha do Redis não derruba a API: consulta a fonte (fallback).
    /// Contabiliza CacheErrors para não confundir com miss.
    /// </summary>
    public bool FallbackToSourceOnCacheFailure { get; set; } = true;

    /// <summary>
    /// Limite de consultas concorrentes à fonte quando o cache falha ou há stampede.
    /// Protege a origem (Desafio 2).
    /// </summary>
    public int MaxConcurrentSourceFetches { get; set; } = 4;

    /// <summary>Timeout do fallback à fonte quando Redis falhou.</summary>
    public int SourceFallbackTimeoutMs { get; set; } = 2000;
}

/// <summary>
/// Cache-aside: procura no Redis → hit devolve → miss consulta fonte → grava com TTL.
/// Inclui single-flight (anti-stampede) por chave e política de falha do Redis.
/// </summary>
public sealed class ConsultaProposta
{
    private readonly IDistributedCache _cache;
    private readonly IPropostaFonte _fonte;
    private readonly ICacheMetrics _metrics;
    private readonly ILogger<ConsultaProposta> _logger;
    private readonly CacheOptions _options;
    private readonly SemaphoreSlim _sourceGate;
    private readonly ConcurrentDictionary<string, Lazy<Task<PropostaDto?>>> _inFlight = new();

    public ConsultaProposta(
        IDistributedCache cache,
        IPropostaFonte fonte,
        ICacheMetrics metrics,
        IOptions<CacheOptions> options,
        ILogger<ConsultaProposta> logger)
    {
        _cache = cache;
        _fonte = fonte;
        _metrics = metrics;
        _logger = logger;
        _options = options.Value;
        _sourceGate = new SemaphoreSlim(
            Math.Max(1, _options.MaxConcurrentSourceFetches),
            Math.Max(1, _options.MaxConcurrentSourceFetches));
    }

    public static string Chave(long id, string version = "v1") => $"proposta:{version}:{id}";

    public async Task<PropostaDto?> ObterAsync(long id, CancellationToken ct)
    {
        var chave = Chave(id, _options.PayloadVersion);

        var cached = await TryGetFromCacheAsync(chave, ct);
        if (cached.Found)
        {
            _metrics.IncrementHit();
            _logger.LogInformation("HIT chave={Chave} id={Id}", chave, id);
            return cached.Value;
        }

        // Single-flight: várias threads com miss na mesma chave compartilham uma única carga.
        // Lock local: coordena só esta instância (PDF Desafio 2).
        var lazy = _inFlight.GetOrAdd(
            chave,
            _ => new Lazy<Task<PropostaDto?>>(() => LoadAndStoreAsync(chave, id, ct)));

        try
        {
            return await lazy.Value;
        }
        finally
        {
            _inFlight.TryRemove(chave, out _);
        }
    }

    private async Task<PropostaDto?> LoadAndStoreAsync(string chave, long id, CancellationToken ct)
    {
        _metrics.IncrementMiss();
        _logger.LogInformation("MISS chave={Chave} id={Id} — consultando fonte", chave, id);

        await _sourceGate.WaitAsync(ct);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.SourceFallbackTimeoutMs);

            var proposta = await _fonte.ObterAsync(id, timeoutCts.Token);
            if (proposta is null)
                return null;

            await TrySetCacheAsync(chave, proposta, ct);
            return proposta;
        }
        finally
        {
            _sourceGate.Release();
        }
    }

    private async Task<(bool Found, PropostaDto? Value)> TryGetFromCacheAsync(string chave, CancellationToken ct)
    {
        try
        {
            var json = await _cache.GetStringAsync(chave, ct);
            if (json is null)
                return (false, null);

            var dto = JsonSerializer.Deserialize<PropostaDto>(json);
            return (dto is not null, dto);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _metrics.IncrementCacheError();
            _logger.LogError(ex, "Erro ao ler cache chave={Chave}", chave);

            if (!_options.FallbackToSourceOnCacheFailure)
                throw;

            // Não tratar erro como miss: métricas separam CacheErrors de Misses.
            return (false, null);
        }
    }

    private async Task TrySetCacheAsync(string chave, PropostaDto proposta, CancellationToken ct)
    {
        try
        {
            await _cache.SetStringAsync(
                chave,
                JsonSerializer.Serialize(proposta),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_options.TtlSeconds)
                },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _metrics.IncrementCacheError();
            _logger.LogError(ex, "Erro ao gravar cache chave={Chave}", chave);
            // Escrita no cache falhou: a resposta da fonte ainda é válida.
        }
    }
}
