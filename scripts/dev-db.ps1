<#
.SYNOPSIS
  Banco de desenvolvimento no Windows: SQL Server 2022 no Docker (porta 14333) com as tabelas
  do RHSenso usadas pela integração e dados de exemplo. Grava a connection string nos
  user-secrets do serviço e da Web.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\dev-db.ps1
  powershell -ExecutionPolicy Bypass -File scripts\dev-db.ps1 -Recriar   # apaga e cria de novo
#>
param([switch]$Recriar)

$ErrorActionPreference = 'Continue'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
$nome = 'sqlserver-integracao-dev'
$arquivoSenha = Join-Path $raiz '.env.local'

function Falhar([string]$mensagem) {
    Write-Host "ERRO: $mensagem" -ForegroundColor Red
    exit 1
}

docker info *> $null
if ($LASTEXITCODE -ne 0) { Falhar 'o Docker não está rodando. Abra o Docker Desktop e espere "Engine running".' }

# Senha do sa: gerada uma vez e guardada em .env.local (fora do git).
if (-not (Test-Path $arquivoSenha)) {
    $gerada = 'Dev!' + [guid]::NewGuid().ToString('N').Substring(0, 16) + 'Aa1'
    Set-Content -Path $arquivoSenha -Value "MSSQL_SA_PASSWORD=$gerada" -Encoding ascii
}
$senha = ((Get-Content $arquivoSenha | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' }) -split '=', 2)[1]

if ($Recriar) {
    docker rm -f $nome *> $null
}

$existe = docker ps -a --filter "name=^$nome$" --format '{{.Names}}'
if (-not $existe) {
    Write-Host 'Criando o container do SQL Server (a primeira vez baixa ~1,5 GB)...'
    docker run -d --name $nome -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e "MSSQL_SA_PASSWORD=$senha" `
        -p 127.0.0.1:14333:1433 --restart unless-stopped mcr.microsoft.com/mssql/server:2022-latest | Out-Null
    if ($LASTEXITCODE -ne 0) { Falhar 'não foi possível criar o container.' }
}
docker start $nome | Out-Null

function Sql([string]$banco, [string]$comando) {
    docker exec -e "SQLCMDPASSWORD=$senha" $nome /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1 -U sa -C -b -d $banco -Q $comando
}

Write-Host 'Aguardando o SQL Server...'
$tentativas = 0
do {
    Start-Sleep -Seconds 3
    $tentativas++
    Sql 'master' 'SELECT 1' *> $null
} until ($LASTEXITCODE -eq 0 -or $tentativas -ge 40)
if ($LASTEXITCODE -ne 0) { Falhar 'o SQL Server não respondeu.' }

Sql 'master' "IF DB_ID('bd_rhu_adn') IS NULL CREATE DATABASE bd_rhu_adn" | Out-Null

$situacao = Sql 'bd_rhu_adn' "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID('dbo.func1') IS NULL THEN 'TABELAS_AUSENTES' ELSE 'TABELAS_OK' END" | Out-String
if ($situacao -notmatch 'TABELAS_OK') {
    Write-Host 'Criando as tabelas do RHSenso e os dados de exemplo...'
    foreach ($arquivo in 'rhu-schema.sql', 'rhu-seed.sql') {
        docker cp (Join-Path $raiz "tests\smoke\$arquivo") "${nome}:/tmp/$arquivo" | Out-Null
        docker exec -e "SQLCMDPASSWORD=$senha" $nome /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1 -U sa -C -b -d bd_rhu_adn -i "/tmp/$arquivo" | Out-Null
        if ($LASTEXITCODE -ne 0) { Falhar "falha ao executar $arquivo." }
    }
}

$conexao = "Server=localhost,14333;Database=bd_rhu_adn;User Id=sa;Password=$senha;Encrypt=True;TrustServerCertificate=True"
foreach ($projeto in 'src\IntegracaoSolidesDP.Worker', 'src\IntegracaoSolidesDP.Web') {
    dotnet user-secrets set 'ConnectionStrings:Rhu' $conexao --project $projeto | Out-Null
    if ($LASTEXITCODE -ne 0) { Falhar "não foi possível gravar os user-secrets de $projeto." }
}

Write-Host ''
Write-Host 'Pronto: bd_rhu_adn em localhost,14333 (connection string nos user-secrets do serviço e da Web).' -ForegroundColor Green
