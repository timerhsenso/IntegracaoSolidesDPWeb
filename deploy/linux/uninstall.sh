#!/usr/bin/env bash
# Remove o serviço systemd. Com --remove-files apaga /opt/integracao-solidesdp (logs e relatórios inclusive).
set -euo pipefail
SERVICE=integracao-solidesdp
systemctl disable --now "$SERVICE" 2>/dev/null || true
rm -f /etc/systemd/system/$SERVICE.service
systemctl daemon-reload
if [ "${1:-}" = "--remove-files" ]; then
  rm -rf /opt/integracao-solidesdp
fi
echo "Serviço $SERVICE removido."
