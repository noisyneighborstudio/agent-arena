#!/usr/bin/env node
// Room host: runs Pezz rooms (the headless engine) on any machine, for a gateway somewhere else to place players in.
// One per machine. The gateway reaches it over the tailnet; the games themselves listen on this machine's loopback
// only, and every request to them goes through here.
//
//   PEZZ_HOST_NAME=mini PEZZ_HOST_REGION=us-east PEZZ_HOST_SECRET=<shared secret> PEZZ_ENGINE=/path/to/pez-headless \
//     node mcp/roomhost.js
//
// Env:
//   PEZZ_HOST_SECRET   required. Every path starts /k/<secret>/ (the gateway's room URLs carry it; nothing else does)
//   PEZZ_HOST_BIND     address to listen on (default: this machine's tailnet IPv4, from `tailscale ip -4`)
//   PEZZ_HOST_PORT     default 7700
//   PEZZ_HOST_NAME     default the hostname;  PEZZ_HOST_REGION  free text, e.g. us-east (default "unknown")
//   PEZZ_HOST_ROOMS    how many rooms this machine takes (default: half its cores, at least 1)
//   PEZZ_ENGINE        the engine: a self-contained pez-headless executable, or a .dll run with `dotnet`
//   PEZZ_HOST_BASE_PORT  first loopback port for rooms (default 7801; each room takes one)
//
// API (all under /k/<secret>):
//   GET    /host                       name, region, platform, capacity, load, rooms
//   POST   /rooms        {id, resume}  start a room (resume: from the snapshot this host holds for that id)
//   DELETE /rooms/<id>                 save and stop a room (its snapshot stays, for a later resume or a move)
//   GET    /rooms/<id>/snapshot        the room's saved game (saves first), for moving it to another host
//   PUT    /rooms/<id>/snapshot        receive a saved game; POST /rooms {id, resume:true} then picks it up
//   POST   /roll                       after a new engine is installed: save and resume each room on it, one at a time
//   *      /rooms/<id>/<path>          the room's own game API (status, register, state, command, wait, admin...)
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawn, execFileSync } from "node:child_process";
import { randomBytes, timingSafeEqual } from "node:crypto";

const SECRET = process.env.PEZZ_HOST_SECRET ?? "";
if (SECRET.length < 16) { console.error("PEZZ_HOST_SECRET (16+ characters) is required"); process.exit(2); }
const NAME = process.env.PEZZ_HOST_NAME || os.hostname().split(".")[0];
const REGION = process.env.PEZZ_HOST_REGION || "unknown";
const PORT = Number(process.env.PEZZ_HOST_PORT || 7700);
const CAPACITY = Number(process.env.PEZZ_HOST_ROOMS) > 0 ? Number(process.env.PEZZ_HOST_ROOMS) : Math.max(1, Math.floor(os.cpus().length / 2)); // 0 or unset: half the cores
const ENGINE = process.env.PEZZ_ENGINE;
const BASE_PORT = Number(process.env.PEZZ_HOST_BASE_PORT || 7801);
const HOME = path.join(os.homedir(), ".config", "pezz");
// Per host name, so two hosts on one machine (a test, a second region on a big box) never adopt each other's rooms.
const STATE = path.join(HOME, `roomhost-${NAME}.json`);
const LEGACY_STATE = path.join(HOME, "roomhost.json");
const LOGS = path.join(HOME, "roomhost-logs");
// pez-headless saves its game here, per port; a room's snapshot follows it from port to port (and host to host).
const snapshotFile = (port) => path.join(HOME, "rooms", `game-${port}.json`);
const heldSnapshot = (id) => path.join(HOME, "rooms", `held-${NAME}-${id}.json`);
if (!ENGINE || !fs.existsSync(ENGINE)) { console.error(`PEZZ_ENGINE must point at the engine (got ${ENGINE})`); process.exit(2); }

function bindAddress() {
  if (process.env.PEZZ_HOST_BIND) return process.env.PEZZ_HOST_BIND;
  try { return execFileSync("tailscale", ["ip", "-4"], { encoding: "utf8" }).trim().split("\n")[0]; } catch {}
  console.error("no tailnet address found: set PEZZ_HOST_BIND"); process.exit(2);
}

