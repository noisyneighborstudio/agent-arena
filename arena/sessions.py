#!/usr/bin/env python3
"""Every Pezz session running on this machine, on one tailnet-only page, with a way into every viewer.

  python3 arena/sessions.py [--port 7426]      (arena/sessions.sh starts it and serves it on the tailnet)

It finds the sessions itself, each time the page asks:
  - room 1 (the live arena: game 7777, frames 7778, public gateway) and any extra gateway rooms (~/.config/pezz/rooms.json);
  - the test room (arena/test-room.sh), the kitchen sink, the preview copy, and test copies on 7967/7977/7987.
For each: status, the match score, every seat, and links to its viewers:
  - spectator: the gateway's /watch (whole room, delayed);
  - each player's own view: the gateway's /view/<room token> (the same page their commander link opens, without orders);
  - live 3D: the renderer's raw stream, proxied here (/frames/<port>/), with a live thumbnail.
Room 1's live 3D is view-only through this page: its camera is the one the public stream follows.

Per-player view links are private tokens; this page is for the tailnet only (never `tailscale funnel`).
"""
import argparse
import json
import os
import subprocess
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

HOME = os.path.expanduser("~")
PUBLIC = "https://pezz.sethwebster.com"


def host():
    try:
        out = subprocess.run(["tailscale", "status", "--json"], capture_output=True, text=True, timeout=5).stdout
        return json.loads(out)["Self"]["DNSName"].rstrip(".")
    except Exception:
        return "localhost"


def get(url, timeout=2.5):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.loads(r.read())


def read(path):
    try:
        return open(path).read().strip()
    except OSError:
        return ""


def known(ts):
    """The fixed sessions: (id, title, game API port, frames port, gateway base or None, room id, extra links, camera ok)."""
    s = [
        ("room1", "Room 1 · live arena", 7777, 7778, PUBLIC, 1, [("site", PUBLIC + "/")], False),  # gateway 7790
        ("test", "Test room", 7927, 7928, f"https://{ts}:8457", 1, [], True),
        ("sink", "Kitchen sink (asset library)", 7947, 7948, None, 0, [("asset library page", f"https://{ts}:8456/")], True),
        ("preview", "Preview copy", 7957, 7958, None, 0, [], True),
    ]
    for p in (7967, 7977, 7987):
        s.append((f"copy{p}", f"Test copy :{p}", p, p + 1, None, 0, [], True))
    # Extra gateway rooms (headless, no renderer) live behind the same public gateway.
    try:
        for r in json.load(open(os.path.join(HOME, ".config/pezz/rooms.json"))).get("rooms", []):
            s.append((f"room{r['id']}", f"Room {r['id']} · {r.get('code', '')}", r["port"], None, PUBLIC, r["id"], [], False))
    except Exception:
        pass
    return s


