using Aula52.Search;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aula52.Tests;

/// <summary>
/// DESAFIO 1 — busca encontra documento; falha não vira lista vazia.
/// </summary>
public sealed class Desafio1_BuscaTests
{
    private static (BuscaPropostas busca, FakeElasticHandler handler) CriarBusca()
    {
        var handler = new FakeElasticHandler();
        var factory = new FakeHttpClientFactory(handler);
        var busca = new BuscaPropostas(factory, NullLogger<BuscaPropostas>.Instance);
        return (busca, handler);
    }

    [Fact]
    public async Task Busca_com_sucesso_devolve_json_do_elasticsearch()
    {
        var (busca, handler) = CriarBusca();
        handler.SearchBody =
            """{"hits":{"total":{"value":1},"hits":[{"_id":"42","_source":{"id":42,"descricao":"Notebook corporativo","status":"aprovada","valor":4500}}]}}""";

        var resultado = await busca.BuscarAsync("Notebook", CancellationToken.None);

        resultado.Success.Should().BeTrue();
        resultado.Json.Should().Contain("Notebook corporativo");
        handler.SearchCalls.Should().Be(1);
    }

    [Fact]
    public async Task Falha_http_nao_retorna_lista_vazia_enganosa()
    {
        var (busca, handler) = CriarBusca();
        handler.SearchStatus = System.Net.HttpStatusCode.InternalServerError;
        handler.SearchBody = """{"error":"boom"}""";

        var resultado = await busca.BuscarAsync("Notebook", CancellationToken.None);

        resultado.Success.Should().BeFalse();
        resultado.StatusCode.Should().Be(502);
        resultado.Json.Should().BeNull();
        resultado.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Timeout_ou_rede_vira_502_e_nao_vazio()
    {
        var (busca, handler) = CriarBusca();
        handler.ThrowOnSearch = true;

        var resultado = await busca.BuscarAsync("Mouse", CancellationToken.None);

        resultado.Success.Should().BeFalse();
        resultado.StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task Termo_vazio_retorna_400()
    {
        var (busca, _) = CriarBusca();
        var resultado = await busca.BuscarAsync("   ", CancellationToken.None);
        resultado.StatusCode.Should().Be(400);
    }
}
