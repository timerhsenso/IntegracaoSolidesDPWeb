# Instalação — IntegracaoSolidesDP

O IntegracaoSolidesDP lê o banco do RHSenso (`bd_rhu_adn`) e envia cargos, locais de trabalho, colaboradores, desligamentos e férias para o **Sólides DP**. Ele roda como serviço, sem interface: a cada intervalo configurado, faz uma sincronização e grava um relatório.

Há três formas de instalar o serviço. Escolha **uma**. A aplicação Web de gestão (opcional, no IIS) tem a sua própria seção: [D. Aplicação Web (IIS)](#d-aplicação-web-iis).

| Forma | Quando usar | Pacote do release |
|---|---|---|
| [Serviço Windows](#a-serviço-windows) | Servidor Windows (o caso mais comum, junto do SQL Server) | `IntegracaoSolidesDP-<versão>-win-x64.zip` |
| [Serviço Linux (systemd)](#b-serviço-linux-systemd) | Servidor Linux x64 (Ubuntu, Debian, RHEL…) | `IntegracaoSolidesDP-<versão>-linux-x64.tar.gz` |
| [Docker](#c-docker) | Quem já opera containers | imagem `ghcr.io/rhsenso/integracaosolidesdp:<versão>` |

Os pacotes Windows e Linux são *self-contained*: **não precisa instalar o .NET**. Os pacotes Windows (serviço e Web) são gerados com `scripts\publicar-windows.ps1`.

## 1. Pré-requisitos

- **Rede:** acesso ao SQL Server do RHSenso e saída HTTPS para `employer.tangerino.com.br:443`.
- **Token do Sólides DP:** gerado em Empregador → Integrações. Se o menu não aparecer, peça ao suporte da Sólides.
- **Logins SQL de privilégio mínimo.** O script `permissoes-sql.sql` (vem nos pacotes; no repositório, em `deploy/windows`) cria os dois logins. Abra no SSMS como sysadmin, troque as duas senhas no início e execute:

  | Login | Quem usa | Pode |
  |---|---|---|
  | `integracao_solidesdp` | serviço | ler o RHSenso (`dbo`); é dono do schema `solidesdp` e cria as próprias tabelas |
  | `integracao_solidesdp_web` | Web (IIS) | ler o RHSenso e o `solidesdp`; gravar (sem apagar) em `solidesdp`; é dono do schema `solidesdp_auth` (login da Web) |

  Nenhum dos dois grava no `dbo`. As tabelas `solidesdp.*` são criadas pelo próprio serviço na primeira execução. Sem a Web, ignore o segundo login.

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

## D. Aplicação Web (IIS)

A Web de gestão acompanha e opera o serviço (Painel, Execuções, Empresas, Regras gerais, Usuários). Ela **não** fala com o Sólides DP: lê as tabelas do serviço e grava pedidos que o serviço atende. Para usá-la, o serviço precisa rodar com `"Gestao": { "Habilitada": true }`.

### D.1 Pré-requisitos (uma vez por servidor)

1. **IIS.** No Windows Server, PowerShell como Administrador:

   ```powershell
   Install-WindowsFeature Web-Server, Web-Mgmt-Console, Web-AppInit
   ```

2. **ASP.NET Core Hosting Bundle do .NET 10** ([download](https://dotnet.microsoft.com/download/dotnet/10.0), item "Hosting Bundle"). Depois de instalar, rode `iisreset`.
3. **Certificado HTTPS** (a Web só funciona em HTTPS). Em produção, o certificado da empresa para o nome escolhido (ex.: `gestao-solides.empresa.local`), importado em *Computador Local › Pessoal* **com a chave privada**. Anote a impressão digital:

   ```powershell
   Get-ChildItem Cert:\LocalMachine\My | Format-Table Thumbprint, Subject, NotAfter
   ```

   Em homologação dá para usar `-CertificadoAutoassinado` (os navegadores das outras máquinas mostram alerta).
4. **Login SQL da Web** criado pelo `permissoes-sql.sql` (seção 1).
5. **Chave dos tokens.** Se a Web e o serviço ficarem **no mesmo servidor**, pode deixar vazia (proteção da máquina). Se ficarem em servidores diferentes, gere uma chave e coloque **a mesma** em `Web:ChaveTokens` (Web) e `Gestao:ChaveTokens` (serviço):

   ```powershell
   [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
   ```

### D.2 Instalar

1. Extraia `IntegracaoSolidesDP.Web-<versão>-win-x64.zip` (ex.: em `C:\Temp\IntegracaoSolidesDP.Web`).
2. PowerShell **como Administrador**, nessa pasta:

   ```powershell
   # produção: nome próprio + certificado da empresa
   .\install-web.ps1 -HostName gestao-solides.empresa.local -CertThumbprint <impressão digital>

   # homologação: https://<servidor>:8443 com certificado gerado pelo servidor
   .\install-web.ps1 -Port 8443 -CertificadoAutoassinado
   ```

   Na primeira vez, o script cria o app pool e o site, copia os arquivos para `C:\inetpub\IntegracaoSolidesDP.Web`, cria o `appsettings.json` lá e **para**, porque a configuração ainda está vazia.
3. Edite `C:\inetpub\IntegracaoSolidesDP.Web\appsettings.json`:

   ```jsonc
   {
     "ConnectionStrings": {
       "Rhu": "Server=<servidor-sql>;Database=bd_rhu_adn;User Id=integracao_solidesdp_web;Password=<senha>;Encrypt=True;TrustServerCertificate=True"
     },
     "Web": {
       "InstanceName": "default",   // igual a Sync:InstanceName do serviço
       "TimeZone": "America/Bahia",
       "ChaveTokens": ""            // a mesma do Gestao:ChaveTokens do serviço (vazia = mesmo servidor)
     }
   }
   ```

4. Rode o mesmo comando do passo 2 de novo. O script:
   - roda `IntegracaoSolidesDP.Web.exe --check-config` (banco, permissões, tabelas do login, chave dos tokens, fuso) e **só inicia o site se não houver `[ERRO]`**;
   - inicia o site e faz a primeira requisição, que cria as tabelas do login (`solidesdp_auth`).
5. **Primeiro administrador:** num navegador **no próprio servidor**, abra `https://<nome>/PrimeiroAcesso` e crie o usuário Admin. A tela recusa acessos de outras máquinas e some assim que existe um usuário. Os demais usuários são criados pelo Admin em **Usuários**.

O que o `install-web.ps1` configura:

| Item | Valor |
|---|---|
| App pool | `IntegracaoSolidesDP.Web`, sem código gerenciado, identidade `IIS AppPool\IntegracaoSolidesDP.Web`, sempre ligado, reciclagem diária às 03:00 |
| Site | `IntegracaoSolidesDP.Web`, só HTTPS (com `-HostName`, usa SNI e pode dividir a porta 443 com outros sites) |
| Pasta | `C:\inetpub\IntegracaoSolidesDP.Web` (`-InstallDir` para mudar); o app pool lê a pasta e grava em `logs` |
| `appsettings.json` | legível só por Administradores, SYSTEM e o app pool |
| Firewall | regra de entrada na porta do site, perfis Domínio e Privado (`-SemFirewall` para não criar) |

Parâmetros: `-SiteName`, `-AppPoolName`, `-InstallDir`, `-HostName`, `-Port` (padrão 443), `-CertThumbprint`, `-CertificadoAutoassinado`, `-SemFirewall`.

### D.3 Atualizar, renovar o certificado, remover

```powershell
.\install-web.ps1                                        # atualizar: extraia o zip novo e rode sem parâmetros
.\install-web.ps1 -CertThumbprint <nova impressão digital>   # certificado renovado (mantém o endereço)
.\uninstall-web.ps1                                      # remove site e app pool (mantém a pasta; -RemoveFiles apaga)
```

Sem `-HostName`/`-Port`, o site mantém o endereço que já tem. Na atualização, o script põe a Web em manutenção (`app_offline.htm`), para o app pool, troca os arquivos, preserva o `appsettings.json` e os logs, valida e inicia de novo. O banco (usuários, auditoria, configuração) não é tocado.

### D.4 Problemas comuns

| Sintoma | Causa provável |
|---|---|
| `--check-config` com `[ERRO]` de permissão | `permissoes-sql.sql` não rodou, ou rodou num banco com outro nome. |
| `[ERRO] Empresa N: o token foi cifrado com outra chave` | `Web:ChaveTokens` diferente de `Gestao:ChaveTokens`, ou Web e serviço em servidores diferentes sem chave. |
| HTTP 500.30 / 500.31 | Hosting Bundle ausente ou instalado antes do IIS (reinstale e rode `iisreset`). Veja `logs` da pasta do site e o Visualizador de Eventos (Aplicativo, origem "IIS AspNetCore Module V2"). |
| HTTP 503 | App pool parado: a validação falhou. Rode `install-web.ps1` de novo e leia o resultado do `--check-config`. |
| "Em manutenção" | Uma atualização parou na validação. Corrija e rode `install-web.ps1` de novo. |
| PrimeiroAcesso diz "somente no servidor" | Abra o endereço num navegador **no próprio servidor**. |
| Painel diz que falta preparar a gestão | O serviço ainda não rodou com `Gestao:Habilitada=true` (ou está numa versão anterior). |

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
5. **Rollout:** remova o piloto (`ExternalIdAllowList`) e deixe o serviço rodando com `DryRun: false`. Com a gestão desligada, o serviço sincroniza uma só empresa (`Sync:EmpresasIncluidas`). Para várias empresas, ligue a gestão pela Web: cada empresa (token, simulação, go-live, filiais e piloto) é configurada na tela **Empresas**.

## 4. Operação

| O quê | Onde |
|---|---|
| Logs | Pasta `logs` da instalação, `journalctl` no Linux ou `docker logs`. Web: `C:\inetpub\IntegracaoSolidesDP.Web\logs` |
| Relatório de cada execução | Pasta `reports`: `run-*.csv` com os itens `failed`, `blocked`, `skipped` e o motivo de cada um |
| Histórico no banco | `SELECT * FROM solidesdp.runs ORDER BY started_at DESC` |
| Conferência semanal | `--reconcile`: lista os enviados que não existem mais no DP (`--repair` reenvia) |
| Versão instalada | `--version` |

## Problemas conhecidos nos dados do RHSenso (ADN)

- **12 colaboradores ativos têm o CPF gravado com máscara** em `func1.nocpf`, que é `varchar(11)`. Com isso, os dígitos verificadores foram perdidos. Eles são enviados sem CPF e aparecem no relatório como `cpf_truncado_no_rhsenso`. **Corrigir no RHSenso.**
- **Dois pares de matrículas ativas na empresa 7 compartilham o mesmo CPF.** Ficam fora do envio (`skipped_duplicate_cpf`) até alguém decidir o que fazer.
- **Alguns PIS não passam no dígito verificador.** São enviados sem PIS, com o aviso `invalid_pis_check_digit`.
