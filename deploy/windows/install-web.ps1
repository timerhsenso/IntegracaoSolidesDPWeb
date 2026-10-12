<#
.SYNOPSIS
    Instala ou atualiza a Web de gestão (IntegracaoSolidesDP.Web) no IIS.

.DESCRIPTION
    Rodar como Administrador, de dentro da pasta extraída do zip da Web
    (IntegracaoSolidesDP.Web-<versão>-win-x64.zip). Funciona no Windows PowerShell 5.1 e no PowerShell 7:
    o IIS é configurado com appcmd.exe e netsh, sem depender do módulo WebAdministration.

    1. Confere o IIS e o ASP.NET Core Module (Hosting Bundle do .NET 10).
    2. Cria o app pool (sem código gerenciado, identidade própria, sempre ligado) e o site com binding HTTPS.
    3. Para o site, copia os arquivos preservando o appsettings.json e os logs, e ajusta as permissões de pasta
       (o appsettings.json fica legível só para Administradores, SYSTEM e o app pool).
    4. Roda IntegracaoSolidesDP.Web.exe --check-config e só inicia o site se a configuração estiver válida.

    Na primeira vez o script cria o appsettings.json na pasta do site e para: edite-o e rode o script de novo.

.EXAMPLE
    .\install-web.ps1 -HostName gestao-solides.empresa.local -CertThumbprint 1A2B3C...
    Site em https://gestao-solides.empresa.local com o certificado da empresa (Computador Local > Pessoal).

.EXAMPLE
    .\install-web.ps1 -Port 8443 -CertificadoAutoassinado
    Homologação: https://<servidor>:8443 com um certificado gerado pelo próprio servidor.

.EXAMPLE
    .\install-web.ps1
    Atualização: troca os arquivos e mantém site, binding, certificado e appsettings.json.
#>
param(
    [string]$SiteName = "IntegracaoSolidesDP.Web",
    [string]$AppPoolName = "IntegracaoSolidesDP.Web",
    # Pasta do site. Vazio: na atualização, a pasta do site já instalado; na primeira instalação, pergunta
    # (Enter = C:\inetpub\IntegracaoSolidesDP.Web).
    [string]$InstallDir = "",
    # Nome pelo qual os usuários acessam (ex.: gestao-solides.empresa.local). Vazio = qualquer nome, e a porta
    # fica só para este site.
    [string]$HostName = "",
    [ValidateRange(1, 65535)]
    [int]$Port = 443,
    # Impressão digital de um certificado já instalado em Computador Local > Pessoal (com chave privada).
    [string]$CertThumbprint = "",
    # Gera (ou reaproveita) um certificado do próprio servidor. Só para homologação: os navegadores das outras
    # máquinas vão mostrar alerta de segurança.
    [switch]$CertificadoAutoassinado,
    # Não cria a regra de entrada no Firewall do Windows.
    [switch]$SemFirewall,
    # Só para testes: inicia o site mesmo com a configuração incompleta.
    [switch]$SkipConfigCheck
)
$ErrorActionPreference = "Stop"

# ------------------------------------------------------------------ pré-requisitos

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Execute este script como Administrador."
}

$appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
if (-not (Test-Path $appcmd)) {
    throw @"
O IIS não está instalado. No Windows Server, rode como Administrador:
    Install-WindowsFeature Web-Server, Web-Mgmt-Console, Web-AppInit
e depois instale o ASP.NET Core Hosting Bundle do .NET 10 (https://dotnet.microsoft.com/download/dotnet/10.0).
"@
}

$ancm = Join-Path $env:ProgramFiles "IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
if (-not (Test-Path $ancm)) {
    throw @"
O ASP.NET Core Module do IIS não está instalado.
Instale o "ASP.NET Core Hosting Bundle" do .NET 10 (https://dotnet.microsoft.com/download/dotnet/10.0),
rode 'iisreset' e execute este script de novo.
"@
}

$source = (Resolve-Path $PSScriptRoot).Path
$exeName = "IntegracaoSolidesDP.Web.exe"
if (-not (Test-Path (Join-Path $source $exeName))) {
    throw "Não encontrei $exeName em $source. Rode o script de dentro da pasta extraída do zip da Web."
}

# Roda appcmd/netsh e devolve código de saída e texto. ErrorActionPreference local em Continue: no Windows
# PowerShell 5.1, com Stop, qualquer linha no stderr de um executável vira erro fatal antes de lermos o código.
function Invoke-Exe {
    param([string]$Exe, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $ErrorActionPreference = "Continue"
    $output = @(& $Exe @Arguments 2>&1 | ForEach-Object { "$_" })
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = $output; Text = ($output -join "`n") }
}

# appcmd devolve código de saída diferente de zero quando o objeto não existe ou o comando falha.
function Invoke-AppCmd {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $r = Invoke-Exe $appcmd @Arguments
    if ($r.Code -ne 0) {
        throw "appcmd $($Arguments -join ' ') falhou: $($r.Text)"
    }
    return $r.Output
}

function Test-AppCmdObject([string]$Type, [string]$Name) {
    return (Invoke-Exe $appcmd list $Type $Name).Code -eq 0
}

function Get-AppPoolState {
    $r = Invoke-Exe $appcmd list apppool $AppPoolName /text:state
    if ($r.Code -ne 0) { return "" }
    return ($r.Output | Select-Object -First 1)
}

function Get-SiteState {
    $r = Invoke-Exe $appcmd list site $SiteName /text:state
    if ($r.Code -ne 0) { return "" }
    return ($r.Output | Select-Object -First 1)
}

function Get-SiteBindings([string]$Name) {
    $r = Invoke-Exe $appcmd list site $Name /text:bindings
    if ($r.Code -ne 0 -or -not $r.Text) { return @() }
    return @(($r.Output -join ",").Split(",", [StringSplitOptions]::RemoveEmptyEntries))
}

$siteExiste = Test-AppCmdObject site $SiteName

# Pasta do site: a que ele já usa (atualização), a informada, ou perguntada na primeira instalação.
$pastaPadrao = "C:\inetpub\IntegracaoSolidesDP.Web"
$pastaAtual = $null
if ($siteExiste) {
    $r = Invoke-Exe $appcmd list vdir "$SiteName/" /text:physicalPath
    if ($r.Code -eq 0 -and $r.Text) {
        $pastaAtual = [Environment]::ExpandEnvironmentVariables(($r.Output | Select-Object -First 1).Trim()).TrimEnd('\')
    }
}
if (-not $InstallDir) {
    if ($pastaAtual) {
        $InstallDir = $pastaAtual
    }
    elseif ([Environment]::UserInteractive) {
        $resposta = Read-Host "Pasta de instalação da Web (Enter = $pastaPadrao)"
        $InstallDir = if ($resposta.Trim()) { $resposta.Trim().Trim('"') } else { $pastaPadrao }
    }
    else {
        $InstallDir = $pastaPadrao
    }
}
if ($InstallDir -notmatch '^[A-Za-z]:\\') {
    throw "Informe o caminho completo da pasta da Web, com a unidade (ex.: D:\Sites\IntegracaoSolidesDP.Web)."
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
if ($pastaAtual -and -not [string]::Equals($InstallDir, $pastaAtual, [StringComparison]::OrdinalIgnoreCase)) {
    throw @"
O site $SiteName já usa a pasta $pastaAtual. Para atualizar, rode sem -InstallDir.
Para mudar de pasta: remova o site (.\uninstall-web.ps1), copie o appsettings.json de $pastaAtual para $InstallDir
e rode este script de novo com -InstallDir "$InstallDir" (e o -HostName/-Port/-CertThumbprint do site).
"@
}
if (-not $pastaAtual -and [string]::Equals($InstallDir, $source.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw @"
Não instale na pasta onde o pacote foi extraído ($source): na próxima atualização o zip novo sobrescreveria a configuração.
Extraia o pacote numa pasta temporária (ex.: C:\Temp\IntegracaoSolidesDP.Web-x.y.z) e informe a pasta definitiva.
"@
}
Write-Host "Pasta da Web: $InstallDir"

# Atualização sem -HostName/-Port: mantém o endereço que o site já tem.
if ($siteExiste -and -not ($PSBoundParameters.ContainsKey("HostName") -or $PSBoundParameters.ContainsKey("Port"))) {
    foreach ($b in (Get-SiteBindings $SiteName)) {
        if ($b -match '^https/\*:(?<porta>\d+):(?<nome>.*)$') {
            $Port = [int]$Matches.porta
            $HostName = $Matches.nome
            break
        }
    }
}

$binding = "https/*:${Port}:$HostName"
$bindingInformation = "*:${Port}:$HostName"
$sslEndpoint = if ($HostName) { "hostnameport=${HostName}:$Port" } else { "ipport=0.0.0.0:$Port" }

# Outro site já responde no mesmo endereço? (ex.: Default Web Site com https em *:443)
foreach ($line in (Invoke-Exe $appcmd list site).Output) {
    if ($line -match '^SITE "(?<nome>[^"]+)"' -and $Matches.nome -ne $SiteName) {
        $outro = $Matches.nome
        if ((Get-SiteBindings $outro) -contains $binding) {
            throw "O site '$outro' já usa o endereço $binding. Use outro -HostName ou -Port (ex.: -Port 8443)."
        }
    }
}

# ------------------------------------------------------------------ certificado

function Get-CertificadoAutoassinado {
    $friendly = "IntegracaoSolidesDP.Web (autoassinado)"
    $dns = @($(if ($HostName) { $HostName }), $env:COMPUTERNAME, "localhost") | Where-Object { $_ } | Select-Object -Unique
    $existente = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.FriendlyName -eq $friendly -and $_.NotAfter -gt (Get-Date).AddDays(30) -and $_.HasPrivateKey } |
        Where-Object { $nomes = @($_.DnsNameList | ForEach-Object { $_.Unicode }); -not ($dns | Where-Object { $nomes -notcontains $_ }) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($existente) {
        Write-Host "Reaproveitando o certificado autoassinado $($existente.Thumbprint)."
        return $existente.Thumbprint
    }

    $novo = New-SelfSignedCertificate -DnsName $dns -CertStoreLocation Cert:\LocalMachine\My `
        -FriendlyName $friendly -NotAfter (Get-Date).AddYears(2) -KeyExportPolicy NonExportable
    $thumb = $novo.Thumbprint
    # Confiável neste servidor (o primeiro acesso é feito pelo navegador do próprio servidor).
    $cert = Get-Item "Cert:\LocalMachine\My\$thumb"
    $root = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
    $root.Open("ReadWrite")
    try {
        $root.Add([System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData))
    }
    finally {
        $root.Close()
    }
    Write-Host "Certificado autoassinado criado: $thumb ($($dns -join ', '))."
    return $thumb
}

function Test-Certificado([string]$Thumb) {
    $cert = Get-Item "Cert:\LocalMachine\My\$Thumb" -ErrorAction SilentlyContinue
    if (-not $cert) {
        throw "Certificado $Thumb não encontrado em Computador Local > Pessoal (Cert:\LocalMachine\My)."
    }
    if (-not $cert.HasPrivateKey) {
        throw "O certificado $Thumb não tem a chave privada. Importe o .pfx (com a chave) em Computador Local > Pessoal."
    }
    if ($cert.NotAfter -lt (Get-Date)) {
        throw "O certificado $Thumb venceu em $($cert.NotAfter)."
    }
    if ($cert.NotAfter -lt (Get-Date).AddDays(30)) {
        Write-Warning "O certificado $Thumb vence em $($cert.NotAfter). Providencie a renovação."
    }
}

$thumbprint = $CertThumbprint.Replace(" ", "").ToUpperInvariant()
if ($CertificadoAutoassinado -and $thumbprint) {
    throw "Use -CertThumbprint ou -CertificadoAutoassinado, não os dois."
}
if ($CertificadoAutoassinado) {
    $thumbprint = Get-CertificadoAutoassinado
}
if ($thumbprint) {
    Test-Certificado $thumbprint
}

$temCertificado = (Invoke-Exe netsh http show sslcert $sslEndpoint).Code -eq 0
if (-not $thumbprint -and -not $temCertificado) {
    throw @"
O endereço https://$(if ($HostName) { $HostName } else { '<servidor>' }):$Port ainda não tem certificado.
Informe -CertThumbprint <impressão digital> (certificado em Computador Local > Pessoal)
ou -CertificadoAutoassinado (homologação).
"@
}

# ------------------------------------------------------------------ app pool

if (-not (Test-AppCmdObject apppool $AppPoolName)) {
    Invoke-AppCmd add apppool /name:$AppPoolName | Out-Null
    Write-Host "App pool $AppPoolName criado."
}
# Sem código gerenciado (o ASP.NET Core roda pelo módulo), identidade própria (IIS AppPool\<nome>),
# sempre ligado (a Web não "dorme") e reciclagem diária às 03:00 em vez de a cada 29 horas.
Invoke-AppCmd set apppool $AppPoolName `
    /managedRuntimeVersion: `
    /managedPipelineMode:Integrated `
    /enable32BitAppOnWin64:false `
    /startMode:AlwaysRunning `
    /processModel.identityType:ApplicationPoolIdentity `
    /processModel.loadUserProfile:true `
    /processModel.idleTimeout:00:00:00 `
    /recycling.periodicRestart.time:00:00:00 | Out-Null
if ((Invoke-Exe $appcmd list apppool $AppPoolName /config).Text -notmatch 'value="03:00:00"') {
    Invoke-AppCmd set apppool $AppPoolName "/+recycling.periodicRestart.schedule.[value='03:00:00']" | Out-Null
}

# ------------------------------------------------------------------ arquivos

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$target = (Resolve-Path $InstallDir).Path
$appOffline = Join-Path $target "app_offline.htm"

if ($siteExiste) {
    # app_offline.htm faz o módulo do ASP.NET Core encerrar a aplicação e mostrar "em manutenção".
    Set-Content -Path $appOffline -Encoding UTF8 -Value "<!doctype html><meta charset=utf-8><title>Em manutenção</title><p>Integração RHSenso → Sólides DP: atualização em andamento. Tente de novo em alguns minutos.</p>"
    Start-Sleep -Seconds 2
}
if ((Get-AppPoolState) -eq "Started") {
    Write-Host "Parando o app pool $AppPoolName..."
    Invoke-AppCmd stop apppool /apppool.name:$AppPoolName | Out-Null
    for ($i = 0; $i -lt 30 -and (Get-AppPoolState) -ne "Stopped"; $i++) { Start-Sleep -Seconds 1 }
}

if ($source -ne $target) {
    # Espelha a pasta do pacote, sem tocar na configuração do cliente, nos logs e no app_offline.htm.
    # Os scripts de instalação e a documentação ficam só no pacote.
    & robocopy $source $target /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP `
        /XF appsettings.json app_offline.htm *.ps1 *.sql *.md `
        /XD logs | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Falha ao copiar os arquivos para $target (robocopy código $LASTEXITCODE). Feche programas que estejam usando a pasta e rode de novo."
    }
    $configNova = -not (Test-Path (Join-Path $target "appsettings.json"))
    if ($configNova) {
        Copy-Item (Join-Path $source "appsettings.json") $target
    }
}
else {
    $configNova = $false
}

New-Item -ItemType Directory -Force -Path (Join-Path $target "logs") | Out-Null

# ------------------------------------------------------------------ permissões de pasta

$identidade = "IIS AppPool\$AppPoolName"
# Pasta do site: leitura. logs: gravação.
& icacls $target /grant "${identidade}:(OI)(CI)RX" /Q | Out-Null
& icacls (Join-Path $target "logs") /grant "${identidade}:(OI)(CI)M" /Q | Out-Null
# appsettings.json (connection string e chave dos tokens): só Administradores (S-1-5-32-544),
# SYSTEM (S-1-5-18) e o app pool. SIDs no lugar dos nomes: funciona no Windows em qualquer idioma.
& icacls (Join-Path $target "appsettings.json") /inheritance:r /grant:r "*S-1-5-32-544:F" "*S-1-5-18:F" "${identidade}:R" /Q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Não foi possível ajustar as permissões do appsettings.json (icacls código $LASTEXITCODE)."
}

# ------------------------------------------------------------------ site e binding

if (-not $siteExiste) {
    Invoke-AppCmd add site /name:$SiteName /physicalPath:$target /bindings:$binding | Out-Null
    Write-Host "Site $SiteName criado."
}
elseif ((Get-SiteBindings $SiteName) -notcontains $binding) {
    Invoke-AppCmd set site /site.name:$SiteName "/+bindings.[protocol='https',bindingInformation='$bindingInformation']" | Out-Null
    Write-Host "Binding $binding adicionado ao site $SiteName."
}
if ($HostName) {
    # SNI: o IIS escolhe o certificado pelo nome, e outros sites podem usar a mesma porta.
    Invoke-AppCmd set site /site.name:$SiteName "/bindings.[protocol='https',bindingInformation='$bindingInformation'].sslFlags:1" | Out-Null
}
Invoke-AppCmd set vdir "$SiteName/" /physicalPath:$target | Out-Null
Invoke-AppCmd set app "$SiteName/" /applicationPool:$AppPoolName /preloadEnabled:true | Out-Null

if ($thumbprint) {
    if ($temCertificado) {
        Invoke-Exe netsh http delete sslcert $sslEndpoint | Out-Null
    }
    # appid: o identificador do próprio IIS para os bindings HTTPS.
    $r = Invoke-Exe netsh http add sslcert $sslEndpoint certhash=$thumbprint "appid={4dc3e181-e14b-4a21-b022-59fc669b0914}" certstorename=MY
    if ($r.Code -ne 0) {
        throw "Não foi possível associar o certificado ao endereço ($sslEndpoint): $($r.Text)"
    }
    Write-Host "Certificado $thumbprint associado a $sslEndpoint."
}

if (-not $SemFirewall) {
    $regra = "IntegracaoSolidesDP.Web (HTTPS $Port)"
    if (-not (Get-NetFirewallRule -DisplayName $regra -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $regra -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain, Private | Out-Null
        Write-Host "Regra de firewall criada: $regra (perfis Domínio e Privado)."
    }
}

# ------------------------------------------------------------------ validação e partida

$exe = Join-Path $target $exeName
Write-Host "Versão instalada: $(& $exe --version)"

if ($configNova) {
    Write-Warning @"
appsettings.json criado em $target. O site NÃO foi iniciado.
Preencha ConnectionStrings:Rhu (e Web:ChaveTokens se o serviço estiver em outro servidor) e rode este script de novo.
Veja INSTALL.md, seção D.
"@
    exit 1
}

if (-not $SkipConfigCheck) {
    Write-Host "Validando a configuração..."
    Push-Location $target
    try {
        & $exe --check-config
        $check = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($check -ne 0) {
        Write-Warning "Configuração com problemas: o site NÃO foi iniciado (app_offline.htm continua no ar). Corrija $target\appsettings.json ou as permissões no banco e rode este script de novo."
        exit $check
    }
}

Invoke-AppCmd start apppool /apppool.name:$AppPoolName | Out-Null
if ((Get-SiteState) -ne "Started") {
    Invoke-AppCmd start site /site.name:$SiteName | Out-Null
}
Remove-Item $appOffline -ErrorAction SilentlyContinue

$url = "https://$(if ($HostName) { $HostName } else { $env:COMPUTERNAME }):$Port"
if ($Port -eq 443) { $url = $url -replace ":443$", "" }

# Primeira requisição: aplica as migrations do login e já mostra se a Web subiu (PowerShell 7).
if ($PSVersionTable.PSVersion.Major -ge 7) {
    try {
        $resposta = Invoke-WebRequest "$url/Conta/Entrar" -SkipCertificateCheck -TimeoutSec 120 -SkipHttpErrorCheck
        if ($resposta.StatusCode -ge 500) {
            Write-Warning "A Web respondeu $($resposta.StatusCode). Veja $target\logs e o Visualizador de Eventos (Aplicativo, origem 'IIS AspNetCore Module V2')."
        }
        else {
            Write-Host "A Web respondeu $($resposta.StatusCode) em $url."
        }
    }
    catch {
        Write-Warning "Não consegui abrir $url a partir deste servidor ($($_.Exception.Message)). Confira o DNS do nome e abra o endereço no navegador."
    }
}

Write-Host ""
Write-Host "Web instalada em $url  (pasta: $target, logs: $target\logs)"
Write-Host "Instalação nova? Abra $url/PrimeiroAcesso NO PRÓPRIO SERVIDOR e crie o primeiro administrador."
