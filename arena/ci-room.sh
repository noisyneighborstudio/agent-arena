#!/bin/zsh
# The CI room: the kitchen sink and the browser view's UI gate, with no GPU anywhere. The headless .NET engine runs the
# showcase world (headless --kitchen-sink), a private gateway copy serves it and the browser build (Unity WebGL) is the
# viewer. Two jobs (docs/CI_ROOM.md):
#   1. the owner's test and showcase room: `open`, then look around everything in the new renderer, over the tailnet;
#   2. a CI gate: before CI deploys a commit it brings the room up on the candidate build and runs `verify`, an automated
#      in-browser UI check (arena/webclient/verify.mjs); a failure blocks the deploy.
#
#   arena/ci-room.sh up [--build <web build dir>] [--checkout <repo dir>]
#                        start (or restart onto a different build) the whole room; idempotent. --checkout is the repo whose
#                        engine (headless/bin) and gateway (mcp/) run (default ci-work/live, else this one); --build is the
#                        browser build (default <checkout>/build/web).
#   arena/ci-room.sh verify   the UI check; exit non-zero on any failure; results and screenshots in ci-work/ci-room/last/
#   arena/ci-room.sh gate [up options]   what CI runs: `up` then `verify`, and the room is left alone by `ensure` meanwhile.
#                        Exit 0 passed, 1 failed, 2 the check couldn't run (no Playwright)
#   arena/ci-room.sh status   what's up, on which build, and the last check
#   arena/ci-room.sh down     stop everything (and the owner's tailnet serve, if it is ours)
#   arena/ci-room.sh open     the owner's command: up (same options), then serve the page on the tailnet and print the URL
#   arena/ci-room.sh ensure [up options]   for CI after a gate or a deploy: if the owner wants the room (they ran `open`
#                        and not `down`), bring it up on the given build; if not, stop it so the ports are free
#
# The showcase room and the check room are two stacks, because the kitchen sink has no seats to join (nobody plays in it) and the
# player checks need a commander link: the showcase (engine --kitchen-sink + gateway + the page) is what people look at, the check room
# (an ordinary open room with its house AI, + its own gateway) is what `verify` plays a seat in. `verify` restarts the check
# room fresh every time. Neither touches the live arena or ~/.config/pezz (the gateways run with a HOME under ci-work/ci-room).
#
# Env: PEZZ_CIROOM_PORT (first port, default 7910; the room uses base+0, +2, +3, +4, +6), PEZZ_CIROOM_STATE (state folder; default
# <repo>/ci-work/ci-room), PEZZ_CIROOM_TAILNET_PORT (default 8456), PEZZ_CIROOM_NO_TAILSCALE=1 (open: don't touch tailscale; print the
# local URL only), PEZZ_NODE / PEZZ_DOTNET (absolute paths; the asdf shims need the real HOME, which the gateway doesn't have).
set -u
REPO=${0:A:h:h}; SELF=${0:A}
CI="$REPO/ci-work"
STATE=${PEZZ_CIROOM_STATE:-$CI/ci-room}
BASE=${PEZZ_CIROOM_PORT:-7910}
# Each engine leaves the next port free: a gateway reads its room's frames from engine port + 1.
P_SINK=$BASE; P_SINK_GW=$((BASE + 2)); P_PAGE=$((BASE + 3)); P_CHECK=$((BASE + 4)); P_CHECK_GW=$((BASE + 6))
TSPORT=${PEZZ_CIROOM_TAILNET_PORT:-8456}
NODE=${PEZZ_NODE:-$(asdf which node 2>/dev/null || command -v node)}
DOTNET=${PEZZ_DOTNET:-$(command -v dotnet)}
PAGE_URL=http://127.0.0.1:$P_PAGE/ci-room
mkdir -p "$STATE/pids" "$STATE/logs"
log() { echo "$(date '+%F %T') ci-room: $*"; }
die() { log "$*"; exit 1; }