const log = (...a) => console.log(new Date().toISOString(), ...a);
const rooms = new Map(); // id -> { id, port, pid, startedAt }
const save = () => { fs.mkdirSync(HOME, { recursive: true, mode: 0o700 }); fs.writeFileSync(STATE, JSON.stringify([...rooms.values()]), { mode: 0o600 }); };

async function alive(port) {
  try { return (await fetch(`http://127.0.0.1:${port}/api/status`, { signal: AbortSignal.timeout(1500) })).ok; } catch { return false; }
}

async function launch(id, resume) {
  let port = BASE_PORT;
  const used = new Set([...rooms.values()].map((r) => r.port));
  while (used.has(port) || await alive(port)) port++;
  fs.mkdirSync(path.dirname(snapshotFile(port)), { recursive: true, mode: 0o700 });
  // A resume picks up the snapshot this host holds for the room, wherever it was saved (another port, another host).
  if (resume) {
    const held = fs.existsSync(heldSnapshot(id)) ? heldSnapshot(id) : null;
    if (!held) throw Object.assign(new Error(`no saved game for room ${id} on this host`), { status: 404 });
    fs.copyFileSync(held, snapshotFile(port));
  } else { fs.rmSync(snapshotFile(port), { force: true }); fs.rmSync(heldSnapshot(id), { force: true }); } // a new room: no old game under its id
  fs.mkdirSync(LOGS, { recursive: true });
  const out = fs.openSync(path.join(LOGS, `room-${id}.log`), "a");
  const seed = String(1 + (randomBytes(4).readUInt32BE() % 1e9));
  const args = ["--port", String(port), "--open", "--controllers", "ai", "--seed", seed, resume ? "--resume" : "--fresh"];
  const [cmd, argv] = ENGINE.endsWith(".dll") ? ["dotnet", [ENGINE, ...args]] : [ENGINE, args];
  const proc = spawn(cmd, argv, { detached: true, stdio: ["ignore", out, out] });
  proc.unref();
  for (let i = 0; i < 80 && !(await alive(port)); i++) await new Promise((r) => setTimeout(r, 250));
  if (!(await alive(port))) { try { process.kill(proc.pid); } catch {} throw new Error(`room ${id} didn't start (see ${LOGS}/room-${id}.log)`); }
  const room = { id, port, pid: proc.pid, startedAt: Date.now() };
  rooms.set(id, room); save();
  log(`room ${id} ${resume ? "resumed" : "started"} on 127.0.0.1:${port} (pid ${proc.pid})`);
  return room;
}

/** Save a room's game and keep it as this host's held snapshot for the room. */
async function hold(r) {
  try { await fetch(`http://127.0.0.1:${r.port}/api/admin/save`, { method: "POST", body: "{}", signal: AbortSignal.timeout(5000) }); } catch {}
  if (fs.existsSync(snapshotFile(r.port))) fs.copyFileSync(snapshotFile(r.port), heldSnapshot(r.id));
}

async function stop(r) {
  await hold(r);
  try { process.kill(r.pid); } catch {}
  for (let i = 0; i < 20 && await alive(r.port); i++) await new Promise((res) => setTimeout(res, 250));
  if (fs.existsSync(snapshotFile(r.port))) fs.copyFileSync(snapshotFile(r.port), heldSnapshot(r.id)); // it saves on the way out too
  rooms.delete(r.id); save();
  log(`room ${r.id} stopped (snapshot held)`);
}

function hostInfo() {
  const load = os.loadavg()[0] / os.cpus().length;
  return {
    ok: true, name: NAME, region: REGION, platform: `${os.platform()}-${os.arch()}`, capacity: CAPACITY,
    free: Math.max(0, CAPACITY - rooms.size), load_pct: Math.round(load * 100),
    rooms: [...rooms.values()].map((r) => ({ id: r.id, port: r.port, up_s: Math.round((Date.now() - r.startedAt) / 1000) })),
  };
}

const send = (res, code, body) => { res.writeHead(code, { "content-type": "application/json", "cache-control": "no-store" }); res.end(JSON.stringify(body)); };
const readBody = (req) => new Promise((resolve, reject) => { const c = []; let n = 0; req.on("data", (d) => { n += d.length; if (n > 64 << 20) { reject(new Error("too large")); req.destroy(); } else c.push(d); }); req.on("end", () => resolve(Buffer.concat(c))); req.on("error", reject); });

function secretOk(s) {
  const a = Buffer.from(String(s)), b = Buffer.from(SECRET);
  return a.length === b.length && timingSafeEqual(a, b);
}

