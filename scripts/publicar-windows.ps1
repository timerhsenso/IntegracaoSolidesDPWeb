<#
.SYNOPSIS
    Gera os pacotes Windows (self-contained, win-x64) do serviço e da Web, prontos para levar ao servidor.

.DESCRIPTION
    Saída em artifacts\ (fora do git):
      IntegracaoSolidesDP-<versão>-win-x64.zip      serviço  + install-service.ps1 / uninstall-service.ps1
      IntegracaoSolidesDP.Web-<versão>-win-x64.zip  Web/IIS  + install-web.ps1 / uninstall-web.ps1
      SHA256SUMS.txt                                para conferir a integridade no servidor
    Os dois pacotes levam o INSTALL.md e o deploy\windows\permissoes-sql.sql.
    Self-contained: o servidor não precisa do .NET instalado para o serviço; para a Web basta o
    ASP.NET Core Hosting Bundle (que instala o módulo do IIS).

.EXAMPLE
    .\scripts\publicar-windows.ps1                  # versão <VersionPrefix>.0 (Directory.Build.props)
    .\scripts\publicar-windows.ps1 -Versao 1.0.15
#>
[CmdletBinding()]
param(
    [string]$Versao,
    [string]$Saida
)
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent $PSScriptRoot
if (-not $Saida) { $Saida = Join-Path $raiz 'artifacts' }

if (-not $Versao) {
    [xml]$props = Get-Content (Join-Path $raiz 'Directory.Build.props')
    $prefixo = ($props.Project.PropertyGroup | Where-Object { $_.VersionPrefix } | Select-Object -First 1).VersionPrefix
    $Versao = "$prefixo.0"
}
if ($Versao -notmatch '^\d+\.\d+\.\d+$') {
    throw "Versão inválida: '$Versao' (use MAJOR.MINOR.PATCH, ex.: 1.0.15)."
}

$temp = Join-Path $Saida "temp"
if (Test-Path $temp) { Remove-Item -Recurse -Force $temp }
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$comuns = @(
    (Join-Path $raiz 'INSTALL.md'),
    (Join-Path $raiz 'deploy\windows\permissoes-sql.sql')
)

function Publicar([string]$Projeto, [string]$Pacote, [string[]]$Scripts) {
    $pasta = Join-Path $temp $Pacote
    Write-Host "==> Publicando $Projeto ($Versao)"
    & dotnet publish (Join-Path $raiz "src\$Projeto") -c Release -r win-x64 --self-contained true `
        -p:Version=$Versao -o $pasta --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish de $Projeto falhou." }

    # Configuração de desenvolvimento não vai para o servidor.
    Get-ChildItem $pasta -Filter 'appsettings.Development.json' | Remove-Item
    foreach ($arquivo in ($Scripts + $comuns)) { Copy-Item $arquivo $pasta }

    $zip = Join-Path $Saida "$Pacote-$Versao-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $pasta '*') -DestinationPath $zip
    Write-Host "    $zip"
    return $zip
}

$zips = @(
    (Publicar 'IntegracaoSolidesDP.Worker' 'IntegracaoSolidesDP' @(
        (Join-Path $raiz 'deploy\windows\install-service.ps1'),
        (Join-Path $raiz 'deploy\windows\uninstall-service.ps1'))),
    (Publicar 'IntegracaoSolidesDP.Web' 'IntegracaoSolidesDP.Web' @(
        (Join-Path $raiz 'deploy\windows\install-web.ps1'),
        (Join-Path $raiz 'deploy\windows\uninstall-web.ps1')))
)

$sums = $zips | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path -Leaf $_)" }
Set-Content -Path (Join-Path $Saida 'SHA256SUMS.txt') -Value $sums -Encoding ascii
Remove-Item -Recurse -Force $temp

Write-Host ""
Write-Host "Pacotes em $Saida"
Write-Host "No servidor, confira com: Get-FileHash <zip> -Algorithm SHA256"
