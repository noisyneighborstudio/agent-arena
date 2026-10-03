#!/usr/bin/env python3
"""The kitchen sink's viewer page: the showcase room's live stream with an index of every district and exhibit.

Serves its own page (kitchen_sink.html, next to this file) and a handful of endpoints, and talks only to the kitchen
sink's own game (API :7947, frames :7948), never to the live arena:

  GET  /            the page                    GET  /stream, /frame.jpg   the MJPEG stream / a still (from :7948)
  GET  /cam?...     pan, zoom, rotate (:7948)   POST /goto {x, y, zoom}     jump the camera (admin camera, yaw 45)
  GET  /catalog     districts, exhibits, legend GET/POST /speed             game speed and pause
  GET  /status      tick, fps, build

Run by arena/kitchen-sink.sh on 127.0.0.1:7949 (exposed on the tailnet with tailscale serve, never funnel).
"""
import json
import os
import sys
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

GAME, FRAMES = "http://127.0.0.1:7947", "http://127.0.0.1:7948"
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 7949
HERE = os.path.dirname(os.path.abspath(__file__))
STATE = os.path.expanduser("~/pezz-kitchen-sink")


def call(path, body=None, timeout=3):
    req = urllib.request.Request(GAME + path, data=None if body is None else json.dumps(body).encode(), method="GET" if body is None else "POST")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def build_info():
    try:
        return open(os.path.join(STATE, "build.txt")).read().strip()
    except OSError:
        return ""


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, code, body, ctype="application/json"):
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def body(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}"
        return json.loads(raw)

    def speed(self):
        s = call("/api/status")
        return {"speed": s.get("speed"), "paused": s.get("paused")}

    def do_POST(self):
        try:
            if self.path == "/goto":
                b = self.body()
                cam = {"x": float(b["x"]), "y": float(b["y"]), "distance": float(b.get("zoom", 10)), "yaw": float(b.get("yaw", 45)), "menu": "close"}
                return self.send(200, call("/api/admin/camera", cam))
            if self.path == "/speed":
                b = self.body()
                out = {k: b[k] for k in ("speed", "paused") if k in b}
                call("/api/admin/speed", out)
                return self.send(200, self.speed())
        except Exception as e:
            return self.send(502, {"error": str(e)})
        self.send(404, {"error": "not found"})

    def do_GET(self):
        path = self.path.split("?")[0]
        if path in ("/", "/index.html"):
            with open(os.path.join(HERE, "kitchen_sink.html"), "rb") as f:
                return self.send(200, f.read(), "text/html; charset=utf-8")
        try:
            if path == "/catalog":
                return self.send(200, call("/api/admin/showcase"))
            if path == "/speed":
                return self.send(200, self.speed())
            if path == "/status":
                s = call("/api/status")
                return self.send(200, {k: s.get(k) for k in ("tick", "time_s", "speed", "paused", "render_fps", "sim_errors")} | {"build": build_info()})
        except Exception as e:
            return self.send(502, {"error": str(e)})
        if path not in ("/stream", "/frame.jpg", "/cam"):
            return self.send(404, {"error": "not found"})
        try:
            up = urllib.request.urlopen(FRAMES + self.path, timeout=10)
        except Exception:
            return self.send(502, b"the kitchen sink isn't running", "text/plain")
        # Stream everything through as it arrives (the MJPEG stream never ends).
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
