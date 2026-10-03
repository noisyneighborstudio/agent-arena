#!/bin/zsh
# The kitchen sink: a secret, always-on showcase room that shows every unit and structure in every state, laid out for
# artistic review (docs/art/KITCHEN_SINK.md). Its own private copy of the live build, never the live arena.
#
#   arena/kitchen-sink.sh open       start it if it isn't running, make sure the page is served on the tailnet, check
#                                    the URL answers, and print it (fast and idempotent when it's already up)
#   arena/kitchen-sink.sh status     is it up, which build, how fast it's rendering
#   arena/kitchen-sink.sh stop       stop it (and its tailnet serve); CI leaves it off until the next 'open'
#   arena/kitchen-sink.sh restart    restart it on a fresh copy of the live build
#   arena/kitchen-sink.sh ensure     for CI (arena/ci.sh): unless stopped, start it if it's down and restart it if the
#                                    live build changed since it started. Never touches the live arena.
#
# Env: PEZZ_SINK_APP (the build to copy; default: the live unity/Build/Pezz.app, read-only).
# Ports: game API 7947, frames 7948, the page 7949, tailnet https 8456 (tailscale serve, never funnel). The live arena is
# 7777/7778 with its gateway on 7790; the preview is 7957-7959; test copies use 7967/7977/7987.
set -u
REPO=${0:A:h:h}; SELF=${0:A}
SRC_APP=${PEZZ_SINK_APP:-$REPO/unity/Build/Pezz.app}
HOME_DIR=$HOME/pezz-kitchen-sink
APP=$HOME_DIR/app/Pezz.app          # never under a "Build/" folder: deploy.sh finds the live game by "Build/Pezz.app"
BIN=$APP/Contents/MacOS/Pezz
PORT=7947; FRAMES=7948; WEB=7949; TSPORT=8456
API=http://127.0.0.1:$PORT
mkdir -p "$HOME_DIR"
log() { echo "$(date '+%F %T') kitchen-sink: $*"; }
host() { tailscale status --json 2>/dev/null | python3 -c 'import json,sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))' 2>/dev/null; }
url() { echo "https://$(host):$TSPORT/"; }

game_pid() { pgrep -f "$BIN" | head -1; }
web_pid() { pgrep -f "kitchen_sink_web.py $WEB" | head -1; }
answers() { curl -s -m 3 -o /dev/null "$API/api/status"; }
# A stamp for a build: its binary's modification time and size (a deploy swaps in a new bundle).
stamp() { [ -f "$1/Contents/MacOS/Pezz" ] && stat -f '%m %z' "$1/Contents/MacOS/Pezz"; }

# One start/stop at a time (CI and a person may both ask).
LOCK=$HOME_DIR/lock
lock() {
  for i in {1..120}; do
    mkdir "$LOCK" 2>/dev/null && { echo $$ > "$LOCK/pid"; return 0; }
    [ -f "$LOCK/pid" ] && ! kill -0 "$(cat "$LOCK/pid" 2>/dev/null)" 2>/dev/null && rm -rf "$LOCK"
    sleep 1
  done
  log "another start/stop is still running"; return 1
}
unlock() { rm -rf "$LOCK"; }

stop_game() {
  local pid=$(game_pid)
  [ -z "$pid" ] && return 0
  kill "$pid" 2>/dev/null
  for i in {1..20}; do kill -0 "$pid" 2>/dev/null || return 0; sleep 0.5; done
  kill -9 "$pid" 2>/dev/null; sleep 0.5
}

# Does a build have the kitchen sink in it? (Older builds would ignore -kitchensink and start an ordinary game.)
supports() { LC_ALL=C grep -a -q KitchenSinkView "$1/Contents/Resources/Data/Managed/Assembly-CSharp.dll" 2>/dev/null; }

