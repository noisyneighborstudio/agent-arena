#!/usr/bin/env node
// The CI room's showcase page (arena/ci-room.sh runs it): the browser build on the kitchen sink room, plus the catalog index.
//   node arena/ci-room-page.mjs --port 7912 --gateway http://127.0.0.1:7911 --engine http://127.0.0.1:7910 [--info <info.json>]
// One origin for the tailnet: /ci-room is the page (arena/ci-room.html), /ci-room/catalog and /ci-room/status come from the
// showcase engine, and /watch and /play (the spectator view and the build's files) pass through to the room's private gateway.
// Nothing else is forwarded: no /join, no /admin, no /mcp. Bound to loopback (tailscale serve fronts it, never funnel).
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 && args[i + 1] ? args[i + 1] : def; };
const PORT = Number(opt("--port", 7912));
const GATEWAY = new URL(opt("--gateway", "http://127.0.0.1:7911"));
const ENGINE = opt("--engine", "http://127.0.0.1:7910").replace(/\/$/, "");
const INFO = opt("--info", "");
const PAGE = path.join(path.dirname(fileURLToPath(import.meta.url)), "ci-room.html");

const send = (res, code, body, type = "application/json") => {
  const buf = Buffer.isBuffer(body) ? body : Buffer.from(typeof body === "string" ? body : JSON.stringify(body));
  res.writeHead(code, { "content-type": type, "content-length": buf.length, "cache-control": "no-store" });
  res.end(buf);
};
const readBody = (req) => new Promise((resolve) => { const c = []; let n = 0; req.on("data", (d) => { n += d.length; if (n < 4096) c.push(d); }); req.on("end", () => resolve(Buffer.concat(c).toString())); });

async function engine(p, body) {
  const r = await fetch(ENGINE + p, { method: body === undefined ? "GET" : "POST", body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(4000) });
  return { status: r.status, json: await r.json() };
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, "http://x");
  const p = url.pathname;
  try {
    if (p === "/") { res.writeHead(302, { location: "/ci-room", "cache-control": "no-store" }); return res.end(); }
    if (p === "/ci-room") return send(res, 200, fs.readFileSync(PAGE), "text/html; charset=utf-8");
    if (p === "/ci-room/catalog") { const r = await engine("/api/admin/showcase"); return send(res, r.status, r.json); }
    if (p === "/ci-room/status") {
      const s = (await engine("/api/status")).json;
      let info = {}; try { info = JSON.parse(fs.readFileSync(INFO, "utf8")); } catch {}
      return send(res, 200, { tick: s.tick, time_s: s.time_s, speed: s.speed, paused: s.paused, sim_errors: s.sim_errors, build: info.build ?? "" });
    }
    if (p === "/ci-room/speed") {
      // Game speed and pause, the only thing the page may change (the old kitchen sink's speed bar).
      if (req.method === "POST") {
        const b = JSON.parse((await readBody(req)) || "{}");
        const out = {}; if (typeof b.speed === "number") out.speed = b.speed; if (typeof b.paused === "boolean") out.paused = b.paused;
        await engine("/api/admin/speed", out);
      }
      const s = (await engine("/api/status")).json;
      return send(res, 200, { speed: s.speed, paused: s.paused });
    }
    if (/^\/(watch|play)(\/|$)/.test(p) && (req.method === "GET" || req.method === "HEAD")) return proxy(req, res);
  } catch (e) { return send(res, 502, { error: String(e.message ?? e) }); }
  send(res, 404, { error: "not found" });
});

/** The gateway's answer, streamed straight back (the build's big precompressed files, the 302 to the right feed). */
function proxy(req, res) {
  const up = http.request({ host: GATEWAY.hostname, port: GATEWAY.port, path: req.url, method: req.method, headers: { ...req.headers, host: GATEWAY.host } }, (r) => {
    res.writeHead(r.statusCode, r.headers);
    r.pipe(res);
  });
  up.on("error", (e) => { if (!res.headersSent) send(res, 502, { error: "the room's gateway isn't answering: " + e.message }); else res.destroy(); });
  res.on("close", () => up.destroy());
  up.end();
}

server.listen(PORT, "127.0.0.1", () => console.log(`CI room page on http://127.0.0.1:${PORT}/ci-room (gateway ${GATEWAY.origin}, engine ${ENGINE})`));
process.on("SIGTERM", () => process.exit(0));
