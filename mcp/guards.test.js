// Opt-in squad guards through MCP and the real gateway. Only explicit fields reach
// the Sim; formation, registration and omitted guards never enable safeguards.
// Every process, port and state directory belongs to this test.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { Command, Player, registerPlayTools } from "./play.js";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const DIR = fs.mkdtempSync(path.join(os.tmpdir(), "pezz-guards-"));
let gateway, port;
const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
async function FreePort() {
  const server = http.createServer();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const found = server.address().port;
  await new Promise((resolve) => server.close(resolve));
  return found;
}
async function until(fn, seconds = 20) {
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) {
    try { const value = await fn(); if (value) return value; } catch {}
    await pause(100);
  }
  throw new Error("test server did not become ready");
}
async function call(route, { token, body, headers = {}, method = body ? "POST" : "GET" } = {}) {
  const r = await fetch(`http://127.0.0.1:${port}${route}`, {
    method, headers: { ...headers, ...(token ? { authorization: `Bearer ${token}` } : {}), ...(body ? { "content-type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  return { status: r.status, json: await r.json() };
}
const result = (reply) => reply.json.results.results[0];

before(async () => {
  port = await FreePort();
  const enginePort = await FreePort();
  const home = path.join(DIR, "home");
  fs.mkdirSync(home);
  const output = fs.openSync(path.join(DIR, "gateway.log"), "w");
  gateway = spawn(process.execPath, [path.join(ROOT, "mcp/gateway.js")], {
    cwd: ROOT, stdio: ["ignore", output, output],
    env: { PATH: process.env.PATH, HOME: home, PEZZ_FRAMES: "none", PEZZ_GATEWAY_PORT: String(port), PEZZ_ROOM_BASE_PORT: String(enginePort),
      PEZZ_ENGINE: path.join(ROOT, "headless/bin/Release/net10.0/pez-headless.dll"), PEZZ_MAX_ROOMS: "1", PEZZ_ADMIN_SECRET: "squad-test-admin-secret" },
  });
  fs.closeSync(output);
  await until(async () => (await call("/lobby")).status === 200);
});

after(async () => {
  let rooms = [];
  try { rooms = JSON.parse(fs.readFileSync(path.join(DIR, "home/.config/pezz/rooms.json"), "utf8")).rooms; } catch {}
  for (const room of rooms) if (room.pid) { try { process.kill(room.pid, "SIGTERM"); } catch {} }
  gateway?.kill("SIGTERM");
  await pause(200);
});

test("MCP configures only explicit guards and exposes them in own state and feed", async () => {
  const a = (await call("/join", { body: { name: "GuardA", room: "new" } })).json;
  const b = (await call("/join", { body: { name: "GuardB", room: a.room_code } })).json;
  for (const seat of [a, b]) await call("/ready?format=json", { token: seat.token, method: "POST" });
  const state = async (seat = a) => (await call("/state?format=json", { token: seat.token })).json.state;
  const initial = await state();
  assert.deepEqual(initial.squads, []);
  const ids = initial.units.filter((unit) => unit.type === "rifleman").map((unit) => unit.id);
  const created = await call("/command?format=json", { token: a.token, body: { commands: [{ type: "squad", name: "west", add: ids }] } });
  assert.equal(result(created).ok, true);
  const guards = () => state().then((s) => s.squads.find((squad) => squad.name === "west").guards);
  const off = await guards();
  for (const key of ["retreat_below_pct", "hold_for_support", "screen", "withdraw_below_strength_pct", "to"]) assert.equal(off[key], null, `${key} defaults off`);
  assert.equal(off.withdrawn, false);
  assert.deepEqual(off.controlled, []);
  assert.deepEqual(off.interventions, []);

  const baselineFeed = (await call("/observe", { token: a.token })).json;
  const client = new Client({ name: "guard-test", version: "1" });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)));
  try {
    await client.callTool({ name: "rejoin", arguments: { token: a.token } });
    const execute = async (command) => {
      const r = await client.callTool({ name: "command", arguments: { commands: [command], format: "json" } });
      assert.equal(r.isError, undefined, r.content[0].text);
      return JSON.parse(r.content[0].text).results.results[0];
    };
    assert.equal((await execute({ type: "guard", squad: "west", hold_for_support: { within: 6 } })).ok, true);
    const support = await guards();
    assert.deepEqual(support.hold_for_support, { within: 6 });
    for (const key of ["retreat_below_pct", "screen", "withdraw_below_strength_pct", "to"]) assert.equal(support[key], null);
    const full = { type: "guard", squad: "west", retreat_below_pct: 40, hold_for_support: { within: 6 },
      screen: { roles: ["artillery"], behind: 4 }, withdraw_below_strength_pct: 50, to: [initial.you.start.x, initial.you.start.y] };
    assert.equal((await execute(full)).ok, true);
    const enabled = await guards();
    for (const key of ["retreat_below_pct", "hold_for_support", "screen", "withdraw_below_strength_pct", "to"]) assert.deepEqual(enabled[key], full[key]);
    assert.deepEqual(enabled.controlled, [], "configured guards cannot invent a route or a reason to retreat");
    const feed = (await call("/observe/snapshot", { token: a.token })).json.snapshot.squads.find((squad) => squad.name === "west").guards;
    for (const key of ["retreat_below_pct", "hold_for_support", "screen", "withdraw_below_strength_pct", "to"]) assert.deepEqual(feed[key], full[key]);
    assert.deepEqual((await state(b)).squads, [], "rival guard settings do not appear in the other player's state");
    const accepted = (await call(`/observe?cursor=${baselineFeed.next_cursor}&stream=${baselineFeed.stream}`, { token: a.token })).json.events;
    assert.ok(accepted.some((event) => event.kind === "command_accepted" && event.command.type === "guard"));
    assert.equal((await execute({ type: "guard", squad: "west", retreat_below_pct: 40 })).ok, false, "retreat cannot infer a destination");
    assert.equal((await guards()).retreat_below_pct, 40, "invalid replacement preserves the current guard config");
    assert.equal((await execute({ type: "guard", squad: "west" })).ok, true);
    const cleared = await guards();
    for (const key of ["retreat_below_pct", "hold_for_support", "screen", "withdraw_below_strength_pct", "to"]) assert.equal(cleared[key], null);
    assert.deepEqual(cleared.controlled, []);
  } finally { await client.close(); }
});

test("registration and guard transport add no safeguard defaults", async () => {
  const player = new Player("http://unused.invalid", { team: 0 }, null);
  const sent = [];
  player.call = async (route, opts) => {
    if (route === "/api/command") { sent.push(opts.body.commands[0]); return JSON.stringify({ results: [{ ok: true }] }); }
    return JSON.stringify({ new_alerts: [], last_alert_seq: 0, orders_version: 0 });
  };
  const tools = new Map();
  registerPlayTools({ registerTool: (name, _, fn) => tools.set(name, fn) }, () => player);
  assert.deepEqual(sent, []);
  for (const command of [{ type: "squad", name: "west", add: [7] }, { type: "guard", squad: "west", hold_for_support: { within: 6 } }, { type: "guard", squad: "west" }]) {
    const parsed = Command.parse(command);
    assert.deepEqual(parsed, command, "schema preserves exactly the fields the commander supplied");
    await tools.get("command")({ commands: [parsed], format: "json" });
    assert.deepEqual(sent.at(-1), command);
  }
});

test("MCP guard schema describes optional explicit thresholds, roles and fallback", () => {
  const full = { type: "guard", squad: "west", retreat_below_pct: 40, hold_for_support: { within: 6 },
    screen: { roles: ["artillery"], behind: 4 }, withdraw_below_strength_pct: 50, to: [20, 30] };
  assert.equal(Command.safeParse(full).success, true);
  assert.equal(Command.safeParse({ type: "guard", squad: "west" }).success, true);
  for (const patch of [{ retreat_below_pct: -1 }, { withdraw_below_strength_pct: 101 }, { hold_for_support: { within: 0 } },
    { screen: { roles: [], behind: 4 } }, { to: "base" }]) {
    assert.equal(Command.safeParse({ type: "guard", squad: "west", ...patch }).success, false);
  }
});