# Stage a copy of the source build in app.new (the running copy is untouched); install_build swaps it in once stopped.
stage_build() {
  rm -rf "$HOME_DIR/app.new"
  [ -d "$SRC_APP" ] || { log "no build at $SRC_APP"; return 1; }
  if ! supports "$SRC_APP"; then
    log "$SRC_APP predates the kitchen sink: keeping the room's current build (PEZZ_SINK_APP=<a newer Pezz.app> $SELF restart to change it)"
    stamp "$SRC_APP" > "$HOME_DIR/skipped-stamp"
    return 1
  fi
  # An APFS clone of the live app (instant); the live app itself is only read.
  rm -rf "$HOME_DIR/app.new"; mkdir -p "$HOME_DIR/app.new"
  cp -c -R "$SRC_APP" "$HOME_DIR/app.new/Pezz.app" 2>/dev/null || cp -R "$SRC_APP" "$HOME_DIR/app.new/Pezz.app" || { rm -rf "$HOME_DIR/app.new"; return 1; }
  stamp "$SRC_APP" > "$HOME_DIR/app.new/source-stamp"
  local commit=$(cat "$REPO/ci-work/deployed" 2>/dev/null)
  [ "$SRC_APP" != "$REPO/unity/Build/Pezz.app" ] && commit=""   # a hand-picked build, not the deployed one
  local built=$(date -r "$(stamp "$SRC_APP" | cut -d' ' -f1)" '+%b %d %H:%M')
  echo "${commit[1,7]:-local} · built $built" > "$HOME_DIR/app.new/build.txt"
}

install_build() {
  [ -d "$HOME_DIR/app.new/Pezz.app" ] || return 0
  rm -rf "$HOME_DIR/app"; mv "$HOME_DIR/app.new" "$HOME_DIR/app"
  cp "$HOME_DIR/app/source-stamp" "$HOME_DIR/source-stamp"; cp "$HOME_DIR/app/build.txt" "$HOME_DIR/build.txt"
}

start_game() {
  [ -n "$(game_pid)" ] && answers && return 0
  if lsof -iTCP:$PORT -sTCP:LISTEN >/dev/null 2>&1 || lsof -iTCP:$FRAMES -sTCP:LISTEN >/dev/null 2>&1; then
    log "port $PORT or $FRAMES is in use by something else; not starting"; return 1
  fi
  [ -x "$BIN" ] || { stage_build && install_build; } || return 1
  # Windowed 1280x720 (the stream's size), the showcase scenario, its own log; nice so the live arena wins any contest.
  open -n "$APP" --args -kitchensink -autostart -port $PORT -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile "$HOME_DIR/player.log"
  local pid=""
  for i in {1..90}; do pid=$(game_pid); [ -n "$pid" ] && answers && break; sleep 1; done
  [ -n "$pid" ] && answers || { log "the room didn't start (see $HOME_DIR/player.log)"; return 1; }
  renice -n 10 -p "$pid" >/dev/null 2>&1
  log "room running (pid $pid, $(cat "$HOME_DIR/build.txt" 2>/dev/null))"
}

start_web() {
  if [ -z "$(web_pid)" ] || ! curl -s -m 2 -o /dev/null "http://127.0.0.1:$WEB/speed"; then
    [ -n "$(web_pid)" ] && kill "$(web_pid)" 2>/dev/null
    (nohup python3 "$REPO/arena/kitchen_sink_web.py" $WEB >> "$HOME_DIR/web.log" 2>&1 &)
    for i in {1..20}; do curl -s -m 1 -o /dev/null "http://127.0.0.1:$WEB/speed" && break; sleep 0.25; done
  fi
  # Tailnet only: tailscale serve, never funnel.
  tailscale serve status 2>/dev/null | grep -A1 ":$TSPORT " | grep -q "127.0.0.1:$WEB" || tailscale serve --bg --https=$TSPORT http://127.0.0.1:$WEB >/dev/null
}

check_url() {
  local code
  for i in {1..10}; do
    code=$(curl -s -m 5 -o /dev/null -w '%{http_code}' "$(url)")
    [ "$code" = 200 ] && return 0
    sleep 1
  done
  log "the tailnet URL answered $code"; return 1
}

