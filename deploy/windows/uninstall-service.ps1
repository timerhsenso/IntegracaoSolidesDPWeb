<#
.SYNOPSIS
    Remove o serviço IntegracaoSolidesDP. Os arquivos, logs e relatórios ficam na pasta
    (use -RemoveFiles para apagá-los). O estado no banco (schema solidesdp) não é tocado.
#>
param(
    [string]$ServiceName = "IntegracaoSolidesDP",
    [string]$InstallDir = "C:\Services\IntegracaoSolidesDP",
    [switch]$RemoveFiles
)
$ErrorActionPreference = "Stop"

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
    }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Serviço $ServiceName removido."
}
else {
    Write-Host "Serviço $ServiceName não está instalado."
}

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "Pasta $InstallDir removida."
}
