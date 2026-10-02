#!/usr/bin/env node
// Battle of the LLMs: pits agent CLIs against each other (or the scripted AI) in Pez.
//
//   node arena/battle.mjs claude codex            # Claude (Blue) vs Codex (Red), headless game
//   node arena/battle.mjs claude ai --attach      # Claude vs scripted AI in the running Unity game
//   node arena/battle.mjs grok gemini --speed 0.5 --minutes 30
//
// Players: claude | codex | grok | gemini | ai | human | external (human/external only make sense with --attach)
// Options: --attach (use the game already running at --url, e.g. Unity), --no-restart (join the current game as-is),
//          --url, --speed, --seed, --minutes, --model-<player> <model>, --effort-<player> <low|medium|high|...>
import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const MCP_SERVER = path.join(ROOT, "mcp", "server.js");
const argv = process.argv.slice(2);
const opt = (name, def) => { const i = argv.indexOf(`--${name}`); return i >= 0 ? argv[i + 1] : def; };
const flag = (name) => argv.includes(`--${name}`);
const optValues = new Set(argv.flatMap((a, i) => (a.startsWith("--") && argv[i + 1] && !argv[i + 1].startsWith("--") && !["attach", "no-restart"].includes(a.slice(2)) ? [i + 1] : [])));
const players = argv.filter((a, i) => !a.startsWith("--") && !optValues.has(i));
if (players.length < 2) {
  console.error("usage: node arena/battle.mjs <player0> <player1> [--attach] [--speed 1] [--seed N] [--minutes 30]\nplayers: claude codex grok gemini ai human");
  process.exit(1);
}
const URL_BASE = opt("url", "http://127.0.0.1:7777");
const SPEED = Number(opt("speed", "1"));
const SEED = Number(opt("seed", String(Math.floor(Math.random() * 100000))));
const MINUTES = Number(opt("minutes", "30"));
const TEAM_NAMES = ["Blueberry", "Cherry", "Lime", "Lemon"];
const DISPLAY = { claude: "Claude", codex: "Codex", grok: "Grok", gemini: "Gemini", ai: "Scripted AI", human: "Human", external: "External" };
const NO_AGENT = ["ai", "human", "external"];
const stamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
const LOGDIR = path.join(ROOT, "arena", "logs", stamp);
fs.mkdirSync(LOGDIR, { recursive: true });

const children = new Set();
let over = false;

async function api(p, body) {
  const r = await fetch(URL_BASE + p, body ? { method: "POST", body: JSON.stringify(body), headers: { "content-type": "application/json" } } : undefined);
  return r.json();
}

async function waitForServer() {
  for (let i = 0; i < 120; i++) {
    try { await api("/api/status"); return; } catch { await new Promise((r) => setTimeout(r, 500)); }
  }
  throw new Error(`game not reachable at ${URL_BASE}`);
}

function prompt(team, player, continuation) {
  const me = TEAM_NAMES[team];
  const foes = players.map((p, i) => (i === team ? null : `${TEAM_NAMES[i]} (${DISPLAY[p] ?? p})`)).filter(Boolean).join(", ");
  return [
    continuation
      ? `You are ${DISPLAY[player]}, still commanding team ${team} (${me}) in an ongoing game of Pez. Your previous session ended but the game is NOT over. Call get_state now and keep fighting.`
      : `You are ${DISPLAY[player]}, commanding team ${team} (${me}) in Pez, a real-time strategy game in the style of Command & Conquer. Your opponent(s): ${foes}. This is a battle of the LLMs.`,
    "Use only the pez MCP tools (get_rules, get_state, get_map, command, wait).",
    continuation ? "" : "Start with get_rules once, then get_state and get_map.",
    "Then loop until get_state shows game_over: true — read state, issue a batch of commands (economy, production, army orders), then wait a few seconds.",
    "The game runs in real time and never pauses for you, so keep turns short and batch several commands per call.",
    "COMMANDER'S ORDERS: a human commander may give you standing orders at any time. They appear as standing_orders in get_state and as a 📣 NEW ORDERS banner when they change. They take precedence over your own strategy; follow them, and use your judgment for anything they don't cover. Never stop playing because of them.",
    "PRIORITY ALERTS: when a tool response starts with ⚠️ PRIORITY ALERT (your base, trucks or units under attack, enemies near your base, a stealth bomber detected), handle it first, the way a human commander would: send nearby combat units (the alert lists them), pull back trucks, repair, or build defenses. Then resume your plan. wait returns early when an alert fires, so long waits are safe.",
    "Win by destroying every enemy structure. The economy is a production chain: mining trucks mine four ore types into your stockpile, converter buildings refine them (steel, copper, circuits, lenses, plasma, composite), and higher tiers cost those materials. Assign trucks to the ores you need, keep power positive, expand with outpost trucks to claim more ore fields, defend what you own, scout through the fog, and attack.",
    "Use say occasionally to trash-talk your opponent; it shows on screen. Do not stop until the game is over.",
  ].filter(Boolean).join(" ");
}

