#!/usr/bin/env node
// MCP server that lets an LLM command one team in Pezz RTS.
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
let lastSeq = 0;       // last game event seen (events are reported as deltas)
let lastAlertSeq = 0;  // last priority alert already shown to the model
let lastTick = 0;      // detects a game restart, which resets the server's sequence numbers
let lastOrdersVersion = 0; // last version of the human commander's standing orders already shown

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
    throw new Error(`Pezz game is not reachable at ${BASE} (${e.cause?.code || e.message}). Is the game running?`);
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

function noteState(s) {
  if (s.tick < lastTick) { lastSeq = 0; lastAlertSeq = 0; lastOrdersVersion = 0; } // new game
  lastTick = s.tick;
  lastSeq = s.last_event_seq ?? lastSeq;
}

function banner(alerts, lead) {
  if (!alerts?.length) return "";
  lastAlertSeq = Math.max(lastAlertSeq, ...alerts.map((a) => a.seq));
  return `⚠️ ${lead}\n${alerts.map((a) => `- [#${a.seq}] ${a.text}`).join("\n")}\n\n`;
}

function ordersBanner(version, text) {
  if (version <= lastOrdersVersion) return "";
  lastOrdersVersion = version;
  return text
    ? `📣 NEW ORDERS FROM YOUR HUMAN COMMANDER (these take precedence over your own plans; follow them until they change):\n${text}\n\n`
    : "📣 Your human commander has cleared their standing orders. Use your own judgment.\n\n";
}

/** Priority alerts (high or critical) the model hasn't been shown yet, plus any new commander's orders. */
async function unseen() {
  const r = JSON.parse(await call(`/api/alerts?since=${lastAlertSeq}&min=high`));
  if (r.last_alert_seq < lastAlertSeq || r.orders_version < lastOrdersVersion) { lastAlertSeq = 0; lastOrdersVersion = 0; return unseen(); } // new game
  return { alerts: r.new_alerts, orders: ordersBanner(r.orders_version, r.standing_orders) };
}

async function stateText() {
  const u = await unseen();
  const s = JSON.parse(await call(`/api/state?since=${lastSeq}`));
  noteState(s);
  return u.orders + banner(u.alerts, "PRIORITY ALERT: deal with this before continuing your plan:") + JSON.stringify(s, null, 1);
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
      "Your team's view of the battlefield: your human commander's standing_orders (follow them), active priority alerts, stockpile (ores and materials with per-second rates), power, converter status, production queues, what you can build and its cost, your structures and units (with ids), visible enemies, explored ore fields by type, and events since your last get_state/wait. Coordinates are tile x,y (x east, y north).",
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
      .enum(["build", "train", "move", "attack_move", "attack", "stop", "harvest", "deploy", "repair", "heal", "load", "unload", "capture", "lay_mines", "rally", "sell", "cancel", "say"])
      .describe("Command type"),
    structure: z.string().optional().describe("build: structure key, e.g. power_plant"),
    unit: z.string().optional().describe("train/cancel: unit key, e.g. light_tank"),
    count: z.number().int().min(1).max(10).optional().describe("train: how many; lay_mines: how many mines (max 8)"),
    units: z
      .union([z.array(z.number().int()), z.enum(["all", "idle"])])
      .optional()
      .describe("unit ids, or 'all' / 'idle' for your combat units"),
    x: z.number().optional().describe("tile x"),
    y: z.number().optional().describe("tile y"),
    target: z.number().int().optional().describe("attack/capture: enemy entity id; repair/heal: your damaged unit/structure id"),
    transport: z.number().int().optional().describe("load: id of your apc or transport_chopper"),
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
      '{"type":"harvest","units":[3],"ore":"crystal"}, {"type":"deploy","units":[57]} (outpost_truck -> outpost), {"type":"repair","units":[61],"target":12} (repair truck), {"type":"heal","units":[70],"target":33} (medic), {"type":"load","units":[20,21],"transport":40}, {"type":"unload","units":[40]}, {"type":"capture","units":[52],"target":7} (engineer), {"type":"lay_mines","units":[60],"x":30,"y":30,"count":4}, {"type":"say","text":"gg"}',
    inputSchema: { commands: z.array(Command).min(1).describe("Commands to execute in order") },
  },
  async ({ commands }) => {
    await ensureJoined();
    const result = await call("/api/command", { method: "POST", body: { commands } });
    const u = await unseen();
    return text(u.orders + banner(u.alerts, "NEW PRIORITY ALERT since your last look:") + result);
  },
);

server.registerTool(
  "wait",
  {
    description:
      "Let the game run for up to `seconds` (1-30), then get your updated state. The wait is cut short the moment a priority alert " +
      "is raised for your team (base under attack, trucks or units under attack, enemies near your base, stealth bomber detected), " +
      "like a human hearing an alarm. The game is real-time and does not pause while you think.",
    inputSchema: {
      seconds: z.number().min(1).max(30).describe("Maximum seconds to wait"),
      interrupt_on: z.enum(["high", "critical", "none"]).optional().describe("Lowest alert priority that ends the wait early (default high)"),
    },
  },
  async ({ seconds, interrupt_on }) => {
    await ensureJoined();
    const r = JSON.parse(await call(`/api/wait?seconds=${seconds}&since=${lastAlertSeq}&events_since=${lastSeq}&orders_version=${lastOrdersVersion}&min=${interrupt_on ?? "high"}`));
    noteState(r.state);
    const orders = ordersBanner(r.state.orders_version, r.state.standing_orders === "none" ? "" : r.state.standing_orders);
    const head = r.new_alerts?.length
      ? banner(r.new_alerts, `PRIORITY ALERT: your wait was cut short after ${r.waited_s}s of ${seconds}s. Respond to this first:`)
      : r.interrupted ? `Your wait was cut short after ${r.waited_s}s of ${seconds}s by new orders.\n\n` : `Waited ${r.waited_s}s. No new priority alerts.\n\n`;
    return text(orders + head + JSON.stringify(r.state, null, 1));
  },
);

await server.connect(new StdioServerTransport());
