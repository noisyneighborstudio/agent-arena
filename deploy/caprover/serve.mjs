#!/usr/bin/env node
// The Pezz server in one container (CapRover): the gateway, and the rooms it opens as local headless engines when agents
// join (there's no permanent room: with nobody connected there's no engine running at all).
//
//   node deploy/caprover/serve.mjs        (the image's entrypoint, under tini)
//
// Everything that has to outlive a deploy is under $HOME (/data, CapRover's persistent volume): each room's saved game
// (~/.config/pezz/rooms/game-<port>.json), the gateway's rooms, seats, commander links, replays and operator secret, and the
// logs. A deploy stops the container with SIGTERM: the gateway stops, then every engine saves its game and exits; the
// next container's gateway resumes them all from ~/.config/pezz/rooms.json. The engines are the gateway's (detached) children,
// not ours: one that exits on its own is brought back from its save by the gateway (Rooms.reap, within a minute), never
// treated as a crash here. If the gateway exits on its own, this stops every engine cleanly and exits, and CapRover starts
// a fresh container that resumes them.
// Env: PEZZ_STOP_S (25); the gateway's own (PEZZ_GATEWAY_PORT 8080 and the rest) come from the image and the app.
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const APP = "/app";
const HOME = os.homedir();
const CONFIG = path.join(HOME, ".config", "pezz");
const LOGS = path.join(HOME, "logs");
const STOP_S = Number(process.env.PEZZ_STOP_S || 25);
const log = (s) => console.log(`${new Date().toISOString().replace("T", " ").slice(0, 19)} serve: ${s}`);

fs.mkdirSync(path.join(CONFIG, "rooms"), { recursive: true });
fs.mkdirSync(LOGS, { recursive: true });
// The gateway writes rooms' logs to <checkout>/arena/logs: keep them on the volume too.
const appLogs = path.join(APP, "arena", "logs");
try { if (!fs.lstatSync(appLogs).isSymbolicLink()) fs.rmSync(appLogs, { recursive: true, force: true }); } catch {}
if (!fs.existsSync(appLogs)) fs.symlinkSync(LOGS, appLogs);

let stopping = false;

/** Every engine in the container (the gateway's rooms: detached, so not our children). */
function engines() {
  const pids = [];
  for (const d of fs.readdirSync("/proc")) {
    if (!/^\d+$/.test(d)) continue;
    try { if (fs.readFileSync(`/proc/${d}/cmdline`, "utf8").includes("pez-headless")) pids.push(Number(d)); } catch {}
  }
  return pids;
}
const alive = (pid) => { try { process.kill(pid, 0); return true; } catch { return false; } };

async function shutdown(why, code = 0) {
  if (stopping) return;
  stopping = true;
  log(`${why}: saving every room, then stopping`);
  // The gateway first: its reaper relaunches an engine that has stopped, and must not bring one back mid-shutdown. The
  // engines are detached, so they run on until told; then each saves its game on SIGTERM.
  if (gateway.exitCode === null && gateway.signalCode === null) {
    gateway.kill("SIGTERM");
    await new Promise((r) => { const t = setTimeout(r, 3000); gateway.once("exit", () => { clearTimeout(t); r(); }); });
  }
  const pids = engines();
  for (const pid of pids) try { process.kill(pid, "SIGTERM"); } catch {}
  const end = Date.now() + STOP_S * 1000;
  while (pids.some(alive) && Date.now() < end) await new Promise((r) => setTimeout(r, 200));
  const stuck = pids.filter(alive);
  if (stuck.length) { log(`engines still running after ${STOP_S} s: ${stuck.join(", ")}; killing`); for (const pid of stuck) try { process.kill(pid, "SIGKILL"); } catch {} }
  else log(`${pids.length} engine(s) saved and stopped`);
  process.exit(code);
}

const env = { ...process.env,
  PEZZ_FRAMES: "none",            // no renderer: browsers draw the game, and `look` uses the player's browser or the map
  PEZZ_ENGINE: path.join(APP, "engine", "pez-headless.dll"), // what rooms run
  PEZZ_WEB_DIR: path.join(APP, "build", "web"),
  PEZZ_BUILD: (() => { try { return fs.readFileSync(path.join(APP, "BUILD_INFO"), "utf8").trim(); } catch { return ""; } })(), // /healthz
};
delete env.PEZZ_GAME; // a game the gateway doesn't run is for the CI room, never here (one left in the app's settings would pin a dead room 1)
const out = fs.openSync(path.join(LOGS, "gateway.log"), "a");
const gateway = spawn(process.execPath, [path.join(APP, "mcp", "gateway.js")], { cwd: APP, stdio: ["ignore", out, out], env });
log(`gateway started (pid ${gateway.pid})`);
gateway.on("exit", (code, sig) => { log(`gateway exited (${sig ?? code})`); if (!stopping) shutdown("the gateway exited on its own", 1); });
process.on("SIGTERM", () => shutdown("SIGTERM"));
process.on("SIGINT", () => shutdown("SIGINT"));
