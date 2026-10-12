// Rooms: each room is one game with up to 8 seats. There's no permanent room: if nobody's connected, there's no room.
// When no room can take a join (placementOrder), it opens a new room: on a room host (mcp/roomhost.js on any tailnet
// machine, preferring the player's region, then the least loaded), or failing that a headless engine on this machine.
// A room closes once it has had no outside players for a while, or after a match's results with nobody left to play
// the next. Each room has a short code: a player hands it to a friend, and the friend's agent joins the same game with
// it. Codes pick a room; they aren't secrets. A room opened by a join waits, frozen, until min_players outside agents
// are in (the engine's waiting state), and so does every next match; a private one is left out of placement and
// listed without its code.
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
const fail = (status, m) => Object.assign(new Error(m), { status });

const ROOM_FIELDS = ["id", "code", "kind", "host", "region", "port", "pid", "createdAt", "announced", "private", "matchHours", "minPlayers"];

/**
 * Where a join with no code goes, given each room's lobby entry: a public playing room that isn't late (a late joiner
 * can't win on points), then a public waiting room; never one closing or showing results. Among those, the one with more
 * active outside players first, then more time left before sudden death.
 */
const activeOutside = (x) => (x.teams ?? []).filter((t) => !t.house && t.active).length;
const byPull = (a, b) => (activeOutside(b) - activeOutside(a)) || ((b.sudden_death_in_s ?? 0) - (a.sudden_death_in_s ?? 0));
export function placementOrder(entries) {
  const open = entries.filter((x) => !x.private && x.joinable);
  return [...open.filter((x) => x.status === "playing" && x.verdict !== "late").sort(byPull), ...open.filter((x) => x.status === "waiting").sort(byPull)];
}
/** The room /watch shows when none is named: a public running room with the most active outside players, then the most seated. */
const seated = (x) => (x.teams ?? []).filter((t) => !t.house && t.status === "playing").length;
export const busiest = (entries) => entries.filter((x) => !x.private && x.status !== "not running")
  .sort((a, b) => (activeOutside(b) - activeOutside(a)) || (seated(b) - seated(a)))[0] ?? null;

export class Rooms {
  /**
   * @param {object} o
   * @param {{game: string, frames: string|null}} [o.fixed] a game this gateway fronts but doesn't run (PEZZ_GAME: the CI
   *                              room, a test room): room 1 while the gateway runs, never closed, relaunched, moved or saved
   * @param {string} o.engine     the headless engine (a dll, or a self-contained executable) that local rooms run, when no room host has space
   * @param {string} o.stateFile  where room codes and running rooms are remembered across restarts
   * @param {string} o.hostsFile  room hosts: [{name, url, region}] (arena/roomhost.sh writes it)
   */
  constructor({ fixed, engine, stateFile, hostsFile, logDir, maxRooms = 6, idleMinutes = 10, basePort = 7801, localRooms = true, log = console.log }) {
    Object.assign(this, { engine, stateFile, hostsFile, logDir, maxRooms, idleMs: idleMinutes * 60000, basePort, localRooms, log });
    this.rooms = new Map();
    this.hostInfo = new Map(); // name -> { at, info } (cached /host answers)
    let saved = {};
    try { saved = JSON.parse(fs.readFileSync(stateFile, "utf8")); } catch {}
    if (fixed) this.rooms.set(1, { id: 1, kind: "fixed", ...fixed, code: newCode(), region: "us", host: "main" });
    // ---- One-time move-over (#39); remove once every server has started on this code. Room 1 used to be the host's
    // own game, outside rooms[] (rooms.json had hostCode; its save is game-<PEZZ_ROOM1_PORT, default 7777>.json). It
    // comes back as an ordinary local room resumed from that save: id 1, same code, seats and tokens (r1-...).
    const r1port = Number(process.env.PEZZ_ROOM1_PORT || 7777);
    if (saved.hostCode && !fixed && fs.existsSync(snapshotFile(r1port))) {
      saved.rooms = [{ id: 1, kind: "headless", host: "main", region: "us", port: r1port, code: saved.hostCode, announced: saved.hostAnnounced ?? null, createdAt: Date.now() }, ...(saved.rooms ?? [])];
      log(`room 1 (code ${saved.hostCode}) is an ordinary room now: resuming it from its save on port ${r1port}`);
    }
    // ---- end of the move-over
    // Rooms keep running across a gateway restart (they're detached, or on another machine): re-adopt the ones that
    // still answer. One that died (a crash, a machine restarting, a deploy) is brought back from its saved game: same
    // code, seats and tokens.
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
    const out = [{ name: "main", region: "us", rooms: this.all().filter((r) => r.host === "main").length, status: "up" }];
    for (const h of this.hosts()) {
      const s = await this.hostStatus(h);
      out.push(s ? { name: h.name, region: h.region, platform: s.platform, rooms: s.rooms.length, capacity: s.capacity, load_pct: s.load_pct, status: "up" } : { name: h.name, region: h.region, status: "down" });
    }
    return out;
  }

