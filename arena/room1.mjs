#!/usr/bin/env node
// Room 1 without a GPU: the headless engine, in place of the Unity app.
//
//   node arena/room1.mjs --engine <dir with pez-headless.dll> [--resume | --fresh] [--port 7777]
//
// Started (detached) by arena/deploy.sh, which stops it with SIGTERM: the engine saves and exits, then this exits. It runs
// the game, an open room like the gateway's other rooms (scripted house AI, outside agents join): `dotnet
// <engine>/pez-headless.dll --port 7777 --open --controllers ai` on the room's saved game
// (~/.config/pezz/rooms/game-7777.json), resumed unless --fresh.
// If the engine exits on its own, this exits too: CI's watchdog relaunches room 1 from its save (deploy.sh --restart).
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
const log = (s) => console.log(`${new Date().toISOString().replace("T", " ").slice(0, 19)} room1: ${s}`);

const dll = path.join(ENGINE, "pez-headless.dll");
if (!fs.existsSync(dll)) { log(`no engine at ${dll}`); process.exit(2); }
const gameArgs = [dll, "--port", String(PORT), "--open", "--controllers", "ai", "--snapshot", SNAP, FRESH ? "--fresh" : "--resume"];
log(`starting the game: dotnet ${gameArgs.join(" ")}`);
const game = spawn("dotnet", gameArgs, { stdio: ["ignore", "inherit", "inherit"] });
let stopping = false;

function stop(sig) {
  if (stopping) return;
  stopping = true;
  log(`${sig}: stopping the game (it saves on the way out)`);
  game.kill("SIGTERM");
  setTimeout(() => { log("the game didn't exit in 20 s; SIGKILL"); game.kill("SIGKILL"); }, 20000).unref();
}
process.on("SIGTERM", () => stop("SIGTERM"));
process.on("SIGINT", () => stop("SIGINT"));
game.on("exit", (code, signal) => {
  log(`the game exited (${signal ?? code})`);
  process.exit(stopping ? 0 : 1);
});
