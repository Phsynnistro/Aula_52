using System.Collections.Concurrent;
using Aula52.Search;

namespace Aula52.Sync;

/// <summary>
/// Evento persistido junto com a escrita (padrão outbox).
/// O worker aplica depois do "commit". Destino deve aceitar repetição.
/// </summary>
public sealed record OutboxEvent(
    Guid EventId,
    string Tipo,
    long PropostaId,
    DateTimeOffset CriadoEm,
    PropostaDto Payload,
    int Tentativas = 0,
    DateTimeOffset? ProcessadoEm = null);

public interface IOutboxStore
{
    Task EnqueueAsync(OutboxEvent evt, CancellationToken ct);
    Task<IReadOnlyList<OutboxEvent>> PeekPendingAsync(int take, CancellationToken ct);
    Task MarkProcessedAsync(Guid eventId, CancellationToken ct);
    Task MarkRetryAsync(Guid eventId, CancellationToken ct);
    int PendingCount { get; }
}

public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxEvent> _events = new();

    public int PendingCount => _events.Values.Count(e => e.ProcessadoEm is null);

    public Task EnqueueAsync(OutboxEvent evt, CancellationToken ct)
    {
        _events[evt.EventId] = evt;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxEvent>> PeekPendingAsync(int take, CancellationToken ct)
    {
        IReadOnlyList<OutboxEvent> pending = _events.Values
            .Where(e => e.ProcessadoEm is null)
            .OrderBy(e => e.CriadoEm)
            .Take(take)
            .ToList();
        return Task.FromResult(pending);
    }

    public Task MarkProcessedAsync(Guid eventId, CancellationToken ct)
    {
        if (_events.TryGetValue(eventId, out var current))
            _events[eventId] = current with { ProcessadoEm = DateTimeOffset.UtcNow };
        return Task.CompletedTask;
    }

    public Task MarkRetryAsync(Guid eventId, CancellationToken ct)
    {
        if (_events.TryGetValue(eventId, out var current))
            _events[eventId] = current with { Tentativas = current.Tentativas + 1 };
        return Task.CompletedTask;
    }
}

/// <summary>
/// Worker didático: publica eventos do outbox no Elasticsearch.
/// Retry idempotente: indexar pelo mesmo ID sobrescreve o documento.
/// Eventos fora de ordem: versão/timestamp no payload; aqui usamos CriadoEm como ordenação de processamento.
/// </summary>
public sealed class OutboxSyncWorker : BackgroundService
{
    private readonly IOutboxStore _outbox;
    private readonly IndexadorPropostas _indexador;
    private readonly ILogger<OutboxSyncWorker> _logger;
    private readonly TimeSpan _intervalo = TimeSpan.FromSeconds(2);

    public OutboxSyncWorker(
        IOutboxStore outbox,
        IndexadorPropostas indexador,
        ILogger<OutboxSyncWorker> logger)
    {
        _outbox = outbox;
        _indexador = indexador;
        _logger = logger;
    }

    /// <summary>Processa um lote — exposto para testes do Desafio 2.</summary>
    public async Task ProcessarLoteAsync(CancellationToken ct)
    {
        var pendentes = await _outbox.PeekPendingAsync(20, ct);
        foreach (var evt in pendentes)
        {
            try
            {
                await _indexador.IndexarAsync(evt.Payload, ct);
                await _outbox.MarkProcessedAsync(evt.EventId, ct);
                _logger.LogInformation(
                    "Outbox processado eventId={EventId} proposta={PropostaId} tentativas={Tentativas}",
                    evt.EventId,
                    evt.PropostaId,
                    evt.Tentativas);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _outbox.MarkRetryAsync(evt.EventId, ct);
                _logger.LogWarning(
                    ex,
                    "Falha no outbox eventId={EventId}. Retry idempotente (PUT por ID). Pendentes={Pendentes}",
                    evt.EventId,
                    _outbox.PendingCount);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Em ambiente de teste (WebApplicationFactory) o worker pode ficar ocioso;
        // a sync sob demanda usa ProcessarLoteAsync.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessarLoteAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Erro no ciclo do OutboxSyncWorker");
            }

            await Task.Delay(_intervalo, stoppingToken);
        }
    }
}
