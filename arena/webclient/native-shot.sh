#!/bin/bash
# Native macOS reference still of a snapshot, framed like the web build's cam=x,y,zoom.
#   arena/webclient/native-shot.sh <snapshot.json> <x,y,dist> <out.png> [WxH]
# Runs a COPY of the built app (/tmp/pezweb/native/Pezz.app, made with ditto from the live checkout's unity/Build, which is
# never touched) on port 7951 (API; frames/spectator on 7952), resumes a COPY of the snapshot, pauses the game, sets the
# camera through POST /api/admin/camera (LookAt(x,0,y) + Distance = the ortho half-height, same as the web's cam=), then
# POST-less GET /api/admin/screenshot (ScreenCapture, includes the HUD), and kills only the PID it started.
# -gatewayport 7955: this copy is an open arena, so it would otherwise "use the running gateway" on 7790 (the live one).
set -euo pipefail
SNAP=${1:?snapshot.json}; CAM=${2:?x,y,dist}; OUT=${3:?out.png}; SIZE=${4:-1728x1080}
W=${SIZE%x*}; H=${SIZE#*x}
PORT=${NATIVE_PORT:-7951}
DIR=/tmp/pezweb/native
APP=$DIR/Pezz.app
SRC=/Users/sethwebster/Development/pez/unity/Build/Pezz.app
[ -d "$APP" ] || ditto "$SRC" "$APP"
for p in $PORT $((PORT+1)) 7955; do
  if lsof -nP -iTCP:$p -sTCP:LISTEN >/dev/null 2>&1; then echo "port $p busy" >&2; exit 1; fi
done
mkdir -p "$DIR" "$(dirname "$OUT")"
RUN=$DIR/run-$(basename "$SNAP")
cp "$SNAP" "$RUN"
IFS=, read -r CX CY CD <<<"$CAM"
LOG=$DIR/player-$(basename "$OUT" .png).log
"$APP/Contents/MacOS/Pezz" -port $PORT -gatewayport 7955 -resume -snapshot "$RUN" -looktune /nonexistent \
  -screen-width "$W" -screen-height "$H" -screen-fullscreen 0 -logFile "$LOG" >/dev/null 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null || true' EXIT
for i in $(seq 1 120); do
  curl -sf "http://127.0.0.1:$PORT/api/status" >/dev/null 2>&1 && break
  kill -0 $PID 2>/dev/null || { echo "app exited early; see $LOG" >&2; exit 1; }
  sleep 0.5
done
A="http://127.0.0.1:$PORT/api/admin"
curl -sf -X POST "$A/speed" -d '{"speed":1,"paused":true}'; echo
sleep 1
curl -sf -X POST "$A/camera" -d "{\"x\":$CX,\"y\":$CY,\"distance\":$CD,\"edge_pan\":false}"; echo
sleep 4   # a few frames at the new pose (shadow fit, post-processing settle)
rm -f "$OUT"
curl -sf "$A/screenshot?path=$OUT"; echo
for i in $(seq 1 40); do [ -s "$OUT" ] && break; sleep 0.25; done
sleep 0.5
curl -sf "http://127.0.0.1:$PORT/api/status" | head -c 300; echo
sips -g pixelWidth -g pixelHeight "$OUT" | tail -2
grep -iE "resumed|error|exception" "$LOG" | head -5 || true
kill $PID 2>/dev/null || true; wait $PID 2>/dev/null || true
trap - EXIT
echo "wrote $OUT"
