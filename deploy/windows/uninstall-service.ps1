<#
.SYNOPSIS
    Remove o serviço IntegracaoSolidesDP. Os arquivos, logs e relatórios ficam na pasta
    (use -RemoveFiles para apagá-los). O estado no banco (schema solidesdp) não é tocado.
#>
param(
    [string]$ServiceName = "IntegracaoSolidesDP",
    # Só com -RemoveFiles. Vazio: a pasta onde o serviço está instalado.
    [string]$InstallDir = "",
    [switch]$RemoveFiles
)
$ErrorActionPreference = "Stop"

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and -not $InstallDir) {
    $caminho = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").PathName.Trim()
    $caminho = if ($caminho.StartsWith('"')) { $caminho.Substring(1, $caminho.IndexOf('"', 1) - 1) } else { ($caminho -split ' ')[0] }
    $InstallDir = Split-Path -Parent $caminho
}

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

if ($RemoveFiles -and -not $InstallDir) {
    Write-Warning "Serviço não encontrado: informe a pasta com -InstallDir para apagar os arquivos."
}
elseif ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "Pasta $InstallDir removida."
}
