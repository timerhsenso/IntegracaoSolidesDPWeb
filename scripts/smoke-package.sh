#!/usr/bin/env bash
# Smoke test de um pacote PUBLICADO (não do código): sobe SQL Server + fake do Sólides DP em
# containers e roda o worker de verdade — check-config, dry-run, execução real contra o fake
# e uma segunda execução que não pode escrever nada.
#
#   scripts/smoke-package.sh binary <pasta-do-pacote>   # executável self-contained (Linux/macOS)
#   scripts/smoke-package.sh docker <imagem>            # imagem Docker do worker
#
# Precisa de docker, python3 e curl.
set -euo pipefail

MODE="${1:?modo: binary|docker}"
TARGET="${2:?pasta do pacote ou imagem}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
NET=solidesdp-smoke
SQL=solidesdp-smoke-sql
FAKE=solidesdp-smoke-fake
SQL_PORT="${SMOKE_SQL_PORT:-14399}"
FAKE_PORT="${SMOKE_FAKE_PORT:-5099}"
PASSWORD="Smoke!$(openssl rand -hex 8)Aa1"
WORK="$(mktemp -d)"

cleanup() {
  docker rm -f "$SQL" "$FAKE" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT
cleanup
mkdir -p "$WORK"

step() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nSMOKE FALHOU: %s\n' "$*" >&2; exit 1; }
fake() { curl -q -sS "http://127.0.0.1:$FAKE_PORT$1" "${@:2}"; }
sqlcmd() { docker exec -e SQLCMDPASSWORD="$PASSWORD" "$SQL" /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1 -U sa -C -b "$@"; }

step "Subindo SQL Server e o fake"
docker network create "$NET" >/dev/null
docker run -d --name "$SQL" --network "$NET" --platform linux/amd64 \
  -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$PASSWORD" -p "127.0.0.1:$SQL_PORT:1433" \
  mcr.microsoft.com/mssql/server:2022-latest >/dev/null
docker build -q -f "$ROOT/src/SolidesDP.Fake/Dockerfile" -t solidesdp-fake:smoke "$ROOT" >/dev/null
docker run -d --name "$FAKE" --network "$NET" -p "127.0.0.1:$FAKE_PORT:8080" solidesdp-fake:smoke >/dev/null

for _ in $(seq 1 90); do sqlcmd -Q "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done
sqlcmd -Q "SELECT 1" >/dev/null || fail "SQL Server não subiu"
for _ in $(seq 1 30); do fake /_fake/state -o /dev/null 2>/dev/null && break; sleep 1; done

step "Criando bd_rhu_adn com o schema e os dados do smoke"
sqlcmd -Q "CREATE DATABASE bd_rhu_adn" >/dev/null
docker cp "$ROOT/tests/smoke/rhu-schema.sql" "$SQL:/tmp/schema.sql"
docker cp "$ROOT/tests/smoke/rhu-seed.sql" "$SQL:/tmp/seed.sql"
sqlcmd -d bd_rhu_adn -i /tmp/schema.sql >/dev/null
sqlcmd -d bd_rhu_adn -i /tmp/seed.sql >/dev/null

worker() {
  if [ "$MODE" = docker ]; then
    docker run --rm --network "$NET" \
      -e ConnectionStrings__Rhu="Server=$SQL,1433;Database=bd_rhu_adn;User Id=sa;Password=$PASSWORD;Encrypt=True;TrustServerCertificate=True" \
      -e SolidesDP__BaseUrl="http://$FAKE:8080" -e SolidesDP__Token=fake-token \
      -e Sync__DryRun=false -e Sync__GoLiveDate=2026-01-01 -e Sync__ReportDirectory=/tmp/reports \
      "$TARGET" "$@"
  else
    (cd "$TARGET" && \
      ConnectionStrings__Rhu="Server=127.0.0.1,$SQL_PORT;Database=bd_rhu_adn;User Id=sa;Password=$PASSWORD;Encrypt=True;TrustServerCertificate=True" \
      SolidesDP__BaseUrl="http://127.0.0.1:$FAKE_PORT" SolidesDP__Token=fake-token \
      Sync__DryRun=false Sync__GoLiveDate=2026-01-01 Sync__ReportDirectory="$WORK/reports" \
      ./IntegracaoSolidesDP "$@")
  fi
}

writes() { fake /_fake/requests | python3 -c "import json,sys; print(sum(1 for r in json.load(sys.stdin) if r['method'] in ('POST','PUT','DELETE')))"; }

step "--version"
worker --version

step "--check-config (banco e token)"
worker --check-config || fail "--check-config"

step "--dry-run (não pode chamar a API)"
fake /_fake/requests -X DELETE -o /dev/null
worker --dry-run || fail "--dry-run"
[ "$(fake /_fake/requests | python3 -c 'import json,sys; print(len(json.load(sys.stdin)))')" = "0" ] || fail "dry-run chamou a API"

step "--run-once (real, contra o fake)"
worker --run-once || fail "--run-once"
fake /_fake/state -o "$WORK/state.json"
STATE="$WORK/state.json" python3 - <<'EOF' || fail "estado do fake diferente do esperado"
import json, os
s = json.load(open(os.environ["STATE"]))
employees = sorted(e["externalId"] for e in s["employees"])
assert employees == ["1-00000001", "1-00000002"], employees          # nem o demitido antigo nem o autônomo
assert not any(e["fired"] for e in s["employees"])
assert sorted(j["externalId"] for j in s["jobRoles"]) == ["00100", "00200"], s["jobRoles"]
assert [w["externalId"] for w in s["workplaces"]] == ["1-1"], s["workplaces"]
assert len(s["adjustments"]) == 1 and s["adjustments"][0]["status"] == "APROVADO", s["adjustments"]
print("estado do fake OK:", employees, "+ 1 férias")
EOF

step "Segunda execução: idempotente (nenhuma escrita)"
fake /_fake/requests -X DELETE -o /dev/null
worker --run-once || fail "segunda --run-once"
[ "$(writes)" = "0" ] || fail "a segunda execução escreveu na API"

printf '\nSMOKE OK (%s %s)\n' "$MODE" "$TARGET"
