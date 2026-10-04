#!/bin/zsh
# A private test room: an open arena built from any commit, with its own gateway, for agent players to join and for
# validating changes by playing them (docs/agents/spikes.md). Never the live arena.
#
#   arena/test-room.sh up "<label>" [ref]   build ref (default: HEAD of this checkout) and start the room fresh
#   arena/test-room.sh restart             start a fresh game on the running build
#   arena/test-room.sh speed 1|2|4         game speed
#   arena/test-room.sh status | down
#
# Ports: game API 7927, frames 7928, gateway 7929. Players join at http://127.0.0.1:7929 (the /play briefing there).
# Watch it over the tailnet at https://<host>.ts.net:8457/watch (gateway pages; tailnet only, no login).
set -u
SELF=${0:A}; REPO=${0:A:h:h}
UNITY=/Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/MacOS/Unity
HOME_DIR=$HOME/pezz-testroom; SRC=$HOME_DIR/src; STATE=$HOME_DIR/state
GAME=7927; FRAMES=7928; GW=7929; TSPORT=8457
mkdir -p "$HOME_DIR" "$STATE"
log() { echo "$(date '+%T') $*"; }
slug() { echo "$1" | tr 'A-Z' 'a-z' | sed -E 's/[^a-z0-9]+/-/g; s/^-|-$//g' | cut -c1-40; }
host() { tailscale status --json 2>/dev/null | python3 -c 'import json,sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))' 2>/dev/null; }

stop_pid() { local f=$1; local pid=$(cat "$f" 2>/dev/null); if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then kill "$pid"; for i in {1..20}; do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done; kill -9 "$pid" 2>/dev/null; fi; rm -f "$f"; }

down() { stop_pid "$HOME_DIR/gateway.pid"; stop_pid "$HOME_DIR/game.pid"; log "test room stopped"; }

start_gateway() {
  stop_pid "$HOME_DIR/gateway.pid"
  rm -rf "$STATE"; mkdir -p "$STATE"
  local base="https://$(host):$TSPORT"
  ( cd "$SRC/mcp" && [ -e node_modules ] || ln -s "$REPO/mcp/node_modules" node_modules )
  ( cd "$SRC/mcp" && PEZZ_GAME=http://127.0.0.1:$GAME PEZZ_FRAMES=http://127.0.0.1:$FRAMES PEZZ_GATEWAY_PORT=$GW PEZZ_GATEWAY_HOST=127.0.0.1 \
      PEZZ_MAX_ROOMS=1 PEZZ_ROOMS_FILE=$STATE/rooms.json PEZZ_SEEN_FILE=$STATE/seen.json PEZZ_SESSION_FILE=$STATE/sessions.json \
      PEZZ_COMMANDERS_FILE=$STATE/commanders.json PEZZ_MAINT_FILE=$STATE/maint.json PEZZ_REPLAY_DIR=$STATE/replays \
      PEZZ_INVENTIONS_DIR=$STATE/inventions PEZZ_PUBLIC_URL="$base" PEZZ_WATCH_DELAY=${PEZZ_WATCH_DELAY:-10} \
      nohup node gateway.js > "$HOME_DIR/gateway.log" 2>&1 & echo $! > "$HOME_DIR/gateway.pid" )
  for i in {1..30}; do curl -s -m 2 -o /dev/null "http://127.0.0.1:$GW/" && break; sleep 0.5; done
  tailscale serve status 2>/dev/null | grep -A1 ":$TSPORT " | grep -q "127.0.0.1:$GW" || tailscale serve --bg --https=$TSPORT http://127.0.0.1:$GW >/dev/null
}

start_game() {
  stop_pid "$HOME_DIR/game.pid"
  local app="$HOME_DIR/build/Pezz.app"
  # An open arena with one passive house AI, like room 1, on a fresh map. -fresh: never resume an old test game.
  open -n "$app" --args -autostart -fresh -open -port $GAME -team0 ai -team1 ai -mapsize ${PEZZ_TEST_MAP:-96} -speed ${PEZZ_TEST_SPEED:-1}
  local pid=""
  for i in {1..60}; do pid=$(pgrep -n -f "$app/Contents/MacOS"); [ -n "$pid" ] && curl -s -m 2 "http://127.0.0.1:$GAME/api/status" >/dev/null && break; sleep 1; done
  [ -n "$pid" ] || { echo "the test room didn't start"; exit 1; }
  echo "$pid" > "$HOME_DIR/game.pid"
}

up() {
  local label=$1 ref=${2:-HEAD}
  local commit=$(git -C "$REPO" rev-parse "$ref") || exit 1
  if [ ! -e "$SRC/.git" ]; then
    git -C "$REPO" worktree add -q --detach "$SRC" "$commit" || exit 1
    cp -c -R "$REPO/unity/Library" "$SRC/unity/Library"
  fi
  git -C "$SRC" checkout -q -f --detach "$commit" || exit 1
  if [ "$(cat "$HOME_DIR/build/commit" 2>/dev/null)" != "$commit" ]; then
    log "building ${commit[1,7]} for '$label'"
    rm -rf "$HOME_DIR/build.tmp"
    "$UNITY" -batchmode -projectPath "$SRC/unity" -executeMethod Pez.EditorTools.PezSetup.Build -pezOut "$HOME_DIR/build.tmp/Pezz.app" -logFile "$HOME_DIR/build.log" -quit
    grep -q "Pez build: Succeeded" "$HOME_DIR/build.log" || { echo "build failed: $HOME_DIR/build.log"; exit 1; }
    down
    rm -rf "$HOME_DIR/build"; mv "$HOME_DIR/build.tmp" "$HOME_DIR/build"; echo "$commit" > "$HOME_DIR/build/commit"
  fi
  echo "$label" > "$HOME_DIR/label"
  start_game
  start_gateway
  status
}

status() {
  local g=$(cat "$HOME_DIR/game.pid" 2>/dev/null) w=$(cat "$HOME_DIR/gateway.pid" 2>/dev/null)
  echo "label: $(cat "$HOME_DIR/label" 2>/dev/null)  build: $(git -C "$REPO" log -1 --format='%h %s' "$(cat "$HOME_DIR/build/commit" 2>/dev/null)" 2>/dev/null)"
  echo "game:    $( [ -n "$g" ] && kill -0 "$g" 2>/dev/null && echo "running (pid $g) $(curl -s -m 2 http://127.0.0.1:$GAME/api/status | python3 -c 'import json,sys; s=json.load(sys.stdin); print(f"t {s[\"time_s\"]:.0f}s, speed {s[\"speed\"]}, {len(s[\"teams\"])} seats")' 2>/dev/null)" || echo down)"
  echo "gateway: $( [ -n "$w" ] && kill -0 "$w" 2>/dev/null && echo "running (pid $w)" || echo down)"
  echo "players join: http://127.0.0.1:$GW   (briefing: http://127.0.0.1:$GW/play)"
  echo "watch:        https://$(host):$TSPORT/watch   (local http://127.0.0.1:$GW/watch)"
}

case "${1:-status}" in
  up) up "${2:-test}" "${3:-HEAD}" ;;
  restart) start_game; start_gateway; status ;;
  speed) curl -s -X POST "http://127.0.0.1:$GAME/api/admin/speed" -d "{\"speed\":${2:-1}}"; echo ;;
  status) status ;;
  down) down ;;
  *) echo "usage: $0 up <label> [ref] | restart | speed N | status | down"; exit 2 ;;
esac
