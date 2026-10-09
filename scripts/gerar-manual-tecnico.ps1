<#
.SYNOPSIS
    Gera docs/manual-tecnico/manual-tecnico.pdf a partir do manual-tecnico.html.

.DESCRIPTION
    Usa o Microsoft Edge (ou o Google Chrome) em modo headless, que já vem no Windows: não precisa
    instalar nada. O PDF gerado vai embutido na dll da Web (IntegracaoSolidesDP.Web.csproj) e é
    oferecido ao perfil Admin em Ajuda > Para o TI. Faça commit do HTML e do PDF juntos.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\gerar-manual-tecnico.ps1
#>
[CmdletBinding()]
param(
    [string]$Navegador
)

$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent $PSScriptRoot
$html = Join-Path $raiz 'docs\manual-tecnico\manual-tecnico.html'
$pdf = Join-Path $raiz 'docs\manual-tecnico\manual-tecnico.pdf'

if (-not (Test-Path $html)) {
    throw "Não encontrei $html."
}

if (-not $Navegador) {
    $candidatos = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
    )
    $Navegador = $candidatos | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

if (-not $Navegador) {
    throw 'Microsoft Edge ou Google Chrome não encontrado. Informe o caminho com -Navegador.'
}

# Perfil temporário: não interfere no navegador aberto do usuário.
$perfil = Join-Path ([IO.Path]::GetTempPath()) ("manual-pdf-" + [guid]::NewGuid().ToString('N'))
$url = ([Uri]$html).AbsoluteUri

Write-Host "Gerando o PDF com $Navegador..."
$argumentos = @(
    '--headless',
    '--disable-gpu',
    '--no-pdf-header-footer',
    "--user-data-dir=`"$perfil`"",
    "--print-to-pdf=`"$pdf`"",
    $url
)
$processo = Start-Process -FilePath $Navegador -ArgumentList $argumentos -Wait -PassThru -WindowStyle Hidden
Remove-Item $perfil -Recurse -Force -ErrorAction SilentlyContinue

if ($processo.ExitCode -ne 0 -or -not (Test-Path $pdf)) {
    throw "O navegador terminou com código $($processo.ExitCode) e o PDF não foi gerado."
}

$tamanho = [math]::Round((Get-Item $pdf).Length / 1KB)
Write-Host "PDF gerado: $pdf ($tamanho KB)." -ForegroundColor Green
Write-Host 'Faça commit do HTML e do PDF e publique a Web de novo.'
