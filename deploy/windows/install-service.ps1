<#
.SYNOPSIS
    Instala ou atualiza o IntegracaoSolidesDP como serviço do Windows.

.DESCRIPTION
    Rodar como Administrador, de dentro da pasta extraída do zip do release.
    - Copia os arquivos para -InstallDir preservando o appsettings.json que já estiver lá.
    - Registra o serviço (início automático, reinício em caso de falha).
    - Roda --check-config e só inicia o serviço se a configuração estiver válida.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -InstallDir "D:\Servicos\IntegracaoSolidesDP"
#>
param(
    [string]$ServiceName = "IntegracaoSolidesDP",
    [string]$InstallDir = "C:\Services\IntegracaoSolidesDP",
    # Só para testes automatizados: inicia o serviço mesmo com a configuração incompleta.
    [switch]$SkipConfigCheck
)
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Execute este script como Administrador."
}

$source = (Resolve-Path $PSScriptRoot).Path
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$target = (Resolve-Path $InstallDir).Path

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
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