  save() {
    const data = { rooms: this.all().filter((r) => r.kind !== "fixed").map((r) => Object.fromEntries(ROOM_FIELDS.map((k) => [k, r[k]]))) };
    try {
      fs.mkdirSync(path.dirname(this.stateFile), { recursive: true, mode: 0o700 });
      fs.writeFileSync(this.stateFile, JSON.stringify(data), { mode: 0o600 });
    } catch (e) { this.log("couldn't save rooms:", e.message); }
  }

  /** Start a local room's headless engine on its port (resume: from its saved game) and wait until it answers. */
  async launch(id, port, resume, flags = []) {
    fs.mkdirSync(this.logDir, { recursive: true });
    const out = fs.openSync(path.join(this.logDir, `room-${id}.log`), "a");
    const seed = String(1 + (randomBytes(4).readUInt32BE() % 1e9));
    const args = ["--port", String(port), "--open", "--controllers", "ai", "--seed", seed, resume ? "--resume" : "--fresh", ...flags];
    // A .dll runs with dotnet; anything else (a self-contained pez-headless) runs as it is, as on room hosts.
    const proc = spawn(...(this.engine.endsWith(".dll") ? ["dotnet", [this.engine, ...args]] : [this.engine, args]), { detached: true, stdio: ["ignore", out, out] });
    proc.unref();
    let failed = false; // an engine that can't start at all (no dotnet, a bad path) fails this launch, not the gateway
    proc.on("error", (e) => { failed = true; this.log(`room ${id}'s engine couldn't start: ${e.message}`); });
    const game = `http://127.0.0.1:${port}`;
    // Up to a minute: resuming a long match's save takes a while, and an engine given up on here is killed.
    for (let i = 0; i < 240 && !failed && !(await alive(game)); i++) await new Promise((r) => setTimeout(r, 250));
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
    // The engine takes its config from its save, but --min-players wins (Program.OperatorFlagsWin): a room saved before
    // waiting rooms (no minPlayers; room 1 above) waits for players from its next match on, like any other.
    r.minPlayers ??= 2;
    const pid = await this.launch(r.id, r.port, true, ["--min-players", String(r.minPlayers)]);
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

  /** Open a new room (a waiting room: match length and players to start as given) and wait until its game answers. */
  async spawn(region = null, { matchHours = 4, minPlayers = 2, private: priv = false } = {}) {
    const opts = { matchHours, minPlayers, private: priv };
    if (this.rooms.size >= this.maxRooms) throw fail(503, `every room is full (${this.rooms.size} rooms of 8); try again later`);
    let id = 1; while (this.rooms.has(id)) id++; // the lowest free id
    const host = await this.pickHost(region);
    let room;
    if (host) {
      await call(`${host.url}/rooms`, { method: "POST", body: JSON.stringify({ id, match_hours: matchHours, min_players: minPlayers }), timeout: 60000 });
      this.hostInfo.delete(host.name);
      room = { id, kind: "remote", host: host.name, region: host.region, game: `${host.url}/rooms/${id}`, frames: null, code: newCode(), createdAt: Date.now(), emptySince: Date.now(), ...opts };
      this.log(`room ${id} started on ${host.name} (${host.region}, code ${room.code})`);
    } else {
      if (!this.localRooms) throw fail(503, "no room host has space right now; try again later");
      // The lowest port (every other one: an engine may serve frames on port + 1) that no room holds and nothing answers on.
      const taken = new Set(this.all().map((r) => r.port));
      let port = this.basePort;
      while (taken.has(port) || await alive(`http://127.0.0.1:${port}`)) port += 2;
      // --fresh: never pick up an old room's game on this port.
      const pid = await this.launch(id, port, false, ["--match-hours", String(matchHours), "--min-players", String(minPlayers)]);
      if (!pid) throw new Error("couldn't start a new room");
      room = { id, kind: "headless", host: "main", region: "us", game: `http://127.0.0.1:${port}`, frames: null, port, pid, code: newCode(), createdAt: Date.now(), emptySince: Date.now(), ...opts };
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
    if (!r) throw fail(404, `no room ${id}`);
    if (r.kind === "fixed") throw fail(400, `room ${id} is a game this gateway doesn't run (PEZZ_GAME): it can't move`);
    const to = this.hosts().find((h) => h.name === toName);
    if (!to) throw fail(404, `no room host named ${toName}`);
    if (r.kind === "remote" && r.host === to.name) return { ok: true, note: "already there" };
    const ts = await this.hostStatus(to);
    if (!ts || ts.free < 1) throw fail(503, `${to.name} has no free slot`);
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

  /**
   * Close rooms nobody plays (after a while without outside players, or straight after a match's results with none left
   * to play the next), and bring back ones that died. Every minute; one pass at a time (a relaunch can take a while,
   * and two would start two engines on one save), and never before the rooms found at startup are back.
   */
  reap() { return (this.reaping ??= this.ready.then(() => this.reapAll()).finally(() => { this.reaping = null; })); }
  async reapAll() {
    for (const r of this.all()) {
      if (r.kind === "fixed") continue;
      let outside = 0, status;
      try {
        const l = await (await fetch(`${r.game}/api/lobby`, { signal: AbortSignal.timeout(5000) })).json();
        // A seat whose agent stopped calling state, command or wait doesn't keep a room (and a host's slot) open forever.
        outside = l.teams.filter((t) => !t.house && t.status === "playing" && !(t.idle_s > this.idleMs / 1000)).length;
        status = l.status;
        r.downSince = null;
      } catch {
        // The engine (or its whole host) is down: bring the game back from its save. A host that stays unreachable
        // for 30 minutes loses the room; until then players see "not running" and their seats wait.
        if (!(await alive(r.game)) && await this.relaunch(r)) { this.save(); continue; }
        r.downSince ??= Date.now();
        if (Date.now() - r.downSince > 30 * 60000) { this.rooms.delete(r.id); this.save(); this.log(`room ${r.id} is gone`); }
        continue;
      }
      // A waiting room has no game time, so its seats never idle: it closes after idleMs with no call from its players.
      if (status === "waiting") {
        r.calledAt ??= Date.now();
        if (Date.now() - r.calledAt < this.idleMs) continue;
        await this.close(r);
        this.log(`room ${r.id} closed (waiting, no call from its players for ${Math.round(this.idleMs / 60000)} min)`);
        continue;
      }
      if (outside > 0) { r.emptySince = null; continue; }
      // A match is over and nobody's left to play the next. (With players still seated, the engine opens the next match
      // as a waiting room: same code, min_players and match length.)
      if (status === "results") { await this.close(r); this.log(`room ${r.id} closed (its match is over, with no outside players left)`); continue; }
      r.emptySince ??= Date.now();
      if (Date.now() - r.emptySince < this.idleMs) continue;
      await this.close(r);
      this.log(`room ${r.id} closed (empty for ${Math.round(this.idleMs / 60000)} min)`);
    }
  }
}
