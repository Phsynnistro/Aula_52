using Microsoft.Extensions.Caching.Distributed;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Aula52.Tests;

/// <summary>
/// Fonte controlada: conta consultas e permite atraso (simular carga / timeout).
/// </summary>
public sealed class ContagemPropostaFonte : IPropostaFonte, IPropostaEscrita
{
    private readonly ConcurrentDictionary<long, PropostaDto> _dados = new();
    private int _consultas;
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public int ContagemConsultas => Volatile.Read(ref _consultas);

    public void Seed(PropostaDto proposta) => _dados[proposta.Id] = proposta;

    public async Task<PropostaDto?> ObterAsync(long id, CancellationToken ct)
    {
        Interlocked.Increment(ref _consultas);
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, ct);

        _dados.TryGetValue(id, out var proposta);
        return proposta;
    }

    public Task AtualizarAsync(PropostaDto proposta, CancellationToken ct)
    {
        _dados[proposta.Id] = proposta;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Cache que pode falhar sob demanda (Desafio 2: Redis fora do ar).
/// </summary>
public sealed class FaultyDistributedCache : IDistributedCache
{
    private readonly ConcurrentDictionary<string, (byte[] Value, DateTimeOffset? Expires)> _store = new();
    public bool FailReads { get; set; }
    public bool FailWrites { get; set; }
    public bool FailRemoves { get; set; }

    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        if (FailReads)
            throw new InvalidOperationException("Redis simulado indisponível (leitura).");

        if (!_store.TryGetValue(key, out var entry))
            return Task.FromResult<byte[]?>(null);

        if (entry.Expires is { } exp && exp <= DateTimeOffset.UtcNow)
        {
            _store.TryRemove(key, out _);
            return Task.FromResult<byte[]?>(null);
        }

        return Task.FromResult<byte[]?>(entry.Value);
    }

    public void Refresh(string key) { }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        if (FailRemoves)
            throw new InvalidOperationException("Redis simulado indisponível (remove).");

        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        SetAsync(key, value, options).GetAwaiter().GetResult();

    public Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        if (FailWrites)
            throw new InvalidOperationException("Redis simulado indisponível (escrita).");

        DateTimeOffset? expires = options.AbsoluteExpirationRelativeToNow is { } ttl
            ? DateTimeOffset.UtcNow.Add(ttl)
            : options.AbsoluteExpiration;

        _store[key] = (value, expires);
        return Task.CompletedTask;
    }

    public bool Contains(string key) =>
        _store.TryGetValue(key, out var e) &&
        (e.Expires is null || e.Expires > DateTimeOffset.UtcNow);
}

/// <summary>
/// Handler HTTP que simula Elasticsearch (sucesso, 500, timeout).
/// </summary>
public sealed class FakeElasticHandler : HttpMessageHandler
{
    public HttpStatusCode SearchStatus { get; set; } = HttpStatusCode.OK;
    public string SearchBody { get; set; } =
        """{"hits":{"hits":[{"_source":{"id":42,"descricao":"Notebook","status":"aprovada","valor":4500}}]}}""";

    public HttpStatusCode IndexStatus { get; set; } = HttpStatusCode.OK;
    public bool ThrowOnSearch { get; set; }
    public int IndexCalls { get; private set; }
    public int SearchCalls { get; private set; }
    public List<string> IndexedBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path.Contains("_search", StringComparison.Ordinal))
        {
            SearchCalls++;
            if (ThrowOnSearch)
                throw new HttpRequestException("Elasticsearch fora do ar");

            return new HttpResponseMessage(SearchStatus)
            {
                Content = new StringContent(SearchBody, Encoding.UTF8, "application/json")
            };
        }

        if (path.Contains("_doc", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
        {
            IndexCalls++;
            if (request.Content is not null)
                IndexedBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(IndexStatus)
            {
                Content = new StringContent("""{"result":"created"}""", Encoding.UTF8, "application/json")
            };
        }

        if (request.Method == HttpMethod.Head || request.Method == HttpMethod.Put)
        {
            // garantir índice
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public static class TestHelpers
{
    public static byte[] ToBytes(PropostaDto dto) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto));
}

public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost:59238/")
        };
}
