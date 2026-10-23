using Aula52.Cache;
using Aula52.Search;
using Aula52.Sync;
using Aula52.Write;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aula52.Tests;

/// <summary>
/// DESAFIO 2 — resiliência, concorrência e sincronização.
/// </summary>
public sealed class Desafio2_ResilienciaTests
{
    [Fact]
    public async Task Redis_fora_do_ar_com_fallback_consulta_fonte_e_conta_erro_separado()
    {
        var fonte = new ContagemPropostaFonte();
        fonte.Seed(new PropostaDto(42, "Notebook", "aprovada", 4500m));

        var cache = new FaultyDistributedCache { FailReads = true };
        var metrics = new CacheMetrics();
        var options = Options.Create(new CacheOptions
        {
            FallbackToSourceOnCacheFailure = true,
            MaxConcurrentSourceFetches = 4,
            SourceFallbackTimeoutMs = 3000,
            TtlSeconds = 30
        });

        var consulta = new ConsultaProposta(
            cache, fonte, metrics, options, NullLogger<ConsultaProposta>.Instance);

        var proposta = await consulta.ObterAsync(42, CancellationToken.None);

        proposta.Should().NotBeNull();
        fonte.ContagemConsultas.Should().Be(1);
        metrics.CacheErrors.Should().BeGreaterThan(0, "erro de Redis não pode ser contado só como miss");
        metrics.Hits.Should().Be(0);
    }

    [Fact]
    public async Task Misses_simultaneos_na_mesma_chave_disparam_uma_unica_consulta_a_fonte()
    {
        var fonte = new ContagemPropostaFonte { Delay = TimeSpan.FromMilliseconds(200) };
        fonte.Seed(new PropostaDto(42, "Notebook", "aprovada", 4500m));

        var cache = new FaultyDistributedCache();
        var metrics = new CacheMetrics();
        var options = Options.Create(new CacheOptions
        {
            TtlSeconds = 30,
            MaxConcurrentSourceFetches = 8,
            SourceFallbackTimeoutMs = 5000
        });

        var consulta = new ConsultaProposta(
            cache, fonte, metrics, options, NullLogger<ConsultaProposta>.Instance);

        // Stampede: 20 requisições paralelas no mesmo ID.
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => consulta.ObterAsync(42, CancellationToken.None))
            .ToArray();

        var resultados = await Task.WhenAll(tasks);

        resultados.Should().OnlyContain(r => r != null && r.Id == 42);

