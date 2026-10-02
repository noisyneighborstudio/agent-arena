#!/usr/bin/env node
// MCP server that lets an LLM command one team in Pez RTS.
// Env: PEZ_TEAM (required, 0-based), PEZ_PLAYER (display name, e.g. "Claude"), PEZ_URL (default http://127.0.0.1:7777)
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const BASE = (process.env.PEZ_URL || "http://127.0.0.1:7777").replace(/\/$/, "");
const TEAM = Number(process.env.PEZ_TEAM ?? NaN);
const PLAYER = process.env.PEZ_PLAYER || "LLM";
if (!Number.isInteger(TEAM)) {
  console.error("PEZ_TEAM must be set to your team index (0, 1, ...)");
  process.exit(1);
}

let joined = false;
let lastSeq = 0;

async function call(path, { method = "GET", body } = {}) {
  const sep = path.includes("?") ? "&" : "?";
  let res;
  try {
    res = await fetch(`${BASE}${path}${sep}team=${TEAM}`, {
      method,
      headers: body ? { "content-type": "application/json" } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });
  } catch (e) {
    throw new Error(`Pez game is not reachable at ${BASE} (${e.cause?.code || e.message}). Is the game running?`);
  }
  const text = await res.text();
  if (!res.ok) throw new Error(`HTTP ${res.status}: ${text}`);
  return text;
}

async function ensureJoined() {
  if (joined) return;
  await call("/api/join", { method: "POST", body: { name: PLAYER } });
  joined = true;
}

const text = (t) => ({ content: [{ type: "text", text: t }] });

async function stateText() {
  const s = JSON.parse(await call(`/api/state?since=${lastSeq}`));
  lastSeq = s.last_event_seq ?? lastSeq;
  return JSON.stringify(s, null, 1);
}

const server = new McpServer({ name: "pez-rts", version: "0.1.0" });

server.registerTool(
  "get_rules",
  { description: "Rules, unit/structure stats, costs, prerequisites and the command reference. Read this once at the start." },
  async () => text(await call("/api/rules")),
);

server.registerTool(
  "get_state",
  {
    description:
      "Your team's view of the battlefield: stockpile (ores and materials with per-second rates), power, converter status, production queues, what you can build and its cost, your structures and units (with ids), visible enemies, explored ore fields by type, and events since your last get_state/wait. Coordinates are tile x,y (x east, y north).",
  },
  async () => {
    await ensureJoined();
    return text(await stateText());
  },
);

server.registerTool(
  "get_map",
  { description: "ASCII picture of the whole map from your perspective (fogged). Use it to plan placement and routes." },
  async () => {
    await ensureJoined();
    return text(await call("/api/map"));
  },
);

const Command = z
  .object({
    type: z
      .enum(["build", "train", "move", "attack_move", "attack", "stop", "harvest", "deploy", "rally", "sell", "cancel", "say"])
      .describe("Command type"),
    structure: z.string().optional().describe("build: structure key, e.g. power_plant"),
    unit: z.string().optional().describe("train/cancel: unit key, e.g. light_tank"),
    count: z.number().int().min(1).max(10).optional().describe("train: how many"),
    units: z
      .union([z.array(z.number().int()), z.enum(["all", "idle"])])
      .optional()
      .describe("unit ids, or 'all' / 'idle' for your combat units"),
    x: z.number().optional().describe("tile x"),
    y: z.number().optional().describe("tile y"),
    target: z.number().int().optional().describe("attack: enemy entity id"),
    ore: z.enum(["iron_ore", "copper_ore", "crystal", "uranium", "any"]).optional().describe("harvest: which ore type the trucks should mine"),
    structure_id: z.number().int().optional().describe("rally/sell: your structure id"),
    text: z.string().optional().describe("say: chat message shown on screen to everyone"),
  })
  .passthrough();

server.registerTool(
  "command",
  {
    description:
      "Issue one or more commands to your team in a single call. Each returns ok/error. Examples: " +
      '{"type":"build","structure":"power_plant"} (auto-placed), {"type":"train","unit":"light_tank","count":3}, ' +
      '{"type":"attack_move","units":"idle","x":50,"y":50}, {"type":"attack","units":[12,13],"target":40}, ' +
      '{"type":"harvest","units":[3],"ore":"crystal"}, {"type":"deploy","units":[57]} (outpost_truck -> outpost), {"type":"say","text":"gg"}',
    inputSchema: { commands: z.array(Command).min(1).describe("Commands to execute in order") },
  },
  async ({ commands }) => {
    await ensureJoined();
    return text(await call("/api/command", { method: "POST", body: { commands } }));
  },
);

server.registerTool(
  "wait",
  {
    description:
      "Let the game run for a few seconds (1-30), then get your updated state. The game is real-time and does not pause while you think, so use this when you have nothing to do right now.",
    inputSchema: { seconds: z.number().min(1).max(30).describe("Seconds to wait") },
  },
  async ({ seconds }) => {
    await ensureJoined();
    await new Promise((r) => setTimeout(r, seconds * 1000));
    return text(await stateText());
  },
);

await server.connect(new StdioServerTransport());
