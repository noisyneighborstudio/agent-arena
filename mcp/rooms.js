// Rooms: each room is one game with up to 8 seats. Room 1 is the host's game (the Unity app, which also renders
// high-res live views). When every room is full, the next join opens a new room: on a room host (mcp/roomhost.js on
// any tailnet machine, preferring the player's region, then the least loaded), or failing that a headless engine on
// this machine. A room shuts down after it has had no outside players for a while. Each room has a short code: a
// player hands it to a friend, and the friend's agent joins the same game with it. Codes pick a room; they aren't
// secrets.
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { randomBytes } from "node:crypto";

const CODE_CHARS = "abcdefghjkmnpqrstuvwxyz23456789"; // no 0/o, 1/l/i
export const newCode = () => "pezz-" + [...randomBytes(6)].map((b) => CODE_CHARS[b % CODE_CHARS.length]).join("");

// Where a local headless room saves its game (pez-headless's default for its port), so a room that died resumes.
const snapshotFile = (port) => path.join(os.homedir(), ".config", "pezz", "rooms", `game-${port}.json`);

async function alive(game) {
  try { return (await fetch(`${game}/api/status`, { signal: AbortSignal.timeout(2500) })).ok; } catch { return false; }
}

async function call(url, opts = {}) {
  const r = await fetch(url, { ...opts, signal: AbortSignal.timeout(opts.timeout ?? 30000) });
  const j = await r.json().catch(() => ({ ok: false, error: `HTTP ${r.status}` }));
  if (!r.ok || j.ok === false) throw Object.assign(new Error(j.error || `HTTP ${r.status}`), { status: r.status });
  return j;
}

/** A player's region from Cloudflare's country header: the Americas, Europe/Africa, or Asia-Pacific. */
const EU = new Set("AD AL AT BA BE BG BY CH CY CZ DE DK EE ES FI FO FR GB GG GI GR HR HU IE IM IS IT JE LI LT LU LV MC MD ME MK MT NL NO PL PT RO RS RU SE SI SK SM UA VA XK".split(" "));
export function regionOf(country) {
  const c = String(country ?? "").toUpperCase();
  if (!/^[A-Z]{2}$/.test(c) || c === "XX" || c === "T1") return null;
  if (EU.has(c) || /^(DZ|EG|MA|NG|KE|ZA|TN|GH|ET|SN|CI|CM|TZ|UG|AO)$/.test(c)) return "eu";
  if (/^(US|CA|MX|BR|AR|CL|CO|PE|VE|EC|BO|PY|UY|CR|PA|GT|HN|SV|NI|CU|DO|PR|JM|TT|BS|BB)$/.test(c)) return "us";
  return "ap";
}
const sameRegion = (hostRegion, region) => !!region && String(hostRegion ?? "").toLowerCase().startsWith(region);

export class Rooms {
  /**
   * @param {object} o
   * @param {string} o.hostGame   room 1's game API (the Unity app)
   * @param {string} o.hostFrames room 1's frame server (per-player live renders)
   * @param {string} o.engine     the headless engine (dll) that local rooms run, when no room host has space
   * @param {string} o.stateFile  where room codes and running rooms are remembered across restarts
   * @param {string} o.hostsFile  room hosts: [{name, url, region}] (arena/roomhost.sh writes it)
   */
  constructor({ hostGame, hostFrames, engine, stateFile, hostsFile, logDir, maxRooms = 6, idleMinutes = 10, basePort = 7801, localRooms = true, log = console.log }) {
    Object.assign(this, { engine, stateFile, hostsFile, logDir, maxRooms, idleMs: idleMinutes * 60000, basePort, localRooms, log });
    this.rooms = new Map();
    this.hostInfo = new Map(); // name -> { at, info } (cached /host answers)
    let saved = {};
    try { saved = JSON.parse(fs.readFileSync(stateFile, "utf8")); } catch {}
    this.rooms.set(1, { id: 1, kind: "host", game: hostGame, frames: hostFrames, code: saved.hostCode || newCode(), emptySince: null, announced: saved.hostAnnounced ?? null, region: "us", host: "main" });
    // Rooms keep running across a gateway restart (they're detached, or on another machine): re-adopt the ones that
    // still answer. One that died (a crash, a machine restarting) is brought back from its saved game: same code,
    // seats and tokens.
    for (const r of saved.rooms ?? []) {
      if (r.kind === "remote") { const h = this.hosts().find((x) => x.name === r.host); if (h) this.rooms.set(r.id, { ...r, game: `${h.url}/rooms/${r.id}`, frames: null, emptySince: null, adopting: true }); }
      else this.rooms.set(r.id, { ...r, kind: "headless", game: `http://127.0.0.1:${r.port}`, frames: null, emptySince: null, adopting: true });
    }
    this.save();
    this.ready = Promise.all([...this.rooms.values()].filter((r) => r.adopting).map(async (r) => {
      delete r.adopting;
      if (await alive(r.game)) return;
      if (await this.relaunch(r)) return;
      this.rooms.delete(r.id);
    })).then(() => this.save());
    setInterval(() => this.reap().catch(() => {}), 60000).unref();
  }

