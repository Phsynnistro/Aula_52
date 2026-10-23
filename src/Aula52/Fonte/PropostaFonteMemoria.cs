using System.Collections.Concurrent;

namespace Aula52.Fonte;

/// <summary>
/// Fonte sintética da aula. Guarda propostas em memória e conta cada leitura.
/// Use ContagemConsultas para provar miss (subiu) e hit (não subiu).
/// </summary>
public sealed class PropostaFonteMemoria : IPropostaFonte, IPropostaEscrita
{
    private readonly ConcurrentDictionary<long, PropostaDto> _dados = new();
    private int _consultas;

    public int ContagemConsultas => Volatile.Read(ref _consultas);

    public PropostaFonteMemoria()
    {
        // ID 42 é o cenário didático do PDF.
        _dados[42] = new PropostaDto(42, "Notebook corporativo 16GB", "aprovada", 4500.00m);
        _dados[7] = new PropostaDto(7, "Mouse ergonômico", "pendente", 189.90m);
        _dados[99] = new PropostaDto(99, "Monitor ultrawide", "reprovada", 2100.00m);
    }

    public Task<PropostaDto?> ObterAsync(long id, CancellationToken ct)
    {
        Interlocked.Increment(ref _consultas);
        _dados.TryGetValue(id, out var proposta);
        return Task.FromResult(proposta);
    }

    public Task AtualizarAsync(PropostaDto proposta, CancellationToken ct)
    {
        _dados[proposta.Id] = proposta;
        return Task.CompletedTask;
    }

    public void Seed(PropostaDto proposta) => _dados[proposta.Id] = proposta;
}
