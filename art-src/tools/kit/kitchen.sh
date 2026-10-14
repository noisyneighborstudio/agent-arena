#!/bin/zsh
# Start a built Pezz app in the kitchen sink on your own port, wait until it answers, close the menu, print its PID.
#
#   art-src/tools/kit/kitchen.sh <Pezz.app> <port> <log dir>
#
# The app's frame server also takes port+1, so parallel workers use ports 10 apart. Stop it with `kill <pid>` (only
# yours: never pkill, and never touch pez-headless processes, which are live CI rooms).
set -e
APP=${1:A}; PORT=$2; LOGS=${3:A}
mkdir -p $LOGS
open -n $APP --args -kitchensink -autostart -port $PORT -screen-width 2400 -screen-height 1500 -screen-fullscreen 0 \
  -logFile $LOGS/player.log
for i in {1..90}; do curl -sf http://127.0.0.1:$PORT/api/status >/dev/null && break; sleep 1; done
curl -sf http://127.0.0.1:$PORT/api/status >/dev/null || { echo "no answer on $PORT; see $LOGS/player.log" >&2; exit 1; }
curl -s -X POST http://127.0.0.1:$PORT/api/admin/camera -d '{"menu":"close","edge_pan":false}' >/dev/null
pgrep -f "$APP/Contents/MacOS" | head -1