report() {
  echo "Kitchen sink: $(url)"
  echo "  local:  http://127.0.0.1:$WEB/"
  echo "  build:  $(cat "$HOME_DIR/build.txt" 2>/dev/null)"
  echo "  tailnet only, no login: anyone on the tailnet can watch it and move its camera."
  echo "  stop with: arena/kitchen-sink.sh stop"
}

cmd_open() {
  rm -f "$HOME_DIR/off"
  if [ -n "$(game_pid)" ] && answers; then start_web; check_url; report; return; fi
  lock || return 1
  start_game; local rc=$?
  unlock
  [ $rc = 0 ] || return 1
  start_web; check_url; report
}

cmd_restart() {
  rm -f "$HOME_DIR/off"
  lock || return 1
  stage_build || [ -x "$BIN" ] || { unlock; return 1; }   # a build without the kitchen sink keeps the current copy
  stop_game
  install_build
  start_game; local rc=$?
  unlock
  [ $rc = 0 ] || return 1
  start_web; check_url >/dev/null && log "restarted: $(url)"
}

cmd_ensure() {
  [ -f "$HOME_DIR/off" ] && return 0
  if [ -n "$(game_pid)" ] && answers; then
    # Still on the build it was copied from? A deploy swaps the live app: follow it.
    local now=$(stamp "$SRC_APP")
    if [ -z "$now" ] || [ "$now" = "$(cat "$HOME_DIR/source-stamp" 2>/dev/null)" ] || [ "$now" = "$(cat "$HOME_DIR/skipped-stamp" 2>/dev/null)" ]; then start_web; return 0; fi
    supports "$SRC_APP" || { stamp "$SRC_APP" > "$HOME_DIR/skipped-stamp"; log "the live build predates the kitchen sink: staying on $(cat "$HOME_DIR/build.txt")"; start_web; return 0; }
    log "the live build changed: restarting on it"
    cmd_restart; return
  fi
  log "not running: starting"
  lock || return 1
  # On the live build if it has the kitchen sink, else on the copy it last ran.
  { stage_build && install_build || [ -x "$BIN" ]; } && start_game; local rc=$?
  unlock
  [ $rc = 0 ] && start_web
}

cmd_stop() {
  touch "$HOME_DIR/off"
  lock || return 1
  stop_game
  unlock
  [ -n "$(web_pid)" ] && kill "$(web_pid)" 2>/dev/null
  tailscale serve --https=$TSPORT off >/dev/null 2>&1
  log "stopped (CI leaves it off until 'open')"
}

cmd_status() {
  local pid=$(game_pid)
  if [ -n "$pid" ] && answers; then
    local s=$(curl -s -m 3 "$API/api/status")
    echo "running (pid $pid): $(echo "$s" | python3 -c 'import json,sys; d=json.load(sys.stdin); print("%d:%02d in, %.0f fps, speed %s%s, %s sim errors" % (d["time_s"]//60, d["time_s"]%60, d.get("render_fps") or 0, d["speed"], " (paused)" if d["paused"] else "", d.get("sim_errors", 0)))')"
    local note=""
    if [ "$(stamp "$SRC_APP")" != "$(cat "$HOME_DIR/source-stamp" 2>/dev/null)" ]; then
      supports "$SRC_APP" && note=" (the live build has changed since; 'restart' or CI's 'ensure' picks it up)" \
                          || note=" (the live build predates the kitchen sink, so the room stays on this one)"
    fi
    echo "  build: $(cat "$HOME_DIR/build.txt" 2>/dev/null)$note"
    echo "  page:  $(url)  (local http://127.0.0.1:$WEB/, $([ -n "$(web_pid)" ] && echo up || echo down))"
  else
    echo "not running$([ -f "$HOME_DIR/off" ] && echo " (stopped by hand; 'open' starts it)")"
  fi
}

case "${1:-status}" in
  open) cmd_open ;;
  status) cmd_status ;;
  stop) cmd_stop ;;
  restart) cmd_restart ;;
  ensure) cmd_ensure ;;
  *) echo "usage: $0 open|status|stop|restart|ensure"; exit 2 ;;
esac
