#!/usr/bin/env bash
# Installs the RunnerWatch metrics agent as a systemd service on a Linux runner machine.
# Usage: sudo ./install-linux.sh [port] [optional-token]
set -euo pipefail

PORT="${1:-9465}"
TOKEN="${2:-}"
DIR=/opt/runner-agent

if [[ $EUID -ne 0 ]]; then echo "Run with sudo." >&2; exit 1; fi
command -v python3 >/dev/null || { echo "python3 not found." >&2; exit 1; }

install -d "$DIR"
install -m 0755 "$(dirname "$0")/runner_agent.py" "$DIR/runner_agent.py"

install -m 0600 /dev/null /etc/runner-agent.env
if [[ -n "$TOKEN" ]]; then printf 'RUNNER_AGENT_TOKEN=%s\n' "$TOKEN" > /etc/runner-agent.env; fi

cat > /etc/systemd/system/runner-agent.service <<EOF
[Unit]
Description=RunnerWatch metrics agent (GitHub Actions runner CPU/RAM)
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/usr/bin/env python3 $DIR/runner_agent.py --port $PORT
# Token in a root-only file: unit files are world-readable.
EnvironmentFile=-/etc/runner-agent.env
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
echo "Remember to open port $PORT in the firewall for your network only."
