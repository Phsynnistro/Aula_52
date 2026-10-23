namespace Aula52;

/// <summary>
/// Payload que a API devolve ao cliente e que também guardamos no Redis.
/// Manter o contrato estável evita desserializar JSON incompatível no cache.
/// </summary>
public record PropostaDto(long Id, string Descricao, string Status, decimal Valor);

/// <summary>
/// Fonte autoritativa (no laboratório: memória; em produção: banco/EF).
/// A consulta de cache depende só desta interface — facilita testar hit/miss.
/// </summary>
public interface IPropostaFonte
{
    Task<PropostaDto?> ObterAsync(long id, CancellationToken ct);
    int ContagemConsultas { get; }
}

/// <summary>
/// Escrita na fonte. A invalidação do cache ocorre depois desta confirmação.
/// </summary>
public interface IPropostaEscrita
{
    Task AtualizarAsync(PropostaDto proposta, CancellationToken ct);
}

/// <summary>
/// Contadores observáveis: hit, miss, erro de cache e consultas à fonte.
/// No Desafio 1, estes números provam o comportamento (igualdade do JSON sozinha não prova hit).
/// </summary>
public interface ICacheMetrics
{
    long Hits { get; }
    long Misses { get; }
    long CacheErrors { get; }
    void IncrementHit();
    void IncrementMiss();
    void IncrementCacheError();
}

public sealed class CacheMetrics : ICacheMetrics
{
    private long _hits;
    private long _misses;
    private long _cacheErrors;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long CacheErrors => Interlocked.Read(ref _cacheErrors);

    public void IncrementHit() => Interlocked.Increment(ref _hits);
    public void IncrementMiss() => Interlocked.Increment(ref _misses);
    public void IncrementCacheError() => Interlocked.Increment(ref _cacheErrors);
}
