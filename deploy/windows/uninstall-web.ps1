<#
.SYNOPSIS
    Remove a Web de gestão (IntegracaoSolidesDP.Web) do IIS: site, app pool, associação do certificado e
    regra de firewall criados pelo install-web.ps1.

.DESCRIPTION
    Os arquivos, o appsettings.json e os logs ficam na pasta (use -RemoveFiles para apagá-los).
    O certificado continua no repositório do Windows e o banco (schemas solidesdp e solidesdp_auth,
    usuários da Web e auditoria) não é tocado.
#>
param(
    [string]$SiteName = "IntegracaoSolidesDP.Web",
    [string]$AppPoolName = "IntegracaoSolidesDP.Web",
    [string]$InstallDir = "C:\inetpub\IntegracaoSolidesDP.Web",
    [switch]$RemoveFiles
)
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Execute este script como Administrador."
}

$appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
if (-not (Test-Path $appcmd)) {
    Write-Host "IIS não instalado: nada a remover no IIS."
}
else {
    $bindings = & $appcmd list site $SiteName /text:bindings 2>$null
    if ($LASTEXITCODE -eq 0) {
        foreach ($b in ($bindings -join ",").Split(",", [StringSplitOptions]::RemoveEmptyEntries)) {
            # https/*:443:nome  ou  https/*:8443:
            if ($b -match '^https/\*:(?<porta>\d+):(?<nome>.*)$') {
                $endpoint = if ($Matches.nome) { "hostnameport=$($Matches.nome):$($Matches.porta)" } else { "ipport=0.0.0.0:$($Matches.porta)" }
                & netsh http delete sslcert $endpoint *> $null
                if ($LASTEXITCODE -eq 0) { Write-Host "Associação do certificado removida: $endpoint." }
                Get-NetFirewallRule -DisplayName "IntegracaoSolidesDP.Web (HTTPS $($Matches.porta))" -ErrorAction SilentlyContinue |
                    Remove-NetFirewallRule
            }
        }
        & $appcmd delete site $SiteName | Out-Null
        Write-Host "Site $SiteName removido."
    }
    else {
        Write-Host "Site $SiteName não está instalado."
    }

    & $appcmd list apppool $AppPoolName *> $null
    if ($LASTEXITCODE -eq 0) {
        & $appcmd delete apppool $AppPoolName | Out-Null
        Write-Host "App pool $AppPoolName removido."
    }
}

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "Pasta $InstallDir removida."
}
