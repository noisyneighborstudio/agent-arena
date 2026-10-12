#!/usr/bin/env node
// Room 1 without a GPU: the headless engine plus its resident agents, in place of the Unity app.
//
//   node arena/room1.mjs --engine <dir with pez-headless.dll> [--resume | --fresh] [--port 7777]
//
// Started (detached) by arena/deploy.sh, which stops it with SIGTERM: the agents stop, the engine saves and exits, then
// this exits. It does what the Unity app did for room 1 besides drawing it:
//   - runs the game: `dotnet <engine>/pez-headless.dll --port 7777 --open` on the room's saved game
//     (~/.config/pezz/rooms/game-7777.json), resumed unless --fresh;
//   - launches the house agents once the game answers, as the app's -team0/-team1 did: `node arena/battle.mjs claude
//     codex --attach --no-restart` (models and efforts below), in a login shell for the user's PATH, logged to
//     arena/logs/room1-agents.log.
// If the engine exits on its own, this exits too: CI's watchdog relaunches room 1 from its save (deploy.sh --restart).
// Env: PEZZ_ROOM1_AGENTS (the battle.mjs players and flags; "none" for no agents).
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf("--" + n); return i >= 0 && args[i + 1] && !args[i + 1].startsWith("--") ? args[i + 1] : d; };
const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const ENGINE = path.resolve(opt("engine", path.join(ROOT, "unity/Build/engine")));
const PORT = Number(opt("port", 7777));
const FRESH = args.includes("--fresh");
const SNAP = path.join(os.homedir(), ".config", "pezz", "rooms", `game-${PORT}.json`);
const AGENTS = process.env.PEZZ_ROOM1_AGENTS ?? "claude codex --model-claude sonnet --effort-claude medium --model-codex gpt-5.6-luna --effort-codex medium";
const LOGS = path.join(ROOT, "arena", "logs");
fs.mkdirSync(LOGS, { recursive: true });
const log = (s) => console.log(`${new Date().toISOString().replace("T", " ").slice(0, 19)} room1: ${s}`);

const dll = path.join(ENGINE, "pez-headless.dll");
if (!fs.existsSync(dll)) { log(`no engine at ${dll}`); process.exit(2); }
const gameArgs = [dll, "--port", String(PORT), "--open", "--controllers", "llm,llm", "--snapshot", SNAP, FRESH ? "--fresh" : "--resume"];
log(`starting the game: dotnet ${gameArgs.join(" ")}`);
const game = spawn("dotnet", gameArgs, { stdio: ["ignore", "inherit", "inherit"] });
let agents = null, stopping = false;

async function answering() {
  for (let i = 0; i < 120 && !stopping; i++) {
    try { const r = await fetch(`http://127.0.0.1:${PORT}/api/status`, { signal: AbortSignal.timeout(2000) }); if (r.ok) return true; } catch {}
    await new Promise((r) => setTimeout(r, 1000));
  }
  return false;
}

(async () => {
  if (!(await answering()) || stopping) return;
  if (AGENTS === "none") { log("game up; no house agents (PEZZ_ROOM1_AGENTS=none)"); return; }
  // As the Unity app did (GameRunner.LaunchAgents): attach to the running game, keep its seats, two hours of play.
  const cmd = `cd '${ROOT}' && exec node arena/battle.mjs ${AGENTS} --attach --no-restart --url http://127.0.0.1:${PORT} --minutes 120 >> '${path.join(LOGS, "room1-agents.log")}' 2>&1`;
  agents = spawn("/bin/zsh", ["-lic", cmd], { stdio: "ignore", detached: true }); // its own group, so stopping it takes the agent CLIs too
  log(`game up; house agents launched (pid ${agents.pid}): ${AGENTS}`);
  agents.on("exit", (code) => { log(`house agents exited (${code})`); agents = null; });
})();

function stopAgents() {
  if (!agents) return;
  try { process.kill(-agents.pid, "SIGTERM"); } catch {}
  agents = null;
}

function stop(sig) {
  if (stopping) return;
  stopping = true;
  log(`${sig}: stopping the agents, then the game (it saves on the way out)`);
  stopAgents();
  game.kill("SIGTERM");
  setTimeout(() => { log("the game didn't exit in 20 s; SIGKILL"); game.kill("SIGKILL"); }, 20000).unref();
}
process.on("SIGTERM", () => stop("SIGTERM"));
process.on("SIGINT", () => stop("SIGINT"));
game.on("exit", (code, signal) => {
  log(`the game exited (${signal ?? code})`);
  stopAgents();
  process.exit(stopping ? 0 : 1);
});
