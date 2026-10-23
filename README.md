# Aula 52 — Redis, cache distribuído e Elasticsearch em .NET

Projeto didático da FIAP (Formação Backend C# / CAIXA), alinhado ao PDF **Aula52-Redis-Cache-Elasticsearch-NET** e ao zip **ProductCatalog**.

Pasta do projeto: `C:\Users\Paulo\Aula52`

---

## 1. O que você vai aprender (mapa mental)

Imagine um e-commerce com **várias réplicas** da mesma API:

| Peça | Papel | Analogia |
| --- | --- | --- |
| **API** | Coordena a requisição | Atendente |
| **Fonte (banco / memória)** | Estado **autoritativo** (verdade oficial) | Livro-caixa |
| **Redis** | Cópias **temporárias** por chave, compartilhadas entre instâncias | Post-it rápido no balcão |
| **Elasticsearch** | **Busca** sobre uma **projeção** dos dados | Índice de biblioteca |

Cache e índice **não** são a mesma coisa que o banco:

- Ciclos de atualização diferentes
- Podem ficar **atrasados** por alguns segundos (janela de inconsistência)
- Você define quanto atraso o negócio aceita

### Cache local × cache distribuído

- `IMemoryCache` → memória **desta** instância. Outra réplica não vê.
- Redis + `IDistributedCache` → várias instâncias compartilham o mesmo serviço.
- A aplicação serializa (JSON) e monta a **chave**.

---

## 2. Padrão cache-aside (caminho de leitura)

É o padrão da aula:

1. Procurar no cache (`GetStringAsync`)
2. **Hit** → desserializar e devolver (**fonte não é consultada**)
3. **Miss** → consultar a fonte
4. Se existir, **gravar** no cache com **TTL** e devolver
5. Se **não** existir na fonte, neste exemplo **não** cacheamos ausência

No código: `src/Aula52/Cache/ConsultaProposta.cs`

```text
GET /propostas/42
   │
   ├─ Redis tem aula52:proposta:v1:42 ? ──sim──► HIT (métrica + log)
   │
   └─ não ──► MISS ──► fonte.ObterAsync(42)
                          │
                          ├─ null ──► 404 (sem gravar cache)
                          └─ dto ──► SetString + TTL 30s ──► 200
```

### Convenção de chave

```text
InstanceName + "proposta:" + versão + ":" + id
exemplo: aula52:proposta:v1:42
```

A **versão (`v1`)** entra na chave para você poder mudar o formato do JSON sem ler lixo antigo.

### Invalidação (escrita)

Depois de **confirmar** a escrita na fonte:

```text
await escrita.AtualizarAsync(...)
await cache.RemoveAsync("proposta:v1:{id}")
await outbox.Enqueue(...)   // sincroniza busca depois
```

Código: `src/Aula52/Write/AtualizacaoProposta.cs`

Banco + Redis **não** formam uma transação distribuída. Sempre existe uma **janela** em que outra leitura pode repovoar o cache ou o índice ainda está atrasado. O **TTL** limita parte do atraso; regras críticas podem exigir leitura direta na fonte.

---

## 3. Como rodar

### Opção A — sem Docker (padrão do `appsettings.json`)

`Redis:UseMemoryCache = true` usa `AddDistributedMemoryCache()` com a **mesma** API `IDistributedCache`. Ideal para estudar e para os testes.

```powershell
cd C:\Users\Paulo\Aula52\src\Aula52
dotnet run --urls http://localhost:5052
```

### Opção B — Redis + Elasticsearch (como no PDF)

1. Suba o Docker Desktop.
2. Na raiz do projeto:

```powershell
docker compose up -d
```

3. Em `appsettings.json` (ou variáveis de ambiente):

```json
"Redis": {
  "Configuration": "localhost:56338",
  "InstanceName": "aula52:",
  "UseMemoryCache": false
},
"Elasticsearch": {
  "Url": "http://localhost:59238/"
}
```

4. Rode a API na porta **5052** (igual ao material).

### Testes automatizados (Desafios 1 e 2)

```powershell
cd C:\Users\Paulo\Aula52
dotnet test Aula52.slnx
```

Resultado esperado: **14 aprovados**.

---

## 4. Endpoints

| Método | Rota | O que faz |
| --- | --- | --- |
| GET | `/propostas/{id}` | Cache-aside |
| PUT | `/propostas/{id}` | Atualiza fonte + invalida cache + outbox |
| GET | `/busca?termo=` | Query DSL no Elasticsearch |
| GET | `/metrics/cache` | Hits, misses, erros, consultas à fonte, outbox |
| POST | `/sync/outbox` | Processa lote do outbox (laboratório) |

### Exercício manual (Desafio 1)

Com a API no ar:

```powershell
# 1ª leitura → MISS (veja o log e /metrics/cache)
curl http://localhost:5052/propostas/42

# 2ª leitura → HIT (consultasFonte não sobe)
curl http://localhost:5052/propostas/42

curl http://localhost:5052/metrics/cache
```

Com Redis real:

```powershell
docker exec aula38-redis redis-cli TTL aula52:proposta:v1:42
```

**Importante (dica do PDF):** duas respostas JSON iguais **não** provam hit. Prove com:

- contagem de consultas à fonte (`consultasFonte`)
- métricas `cacheHits` / `cacheMisses`
- logs `HIT` / `MISS`
- `TTL` no Redis

Atualização + invalidação:

```powershell
curl -X PUT http://localhost:5052/propostas/42 ^
  -H "Content-Type: application/json" ^
  -d "{\"id\":42,\"descricao\":\"Notebook 32GB\",\"status\":\"aprovada\",\"valor\":5200}"

curl http://localhost:5052/propostas/42
```

A leitura após o PUT deve ser **miss** de novo e trazer o valor novo.

---

## 5. Elasticsearch: busca e indexação

### Busca (Query DSL)

`BuscaPropostas` monta um objeto C# e serializa — **não** concatena o termo do usuário dentro de uma string DSL (evita “injeção” de query).

Filtro didático: só `status = aprovada`, com `match` em `descricao`.

Se o Elasticsearch falhar (HTTP 5xx, timeout, rede), a API devolve **502 Problem** — **nunca** uma lista vazia fingindo “não achei nada”. Ausência de hits ≠ erro de infraestrutura.

### Indexação

`PUT propostas/_doc/{id}?refresh=wait_for`

- ID estável → atualiza o mesmo documento
- `wait_for` → facilita ver o documento logo no laboratório (near real-time)

### Outbox (sincronização)

Fluxo da aula (Parte 2 / Desafio 2):

1. Gravar mudança (+ evento) de forma atômica do ponto de vista da fonte
2. Worker lê outbox e publica no Elasticsearch
3. Destino **idempotente** (mesmo ID sobrescreve)
4. Repetição **não** garante ordem entre várias réplicas — trate versão/timestamp

Código: `src/Aula52/Sync/Outbox.cs`

---

## 6. Desafio 1 — fundamentos (o que entregamos)

| Critério | Onde está |
| --- | --- |
| 1ª leitura consulta fonte | `Desafio1_CacheAsideTests` + `ConsultaProposta` |
| 2ª leitura usa cache no TTL | métricas Hits/Misses + `ContagemConsultas` |
| Após expiração, fonte de novo | teste com TTL 1s |
| Update invalida chave correta | `AtualizacaoProposta` + teste |
| Busca encontra / falha explícita | `Desafio1_BuscaTests` |

---

## 7. Desafio 2 — resiliência (o que entregamos)

| Critério | Política neste projeto |
| --- | --- |
| Redis fora do ar | `FallbackToSourceOnCacheFailure = true`; conta `CacheErrors` **separado** de miss |
| Limitar carga no fallback | `SemaphoreSlim` (`MaxConcurrentSourceFetches`) + timeout (`SourceFallbackTimeoutMs`) |
| Falha de busca | HTTP 502, sem `[]` enganoso |
| Misses simultâneos | **Single-flight** por chave (`ConcurrentDictionary` + `Lazy`) — coordena **só esta instância** |
| Retry / ordem | Outbox com retry; PUT por ID idempotente; processamento ordenado por `CriadoEm` |

**Lembrete do professor:** lock local **não** coordena várias instâncias da API. Em produção, stampede entre pods pediria lock distribuído (ex.: Redis lock) ou stale-while-revalidate.

Testes: `Desafio2_ResilienciaTests.cs`

---

## 8. Ligação com o ProductCatalog.zip

O zip é o **catálogo de produtos** em CQRS (Domain / Application / Infrastructure / Api), já com:

| Conceito da Aula 52 | Onde no ProductCatalog |
| --- | --- |
| Cache-aside | `GetProductByIdQueryHandler` + `RedisProductDetailsCache` |
| Chave + TTL | `products:{guid}` / `Cache:ProductDetailsTtlMinutes` |
| Invalidação após escrita | `UpdateProductCommandHandler` → `_cache.RemoveAsync` |
| Busca Elasticsearch | `SearchProductsQuery` + `ElasticsearchProductIndex` |
| Falha de busca sem vazio enganoso | `ProductErrors.SearchUnavailable` → HTTP 503 |
| Redis cai → lê SQL | `RedisProductDetailsCache` captura exceção e devolve `null` (vira miss/fallback) |

Esta pasta **Aula52** é a versão **mínima e didática** do PDF (propostas + métricas + outbox + testes dos desafios). O ProductCatalog é a versão **de produto/turma** com SQL Server, MediatR, Prometheus e Grafana.

Para estudar o zip:

```powershell
Expand-Archive "C:\Users\Paulo\OneDrive\Área de Trabalho\ProductCatalog.zip" -DestinationPath "C:\Users\Paulo\ProductCatalog" -Force
cd C:\Users\Paulo\ProductCatalog
# com Docker Desktop ligado:
docker compose up -d --build
```

Swagger: http://localhost:5124/swagger  

---

## 9. Perguntas da correção (com respostas curtas)

1. **Como comprovou o hit sem olhar só a resposta?**  
   Contagem de consultas à fonte estável + `cacheHits` / log `HIT`.

2. **O dado ficou obsoleto por quanto tempo?**  
   Até o TTL (30s no lab) ou até a invalidação após o PUT. Se o `Remove` falhar, até o TTL.

3. **Qual limite protege a fonte quando o cache falha?**  
   `MaxConcurrentSourceFetches` + timeout de fallback.

4. **Como uma atualização perdida no índice é recuperada?**  
   Evento fica no outbox; worker faz retry; reconciliação pode reindexar por ID.

---

## 10. Estrutura de pastas

```text
Aula52/
  docker-compose.yml          # Redis :56338, ES :59238
  src/Aula52/
    Contratos.cs              # DTO, interfaces, métricas
    Program.cs                # DI + endpoints
    Cache/ConsultaProposta.cs # cache-aside + stampede + fallback
    Write/AtualizacaoProposta.cs
    Search/BuscaPropostas.cs
    Sync/Outbox.cs
    Fonte/PropostaFonteMemoria.cs
  tests/Aula52.Tests/
    Desafio1_*.cs
    Desafio2_*.cs
```

Bom estudo — foque em **comportamento observável** (métricas e contagens), não só no JSON bonito da resposta.
