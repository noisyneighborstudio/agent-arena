#!/bin/zsh
# Swap in a staged Unity build (unity/Build/next/Pezz.app, from PezSetup.Build -pezOut Build/next/Pezz.app) once room 1
# has no outside players, then relaunch the open arena on it. Relaunching resets room 1, so it waits for it to empty.
# The standalone gateway keeps running throughout, so agents' MCP sessions and the overflow rooms are unaffected.
#
#   arena/swap-build-when-empty.sh [player name to ignore ...]     e.g. a stray idle seat
#   RESTART_JSON='{"ore_scale":0.3,"open":true}' arena/swap-build-when-empty.sh   keep a scarce map after the swap
set -u
cd "${0:A:h}/.."
NEXT=unity/Build/next/Pezz.app
[ -d "$NEXT" ] || { echo "no staged build at $NEXT"; exit 1; }
IGNORE=$(printf '%s\n' "$@" | python3 -c 'import sys,json; print(json.dumps([l.strip() for l in sys.stdin if l.strip()]))')
log() { echo "$(date '+%F %T') $*"; }

while true; do
  # Seats 0 and 1 are the in-app agents; anyone else still playing is an outside player.
  outside=$(curl -s --max-time 5 http://127.0.0.1:7777/api/lobby | IGNORE="$IGNORE" python3 -c '
import sys, json, os
ignore = set(json.loads(os.environ["IGNORE"]))
try: teams = json.load(sys.stdin)["teams"]
except Exception: print(-1); sys.exit()
print(sum(1 for i, t in enumerate(teams) if i >= 2 and t["status"] == "playing" and not t.get("house") and t.get("player") not in ignore))')
  [ "$outside" = "0" ] && break
  [ "$outside" = "-1" ] && log "room 1 isn't answering; swapping now" && break
  sleep 60
done

log "room 1 is empty; swapping in the staged build"
# Tell connected agents (other rooms, and anyone who reconnects) to hold on rather than leave.
arena/maintenance.sh on 60 "Room 1 is updating to a new build." >/dev/null
# Stop the running game by process id ("quit app" can be ignored, and then the relaunch just re-activates the old one).
OLD=$(pgrep -f "Build/Pezz.app/Contents/MacOS/Pezz")
[ -n "$OLD" ] && kill $OLD
for i in {1..20}; do pgrep -f "Build/Pezz.app/Contents/MacOS/Pezz" >/dev/null || break; sleep 1; done
pgrep -f "Build/Pezz.app/Contents/MacOS/Pezz" >/dev/null && pkill -9 -f "Build/Pezz.app/Contents/MacOS/Pezz"
rm -rf unity/Build/prev; mv unity/Build/Pezz.app unity/Build/prev && mv "$NEXT" unity/Build/Pezz.app
open -n -a "$PWD/unity/Build/Pezz.app" --args -team0 claude -model0 sonnet -effort0 medium -team1 codex -model1 gpt-5.6-luna -effort1 medium \
  -open -gatewayurl https://pezz.sethwebster.com -autostart
for i in {1..60}; do curl -s -o /dev/null http://127.0.0.1:7777/api/status && break; sleep 1; done
# Optional game settings for the new arena, e.g. RESTART_JSON='{"ore_scale":0.3,"open":true}'
[ -n "${RESTART_JSON:-}" ] && sleep 2 && curl -s -X POST http://127.0.0.1:7777/api/admin/restart -d "$RESTART_JSON" >/dev/null
arena/maintenance.sh off >/dev/null
log "relaunched: $(curl -s http://127.0.0.1:7777/api/lobby | head -c 200)"
