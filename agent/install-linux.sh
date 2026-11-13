#!/usr/bin/env bash
# Instala o agente de métricas como serviço systemd numa máquina runner Linux.
# Uso: sudo ./install-linux.sh [porta] [token-opcional]
set -euo pipefail

PORT="${1:-9465}"
TOKEN="${2:-}"
DIR=/opt/runner-agent

if [[ $EUID -ne 0 ]]; then echo "Rode com sudo." >&2; exit 1; fi
command -v python3 >/dev/null || { echo "python3 não encontrado." >&2; exit 1; }

install -d "$DIR"
install -m 0755 "$(dirname "$0")/runner_agent.py" "$DIR/runner_agent.py"

cat > /etc/systemd/system/runner-agent.service <<EOF
[Unit]
Description=GitHub Actions runner metrics agent
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/usr/bin/env python3 $DIR/runner_agent.py --port $PORT
Environment=RUNNER_AGENT_TOKEN=$TOKEN
DynamicUser=yes
Restart=always
RestartSec=5
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
MemoryMax=64M

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now runner-agent
echo "OK: http://$(hostname):$PORT/metrics"
echo "Lembre de liberar a porta $PORT no firewall só para a sua rede."
