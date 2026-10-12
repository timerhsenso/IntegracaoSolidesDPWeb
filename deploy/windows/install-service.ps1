<#
.SYNOPSIS
    Instala ou atualiza o IntegracaoSolidesDP como serviço do Windows.

.DESCRIPTION
    Rodar como Administrador, de dentro da pasta extraída do zip do release.
    - Pasta de instalação: -InstallDir, ou, sem ele, pergunta na primeira instalação (Enter = C:\Services\IntegracaoSolidesDP).
      Na atualização usa a pasta onde o serviço já está instalado.
    - Copia os arquivos para a pasta de instalação preservando o appsettings.json que já estiver lá.
    - Registra o serviço (início automático, reinício em caso de falha).
    - Roda --check-config e só inicia o serviço se a configuração estiver válida.

.EXAMPLE
    .\install-service.ps1
    Primeira instalação: pergunta a pasta. Atualização: usa a pasta onde o serviço já está.

.EXAMPLE
    .\install-service.ps1 -InstallDir "D:\Servicos\IntegracaoSolidesDP"
    Primeira instalação numa pasta escolhida, sem perguntar.
#>
param(
    [string]$ServiceName = "IntegracaoSolidesDP",
    # Vazio: na atualização, a pasta do serviço já instalado; na primeira instalação, pergunta.
    [string]$InstallDir = "",
    # Só para testes automatizados: inicia o serviço mesmo com a configuração incompleta.
    [switch]$SkipConfigCheck
)
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Execute este script como Administrador."
}

$pastaPadrao = "C:\Services\IntegracaoSolidesDP"
$source = (Resolve-Path $PSScriptRoot).Path.TrimEnd('\')
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

# Atualização: a pasta é a do executável do serviço registrado.
$pastaAtual = $null
if ($existing) {
    $caminho = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").PathName.Trim()
    $caminho = if ($caminho.StartsWith('"')) { $caminho.Substring(1, $caminho.IndexOf('"', 1) - 1) } else { ($caminho -split ' ')[0] }
    $pastaAtual = (Split-Path -Parent $caminho).TrimEnd('\')
}

if (-not $InstallDir) {
    if ($pastaAtual) {
        $InstallDir = $pastaAtual
    }
    elseif ([Environment]::UserInteractive) {
        $resposta = Read-Host "Pasta de instalação do serviço (Enter = $pastaPadrao)"
        $InstallDir = if ($resposta.Trim()) { $resposta.Trim().Trim('"') } else { $pastaPadrao }
    }
    else {
        $InstallDir = $pastaPadrao
    }
}

if ($InstallDir -notmatch '^[A-Za-z]:\\') {
    throw "Informe o caminho completo da pasta de instalação, com a unidade (ex.: D:\Sistemas\IntegracaoSolidesDP)."
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')

if ($pastaAtual -and -not [string]::Equals($InstallDir, $pastaAtual, [StringComparison]::OrdinalIgnoreCase)) {
    throw @"
O serviço $ServiceName já está instalado em $pastaAtual. Para atualizar, rode sem -InstallDir.
Para mudar de pasta: remova o serviço (.\uninstall-service.ps1), copie o appsettings.json (e, se quiser, logs e reports)
de $pastaAtual para $InstallDir e rode este script de novo com -InstallDir "$InstallDir".
"@
}
if (-not $pastaAtual -and [string]::Equals($InstallDir, $source, [StringComparison]::OrdinalIgnoreCase)) {
    throw @"
Não instale na pasta onde o pacote foi extraído ($source): na próxima atualização o zip novo sobrescreveria a configuração.
Extraia o pacote numa pasta temporária (ex.: C:\Temp\IntegracaoSolidesDP-x.y.z) e informe a pasta definitiva.
"@
}

Write-Host "Pasta de instalação: $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$target = (Resolve-Path $InstallDir).Path.TrimEnd('\')

if ($existing -and $existing.Status -ne "Stopped") {
    Write-Host "Parando o serviço $ServiceName..."
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
}

if ($source -ne $target) {
    # A configuração do cliente nunca é sobrescrita numa atualização.
    Get-ChildItem -Path $source -Exclude "appsettings.json" | Copy-Item -Destination $target -Recurse -Force
    if (-not (Test-Path (Join-Path $target "appsettings.json"))) {
        Copy-Item (Join-Path $source "appsettings.json") $target
        Write-Host "appsettings.json criado em $target. Edite-o antes de iniciar o serviço (veja INSTALL.md)."
    }
}

$exe = Join-Path $target "IntegracaoSolidesDP.exe"
if (-not $existing) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" `
        -DisplayName "Integração RHSenso -> Sólides DP" `
        -Description "Sincroniza cargos, locais, colaboradores e férias do RHSenso com o Sólides DP." `
        -StartupType Automatic | Out-Null
    # Reinicia após 1 minuto em caso de queda; zera a contagem a cada 24h.
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    Write-Host "Serviço $ServiceName registrado."
}

Write-Host "Versão instalada: $(& $exe --version)"

if (-not $SkipConfigCheck) {
    Write-Host "Validando a configuração..."
    & $exe --check-config
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Configuração com problemas: o serviço NÃO foi iniciado. Corrija $target\appsettings.json e rode este script de novo."
        exit $LASTEXITCODE
    }
}

Start-Service -Name $ServiceName
Write-Host "Serviço $ServiceName iniciado. Logs: $target\logs  Relatórios: $target\reports"
