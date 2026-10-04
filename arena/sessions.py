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
        ("room1", "Room 1 · live arena", 7777, 7778, PUBLIC, 1, [("site", PUBLIC + "/")], False),
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
        seats.append({"name": t.get("name"), "player": t.get("player"), "controller": t.get("controller"),
                      "out": bool(t.get("defeated")), "structures": t.get("structures"), "units": t.get("units"),
                      "kills": t.get("kills"), "view": view})
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
        viewers.append(("live 3D" + ("" if cam else " (view-only)"), f"/frames/{frames}/"))
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
#viewer.on{display:flex}#viewer .bar{display:flex;gap:10px;align-items:center;padding:8px 12px;color:var(--ink)}
#viewer iframe{flex:1;border:0;width:100%;background:#000}#viewer button{background:#2a262b;color:var(--ink);border:0;border-radius:6px;padding:5px 10px;cursor:pointer}
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
  const thumb=s.frames?`<img class=thumb data-src="/frames/${s.frames}/frame.jpg" onclick="openV(${esc(JSON.stringify(s.title+' · live 3D'))},'/frames/${s.frames}/')">`:`<div class=nothumb>no renderer (tactical views only)</div>`;
  const status=s.game_over?'<span class=err>game over</span>':s.paused?'paused':`<span class=okc>running</span> ×${s.speed}`;
  const links=s.viewers.map(([n,u])=>`<a href="${esc(u)}" onclick="openV(${esc(JSON.stringify(s.title+' · '+n))},'${esc(u)}');return false">${esc(n)}</a>`).join('');
  const rows=s.seats.map(t=>`<tr class="${t.out?'out':''}"><td><b>${esc(t.name)}</b> ${esc(t.player||t.controller||'')}${t.out?' · out':''}</td><td class=n>${t.score??''}</td><td class=n>${t.structures??''}s ${t.units??''}u ${t.kills??''}k</td><td>${t.view?`<a href="${esc(t.view)}" onclick="openV(${esc(JSON.stringify(s.title+' · '+t.name+' ('+(t.player||'')+')'))},'${esc(t.view)}');return false">view</a>`:''}</td></tr>`).join('');
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
        if path.startswith("/frames/"):
            return self.frames(path)
        self.send(404, "not found", "text/plain")

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
        if rest.startswith("cam") and port == 7778:
            return self.send(204, b"")  # room 1's camera is the public stream's: look, don't steer
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
