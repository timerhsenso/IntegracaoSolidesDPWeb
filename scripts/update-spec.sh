#!/usr/bin/env bash
# Atualiza o Swagger vendorizado do Sólides DP (público, sem token).
# Se o diff mudar algo que o worker usa, os testes de contrato acusam.
set -euo pipefail
cd "$(dirname "$0")/.."
curl -fsSL https://employer.tangerino.com.br/v2/api-docs | python3 -m json.tool --indent 2 > spec/tangerino-employer.json.new
mv spec/tangerino-employer.json.new spec/tangerino-employer.json
git --no-pager diff --stat -- spec/tangerino-employer.json || true
