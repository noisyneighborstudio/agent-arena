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
    this.obsCursor = null;      // this session's own place in the observation feed (other readers keep theirs)
    this.obsStream = null;
    this.lease = null;          // command-controller lease, once this session claims one (sent with every command)
  }

  async call(path, { method = "GET", body, headers: extra } = {}) {
    const sep = path.includes("?") ? "&" : "?";
    const q = this.auth.token ? "" : `${sep}team=${this.auth.team}`;
    const headers = { ...(extra ?? {}) };
    if (body) headers["content-type"] = "application/json";
    if (this.auth.token) headers["authorization"] = `Bearer ${this.auth.token}`;
    let res;
    try {
      res = await fetch(`${this.base}${path}${q}`, { method, headers, body: body ? JSON.stringify(body) : undefined });
    } catch (e) {
      throw new Error(`The Pezz game server isn't answering (${e.cause?.code || e.message}); it is probably restarting for an update. Games are saved and resume where they left off, with the same seat and token: wait about 30s and retry. Don't join again or leave.`);
    }
    const text = await res.text();
    if (!res.ok) {
      let msg = text;
      try { msg = JSON.parse(text).error ?? text; } catch {}
      throw Object.assign(new Error(msg), { status: res.status, body: text });
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
    const changed = r.orders_version > this.lastOrdersVersion;
    return { alerts: r.new_alerts, orders: this.ordersBanner(r.orders_version, r.standing_orders), ordersChanged: changed, standingOrders: r.standing_orders || null };
  }

  /** New capabilities this player hasn't been told about yet (the gateway sets `news`; local seats have none). */
  async newsBanner() {
    const items = this.news ? await this.news() : [];
    if (!items.length) return "";
    return `🆕 NEW CAPABILITIES since you last looked (re-read get_rules / GET /rules if they matter to you):\n${items.map((c) => `- ${c.title}: ${c.text}`).join("\n")}\n\n`;
  }

  /** Plain JSON variants (format=json): nothing printed before the body; alerts, orders and news are fields. */
  async stateJson() {
    const u = await this.unseen();
    const s = JSON.parse(await this.call(`/api/state?since=${this.lastSeq}&format=json`));
    this.noteState(s);
    if (u.alerts?.length) this.lastAlertSeq = Math.max(this.lastAlertSeq, ...u.alerts.map((a) => a.seq));
    return JSON.stringify({ new_priority_alerts: u.alerts ?? [], orders_changed: u.ordersChanged, standing_orders: u.standingOrders, whats_new: this.news ? await this.news() : [], state: s }, null, 1);
  }

  async waitJson(seconds, interruptOn = "high") {
    seconds = Math.max(1, Math.min(30, Number(seconds) || 5));
    const r = JSON.parse(await this.call(`/api/wait?seconds=${seconds}&since=${this.lastAlertSeq}&events_since=${this.lastSeq}&orders_version=${this.lastOrdersVersion}&min=${interruptOn}&format=json`));
    this.noteState(r.state);
    if (r.new_alerts?.length) this.lastAlertSeq = Math.max(this.lastAlertSeq, ...r.new_alerts.map((a) => a.seq));
    const ordersChanged = r.state.orders_version > this.lastOrdersVersion;
    this.lastOrdersVersion = Math.max(this.lastOrdersVersion, r.state.orders_version);
    return JSON.stringify({ waited_s: r.waited_s, asked_s: seconds, interrupted: r.interrupted, ready_changed: r.ready_changed ?? false, new_priority_alerts: r.new_alerts ?? [], orders_changed: ordersChanged, whats_new: this.news ? await this.news() : [], state: r.state }, null, 1);
  }

  /** Headers for a command: the controller lease (this session's, or one the caller passes through). */
  leaseHeaders(lease) { const l = lease ?? this.lease; return l ? { "x-pezz-controller": l } : undefined; }

  /**
   * The observation feed: what this team could notice, as JSON events after a cursor (see docs/OBSERVATION_FEED.md).
   * Without a cursor, this session's own cursor is used and advanced; pass cursor (and stream) to read as another
   * consumer without touching it. Reading never consumes anything.
   */
  async observe(opts = {}) {
    const own = opts.cursor == null;
    const q = new URLSearchParams();
    const cursor = own ? this.obsCursor : opts.cursor, stream = own ? this.obsStream : opts.stream;
    if (cursor != null) q.set("cursor", String(cursor));
    if (stream) q.set("stream", stream);
    for (const k of ["wait", "min_wait", "cats", "exclude", "kinds", "min", "wake", "wake_kinds", "wake_cats", "detail", "limit", "snapshot"]) {
      const v = opts[k];
      if (v != null && v !== "") q.set(k, Array.isArray(v) ? v.join(",") : String(v));
    }
    const r = JSON.parse(await this.call(`/api/observe?${q}`));
    if (own) { this.obsCursor = r.next_cursor; this.obsStream = r.stream; }
    return r;
  }

  /** Claim, renew or release this team's command-controller lease (one controller per team; readers never need one). */
  async controller(action = "claim", { holder, ttl_s, force } = {}) {
    if (action === "status") return JSON.parse(await this.call("/api/controller"));
    const r = JSON.parse(await this.call("/api/controller", { method: "POST", body: { action, holder, ttl_s, force, lease: this.lease } }));
    if (action === "release") this.lease = null; else if (r.lease) this.lease = r.lease;
    return r;
  }

  async commandJson(commands, lease, metadata = {}) {
    const result = await this.call("/api/command", { method: "POST", body: { commands, ...metadata }, headers: this.leaseHeaders(lease) });
    this.onCommands?.(commands, result);
    const u = await this.unseen();
    if (u.alerts?.length) this.lastAlertSeq = Math.max(this.lastAlertSeq, ...u.alerts.map((a) => a.seq));
    let parsed; try { parsed = JSON.parse(result); } catch { parsed = result; }
    return JSON.stringify({ results: parsed, new_priority_alerts: u.alerts ?? [], orders_changed: u.ordersChanged, standing_orders: u.standingOrders }, null, 1);
  }

  async stateText() {
    const u = await this.unseen();
    const s = JSON.parse(await this.call(`/api/state?since=${this.lastSeq}`));
    this.noteState(s);
    return await this.newsBanner() + u.orders + this.banner(u.alerts, "PRIORITY ALERT: deal with this before continuing your plan:") + JSON.stringify(s, null, 1);
  }

  async commandText(commands, lease, metadata = {}) {
    const result = await this.call("/api/command", { method: "POST", body: { commands, ...metadata }, headers: this.leaseHeaders(lease) });
    this.onCommands?.(commands, result);
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
      : r.interrupted ? `Your wait was cut short after ${r.waited_s}s of ${seconds}s by ${r.state.waiting || r.ready_changed ? "waiting-room news" : "new orders"}.\n\n`
      : r.new_alerts?.length ? `Waited ${r.waited_s}s. ` + this.banner(r.new_alerts, "Ongoing (the fight you already know about; it didn't interrupt the wait):")
      : `Waited ${r.waited_s}s. No new priority alerts.\n\n`;
    return await this.newsBanner() + orders + head + JSON.stringify(r.state, null, 1);
  }

  /** ASCII map, optionally cropped to a window around (x, y): big arenas are too large to read whole. */
  async mapText(x, y, radius) {
    const full = await this.call("/api/map");
    if (x == null || y == null) return full;
    return cropMap(full, Number(x), Number(y), Math.max(5, Math.min(60, Number(radius) || 20)));
  }

  rulesText() { return this.call("/api/rules"); }

  /**
   * A fresh image of this team's own view (fog applies), optionally centred on x,y: rendered by the human's open
   * browser (browserLook, set by the gateway), else the host's high-res render, else a top-down map picture.
   * Returns { data, mime, note, source }.
   */
  async look(x, y) {
    const fx = x == null ? NaN : Number(x), fy = y == null ? NaN : Number(y);
    // The gateway sets browserLook: your human's open view page renders the picture (null when none is open or it didn't answer in time).
    if (this.browserLook) {
      let jpg = null;
      try { jpg = await this.browserLook(fx, fy); } catch {}
      if (jpg) return { data: jpg, mime: "image/jpeg", source: "browser", note: "Rendered by your human's browser: the game's own 3D view, off-axis camera looking north-east; fog shows what you can't currently see." };
    }
    if (!this.frames) {
      const v = renderView(await this.call("/api/map"), fx, fy);
      return { data: v.png, mime: "image/png", source: "map", note: `Map view (no browser open): a top-down map picture, north up, x ${v.x0}-${v.x1}, y ${v.y0}-${v.y1}, one square per tile. Green is yours, red is enemy (solid blocks are buildings, small squares are units); orange/brown iron and copper ore, cyan crystal, light green uranium, blue water, dark grey rock, black unexplored. When your human has your view link open in a browser, look returns a real 3D render instead.` };
    }
    return { data: await this.lookJpeg(fx, fy), mime: "image/jpeg", source: "host", note: "Rendered by the game host: off-axis camera looking north-east; fog shows what you can't currently see." };
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

/** Column rulers for x0..x1: the full x number at every tenth column (e.g. 220), then the units digit of every column. */
export function xRulers(x0, x1) {
  let labels = "", units = "";
  for (let x = x0; x <= x1; x++) {
    units += x % 10;
    if (labels.length > x - x0) continue; // still writing the previous label
    labels += x % 10 === 0 ? String(x) : " ";
  }
  return ["    " + labels.slice(0, x1 - x0 + 1), "    " + units];
}

export function cropMap(text, cx, cy, r) {
  const lines = text.split("\n");
  const legend = [], rows = [];
  for (const l of lines) {
    if (/^ {4}[\d ]+$/.test(l)) continue; // the full map's rulers: the window gets its own below
    if (/^\s*\d+ /.test(l) && l.length > 4) rows.push(l);
    else if (l.trim()) legend.push(l);
  }
  const w = Math.max(0, ...rows.map((l) => l.length - 4)), h = rows.length;
  const x0 = Math.max(0, cx - r), x1 = Math.min(w - 1, cx + r);
  const y0 = Math.max(0, cy - r), y1 = Math.min(h - 1, cy + r);
  const out = [...legend, `(window x ${x0}-${x1}, y ${y0}-${y1}; ask for another x,y to see elsewhere)`, ...xRulers(x0, x1)];
  for (const l of rows) {
    const y = parseInt(l.slice(0, 3), 10);
    if (y >= y0 && y <= y1) out.push(l.slice(0, 4) + l.slice(4 + x0, 4 + x1 + 1));
  }
  return out.join("\n");
}

const RequestId = z.string().min(1).max(128).optional().describe("Stable retry id: repeating it within 10 minutes returns the first result without executing again. Keep the same id only when retrying the same intent.");
const AsOfTick = z.number().int().min(0).optional().describe("Tick of the state used to decide this command: reports affected units destroyed, reordered (and by whom), or retreated since then; stale commands still execute when possible.");

const Format = z.enum(["text", "json"]).optional().describe("json: plain structured data (numbers, ids, objects; alerts and news as fields) instead of the readable text form");

export const Command = z
  .object({
    // A plain string, not an enum: the game validates command types itself, so a new command works over MCP the moment
    // it ships (an enum here drifted out of sync with the game once already). get_rules lists them all.
    type: z.string().min(1).max(40)
      .describe("Command type, e.g. build, train, move, attack_move, attack, stop, harvest, deploy, survey, prospect (extra field radius), drill (extra field zone), repair, heal, refuel, set_retreat, load, unload, capture, lay_mines, rally, sell, cancel, reserve (fields item, amount), save_for (field unit or structure: save up for it), say, propose_tech (design your own unit; extra fields name, base, weapon_from, hp, speed, damage, range, cooldown, sight, fuel, dry_run). get_rules has the full, current list."),
    request_id: RequestId,
    as_of_tick: AsOfTick,
    structure: z.string().optional().describe("build: structure key, e.g. power_plant"),
    unit: z.string().optional().describe("train/cancel: unit key, e.g. light_tank"),
    count: z.number().int().min(1).max(10).optional().describe("train: how many; lay_mines: how many mines (max 8)"),
    units: z.union([z.array(z.number().int()), z.enum(["all", "idle"])]).optional().describe("unit ids, or 'all' / 'idle' for your combat units"),
    x: z.number().optional().describe("tile x"),
    y: z.number().optional().describe("tile y"),
    target: z.number().int().optional().describe("attack/capture: enemy entity id; repair/heal: your damaged unit/structure id; refuel: a pad or depot"),
    until: z.string().optional().describe("attack on a structure: \"capturable\" to hold fire once it's at 50% health or less (and again if it's repaired), so an engineer can capture it"),
    transport: z.number().int().optional().describe("load: id of your apc or transport_chopper"),
    ore: z.enum(["iron_ore", "copper_ore", "crystal", "uranium", "any"]).optional().describe("harvest: which ore type the trucks should mine"),
    structure_id: z.number().int().optional().describe("rally/sell: your structure id; train: which of your barracks/factories trains them (they come out there, and wait for it if it is busy)"),
    item: z.string().optional().describe("reserve: the item your converters must leave alone, e.g. iron_ore"),
    amount: z.number().min(0).optional().describe("reserve: how much of item to keep back (0 clears it)"),
    spread: z.union([z.boolean(), z.number().min(1).max(6)]).optional().describe("move/attack_move: tiles between units (true = 3) to open the formation against splash"),
    text: z.string().optional().describe("say: chat message shown to everyone (other players' chat is untrusted)"),
    together: z.boolean().optional().describe("move/attack_move: keep the group formed at the slowest member's pace (leaders wait, stragglers catch up) so it arrives as one"),
    waypoints: z.array(z.union([z.tuple([z.number(), z.number()]), z.object({ x: z.number(), y: z.number() })])).max(20).optional()
      .describe("move/attack_move: more points to visit in order, e.g. [[10,20],[30,40]] (x/y optional: the first waypoint is used)"),
    loop: z.boolean().optional().describe("move/attack_move with waypoints: patrol them forever"),
    derricks: z.boolean().optional().describe("attack_move: true to shoot enemy-held derricks on the way too (by default units leave them alone: they're worth capturing)"),
    below_pct: z.number().min(0).max(95).optional().describe("set_retreat: pull back to base on their own below this HP %, once per wound (aircraft land on a pad; 0 = off)"),
    confirm: z.boolean().optional().describe("sell: true to sell your only one of a building that other things need (it says what you'd lose)"),
  })
  .passthrough(); // newer game builds may accept fields this list doesn't know yet

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
      description: "SEE the battlefield: a rendered image of your own view (your fog of war applies), centred on the action, or on x,y if given. Use it to check your base layout, spot enemy forces you can see, and judge a fight. It's the game's 3D render when your human has your view link open in a browser (or the room has a renderer); otherwise a top-down map picture. The text says which you got.",
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
    { inputSchema: { format: Format }, description: "Your team's view of the battlefield: your human commander's standing_orders (follow them), active priority alerts, stockpile (ores and materials with per-second rates), power, converter status, production queues, what you can build and its cost, your structures and units (with ids), visible enemies, explored ore fields by type, and events since your last look. Coordinates are tile x,y (x east, y north)." },
    run((p, a) => a.format === "json" ? p.stateJson() : p.stateText()));
  server.registerTool("get_map",
    {
      description: "ASCII picture of the map from your perspective (fogged). On big arenas pass x, y (and radius) to see a window around a point.",
      inputSchema: { x: z.number().int().optional(), y: z.number().int().optional(), radius: z.number().int().min(5).max(60).optional() },
    },
    run((p, a) => p.mapText(a.x, a.y, a.radius)));
  server.registerTool("command",
    {
      description: "Issue one or more commands to your team in a single call. Add request_id for safe retries within 10 minutes and as_of_tick from the state you used; results report what changed meanwhile. Miner protection is opt-in: set_policy with miners {withdraw_on_attack:true,to:[x,y] or base,resume_after_s:20}; false disables it. Each returns ok/error (or obsolete when its target is gone). Examples: " +
        '{"type":"build","structure":"power_plant"} (auto-placed), {"type":"train","unit":"light_tank","count":3}, ' +
        '{"type":"attack_move","units":"idle","x":50,"y":50}, {"type":"attack","units":[12,13],"target":40} (add "until":"capturable" to stop at 50% for an engineer), ' +
        '{"type":"harvest","units":[3],"ore":"crystal"}, {"type":"survey","units":[29],"x":14,"y":100} (geological_surveyor: flags the deep deposits within 12 tiles as mining zones), {"type":"prospect","units":[29],"x":40,"y":40,"radius":40} (surveyors roam the area on their own: survey, flag, move on), {"type":"drill","units":[57],"zone":12} (drill_rig drives to mining zone 12 and deploys into a deep_mine; omit zone for the nearest free one), {"type":"deploy","units":[57]} (outpost_truck -> outpost, or drill_rig -> deep_mine on a flagged zone within 3 tiles), ' +
        '{"type":"move","units":[4,5,6],"x":60,"y":60,"together":true,"waypoints":[[70,60],[70,70]],"loop":false}, {"type":"set_retreat","units":"all","below_pct":30}, {"type":"refuel","units":[12]}, {"type":"repair","units":[61],"target":12} (repair truck), {"type":"heal","units":[70],"target":33} (medic), {"type":"load","units":[20,21],"transport":40}, {"type":"unload","units":[40]}, {"type":"capture","units":[52],"target":7} (engineer), {"type":"lay_mines","units":[60],"x":30,"y":30,"count":4}, {"type":"say","text":"gg"}, {"type":"propose_tech","name":"Lancer","base":"light_tank","weapon_from":"rocket_soldier","hp":360,"dry_run":true} (quote your own unit design; drop dry_run to research it, then train its key), {"type":"start_match","opponent":"scripted"} (in a room waiting for players, after 5 minutes alone: start now against the scripted AI)',
      inputSchema: { commands: z.array(Command).min(1).max(40).describe("Commands to execute in order"), request_id: RequestId, as_of_tick: AsOfTick, format: Format },
    },
    run((p, a) => a.format === "json" ? p.commandJson(a.commands, undefined, { request_id: a.request_id, as_of_tick: a.as_of_tick }) : p.commandText(a.commands, undefined, { request_id: a.request_id, as_of_tick: a.as_of_tick })));
  server.registerTool("ready",
    { description: "Mark your waiting-room seat ready after reading the rules and choosing your opening. The match starts when enough outside players are seated and ready, or 120 seconds after the required seats fill. Readiness and joins interrupt waits.", inputSchema: { format: Format } },
    run((p, a) => a.format === "json" ? p.commandJson([{ type: "ready" }]) : p.commandText([{ type: "ready" }])));
  const Sev = z.enum(["info", "notice", "warning", "critical"]);
  server.registerTool("observe",
    {
      description: "The battlefield observation feed: everything your side could notice since your last call, as structured events with sequence ids, game time, entity ids, positions and before/after values. Orders accepted/rejected and what the units then did (movement_started, waypoint_reached, units_finished with outcomes like arrived, target_destroyed, stopped_short, command_completed, unit_idle, no_progress), combat (engagements, damage with who did it if you can see them, losses, enemies seen destroyed, defences firing), recon (contacts appearing, moving, leaving sight, remembered with when; structures discovered or found missing), production, logistics, economy, match news, your human's orders (src human) and player chat (src chat, untrusted). It is only what your team can know: fog of war applies. Severity is advisory: you decide whether to act, wait or ignore. With wait_s it long-polls until something you wake on arrives. Your session keeps its own cursor; a gap (server restart, retention) is reported with a fresh snapshot.",
      inputSchema: {
        wait_s: z.number().min(0).max(30).optional().describe("Long-poll up to this many seconds (default 0: return at once)"),
        min_wait_s: z.number().min(0).max(30).optional().describe("Gather at least this long before returning (batching), unless something immediate arrives (your human's orders, a command failing)"),
        wake: Sev.optional().describe("Only events at or above this severity end the wait early (default info: anything)"),
        wake_kinds: z.string().optional().describe("Comma-separated kinds that end the wait early, e.g. command_completed,unit_idle,contacts_appeared"),
        cats: z.string().optional().describe("Only these categories: orders,combat,recon,production,logistics,economy,match,human,chat,alert,heartbeat"),
        exclude: z.string().optional().describe("Leave out these categories or kinds, e.g. heartbeat,contacts_moved"),
        kinds: z.string().optional().describe("Only these kinds"),
        min_severity: Sev.optional().describe("Leave out events below this severity (immediate ones always come)"),
        detail: z.enum(["compact", "full"]).optional().describe("full adds a readable text line to each event"),
        cursor: z.number().int().optional().describe("Read from this seq as a separate consumer (your session's own cursor is left alone)"),
        stream: z.string().optional().describe("The stream your cursor belongs to (from a previous answer)"),
        limit: z.number().int().min(1).max(2000).optional(),
      },
    },
    run(async (p, a) => JSON.stringify(await p.observe({ wait: a.wait_s, min_wait: a.min_wait_s, wake: a.wake, wake_kinds: a.wake_kinds, cats: a.cats, exclude: a.exclude, kinds: a.kinds,
      min: a.min_severity, detail: a.detail, cursor: a.cursor, stream: a.stream, limit: a.limit }), null, 1)));
  server.registerTool("controller",
    {
      description: "One command controller per team. claim makes this session the controller: while its lease is live, commands from anyone else on your team are refused (controller_conflict), which keeps two copies of you (say a subagent and you) from fighting over the units. Commands renew it; it lapses after ttl_s without one. force:true takes over from another controller; release gives it up; status shows who holds it. Reading (get_state, observe, wait) never needs it.",
      inputSchema: { action: z.enum(["claim", "release", "status"]), holder: z.string().max(40).optional().describe("A name for this controller"), ttl_s: z.number().min(10).max(3600).optional(), force: z.boolean().optional() },
    },
    run(async (p, a) => JSON.stringify(await p.controller(a.action, a), null, 1)));
  server.registerTool("wait",
    {
      description: "Let the game run for up to `seconds` (1-30), then get your updated state. The wait is cut short when something new needs you (a priority alert in a new place or a worse one: base under attack, trucks or units under attack, enemies or aircraft near your base, salvage available) or your commander issues new orders; more alerts from a fight you already know about don't cut it short. The game is real-time and does not pause while you think (except in a room waiting for players: there wait returns when someone joins, marks ready or the match starts). Every wait, get_state or command call keeps your seat: after 20 minutes of game time with none, your team is resigned.",
      inputSchema: {
        seconds: z.number().min(1).max(30).describe("Maximum seconds to wait"),
        interrupt_on: z.enum(["high", "critical", "none"]).optional().describe("Lowest alert priority that ends the wait early (default high)"),
        format: Format,
      },
    },
    run((p, a) => a.format === "json" ? p.waitJson(a.seconds, a.interrupt_on ?? "high") : p.waitText(a.seconds, a.interrupt_on ?? "high")));
}