def session(entry):
    sid, title, api, frames, gw, room, links, cam = entry
    try:
        st = get(f"http://127.0.0.1:{api}/api/status")
    except Exception:
        return None
    seats = []
    for t in st.get("teams", []):
        view = None
        if gw and room:
            try:
                try:  # host-only: mints a view token for seats that never got one (newer builds)
                    vt = get(f"http://127.0.0.1:{api}/api/admin/viewlink?team={t['team']}").get("view_token")
                except Exception:
                    vt = get(f"http://127.0.0.1:{api}/api/viewlink?team={t['team']}").get("view_token")
                if vt:
                    view = f"{gw}/view/r{room}-{vt}"
            except Exception:
                pass
        # With a renderer, "watch" opens an observer camera seeing what this team sees (its own camera, so the player's
        # view never moves); without one, the gateway's tactical view of the seat.
        # The player's own page (HUD, alerts, their agent's feed, stats) on an observer camera of our own.
        gw_local = GATEWAYS.get(api)
        watch = (f"/pv/start?frames={frames}&api={api}&team={t['team']}&gw={gw_local}&room={room}" if frames and gw_local
                 else f"/observe?frames={frames}&api={api}&pov={t['team']}" if frames else view)
        seats.append({"name": t.get("name"), "player": t.get("player"), "controller": t.get("controller"),
                      "out": bool(t.get("defeated")), "structures": t.get("structures"), "units": t.get("units"),
                      "kills": t.get("kills"), "view": watch, "team": t["team"]})
    match = st.get("match") or {}
    scores = {s["team"]: s["score"] for s in match.get("scores", [])}
    for t, seat in zip(st.get("teams", []), seats):
        seat["score"] = scores.get(t["team"])
    label = read(os.path.join(HOME, "pezz-testroom/label")) if sid == "test" else ""
    commit = read(os.path.join(HOME, "pezz-testroom/build/commit"))[:7] if sid == "test" else ""
    viewers = []
    if gw and room:
        viewers.append(("spectator /watch", f"{gw}/watch" if room == 1 else f"{gw}/watch/{room}"))
    if frames:
        viewers.append(("live 3D (everything)", f"/observe?frames={frames}&api={api}&pov=-1"))
    viewers += links
    return {"id": sid, "title": title, "label": label, "commit": commit, "api": api, "frames": frames,
            "time_s": st.get("time_s"), "speed": st.get("speed"), "paused": st.get("paused"), "map": st.get("map_size"),
            "errors": st.get("sim_errors"), "game_over": st.get("game_over"), "phase": match.get("phase"),
            "ends_in_s": match.get("ends_in_s"), "seats": seats, "viewers": viewers}


def sessions(ts):
    return [s for s in (session(e) for e in known(ts)) if s]


