#!/bin/zsh
# Every Pezz session on this machine, on one tailnet-only page (arena/sessions.py): status, players and every viewer.
#
#   arena/sessions.sh up | down | status
#
# Local page: http://127.0.0.1:7426/   Tailnet: https://<host>.ts.net:8458/ (tailscale serve; never funnel).
set -u
REPO=${0:A:h:h}
PORT=7426; TSPORT=8458; PIDF=$HOME/.config/pezz/sessions.pid
mkdir -p "${PIDF:h}"
host() { tailscale status --json 2>/dev/null | python3 -c 'import json,sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))' 2>/dev/null; }

down() { local p=$(cat "$PIDF" 2>/dev/null); [ -n "$p" ] && kill "$p" 2>/dev/null; rm -f "$PIDF"; }

up() {
  down
  nohup python3 "$REPO/arena/sessions.py" --port $PORT > "$REPO/arena/logs/sessions.log" 2>&1 & echo $! > "$PIDF"
  for i in {1..20}; do curl -s -m 2 -o /dev/null "http://127.0.0.1:$PORT/" && break; sleep 0.3; done
  tailscale serve status 2>/dev/null | grep -A1 ":$TSPORT " | grep -q "127.0.0.1:$PORT" || tailscale serve --bg --https=$TSPORT http://127.0.0.1:$PORT >/dev/null
  status
}

status() {
  local p=$(cat "$PIDF" 2>/dev/null)
  echo "sessions page: $( [ -n "$p" ] && kill -0 "$p" 2>/dev/null && echo "running (pid $p)" || echo down)"
  echo "tailnet: https://$(host):$TSPORT/   local: http://127.0.0.1:$PORT/   stop: arena/sessions.sh down (and tailscale serve --https=$TSPORT off)"
}

case "${1:-status}" in
  up) up ;;
  down) down; echo "stopped" ;;
  status) status ;;
  *) echo "usage: $0 up | down | status"; exit 2 ;;
esac
