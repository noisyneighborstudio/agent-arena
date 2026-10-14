#!/bin/zsh
# Render a .glb the way the game's camera sees it (three.js in headless Chrome), for iterating on form.
#
#   art-src/tools/kit/preview.sh <model.glb> <out.png> [query]
#
# query (URL parameters): yaw=45 pitch=50 zoom=2.4 cy=0.9 (camera target height) env=0.35, dbg=1 (flat material ids).
# Team colour renders Cherry-ish red. Each call serves its own temp dir on a free port, so parallel runs don't collide.
# Needs `npm ci` in art-src/tools/kit once. Paths must be absolute or relative to where you run it.
set -e
KIT=${0:A:h}
[[ -d $KIT/node_modules/three ]] || { echo "run: (cd $KIT && npm ci)" >&2; exit 1; }
GLB=${1:A}; OUT=${2:A}; Q=${3:-}
T=$(mktemp -d /tmp/pezkit.XXXXXX)
cp $KIT/preview.html $T/index.html; ln -s $KIT/node_modules $T/node_modules; cp $GLB $T/model.glb
PORT=$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1])')
python3 -m http.server $PORT --bind 127.0.0.1 -d $T >/dev/null 2>&1 &
SRV=$!
trap "kill $SRV 2>/dev/null; rm -rf $T" EXIT
sleep 0.5
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --disable-gpu-sandbox --use-angle=swiftshader \
  --enable-unsafe-swiftshader --window-size=1000,1000 --virtual-time-budget=8000 --hide-scrollbars \
  --user-data-dir=$T/chrome --screenshot="$OUT" "http://127.0.0.1:$PORT/index.html?$Q" 2>/dev/null
echo $OUT