PAGE = r"""<!doctype html><html><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>Pezz sessions</title><style>
:root{--bg:#121013;--card:#1c191d;--ink:#ece4d2;--dim:#9a9184;--acc:#e8a33d;--bad:#e0605a;--ok:#7fc27a}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.4 ui-sans-serif,system-ui,-apple-system}
header{display:flex;align-items:baseline;gap:12px;padding:14px 18px;border-bottom:1px solid #2a262b}
h1{font-size:18px;margin:0}header small{color:var(--dim)}
main{display:grid;grid-template-columns:repeat(auto-fill,minmax(380px,1fr));gap:14px;padding:14px 18px}
.card{background:var(--card);border:1px solid #2a262b;border-radius:10px;overflow:hidden}
.thumb{display:block;width:100%;aspect-ratio:16/9;object-fit:cover;background:#0b0c0e;cursor:pointer}
.nothumb{display:flex;align-items:center;justify-content:center;aspect-ratio:16/9;background:#0b0c0e;color:var(--dim)}
.body{padding:10px 12px}.t{display:flex;justify-content:space-between;gap:8px}.t b{font-size:15px}
.meta{color:var(--dim);font-size:12px;margin:2px 0 8px}.lbl{color:var(--acc);font-size:12px}
.links a{display:inline-block;margin:0 6px 6px 0;padding:4px 9px;border:1px solid #3a343b;border-radius:6px;color:var(--ink);text-decoration:none;font-size:12px}
.links a:hover{border-color:var(--acc)}
table{width:100%;border-collapse:collapse;font-size:12px}td{padding:3px 4px;border-top:1px solid #2a262b}
td.n{text-align:right;color:var(--dim)}.out{opacity:.45}.err{color:var(--bad)}.okc{color:var(--ok)}
#viewer{position:fixed;inset:0;background:rgba(0,0,0,.92);display:none;flex-direction:column}
#viewer.on{display:flex}body:has(#viewer.on){overflow:hidden;overscroll-behavior:none}#viewer .bar{display:flex;gap:10px;align-items:center;padding:8px 12px;color:var(--ink)}
#viewer iframe{flex:1;border:0;width:100%;min-height:0;background:#000;touch-action:none}#viewer button{background:#2a262b;color:var(--ink);border:0;border-radius:6px;padding:5px 10px;cursor:pointer}
</style></head><body>
<header><h1>Pezz sessions</h1><small id=upd>loading…</small><small style="margin-left:auto">tailnet only · per-player links are private</small></header>
<main id=grid></main>
<div id=viewer><div class=bar><b id=vt></b><a id=vo target=_blank style="color:var(--acc)">open in a new tab</a><span style="flex:1"></span><button onclick="closeV()">close (Esc)</button></div><iframe id=vf allow="fullscreen"></iframe></div>
<script>
const esc=s=>String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const hms=s=>s==null?'':`${Math.floor(s/3600)}:${String(Math.floor(s%3600/60)).padStart(2,'0')}:${String(Math.floor(s%60)).padStart(2,'0')}`;
function openV(title,url){vt.textContent=title;vo.href=url;vf.src=url;viewer.classList.add('on')}
function closeV(){viewer.classList.remove('on');vf.src='about:blank'}
addEventListener('keydown',e=>{if(e.key==='Escape')closeV()});
function card(s){
  const thumb=s.frames?`<img class=thumb data-src="/frames/${s.frames}/frame.jpg" onclick="openV(${esc(JSON.stringify(s.title+' · live 3D'))},'/observe?frames=${s.frames}&api=${s.api}&pov=-1')">`:`<div class=nothumb>no renderer (tactical views only)</div>`;
  const status=s.game_over?'<span class=err>game over</span>':s.paused?'paused':`<span class=okc>running</span> ×${s.speed}`;
  const links=s.viewers.map(([n,u])=>`<a href="${esc(u)}" onclick="openV(${esc(JSON.stringify(s.title+' · '+n))},'${esc(u)}');return false">${esc(n)}</a>`).join('');
  const rows=s.seats.map(t=>`<tr class="${t.out?'out':''}"><td><b>${esc(t.name)}</b> ${esc(t.player||t.controller||'')}${t.out?' · out':''}</td><td class=n>${t.score??''}</td><td class=n>${t.structures??''}s ${t.units??''}u ${t.kills??''}k</td><td>${t.view?`<a href="${esc(t.view)}" onclick="openV(${esc(JSON.stringify(s.title+' · '+t.name+' ('+(t.player||'')+')'))},'${esc(t.view)}');return false">watch</a>`:''}</td></tr>`).join('');
  return `<div class=card>${thumb}<div class=body><div class=t><b>${esc(s.title)}</b><span>${status}</span></div>
  ${s.label?`<div class=lbl>${esc(s.label)}${s.commit?' · '+esc(s.commit):''}</div>`:''}
  <div class=meta>game ${hms(s.time_s)} · map ${esc(s.map)} · ${s.phase?esc(s.phase)+(s.ends_in_s!=null?' · ends in '+hms(s.ends_in_s):''):''} · api :${s.api}${s.errors?` · <span class=err>${s.errors} sim errors</span>`:''}</div>
  <div class=links>${links}</div><table>${rows}</table></div></div>`;
}
async function load(){
  try{const r=await fetch('/api/sessions');const d=await r.json();
    grid.innerHTML=d.sessions.length?d.sessions.map(card).join(''):'<p style="color:var(--dim)">No Pezz sessions are running.</p>';
    upd.textContent=`${d.sessions.length} session(s) · updated ${new Date().toLocaleTimeString()}`;thumbs();
  }catch(e){upd.textContent='refresh failed: '+e}
}
function thumbs(){document.querySelectorAll('img.thumb').forEach(i=>i.src=i.dataset.src+'?'+Date.now())}
load();setInterval(load,10000);setInterval(thumbs,3000);
</script></body></html>"""