  all() { return [...this.rooms.values()].sort((a, b) => a.id - b.id); }
  get(id) { return this.rooms.get(Number(id)); }
  byCode(code) { const c = String(code ?? "").trim().toLowerCase(); return this.all().find((r) => r.code === c); }

  /** The room hosts this gateway may use (re-read each time, so adding one needs no restart). */
  hosts() {
    if (!this.hostsFile) return [];
    try { return JSON.parse(fs.readFileSync(this.hostsFile, "utf8")); } catch { return []; }
  }

  /** A host's own report (capacity, free slots, load), cached for 10 s; null when it doesn't answer. */
  async hostStatus(h) {
    const c = this.hostInfo.get(h.name);
    if (c && Date.now() - c.at < 10000) return c.info;
    let info = null;
    try { info = await call(`${h.url}/host`, { timeout: 3000 }); } catch {}
    this.hostInfo.set(h.name, { at: Date.now(), info });
    return info;
  }

  /** Every host with what it reports, for the lobby and dashboards (no secrets). */
  async fleet() {
    const out = [{ name: "main", region: "us", rooms: 1, kind: "renderer (room 1)" }];
    for (const h of this.hosts()) {
      const s = await this.hostStatus(h);
      out.push(s ? { name: h.name, region: h.region, platform: s.platform, rooms: s.rooms.length, capacity: s.capacity, load_pct: s.load_pct, status: "up" } : { name: h.name, region: h.region, status: "down" });
    }
    return out;
  }

  save() {
    const data = { hostCode: this.get(1).code, hostAnnounced: this.get(1).announced ?? null,
      rooms: this.all().filter((r) => r.kind !== "host").map(({ id, code, kind, host, region, port, pid, createdAt, announced }) => ({ id, code, kind, host, region, port, pid, createdAt, announced })) };
    try {
      fs.mkdirSync(path.dirname(this.stateFile), { recursive: true, mode: 0o700 });
      fs.writeFileSync(this.stateFile, JSON.stringify(data), { mode: 0o600 });
    } catch (e) { this.log("couldn't save rooms:", e.message); }
  }

  /** Start a local room's headless engine on its port (resume: from its saved game) and wait until it answers. */
  async launch(id, port, resume) {
    fs.mkdirSync(this.logDir, { recursive: true });
    const out = fs.openSync(path.join(this.logDir, `room-${id}.log`), "a");
    const seed = String(1 + (randomBytes(4).readUInt32BE() % 1e9));
    const proc = spawn("dotnet", [this.engine, "--port", String(port), "--open", "--controllers", "ai", "--seed", seed, resume ? "--resume" : "--fresh"], { detached: true, stdio: ["ignore", out, out] });
    proc.unref();
    const game = `http://127.0.0.1:${port}`;
    for (let i = 0; i < 60 && !(await alive(game)); i++) await new Promise((r) => setTimeout(r, 250));
    if (!(await alive(game))) { try { process.kill(proc.pid); } catch {} return null; }
    return proc.pid;
  }

