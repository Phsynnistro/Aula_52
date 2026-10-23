using Aula52.Cache;
using Aula52.Sync;
using Aula52.Write;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aula52.Tests;

/// <summary>
/// DESAFIO 1 — Cache-aside verificável.
/// Critérios: miss → hit → expiração → invalidação após update.
/// Evidência: ContagemConsultas + métricas (não apenas igualdade do JSON).
/// </summary>
public sealed class Desafio1_CacheAsideTests
{
    private static (ConsultaProposta consulta, ContagemPropostaFonte fonte, FaultyDistributedCache cache, CacheMetrics metrics) CriarSut(
        int ttlSeconds = 30)
    {
        var fonte = new ContagemPropostaFonte();
        fonte.Seed(new PropostaDto(42, "Notebook corporativo 16GB", "aprovada", 4500m));

        var cache = new FaultyDistributedCache();
        var metrics = new CacheMetrics();
        var options = Options.Create(new CacheOptions
        {
            TtlSeconds = ttlSeconds,
            PayloadVersion = "v1",
            FallbackToSourceOnCacheFailure = true,
            MaxConcurrentSourceFetches = 8,
            SourceFallbackTimeoutMs = 5000
        });

        var consulta = new ConsultaProposta(
            cache,
            fonte,
            metrics,
            options,
            NullLogger<ConsultaProposta>.Instance);

        return (consulta, fonte, cache, metrics);
    }

    [Fact]
    public async Task Primeira_leitura_consulta_a_fonte_e_segunda_usa_cache()
    {
        var (consulta, fonte, cache, metrics) = CriarSut();

        var primeira = await consulta.ObterAsync(42, CancellationToken.None);
        var consultasAposMiss = fonte.ContagemConsultas;

        var segunda = await consulta.ObterAsync(42, CancellationToken.None);
        var consultasAposHit = fonte.ContagemConsultas;

        primeira.Should().NotBeNull();
        segunda.Should().BeEquivalentTo(primeira);

        // Evidência de hit: a fonte NÃO foi consultada de novo.
        consultasAposMiss.Should().Be(1);
        consultasAposHit.Should().Be(1);
        metrics.Misses.Should().Be(1);
        metrics.Hits.Should().Be(1);
        cache.Contains(ConsultaProposta.Chave(42)).Should().BeTrue();
    }

    [Fact]
    public async Task Apos_expiracao_do_TTL_a_fonte_recebe_nova_consulta()
    {
        var (consulta, fonte, _, metrics) = CriarSut(ttlSeconds: 1);

        await consulta.ObterAsync(42, CancellationToken.None); // miss + grava
        await consulta.ObterAsync(42, CancellationToken.None); // hit
        fonte.ContagemConsultas.Should().Be(1);
        metrics.Hits.Should().Be(1);

        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        await consulta.ObterAsync(42, CancellationToken.None); // miss de novo
        fonte.ContagemConsultas.Should().Be(2);
        metrics.Misses.Should().Be(2);
    }

    [Fact]
    public async Task Atualizacao_invalida_a_chave_correta()
    {
        var (consulta, fonte, cache, metrics) = CriarSut();
        var outbox = new InMemoryOutboxStore();
        var options = Options.Create(new CacheOptions { TtlSeconds = 30, PayloadVersion = "v1" });

        var atualizacao = new AtualizacaoProposta(
            fonte,
            cache,
            outbox,
            options,
            NullLogger<AtualizacaoProposta>.Instance);

        await consulta.ObterAsync(42, CancellationToken.None);
        cache.Contains(ConsultaProposta.Chave(42)).Should().BeTrue();

        var atualizada = new PropostaDto(42, "Notebook 32GB atualizado", "aprovada", 5200m);
        await atualizacao.AtualizarAsync(atualizada, CancellationToken.None);

        cache.Contains(ConsultaProposta.Chave(42)).Should().BeFalse(
            "após escrita confirmada a chave proposta:v1:42 deve ser removida");

        outbox.PendingCount.Should().Be(1, "evento de outbox enfileirado para o índice");

        // Próxima leitura: miss + dado novo da fonte
        var depois = await consulta.ObterAsync(42, CancellationToken.None);
        depois!.Descricao.Should().Be("Notebook 32GB atualizado");
        depois.Valor.Should().Be(5200m);
        fonte.ContagemConsultas.Should().Be(2);
        metrics.Misses.Should().Be(2);
    }

    [Fact]
    public async Task Id_ausente_nao_entra_no_cache_e_retorna_null()
    {
        var (consulta, fonte, cache, _) = CriarSut();

        var resultado = await consulta.ObterAsync(999, CancellationToken.None);

        resultado.Should().BeNull();
        fonte.ContagemConsultas.Should().Be(1);
        cache.Contains(ConsultaProposta.Chave(999)).Should().BeFalse(
            "ausência na fonte não deve ser cacheada neste exemplo didático");
    }
}
