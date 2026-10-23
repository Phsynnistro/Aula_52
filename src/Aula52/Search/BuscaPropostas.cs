using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aula52.Search;

/// <summary>
/// Consulta Elasticsearch com Query DSL via IHttpClientFactory.
/// Falha de busca retorna Problem (502/503), nunca lista vazia enganosa.
/// </summary>
public sealed class BuscaPropostas
{
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<BuscaPropostas> _logger;

    public BuscaPropostas(IHttpClientFactory clients, ILogger<BuscaPropostas> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public async Task<BuscaResultado> BuscarAsync(string termo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(termo))
            return BuscaResultado.BadRequest("Informe o parâmetro termo.");

        var client = _clients.CreateClient("busca");

        // Objeto JSON evita concatenar entrada do usuário dentro do DSL (injeção de query).
        var body = new
        {
            query = new
            {
                @bool = new
                {
                    must = new object[]
                    {
                        new { match = new { descricao = termo.Trim() } }
                    },
                    filter = new object[]
                    {
                        new { term = new { status = "aprovada" } }
                    }
                }
            }
        };

        try
        {
            using var resposta = await client.PostAsJsonAsync("propostas/_search", body, ct);
            if (!resposta.IsSuccessStatusCode)
            {
                var detalhe = await resposta.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "Elasticsearch rejeitou busca status={Status} body={Body}",
                    (int)resposta.StatusCode,
                    detalhe);

                // 502 Bad Gateway: upstream (busca) falhou. Não devolver [] como se não houvesse hits.
                return BuscaResultado.UpstreamFailure(
                    "Serviço de busca indisponível ou rejeitou a consulta.");
            }

            var json = await resposta.Content.ReadAsStringAsync(ct);
            return BuscaResultado.Ok(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Timeout ou falha de rede ao consultar Elasticsearch");
            return BuscaResultado.UpstreamFailure(
                "Timeout ou falha de rede ao consultar o Elasticsearch.");
        }
    }
}

public sealed record BuscaResultado(bool Success, int? StatusCode, string? Json, string? Error)
{
    public static BuscaResultado Ok(string json) => new(true, StatusCodes.Status200OK, json, null);

    public static BuscaResultado BadRequest(string error) =>
        new(false, StatusCodes.Status400BadRequest, null, error);

    public static BuscaResultado UpstreamFailure(string error) =>
        new(false, StatusCodes.Status502BadGateway, null, error);
}

/// <summary>
/// Indexação com ID estável: PUT propostas/_doc/{id} atualiza o mesmo documento.
/// refresh=wait_for facilita observar no laboratório (near real-time).
/// </summary>
public sealed class IndexadorPropostas
{
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<IndexadorPropostas> _logger;

    public IndexadorPropostas(IHttpClientFactory clients, ILogger<IndexadorPropostas> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public async Task IndexarAsync(PropostaDto proposta, CancellationToken ct)
    {
        var client = _clients.CreateClient("busca");
        var documento = new
        {
            id = proposta.Id,
            descricao = proposta.Descricao,
            status = proposta.Status,
            valor = proposta.Valor
        };

        using var resposta = await client.PutAsJsonAsync(
            $"propostas/_doc/{proposta.Id}?refresh=wait_for",
            documento,
            ct);

        if (!resposta.IsSuccessStatusCode)
        {
            var body = await resposta.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Falha ao indexar proposta {Id} status={Status} body={Body}",
                proposta.Id,
                (int)resposta.StatusCode,
                body);
            resposta.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Garante o índice e o mapping básico usados na Aula 38 / Aula 52.
    /// </summary>
    public async Task GarantirIndiceAsync(CancellationToken ct)
    {
        var client = _clients.CreateClient("busca");
        using var head = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "propostas"),
            ct);

        if (head.StatusCode == HttpStatusCode.OK)
            return;

        var mapping = new
        {
            mappings = new
            {
                properties = new
                {
                    id = new { type = "long" },
                    descricao = new { type = "text" },
                    status = new { type = "keyword" },
                    valor = new { type = "double" }
                }
            }
        };

        using var create = await client.PutAsJsonAsync("propostas", mapping, ct);
        create.EnsureSuccessStatusCode();
        _logger.LogInformation("Índice propostas criado com mapping didático");
    }
}
