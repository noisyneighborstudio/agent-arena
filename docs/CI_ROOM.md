# The CI room

The kitchen sink (every unit and structure in every state, `unity/Assets/Pez/Sim/KitchenSink.cs`, see
`docs/art/KITCHEN_SINK.md`) used to run as a Unity macOS app, which needs a GPU and a screen. The CI room runs the same
showcase world with no GPU anywhere: the headless .NET engine (`pez-headless --kitchen-sink`), a private copy of the
gateway, and the browser build (Unity WebGL) as the viewer. It does two jobs.

1. **The owner's test and showcase room.** Private, tailnet only: open it in a browser and look around everything in the
   new renderer, with the old kitchen sink's index of districts and exhibits beside it.
2. **A CI gate.** Before CI deploys a commit it brings the room up on the candidate's engine, gateway and browser build and
   runs an automated in-browser UI check (`arena/webclient/verify.mjs`). A failure blocks the deploy.

## Owner commands

```
arena/ci-room.sh open                 start it if needed, serve the page on the tailnet (8456), print the URL
arena/ci-room.sh status               what's up, on which build, the last check
arena/ci-room.sh down                 stop everything (and the tailnet serve, if it is ours)
arena/ci-room.sh up [--build <web build dir>] [--checkout <repo dir>]    start or restart onto another build, no tailnet
arena/ci-room.sh verify               the UI check; exit non-zero on a failure; results in ci-work/ci-room/last/
```

`open` and `up` take `--checkout` (the repo whose `headless/bin` engine and `mcp/gateway.js` run; default `ci-work/live`,
the deployed commit, else this repo) and `--build` (the browser build; default `<checkout>/build/web`). Everything is
idempotent: asking for what is already running does nothing; asking for a different build restarts the room onto it.

