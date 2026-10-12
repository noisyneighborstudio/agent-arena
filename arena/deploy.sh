#!/bin/zsh
# Deploy the staged game engine (unity/Build/next/engine: the headless .NET build) into room 1 WITHOUT ending anyone's
# game, at any time. Room 1 runs no renderer (browsers draw the game): arena/room1.mjs runs the engine.
#
#   1. POST /api/admin/save: the running game writes its snapshot (~/.config/pezz/rooms/game-PORT.json); a copy is kept.
#      A build too old to save stops the deploy here (exit 3), before anyone is told about maintenance
#   2. maintenance notice on (--preserved: agents are told to wait, their game and tokens are kept), only once the
#      deploy is certain to go ahead
#   3. stop room 1 (its supervisor, then whatever still holds the port: the engine, or the Unity app it replaced), escalating to SIGKILL
#   4. swap the staged engine in (the old one becomes unity/Build/prev/engine)
#   5. launch arena/room1.mjs on the new engine with --resume
#   6. verify: /api/status shows resumed_from, the tick carries on from the snapshot's (not 0), and the same seats exist
#   7. if that fails: put the previous build back, restore the snapshot copy, relaunch it with -resume, exit 1
#   8. maintenance off (the gateway's what's-new broadcast then announces anything new)
#
#   arena/deploy.sh              deploy the staged build; with nothing staged, does nothing (exit 0)
#   arena/deploy.sh --restart    relaunch the current build the same way (no staged build needed)
#   arena/deploy.sh --fresh      deliberately start a NEW game (the old one is discarded); RESTART_JSON='{...}' settings
#
# Non-interactive and safe to run repeatedly (CI): one deploy at a time (lock), exit 0 = deployed (or nothing to do),
# 1 = rolled back, 2 = couldn't run, 3 = the running build can't save games yet (nothing changed). Env: PEZZ_PORT (7777),
# PEZZ_MAINT_SECS (notice length, 45), PEZZ_VERIFY_SECS (how long to wait for the new build, 120).
set -u
cd "${0:A:h}/.."
ROOT=$PWD
PORT=${PEZZ_PORT:-7777}
GAME=http://127.0.0.1:$PORT
APP=unity/Build/engine
NEXT=unity/Build/next/engine
PREV=unity/Build/prev
PIDF=unity/Build/room1.pid  # the supervisor (arena/room1.mjs); unity/Build is shared by every checkout
SNAP="$HOME/.config/pezz/rooms/game-$PORT.json"
KEEP="$SNAP.predeploy"
MAINT_SECS=${PEZZ_MAINT_SECS:-45}
VERIFY_SECS=${PEZZ_VERIFY_SECS:-120}
MODE=deploy
case "${1:-}" in --restart) MODE=restart;; --fresh) MODE=fresh;; "") ;; *) echo "usage: $0 [--restart|--fresh]"; exit 2;; esac

log() { echo "$(date '+%F %T') deploy: $*"; }
LOCK="$ROOT/arena/logs/deploy.lock"; mkdir -p "$ROOT/arena/logs"
if ! mkdir "$LOCK" 2>/dev/null; then
  # A lock left by a deploy that died: take it over if its pid is gone.
  if [ -f "$LOCK/pid" ] && ! kill -0 "$(cat "$LOCK/pid")" 2>/dev/null; then rm -rf "$LOCK"; mkdir "$LOCK"; else log "another deploy is running"; exit 2; fi
fi
echo $$ > "$LOCK/pid"
trap 'rm -rf "$LOCK"' EXIT

if [ "$MODE" = deploy ] && [ ! -d "$NEXT" ]; then log "nothing staged at $NEXT"; exit 0; fi
[ -d "$APP" ] || [ -d "$NEXT" ] || { log "no build at $APP"; exit 2; }

json() { python3 -c "import sys,json; d=json.load(sys.stdin); print($1)" 2>/dev/null; }
sup_pid() { local p=$(cat "$PIDF" 2>/dev/null); [ -n "$p" ] && kill -0 "$p" 2>/dev/null && echo "$p"; }
# Only what this room's pid file names and whatever holds this room's port (the engine, or the Unity app room 1 used to be),
# never a process found by name: another copy of the game (a test, the kitchen sink) can look the same.
app_pids() { { sup_pid; lsof -tiTCP:$PORT -sTCP:LISTEN 2>/dev/null; } | sort -u ; }

stop_app() {
  local pids; pids=$(app_pids)
  [ -z "$pids" ] && return 0
  log "stopping the game (pid $(echo $pids | tr '\n' ' '))"
  # The supervisor first: it stops the agents and lets the engine save on its way out.
  local sp=$(sup_pid); [ -n "$sp" ] && kill "$sp" 2>/dev/null
  for i in {1..25}; do [ -z "$(lsof -tiTCP:$PORT -sTCP:LISTEN 2>/dev/null)" ] && [ -z "$(sup_pid)" ] && break; sleep 1; [ $i = 3 ] && kill $=pids 2>/dev/null; done
  pids=$(app_pids); [ -z "$pids" ] && { rm -f "$PIDF"; return 0; }
  log "still running after 25s; SIGKILL"
  kill -9 $=pids 2>/dev/null; sleep 1; rm -f "$PIDF"
}

launch() {  # $1: -resume | -fresh
  log "launching room 1 on $APP ($1)"
  nohup node arena/room1.mjs --engine "$ROOT/$APP" --port $PORT ${1/-/--} >> "$ROOT/arena/logs/room1.log" 2>&1 &
  echo $! > "$PIDF"; disown
}