# ---------------------------------------------------------------- processes (only ever the PIDs this script recorded)
pid_of() { cat "$STATE/pids/$1.pid" 2>/dev/null; }
# Is the recorded process still ours? (a reused PID must never be signalled)
alive() { local p=$(pid_of $1); [ -n "$p" ] && kill -0 "$p" 2>/dev/null && ps -p "$p" -o command= 2>/dev/null | grep -q "$2"; }
spawn() { local name=$1; shift; ( nohup "$@" >> "$STATE/logs/$name.log" 2>&1 & echo $! > "$STATE/pids/$name.pid" ); }
stop_one() {
  local name=$1 p=$(pid_of $1)
  if [ -n "$p" ] && kill -0 "$p" 2>/dev/null && ps -p "$p" -o command= 2>/dev/null | grep -q "$2"; then
    kill "$p" 2>/dev/null
    for i in {1..40}; do kill -0 "$p" 2>/dev/null || break; sleep 0.25; done
    kill -0 "$p" 2>/dev/null && { kill -9 "$p" 2>/dev/null; sleep 0.3; }
  fi
  rm -f "$STATE/pids/$name.pid"
}
answers() { curl -s -m 3 -o /dev/null -f "$1"; }
wait_for() { for i in {1..60}; do answers "$1" && return 0; sleep 0.5; done; return 1; }

# One start/stop at a time (CI and the owner may both ask).
LOCK=$STATE/lock
lock() {
  for i in {1..120}; do
    mkdir "$LOCK" 2>/dev/null && { echo $$ > "$LOCK/pid"; return 0; }
    [ -f "$LOCK/pid" ] && ! kill -0 "$(cat "$LOCK/pid" 2>/dev/null)" 2>/dev/null && rm -rf "$LOCK"
    sleep 1
  done
  die "another start or stop is still running"
}
unlock() { rm -rf "$LOCK"; }

# ---------------------------------------------------------------- what runs where
CO=""; WEB=""; DLL=""
resolve() {  # <checkout or ""> <build or "">: sets CO, WEB, DLL; fails with a message if anything is missing
  CO=$1; WEB=$2
  [ -z "$CO" ] && { [ -f "$CI/live/headless/bin/Release/net10.0/pez-headless.dll" ] && CO=$CI/live || CO=$REPO; }
  CO=${CO:A}
  if [ -z "$WEB" ]; then
    for w in "$CO/build/web" "$CI/live/build/web" "$REPO/build/web"; do [ -f "$w/index.html" ] && { WEB=$w; break; }; done
  fi
  [ -n "$WEB" ] && [ -f "$WEB/index.html" ] || { log "no browser build (index.html) in ${WEB:-$CO/build/web}: pass --build <dir>"; return 1; }
  WEB=${WEB:A}
  DLL=$CO/headless/bin/Release/net10.0/pez-headless.dll
  if [ ! -f "$DLL" ]; then
    log "building the engine in $CO/headless"
    (cd "$CO/headless" && "$DOTNET" build -c Release >> "$STATE/logs/engine-build.log" 2>&1)
  fi
  [ -f "$DLL" ] || { log "no engine at $DLL"; return 1; }
  [ -f "$CO/mcp/gateway.js" ] || { log "no gateway at $CO/mcp/gateway.js"; return 1; }
  # A fresh checkout has no node_modules: share this repo's (what ci.sh does for its checkouts).
  [ -e "$CO/mcp/node_modules" ] || { [ -d "$REPO/mcp/node_modules" ] && ln -s "$REPO/mcp/node_modules" "$CO/mcp/node_modules"; }
  [ -d "$CO/mcp/node_modules" ] || { log "no node_modules for the gateway in $CO/mcp"; return 1; }
  return 0
}
info() { "$NODE" -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1]))[process.argv[2]]??""))}catch{}' "$STATE/info.json" "$1"; }
# What the running room was started with (for verify, which restarts the check room onto the same build).
load_info() { CO=$(info checkout); WEB=$(info web); DLL=$CO/headless/bin/Release/net10.0/pez-headless.dll; [ -f "$DLL" ] && [ -f "$WEB/index.html" ]; }
# The build and engine the room is running, so `up` can tell when it has to restart onto something else.
stamp() { echo "$CO|$WEB|$(stat -f '%m %z' "$WEB/index.html")|$(stat -f '%m %z' "$DLL")|$(stat -f %m "$SELF:h/ci-room-page.mjs" "$SELF:h/ci-room.html")"; }

