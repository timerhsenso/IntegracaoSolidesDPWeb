# IntegracaoSolidesDP

Worker .NET 10 que sincroniza **RHSenso → Sólides DP** (antiga Tangerino) para a **ADN**. A direção é única: o worker lê o banco da folha (`bd_rhu_adn`) e envia para a API do Sólides DP. Ele roda como serviço Windows (ou systemd/container), na frequência configurada no `appsettings.json`.

> **Atenção:** esta integração **não** é a do Sólides Gestão (RH), que fica em `solides-employee-sync` e usa `app.solides.com/.../externos/colaboradores`. O Sólides DP é a API `https://employer.tangerino.com.br` ([documentação](https://docs.tangerino.com.br/)).

## Escopo (combinado na call)

| Item | Endpoint no Sólides DP | Fonte no RHSenso |
|---|---|---|
| Cargo | `POST /job-role/register` | `cargo1` (só os cargos usados por colaboradores em escopo) |
| Local de trabalho / Setor | `POST /workplace/register?allowUpdate=true` | `test1`: a filial, que na ADN é o posto/cliente de alocação |
| Colaborador | `POST /employee/register?allowUpdate=true` | `func1` (+ `tcus1`, `test1`, `tsitu1`) |
| Desligamento / transferência de empresa | `POST /employee/dismiss` | `func1` com situação 08 ou 09 |
| Lançamento de férias | `POST /adjustment/register/1.1`, `PUT /adjustment/update/{id}` | `feria2` (cada parcela vira um lançamento) |

Fora do escopo:
- **Escala de trabalho, fuso e gestor:** são configurados no DP. O worker só referencia a escala e a regra de ponto.
- **"Emissão de folha de ponto":** é um `GET /time-sheet` que devolve um PDF, ou seja, um fluxo DP → RHSenso.

## Como funciona

Cada execução segue esta ordem de dependência:

```
referências no DP (token, escala, regra de ponto, empresas por CNPJ, motivo FÉRIAS)
  → cargos → locais → desligamentos/transferências → colaboradores → férias
```

- **Idempotência.** Cada payload tem um hash SHA-256 gravado no estado. Um registro sem mudança não gera nenhuma chamada à API, então a segunda execução seguida não escreve nada.
- **Identidade do colaborador: `{cdempresa}-{nomatric}`.**
  - A filial fica de fora da chave. Na ADN a filial é o cliente onde a pessoa está alocada, e uma transferência entre filiais cria outra linha no `func1` com a mesma matrícula (a antiga fica 09). Com a chave sem filial, essa transferência vira um update de local e o histórico de ponto não se divide.
  - Uma transferência para **outra empresa** é um vínculo novo: primeiro o vínculo antigo é desligado no DP (`TRANSFERENCIA_GRUPO_EMPRESARIAL`), depois o novo é criado.
  - O esquema da chave fica gravado no estado. O worker recusa iniciar se ele mudar.
- **Quem entra.** `tpcolab` 1 (Empregado) e 2 (Estagiário), configurável. Afastados continuam ativos. Pré-cadastro (99) é ignorado.
- **Desligados históricos nunca são enviados.** Só é desligado no DP quem a própria integração criou lá.
- **CPF repetido entre ativos.** Fica fora e aparece no relatório (`skipped_duplicate_cpf`), a menos que `Sync:AllowDoubleBind=true`.
- **Escala e regra de ponto.** Na criação vão as padrões da conta (ou as do config). Na atualização, o worker reenvia a escala que já está no DP, para não desfazer o que o RH configurou lá.
- **`effectiveDate` = `max(admissão, Sync:GoLiveDate)`.** Assim o DP não calcula ponto retroativo para quem já trabalhava antes do go-live.
- **Férias.**
  - **Status** (`feria2.flconfirm`):
    - 2, 3, 4 e 6 → `APROVADO`.
    - 1 (Programada) → não envia, ou envia como `PENDENTE` com `Sync:FeriasEnviarProgramadas=true`.
    - 7 (Reprogramada), ou linha apagada → o lançamento é excluído no DP.
  - **Não duplica.** O POST de férias não tem chave idempotente. Por isso o worker grava `pending` antes de enviar, não faz retry automático, e reconcilia pelo marcador `RHSenso:{feria2.id}` em `observation`.
- **Travas de segurança.**
  - `Sync:DryRun=true` por padrão.
  - `Sync:MaxCreatesPerRun` e `Sync:MaxCancellationsPerRun` limitam o que uma execução pode fazer.
  - A execução aborta se o banco devolver 0 colaboradores.
  - Há um lock por instância (`sp_getapplock`).
  - Fora de Production, a API real só é usada com opt-in explícito.

## Configuração

Toda chave pode vir do `appsettings.json`, de uma variável de ambiente (`Sync__DryRun=false`) ou da linha de comando (`--Sync:DryRun=false`).

| Chave | Padrão | Observação |
|---|---|---|
| `ConnectionStrings:Rhu` | — | Obrigatória. Leitura em `dbo`; DDL/DML no schema `solidesdp` (veja INSTALL.md). |
| `SolidesDP:BaseUrl` | `https://employer.tangerino.com.br` | Em Development aponta para o fake (`http://localhost:5080`). |
| `SolidesDP:Token` | — | Gerado no DP em Empregador → Integrações. Obrigatório fora do dry-run. |
| `SolidesDP:TimeoutSeconds` | `30` | Por tentativa; o total é 4x. |
| `SolidesDP:SkipUnifiedSync` | `true` | Não propaga para a base unificada (CUC) da Sólides. Confirmar com a Sólides. |
| `SolidesDP:AllowProductionApiOutsideProduction` | `false` | Opt-in para usar a API real fora de Production. |
| `Execution:Interval` | `00:30:00` | Frequência. Use esta **ou** `TimesOfDay`. |
| `Execution:TimesOfDay` | — | Ex.: `["07:00", "13:00"]`, no fuso abaixo. |
| `Execution:TimeZone` | `America/Bahia` | Fuso dos horários e das datas do RHSenso. |
| `Sync:DryRun` | `true` | Simula sem chamar a API e gera o relatório. |
| `Sync:GoLiveDate` | — | Obrigatória fora do dry-run. |
| `Sync:TiposColaborador` | `[1, 2]` | `func1.tpcolab`. |
| `Sync:EmpresasIncluidas` | `[]` (todas) | Rollout por empresa. |
| `Sync:ExternalIdAllowList` | `[]` (todos) | Piloto com poucas pessoas (`"14-00901482"`). |
| `Sync:WorkScheduleExternalId` / `Sync:PunchRuleExternalId` | padrão da conta | Escala e regra de ponto dos novos colaboradores (`--discover`). |
| `Sync:CompanyMode` | `ResolveByCnpj` | Casa a empresa do DP pelo CNPJ da filial; `None` não envia empresa. |
| `Sync:CreateMissingCompanies` | `false` | Sem correspondência de CNPJ, o colaborador fica `blocked`. |
| `Sync:AllowDoubleBind` | `false` | Envia CPFs repetidos com `doubleBindEmployee=true`. |
| `Sync:MotivoDemissaoMap` | mapa de `tcre1` | `cdcausres` → `resignationReason`; o que não estiver mapeado vira `OUTROS`. |
| `Sync:FeriasJanelaDias` | `60` | Envia férias com término a partir de hoje − N dias. |
| `Sync:FeriasEnviarProgramadas` | `false` | Envia as férias "Programadas" como `PENDENTE`. |
| `Sync:FeriasMotivoId` | descoberto | Id do motivo FÉRIAS no DP. |
| `Sync:FeriasEndDateMode` | `InicioDoDiaSeguinte` | Como calcular o `endDate` (calibrar com o DP real). |
| `Sync:MaxCreatesPerRun` / `Sync:MaxCancellationsPerRun` | `300` / `20` | Travas de segurança. |

## Comandos

```bash
IntegracaoSolidesDP                  # serviço (frequência do Execution)
IntegracaoSolidesDP --check-config   # valida config, banco e token
IntegracaoSolidesDP --discover       # lista empresas, escalas, regras e motivos do DP
IntegracaoSolidesDP --dry-run        # execução simulada + relatório
IntegracaoSolidesDP --run-once       # uma execução (Sync:DryRun decide se é real)
IntegracaoSolidesDP --reconcile [--repair]  # confere no DP os colaboradores enviados
```

Cada execução grava o seguinte:
- `solidesdp.runs` e `solidesdp.run_items` no banco;
- `reports/run-*.csv`, que abre no Excel, e `reports/run-*.json`;
- logs em `logs/`.

## Publicação (CI/CD)

Cada push na `main` dispara o workflow [`ci`](.github/workflows/ci.yml), sem pull request:

1. **Testes:** unitários, de contrato, de SQL e ponta a ponta contra o fake.
2. **Pacotes:**
   - `win-x64.zip`: serviço Windows, com os scripts `install-service.ps1` e `uninstall-service.ps1`;
   - `linux-x64.tar.gz`: systemd, com `install.sh`, `uninstall.sh` e a unit;
   - `SHA256SUMS.txt`.
3. **Smoke test dos pacotes instalados de verdade:**
   - **Windows:** o serviço é instalado, atualizado (sem perder o `appsettings.json`) e removido num runner Windows.
   - **Linux:** o binário e a imagem Docker rodam contra SQL Server + fake (`check-config`, `dry-run`, execução real e uma segunda execução que não pode escrever nada). Depois o pacote é instalado, atualizado e removido via systemd.
4. **Release:** só se tudo passar. Cria a tag `v<VersionPrefix>.<número da execução>` (ex.: `v1.0.12`), com os pacotes e notas, e publica a imagem `ghcr.io/rhsenso/integracaosolidesdp:<versão>` / `latest`.

- **Versão maior ou menor:** altere `VersionPrefix` no `Directory.Build.props`.
- **Instalação na ADN:** veja o [INSTALL.md](INSTALL.md).
- **Smoke local:** `scripts/smoke-package.sh binary <pasta-publicada>` ou `scripts/smoke-package.sh docker <imagem>`.

## Sem homologação: o fake da API

O Sólides DP **não tem ambiente de homologação**. Por isso todo o desenvolvimento e os testes rodam contra um fake da API, `src/SolidesDP.Fake`. Ele guarda estado, segue o Swagger do fornecedor, tem perfis para os comportamentos que a documentação não define e permite injetar falhas. Detalhes em [src/SolidesDP.Fake/README.md](src/SolidesDP.Fake/README.md).

## Desenvolvimento

```bash
scripts/dev-sql.sh ~/Downloads/2026-08-11__WIN-LGBL8U4SU4T__bd_rhu_adn.bak   # SQL Server local + restore
docker compose -f docker-compose.dev.yml up -d                                # fake da API em :5080
cd src/IntegracaoSolidesDP.Worker && DOTNET_ENVIRONMENT=Development dotnet run -- --dry-run
dotnet test                                                                   # unit + contrato + SQL + ponta a ponta
```

- **Testes de contrato.** Garantem que todo campo, enum e parâmetro enviado existe no Swagger vendorizado (`spec/tangerino-employer.json`). Para atualizar o Swagger: `scripts/update-spec.sh`.
- **Testes de SQL e ponta a ponta.** Usam Testcontainers, então precisam de Docker.

## Para a implantação na ADN (não bloqueia desenvolvimento nem testes, que usam o fake)

- **Token** de integração do DP da ADN.
- **Conta de teste/trial** do DP para servir de sandbox, já que não há homologação.
- **Estrutura de conta:** uma conta para todos os CNPJs, ou uma por CNPJ? Se for uma por CNPJ, sobe uma instância por token, com `Sync:InstanceName` e `Sync:EmpresasIncluidas`.
- **Escala e regra de ponto padrão**, e **data de go-live**.
- **CUC:** o DP e o Gestão da ADN compartilham a base unificada? Isso define `SolidesDP:SkipUnifiedSync`.
- **Férias "Programadas" que já passaram.** No banco atual há férias com data no passado ainda em `flconfirm = 1` (Programada), ou seja, o RH não as liberou no RHSenso. Com o padrão, elas nunca vão para o DP. É preciso confirmar o processo da ADN: liberar no RHSenso, ou ligar `Sync:FeriasEnviarProgramadas`.
- **Checklist de calibração** quando chegar o token: veja INSTALL.md, seção "Primeiro contato com a API real".
