#!/bin/zsh
# Re-render a build icon (256x192) from the shipped model, with art-src/tools/render/render.html.
#
#   art-src/tools/kit/icon.sh <key> [structures|units] [out.png]
#
# Reads unity/Assets/Pez/Resources/PezModels/<key>.glb; writes unity/Assets/Pez/Resources/PezIcons/<key>.png unless
# out.png is given. Soldiers turn three-quarters to the camera (render.html; add &turn=<radians> via ICONQ to override).
# Needs `npm ci` in art-src/tools/kit once.
set -e
KIT=${0:A:h}; REPO=${KIT:h:h:h}
[[ -d $KIT/node_modules/three ]] || { echo "run: (cd $KIT && npm ci)" >&2; exit 1; }
KEY=$1; KIND=${2:-structures}; OUT=${3:-$REPO/unity/Assets/Pez/Resources/PezIcons/$KEY.png}; OUT=${OUT:A}
T=$(mktemp -d /tmp/pezkit.XXXXXX)
mkdir -p $T/render $T/pack/models/$KIND
cp $REPO/art-src/tools/render/render.html $T/render/; ln -s $KIT/node_modules $T/node_modules; ln -s $KIT/node_modules $T/render/node_modules
cp $REPO/unity/Assets/Pez/Resources/PezModels/$KEY.glb $T/pack/models/$KIND/
PORT=$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1])')
python3 -m http.server $PORT --bind 127.0.0.1 -d $T >/dev/null 2>&1 &
SRV=$!
trap "kill $SRV 2>/dev/null; rm -rf $T" EXIT
sleep 0.5
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --disable-gpu-sandbox --use-angle=swiftshader \
  --enable-unsafe-swiftshader --window-size=256,192 --virtual-time-budget=8000 --hide-scrollbars \
  --user-data-dir=$T/chrome --screenshot="$OUT" "http://127.0.0.1:$PORT/render/render.html?icon=$KEY&kind=$KIND${ICONQ:+&$ICONQ}" 2>/dev/null
echo $OUT
