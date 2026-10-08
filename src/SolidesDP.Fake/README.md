# SolidesDP.Fake

Fake **stateful** da API REST da Sólides DP (antiga Tangerino).

## Por que existe

O fornecedor **não tem ambiente de homologação**. Este fake é o alvo contra o qual o worker de integração
(`src/IntegracaoSolidesDP.Worker`) é desenvolvido e testado: ele guarda estado em memória (colaboradores, lançamentos,
cargos...), valida os requests contra o Swagger 2.0 do fornecedor (`spec/tangerino-employer.json`) e responde no formato
das `definitions` desse mesmo Swagger.

Fidelidade ao Swagger vem antes de qualquer outra coisa:

- o Swagger vai **embutido** no assembly e é a fonte da verdade da validação de requests (propriedades desconhecidas,
  tipos, enums, `required`);
- os testes de contrato (`tests/SolidesDP.Fake.Tests/Contracts`) chamam todos os endpoints e validam, recursivamente,
  cada resposta contra a definition do Swagger (todas as propriedades devolvidas existem na definition, `$ref` e nomes
  genéricos `Page«X»` resolvidos, valores dentro dos enums).

> O que o Swagger **não** diz (mensagens de erro, regras de negócio, semântica de update...) foi **assumido** e está
> listado em [Comportamentos assumidos](#comportamentos-assumidos). Tudo ali precisa ser calibrado quando existir um
> token real.

## Como rodar

### `dotnet run`

```bash
dotnet run --project src/SolidesDP.Fake            # http://localhost:5080 (launchSettings.json)
curl -H "Authorization: Basic fake-token" http://localhost:5080/test
```

### Docker

O contexto de build é a **raiz do repositório** (o build precisa de `global.json`, `Directory.*.props` e `spec/`):

```bash
docker build -f src/SolidesDP.Fake/Dockerfile -t solides-dp-fake .
docker run --rm -p 5080:8080 solides-dp-fake          # dentro do container escuta na 8080
```

Configuração por variáveis de ambiente (ver [Configuração](#configuração)):

```bash
docker run --rm -p 5080:8080 \
  -e Fake__Tokens__0=meu-token \
  -e Fake__Behavior__ErrorStyle=Http \
  solides-dp-fake
```

### Em testes (em processo)

Use **sempre** o tipo marcador `SolidesDP.Fake.FakeApi` (o `Program` do fake é interno, justamente para não colidir
com o `Program` do worker):

```csharp
await using var factory = new WebApplicationFactory<SolidesDP.Fake.FakeApi>()
    .WithWebHostBuilder(b => b.UseSetting("Fake:Behavior:ErrorStyle", "Http"));
using var http = factory.CreateClient();
http.DefaultRequestHeaders.Authorization = new("Basic", "fake-token");
var admin = new SolidesDP.Fake.Testing.FakeAdminClient(factory.CreateClient());   // /_fake não exige token

await admin.ResetAsync();
// ... exercita o cliente ...
var estado = await admin.GetStateAsync();            // tipado (FakeState); GetStateDocumentAsync() = JsonDocument
var chamadas = await admin.GetRequestsAsync();       // diário de requests
```

`SolidesDP.Fake.Testing.FakeAdminClient`: `ResetAsync`, `GetStateAsync`, `GetStateDocumentAsync`, `GetRequestsAsync`,
`ClearRequestsAsync`, `GetBehaviorAsync`, `SetBehaviorAsync`, `UpdateBehaviorAsync(Action<FakeBehavior>)`,
`AddFaultAsync`, `GetFaultsAsync`, `ClearFaultsAsync`.

### Em testes (Kestrel real)

`SolidesDP.Fake.Testing.FakeServer.StartAsync(settings)` sobe o fake num Kestrel real em porta loopback efêmera
(`BaseAddress`, `CreateClient()`, `CreateAdminClient()`). Use quando o transporte importa — principalmente com
`CommitThenDrop`: no Kestrel o cliente recebe `HttpRequestException` (conexão resetada); no `TestServer` do
`WebApplicationFactory` o `HttpContext.Abort()` vira `OperationCanceledException("The application aborted the request.")`,
que um cliente com política de retry pode **não** tratar como falha de rede.

## Configuração

Seção `Fake` (appsettings, variáveis de ambiente `Fake__...` ou configuração do `WebApplicationFactory`):

| Chave | Padrão | Descrição |
|---|---|---|
| `Fake:Tokens` (`string[]`) | `["fake-token"]` | Tokens válidos. Header `Authorization: Basic <token>`. Ausente/inválido → `401`. |
| `Fake:SeedFile` | *(vazio)* | Caminho de um JSON de seed (mesmo formato de `GET /_fake/state`). Vazio = recurso embutido `fake-seed.json`. |
| `Fake:Behavior:*` | ver abaixo | Perfil de comportamento inicial (restaurado por `POST /_fake/reset`). |

Arrays em variável de ambiente: `Fake__Tokens__0`, `Fake__Tokens__1`...

## Perfis de comportamento (`Fake:Behavior`)

Todos trocáveis em runtime por `PUT /_fake/behavior`.

| Propriedade | Padrão | Efeito |
|---|---|---|
| `ErrorStyle` | `ResponseEntity` | `ResponseEntity`: endpoints cujo schema 200 é `ResponseEntity` (`POST /employee/register`, `POST /employee/dismiss`, `PUT /adjustment/{id}`) respondem **HTTP 200** com `{"body":{"message","error"},"statusCode":"BAD_REQUEST","statusCodeValue":400}`; os de lançamento (`POST /adjustment/register`, `/register/1.1`, `PUT /adjustment/update/{id}`) respondem **HTTP 200** com `{"registered":false,"message":"..."}`; todos os demais respondem **HTTP 4xx** com corpo Spring `{timestamp,status,error,message,path}`. `Http`: todo erro de validação/negócio vira HTTP 400/404/409 com corpo Spring. |
| `RequireBasicPrefix` | `true` | `false` aceita também o token puro no header. |
| `RejectUnknownFields` | `true` | Rejeita propriedades fora da definition do Swagger (pega *drift* do cliente). |
| `UniqueCpfAmongActive` | `true` | CPF de outro colaborador não demitido → `duplicate_cpf` (salvo `doubleBindEmployee=true`). |
| `UpdateOmittedFields` | `Keep` | Em `allowUpdate=true`: propriedades ausentes/nulas mantêm (`Keep`) ou limpam (`Clear`) o valor guardado. |
| `RegisterFiredExternalId` | `Error` | `POST /employee/register` com externalId de demitido: `Error` (`employee_fired`), `CreateNew` (novo id, mesmo externalId) ou `Reactivate` (mesmo id, volta a ativo). |
| `JobRoleDuplicateExternalId` | `Error` | `POST /job-role/register` com externalId existente: `Error` (`duplicate_external_id`), `Duplicate` (cria outro) ou `Update`. |
| `RejectOverlappingAdjustments` | `true` | Lançamento que se sobrepõe a outro (não excluído) do mesmo colaborador → `overlap`. |

Códigos de erro (`body.error` e header de resposta `X-Fake-Error-Code`) e status HTTP: `required_field`, `unknown_field`,
`invalid_value`, `invalid_reference`, `invalid_date_range` → 400; `not_found` → 404; `already_exists`, `duplicate_cpf`,
`duplicate_cnpj`, `duplicate_external_id`, `employee_fired`, `already_fired`, `overlap` → 409; `injected_fault` → o status da falha.

## Endpoints implementados

Caminhos, parâmetros e formatos exatamente como no Swagger:

`GET /test` · `GET|POST /companies` · `POST /job-role/register` · `GET /job-role/find` · `GET /job-role/find-all` ·
`POST /workplace/register` · `GET /workplace/find` · `GET /workplace/find-all` · `GET /work-schedule` ·
`GET /work-schedule/default` · `GET /v2/punch-rule` · `POST /employee/register` · `GET /employee/find` ·
`GET /employee/find-all` · `POST /employee/dismiss` · `GET /adjustment-reason/find-all` · `POST /adjustment/register` ·
`POST /adjustment/register/1.1` · `GET /adjustment/find-all` · `GET /adjustment/{id}` · `PUT /adjustment/update/{id}` ·
`PUT /adjustment/{id}`.

Paginação (todos os `find-all`/listagens): `page`/`size` **ou** `pageNumber`/`pageSize`, base 0, tamanho padrão 20 (máx. 1000);
resposta no formato `Page` do Spring (`content, first, last, number, numberOfElements, size, sort, totalElements, totalPages`).

### Seed (embutido)

- 14 filiais (`companies`) com ids `3001..3014` e os CNPJs do cliente.
- Escalas: `1001` "ESCALA PADRAO 44H SEG-SEX" (`ESC-PADRAO`, padrão) e `1002` "ESCALA 12X36" (`ESC-12X36`).
- Regras de ponto: `2001` "REGRA PADRAO" (`REGRA-PADRAO`, padrão) e `2002` "REGRA ESTAGIO" (`REGRA-ESTAGIO`).
- Motivos de lançamento: `1` FÉRIAS, `4` ABONO, `5` ATESTADO MÉDICO, `6` FOLGA.
- Sem cargos, locais, colaboradores ou lançamentos. Ids novos: auto-incremento por tipo de entidade a partir de `10000`.

## Endpoints administrativos (`/_fake`, sem autenticação, fora do diário)

| Endpoint | Descrição |
|---|---|
| `GET /_fake/state` | `{companies, jobRoles, workplaces, workSchedules, punchRules, adjustmentReasons, employees, adjustments}` — registros internos completos (histórico de escalas, `fired`, `excluded`, `fields`...). |
| `POST /_fake/reset` | Recarrega o seed, zera ids, diário e falhas, restaura o comportamento da configuração (`204`). |
| `GET /_fake/requests` | Diário `[{seq, method, path, query, body, status, at, fault?}]` de todos os requests não-admin (inclusive `401`). |
| `DELETE /_fake/requests` | Limpa o diário (`204`). |
| `GET /_fake/behavior` · `PUT /_fake/behavior` | Lê / **substitui** o `FakeBehavior` (JSON camelCase, enums por nome; propriedades omitidas voltam ao padrão; propriedade desconhecida → `400`). |
| `POST /_fake/faults` · `GET /_fake/faults` · `DELETE /_fake/faults` | Cadastra / lista (com `remaining`) / remove regras de falha. |

## Injeção de falhas

`FaultRule`:

```json
{ "method": "POST", "path": "/adjustment/register", "times": 1, "kind": "CommitThenDrop",
  "status": 503, "retryAfterSeconds": 3, "delayMs": 500, "message": "texto do erro" }
```

- `method`: opcional (nulo = qualquer). `path`: exato ou prefixo quando termina em `*`. `times`: padrão 1; `-1` = ilimitado.
  Cada request que casa consome um uso; regras são aplicadas na ordem de cadastro. Requests sem credencial válida (`401`) e
  endpoints `/_fake` não consomem falhas.
- `kind`:
  - `Status` — responde `status` (padrão 500; ex.: 429/500/503) com corpo Spring, **sem** executar a operação; `retryAfterSeconds` adiciona `Retry-After`.
  - `Delay` — espera `delayMs` (padrão 1000) e processa normalmente.
  - `ErrorInBody` — **não** executa e devolve o formato de erro do endpoint para o `ErrorStyle` corrente (HTTP 200 nos endpoints `ResponseEntity`/lançamento quando `ErrorStyle=ResponseEntity`); `status` define o `statusCodeValue` (padrão 400).
  - `CommitThenDrop` — **executa e persiste** a operação e então derruba a conexão (`HttpContext.Abort()`): o cliente nunca recebe a resposta. Serve para provar que um retry não cria férias duplicadas.

```bash
H='Authorization: Basic fake-token'; J='Content-Type: application/json'

# 2 respostas 429 com Retry-After: 5 para qualquer GET em /companies
curl -X POST localhost:5080/_fake/faults -H "$J" \
  -d '{"method":"GET","path":"/companies","times":2,"kind":"Status","status":429,"retryAfterSeconds":5}'

# 503 em tudo que começa com /employee/ (prefixo), 3 vezes
curl -X POST localhost:5080/_fake/faults -H "$J" -d '{"path":"/employee/*","times":3,"kind":"Status","status":503}'

# latência de 2 s no próximo registro de colaborador
curl -X POST localhost:5080/_fake/faults -H "$J" -d '{"path":"/employee/register","kind":"Delay","delayMs":2000}'

# a próxima criação de lançamento falha "dentro do corpo" (HTTP 200 + {"registered":false,...})
curl -X POST localhost:5080/_fake/faults -H "$J" -d '{"path":"/adjustment/register","kind":"ErrorInBody","message":"falha simulada"}'

# o lançamento é gravado, mas a resposta nunca chega (conexão derrubada)
curl -X POST localhost:5080/_fake/faults -H "$J" -d '{"path":"/adjustment/register","kind":"CommitThenDrop"}'
curl -s localhost:5080/_fake/state | jq '.adjustments'      # ...e está lá

curl localhost:5080/_fake/faults                              # regras ativas + remaining
curl -X DELETE localhost:5080/_fake/faults                    # limpa
curl -X PUT localhost:5080/_fake/behavior -H "$J" -d '{"errorStyle":"Http","updateOmittedFields":"Clear"}'
```

## Comportamentos assumidos

O Swagger não especifica o que segue. Foram escolhas razoáveis do fake e **precisam ser calibradas** quando houver um token real
(todas as que são booleanas/enum estão em `FakeBehavior`; as demais estão no código e são fáceis de achar pelo texto da mensagem):

1. **Datas**: o fake responde epoch **ms** (número) em todos os campos `date-time` (a doc pública diz ms; o Swagger/springfox rotula as respostas como `string(date-time)`). Requests aceitam ms ou string ISO-8601 nesses campos.
2. **`ResponseEntity.statusCode`**: nome da constante do Spring (`"OK"`, `"CREATED"`, `"BAD_REQUEST"`), conforme a especificação da tarefa; o Swagger lista o enum como números em string (`"200"`).
3. **Corpos de erro**: o formato `{timestamp,status,error,message,path}` (Spring Boot), as mensagens (em inglês) e os códigos `error` (`duplicate_cpf`, `already_exists`, `not_found`, `already_fired`, `overlap`...) são inventados. O mapeamento de status (400/404/409) também.
4. **Falhas de transporte** (JSON malformado, corpo ausente, `Content-Type` ≠ `application/json` → `415`, parâmetro de query mal formatado) são **sempre** HTTP 4xx Spring, independente do `ErrorStyle`. Já propriedade desconhecida, tipo errado, enum inválido, campo obrigatório ausente e CPF/PIS inválido **seguem** o `ErrorStyle`.
5. **Propriedades desconhecidas**: o Jackson do Spring normalmente as ignora; o fake as rejeita (`RejectUnknownFields`) de propósito. Tipos são estritos (string num campo inteiro é erro, embora o Jackson coaja) e enums são *case-sensitive*. `null` equivale a ausente.
6. **CPF/PIS**: exatamente 11 dígitos, sem máscara, **sem** conferir dígito verificador. String vazia/em branco = ausente. CNPJ (`POST /companies`): 14 dígitos (aceita máscara), sem dígito verificador.
7. **Unicidade de CPF**: só entre colaboradores **não demitidos**; `doubleBindEmployee=true` no request libera.
8. **`allowUpdate`**: só atualiza colaborador **não demitido**; sem a flag, `externalId` existente é `already_exists`. Atualizar por `tangerinoId` (no body) também é suportado. Ignora `id` do body.
9. **`UpdateOmittedFields=Clear`**: limpa propriedades livres, CPF/PIS e referências (cargo/local/filial); escala e regra de ponto ausentes voltam ao **padrão** (como numa criação).
10. **Referências** (`jobRole`/`workplace`/`company`/`workSchedule` por id **ou** externalId; `punchRuleExternalId`): inexistente → `invalid_reference`. Sem escala/regra informada usa a padrão; sem cargo/local/filial fica vazio. Se id e externalId vêm juntos, o id vence.
11. **Histórico de escalas**: acrescenta uma entrada quando (escala, `workScheduleDateInMillis`) difere da última; `alterationDate` de cada entrada em `workScheduleList` = a data de vigência; `currentWorkSchedule` = a escala da última entrada (o fake não compara com "agora"). `workplaceList` traz só o local atual.
12. **`find`**: `ignoreFired` padrão `false` (devolve o demitido, `fired:true`); se há mais de um registro com o mesmo externalId, o ativo tem prioridade, senão o demitido mais recente. **`find-all`**: `showFired` ausente/`0` **oculta** demitidos, diferente de zero inclui (o Swagger só diz `integer`). `managerId`/`managerExternalId`/`managerDetails` são aceitos e ignorados. `offset` é aceito e ignorado.
13. **Corpo de sucesso de `employee/register`**: `EmployeeReturnDTO` completo (com `pin` de 6 dígitos gerado). `employee/dismiss`: `body` = `EmployeeReturnWithoutPinDTO` do demitido; exige `resignationDate` (a razão é opcional). `PUT /adjustment/{id}`: `body` = `AdjustmentReasonRecordResponseDTO`.
14. **Lançamentos**: `status` ausente = `APROVADO`; `fullDay` ausente herda o do motivo; colaborador demitido ou motivo inativo → erro. **Sobreposição** é inclusiva nas pontas, vale entre **todos** os motivos e status (inclusive `REPROVADO`) e ignora só os `excluded`. **Hipótese crítica**: se o fornecedor *não* recusa lançamentos sobrepostos, um retry após `CommitThenDrop` criaria férias duplicadas — configure `RejectOverlappingAdjustments=false` para simular isso e reconcilie via `GET /adjustment/find-all`.
15. **`excluded`**: o Swagger só expõe `excluded` no *request* do `PUT /adjustment/update/{id}`; nenhuma resposta o traz. Logo, só `ignoreExcluded=true` esconde excluídos (padrão `false`: aparecem, indistinguíveis); `GET /adjustment/{id}` devolve o lançamento mesmo excluído.
16. **`lastUpdate`** (`find-all`): filtro `>=` sobre a última alteração do registro.
17. **Paginação**: valores inválidos/negativos caem no padrão (como o resolver do Spring), `sort` é ignorado, `Page.sort` é `{"sorted":false,"unsorted":true,"empty":true}`.
18. **Escalas do seed**: `day` segue `java.util.Calendar` (1 = domingo ... 7 = sábado), horários em ms desde a meia-noite (44h = 08:00–12:00 / 13:00–17:48 de seg a sex). Regras de ponto trazem só um subconjunto de campos de `PunchRuleV2ResponseDTO`.
19. **Filial no colaborador** (`CompanyReturnDTO`): `accountStatus` fixo `PAGANTE`, `standard` fixo `false`.
20. **Sem** idempotência, limite de taxa, sincronização unificada (`skipUnifiedSync` é aceito e ignorado), vínculo com gestor (o header `gestorId` é só registrado) nem endpoints fora da lista acima — chamadas a eles dão `404` Spring.
21. **Autenticação**: o token é comparado por igualdade exata (`Basic` case-insensitive), o mesmo token vale para qualquer "empregador".
22. **`CommitThenDrop` em processo**: ver a nota sobre `FakeServer` acima — o tipo de exceção vista pelo cliente depende do servidor.
