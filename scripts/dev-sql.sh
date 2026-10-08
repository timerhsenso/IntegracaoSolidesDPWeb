#!/usr/bin/env bash
# Sobe um SQL Server 2022 local (Docker, porta 14333) e restaura o backup do bd_rhu_adn.
# Uso: scripts/dev-sql.sh /caminho/para/bd_rhu_adn.bak
# A senha do sa é gerada em .env.local (fora do git) e gravada nos user-secrets do worker.
set -euo pipefail
cd "$(dirname "$0")/.."
BAK="${1:?informe o caminho do .bak}"
NAME=sqlserver-adn

if [ ! -f .env.local ]; then
  umask 077
  printf 'MSSQL_SA_PASSWORD=%s\n' "Adn!$(openssl rand -hex 12)Aa1" > .env.local
fi
set -a; . ./.env.local; set +a

if ! docker ps -a --format '{{.Names}}' | grep -qx "$NAME"; then
  docker volume create sqlserver-adn-data >/dev/null
  docker run -d --name "$NAME" --platform linux/amd64 \
    -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e MSSQL_SA_PASSWORD="$MSSQL_SA_PASSWORD" \
    -p 127.0.0.1:14333:1433 -v sqlserver-adn-data:/var/opt/mssql --restart unless-stopped \
    mcr.microsoft.com/mssql/server:2022-latest >/dev/null
fi
docker start "$NAME" >/dev/null

echo "Aguardando o SQL Server..."
until docker exec -e SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" "$NAME" /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1 -U sa -C -Q "SELECT 1" >/dev/null 2>&1; do sleep 3; done

docker exec "$NAME" mkdir -p /var/opt/mssql/backups
docker cp "$BAK" "$NAME":/var/opt/mssql/backups/bd_rhu_adn.bak
docker exec -u 0 "$NAME" chown mssql /var/opt/mssql/backups/bd_rhu_adn.bak
docker exec -e SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" "$NAME" /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1 -U sa -C -b -Q \
  "RESTORE DATABASE bd_rhu_adn FROM DISK='/var/opt/mssql/backups/bd_rhu_adn.bak' WITH MOVE 'bd_rhu_Data' TO '/var/opt/mssql/data/bd_rhu_adn.mdf', MOVE 'bd_rhu_Log' TO '/var/opt/mssql/data/bd_rhu_adn_log.ldf', REPLACE"
docker exec "$NAME" rm -f /var/opt/mssql/backups/bd_rhu_adn.bak

dotnet user-secrets set "ConnectionStrings:Rhu" \
  "Server=localhost,14333;Database=bd_rhu_adn;User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=True;TrustServerCertificate=True" \
  --project src/IntegracaoSolidesDP.Worker >/dev/null
echo "Pronto: bd_rhu_adn em localhost,14333 (connection string nos user-secrets do worker)."
