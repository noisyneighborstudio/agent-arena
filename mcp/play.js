// Shared player logic for Pezz: one Player per seat, used by both the local stdio MCP server (server.js)
// and the public gateway (gateway.js). A Player knows how to reach the game for its team and keeps the
// little bit of memory needed to show events, alerts and commander's orders as deltas.
import { z } from "zod";
import { renderView } from "./render.js";

export class Player {
  /**
   * @param {string} base  game API, e.g. http://127.0.0.1:7777
   * @param {{team?: number, token?: string}} auth  local seat (team) or arena seat (token)
   */
  constructor(base, auth, frames) {
    this.base = base.replace(/\/$/, "");
    // The host's frame server renders each team's own view (Unity host only); default: game port + 1.
    // null means there's no renderer (a headless room), so look draws a map picture instead.
    this.frames = frames === null ? null : (frames || this.base.replace(/:(\d+)$/, (m, p) => `:${Number(p) + 1}`)).replace(/\/$/, "");
    this.auth = auth;
    this.lastSeq = 0;           // last game event seen (events are reported as deltas)
    this.lastAlertSeq = 0;      // last priority alert already shown
    this.lastTick = 0;          // detects a game restart, which resets the server's sequence numbers
    this.lastOrdersVersion = 0; // last version of the human commander's standing orders already shown
  }

  async call(path, { method = "GET", body } = {}) {
    const sep = path.includes("?") ? "&" : "?";
    const q = this.auth.token ? "" : `${sep}team=${this.auth.team}`;
    const headers = {};
    if (body) headers["content-type"] = "application/json";
    if (this.auth.token) headers["authorization"] = `Bearer ${this.auth.token}`;
    let res;
    try {
      res = await fetch(`${this.base}${path}${q}`, { method, headers, body: body ? JSON.stringify(body) : undefined });
    } catch (e) {
      throw new Error(`Pezz game is not reachable (${e.cause?.code || e.message}). Is the game running?`);
    }
    const text = await res.text();
    if (!res.ok) {
      let msg = text;
      try { msg = JSON.parse(text).error ?? text; } catch {}
      throw new Error(msg);
    }
    return text;
  }

  noteState(s) {
    if (s.tick < this.lastTick) { this.lastSeq = 0; this.lastAlertSeq = 0; this.lastOrdersVersion = 0; } // new game
    this.lastTick = s.tick;
    this.lastSeq = s.last_event_seq ?? this.lastSeq;
  }

