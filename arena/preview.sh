#!/bin/zsh
# Previews: watch work in progress before it ships. A private copy of the game (never the live arena) runs an
# AI-vs-AI match you can watch live, and every preview adds a set of stills to a gallery, so each step can be
# compared with the ones before it.
#
#   arena/preview.sh build <branch|commit> "<label>"   build that commit and launch it as the preview
#   arena/preview.sh launch <Pezz.app> "<label>"       launch an already-built app as the preview
#   arena/preview.sh shots ["<label>"]                 add stills from the running preview to the gallery
#   arena/preview.sh speed 1|2|4 / pause / resume      game speed of the running preview (the stream page can't click the HUD)
#   arena/preview.sh stop                              stop the preview (do this before measuring performance)
#   arena/preview.sh status
#
# Live view:  https://<host>.ts.net:8455/   (the game's live view with a 1x/2x/4x/pause bar: drag to pan, scroll to zoom)
# Gallery:    https://<host>.ts.net:8452/pezz-previews.html   (served from ~/dashboards)
# Both are tailnet-only (tailscale serve, never funnel) and have no login.
#
# Ports: game API 7957, frames 7958, the page 7959 (the live arena is 7777/7778; the render pass's test copies use 7977/7987).
set -u
SELF=${0:A}; REPO=${0:A:h:h}
UNITY=/Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/MacOS/Unity
HOME_DIR=$HOME/pezz-previews; SRC=$HOME_DIR/src
GALLERY=$HOME/dashboards/previews; PAGE=$HOME/dashboards/pezz-previews.html
PORT=7957; FRAMES=7958; WEB=7959; TSPORT=8455
API=http://127.0.0.1:$PORT
mkdir -p "$HOME_DIR" "$GALLERY"
log() { echo "$(date '+%T') $*"; }
slug() { echo "$1" | tr 'A-Z' 'a-z' | sed -E 's/[^a-z0-9]+/-/g; s/^-|-$//g' | cut -c1-40; }
host() { tailscale status --json 2>/dev/null | python3 -c 'import json,sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))' 2>/dev/null; }

stop() {
  local pid=$(cat "$HOME_DIR/pid" 2>/dev/null)
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid"; for i in {1..20}; do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
    kill -0 "$pid" 2>/dev/null && kill -9 "$pid"
    log "preview stopped (pid $pid)"
  fi
  rm -f "$HOME_DIR/pid"
}

serve() {
  # The page (arena/preview_web.py) is the game's live view plus a speed bar; it proxies the stream from :$FRAMES.
  if ! curl -s -m 2 -o /dev/null http://127.0.0.1:$WEB/speed; then
    (nohup python3 "$REPO/arena/preview_web.py" $WEB > "$HOME_DIR/web.log" 2>&1 &)
    sleep 1
  fi
  tailscale serve status 2>/dev/null | grep -A1 ":$TSPORT " | grep -q "127.0.0.1:$WEB" || tailscale serve --bg --https=$TSPORT http://127.0.0.1:$WEB >/dev/null
}

launch() {
  local app=$1 label=$2
  [ -d "$app" ] || { echo "no app at $app"; exit 1; }
  # Keep a copy, so the build behind a gallery entry can be relaunched later.
  local keep="$HOME_DIR/builds/$(slug "$label")/Pezz.app"
  if [ "${app:A}" != "${keep:A}" ]; then rm -rf "$keep"; mkdir -p "${keep:h}"; cp -c -R "$app" "$keep" 2>/dev/null || cp -R "$app" "$keep"; fi
  stop
  if lsof -iTCP:$PORT -sTCP:LISTEN >/dev/null 2>&1; then echo "port $PORT is busy (not our preview); not launching"; exit 1; fi
  # A fixed seed and map so every preview shows the same world; two house AIs (PREVIEW_SPEED=2 for double speed) so bases and fights
  # appear within a couple of minutes.
  open -n "$keep" --args -autostart -fresh -port $PORT -team0 ai -team1 ai -seed 4242 -mapsize 96 -speed "${PREVIEW_SPEED:-1}"
  local pid=""
  for i in {1..60}; do pid=$(pgrep -n -f "${keep}/Contents/MacOS"); [ -n "$pid" ] && curl -s -m 2 "$API/api/status" >/dev/null && break; sleep 1; done
  [ -n "$pid" ] || { echo "the preview didn't start"; exit 1; }
  echo "$pid" > "$HOME_DIR/pid"; echo "$label" > "$HOME_DIR/label"
  git -C "$REPO" rev-parse --short HEAD >/dev/null 2>&1
  serve
  log "preview running: $label (pid $pid). Live: https://$(host):$TSPORT/"
  # Stills once the match has bases and the first fights (about 3 game-minutes at 2x).
  ( sleep 150; "$SELF" shots "$label" >> "$HOME_DIR/shots.log" 2>&1 ) &!
}

