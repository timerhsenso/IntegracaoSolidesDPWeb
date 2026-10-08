#!/usr/bin/env bash
# Instala ou atualiza o IntegracaoSolidesDP como serviço systemd.
# Rodar como root, de dentro da pasta extraída do tar.gz do release:
#   sudo ./install.sh
# Preserva o appsettings.json existente; só inicia o serviço se --check-config passar.
# Uso em testes automatizados: SKIP_CONFIG_CHECK=1 sudo -E ./install.sh
set -euo pipefail

INSTALL_DIR=/opt/integracao-solidesdp
SERVICE=integracao-solidesdp
SERVICE_USER=integracao-solidesdp
SOURCE="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then
  echo "Execute como root (sudo ./install.sh)." >&2
  exit 1
fi

id "$SERVICE_USER" >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"

if systemctl is-active --quiet "$SERVICE"; then
  echo "Parando $SERVICE..."
  systemctl stop "$SERVICE"
fi

mkdir -p "$INSTALL_DIR/logs" "$INSTALL_DIR/reports"
for item in "$SOURCE"/*; do
  name="$(basename "$item")"
  [ "$name" = "appsettings.json" ] && continue
  cp -R "$item" "$INSTALL_DIR/"
done
if [ ! -f "$INSTALL_DIR/appsettings.json" ]; then
  cp "$SOURCE/appsettings.json" "$INSTALL_DIR/"
  echo "appsettings.json criado em $INSTALL_DIR. Edite-o antes de iniciar o serviço (veja INSTALL.md)."
fi
chmod +x "$INSTALL_DIR/IntegracaoSolidesDP"
chown -R root:"$SERVICE_USER" "$INSTALL_DIR"
chmod 750 "$INSTALL_DIR"
chmod 640 "$INSTALL_DIR/appsettings.json"
chown -R "$SERVICE_USER":"$SERVICE_USER" "$INSTALL_DIR/logs" "$INSTALL_DIR/reports"

cp "$SOURCE/integracao-solidesdp.service" /etc/systemd/system/$SERVICE.service
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null

echo "Versão instalada: $("$INSTALL_DIR/IntegracaoSolidesDP" --version)"

if [ "${SKIP_CONFIG_CHECK:-0}" != "1" ]; then
  echo "Validando a configuração..."
  if ! (cd "$INSTALL_DIR" && sudo -u "$SERVICE_USER" ./IntegracaoSolidesDP --check-config); then
    echo "Configuração com problemas: o serviço NÃO foi iniciado. Corrija $INSTALL_DIR/appsettings.json e rode de novo." >&2
    exit 1
  fi
fi

systemctl start "$SERVICE"
echo "Serviço $SERVICE iniciado. Logs: journalctl -u $SERVICE e $INSTALL_DIR/logs; relatórios: $INSTALL_DIR/reports"
