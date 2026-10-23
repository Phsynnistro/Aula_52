using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Aula52.Cache;
using Aula52.Sync;

namespace Aula52.Write;

/// <summary>
/// Fluxo de escrita: persiste na fonte → invalida cache → registra evento de outbox para o índice.
/// Banco e Redis não formam uma única transação distribuída (janela de inconsistência).
/// </summary>
public sealed class AtualizacaoProposta
{
    private readonly IPropostaEscrita _escrita;
    private readonly IDistributedCache _cache;
    private readonly IOutboxStore _outbox;
    private readonly CacheOptions _options;
    private readonly ILogger<AtualizacaoProposta> _logger;

    public AtualizacaoProposta(
        IPropostaEscrita escrita,
        IDistributedCache cache,
        IOutboxStore outbox,
        IOptions<CacheOptions> options,
        ILogger<AtualizacaoProposta> logger)
    {
        _escrita = escrita;
        _cache = cache;
        _outbox = outbox;
        _options = options.Value;
        _logger = logger;
    }

    public async Task AtualizarAsync(PropostaDto proposta, CancellationToken ct)
    {
        // 1) Fonte autoritativa confirma a mudança.
        await _escrita.AtualizarAsync(proposta, ct);

        // 2) Invalidação: mesma convenção de chave da leitura. Remove é idempotente.
        var chave = ConsultaProposta.Chave(proposta.Id, _options.PayloadVersion);
        try
        {
            await _cache.RemoveAsync(chave, ct);
            _logger.LogInformation("Cache invalidado chave={Chave}", chave);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Se a remoção falhar, o TTL ainda limita o atraso (dado pode ficar obsoleto até expirar).
            _logger.LogError(ex, "Falha ao invalidar chave={Chave}. TTL ainda limita o atraso.", chave);
        }

        // 3) Outbox: evento na mesma unidade lógica da escrita (aqui: memória).
        // Worker publica depois; destino precisa aceitar repetição (idempotência por ID).
        await _outbox.EnqueueAsync(
            new OutboxEvent(
                Guid.NewGuid(),
                "PropostaAtualizada",
                proposta.Id,
                DateTimeOffset.UtcNow,
                proposta),
            ct);
    }
}
