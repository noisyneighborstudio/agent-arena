#!/bin/zsh
# Continuous integration for the live arena. Whenever origin/main moves: test it, build it, and deploy it into room 1
# WITHOUT ending anyone's game (arena/deploy.sh pauses with a notice, saves, swaps, resumes, verifies, rolls back).
#
#   arena/ci.sh once     one pass: deploy origin/main if it isn't live yet
#   PEZZ_CI_FORCE_FRESH=1 arena/ci.sh once   the owner's call: when the running build can't resume, restart room 1 fresh
#                        now (60 s warning in the arena chat) instead of waiting for it to empty
#   arena/ci.sh loop     forever, a pass every PEZZ_CI_EVERY seconds (default 180); the com.sethwebster.pezz-ci
#                        LaunchAgent runs this
#
# It tests and builds in its own checkout (ci-work/worktree, detached at origin/main), so nobody's uncommitted edits
# ship. The gateway runs from a second checkout (ci-work/live) that only moves to a commit once it has deployed, so the
# live gateway is always the deployed commit. A commit that fails its tests or build is recorded in ci-work/failed and
# not retried until main moves again.
set -u
REPO=${0:A:h:h}
CI="$REPO/ci-work"; WT="$CI/worktree"; LIVE="$CI/live"  # build-and-test checkout; deployed checkout (the gateway runs here)
UNITY=/Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/MacOS/Unity
# A fresh room 1's settings (POST /api/admin/restart): ore_scale, open, map_size, match_hours (the match clock; default 4).
FRESH_JSON=${PEZZ_FRESH_JSON:-'{"ore_scale":0.3,"open":true}'}
mkdir -p "$CI"
log() { echo "$(date '+%F %T') $*"; }
status() { printf '{"at":"%s","state":"%s","commit":"%s","detail":"%s"}\n' "$(date '+%FT%T%z')" "$1" "${2:-}" "${3:-}" > "$CI/status.json"; }

setup() {
  if [ ! -e "$LIVE/.git" ]; then
    git -C "$REPO" worktree add -q --detach "$LIVE" "$(cat "$CI/deployed" 2>/dev/null || echo origin/main)" || return 1
    ln -s "$REPO/mcp/node_modules" "$LIVE/mcp/node_modules"
    mkdir -p "$LIVE/arena/logs" # the gateway logs here (git-ignored, so a fresh checkout lacks it)
    (cd "$LIVE/headless" && dotnet build -c Release >/dev/null 2>&1) # the overflow rooms' engine
  fi
  [ -e "$WT/.git" ] && return 0
  log "creating the CI checkout"
  git -C "$REPO" fetch -q origin
  git -C "$REPO" worktree add -q --detach "$WT" origin/main || return 1
  # Shared, untracked pieces: Unity's import cache (an APFS clone: instant), the build folder (so deploy.sh swaps the
  # live app), and the gateway's node modules.
  cp -c -R "$REPO/unity/Library" "$WT/unity/Library"
  ln -s "$REPO/unity/Build" "$WT/unity/Build"
  ln -s "$REPO/mcp/node_modules" "$WT/mcp/node_modules"
  mkdir -p "$WT/arena/logs"
}

# Outside players still at it. A seat that hasn't sent a command for PEZZ_CI_IDLE_S (lobby idle_s, builds that report it)
# is abandoned and doesn't count: an AFK seat must not hold a deploy forever.
outside_players() {
  curl -s -m 5 http://127.0.0.1:7777/api/lobby | IDLE="${PEZZ_CI_IDLE_S:-1800}" python3 -c 'import json,os,sys
try: t=json.load(sys.stdin)["teams"]
except Exception: print(-1); sys.exit()
idle=float(os.environ["IDLE"])
print(sum(1 for i,x in enumerate(t) if i>=2 and x["status"]=="playing" and not x.get("house") and x.get("idle_s", 0) < idle))'
}