# Waits for the relaunched game and checks it resumed: prints nothing and returns 0 if good, else the reason.
verify() {  # $1: saved tick, $2: saved seats
  local st tick resumed seats_now
  for i in $(seq 1 $VERIFY_SECS); do
    st=$(curl -s -m 3 "$GAME/api/status") && [ -n "$st" ] && break
    sleep 1
  done
  [ -z "$st" ] && { echo "the game never answered on $GAME"; return 1; }
  sleep 3; st=$(curl -s -m 3 "$GAME/api/status")
  [ "$MODE" = fresh ] && return 0
  [ -z "$1" ] && return 0 # nothing was saved to resume (no game was running)
  resumed=$(echo "$st" | json 'd.get("resumed_from") or ""')
  tick=$(echo "$st" | json 'd["tick"]')
  seats_now=$(echo "$st" | json '" ".join("%s:%s" % (t["team"], t.get("seat", "?")) for t in d["teams"])')
  [ -z "$resumed" ] && { echo "the new build started a new game instead of resuming (resumed_from is empty)"; return 1; }
  [ -z "$tick" ] || [ "$tick" -lt "$1" ] && { echo "the tick went back ($tick < saved $1)"; return 1; }
  for s in ${=2}; do [[ " $seats_now " == *" $s "* ]] || { echo "seat $s is missing after the resume (now: $seats_now)"; return 1; }; done
  return 0
}

# ---- 1. save
SAVED_TICK=""; SAVED_SEATS=""
snap_tick() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["world"]["tick"])' "$SNAP" 2>/dev/null; }
snap_seats() { python3 -c 'import json,sys; print(" ".join("%s:%s" % (i, t.get("seat", "?")) for i, t in enumerate(json.load(open(sys.argv[1]))["world"]["teams"])))' "$SNAP" 2>/dev/null; }
if [ "$MODE" != fresh ]; then
  OUT=$(mktemp -t pezz-deploy)
  code=$(curl -s -m 30 -o "$OUT" -w '%{http_code}' -X POST "$GAME/api/admin/save" -d '{}')
  res=$(cat "$OUT"); rm -f "$OUT"
  if [ "$(echo "$res" | json 'd.get("ok")')" = True ]; then
    SAVED_TICK=$(echo "$res" | json 'd["tick"]'); SAVED_SEATS=$(snap_seats)
    log "saved at tick $SAVED_TICK ($(echo "$res" | json 'd["bytes"]') bytes); seats (team:seat) $SAVED_SEATS"
  elif [ "$code" = 404 ] && [ ! -f "$SNAP" ]; then
    # The running build predates saved games: this one swap can't keep the game. Don't end it behind anyone's back.
    log "the running build can't save games (it predates resume); nothing changed. Use arena/swap-build-when-empty.sh (waits for room 1 to empty) or $0 --fresh"
    exit 3
  elif [ -f "$SNAP" ]; then
    log "the game didn't answer the save (HTTP $code); resuming from the last periodic save"
    SAVED_TICK=$(snap_tick); SAVED_SEATS=$(snap_seats)
  else
    log "no running game and no saved game: the new build starts a new one"
  fi
fi

# ---- 2. notice: only now that the swap is going ahead (a deploy that gives up above must not cry wolf)
if [ "$MODE" = fresh ]; then
  arena/maintenance.sh on "$MAINT_SECS" "Room 1 is restarting with a new game." >/dev/null
else
  arena/maintenance.sh on "$MAINT_SECS" "Room 1 is updating to a new build." --preserved >/dev/null
fi
trap 'arena/maintenance.sh off >/dev/null; rm -rf "$LOCK"' EXIT

# ---- 3. stop
stop_app
# The app may save once more on quit; either way keep a copy to roll back with.
[ -f "$SNAP" ] && cp -p "$SNAP" "$KEEP" && chmod 600 "$KEEP"

# ---- 4. swap
SWAPPED=false
if [ "$MODE" != restart ] && [ -d "$NEXT" ]; then
  rm -rf "$PREV"; mkdir -p "$PREV"
  [ -d "$APP" ] && mv "$APP" "$PREV/"
  mv "$NEXT" "$APP" && SWAPPED=true
  log "swapped in the staged build (previous one kept in $PREV)"
fi

# ---- 5./6. launch and verify
launch $([ "$MODE" = fresh ] && echo -fresh || echo -resume)
why=$(verify "$SAVED_TICK" "$SAVED_SEATS")
if [ $? -eq 0 ]; then
  if [ "$MODE" = fresh ] && [ -n "${RESTART_JSON:-}" ]; then curl -s -X POST "$GAME/api/admin/restart" -d "$RESTART_JSON" >/dev/null; fi
  log "ok: $(curl -s -m 3 "$GAME/api/status" | json '"tick %s, resumed_from %s, %d seats" % (d["tick"], d.get("resumed_from"), len(d["teams"]))')"
  exit 0
fi

# ---- 7. roll back
log "FAILED: $why"
stop_app
if $SWAPPED && [ -d "$PREV/engine" ]; then
  rm -rf unity/Build/failed; mkdir -p unity/Build/failed; mv "$APP" unity/Build/failed/ ; mv "$PREV/engine" "$APP"
  log "rolled back to the previous build (the failed one is in unity/Build/failed)"
fi
if [ -f "$KEEP" ]; then cp -p "$KEEP" "$SNAP"; chmod 600 "$SNAP"; fi
launch -resume
why2=$(verify "$SAVED_TICK" "$SAVED_SEATS") && log "the previous build resumed the game" || log "ROLLBACK ALSO FAILED: $why2"
exit 1