  /** Bring a room that died back from its saved game (on its host, or locally). */
  async relaunch(r) {
    if (r.kind === "remote") {
      const h = this.hosts().find((x) => x.name === r.host);
      if (!h) return false;
      try { await call(`${h.url}/rooms`, { method: "POST", body: JSON.stringify({ id: r.id, resume: true }), timeout: 60000 }); }
      catch (e) { this.log(`room ${r.id} couldn't be resumed on ${r.host}: ${e.message}`); return false; }
      r.emptySince = null;
      this.log(`room ${r.id} resumed from its saved game on ${r.host}`);
      return true;
    }
    if (!fs.existsSync(snapshotFile(r.port))) return false;
    const pid = await this.launch(r.id, r.port, true);
    if (!pid) { this.log(`room ${r.id} couldn't be resumed`); return false; }
    r.pid = pid; r.emptySince = null;
    this.log(`room ${r.id} resumed from its saved game on port ${r.port} (pid ${pid}, code ${r.code})`);
    return true;
  }

  /** Where a new room goes: the player's region first, then the least loaded host with space; local as a fallback. */
  async pickHost(region) {
    const up = [];
    for (const h of this.hosts()) { const s = await this.hostStatus(h); if (s && s.free > 0) up.push({ h, s }); }
    up.sort((a, b) => (sameRegion(b.h.region, region) - sameRegion(a.h.region, region)) || (a.s.load_pct - b.s.load_pct));
    return up[0]?.h ?? null;
  }

  /** Open a new room and wait until its game answers. */
  async spawn(region = null) {
    if (this.rooms.size >= this.maxRooms) { const e = new Error(`every room is full (${this.rooms.size} rooms of 8); try again later`); e.status = 503; throw e; }
    let id = 2; while (this.rooms.has(id)) id++;
    const host = await this.pickHost(region);
    let room;
    if (host) {
      await call(`${host.url}/rooms`, { method: "POST", body: JSON.stringify({ id }), timeout: 60000 });
      this.hostInfo.delete(host.name);
      room = { id, kind: "remote", host: host.name, region: host.region, game: `${host.url}/rooms/${id}`, frames: null, code: newCode(), createdAt: Date.now(), emptySince: Date.now() };
      this.log(`room ${id} started on ${host.name} (${host.region}, code ${room.code})`);
    } else {
      if (!this.localRooms) { const e = new Error("no room host has space right now; try again later"); e.status = 503; throw e; }
      let port = this.basePort + (id - 2) * 2;
      while (await alive(`http://127.0.0.1:${port}`)) port += 2;
      const pid = await this.launch(id, port, false); // --fresh: never pick up an old room's game on this port
      if (!pid) throw new Error("couldn't start a new room");
      room = { id, kind: "headless", host: "main", region: "us", game: `http://127.0.0.1:${port}`, frames: null, port, pid, code: newCode(), createdAt: Date.now(), emptySince: Date.now() };
      this.log(`room ${id} started on port ${port} (pid ${pid}, code ${room.code})`);
    }
    this.rooms.set(id, room);
    this.save();
    return room;
  }