start_engine_sink() { spawn engine-sink "$DOTNET" "$DLL" --port $P_SINK --kitchen-sink --fresh --snapshot "$STATE/sink-unused.json"; }
start_engine_check() { spawn engine-check "$DOTNET" "$DLL" --port $P_CHECK --open "$1" --snapshot "$STATE/check-game.json"; }  # --fresh | --resume
# A gateway copy: its own HOME (never ~/.config/pezz), one room, a short spectator delay (it's a private room), no overflow rooms.
start_gateway() {  # <name> <home> <gateway port> <engine port>
  mkdir -p "$2"
  spawn "$1" env HOME="$2" PEZZ_GAME=http://127.0.0.1:$4 PEZZ_GATEWAY_PORT=$3 PEZZ_WEB_DIR="$WEB" PEZZ_MAX_ROOMS=1 PEZZ_WATCH_DELAY=3 PEZZ_LOCAL_ROOMS=0 "$NODE" "$CO/mcp/gateway.js"
}
start_page() { spawn page "$NODE" "$SELF:h/ci-room-page.mjs" --port $P_PAGE --gateway http://127.0.0.1:$P_SINK_GW --engine http://127.0.0.1:$P_SINK --info "$STATE/info.json"; }

# The check room's pieces are restarted together, from nothing, so every check starts from the same world.
restart_check() {  # --fresh | --resume
  stop_one gw-check gateway.js; stop_one engine-check pez-headless
  [ "$1" = --fresh ] && rm -rf "${STATE:?}/home-check"
  start_engine_check "$1"; wait_for http://127.0.0.1:$P_CHECK/api/status || { log "the check room's engine didn't start (logs/engine-check.log)"; return 1; }
  start_gateway gw-check "$STATE/home-check" $P_CHECK_GW $P_CHECK; wait_for http://127.0.0.1:$P_CHECK_GW/lobby || { log "the check room's gateway didn't start (logs/gw-check.log)"; return 1; }
}

showcase_up() { alive engine-sink pez-headless && alive gw-sink gateway.js && alive page ci-room-page && answers http://127.0.0.1:$P_SINK/api/status && answers $PAGE_URL; }
all_up() { showcase_up && alive engine-check pez-headless && alive gw-check gateway.js && answers http://127.0.0.1:$P_CHECK_GW/lobby; }
stop_all() {
  stop_one page ci-room-page; stop_one gw-sink gateway.js; stop_one gw-check gateway.js
  stop_one engine-sink pez-headless; stop_one engine-check pez-headless
}

cmd_up() {
  local build="" checkout=""
  while [ $# -gt 0 ]; do case $1 in --build) build=${2:?--build needs a directory}; shift 2 ;; --checkout) checkout=${2:?--checkout needs a directory}; shift 2 ;; *) die "up: unknown option $1" ;; esac; done
  resolve "$checkout" "$build" || return 1
  lock || return 1
  local rc=0
  if all_up && [ "$(stamp)" = "$(cat "$STATE/config" 2>/dev/null)" ]; then
    log "already up on $(info build)"
  else
    stop_all
    start_engine_sink; start_gateway gw-sink "$STATE/home" $P_SINK_GW $P_SINK
    { wait_for http://127.0.0.1:$P_SINK/api/status && wait_for http://127.0.0.1:$P_SINK_GW/lobby; } || { log "the showcase room didn't start (logs/engine-sink.log, logs/gw-sink.log)"; rc=1; }
    if [ $rc = 0 ]; then
      local commit=$(git -C "$CO" rev-parse --short HEAD 2>/dev/null)
      printf '{"build":"%s built %s","checkout":"%s","web":"%s"}\n' "${commit:-local}" "$(date -r "$(stat -f %m "$WEB/index.html")" '+%b %d %H:%M')" "$CO" "$WEB" > "$STATE/info.json"
      start_page; wait_for $PAGE_URL || { log "the page didn't start (logs/page.log)"; rc=1; }
    fi
    [ $rc = 0 ] && { restart_check --fresh || rc=1; }
    [ $rc = 0 ] && { stamp > "$STATE/config"; log "up on $(git -C "$CO" rev-parse --short HEAD 2>/dev/null) ($WEB): $PAGE_URL"; }
    [ $rc = 0 ] || stop_all
  fi
  unlock
  return $rc
}

cmd_verify() {
  showcase_up && load_info || die "the room isn't up (arena/ci-room.sh up --build <dir> first)"
  local out=$STATE/last
  rm -rf "$out"; mkdir -p "$out"
  restart_check --fresh || { echo "FAIL: the check room didn't start" > "$out/summary.txt"; return 1; }
  log "verifying: the browser build in the check room (a few minutes; screenshots in $out)"
  "$NODE" "$SELF:h/webclient/verify.mjs" --gateway http://127.0.0.1:$P_CHECK_GW --engine http://127.0.0.1:$P_CHECK \
    --page "http://127.0.0.1:$P_PAGE" --ctl "$SELF" --out "$out" 2>&1 | tee "$out/verify.log"
  local rc=${pipestatus[1]}
  local summary=$(tail -1 "$out/verify.log")
  echo "$([ $rc = 0 ] && echo PASS || echo FAIL) $(date '+%F %T') $(info build): $summary" > "$out/summary.txt"
  [ $rc = 0 ] && log "UI check passed" || log "UI check FAILED (see $out/verify.log and the screenshots next to it)"
  return $rc
}

# Used by verify.mjs (stage 4): stop the check room's engine (it saves on SIGTERM) and bring it back resumed. Only ever our own PID.
cmd_engine_check() {
  case "${1:-}" in
    stop) curl -s -m 5 -X POST "http://127.0.0.1:$P_CHECK/api/admin/save" -d '{}' >/dev/null 2>&1
          alive engine-check pez-headless || die "the check room's engine isn't running"; stop_one engine-check pez-headless; echo "engine stopped" ;;
    resume) load_info || die "no engine to resume with"
            start_engine_check --resume; wait_for http://127.0.0.1:$P_CHECK/api/status || die "the engine didn't come back"; echo "engine resumed" ;;
    *) die "usage: engine-check stop|resume" ;;
  esac
}

cmd_status() {
  local rc=0 name
  for pair in "showcase engine:engine-sink:pez-headless:$P_SINK" "showcase gateway:gw-sink:gateway.js:$P_SINK_GW" "page:page:ci-room-page:$P_PAGE" "check engine:engine-check:pez-headless:$P_CHECK" "check gateway:gw-check:gateway.js:$P_CHECK_GW"; do
    local -a f; f=("${(@s/:/)pair}")
    if alive $f[2] $f[3]; then echo "  ${f[1]}: up (pid $(pid_of $f[2]), port $f[4])"; else echo "  ${f[1]}: down"; rc=1; fi
  done
  [ -f "$STATE/info.json" ] && echo "  build: $(info build) ($(info web))"
  [ -f "$STATE/owner" ] && echo "  the owner has it open (arena/ci-room.sh down to stop)"
  echo "  page:  $PAGE_URL"
  [ -f "$STATE/last/summary.txt" ] && echo "  last check: $(/bin/cat "$STATE/last/summary.txt")"
  all_up && echo "running" || echo "not fully running"
  return $rc
}

# ---------------------------------------------------------------- the tailnet (tailscale serve, never funnel)
ts_host() { tailscale status --json 2>/dev/null | python3 -c 'import json,sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))' 2>/dev/null; }
ts_line() { tailscale serve status 2>/dev/null | grep -A1 ":$TSPORT " ; }
ts_ours() { ts_line | grep -q "127.0.0.1:$P_PAGE"; }
ts_serve() {
  [ "${PEZZ_CIROOM_NO_TAILSCALE:-}" = 1 ] && return 0
  if ts_ours; then :
  elif [ -n "$(ts_line)" ]; then
    log "tailnet port $TSPORT is serving something else (the old Unity kitchen sink?): run arena/kitchen-sink.sh stop first, then arena/ci-room.sh open"; return 1
  else tailscale serve --bg --https=$TSPORT http://127.0.0.1:$P_PAGE >/dev/null || return 1
  fi
  local url="https://$(ts_host):$TSPORT/" code=""
  for i in {1..10}; do code=$(curl -s -m 5 -o /dev/null -w '%{http_code}' "$url"); [ "$code" = 200 ] && break; sleep 1; done
  [ "$code" = 200 ] || { log "the tailnet URL answered ${code:-nothing}"; return 1; }
  TS_URL=$url
}
ts_off() { [ "${PEZZ_CIROOM_NO_TAILSCALE:-}" = 1 ] && return 0; ts_ours && tailscale serve --https=$TSPORT off >/dev/null 2>&1; return 0; }

cmd_open() {
  touch "$STATE/owner"
  cmd_up "$@" || return 1
  TS_URL=""
  ts_serve || return 1
  if [ -n "$TS_URL" ]; then
    echo "CI room: $TS_URL"
    echo "  local:  $PAGE_URL"
    echo "  tailnet only, no login: anyone on the tailnet can watch it and look around. It is the browser build on the kitchen sink room."
    echo "  stop with: arena/ci-room.sh down"
  else
    echo "CI room: $PAGE_URL"
    echo "  (not served on the tailnet: PEZZ_CIROOM_NO_TAILSCALE=1)"
  fi
}

cmd_down() {
  rm -f "$STATE/owner"
  ts_off
  lock || return 1
  stop_all
  unlock
  log "stopped"
}

# A gate check in progress (see cmd_gate) owns the room: ensure leaves it alone rather than restart it onto another build mid-check.
gate_running() { local p=$(cat "$STATE/busy" 2>/dev/null); [ -n "$p" ] && kill -0 "$p" 2>/dev/null; }
cmd_ensure() {
  gate_running && { log "a gate check is running: leaving the room alone"; return 0; }
  if [ -f "$STATE/owner" ]; then cmd_up "$@" || { lock && { stop_all; unlock; }; return 1; }
  else lock && { stop_all; unlock; }; fi
}

# What CI runs on a candidate build: bring the room up on it and run the UI check. Exit 0 passed, 1 failed (the candidate's engine,
# gateway or view is broken), 2 the check itself couldn't run (no Playwright): CI should not deploy, and should retry.
cmd_gate() {
  gate_running && die "another gate check is running"
  echo $$ > "$STATE/busy"; trap 'rm -f "$STATE/busy"' EXIT
  cmd_up "$@" || return 1
  cmd_verify
}

cmd=${1:-status}; [ $# -gt 0 ] && shift
case $cmd in
  up) cmd_up "$@" ;;
  verify) cmd_verify ;;
  gate) cmd_gate "$@" ;;
  status) cmd_status ;;
  down) cmd_down ;;
  open) cmd_open "$@" ;;
  ensure) cmd_ensure "$@" ;;
  engine-check) cmd_engine_check "$@" ;;
  *) echo "usage: $0 up [--build <dir>] [--checkout <dir>] | verify | gate [up options] | status | down | open [up options] | ensure [up options]"; exit 2 ;;
esac