shots() {
  local label=${1:-$(cat "$HOME_DIR/label" 2>/dev/null)}
  curl -s -m 3 "$API/api/status" >/dev/null || { echo "no preview running"; exit 1; }
  local dir="$GALLERY/$(date '+%Y%m%d-%H%M%S')-$(slug "$label")"; mkdir -p "$dir"
  curl -s -X POST "$API/api/admin/camera" -d '{"menu":"close","edge_pan":false}' >/dev/null
  # Camera poses from the game itself: both command centers, the middle of the map, and the busiest fight.
  curl -s -m 5 "$API/api/view/frame" > "$dir/frame.json"
  python3 - "$dir/frame.json" > "$dir/poses.txt" <<'EOF'
import json, sys
f = json.load(open(sys.argv[1]))
ents = f.get("entities", [])
hq = {e[2]: e for e in ents if e[1] == "command_center"}
xs = [e[3] for e in ents] or [48]; ys = [e[4] for e in ents] or [48]
cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
poses = [("overview", cx, cy, 40)]
for t in sorted(hq)[:2]:
    poses.append((f"base{t}-close", hq[t][3] + 1.5, hq[t][4] - 2.5, 11))
    poses.append((f"base{t}-wide", hq[t][3], hq[t][4] - 3, 22))
hits = [(e[2], e[3]) for e in f.get("effects", []) if e[1] in ("hit", "shot", "destroyed")]
if hits:
    hx = sum(h[0] for h in hits) / len(hits); hy = sum(h[1] for h in hits) / len(hits)
    poses.append(("battle", hx, hy, 16))
for name, x, y, d in poses: print(name, round(x, 1), round(y, 1), d)
EOF
  while read -r name x y d; do
    curl -s -X POST "$API/api/admin/camera" -d "{\"x\":$x,\"y\":$y,\"distance\":$d,\"yaw\":45}" >/dev/null
    sleep 1.2
    curl -s -X POST -d "{}" "$API/api/admin/screenshot?path=$dir/$name.png" >/dev/null
    sleep 1.5
  done < "$dir/poses.txt"
  # What a viewer's stream actually looks like (compressed MJPEG frame), for the same game.
  curl -s -m 3 "http://127.0.0.1:$FRAMES/team/0.jpg" >/dev/null; sleep 1.5
  curl -s -m 3 -o "$dir/stream-team0.jpg" "http://127.0.0.1:$FRAMES/team/0.jpg"
  [ -s "$dir/stream-team0.jpg" ] || rm -f "$dir/stream-team0.jpg"
  # Shrink the stills for the gallery (originals kept alongside).
  for p in "$dir"/*.png(N); do sips -Z 1400 -s format jpeg -s formatOptions 82 "$p" --out "${p%.png}.jpg" >/dev/null 2>&1 && rm -f "$p"; done
  rm -f "$dir/frame.json"
  printf '{"label":%s,"at":"%s","build":%s}\n' "$(python3 -c 'import json,sys;print(json.dumps(sys.argv[1]))' "$label")" "$(date '+%Y-%m-%d %H:%M')" \
    "$(python3 -c 'import json,sys;print(json.dumps(sys.argv[1]))' "$(cat "$HOME_DIR/builds/$(slug "$label")/commit" 2>/dev/null || echo "")")" > "$dir/meta.json"
  page
  log "stills added: $dir"
}

# The gallery page: newest preview first, each with its stills; links to the live preview.
page() {
  python3 - "$GALLERY" "$PAGE" "https://$(host):$TSPORT/" <<'EOF'
import json, os, sys, html
gal, out, live = sys.argv[1:4]
sets = []
for d in sorted(os.listdir(gal), reverse=True):
    p = os.path.join(gal, d)
    if not os.path.isdir(p): continue
    try: meta = json.load(open(os.path.join(p, "meta.json")))
    except Exception: continue
    order = ["overview", "base0-wide", "base0-close", "base1-wide", "base1-close", "battle", "stream-team0"]
    imgs = sorted([f for f in os.listdir(p) if f.endswith(".jpg")], key=lambda f: order.index(f[:-4]) if f[:-4] in order else 99)
    sets.append((d, meta, imgs))
cards = []
for d, meta, imgs in sets:
    figs = "".join(f'<figure><a href="previews/{d}/{i}"><img loading=lazy src="previews/{d}/{i}"></a><figcaption>{html.escape(i[:-4])}</figcaption></figure>' for i in imgs)
    build = f' · {html.escape(meta["build"])}' if meta.get("build") else ""
    cards.append(f'<section><h2>{html.escape(meta["label"])}</h2><p class=m>{html.escape(meta["at"])}{build}</p><div class=g>{figs}</div></section>')
open(out, "w").write(f"""<!doctype html><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<meta http-equiv=refresh content=60><title>Pezz previews</title>
<style>body{{margin:0;background:#141113;color:#ECE4D2;font:15px -apple-system,system-ui,sans-serif}}
header{{padding:18px 22px;border-bottom:1px solid #2a2527;display:flex;gap:18px;align-items:baseline;flex-wrap:wrap}}
h1{{margin:0;font-size:20px}} a{{color:#FFA22E}} section{{padding:16px 22px;border-bottom:1px solid #2a2527}}
h2{{margin:0 0 2px;font-size:17px}} .m{{margin:0 0 10px;color:#9c9488;font-size:13px}}
.g{{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:10px}}
figure{{margin:0}} img{{width:100%;border-radius:6px;display:block;background:#000}} figcaption{{color:#9c9488;font-size:12px;margin-top:3px}}</style>
<header><h1>Pezz previews</h1><a href="{live}">Watch the current preview live</a><span style="color:#9c9488">newest first · refreshes every minute</span></header>
{''.join(cards) or '<section>No previews yet.</section>'}""")
EOF
}

build() {
  local ref=$1 label=$2
  git -C "$REPO" fetch -q origin 2>/dev/null
  local commit=$(git -C "$REPO" rev-parse "$ref" 2>/dev/null || git -C "$REPO" rev-parse "origin/$ref")
  [ -n "$commit" ] || { echo "unknown ref $ref"; exit 1; }
  if [ ! -e "$SRC/.git" ]; then
    git -C "$REPO" worktree add -q --detach "$SRC" "$commit" || exit 1
    cp -c -R "$REPO/unity/Library" "$SRC/unity/Library"   # Unity's import cache (an APFS clone: instant)
  fi
  git -C "$SRC" checkout -q --detach "$commit" || exit 1
  local out="$HOME_DIR/builds/$(slug "$label")"
  rm -rf "$out"; mkdir -p "$out"
  log "building ${commit[1,7]} for '$label'"
  "$UNITY" -batchmode -projectPath "$SRC/unity" -executeMethod Pez.EditorTools.PezSetup.Build -pezOut "$out/Pezz.app" -logFile "$out/build.log" -quit
  grep -q "Pez build: Succeeded" "$out/build.log" || { echo "build failed: $out/build.log"; exit 1; }
  echo "$(git -C "$REPO" log -1 --format='%h %s' "$commit")" > "$out/commit"
  launch "$out/Pezz.app" "$label"
}

case "${1:-status}" in
  build) build "$2" "$3" ;;
  launch) launch "$2" "$3" ;;
  shots) shots "${2:-}" ;;
  stop) stop ;;
  speed) curl -s -X POST "$API/api/admin/speed" -d "{\"speed\":${2:-1}}"; echo ;;
  pause) curl -s -X POST "$API/api/admin/speed" -d '{"paused":true}'; echo ;;
  resume) curl -s -X POST "$API/api/admin/speed" -d '{"paused":false}'; echo ;;
  page) page ;;
  status) pid=$(cat "$HOME_DIR/pid" 2>/dev/null); if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then echo "running: $(cat "$HOME_DIR/label") (pid $pid)"; else echo "no preview running"; fi
          echo "live: https://$(host):$TSPORT/   gallery: https://$(host):8452/pezz-previews.html" ;;
  *) echo "usage: $0 build <ref> <label> | launch <app> <label> | shots [label] | stop | status"; exit 2 ;;
esac