OBSERVE = r"""<!doctype html><html><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>Pezz observer</title><style>
html,body{margin:0;height:100%;background:#0b0c0e;color:#ece4d2;font:13px ui-sans-serif,system-ui,-apple-system;overflow:hidden}
#wrap{position:absolute;inset:0;display:flex;align-items:center;justify-content:center}
img{max-width:100vw;max-height:100vh;cursor:grab;user-select:none;-webkit-user-drag:none;touch-action:none}img:active{cursor:grabbing}
html,body{touch-action:none;overscroll-behavior:none}
#pad{position:fixed;right:10px;bottom:44px;display:flex;flex-direction:column;gap:6px}
#pad button{width:44px;height:44px;border-radius:10px;border:1px solid #3a343b;background:rgba(20,17,19,.85);color:#ece4d2;font-size:20px}
#bar{position:fixed;top:8px;left:8px;right:8px;display:flex;gap:8px;align-items:center;flex-wrap:wrap;pointer-events:none}
#bar>*{pointer-events:auto;background:rgba(20,17,19,.82);border:1px solid #3a343b;border-radius:8px;padding:5px 9px;color:#ece4d2}
select,button{font:inherit;cursor:pointer}button{background:#2a262b}
#help{position:fixed;bottom:8px;right:10px;color:#9a9184;background:rgba(20,17,19,.8);padding:6px 10px;border-radius:8px;font-size:12px}
#note{color:#e8a33d}
</style></head><body><div id=wrap><img id=f draggable=false></div>
<div id=bar><span>👁 observer · look only</span><label>sees as <select id=pov></select></label><span id=fol>click a unit to follow it</span><button id=unf style="display:none">stop following</button><span id=note></span></div>
<div id=pad><button data-z="0.8">+</button><button data-z="1.25">−</button><button data-y="15">⟲</button><button data-y="-15">⟳</button></div>
<div id=help>drag: pan · pinch or wheel: zoom · buttons or Q/E: rotate · tap a unit: follow · Esc: stop following</div>
<script>
const q=new URLSearchParams(location.search),FR=q.get('frames'),API=q.get('api');let slot=null,pov=+(q.get('pov')??-1);
const img=document.getElementById('f'),base=()=>`/frames/${FR}/team/${slot}`;
const cam=qs=>slot!=null&&fetch(`${base()}/cam?${qs}`);
async function lease(){const r=await fetch(`/api/observe?frames=${FR}`+(slot!=null?`&slot=${slot}`:''));const d=await r.json();
  if(!d.ok){note.textContent=d.error;return false}const first=slot==null;slot=d.slot;if(first){cam(`pov=${pov}`);img.src=`${base()}/stream`;watchdog()}return true}
setInterval(lease,10000);
// Teams to see as.
fetch('/api/sessions').then(r=>r.json()).then(d=>{const s=d.sessions.find(x=>String(x.frames)===FR);const o=[['-1','everything (no fog)']].concat((s?.seats||[]).map(t=>[String(t.team),`${t.name} (${t.player||t.controller||''})${t.out?' · out':''}`]));
  povSel.innerHTML=o.map(([v,n])=>`<option value="${v}">${n}</option>`).join('');povSel.value=String(pov);document.title=`${s?.title||'Pezz'} · observer`});
const povSel=document.getElementById('pov');povSel.onchange=()=>{pov=+povSel.value;cam(`pov=${pov}&follow=0`);fol.textContent='click a unit to follow it';unf.style.display='none'};
// Look around.
let moved=0,acc={dx:0,dy:0,zoom:1,yaw:0},sending=false;
function flush(){if(sending)return;const a=acc;if(!a.dx&&!a.dy&&a.zoom===1&&!a.yaw)return;acc={dx:0,dy:0,zoom:1,yaw:0};sending=true;
  Promise.resolve(cam(`dx=${a.dx.toFixed(4)}&dy=${a.dy.toFixed(4)}&zoom=${a.zoom.toFixed(3)}&yaw=${a.yaw.toFixed(1)}`)).finally(()=>{sending=false;setTimeout(flush,30)})}
// Pointer events: mouse, pen and touch alike. One finger drags, two pinch to zoom, a tap picks a unit to follow.
const pts=new Map();let pinch=0;
img.addEventListener('pointerdown',e=>{img.setPointerCapture(e.pointerId);pts.set(e.pointerId,{x:e.clientX,y:e.clientY});if(pts.size===1)moved=0;e.preventDefault()});
img.addEventListener('pointermove',e=>{const p=pts.get(e.pointerId);if(!p)return;const r=img.getBoundingClientRect();
  if(pts.size===1){moved+=Math.abs(e.clientX-p.x)+Math.abs(e.clientY-p.y);acc.dx-=(e.clientX-p.x)/r.width;acc.dy+=(e.clientY-p.y)/r.height}
  pts.set(e.pointerId,{x:e.clientX,y:e.clientY});
  if(pts.size===2){const[a,b]=[...pts.values()],d=Math.hypot(a.x-b.x,a.y-b.y);if(pinch)acc.zoom*=pinch/d;pinch=d;moved=99}
  flush()});
const up=e=>{if(pts.has(e.pointerId)&&pts.size===1&&moved<6)pick(e);pts.delete(e.pointerId);if(pts.size<2)pinch=0};
img.addEventListener('pointerup',up);img.addEventListener('pointercancel',e=>{pts.delete(e.pointerId);pinch=0});
document.querySelectorAll('#pad button').forEach(b=>b.onclick=()=>{if(b.dataset.z)acc.zoom*=+b.dataset.z;if(b.dataset.y)acc.yaw+=+b.dataset.y;flush()});
addEventListener('wheel',e=>{e.preventDefault();acc.zoom*=e.deltaY<0?0.9:1.11;flush()},{passive:false});
addEventListener('keydown',e=>{const k=e.key.toLowerCase(),s=.08;if(k==='a'||k==='arrowleft')acc.dx-=s;if(k==='d'||k==='arrowright')acc.dx+=s;if(k==='w'||k==='arrowup')acc.dy+=s;if(k==='s'||k==='arrowdown')acc.dy-=s;
  if(k==='q')acc.yaw+=15;if(k==='e')acc.yaw-=15;if(k==='escape')stopF();flush()});
// Follow: click a unit.
async function pick(e){const r=img.getBoundingClientRect();const u=(e.clientX-r.left)/r.width,v=(e.clientY-r.top)/r.height;if(u<0||u>1||v<0||v>1)return;
  const d=await (await fetch(`${base()}/pick?u=${u.toFixed(4)}&v=${v.toFixed(4)}`)).json();
  if(d.id&&!d.structure){cam(`follow=${d.id}`);fol.textContent=`following ${d.type} #${d.id} (team ${d.team})`;unf.style.display=''}}
function stopF(){cam('follow=0');fol.textContent='click a unit to follow it';unf.style.display='none'}
unf.onclick=stopF;
// Streams drop when a game restarts: reconnect. A build without observer cameras sends nothing: say so.
img.onerror=()=>setTimeout(()=>img.src=`${base()}/stream?${Date.now()}`,1000);
function watchdog(){setTimeout(()=>{if(!img.naturalWidth)note.textContent='no picture yet: this session\'s build may predate observer cameras (they arrive with the next deploy)'},6000)}
lease();
</script></body></html>"""