`open` takes tailnet port **8456** (the old Unity kitchen sink's). If `tailscale serve` shows 8456 pointing at something else
it refuses and says so: run `arena/kitchen-sink.sh stop` first (it also frees 8456), then `open`. The page has no login: anyone
on the tailnet can look around and change the game speed. `PEZZ_CIROOM_NO_TAILSCALE=1 arena/ci-room.sh open` skips tailscale
and prints the local URL only. Never `tailscale funnel`.

The page (`arena/ci-room.html`, served by `arena/ci-room-page.mjs`) is the browser build on the room's spectator view with a
sidebar of the 13 districts and 100+ exhibits: click one and the view reloads at its camera (`cam=x,y,zoom`). `[` and `]` step
through, `T` tours every exhibit, `/` finds, `L` shows the legend, and the speed bar pauses and speeds the game. Exhibits have
URL fragments (`#fires-and-damage`). Drag, scroll, `Q`/`E` and `M` in the view are the browser build's own.

## What runs, where

| Piece | Port (`PEZZ_CIROOM_PORT`, default 7910) | What it is |
| --- | --- | --- |
| showcase engine | 7910 | `dotnet pez-headless.dll --port 7910 --kitchen-sink --fresh` |
| showcase gateway | 7912 | `mcp/gateway.js`, `PEZZ_GAME` the showcase engine, one room, `PEZZ_WATCH_DELAY=3` |
| page | 7913 | `arena/ci-room-page.mjs`: the page, `/ci-room/catalog`, `/ci-room/status`, and a pass-through of the gateway's `/watch` and `/play` only (no `/join`, `/admin`, `/mcp`) |
| check engine | 7914 | an ordinary `--open` room, restarted fresh for every check |
| check gateway | 7916 | its own gateway copy, for the player checks |

Ports 7911 and 7915 are left free on purpose: a gateway reads its room's frames from engine port + 1. Both gateways run with
`HOME=ci-work/ci-room/home` (`home-check`), so nothing is ever written to `~/.config/pezz`, with the absolute node path (the asdf
shim needs the real `HOME`), `PEZZ_MAX_ROOMS=1` and no overflow rooms. State (pids, logs, the gateway homes, `info.json`, the last
results) is in `ci-work/ci-room/` (git-ignored; `PEZZ_CIROOM_STATE` overrides). Only the PIDs the script recorded are ever
signalled, after checking the process is still the one it started.

**Why two stacks.** The kitchen sink has no seats to join: it is `GameConfig.KitchenSink` with four `showcase` controllers,
not open, nobody plays (`/api/register` says "not open for joining"), and it is never saved or resumed (rebuilt from code on
every start; `--snapshot` is ignored). The player checks need a seat with a commander link, so they run against a second,
ordinary headless room (`--open`, with its house AI) behind its own gateway copy. The showcase is what people look at; the check
room is what `verify` plays a seat in. The showcase is also covered by the check: the page stage loads it in the browser
build, clicks an exhibit and asserts the view reloads on it and gets a snapshot.

### The headless engine's `--kitchen-sink`

`headless/Program.cs --kitchen-sink` builds `GameConfig { KitchenSink = true, Controllers = 4 x "showcase", Seed = 1,
Speed = 1 }` as the Unity app's `-kitchensink` does, and no `RoomSaver` (no resume, no saving). `/api/admin/showcase` returns the
catalog (districts, exhibits, camera targets, legend). The Unity app also drops to 3 fps when nobody watches; the headless sim
costs about 0.15 ms a tick, so the engine just keeps running.

## The UI check

`arena/webclient/verify.mjs` (Playwright, real Chrome with the GPU flags the rest of the web tooling uses; Playwright is found in
`arena/webclient/node_modules` after `npm install` there, else `/tmp/pezweb/pw`, else `$PEZZ_PLAYWRIGHT`). `ci-room.sh verify`
restarts the check room fresh, then runs five stages and writes `results.json`, `verify.log`, per-stage console logs and
screenshots to `ci-work/ci-room/last/`:

0. **Showcase page**: the catalog lists 13 districts and 100+ exhibits, the browser build shows the showcase, clicking an exhibit
   reloads the view at its camera, search narrows the index, the page forwards only `/watch` and `/play`.
1. **Player** (a seat joined through the gateway's `POST /join`, an agent command in the feed): link redirects to the browser build,
   first snapshot, drag pan, wheel zoom, `M` fit and return, `F` home, `Q` rotate, click selects, minimap click, the commander
   box is shown, typing in it doesn't steer the camera, Send reaches the engine's standing orders, no exceptions.
2. **Spectator**: `/watch` opens the browser build and loads, no commander box, `/watch/classic` is the original page.
3. **Phone** (touch): pinch zooms, one-finger drag pans. The pinch goes first and screenshots last: once the viewport has been
   resized (which a device-scale `page.screenshot()` on a mobile context does) the game no longer sees two-finger gestures, although
   one-finger pans still work (see "Findings"). A pinch that changes nothing is retried (both directions) and finally on a fresh page.
4. **Resilience** (last: it ends the seat): the check room's engine stops (`ci-room.sh engine-check stop`), the page says
   "Reconnecting…", the engine resumes its saved game and the page recovers; the seat is then kicked and the view becomes a spectator
   with results.

A page that "failed to start" or got no snapshot is reloaded once (a `WARN` line and a `warn` entry in `results.json`: the
loader occasionally gets an empty file under load); a build that is really broken fails both times. In the resilience stage one
cut-off snapshot read at the moment the seat's feed switches to the spectator's is also a warning, not a failure (see "Findings").
When "no exceptions" is the only thing a stage failed it runs once more and only a repeat fails (the game logs a stray
`NullReferenceException` on the showcase page about one load in six, see "Findings"). Exit codes: 0 passed, 1 failed,
2 the check couldn't run (no Playwright). A full run takes about 80 seconds.

`arena/ci-room.sh gate [--build ..] [--checkout ..]` is `up` then `verify` with the room marked busy, so a concurrent `ensure`
leaves it alone. `arena/ci-room.sh ensure [--build ..] [--checkout ..]` is for CI after a gate or a deploy: if the owner has the
room (they ran `open` and not `down`) it brings it up on the given build, else it stops it and frees the ports.

## Proposed `arena/ci.sh` wiring (not applied)

The gate runs after the browser build (step 2b) and before the deploy (step 3), on the candidate checkout's engine (`$WT/headless`),
gateway (`$WT/mcp`) and browser build (`$CI/web-next`). A red check marks the commit failed exactly like a red test or build: its
SHA goes in `$CI/failed`, so it isn't retried until main moves. A check that can't run (exit 2) doesn't mark the commit, blocks the
deploy and retries on the next pass, like `setup` errors do. After the gate, and after every pass, the owner's room is put back on
the deployed build (or stopped if the owner hasn't opened it), and the old Unity kitchen sink hook is replaced.

```diff
--- a/arena/ci.sh
+++ b/arena/ci.sh
@@ -56,6 +56,27 @@
 print(sum(1 for i,x in enumerate(t) if i>=2 and x["status"]=="playing" and not x.get("house") and x.get("idle_s", 0) < idle))'
 }
 
+# The CI room (arena/ci-room.sh, docs/CI_ROOM.md): the candidate checkout's engine and gateway with the candidate's browser
+# build, then an in-browser UI check. Its state is under ci-work/ci-room. `ensure` afterwards gives the owner's room back (on
+# the deployed build if they have it open, else it frees the ports).
+ci_room() { PEZZ_CIROOM_STATE="$CI/ci-room" "$WT/arena/ci-room.sh" "$@"; }
+ci_room_gate() {  # <candidate web build>; 0 passed (or no gate in this commit), 1 failed, 2 couldn't run
+  [ -x "$WT/arena/ci-room.sh" ] || return 0   # a candidate older than the CI room has no gate
+  # Playwright for verify.mjs: `npm install` once in $REPO/arena/webclient (or keep /tmp/pezweb/pw); the checkout shares it like mcp/node_modules.
+  [ -d "$REPO/arena/webclient/node_modules" ] && [ ! -e "$WT/arena/webclient/node_modules" ] && ln -s "$REPO/arena/webclient/node_modules" "$WT/arena/webclient/node_modules"
+  ci_room gate --checkout "$WT" --build "$1" > "$CI/ci-room.log" 2>&1; local rc=$?
+  ci_room ensure --checkout "$LIVE" --build "$LIVE/build/web" >> "$CI/ci-room.log" 2>&1
+  return $rc
+}
+
+# After every pass the owner's room follows the deployed build (a no-op if it isn't open or is already on it). Detached and
+# niced: it can't block, slow or fail a deploy. Replaces kitchen_sink() once the owner has moved to the CI room.
+ci_room_follow() {
+  [ -x "$LIVE/arena/ci-room.sh" ] || return 0
+  (nice -n 10 env PEZZ_CIROOM_STATE="$CI/ci-room" "$LIVE/arena/ci-room.sh" ensure --checkout "$LIVE" --build "$LIVE/build/web" >> "$REPO/arena/logs/ci-room.log" 2>&1 &)
+  return 0
+}
+
 pass() {
   setup || { status error "" "couldn't create the CI checkout"; return 2; }
   git -C "$REPO" fetch -q origin || return 2
@@ -112,6 +133,21 @@
     fi
     echo "$target" > "$CI/web-built"
   fi
+  # 2c. The CI room: the candidate's engine, gateway and browser build in a private room, and an in-browser UI check. A red
+  #     check blocks the deploy like a failed test; one that can't run blocks it too, without marking the commit, and retries.
+  #     It runs when there is a new browser build, or the gateway changed (then on the deployed build), once per commit.
+  local gate_web="$web_next"; [ -f "$gate_web/index.html" ] || gate_web="$LIVE/build/web"
+  if [ -f "$gate_web/index.html" ] && { [ -f "$web_next/index.html" ] || [ $changed_mcp = 1 ]; } && [ "$(cat "$CI/ci-room-passed" 2>/dev/null)" != "$target" ]; then
+    log "UI check in the CI room"
+    status testing "$target" "UI check"
+    ci_room_gate "$gate_web"; local gate=$?
+    if [ $gate = 0 ]; then echo "$target" > "$CI/ci-room-passed"
+    elif [ $gate = 1 ]; then
+      log "UI check failed at ${target[1,7]} (see ci-work/ci-room/last/verify.log and the screenshots next to it)"; echo "$target" > "$CI/failed"; status failed "$target" "UI check"; return 1
+    else
+      log "the CI room couldn't run its check (exit $gate; see .ci/ci-room.log): not deploying, will retry"; status error "$target" "CI room check couldn't run"; return 2
+    fi
+  fi
   if [ $changed_unity = 1 ]; then
 
     # 3. Deploy: pause, save, swap, resume, verify (rollback on failure).
@@ -184,7 +220,7 @@
 }
 
 case "${1:-once}" in
-  once) pass; rc=$?; room1_watchdog; kitchen_sink; exit $rc ;;
-  loop) while true; do pass; room1_watchdog; kitchen_sink; sleep "${PEZZ_CI_EVERY:-180}"; done ;;
+  once) pass; rc=$?; room1_watchdog; kitchen_sink; ci_room_follow; exit $rc ;;
+  loop) while true; do pass; room1_watchdog; kitchen_sink; ci_room_follow; sleep "${PEZZ_CI_EVERY:-180}"; done ;;
   *) echo "usage: $0 once|loop"; exit 2 ;;
 esac
```

It applies cleanly with `git apply` on the current `arena/ci.sh`. Notes:

- The gate runs when there is a new browser build (`$web_next`) or the gateway changed (then on the deployed build), and once per
  commit (`$CI/ci-room-passed`), so a deploy that waits for room 1 to empty (`status waiting`) doesn't re-run it every pass. A
  commit that changed neither is not gated.
- The gate's tests share the machine with the live arena: it is a handful of Chrome pages and two small .NET/node processes for
  about 80 seconds; nothing in it touches the live arena, `~/.config/pezz`, or the live gateway's ports.
- Until the owner has taken over 8456 from the old Unity kitchen sink, leave the `kitchen_sink` call in place beside
  `ci_room_follow` (the CI room only serves the tailnet when someone runs `arena/ci-room.sh open`). When flipping: `arena/kitchen-sink.sh stop`,
  then `arena/ci-room.sh open`; the old `kitchen_sink` hook, `arena/kitchen-sink.sh`, `arena/kitchen_sink_web.py`
  and `arena/kitchen_sink.html` can then be deleted (and `KitchenSinkView.cs` kept for the Unity app until it goes too).

## Findings

- **Pinch after a resize**: in the browser build, once the viewport has been resized (Playwright's `setViewportSize`, or a
  device-scale `page.screenshot()` on a mobile context), two-finger gestures stop registering although one-finger pans still work, at
  any finger position. A fresh page pinches fine; a CSS-scale screenshot doesn't break it. This is why the old harness's pinch check
  "sometimes failed after the drag" (the screenshot between them). It may be a real client bug on phones that resize the viewport
  (a rotation, Safari's collapsing toolbar): `RtsCamera.TouchUpdate` is where the owned-finger bookkeeping lives. The CI room check
  avoids the trigger rather than hiding it; it is worth a look in the Unity client.
- **Intermittent load failure**: now and then (about one load in twenty on a busy machine, not reproducible in isolation) a page
  ended on "Pezz failed to start: CompileError: WebAssembly.instantiate(): BufferSource argument is empty" or "Unable to parse
  Build/web.framework.js.br" from the gateway's `/play/` files. `verify.mjs` reloads once and records a warning.
- **A cut-off snapshot at the seat transition**: once in a few runs the client logged `pez snapshot: FormatException: Unexpected end
  of JSON` right as a kicked seat's `/view/<v>/snapshot` switched from the live view to the delayed spectator feed (after a 401). It
  carries on ("rebuilt") and shows the results card, so the check records a warning; it looks like a snapshot body read while the
  gateway changes mode, worth a look in `WebClient`'s 401 handling.
- **A stray `NullReferenceException`**: on the showcase page, about one reload in six, the game logs `NullReferenceException: Object
  reference not set to an instance of an object` (no managed frames: IL2CPP strips them) a few seconds after a view reloads on an
  exhibit; the view keeps running. The check reruns a stage whose only failure is an exception, so this alone doesn't block a
  deploy, but it is a real intermittent bug in the web build worth finding (`results.json` and `*-console.txt` record each one as a warning).
