# Passo a passo didático — Aula 52 (Dev Jr)

Este guia acompanha o código em `src/Aula52`. Leia na ordem.

---

## Passo 0 — Preparar a cabeça

Antes de digitar código, fixe três verdades:

1. **O banco (ou a fonte) é o dono da verdade.** Cache e Elasticsearch são cópias.
2. **Cópia desatualizada é normal.** A pergunta de negócio é: *quanto atraso eu aceito?*
3. **Provar comportamento exige contador.** Dois `200 OK` iguais não provam que a segunda veio do Redis.

---

## Passo 1 — Criar o projeto (como no PDF)

```powershell
dotnet new web -n Aula52 -f net8.0
cd Aula52
dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis --version 8.0.11
```

No nosso repositório isso já está em `src/Aula52`.

---

## Passo 2 — Registrar Redis no `Program.cs`

```csharp
builder.Services.AddStackExchangeRedisCache(o =>
{
    o.Configuration = "localhost:56338";
    o.InstanceName = "aula52:";
});
```

- `Configuration` → onde o Redis escuta.
- `InstanceName` → prefixo automático em **todas** as chaves. Assim várias apps no mesmo Redis não se atropelam.

Para estudar sem Docker, usamos `AddDistributedMemoryCache()` quando `Redis:UseMemoryCache` é `true`. A interface (`IDistributedCache`) é a mesma.

Também registramos o `HttpClient` nomeado `"busca"` apontando para Elasticsearch (`localhost:59238`), com timeout curto (3s). Timeout curto evita fila infinita quando a busca cai.

---

## Passo 3 — Contrato da fonte

```csharp
public record PropostaDto(long Id, string Descricao, string Status, decimal Valor);

public interface IPropostaFonte
{
    Task<PropostaDto?> ObterAsync(long id, CancellationToken ct);
}
```

Por que interface?

- O serviço de cache **não** precisa conhecer Entity Framework.
- Nos testes, trocamos a fonte por uma que **conta** quantas vezes foi chamada.

`PropostaFonteMemoria` já vem com o ID **42** (cenário do material).

---

## Passo 4 — Implementar hit

```csharp
var chave = $"proposta:v1:{id}";
var json = await cache.GetStringAsync(chave, ct);
if (json is not null)
    return JsonSerializer.Deserialize<PropostaDto>(json);
```

Se entrou aqui: **hit**. A fonte **não** roda. Incremente métrica de hit e logue.

---

## Passo 5 — Implementar miss + TTL

```csharp
var proposta = await fonte.ObterAsync(id, ct);
if (proposta is null) return null; // não cacheia ausência neste lab

await cache.SetStringAsync(
    chave,
    JsonSerializer.Serialize(proposta),
    new DistributedCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
    },
    ct);
```

TTL absoluto de 30s é escolha **didática**. Em produção o valor depende de:

- quão “fresco” o dado precisa ser
- quanto o banco aguenta de miss
- se você invalida bem nas escritas

---

## Passo 6 — Endpoint GET

```csharp
app.MapGet("/propostas/{id:long}", async (long id, ConsultaProposta consulta, CancellationToken ct) =>
{
    var proposta = await consulta.ObterAsync(id, ct);
    return proposta is null ? Results.NotFound() : Results.Ok(proposta);
});
```

Minimal APIs injetam `ConsultaProposta` automaticamente (está no DI como Scoped).

---

## Passo 7 — Invalidação após escrita

Ordem mental correta:

1. Persistir na fonte (**confirmação**)
2. Remover a chave conhecida do cache
3. Enfileirar sincronização da busca (outbox)

Se você invalidar **antes** de gravar, outra request pode ler o valor antigo da fonte e **recolocar** o valor velho no cache (pior).

`RemoveAsync` é **idempotente**: remover duas vezes a mesma chave não quebra.

---

## Passo 8 — Busca com Query DSL

Não faça:

```csharp
// ruim: concatenar termo do usuário na string do DSL
var body = "{ \"query\": { \"match\": { \"descricao\": \"" + termo + "\" }}}";
```

Faça: objeto anônimo / DTO → `PostAsJsonAsync`. O serializador escapa caracteres perigosos.

Filtro `term` em `status` (keyword) + `match` em `descricao` (text) é o exemplo da aula.

---

## Passo 9 — Observabilidade mínima

Exponha `/metrics/cache`:

- `cacheHits`
- `cacheMisses`
- `cacheErrors` ← separado! Erro de Redis **não** é miss
- `consultasFonte`
- `outboxPendentes`

Hit ratio só faz sentido com erros separados e com uma **janela de tempo** definida (último minuto, última hora…).

---

## Passo 10 — Desafio 2 em linguagem humana

### Redis caiu. E agora?

Política deste lab: **fallback na fonte**. A API continua respondendo; a origem paga o preço. Por isso limitamos concorrência (`SemaphoreSlim`) e timeout.

### Stampede (manada de misses)

Dez mil usuários pedem o mesmo ID no instante em que a chave expira. Sem proteção, são dez mil queries no banco.

Aqui: **single-flight** — a primeira request carrega; as outras da **mesma instância** esperam o mesmo `Task`.

Isso **não** protege entre dois pods diferentes. Para isso existiria lock no Redis.

### Elasticsearch caiu

Devolver `[]` seria mentira (“não há propostas”). Devolvemos **502** com mensagem clara.

### Outbox

Imagine: commit no banco ok, mas a rede cai antes de indexar. Sem outbox, o documento some do índice para sempre (até alguém reparar). Com outbox, o evento fica pendente e o worker tenta de novo. Como o PUT usa o mesmo ID, tentar duas vezes é seguro (**idempotência**).

---

## Checklist de estudo (marque mentalmente)

- [ ] Sei explicar hit vs miss com métrica
- [ ] Sei montar a chave com versão
- [ ] Sei por que invalidamos depois do commit
- [ ] Sei por que TTL sozinho não resolve consistência forte
- [ ] Sei por que busca falha ≠ zero hits
- [ ] Sei o limite do lock local vs várias instâncias
