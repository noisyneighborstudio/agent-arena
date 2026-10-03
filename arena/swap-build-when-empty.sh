#!/bin/zsh
# Swap in a staged Unity build (unity/Build/next/Pezz.app, from PezSetup.Build -pezOut Build/next/Pezz.app).
#
# Games survive a swap now: arena/deploy.sh saves room 1, relaunches the new build with -resume, checks the game came
# back (same tick onward, same seats) and rolls back if it didn't. So by default this swaps right away, whoever is playing.
# It only waits for room 1 to have no outside players when a NEW game is wanted (RESTART_JSON), or when the running
# build is too old to save its game.
#
#   arena/swap-build-when-empty.sh                                   swap now; the game resumes
#   RESTART_JSON='{"ore_scale":0.3,"open":true}' arena/swap-build-when-empty.sh [player name to ignore ...]
#                                                                    wait until room 1 is empty, then start a new game
set -u
cd "${0:A:h}/.."
[ -d unity/Build/next/Pezz.app ] || { echo "no staged build at unity/Build/next/Pezz.app"; exit 1; }
IGNORE=$(printf '%s\n' "$@" | python3 -c 'import sys,json; print(json.dumps([l.strip() for l in sys.stdin if l.strip()]))')
log() { echo "$(date '+%F %T') $*"; }

wait_until_empty() {
  while true; do
    # Seats 0 and 1 are the in-app agents; anyone else still playing (and not idle for PEZZ_CI_IDLE_S) is an outside player.
    outside=$(curl -s --max-time 5 http://127.0.0.1:7777/api/lobby | IGNORE="$IGNORE" IDLE="${PEZZ_CI_IDLE_S:-1800}" python3 -c '
import sys, json, os
ignore = set(json.loads(os.environ["IGNORE"]))
try: teams = json.load(sys.stdin)["teams"]
except Exception: print(-1); sys.exit()
print(sum(1 for i, t in enumerate(teams) if i >= 2 and t["status"] == "playing" and not t.get("house") and t.get("player") not in ignore and t.get("idle_s", 0) < float(os.environ["IDLE"])))')
    [ "$outside" = "0" ] && return
    [ "$outside" = "-1" ] && log "room 1 isn't answering; swapping now" && return
    sleep 60
  done
}

if [ -n "${RESTART_JSON:-}" ]; then
  wait_until_empty
  log "room 1 is empty; swapping in the staged build with a new game"
  exec arena/deploy.sh --fresh
fi
arena/deploy.sh
rc=$?
if [ $rc -eq 3 ]; then
  # The running build predates saved games: this one last time, wait for room 1 to empty.
  wait_until_empty
  log "room 1 is empty; swapping in the staged build (the running one couldn't save its game)"
  exec arena/deploy.sh --fresh
fi
exit $rc
