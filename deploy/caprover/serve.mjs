#!/usr/bin/env node
// The Pezz server in one container (CapRover): room 1's engine, the gateway in front of it, and whatever extra rooms the
// gateway opens (it starts them itself, as local headless engines, when room 1 is full).
//
//   node deploy/caprover/serve.mjs        (the image's entrypoint, under tini)
//
// Everything that has to outlive a deploy is under $HOME (/data, CapRover's persistent volume): each room's saved game
// (~/.config/pezz/rooms/game-<port>.json), the gateway's rooms, seats, commander links, replays and operator secret, and the
// logs. A deploy stops the container with SIGTERM: every engine (room 1 and the extra rooms) saves its game and exits, then
// the gateway; the next container resumes them all. If room 1's engine or the gateway dies on its own, this stops the rest
// cleanly and exits, and CapRover starts a fresh container that resumes from the saves.
// Env: PEZZ_ROOM1_PORT (7777), PEZZ_GATEWAY_PORT (8080), PEZZ_ROOM1_ARGS (extra engine flags for room 1), PEZZ_STOP_S (25).
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const APP = "/app";
const HOME = os.homedir();
const CONFIG = path.join(HOME, ".config", "pezz");
const LOGS = path.join(HOME, "logs");
const ENGINE = path.join(APP, "engine", "pez-headless.dll");
const PORT = Number(process.env.PEZZ_ROOM1_PORT || 7777);
const STOP_S = Number(process.env.PEZZ_STOP_S || 25);
const log = (s) => console.log(`${new Date().toISOString().replace("T", " ").slice(0, 19)} serve: ${s}`);

fs.mkdirSync(path.join(CONFIG, "rooms"), { recursive: true });
fs.mkdirSync(LOGS, { recursive: true });
// The gateway writes extra rooms' logs to <checkout>/arena/logs: keep them on the volume too.
const appLogs = path.join(APP, "arena", "logs");
try { if (!fs.lstatSync(appLogs).isSymbolicLink()) fs.rmSync(appLogs, { recursive: true, force: true }); } catch {}
if (!fs.existsSync(appLogs)) fs.symlinkSync(LOGS, appLogs);

let stopping = false;
const kids = {};

function start(name, cmd, args, env = {}) {
  const out = fs.openSync(path.join(LOGS, `${name}.log`), "a");
  const p = spawn(cmd, args, { cwd: APP, env: { ...process.env, ...env }, stdio: ["ignore", out, out] });
  kids[name] = p;
  log(`${name} started (pid ${p.pid})`);
  p.on("exit", (code, sig) => {
    log(`${name} exited (${sig ?? code})`);
    delete kids[name];
    if (!stopping) shutdown(`${name} exited on its own`, 1);
  });
  return p;
}

async function answers(url) {
  try { return (await fetch(url, { signal: AbortSignal.timeout(2000) })).ok; } catch { return false; }
}

/** Every engine in the container: room 1 and the rooms the gateway opened (detached, so not our children). */
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
  // Engines first, so the gateway is still there while they save; each saves its game on SIGTERM.
  const pids = engines();
  for (const pid of pids) try { process.kill(pid, "SIGTERM"); } catch {}
  const end = Date.now() + STOP_S * 1000;
  while (pids.some(alive) && Date.now() < end) await new Promise((r) => setTimeout(r, 200));
  const stuck = pids.filter(alive);
  if (stuck.length) { log(`engines still running after ${STOP_S} s: ${stuck.join(", ")}; killing`); for (const pid of stuck) try { process.kill(pid, "SIGKILL"); } catch {} }
  else log(`${pids.length} engine(s) saved and stopped`);
  if (kids.gateway) { kids.gateway.kill("SIGTERM"); await new Promise((r) => { const t = setTimeout(r, 3000); kids.gateway?.once("exit", () => { clearTimeout(t); r(); }); }); }
  process.exit(code);
}
process.on("SIGTERM", () => shutdown("SIGTERM"));
process.on("SIGINT", () => shutdown("SIGINT"));

// Room 1: an open room like the ones the gateway opens, resumed from its save when there is one.
const save = path.join(CONFIG, "rooms", `game-${PORT}.json`);
const extra = (process.env.PEZZ_ROOM1_ARGS || "").split(/\s+/).filter(Boolean);
start("room1", "dotnet", [ENGINE, "--port", String(PORT), "--open", "--controllers", "ai", "--snapshot", save, ...(fs.existsSync(save) ? ["--resume"] : ["--fresh"]), ...extra]);

for (let i = 0; i < 120 && !stopping; i++) {
  if (await answers(`http://127.0.0.1:${PORT}/api/status`)) break;
  await new Promise((r) => setTimeout(r, 500));
}
if (!stopping) {
  log(`room 1 answers on :${PORT}`);
  start("gateway", process.execPath, [path.join(APP, "mcp", "gateway.js")], {
    PEZZ_GAME: `http://127.0.0.1:${PORT}`,
    PEZZ_FRAMES: "none",            // no renderer: browsers draw the game, and `look` uses the player's browser or the map
    PEZZ_ENGINE: ENGINE,            // what extra rooms run
    PEZZ_WEB_DIR: path.join(APP, "build", "web"),
    PEZZ_BUILD: (() => { try { return fs.readFileSync(path.join(APP, "BUILD_INFO"), "utf8").trim(); } catch { return ""; } })(), // /healthz
  });
}
