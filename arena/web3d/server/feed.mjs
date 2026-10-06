// Pezz view feed: one WebSocket per viewer, pushing what changed instead of the browser polling the whole frame.
//
//   node arena/web3d/server/feed.mjs [--port 7427]
//   ws://127.0.0.1:7427/feed?api=7777&team=0        (a local game)
//   ws://127.0.0.1:7427/feed?host=<name>&room=2&team=0 (a room on a room host)
//
// Each (game, team) view is read once per tick by this server however many viewers watch it (10 Hz), and every
// viewer gets: the map (again when it changes), a full snapshot on connect, then deltas: entities added, changed
// (whole row) and removed, new effects, and the fog and header only when they change. Messages are JSON with
// permessage-deflate, so the wire is small without a schema to keep in step.
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { WebSocketServer } from "ws";

const PORT = process.argv.includes("--port") ? Number(process.argv[process.argv.indexOf("--port") + 1]) : 7427;
const HZ = 10;
const LOCAL = new Set([7777, 7927, 7947, 7957, 7967, 7977, 7987]);
const hosts = () => { try { return Object.fromEntries(JSON.parse(fs.readFileSync(path.join(os.homedir(), ".config/pezz/roomhosts.json"), "utf8")).map((h) => [h.name, h.url])); } catch { return {}; } };

function baseFor(q) {
  if (q.get("host")) { const u = hosts()[q.get("host")]; const room = Number(q.get("room")); return u && Number.isInteger(room) ? `${u}/rooms/${room}` : null; }
  const api = Number(q.get("api")); return LOCAL.has(api) ? `http://127.0.0.1:${api}` : null;
}

const getJson = async (url) => { const r = await fetch(url, { signal: AbortSignal.timeout(4000) }); return r.json(); };

/** One view of one game, shared by everyone watching it. */
class Source {
  constructor(key, base, team) { Object.assign(this, { key, base, team, subs: new Set(), ents: new Map(), fxSeen: new Set(), map: null, mapKey: "", shroud: null, head: "", tick: -1, timer: null, stats: { msgs: 0, raw: 0 }, born: Date.now() }); }
  add(ws) { this.subs.add(ws); if (this.map) this.hello(ws); if (!this.timer) this.loop(); }
  drop(ws) { this.subs.delete(ws); if (!this.subs.size) { clearTimeout(this.timer); this.timer = null; sources.delete(this.key); } }
  send(ws, msg) { const s = JSON.stringify(msg); this.stats.msgs++; this.stats.raw += s.length; if (ws.readyState === 1) ws.send(s); }
  broadcast(msg) { for (const ws of this.subs) this.send(ws, msg); }
  hello(ws) {
    this.send(ws, { t: "map", map: this.map });
    this.send(ws, { t: "full", tick: this.tick, head: JSON.parse(this.head || "{}"), shroud: this.shroud, ents: [...this.ents.values()] });
  }
  async loop() {
    const t0 = Date.now();
    try {
      const f = await getJson(`${this.base}/api/view/frame?team=${this.team}`);
      if (f.ok === false) throw new Error(f.error);
      const mk = `${f.version}:${f.ore_version}`;
      if (mk !== this.mapKey) { this.map = await getJson(`${this.base}/api/view/map?team=${this.team}`); this.mapKey = mk; this.broadcast({ t: "map", map: this.map }); }
      if (f.tick !== this.tick) {
        const first = this.tick < 0;
        this.tick = f.tick;
        const now = new Map(f.entities.map((e) => [e[0], e])), upd = [], add = [], del = [];
        for (const [id, e] of now) { const old = this.ents.get(id); if (!old) add.push(e); else if (JSON.stringify(old) !== JSON.stringify(e)) upd.push(e); }
        for (const id of this.ents.keys()) if (!now.has(id)) del.push(id);
        this.ents = now;
        const fx = (f.effects ?? []).filter((e) => !this.fxSeen.has(e[0]));
        for (const e of fx) this.fxSeen.add(e[0]);
        if (this.fxSeen.size > 5000) this.fxSeen = new Set([...this.fxSeen].slice(-2000));
        const head = JSON.stringify({ time_s: f.time_s, you: f.you, teams: f.teams, match: f.match, chat: f.chat });
        const headChanged = head !== this.head; this.head = head;
        const shroudChanged = f.shroud !== this.shroud; this.shroud = f.shroud;
        if (first) for (const ws of this.subs) this.hello(ws);
        else this.broadcast({ t: "delta", tick: f.tick, time_s: f.time_s, add, upd, del, fx, ...(shroudChanged ? { shroud: f.shroud } : {}), ...(headChanged ? { head: JSON.parse(head) } : {}) });
      }
    } catch (e) { this.broadcast({ t: "error", error: String(e.message || e) }); }
    if (this.subs.size) this.timer = setTimeout(() => this.loop(), Math.max(10, 1000 / HZ - (Date.now() - t0)));
  }
}
const sources = new Map();

const server = http.createServer((req, res) => {
  if (req.url.startsWith("/stats")) { res.writeHead(200, { "content-type": "application/json" }); return res.end(JSON.stringify([...sources.values()].map((s) => ({ key: s.key.replace(/\/k\/[^/]+/, "/k/…"), viewers: s.subs.size, msgs: s.stats.msgs, raw_kb: Math.round(s.stats.raw / 1024), wire_kb: Math.round([...s.subs].reduce((n, ws) => n + (ws._socket?.bytesWritten ?? 0), 0) / 1024), up_s: Math.round((Date.now() - s.born) / 1000) })))); }
  res.writeHead(404); res.end();
});
const wss = new WebSocketServer({ server, path: "/feed", perMessageDeflate: { zlibDeflateOptions: { level: 6 }, threshold: 256 } });
wss.on("connection", (ws, req) => {
  const q = new URL(req.url, "http://x").searchParams, base = baseFor(q), team = Number(q.get("team") ?? 0);
  if (!base || !Number.isInteger(team)) { ws.close(1008, "not a Pezz view"); return; }
  const key = `${base}|${team}`;
  let src = sources.get(key); if (!src) { src = new Source(key, base, team); sources.set(key, src); }
  src.add(ws);
  ws.on("close", () => src.drop(ws));
});
server.listen(PORT, "127.0.0.1", () => console.log(`Pezz view feed on ws://127.0.0.1:${PORT}/feed`));
