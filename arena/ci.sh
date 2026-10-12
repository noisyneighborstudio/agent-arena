#!/bin/zsh
# Continuous integration for the live arena. Whenever origin/main moves: test it, build it (engine and browser player), check
# it in the CI room's browser UI check, then ship it to the CapRover app that serves pezz.sethwebster.com (deploy/caprover;
# its rooms save and resume across the swap, so nobody's game ends).
#
#   arena/ci.sh once     one pass: deploy origin/main if it isn't live yet
#   arena/ci.sh loop     forever, a pass every PEZZ_CI_EVERY seconds (default 180); the com.sethwebster.pezz-ci
#                        LaunchAgent runs this
#
# It tests and builds in its own checkout (ci-work/worktree, detached at origin/main), so nobody's uncommitted edits
# ship. A second checkout (ci-work/live) only moves to a commit once it has passed: CapRover deploys from it, and the CI
# room follows it. A commit that fails its tests or build is recorded in ci-work/failed and not retried until main moves
# again.
set -u
REPO=${0:A:h:h}
CI="$REPO/ci-work"; WT="$CI/worktree"; LIVE="$CI/live"  # build-and-test checkout; passed checkout (CapRover deploys from it)
WEB="$CI/web"  # the browser build's checkout: its own Unity import cache on the WebGL target, so neither build switches platforms
UNITY=/Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/MacOS/Unity
# Playwright for the CI room's UI check (arena/webclient/verify.mjs): a stable install outside any checkout.
export PEZZ_PLAYWRIGHT=${PEZZ_PLAYWRIGHT:-$HOME/.local/share/pezz/playwright}
mkdir -p "$CI"
log() { echo "$(date '+%F %T') $*"; }
status() { printf '{"at":"%s","state":"%s","commit":"%s","detail":"%s"}\n' "$(date '+%FT%T%z')" "$1" "${2:-}" "${3:-}" > "$CI/status.json"; }

setup() {
  if [ ! -e "$LIVE/.git" ]; then
    git -C "$REPO" worktree add -q --detach "$LIVE" "$(cat "$CI/deployed" 2>/dev/null || echo origin/main)" || return 1
    ln -s "$REPO/mcp/node_modules" "$LIVE/mcp/node_modules"
    mkdir -p "$LIVE/arena/logs" # a gateway writes its rooms' logs here (git-ignored, so a fresh checkout lacks it)
    (cd "$LIVE/headless" && dotnet build -c Release >/dev/null 2>&1) # the CI room's engine
  fi
  if [ ! -e "$WEB/.git" ]; then
    git -C "$REPO" worktree add -q --detach "$WEB" origin/main || return 1
    cp -c -R "$REPO/unity/Library" "$WEB/unity/Library"
  fi
  [ -e "$WT/.git" ] && return 0
  log "creating the CI checkout"
  git -C "$REPO" fetch -q origin
  git -C "$REPO" worktree add -q --detach "$WT" origin/main || return 1
  # Shared, untracked pieces: Unity's import cache (an APFS clone: instant) and the gateway's node modules.
  cp -c -R "$REPO/unity/Library" "$WT/unity/Library"
  ln -s "$REPO/mcp/node_modules" "$WT/mcp/node_modules"
  mkdir -p "$WT/arena/logs"
}

# The CI room (arena/ci-room.sh, docs/CI_ROOM.md): the candidate checkout's engine and gateway with the candidate's browser
# build, then an in-browser UI check. Its state is under ci-work/ci-room. `ensure` afterwards gives the owner's room back (on
# the deployed build if they have it open, else it frees the ports).
ci_room() { PEZZ_CIROOM_STATE="$CI/ci-room" "$WT/arena/ci-room.sh" "$@"; }
ci_room_gate() {  # <candidate web build>; 0 passed (or no gate in this commit), 1 failed, 2 couldn't run
  [ -x "$WT/arena/ci-room.sh" ] || return 0   # a candidate older than the CI room has no gate
  # Playwright for verify.mjs: `npm install` once in $REPO/arena/webclient (or keep /tmp/pezweb/pw); the checkout shares it like mcp/node_modules.
  [ -d "$REPO/arena/webclient/node_modules" ] && [ ! -e "$WT/arena/webclient/node_modules" ] && ln -s "$REPO/arena/webclient/node_modules" "$WT/arena/webclient/node_modules"
  ci_room gate --checkout "$WT" --build "$1" > "$CI/ci-room.log" 2>&1; local rc=$?
  ci_room ensure --checkout "$LIVE" --build "$LIVE/build/web" >> "$CI/ci-room.log" 2>&1
  return $rc
}