# Observer camera leases: (frames port, slot) -> expiry. A viewer renews every 10 s; a slot is free 30 s after its last renewal.
LEASES = {}
# Game API port -> its gateway's local port (where the player's own viewer page and its data live).
GATEWAYS = {7777: 7790, 7927: 7929}
FRAME_PORTS = {7778, 7928, 7948, 7958, 7968, 7978, 7988}
OBSERVER_SLOTS = range(10, 14)


def lease(port, want):
    import time
    now = time.time()
    for k in [k for k, t in LEASES.items() if t < now]:
        del LEASES[k]
    # A viewer renewing its camera keeps it, even across a restart of this page (it re-claims the slot it had).
    if want is not None and want in OBSERVER_SLOTS and LEASES.get((port, want), 0) <= now + 30:
        LEASES[(port, want)] = now + 30
        return want
    for slot in OBSERVER_SLOTS:
        if (port, slot) not in LEASES:
            LEASES[(port, slot)] = now + 30
            return slot
    return None


class Handler(BaseHTTPRequestHandler):
    ts = "localhost"

    def log_message(self, *a):
        pass

    def send(self, code, body, ctype="text/html; charset=utf-8"):
        b = body.encode() if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(b)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/":
            return self.send(200, PAGE)
        if path == "/api/sessions":
            return self.send(200, json.dumps({"sessions": sessions(Handler.ts)}), "application/json")
        if path == "/observe":
            return self.send(200, OBSERVE)
        if path == "/api/observe":
            from urllib.parse import parse_qs
            q = parse_qs(urlparse(self.path).query)
            try:
                port = int(q["frames"][0])
                want = int(q["slot"][0]) if "slot" in q else None
            except (KeyError, ValueError):
                return self.send(400, json.dumps({"ok": False, "error": "frames=<port> is required"}), "application/json")
            slot = lease(port, want)
            return self.send(200, json.dumps({"ok": slot is not None, "slot": slot, "error": None if slot is not None else "all 4 observer cameras on this session are in use: close another viewer"}), "application/json")
        if path.startswith("/frames/"):
            return self.frames(path)
        if path == "/pv/start":
            return self.pv_start()
        if path.startswith("/pv/"):
            return self.pv(path)
        self.send(404, "not found", "text/plain")

    def do_POST(self):
        self.send(403, "look, don't touch: the dashboard can't send orders", "text/plain")

    def relay(self, url):
        try:
            up = urllib.request.urlopen(url, timeout=10)
        except urllib.error.HTTPError as e:
            return self.send(e.code, e.read() or b"", e.headers.get("Content-Type", "text/plain"))
        except Exception as e:
            return self.send(502, f"not answering: {e}", "text/plain")
        self.send_response(up.status)
        for k in ("Content-Type", "Content-Length"):
            if up.headers.get(k):
                self.send_header(k, up.headers[k])
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        try:
            while True:  # MJPEG streams never end: relay chunk by chunk until the viewer closes
                chunk = up.read1(65536) if hasattr(up, "read1") else up.read(65536)
                if not chunk:
                    break
                self.wfile.write(chunk)
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass
        finally:
            up.close()

    def pv_start(self):
        """A player's own viewer page, as their human sees it, but on an observer camera: lease a camera, aim its
        point of view at that team, and open the page."""
        from urllib.parse import parse_qs
        q = parse_qs(urlparse(self.path).query)
        try:
            frames, api, team, gw, room = (int(q[k][0]) for k in ("frames", "api", "team", "gw", "room"))
        except (KeyError, ValueError):
            return self.send(400, "frames, api, team, gw and room are required", "text/plain")
        if frames not in FRAME_PORTS or GATEWAYS.get(api) != gw:
            return self.send(403, "not a Pezz session", "text/plain")
        slot = lease(frames, None)
        if slot is None:
            return self.send(503, "all 4 observer cameras on this session are in use: close another viewer", "text/plain")
        try:
            vt = get(f"http://127.0.0.1:{api}/api/admin/viewlink?team={team}")["view_token"]
            urllib.request.urlopen(f"http://127.0.0.1:{frames}/team/{slot}/cam?pov={team}&follow=0", timeout=3).read()
        except Exception as e:
            return self.send(502, f"couldn't open that seat's view: {e}", "text/plain")
        self.send_response(302)
        self.send_header("Location", f"/pv/{frames}/{slot}/{gw}/r{room}-{vt}/")
        self.end_headers()

    def pv(self, path):
        # /pv/<frames>/<slot>/<gateway>/<room token>/<rest>
        import re
        m = re.match(r"^/pv/(\d+)/(\d+)/(\d+)/(r\d+-[0-9a-f]+)/?(.*)$", path)
        if not m:
            return self.send(404, "not found", "text/plain")
        frames, slot, gw, tok, rest = int(m.group(1)), int(m.group(2)), int(m.group(3)), m.group(4), m.group(5)
        if frames not in FRAME_PORTS or slot not in OBSERVER_SLOTS or gw not in GATEWAYS.values():
            return self.send(403, "not a Pezz session", "text/plain")
        query = urlparse(self.path).query
        qs = f"?{query}" if query else ""
        base = f"/pv/{frames}/{slot}/{gw}/{tok}"
        if rest == "":
            # The gateway's own viewer page, pointed at us. No ?c= (commander) ever reaches it: no orders box.
            try:
                html = urllib.request.urlopen(f"http://127.0.0.1:{gw}/view/{tok}", timeout=5).read().decode()
            except Exception as e:
                return self.send(502, f"gateway not answering: {e}", "text/plain")
            html = html.replace(f'const BASE = "/view/{tok}"', f'const BASE = "{base}"')
            badge = (f'<div style="position:fixed;left:50%;transform:translateX(-50%);bottom:8px;z-index:99;background:rgba(20,17,19,.85);'
                     f'color:#e8a33d;border:1px solid #3a343b;border-radius:8px;padding:4px 10px;font:12px system-ui">👁 observer: '
                     f'everything this player sees, on your own camera · look only</div>'
                     f'<script>setInterval(()=>fetch("/api/observe?frames={frames}&slot={slot}"),10000)</script>')
            html = html.replace("</body>", badge + "</body>")
            return self.send(200, html)
        # Video and camera: our observer camera, never the player's own.
        if rest == "live.mjpg":
            return self.relay(f"http://127.0.0.1:{frames}/team/{slot}/stream")
        if rest == "live.jpg":
            return self.relay(f"http://127.0.0.1:{frames}/team/{slot}.jpg")
        if rest in ("cam", "pick"):
            return self.relay(f"http://127.0.0.1:{frames}/team/{slot}/{rest}{qs}")
        if rest == "orders":
            return self.send(403, "look, don't touch", "text/plain")
        # Everything else the page reads (its map, its HUD frame, the agent's feed, unit details, replays): read-only.
        return self.relay(f"http://127.0.0.1:{gw}/view/{tok}/{rest}{qs}")

    def frames(self, path):
        # /frames/<port>/<rest>: only the renderers' frame ports, and never room 1's camera control.
        parts = path.split("/", 3)
        try:
            port = int(parts[2])
        except (IndexError, ValueError):
            return self.send(404, "bad port", "text/plain")
        if port not in {7778, 7928, 7948, 7958, 7968, 7978, 7988}:
            return self.send(403, "not a Pezz frames port", "text/plain")
        rest = parts[3] if len(parts) > 3 else ""
        # Look, don't touch: this page only steers its own observer cameras (team/10-13). The host's view, the players'
        # own stream cameras (team/0-7) and the spectator camera (team/8) are never moved from here.
        import re
        if rest.startswith("cam") or "/cam" in rest:
            m = re.match(r"^team/(\d+)/cam$", rest)
            if not m or int(m.group(1)) not in OBSERVER_SLOTS:
                return self.send(204, b"")
        query = urlparse(self.path).query
        url = f"http://127.0.0.1:{port}/{rest}" + (f"?{query}" if query else "")
        try:
            up = urllib.request.urlopen(url, timeout=10)
        except Exception as e:
            return self.send(502, f"renderer not answering: {e}", "text/plain")
        self.send_response(up.status)
        for k in ("Content-Type", "Content-Length"):
            if up.headers.get(k):
                self.send_header(k, up.headers[k])
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        try:
            while True:  # MJPEG streams never end: relay chunk by chunk until the viewer closes
                chunk = up.read1(65536) if hasattr(up, "read1") else up.read(65536)
                if not chunk:
                    break
                self.wfile.write(chunk)
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass
        finally:
            up.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=7426)
    a = ap.parse_args()
    Handler.ts = host()
    srv = ThreadingHTTPServer(("127.0.0.1", a.port), Handler)
    srv.daemon_threads = True
    print(f"Pezz sessions on http://127.0.0.1:{a.port}/")
    srv.serve_forever()


if __name__ == "__main__":
    main()
