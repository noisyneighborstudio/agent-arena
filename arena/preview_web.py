#!/usr/bin/env python3
"""The preview's web page: the game's own live view (frame server, :7958) with a speed bar added.

The stream page can't click the in-game HUD, so this serves that page with 1x / 2x / 4x / pause buttons that call
the preview game's admin API (:7957). Everything else (the MJPEG stream, camera moves) is proxied to the frame
server. Preview only: it never talks to the live arena's ports. Run by arena/preview.sh on 127.0.0.1:7959.
"""
import json
import sys
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

GAME, FRAMES = "http://127.0.0.1:7957", "http://127.0.0.1:7958"
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 7959

BAR = """<div id=sp style="position:fixed;top:10px;left:50%;transform:translateX(-50%);display:flex;gap:6px;
background:rgba(20,17,19,.88);padding:6px;border-radius:10px;font:600 13px -apple-system,sans-serif;z-index:9">
<button data-s=pause>❚❚</button><button data-s=1>1×</button><button data-s=2>2×</button><button data-s=4>4×</button>
<span id=st style="color:#9c9488;padding:6px 8px;font-weight:400"></span></div>
<style>#sp button{background:#2a2527;color:#ECE4D2;border:0;border-radius:7px;padding:7px 12px;font:inherit;cursor:pointer}
#sp button.on{background:#ECE4D2;color:#141113}</style>
<script>
async function speed(body){const r=await fetch('speed',{method:'POST',body:JSON.stringify(body)});show(await r.json())}
function show(s){document.querySelectorAll('#sp button').forEach(b=>b.classList.toggle('on',
  b.dataset.s==='pause'?s.paused:(!s.paused&&Number(b.dataset.s)===s.speed)));
  document.getElementById('st').textContent=s.label||''}
document.querySelectorAll('#sp button').forEach(b=>b.onclick=()=>speed(b.dataset.s==='pause'?{paused:true}:{speed:Number(b.dataset.s),paused:false}));
fetch('speed').then(r=>r.json()).then(show);setInterval(()=>fetch('speed').then(r=>r.json()).then(show),5000);
</script>"""


def label():
    try:
        return open(__import__("os").path.expanduser("~/pezz-previews/label")).read().strip()
    except OSError:
        return ""


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, code, body, ctype="application/json"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def status(self):
        with urllib.request.urlopen(GAME + "/api/status", timeout=3) as r:
            s = json.load(r)
        return {"speed": s.get("speed"), "paused": s.get("paused"), "label": label()}

    def do_POST(self):
        if self.path != "/speed":
            return self.send(404, b"{}")
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}"
        try:
            want = json.loads(body)
            out = {k: want[k] for k in ("speed", "paused") if k in want}
            req = urllib.request.Request(GAME + "/api/admin/speed", data=json.dumps(out).encode(), method="POST")
            urllib.request.urlopen(req, timeout=3).read()
            self.send(200, json.dumps(self.status()).encode())
        except Exception as e:
            self.send(502, json.dumps({"error": str(e)}).encode())

    def do_GET(self):
        if self.path == "/speed":
            try:
                return self.send(200, json.dumps(self.status()).encode())
            except Exception as e:
                return self.send(502, json.dumps({"error": str(e)}).encode())
        try:
            up = urllib.request.urlopen(FRAMES + self.path, timeout=10)
        except Exception:
            return self.send(502, b"preview not running", "text/plain")
        ctype = up.headers.get("Content-Type", "application/octet-stream")
        if self.path in ("/", "") and ctype.startswith("text/html"):
            page = up.read().decode("utf-8", "replace").replace("<body>", "<body>" + BAR, 1)
            return self.send(200, page.encode(), ctype)
        # Stream everything else through as it arrives (the MJPEG stream never ends).
        self.send_response(up.status)
        for k in ("Content-Type", "Content-Length", "Cache-Control"):
            if up.headers.get(k):
                self.send_header(k, up.headers[k])
        self.end_headers()
        try:
            while True:
                chunk = up.read1(65536) if hasattr(up, "read1") else up.read(65536)
                if not chunk:
                    break
                self.wfile.write(chunk)
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass
        finally:
            up.close()


ThreadingHTTPServer.daemon_threads = True
ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