# After every pass the owner's room follows the deployed build (a no-op if it isn't open or is already on it). Detached and
# niced: it can't block, slow or fail a deploy. Replaces kitchen_sink() once the owner has moved to the CI room.
ci_room_follow() {
  [ -x "$LIVE/arena/ci-room.sh" ] || return 0
  (nice -n 10 env PEZZ_CIROOM_STATE="$CI/ci-room" "$LIVE/arena/ci-room.sh" ensure --checkout "$LIVE" --build "$LIVE/build/web" >> "$REPO/arena/logs/ci-room.log" 2>&1 &)
  return 0
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
    git -C "$REPO" diff --quiet "$deployed" "$target" -- unity/ headless/ && changed_unity=0
    git -C "$REPO" diff --quiet "$deployed" "$target" -- mcp/ && changed_mcp=0
  fi
  # 2b. The browser build (what /view and /watch serve): built whenever the game changed, or if the live one is missing.
  #     Installed in the live checkout in step 3 and shipped with the gateway, so the pages and the data they read move together.
  local web_next="$CI/web-next"
  if [ "$(cat "$CI/web-built" 2>/dev/null)" != "$target" ] && { [ $changed_unity = 1 ] || [ ! -f "$LIVE/build/web/index.html" ]; }; then
    log "building the browser player"
    status building "$target" "browser player"
    git -C "$WEB" checkout -q -f --detach "$target" || return 2
    rm -rf "$web_next"
    "$UNITY" -batchmode -buildTarget WebGL -projectPath "$WEB/unity" -executeMethod Pez.EditorTools.PezSetup.BuildWeb \
      -pezOut "$web_next" -logFile "$CI/web-build.log" -quit
    if ! grep -q "Pez build: Succeeded" "$CI/web-build.log" || [ ! -f "$web_next/index.html" ]; then
      log "browser build failed at ${target[1,7]} (see .ci/web-build.log)"; echo "$target" > "$CI/failed"; status failed "$target" "browser build"; return 1
    fi
    echo "$target" > "$CI/web-built"
  fi
  # 2c. The CI room: the candidate's engine, gateway and browser build in a private room, and an in-browser UI check. A red
  #     check blocks the deploy like a failed test; one that can't run blocks it too, without marking the commit, and retries.
  #     It runs when there is a new browser build, or the gateway changed (then on the deployed build), once per commit.
  local gate_web="$web_next"; [ -f "$gate_web/index.html" ] || gate_web="$LIVE/build/web"
  if [ -f "$gate_web/index.html" ] && { [ -f "$web_next/index.html" ] || [ $changed_mcp = 1 ]; } && [ "$(cat "$CI/ci-room-passed" 2>/dev/null)" != "$target" ]; then
    log "UI check in the CI room"
    status testing "$target" "UI check"
    ci_room_gate "$gate_web"; local gate=$?
    if [ $gate = 0 ]; then echo "$target" > "$CI/ci-room-passed"
    elif [ $gate = 1 ]; then
      log "UI check failed at ${target[1,7]} (see ci-work/ci-room/last/verify.log and the screenshots next to it)"; echo "$target" > "$CI/failed"; status failed "$target" "UI check"; return 1
    else
      log "the CI room couldn't run its check (exit $gate; see .ci/ci-room.log): not deploying, will retry"; status error "$target" "CI room check couldn't run"; return 2
    fi
  fi

  # 3. The live checkout moves to the passed commit (caprover() below deploys from it), and its engine is rebuilt there
  #    for the CI room.
  git -C "$LIVE" checkout -q -f --detach "$target"
  (cd "$LIVE/headless" && dotnet build -c Release >/dev/null 2>&1)
  # The browser build swaps in by renaming (the gateway reads its files per request: no restart needed for it).
  if [ -f "$web_next/index.html" ]; then
    mkdir -p "$LIVE/build"; rm -rf "$LIVE/build/web-old"
    [ -d "$LIVE/build/web" ] && mv "$LIVE/build/web" "$LIVE/build/web-old"
    mv "$web_next" "$LIVE/build/web" && log "browser player live (${target[1,7]})"
  fi
  echo "$target" > "$CI/deployed"; rm -f "$CI/failed"
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

# The CapRover server (deploy/caprover/deploy.mjs, docs/CAPROVER.md): the commit that's live here, once it has deployed here
# (so it passed its tests, build and the CI room's UI check). Detached and logged (arena/logs/caprover.log), one at a time;
# a commit that changes nothing the server runs is just recorded. A failed deploy (the old server keeps running) is
# retried after 30 min. PEZZ_CAPROVER_APP names the app (default pezz-staging; empty turns this off).
caprover() {
  local app=${PEZZ_CAPROVER_APP-pezz-staging} want=$(cat "$CI/deployed" 2>/dev/null) have=$(cat "$CI/caprover-deployed" 2>/dev/null)
  [ -n "$app" ] && [ -n "$want" ] && [ "$have" != "$want" ] && [ -f "$LIVE/deploy/caprover/deploy.mjs" ] || return 0
  [ "$(git -C "$LIVE" rev-parse HEAD)" = "$want" ] || return 0
  if [ -n "$have" ] && git -C "$REPO" diff --quiet "$have" "$want" -- mcp unity/Assets headless deploy/caprover arena/web3d 2>/dev/null; then
    echo "$want" > "$CI/caprover-deployed"; return 0
  fi
  local lock="$CI/caprover.lock"
  [ -d "$lock" ] && [ -n "$(find "$lock" -maxdepth 0 -mmin +40)" ] && rmdir "$lock" # a deploy that died with the machine
  [ "$(cat "$CI/caprover-failed" 2>/dev/null)" = "$want" ] && [ -z "$(find "$CI/caprover-failed" -mmin +30)" ] && return 0
  mkdir "$lock" 2>/dev/null || return 0
  log "deploying ${want[1,7]} to CapRover ($app; arena/logs/caprover.log)"
  ( cd "$LIVE" && if node deploy/caprover/deploy.mjs --app "$app" --web "$LIVE/build/web" >> "$REPO/arena/logs/caprover.log" 2>&1
      then echo "$want" > "$CI/caprover-deployed"; rm -f "$CI/caprover-failed"; log "CapRover ($app) live on ${want[1,7]}" >> "$REPO/arena/logs/caprover.log"
      else echo "$want" > "$CI/caprover-failed"; fi
    rmdir "$lock" ) &
  return 0
}

case "${1:-once}" in
  once) pass; rc=$?; kitchen_sink; ci_room_follow; caprover; exit $rc ;;
  loop) while true; do pass; kitchen_sink; ci_room_follow; caprover; sleep "${PEZZ_CI_EVERY:-180}"; done ;;
  *) echo "usage: $0 once|loop"; exit 2 ;;
esac
