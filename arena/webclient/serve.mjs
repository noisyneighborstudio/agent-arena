#!/usr/bin/env node
// Static server for a Unity Web build, for the spike: node arena/webclient/serve.mjs [build dir] [--port 7431]
//   [--snapshot http://127.0.0.1:7991/api/view/snapshot | /tmp/pezweb/sample-snapshot.json]
// Serves Unity's precompressed files (.br / .gz) with the right Content-Encoding AND the underlying file's type
// (browsers refuse a .wasm.br served as octet-stream), and GET /snapshot from a URL (proxied) or a file. Bound to loopback.
import http from "node:http";
import fs from "node:fs";
import path from "node:path";

const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 && args[i + 1] ? args[i + 1] : def; };
const ROOT = path.resolve(args.find((a, i) => !a.startsWith("--") && !args[i - 1]?.startsWith("--")) ?? "/Users/sethwebster/Development/pez-web/build/web");
const PORT = Number(opt("--port", 7431));
const SNAPSHOT = opt("--snapshot", "");

const TYPES = { ".html": "text/html; charset=utf-8", ".js": "application/javascript", ".mjs": "application/javascript", ".json": "application/json", ".wasm": "application/wasm",
  ".data": "application/octet-stream", ".symbols": "application/octet-stream", ".css": "text/css", ".png": "image/png", ".jpg": "image/jpeg", ".ico": "image/x-icon", ".svg": "image/svg+xml", ".txt": "text/plain; charset=utf-8" };
const ENCODINGS = { ".br": "br", ".gz": "gzip" };

function sendFile(res, file) {
  let enc = ENCODINGS[path.extname(file)];
  const base = enc ? file.slice(0, -path.extname(file).length) : file; // the type comes from what's inside the compression
  const head = { "content-type": TYPES[path.extname(base)] ?? "application/octet-stream", "cache-control": "no-cache", // index.html must never go stale; the rest are rebuilt constantly in a spike
    "x-content-type-options": "nosniff", "cross-origin-opener-policy": "same-origin", "cross-origin-embedder-policy": "require-corp" }; // threads/SharedArrayBuffer builds need these
  if (enc) head["content-encoding"] = enc;
  const st = fs.statSync(file);
  head["content-length"] = st.size;
  res.writeHead(200, head);
  fs.createReadStream(file).pipe(res);
}

async function sendSnapshot(req, res) {
  const head = { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" };
  if (!SNAPSHOT) { res.writeHead(404, head); return res.end('{"ok":false,"error":"start with --snapshot <url|file>"}'); }
  try {
    if (/^https?:\/\//.test(SNAPSHOT)) {
      const r = await fetch(SNAPSHOT, { headers: { "accept-encoding": "gzip" }, signal: AbortSignal.timeout(10000) });
      const body = Buffer.from(await r.arrayBuffer()); // fetch has already gunzipped it
      res.writeHead(r.status, { ...head, "content-length": body.length });
      return res.end(body);
    }
    const body = fs.readFileSync(SNAPSHOT);
    res.writeHead(200, { ...head, "content-length": body.length });
    res.end(body);
  } catch (e) {
    res.writeHead(502, head);
    res.end(JSON.stringify({ ok: false, error: `snapshot source unavailable: ${e.message}` }));
  }
}

http.createServer(async (req, res) => {
  try {
    if (req.method !== "GET" && req.method !== "HEAD") { res.writeHead(405); return res.end(); }
    const p = decodeURIComponent(new URL(req.url, "http://x").pathname);
    if (p === "/snapshot") return await sendSnapshot(req, res);
    const file = path.join(ROOT, p === "/" ? "index.html" : p);
    if (file !== ROOT && !file.startsWith(ROOT + path.sep)) { res.writeHead(403); return res.end(); }
    let target = file;
    if (!fs.existsSync(target) || fs.statSync(target).isDirectory()) {
      // A build exported compressed may only have file.br / file.gz; serve whichever the browser accepts.
      const accept = req.headers["accept-encoding"] ?? "";
      target = [[".br", /\bbr\b/], [".gz", /\bgzip\b/]].map(([ext, re]) => re.test(accept) && file + ext).find((f) => f && fs.existsSync(f));
      if (!target) { res.writeHead(404, { "content-type": "text/plain" }); return res.end("not found"); }
    }
    sendFile(res, target);
  } catch (e) {
    res.writeHead(500, { "content-type": "text/plain" });
    res.end(String(e.message));
  }
}).listen(PORT, "127.0.0.1", () => console.log(`Unity web build ${ROOT} on http://127.0.0.1:${PORT}/${SNAPSHOT ? `  (/snapshot <- ${SNAPSHOT})` : ""}`));