/** Forward a request to a room's game on loopback, streaming both ways (long waits and live feeds included). */
function proxy(req, res, r, rest, query) {
  const up = http.request({ host: "127.0.0.1", port: r.port, method: req.method, path: `/${rest}${query}`,
    headers: { ...req.headers, host: `127.0.0.1:${r.port}` } }, (g) => { res.writeHead(g.statusCode, g.headers); g.pipe(res); });
  up.on("error", (e) => { if (!res.headersSent) send(res, 502, { ok: false, error: `room ${r.id} isn't answering: ${e.message}` }); else res.destroy(); });
  req.pipe(up);
  res.on("close", () => up.destroy());
}

const server = http.createServer(async (req, res) => {
  try {
    const u = new URL(req.url, "http://x");
    const m = /^\/k\/([^/]+)(\/.*)?$/.exec(u.pathname);
    if (!m || !secretOk(m[1])) return send(res, 404, { ok: false, error: "not found" });
    const p = m[2] || "/";
    if (p === "/host" && req.method === "GET") return send(res, 200, hostInfo());
    if (p === "/roll" && req.method === "POST") {
      // One room at a time, so each pauses only for its own save and resume (a couple of seconds).
      const rolled = [];
      for (const r of [...rooms.values()]) {
        try { await stop(r); await launch(r.id, true); rolled.push({ id: r.id, ok: true }); }
        catch (e) { rolled.push({ id: r.id, ok: false, error: e.message }); log(`room ${r.id} didn't come back after the roll: ${e.message}`); }
      }
      return send(res, 200, { ok: rolled.every((x) => x.ok), rolled });
    }
    if (p === "/rooms" && req.method === "POST") {
      const b = JSON.parse((await readBody(req)).toString() || "{}");
      const id = Number(b.id);
      if (!Number.isInteger(id) || id < 1) return send(res, 400, { ok: false, error: "id is required" });
      if (rooms.has(id)) return send(res, 200, { ok: true, id, port: rooms.get(id).port, already: true });
      if (rooms.size >= CAPACITY) return send(res, 503, { ok: false, error: `${NAME} is full (${CAPACITY} rooms)` });
      const r = await launch(id, !!b.resume);
      return send(res, 200, { ok: true, id, port: r.port });
    }
    const rm = /^\/rooms\/(\d+)(?:\/(.*))?$/.exec(p);
    if (!rm) return send(res, 404, { ok: false, error: "not found" });
    const id = Number(rm[1]), rest = rm[2] ?? "", r = rooms.get(id);
    if (rest === "snapshot" && req.method === "PUT") {
      fs.mkdirSync(path.dirname(heldSnapshot(id)), { recursive: true, mode: 0o700 });
      fs.writeFileSync(heldSnapshot(id), await readBody(req), { mode: 0o600 });
      return send(res, 200, { ok: true, held: id });
    }
    if (rest === "snapshot" && req.method === "GET") {
      if (r) await hold(r);
      if (!fs.existsSync(heldSnapshot(id))) return send(res, 404, { ok: false, error: `no saved game for room ${id}` });
      res.writeHead(200, { "content-type": "application/json" });
      return fs.createReadStream(heldSnapshot(id)).pipe(res);
    }
    if (!r) return send(res, 404, { ok: false, error: `room ${id} isn't running on ${NAME}` });
    if (rest === "" && req.method === "DELETE") { await stop(r); return send(res, 200, { ok: true, stopped: id }); }
    return proxy(req, res, r, rest, u.search);
  } catch (e) { if (!res.headersSent) send(res, e.status || 500, { ok: false, error: e.message }); }
});

// Rooms survive a restart of this daemon (they're detached); re-adopt the ones that still answer.
try { for (const r of JSON.parse(fs.readFileSync(fs.existsSync(STATE) ? STATE : LEGACY_STATE, "utf8"))) if (await alive(r.port)) rooms.set(r.id, r); } catch {}
if (!fs.existsSync(STATE) && fs.existsSync(LEGACY_STATE) && rooms.size) fs.rmSync(LEGACY_STATE, { force: true }); // migrated: one host owns them
save();
const bind = bindAddress();
server.listen(PORT, bind, () => log(`room host ${NAME} (${REGION}, ${os.platform()}-${os.arch()}, ${CAPACITY} rooms) on http://${bind}:${PORT}; ${rooms.size} room(s) adopted`));
