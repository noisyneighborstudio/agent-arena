// Rooms: each room is one game with up to 8 seats. Room 1 is the host's game (the Unity app, which also renders
// high-res live views). When every room is full, the next join starts a new headless room on this machine, and an
// overflow room shuts down after it has had no outside players for a while. Each room has a short code: a player
// hands it to a friend, and the friend's agent joins the same game with it. Codes pick a room; they aren't secrets.
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { randomBytes } from "node:crypto";

const CODE_CHARS = "abcdefghjkmnpqrstuvwxyz23456789"; // no 0/o, 1/l/i
export const newCode = () => "pezz-" + [...randomBytes(6)].map((b) => CODE_CHARS[b % CODE_CHARS.length]).join("");

// Where a headless room saves its game (pez-headless's default for its port), so a room that died resumes.
const snapshotFile = (port) => path.join(os.homedir(), ".config", "pezz", "rooms", `game-${port}.json`);

async function alive(game) {
  try { return (await fetch(`${game}/api/status`, { signal: AbortSignal.timeout(1500) })).ok; } catch { return false; }
}

export class Rooms {
  /**
   * @param {object} o
   * @param {string} o.hostGame   room 1's game API (the Unity app)
   * @param {string} o.hostFrames room 1's frame server (per-player live renders)
   * @param {string} o.engine     the headless engine dll that overflow rooms run
   * @param {string} o.stateFile  where room codes and running overflow rooms are remembered across restarts
   */
  constructor({ hostGame, hostFrames, engine, stateFile, logDir, maxRooms = 6, idleMinutes = 10, basePort = 7801, log = console.log }) {
    Object.assign(this, { engine, stateFile, logDir, maxRooms, idleMs: idleMinutes * 60000, basePort, log });
    this.rooms = new Map();
    let saved = {};
    try { saved = JSON.parse(fs.readFileSync(stateFile, "utf8")); } catch {}
    this.rooms.set(1, { id: 1, kind: "host", game: hostGame, frames: hostFrames, code: saved.hostCode || newCode(), emptySince: null, announced: saved.hostAnnounced ?? null });
    // Overflow rooms keep running across a gateway restart (they're detached); re-adopt the ones that still answer.
    // One that died (a crash, the machine restarting) is relaunched from its saved game: same code, seats and tokens.
    for (const r of saved.rooms ?? []) this.rooms.set(r.id, { ...r, kind: "headless", game: `http://127.0.0.1:${r.port}`, frames: null, emptySince: null, adopting: true });
    this.save();
    this.ready = Promise.all([...this.rooms.values()].filter((r) => r.adopting).map(async (r) => {
      delete r.adopting;
      if (await alive(r.game)) return;
      if (fs.existsSync(snapshotFile(r.port)) && await this.relaunch(r)) return;
      this.rooms.delete(r.id);
    })).then(() => this.save());
    setInterval(() => this.reap().catch(() => {}), 60000).unref();
  }

  all() { return [...this.rooms.values()].sort((a, b) => a.id - b.id); }
  get(id) { return this.rooms.get(Number(id)); }
  byCode(code) { const c = String(code ?? "").trim().toLowerCase(); return this.all().find((r) => r.code === c); }

  save() {
    const data = { hostCode: this.get(1).code, hostAnnounced: this.get(1).announced ?? null,
      rooms: this.all().filter((r) => r.kind === "headless").map(({ id, code, port, pid, createdAt, announced }) => ({ id, code, port, pid, createdAt, announced })) };
    try {
      fs.mkdirSync(path.dirname(this.stateFile), { recursive: true, mode: 0o700 });
      fs.writeFileSync(this.stateFile, JSON.stringify(data), { mode: 0o600 });
    } catch (e) { this.log("couldn't save rooms:", e.message); }
  }

  /** Start a room's headless engine on its port (resume: from its saved game) and wait until it answers. */
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

  /** Bring a room that died back from its saved game. */
  async relaunch(r) {
    const pid = await this.launch(r.id, r.port, true);
    if (!pid) { this.log(`room ${r.id} couldn't be resumed`); return false; }
    r.pid = pid; r.emptySince = null;
    this.log(`room ${r.id} resumed from its saved game on port ${r.port} (pid ${pid}, code ${r.code})`);
    return true;
  }

  /** Start a new headless room and wait until its game answers. */
  async spawn() {
    if (this.rooms.size >= this.maxRooms) { const e = new Error(`every room is full (${this.rooms.size} rooms of 8); try again later`); e.status = 503; throw e; }
    let id = 2; while (this.rooms.has(id)) id++;
    let port = this.basePort + (id - 2) * 2;
    while (await alive(`http://127.0.0.1:${port}`)) port += 2;
    const pid = await this.launch(id, port, false); // --fresh: never pick up an old room's game on this port
    if (!pid) throw new Error("couldn't start a new room");
    const room = { id, kind: "headless", game: `http://127.0.0.1:${port}`, frames: null, port, pid, code: newCode(), createdAt: Date.now(), emptySince: Date.now() };
    this.rooms.set(id, room);
    this.save();
    this.log(`room ${id} started on port ${port} (pid ${proc.pid}, code ${room.code})`);
    return room;
  }

  /** Close overflow rooms that have had no outside players for a while. Room 1 always stays. */
  async reap() {
    for (const r of this.all()) {
      if (r.kind !== "headless") continue;
      let outside = 0;
      try {
        const l = await (await fetch(`${r.game}/api/lobby`, { signal: AbortSignal.timeout(3000) })).json();
        outside = l.teams.filter((t) => !t.house && t.status === "playing").length;
      } catch {
        // The engine died: bring the game back from its save if it has one, else the room is gone.
        if (!(await alive(r.game)) && fs.existsSync(snapshotFile(r.port)) && await this.relaunch(r)) { this.save(); continue; }
        this.rooms.delete(r.id); this.save(); this.log(`room ${r.id} is gone`); continue;
      }
      if (outside > 0) { r.emptySince = null; continue; }
      r.emptySince ??= Date.now();
      if (Date.now() - r.emptySince < this.idleMs) continue;
      try { process.kill(r.pid); } catch {}
      this.rooms.delete(r.id);
      this.save();
      this.log(`room ${r.id} closed (empty for ${Math.round(this.idleMs / 60000)} min)`);
      setTimeout(() => fs.rm(snapshotFile(r.port), { force: true }, () => {}), 5000).unref(); // it saves on the way out: a closed room stays closed
    }
  }
}