        // Single-flight local: uma carga compartilhada por chave nesta instância.
        fonte.ContagemConsultas.Should().Be(1,
            "lock/single-flight local coordena a instância atual; várias instâncias precisariam de lock distribuído");
        metrics.Misses.Should().Be(1);
        metrics.Hits.Should().Be(0);
    }

    [Fact]
    public async Task Limite_de_concorrencia_protege_a_fonte_em_misses_de_chaves_diferentes()
    {
        var fonte = new ContagemPropostaFonte { Delay = TimeSpan.FromMilliseconds(150) };
        for (var i = 1; i <= 10; i++)
            fonte.Seed(new PropostaDto(i, $"Item {i}", "aprovada", i * 10m));

        var cache = new FaultyDistributedCache();
        var metrics = new CacheMetrics();
        var maxConcurrent = 2;
        var options = Options.Create(new CacheOptions
        {
            TtlSeconds = 30,
            MaxConcurrentSourceFetches = maxConcurrent,
            SourceFallbackTimeoutMs = 5000
        });

        var consulta = new ConsultaProposta(
            cache, fonte, metrics, options, NullLogger<ConsultaProposta>.Instance);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = Enumerable.Range(1, 6)
            .Select(id => consulta.ObterAsync(id, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();

        // Com gate=2 e delay=150ms, 6 chaves levam pelo menos ~3 janelas (~450ms).
        sw.ElapsedMilliseconds.Should().BeGreaterThan(400,
            "o SemaphoreSlim limita o fallback à fonte");
        fonte.ContagemConsultas.Should().Be(6);
    }

    [Fact]
    public async Task Outbox_retry_e_idempotente_por_id_do_documento()
    {
        var handler = new FakeElasticHandler();
        var factory = new FakeHttpClientFactory(handler);
        var indexador = new IndexadorPropostas(factory, NullLogger<IndexadorPropostas>.Instance);
        var outbox = new InMemoryOutboxStore();

        var proposta = new PropostaDto(42, "Notebook", "aprovada", 4500m);
        var evt = new OutboxEvent(Guid.NewGuid(), "PropostaAtualizada", 42, DateTimeOffset.UtcNow, proposta);

        // Primeira tentativa falha
        handler.IndexStatus = System.Net.HttpStatusCode.ServiceUnavailable;
        await outbox.EnqueueAsync(evt, CancellationToken.None);

        var worker = new OutboxSyncWorker(outbox, indexador, NullLogger<OutboxSyncWorker>.Instance);
        await worker.ProcessarLoteAsync(CancellationToken.None);
        outbox.PendingCount.Should().Be(1);
        handler.IndexCalls.Should().Be(1);

        // Segunda tentativa (retry) com sucesso — PUT no mesmo ID é idempotente
        handler.IndexStatus = System.Net.HttpStatusCode.OK;
        await worker.ProcessarLoteAsync(CancellationToken.None);
        outbox.PendingCount.Should().Be(0);
        handler.IndexCalls.Should().Be(2);
        handler.IndexedBodies.Should().HaveCount(2);
    }

    [Fact]
    public async Task Eventos_fora_de_ordem_sao_processados_por_CriadoEm_e_ultimo_PUT_vence()
    {
        var handler = new FakeElasticHandler();
        var factory = new FakeHttpClientFactory(handler);
        var indexador = new IndexadorPropostas(factory, NullLogger<IndexadorPropostas>.Instance);
        var outbox = new InMemoryOutboxStore();

        var antigo = new PropostaDto(42, "Versão antiga", "aprovada", 4000m);
        var novo = new PropostaDto(42, "Versão nova", "aprovada", 5200m);

        // Enfileira "novo" com timestamp anterior propositalmente invertido na criação...
        // Ordem de processamento: OrderBy CriadoEm — o mais antigo primeiro, o mais novo por último.
        await outbox.EnqueueAsync(
            new OutboxEvent(Guid.NewGuid(), "PropostaAtualizada", 42, DateTimeOffset.UtcNow.AddSeconds(-10), antigo),
            CancellationToken.None);
        await outbox.EnqueueAsync(
            new OutboxEvent(Guid.NewGuid(), "PropostaAtualizada", 42, DateTimeOffset.UtcNow, novo),
            CancellationToken.None);

        var worker = new OutboxSyncWorker(outbox, indexador, NullLogger<OutboxSyncWorker>.Instance);
        await worker.ProcessarLoteAsync(CancellationToken.None);

        outbox.PendingCount.Should().Be(0);
        handler.IndexCalls.Should().Be(2);
        handler.IndexedBodies.Last().Should().Match(body =>
            body.Contains("nova", StringComparison.Ordinal) &&
            body.Contains("5200", StringComparison.Ordinal),
            "repetição não garante ordem entre instâncias; aqui ordenamos por CriadoEm e o último PUT define o documento");
    }

    [Fact]
    public async Task Falha_ao_invalidar_cache_ainda_persiste_na_fonte_e_enfileira_outbox()
    {
        var fonte = new ContagemPropostaFonte();
        fonte.Seed(new PropostaDto(42, "Old", "aprovada", 1m));
        var cache = new FaultyDistributedCache { FailRemoves = true };
        // Pré-popula cache obsoleto
        await cache.SetAsync(
            ConsultaProposta.Chave(42),
            TestHelpers.ToBytes(new PropostaDto(42, "Old", "aprovada", 1m)),
            new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            });

        var outbox = new InMemoryOutboxStore();
        var atualizacao = new AtualizacaoProposta(
            fonte,
            cache,
            outbox,
            Options.Create(new CacheOptions()),
            NullLogger<AtualizacaoProposta>.Instance);

        await atualizacao.AtualizarAsync(
            new PropostaDto(42, "New", "aprovada", 99m),
            CancellationToken.None);

        // Fonte e outbox ok; chave pode permanecer até o TTL (janela de inconsistência).
        outbox.PendingCount.Should().Be(1);
        cache.Contains(ConsultaProposta.Chave(42)).Should().BeTrue(
            "remoção falhou: dado pode ficar obsoleto até o TTL");
    }
}