  /**
   * Move a room to another host: save it where it is, stop it, carry its saved game over, and resume it there. Seats,
   * tokens and the code stay the same; players see a few seconds of "not running", then carry on.
   */
  async move(id, toName) {
    const r = this.get(id);
    if (!r || r.kind === "host") throw Object.assign(new Error("room 1 is the renderer's own game: it can't move"), { status: 400 });
    const to = this.hosts().find((h) => h.name === toName);
    if (!to) throw Object.assign(new Error(`no room host named ${toName}`), { status: 404 });
    if (r.kind === "remote" && r.host === to.name) return { ok: true, note: "already there" };
    const ts = await this.hostStatus(to);
    if (!ts || ts.free < 1) throw Object.assign(new Error(`${to.name} has no free slot`), { status: 503 });
    let snap;
    if (r.kind === "remote") {
      const from = this.hosts().find((h) => h.name === r.host);
      const got = await fetch(`${from.url}/rooms/${r.id}/snapshot`, { signal: AbortSignal.timeout(30000) }); // saves first
      if (!got.ok) throw new Error(`couldn't fetch room ${r.id}'s saved game from ${r.host}`);
      snap = Buffer.from(await got.arrayBuffer());
      await call(`${from.url}/rooms/${r.id}`, { method: "DELETE" });
    } else {
      try { await fetch(`${r.game}/api/admin/save`, { method: "POST", body: "{}", signal: AbortSignal.timeout(5000) }); } catch {}
      snap = fs.readFileSync(snapshotFile(r.port));
      try { process.kill(r.pid); } catch {}
    }
    await call(`${to.url}/rooms/${r.id}/snapshot`, { method: "PUT", body: snap, timeout: 30000 });
    await call(`${to.url}/rooms`, { method: "POST", body: JSON.stringify({ id: r.id, resume: true }), timeout: 60000 });
    const was = r.host ?? "main";
    Object.assign(r, { kind: "remote", host: to.name, region: to.region, game: `${to.url}/rooms/${r.id}`, port: undefined, pid: undefined, emptySince: null });
    this.hostInfo.delete(to.name);
    this.save();
    this.log(`room ${r.id} moved from ${was} to ${to.name} (${to.region})`);
    return { ok: true, room: r.id, from: was, to: to.name, region: to.region };
  }

  /** Move every room off a host (before taking it down), each to the best other host with space. */
  async drain(name) {
    const moved = [];
    for (const r of this.all().filter((x) => x.kind === "remote" && x.host === name)) {
      const target = (await Promise.all(this.hosts().filter((h) => h.name !== name).map(async (h) => ({ h, s: await this.hostStatus(h) }))))
        .filter((x) => x.s && x.s.free > 0)
        .sort((a, b) => (sameRegion(b.h.region, r.region?.slice(0, 2)) - sameRegion(a.h.region, r.region?.slice(0, 2))) || (a.s.load_pct - b.s.load_pct))[0];
      if (!target) throw new Error(`no other host has space for room ${r.id}`);
      moved.push(await this.move(r.id, target.h.name));
    }
    return { ok: true, moved };
  }

  /** Stop a room for good (it saves on the way out; a closed room stays closed). */
  async close(r) {
    if (r.kind === "remote") {
      const h = this.hosts().find((x) => x.name === r.host);
      if (h) try { await call(`${h.url}/rooms/${r.id}`, { method: "DELETE" }); } catch {}
    } else {
      try { process.kill(r.pid); } catch {}
      setTimeout(() => fs.rm(snapshotFile(r.port), { force: true }, () => {}), 5000).unref();
    }
    this.rooms.delete(r.id);
    this.save();
  }

  /** Close rooms that have had no outside players for a while, and bring back ones that died. Room 1 always stays. */
  async reap() {
    for (const r of this.all()) {
      if (r.kind === "host") continue;
      let outside = 0;
      try {
        const l = await (await fetch(`${r.game}/api/lobby`, { signal: AbortSignal.timeout(5000) })).json();
        outside = l.teams.filter((t) => !t.house && t.status === "playing").length;
        r.downSince = null;
      } catch {
        // The engine (or its whole host) is down: bring the game back from its save. A host that stays unreachable
        // for 30 minutes loses the room; until then players see "not running" and their seats wait.
        if (!(await alive(r.game)) && await this.relaunch(r)) { this.save(); continue; }
        r.downSince ??= Date.now();
        if (Date.now() - r.downSince > 30 * 60000) { this.rooms.delete(r.id); this.save(); this.log(`room ${r.id} is gone`); }
        continue;
      }
      if (outside > 0) { r.emptySince = null; continue; }
      r.emptySince ??= Date.now();
      if (Date.now() - r.emptySince < this.idleMs) continue;
      await this.close(r);
      this.log(`room ${r.id} closed (empty for ${Math.round(this.idleMs / 60000)} min)`);
    }
  }
}