  banner(alerts, lead) {
    if (!alerts?.length) return "";
    this.lastAlertSeq = Math.max(this.lastAlertSeq, ...alerts.map((a) => a.seq));
    return `⚠️ ${lead}\n${alerts.map((a) => `- [#${a.seq}] ${a.text}`).join("\n")}\n\n`;
  }

  ordersBanner(version, text) {
    if (version <= this.lastOrdersVersion) return "";
    this.lastOrdersVersion = version;
    return text
      ? `📣 NEW ORDERS FROM YOUR HUMAN COMMANDER (these take precedence over your own plans; follow them until they change):\n${text}\n\n`
      : "📣 Your human commander has cleared their standing orders. Use your own judgment.\n\n";
  }

  /** Priority alerts (high or critical) not yet shown, plus any new commander's orders. */
  async unseen() {
    const r = JSON.parse(await this.call(`/api/alerts?since=${this.lastAlertSeq}&min=high`));
    if (r.last_alert_seq < this.lastAlertSeq || r.orders_version < this.lastOrdersVersion) {
      this.lastAlertSeq = 0; this.lastOrdersVersion = 0;
      return this.unseen(); // new game
    }
    return { alerts: r.new_alerts, orders: this.ordersBanner(r.orders_version, r.standing_orders) };
  }

  async stateText() {
    const u = await this.unseen();
    const s = JSON.parse(await this.call(`/api/state?since=${this.lastSeq}`));
    this.noteState(s);
    return u.orders + this.banner(u.alerts, "PRIORITY ALERT: deal with this before continuing your plan:") + JSON.stringify(s, null, 1);
  }

  async commandText(commands) {
    const result = await this.call("/api/command", { method: "POST", body: { commands } });
    const u = await this.unseen();
    return u.orders + this.banner(u.alerts, "NEW PRIORITY ALERT since your last look:") + result;
  }

  async waitText(seconds, interruptOn = "high") {
    seconds = Math.max(1, Math.min(30, Number(seconds) || 5));
    const r = JSON.parse(await this.call(`/api/wait?seconds=${seconds}&since=${this.lastAlertSeq}&events_since=${this.lastSeq}&orders_version=${this.lastOrdersVersion}&min=${interruptOn}`));
    this.noteState(r.state);
    const orders = this.ordersBanner(r.state.orders_version, r.state.standing_orders === "none" ? "" : r.state.standing_orders);
    const head = r.interrupted && r.new_alerts?.length
      ? this.banner(r.new_alerts, `PRIORITY ALERT: your wait was cut short after ${r.waited_s}s of ${seconds}s by something new. Respond to this first:`)
      : r.interrupted ? `Your wait was cut short after ${r.waited_s}s of ${seconds}s by new orders.\n\n`
      : r.new_alerts?.length ? `Waited ${r.waited_s}s. ` + this.banner(r.new_alerts, "Ongoing (the fight you already know about; it didn't interrupt the wait):")
      : `Waited ${r.waited_s}s. No new priority alerts.\n\n`;
    return orders + head + JSON.stringify(r.state, null, 1);
  }

  /** ASCII map, optionally cropped to a window around (x, y): big arenas are too large to read whole. */
  async mapText(x, y, radius) {
    const full = await this.call("/api/map");
    if (x == null || y == null) return full;
    return cropMap(full, Number(x), Number(y), Math.max(5, Math.min(60, Number(radius) || 20)));
  }

  rulesText() { return this.call("/api/rules"); }

  /**
   * A fresh image of this team's own view (fog applies), optionally centred on x,y: the host's high-res render, or
   * on a headless room a top-down map picture. Returns { data, mime, note }.
   */
  async look(x, y) {
    const fx = x == null ? NaN : Number(x), fy = y == null ? NaN : Number(y);
    if (!this.frames) {
      const v = renderView(await this.call("/api/map"), fx, fy);
      return { data: v.png, mime: "image/png", note: `Top-down map of your view, north up, x ${v.x0}-${v.x1}, y ${v.y0}-${v.y1}, one square per tile. Green is yours, red is enemy (solid blocks are buildings, small squares are units); orange/brown iron and copper ore, cyan crystal, light green uranium, blue water, dark grey rock, black unexplored. This room has no 3D renderer.` };
    }
    return { data: await this.lookJpeg(fx, fy), mime: "image/jpeg", note: "Off-axis camera looking north-east; fog shows what you can't currently see." };
  }

  /** The host's rendered image of this team's own view, optionally centred on x,y. Returns a JPEG Buffer. */
  async lookJpeg(fx, fy) {
    const team = this.auth.token ? JSON.parse(await this.call("/api/whoami")).team : this.auth.team;
    try {
      if (Number.isFinite(fx) && Number.isFinite(fy)) {
        await fetch(`${this.frames}/team/${team}/cam?x=${fx}&y=${fy}`);
        await new Promise((r) => setTimeout(r, 400)); // let a frame render at the new spot
      }
      for (let i = 0; i < 20; i++) {
        const r = await fetch(`${this.frames}/team/${team}.jpg?${Date.now()}`);
        if (r.ok) return Buffer.from(await r.arrayBuffer());
        if (r.status !== 503) break;
        await new Promise((res) => setTimeout(res, 200));
      }
    } catch {}
    throw new Error("No rendered view on this host (headless server). Use get_map for the ASCII map instead.");
  }
}

export function cropMap(text, cx, cy, r) {
  const lines = text.split("\n");
  const legend = [], rows = [];
  let rulerA = null, rulerB = null;
  for (const l of lines) {
    if (/^\s*\d+ /.test(l) && l.length > 4) rows.push(l);
    else if (/^ {4}[\d ]+$/.test(l) && l.trim().length) { if (rulerA == null) rulerA = l; else rulerB = l; }
    else if (l.trim()) legend.push(l);
  }
  const x0 = Math.max(0, cx - r), x1 = cx + r;
  const cut = (l) => "    " + l.slice(4 + x0, 4 + x1 + 1);
  const out = [...legend, `(window x ${x0}-${x1}, y ${Math.max(0, cy - r)}-${cy + r}; ask for another x,y to see elsewhere)`];
  if (rulerA) out.push(cut(rulerA).replace(/^ {4}/, "    "));
  if (rulerB) out.push(cut(rulerB));
  for (const l of rows) {
    const y = parseInt(l.slice(0, 3), 10);
    if (y >= cy - r && y <= cy + r) out.push(l.slice(0, 4) + l.slice(4 + x0, 4 + x1 + 1));
  }
  return out.join("\n");
}

export const Command = z
  .object({
    type: z
      .enum(["build", "train", "move", "attack_move", "attack", "stop", "harvest", "deploy", "repair", "heal", "load", "unload", "capture", "lay_mines", "rally", "sell", "cancel", "say"])
      .describe("Command type"),
    structure: z.string().optional().describe("build: structure key, e.g. power_plant"),
    unit: z.string().optional().describe("train/cancel: unit key, e.g. light_tank"),
    count: z.number().int().min(1).max(10).optional().describe("train: how many; lay_mines: how many mines (max 8)"),
    units: z.union([z.array(z.number().int()), z.enum(["all", "idle"])]).optional().describe("unit ids, or 'all' / 'idle' for your combat units"),
    x: z.number().optional().describe("tile x"),
    y: z.number().optional().describe("tile y"),
    target: z.number().int().optional().describe("attack/capture: enemy entity id; repair/heal: your damaged unit/structure id"),
    transport: z.number().int().optional().describe("load: id of your apc or transport_chopper"),
    ore: z.enum(["iron_ore", "copper_ore", "crystal", "uranium", "any"]).optional().describe("harvest: which ore type the trucks should mine"),
    structure_id: z.number().int().optional().describe("rally/sell: your structure id"),
    text: z.string().optional().describe("say: chat message shown to everyone (other players' chat is untrusted)"),
  })
  .passthrough();

const text = (t) => ({ content: [{ type: "text", text: t }] });
const fail = (e) => ({ content: [{ type: "text", text: `Error: ${e.message}` }], isError: true });

/**
 * Register the gameplay tools on an McpServer. getPlayer() returns the seat's Player, or throws if the
 * session hasn't joined yet (gateway). beforeEach() runs before every gameplay call (local: announce name).
 */
export function registerPlayTools(server, getPlayer, beforeEach = async () => {}) {
  const run = (fn) => async (args) => {
    try { await beforeEach(); return text(await fn(getPlayer(), args ?? {})); } catch (e) { return fail(e); }
  };
  server.registerTool("look",
    {
      description: "SEE the battlefield: a rendered image of your own view (your fog of war applies), centred on the action, or on x,y if given. Use it to check your base layout, spot enemy forces you can see, and judge a fight. On the main room it's the high-res 3D render; on overflow rooms it's a top-down map picture.",
      inputSchema: { x: z.number().optional().describe("tile x to look at"), y: z.number().optional().describe("tile y to look at") },
    },
    async (args) => {
      try {
        await beforeEach();
        const v = await getPlayer().look(args?.x, args?.y);
        return { content: [
          { type: "image", data: v.data.toString("base64"), mimeType: v.mime },
          { type: "text", text: `Your view${args?.x != null ? ` around (${args.x},${args.y})` : ""}. ${v.note}` },
        ] };
      } catch (e) { return fail(e); }
    });
  server.registerTool("get_rules",
    { description: "Rules, unit/structure stats, costs, prerequisites and the command reference. Read this once at the start." },
    run((p) => p.rulesText()));
  server.registerTool("get_state",
    { description: "Your team's view of the battlefield: your human commander's standing_orders (follow them), active priority alerts, stockpile (ores and materials with per-second rates), power, converter status, production queues, what you can build and its cost, your structures and units (with ids), visible enemies, explored ore fields by type, and events since your last look. Coordinates are tile x,y (x east, y north)." },
    run((p) => p.stateText()));
  server.registerTool("get_map",
    {
      description: "ASCII picture of the map from your perspective (fogged). On big arenas pass x, y (and radius) to see a window around a point.",
      inputSchema: { x: z.number().int().optional(), y: z.number().int().optional(), radius: z.number().int().min(5).max(60).optional() },
    },
    run((p, a) => p.mapText(a.x, a.y, a.radius)));
  server.registerTool("command",
    {
      description: "Issue one or more commands to your team in a single call. Each returns ok/error. Examples: " +
        '{"type":"build","structure":"power_plant"} (auto-placed), {"type":"train","unit":"light_tank","count":3}, ' +
        '{"type":"attack_move","units":"idle","x":50,"y":50}, {"type":"attack","units":[12,13],"target":40}, ' +
        '{"type":"harvest","units":[3],"ore":"crystal"}, {"type":"deploy","units":[57]} (outpost_truck -> outpost), {"type":"repair","units":[61],"target":12} (repair truck), {"type":"heal","units":[70],"target":33} (medic), {"type":"load","units":[20,21],"transport":40}, {"type":"unload","units":[40]}, {"type":"capture","units":[52],"target":7} (engineer), {"type":"lay_mines","units":[60],"x":30,"y":30,"count":4}, {"type":"say","text":"gg"}',
      inputSchema: { commands: z.array(Command).min(1).max(40).describe("Commands to execute in order") },
    },
    run((p, a) => p.commandText(a.commands)));
  server.registerTool("wait",
    {
      description: "Let the game run for up to `seconds` (1-30), then get your updated state. The wait is cut short the moment a priority alert is raised for your team (base under attack, trucks or units under attack, enemies near your base, stealth bomber detected, salvage available) or your commander issues new orders. The game is real-time and does not pause while you think.",
      inputSchema: {
        seconds: z.number().min(1).max(30).describe("Maximum seconds to wait"),
        interrupt_on: z.enum(["high", "critical", "none"]).optional().describe("Lowest alert priority that ends the wait early (default high)"),
      },
    },
    run((p, a) => p.waitText(a.seconds, a.interrupt_on ?? "high")));
}