pass() {
  setup || { status error "" "couldn't create the CI checkout"; return 2; }
  git -C "$REPO" fetch -q origin || return 2
  local target=$(git -C "$REPO" rev-parse origin/main)
  local deployed=$(cat "$CI/deployed" 2>/dev/null)
  [ "$target" = "$deployed" ] && { status live "$target" "up to date"; return 0; }
  [ "$target" = "$(cat "$CI/failed" 2>/dev/null)" ] && return 0
  log "main is at ${target[1,7]} (live: ${deployed[1,7]:-none}): testing"
  status testing "$target"
  # -f: Unity writes .meta files for new scripts into this checkout; once main carries them, a plain checkout refuses.
  git -C "$WT" checkout -q -f --detach "$target" || return 2

  # 1. Tests and syntax: never deploy a red build.
  if ! (cd "$WT/headless" && dotnet run -c Release -- --test) > "$CI/test.log" 2>&1 || ! grep -q "All tests passed" "$CI/test.log"; then
    log "tests failed at ${target[1,7]}: not deploying (see .ci/test.log)"; echo "$target" > "$CI/failed"; status failed "$target" "tests"; return 1
  fi
  for f in mcp/gateway.js mcp/play.js mcp/rooms.js mcp/changes.js mcp/render.js; do
    [ -f "$WT/$f" ] && ! node --check "$WT/$f" 2>>"$CI/test.log" && { log "syntax error in $f"; echo "$target" > "$CI/failed"; status failed "$target" "$f"; return 1; }
  done

  # 2. Unity: build only when the game changed.
  local changed_unity=1 changed_mcp=1
  if [ -n "$deployed" ]; then
    git -C "$REPO" diff --quiet "$deployed" "$target" -- unity/ && changed_unity=0
    git -C "$REPO" diff --quiet "$deployed" "$target" -- mcp/ && changed_mcp=0
  fi
  if [ $changed_unity = 1 ] && [ "$(cat "$CI/staged" 2>/dev/null)" = "$target" ] && [ -d "$REPO/unity/Build/next/Pezz.app" ]; then
    log "already built and staged; deploying"
  elif [ $changed_unity = 1 ]; then
    log "building the game"
    status building "$target"
    rm -rf "$REPO/unity/Build/tmpci"
    "$UNITY" -batchmode -projectPath "$WT/unity" -executeMethod Pez.EditorTools.PezSetup.Build \
      -pezOut "$REPO/unity/Build/tmpci/Pezz.app" -logFile "$CI/unity-build.log" -quit
    if ! grep -q "Pez build: Succeeded" "$CI/unity-build.log"; then
      log "Unity build failed at ${target[1,7]} (see .ci/unity-build.log)"; echo "$target" > "$CI/failed"; status failed "$target" "unity build"; return 1
    fi
    mkdir -p "$REPO/unity/Build/next"; rm -rf "$REPO/unity/Build/next/Pezz.app"
    mv "$REPO/unity/Build/tmpci/Pezz.app" "$REPO/unity/Build/next/Pezz.app" && rm -rf "$REPO/unity/Build/tmpci"
    echo "$target" > "$CI/staged"
  fi
  if [ $changed_unity = 1 ]; then

    # 3. Deploy: pause, save, swap, resume, verify (rollback on failure).
    status deploying "$target"
    (cd "$WT" && arena/deploy.sh); local rc=$?
    if [ $rc = 3 ]; then
      # The running build predates saved games: this one time a resume isn't possible. Wait until nobody outside
      # is playing, then start fresh; every deploy after this one keeps the game.
      # Room 1 never empties on its own (bots rejoin the moment they lose), so after PEZZ_CI_MAX_WAIT_S of waiting the
      # new game starts anyway; deploy.sh --fresh warns everyone first. PEZZ_CI_FORCE_FRESH=1 (the owner's call) does it now.
      [ "$(cat "$CI/waiting_for" 2>/dev/null)" = "$target" ] || { echo "$target" > "$CI/waiting_for"; date +%s > "$CI/waiting_since"; }
      local waited=$(( $(date +%s) - $(cat "$CI/waiting_since") )) max_wait=${PEZZ_CI_MAX_WAIT_S:-10800}
      if [ "${PEZZ_CI_FORCE_FRESH:-}" = 1 ] || [ "$(outside_players)" = "0" ] || [ $waited -ge $max_wait ]; then
        log "running build can't save; $([ "${PEZZ_CI_FORCE_FRESH:-}" = 1 ] && echo "the owner asked for a restart" || { [ $waited -ge $max_wait ] && echo "waited ${waited}s for room 1 to empty" || echo "room 1 has no active outside players"; }): starting fresh on the new build"
        (cd "$WT" && RESTART_JSON="$FRESH_JSON" arena/deploy.sh --fresh); rc=$?
      else
        log "running build can't save and room 1 has players: waiting for it to empty (${waited}s of ${max_wait}s)"
        status waiting "$target" "first resumable deploy waits for room 1 to empty, or starts fresh in $(( (max_wait - waited) / 60 )) min"; return 0
      fi
    fi
    if [ $rc != 0 ]; then
      log "deploy failed (exit $rc) at ${target[1,7]}"; [ $rc = 1 ] && echo "$target" > "$CI/failed"; status failed "$target" "deploy exit $rc"; return 1
    fi
  fi

  # 4. The live checkout moves to the deployed commit; the gateway restarts onto it if it changed (MCP sessions, rooms
  #    and commander links survive restarts), and the overflow rooms' engine is rebuilt there for new rooms.
  git -C "$LIVE" checkout -q -f --detach "$target"
  (cd "$LIVE/headless" && dotnet build -c Release >/dev/null 2>&1)
  if [ $changed_mcp = 1 ]; then
    (cd "$LIVE/mcp" && [ package.json -nt node_modules ] && npm install --silent >/dev/null 2>&1)
    launchctl kickstart -k "gui/$(id -u)/com.sethwebster.pezz-gateway" && log "gateway restarted on ${target[1,7]}"
  fi
  echo "$target" > "$CI/deployed"; rm -f "$CI/failed" "$CI/waiting_for" "$CI/waiting_since"
  log "live: ${target[1,7]} $(git -C "$REPO" log -1 --format=%s "$target")"
  # Room hosts on other machines (arena/roomhost.sh) get the same engine; their rooms save and resume onto it.
  # In the background and logged: a slow or unreachable host never holds up the arena.
  if [ -s "$HOME/.config/pezz/roomhosts.json" ]; then
    (nice "$LIVE/arena/roomhost.sh" roll-all >> "$REPO/arena/logs/roomhosts.log" 2>&1 &) ; log "rolling room hosts onto ${target[1,7]} (arena/logs/roomhosts.log)"
  fi
  status live "$target" "$(git -C "$REPO" log -1 --format=%s "$target" | tr '"' "'")"
}

# The kitchen sink (arena/kitchen-sink.sh, docs/art/KITCHEN_SINK.md): a private showcase room running a copy of the live
# build. After every pass it's started if it's down and restarted if the live build changed, so right after a deploy it
# comes back on the new build. Detached, niced and logged: it can't block, slow or fail a deploy.
kitchen_sink() {
  [ -x "$REPO/arena/kitchen-sink.sh" ] || return 0
  (nice -n 10 "$REPO/arena/kitchen-sink.sh" ensure >> "$REPO/arena/logs/kitchen-sink.log" 2>&1 &)
  return 0
}

case "${1:-once}" in
  once) pass; rc=$?; kitchen_sink; exit $rc ;;
  loop) while true; do pass; kitchen_sink; sleep "${PEZZ_CI_EVERY:-180}"; done ;;
  *) echo "usage: $0 once|loop"; exit 2 ;;
esac