function mcpEnv(team, player) {
  return { PEZ_TEAM: String(team), PEZ_PLAYER: DISPLAY[player] ?? player, PEZ_URL: URL_BASE };
}

/** Returns [command, args, cwd] for one agent session. */
function agentCommand(team, player, continuation) {
  const dir = path.join(LOGDIR, `team${team}-${player}`);
  fs.mkdirSync(dir, { recursive: true });
  const env = mcpEnv(team, player);
  const p = prompt(team, player, continuation);
  const model = opt(`model-${player}`);
  const effort = opt(`effort-${player}`);
  switch (player) {
    case "claude": {
      const cfg = path.join(dir, "mcp.json");
      fs.writeFileSync(cfg, JSON.stringify({ mcpServers: { pez: { command: "node", args: [MCP_SERVER], env } } }, null, 2));
      const args = ["-p", p, "--mcp-config", cfg, "--strict-mcp-config", "--allowedTools", "mcp__pez", "--tools", "", "--output-format", "stream-json", "--verbose"];
      if (model) args.push("--model", model);
      if (effort) args.push("--effort", effort);
      return ["claude", args, dir];
    }
    case "codex": {
      const toml = (o) => `{${Object.entries(o).map(([k, v]) => `${k}="${v}"`).join(",")}}`;
      const args = ["exec", "--skip-git-repo-check", "--json", "-s", "read-only",
        "-c", `mcp_servers.pez.command="node"`, "-c", `mcp_servers.pez.args=["${MCP_SERVER}"]`, "-c", `mcp_servers.pez.env=${toml(env)}`,
        "-c", `mcp_servers.pez.default_tools_approval_mode="approve"`, "-c", `approval_policy="never"`, "-c", `mcp_servers.pez.tool_timeout_sec=60`];
      if (model) args.push("-m", model);
      if (effort) args.push("-c", `model_reasoning_effort="${effort}"`);
      args.push(p);
      return ["codex", args, dir];
    }
    case "grok": {
      if (!fs.existsSync(path.join(dir, ".grok", "config.toml"))) {
        spawnSync("grok", ["mcp", "add", "-s", "project", ...Object.entries(env).flatMap(([k, v]) => ["-e", `${k}=${v}`]), "pez", "node", "--", MCP_SERVER], { cwd: dir, stdio: "ignore" });
      }
      const args = ["-p", p, "--always-approve", "--cwd", dir];
      if (model) args.push("--model", model);
      return ["grok", args, dir];
    }
    case "gemini": {
      fs.mkdirSync(path.join(dir, ".gemini"), { recursive: true });
      fs.writeFileSync(path.join(dir, ".gemini", "settings.json"), JSON.stringify({ mcpServers: { pez: { command: "node", args: [MCP_SERVER], env, trust: true } } }, null, 2));
      const args = ["-p", p, "--yolo"];
      if (model) args.push("-m", model);
      return ["gemini", args, dir];
    }
    default:
      throw new Error(`unknown player '${player}'`);
  }
}

