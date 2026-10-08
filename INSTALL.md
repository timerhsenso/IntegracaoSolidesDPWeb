# Instalação — IntegracaoSolidesDP

O IntegracaoSolidesDP lê o banco do RHSenso (`bd_rhu_adn`) e envia cargos, locais de trabalho, colaboradores, desligamentos e férias para o **Sólides DP**. Ele roda como serviço, sem interface: a cada intervalo configurado, faz uma sincronização e grava um relatório.

Há três formas de instalação. Escolha **uma**:

| Forma | Quando usar | Pacote do release |
|---|---|---|
| [Serviço Windows](#a-serviço-windows) | Servidor Windows (o caso mais comum, junto do SQL Server) | `IntegracaoSolidesDP-<versão>-win-x64.zip` |
| [Serviço Linux (systemd)](#b-serviço-linux-systemd) | Servidor Linux x64 (Ubuntu, Debian, RHEL…) | `IntegracaoSolidesDP-<versão>-linux-x64.tar.gz` |
| [Docker](#c-docker) | Quem já opera containers | imagem `ghcr.io/rhsenso/integracaosolidesdp:<versão>` |

Os pacotes Windows e Linux são *self-contained*: **não precisa instalar o .NET**.

## 1. Pré-requisitos

- **Rede:** acesso ao SQL Server do RHSenso e saída HTTPS para `employer.tangerino.com.br:443`.
- **Token do Sólides DP:** gerado em Empregador → Integrações. Se o menu não aparecer, peça ao suporte da Sólides.
- **Um login SQL de privilégio mínimo.** O worker lê o `dbo` e só escreve no schema próprio `solidesdp`:

  ```sql
  CREATE LOGIN integracao_solidesdp WITH PASSWORD = '<senha forte>';
  GO
  USE bd_rhu_adn;
  CREATE USER integracao_solidesdp FOR LOGIN integracao_solidesdp;
  ALTER ROLE db_datareader ADD MEMBER integracao_solidesdp;
  EXEC('CREATE SCHEMA solidesdp AUTHORIZATION integracao_solidesdp');
  GRANT CREATE TABLE TO integracao_solidesdp;
  ```

  As tabelas `solidesdp.*` são criadas pelo próprio worker na primeira execução.

### Onde baixar

Cada versão fica em **[Releases](https://github.com/rhsenso/IntegracaoSolidesDP/releases)**, com o arquivo `SHA256SUMS.txt` para conferir a integridade.

O repositório é privado. Se a ADN não tiver acesso ao GitHub da RHSenso, a RHSenso baixa o pacote e o entrega. No caso do Docker, a RHSenso pode exportar a imagem com `docker save`.

## 2. Configuração (`appsettings.json`)

O arquivo fica junto do executável. O mínimo para começar:

```jsonc
{
  "ConnectionStrings": {
    "Rhu": "Server=<servidor-sql>;Database=bd_rhu_adn;User Id=integracao_solidesdp;Password=<senha>;Encrypt=True;TrustServerCertificate=True"
  },
  "SolidesDP": { "Token": "<token do Sólides DP>" },
  "Execution": {
    "Interval": "00:30:00",          // a cada 30 minutos...
    // "TimesOfDay": ["07:00", "13:00"], // ...ou em horários fixos (use só um dos dois)
    "TimeZone": "America/Bahia"
  },
  "Sync": {
    "DryRun": true,                  // COMECE em true: simula e gera relatório, sem enviar nada
    "GoLiveDate": "2026-11-01",      // primeiro dia de uso do ponto no Sólides DP
    "WorkScheduleExternalId": "",    // vazio = escala padrão da conta (veja --discover)
    "PunchRuleExternalId": ""        // vazio = regra de ponto padrão da conta
  }
}
```

- Atualizações **nunca sobrescrevem** o `appsettings.json` instalado.
- Os segredos podem ficar em variáveis de ambiente em vez do arquivo:
  - `ConnectionStrings__Rhu`
  - `SolidesDP__Token`
- Todas as opções estão no [README](README.md#configuração).

## A. Serviço Windows

1. Extraia o zip (ex.: em `C:\Temp\IntegracaoSolidesDP`).
2. Abra o **PowerShell como Administrador** nessa pasta e rode:

   ```powershell
   .\install-service.ps1                                    # instala em C:\Services\IntegracaoSolidesDP
   # ou: .\install-service.ps1 -InstallDir "D:\Servicos\IntegracaoSolidesDP"
   ```

   Na primeira vez, o script cria o `appsettings.json` na pasta de instalação e para, porque a configuração ainda está vazia.
3. Edite o `C:\Services\IntegracaoSolidesDP\appsettings.json` (seção 2) e rode o `install-service.ps1` de novo.
4. O script faz o seguinte:
   - registra o serviço **IntegracaoSolidesDP** com início automático e reinício em caso de falha;
   - roda `--check-config`;
   - **só inicia o serviço se a validação passar**.

Comandos úteis:

```powershell
Get-Service IntegracaoSolidesDP                     # status
Restart-Service IntegracaoSolidesDP                 # depois de mudar o appsettings.json
Get-Content C:\Services\IntegracaoSolidesDP\logs\*.log -Tail 50
.\uninstall-service.ps1                             # remove o serviço (mantém arquivos; -RemoveFiles apaga)
```

- **Atualizar:** extraia o zip novo e rode `install-service.ps1` de novo. O script para o serviço, troca os arquivos, preserva a configuração e inicia de novo.
- **Conta do serviço:** é a LocalSystem. Para autenticação integrada do Windows no SQL Server (sem usuário/senha na connection string), troque a conta em `services.msc` e use `Integrated Security=True`.

## B. Serviço Linux (systemd)

```bash
tar -xzf IntegracaoSolidesDP-<versão>-linux-x64.tar.gz
cd integracao-solidesdp
sudo ./install.sh                                    # instala em /opt/integracao-solidesdp
sudo nano /opt/integracao-solidesdp/appsettings.json # configure (seção 2)
sudo ./install.sh                                    # valida e inicia
```

O `install.sh` faz o seguinte:
- cria o usuário de sistema `integracao-solidesdp`;
- instala a unit `integracao-solidesdp.service`;
- roda `--check-config`, e **só inicia se a validação passar**.

Comandos úteis:

```bash
systemctl status integracao-solidesdp
sudo systemctl restart integracao-solidesdp
journalctl -u integracao-solidesdp -f                # logs (também em /opt/integracao-solidesdp/logs)
sudo ./uninstall.sh                                  # remove (--remove-files apaga /opt/integracao-solidesdp)
```

- **Segredos fora do arquivo:** podem ir em `/etc/integracao-solidesdp.env` (permissão 600), por exemplo `SolidesDP__Token=...`.
- **Requisito do sistema:** a biblioteca ICU, já presente em quase todas as distribuições. No Ubuntu/Debian é o pacote `libicu`.
- **Atualizar:** extraia o tar.gz novo e rode `sudo ./install.sh`.

## C. Docker

```bash
docker run -d --name integracao-solidesdp --restart unless-stopped \
  -e ConnectionStrings__Rhu="Server=<servidor-sql>;Database=bd_rhu_adn;User Id=integracao_solidesdp;Password=<senha>;Encrypt=True;TrustServerCertificate=True" \
  -e SolidesDP__Token="<token>" \
  -e Sync__DryRun=true -e Sync__GoLiveDate=2026-11-01 \
  -e Execution__Interval=00:30:00 \
  -v integracao-solidesdp-reports:/app/reports \
  ghcr.io/rhsenso/integracaosolidesdp:<versão>

docker logs -f integracao-solidesdp
docker run --rm -e ... ghcr.io/rhsenso/integracaosolidesdp:<versão> --check-config   # comandos avulsos
```

- **Configuração:** toda a configuração vai por variável de ambiente, no formato `Secao__Chave`.
- **Relatórios:** ficam no volume `/app/reports`.
- **Atualizar:** `docker pull` da versão nova e recriar o container.

## 3. Primeiro contato com a API real

O Sólides DP **não tem ambiente de homologação**: qualquer chamada vai para a conta de produção da ADN. Siga esta ordem.

Os comandos abaixo valem para as três formas. No Windows, use `IntegracaoSolidesDP.exe` dentro da pasta de instalação. No Linux, use `sudo -u integracao-solidesdp /opt/integracao-solidesdp/IntegracaoSolidesDP`. No Docker, use `docker run --rm ... <imagem>` seguido do comando.

1. **Validar:** `--check-config` precisa mostrar o banco e o token com `[OK]`.
2. **Descobrir os ids:** `--discover` lista as empresas, escalas, regras de ponto e motivos de ajuste do DP.
   - Preencha `WorkScheduleExternalId` e `PunchRuleExternalId`.
   - Confira se os CNPJs das filiais existem no DP.
   - Confira se existe o motivo **FÉRIAS**.
3. **Simular:** com `DryRun: true`, rode `--dry-run` ou deixe o serviço rodar. O relatório sai em `reports/run-*.csv`, que abre no Excel. **O RH da ADN revisa esse relatório.**
4. **Piloto com 2 ou 3 pessoas:** configure `"DryRun": false, "ExternalIdAllowList": ["14-00901482", "..."]` (o formato é `{empresa}-{matrícula}`) e rode `--run-once`. Confira no app.tangerino.com.br:
   - [ ] o colaborador foi criado com cargo, local, empresa, escala e regra corretos;
   - [ ] uma segunda execução não altera nada (tudo `unchanged` no relatório);
   - [ ] ao alterar um dado no RHSenso, a atualização não apaga campos preenchidos no DP nem troca a escala;
   - [ ] o período das férias está certo (o último dia incluído, sem um dia a mais); se não estiver, ajuste `Sync:FeriasEndDateMode`;
   - [ ] o desligamento aparece com a data e o motivo corretos.

   Avise a RHSenso sobre qualquer divergência.
5. **Rollout:** primeiro libere por empresa com `Sync:EmpresasIncluidas`; depois remova os filtros e deixe o serviço rodando com `DryRun: false`.

## 4. Operação

| O quê | Onde |
|---|---|
| Logs | Pasta `logs` da instalação, `journalctl` no Linux ou `docker logs` |
| Relatório de cada execução | Pasta `reports`: `run-*.csv` com os itens `failed`, `blocked`, `skipped` e o motivo de cada um |
| Histórico no banco | `SELECT * FROM solidesdp.runs ORDER BY started_at DESC` |
| Conferência semanal | `--reconcile`: lista os enviados que não existem mais no DP (`--repair` reenvia) |
| Versão instalada | `--version` |

## Problemas conhecidos nos dados do RHSenso (ADN)

- **12 colaboradores ativos têm o CPF gravado com máscara** em `func1.nocpf`, que é `varchar(11)`. Com isso, os dígitos verificadores foram perdidos. Eles são enviados sem CPF e aparecem no relatório como `cpf_truncado_no_rhsenso`. **Corrigir no RHSenso.**
- **Dois pares de matrículas ativas na empresa 7 compartilham o mesmo CPF.** Ficam fora do envio (`skipped_duplicate_cpf`) até alguém decidir o que fazer.
- **Alguns PIS não passam no dígito verificador.** São enviados sem PIS, com o aviso `invalid_pis_check_digit`.