async function runAgent(team, player) {
  let session = 0;
  while (!over) {
    const [cmd, args, cwd] = agentCommand(team, player, session > 0);
    const log = fs.createWriteStream(path.join(LOGDIR, `team${team}-${player}.log`), { flags: "a" });
    log.write(`\n===== session ${session} ${new Date().toISOString()} =====\n$ ${cmd} ${args.map((a) => JSON.stringify(a)).join(" ")}\n`);
    const env = { ...process.env };
    for (const k of Object.keys(env)) if (k === "CLAUDECODE" || k.startsWith("CLAUDE_CODE_")) delete env[k]; // allow nested claude
    const child = spawn(cmd, args, { cwd, env, stdio: ["ignore", "pipe", "pipe"] });
    children.add(child);
    child.stdout.pipe(log, { end: false });
    child.stderr.pipe(log, { end: false });
    const code = await new Promise((r) => child.on("close", r));
    children.delete(child);
    log.end(`\n===== session ${session} exited ${code} =====\n`);
    session++;
    if (!over) await new Promise((r) => setTimeout(r, 2000));
  }
}

let headless;
async function main() {
  if (!flag("attach")) {
    const proj = path.join(ROOT, "headless");
    spawnSync("dotnet", ["build", "-v", "q", proj], { stdio: "inherit" });
    headless = spawn("dotnet", ["run", "--no-build", "--project", proj, "--", "--port", new URL(URL_BASE).port || "7777", "--speed", String(SPEED)], { stdio: ["ignore", fs.openSync(path.join(LOGDIR, "server.log"), "a"), "inherit"] });
  }
  await waitForServer();
  const controllers = players.map((p) => (p === "ai" ? "ai" : p === "human" ? "human" : "llm"));
  if (!flag("no-restart")) await api("/api/admin/restart", { seed: SEED, speed: SPEED, controllers });
  console.log(`Pez arena: ${players.map((p, i) => `${TEAM_NAMES[i]}=${DISPLAY[p] ?? p}${opt(`model-${p}`) ? ` (${opt(`model-${p}`)}${opt(`effort-${p}`) ? " " + opt(`effort-${p}`) : ""})` : ""}`).join(" vs ")} | seed ${SEED} | speed ${SPEED} | logs ${path.relative(ROOT, LOGDIR)}`);

  const agents = players.map((p, i) => (NO_AGENT.includes(p) ? null : runAgent(i, p)));
  const started = Date.now();
  const seenChat = new Set();
  while (!over) {
    await new Promise((r) => setTimeout(r, 5000));
    let s;
    try { s = await api("/api/status"); } catch { continue; }
    const board = s.teams.map((t) => `${t.name}(${t.player ?? t.controller}) steel ${String(t.stockpile?.steel ?? 0).split(" ")[0]} S${t.structures} U${t.units} K${t.kills}${t.defeated ? " DEAD" : ""}`).join("  |  ");
    console.log(`[${String(Math.round(s.time_s)).padStart(4)}s] ${board}`);
    for (const line of s.chat ?? []) if (!seenChat.has(line)) { seenChat.add(line); console.log(`   💬 ${line}`); }
    if (s.game_over || (Date.now() - started) / 60000 > MINUTES) {
      over = true;
      const result = s.game_over ? (s.winner >= 0 ? `${TEAM_NAMES[s.winner]} (${DISPLAY[players[s.winner]]}) WINS` : "DRAW") : `time limit (${MINUTES} min) reached`;
      console.log(`\n🏁 ${result}`);
      fs.writeFileSync(path.join(LOGDIR, "result.json"), JSON.stringify({ players, seed: SEED, result, status: s }, null, 2));
    }
  }
  for (const c of children) c.kill("SIGTERM");
  headless?.kill("SIGTERM");
  await Promise.race([Promise.all(agents.filter(Boolean)), new Promise((r) => setTimeout(r, 3000))]);
  process.exit(0);
}

for (const sig of ["SIGINT", "SIGTERM"]) process.on(sig, () => { over = true; for (const c of children) c.kill("SIGTERM"); headless?.kill("SIGTERM"); process.exit(130); });
main().catch((e) => { console.error(e); headless?.kill("SIGTERM"); process.exit(1); });
