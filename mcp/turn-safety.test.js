// P1 contract through the real gateway and Sim: readiness wakes a wait without a new seat,
// retry ids reach the engine intact, and MCP exposes the same explicit controls.
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
const DIR = fs.mkdtempSync(path.join(os.tmpdir(), "pezz-turn-safety-"));
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
      PEZZ_ENGINE: path.join(ROOT, "headless/bin/Release/net10.0/pez-headless.dll"), PEZZ_MAX_ROOMS: "1" },
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

test("waiting room readiness, HTTP retry ids, and MCP retry metadata", async () => {
  const first = await call("/join", { body: { name: "TurnA", room: "new", min_players: 2 } });
  assert.equal(first.status, 200);
  const a = first.json;
  assert.equal(a.ready, false);
  assert.equal(a.waiting.ready_players, 0);
  assert.equal(a.waiting.ready_seats[0].ready, false);
  const second = await call("/join", { body: { name: "TurnB", room: a.room_code } });
  const b = second.json;
  assert.equal(b.room_status, "waiting", "the second seat alone must not start the match");
  assert.ok(b.waiting.start_after_s > 110 && b.waiting.start_after_s <= 120);
  const room = (await call("/lobby")).json.rooms[0];
  assert.equal(room.ready_players, 0);
  assert.ok(room.waiting_ready_s > 110);
  assert.equal(room.teams.every((team) => team.ready === false), true);

  const wait = call("/wait?seconds=10&format=json&interrupt=none", { token: b.token });
  await pause(200);
  const ready = await call("/ready?format=json", { token: a.token, method: "POST" });
  assert.equal(result(ready).ok, true);
  const woke = await wait;
  assert.equal(woke.json.interrupted, true);
  assert.equal(woke.json.ready_changed, true);
  assert.equal(woke.json.state.waiting.ready_players, 1);
  assert.ok(woke.json.waited_s < 5, "readiness wakes a wait with unchanged seating");

  const client = new Client({ name: "turn-safety-test", version: "1" });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)));
  try {
    const tools = (await client.listTools()).tools;
    assert.ok(tools.some((tool) => tool.name === "ready"));
    const schema = tools.find((tool) => tool.name === "command").inputSchema.properties;
    assert.ok(schema.request_id && schema.as_of_tick);
    await client.callTool({ name: "rejoin", arguments: { token: b.token } });
    const started = JSON.parse((await client.callTool({ name: "ready", arguments: { format: "json" } })).content[0].text);
    assert.equal(started.results.results[0].ok, true);
    await until(async () => !(await call("/state?format=json", { token: a.token })).json.state.waiting);

    const snapshot = (await call("/observe/snapshot", { token: a.token })).json.snapshot;
    const unit = snapshot.units[0];
    assert.ok(unit);
    const body = { request_id: "http-retry-1", as_of_tick: 0, commands: [{ type: "set_retreat", units: [unit.id], below_pct: 40 }] };
    const original = await call("/command?format=json", { token: a.token, body });
    assert.equal(result(original).ok, true);
    const repeated = await call("/command?format=json", { token: a.token, body });
    assert.deepEqual(repeated.json.results, original.json.results, "the gateway preserves batch retry identity");
    await until(async () => (await call("/state?format=json", { token: a.token })).json.state.tick > 0);
    const lease = (await call("/controller", { token: a.token, body: { holder: "TurnA-planner" } })).json.lease;
    const headers = { "x-pezz-controller": lease };
    const move = { type: "move", units: [unit.id], x: unit.pos[0] + 2, y: unit.pos[1] };
    const ordered = await call("/command?format=json", { token: a.token, headers, body: { commands: [move] } });
    assert.equal(result(ordered).ok, true);
    const stale = await call("/command?format=json", { token: a.token, headers, body: { as_of_tick: 0, commands: [{ type: "stop", units: [unit.id] }] } });
    assert.equal(result(stale).ok, true, "a stale command still executes");
    assert.ok(result(stale).stale.some((fact) => fact.unit === unit.id && fact.changes.some((change) => change.kind === "ordered" && change.by === "TurnA-planner")));
    const obsolete = await call("/command?format=json", { token: a.token, headers, body: { commands: [{ type: "attack", units: [unit.id], target: 2147483647 }] } });
    assert.equal(result(obsolete).code, "obsolete");
    assert.equal(result(obsolete).why, "target_unavailable");
    const perCommand = { commands: [{ type: "say", request_id: "single-retry", text: "one command id" }] };
    const single1 = await call("/command?format=json", { token: a.token, headers, body: perCommand });
    const single2 = await call("/command?format=json", { token: a.token, headers, body: perCommand });
    assert.deepEqual(result(single2), result(single1));
    const args = { request_id: "mcp-retry-1", as_of_tick: 0, commands: [{ type: "say", text: "one deliberate command" }], format: "json" };
    const m1 = JSON.parse((await client.callTool({ name: "command", arguments: args })).content[0].text);
    const m2 = JSON.parse((await client.callTool({ name: "command", arguments: args })).content[0].text);
    assert.deepEqual(m2.results, m1.results, "MCP forwards batch retry identity too");
  } finally { await client.close(); }
});

test("shared gameplay tools forward metadata unchanged and never ready a player implicitly", async () => {
  const player = new Player("http://unused.invalid", { team: 0 }, null);
  const sent = [];
  player.call = async (route, opts) => {
    if (route === "/api/command") { sent.push(opts.body); return JSON.stringify({ results: [{ ok: true }] }); }
    return JSON.stringify({ new_alerts: [], last_alert_seq: 0, orders_version: 0 });
  };
  const tools = new Map();
  registerPlayTools({ registerTool: (name, _, fn) => tools.set(name, fn) }, () => player);
  assert.deepEqual(sent, []);
  const commands = [{ type: "move", units: [7], request_id: "unit-retry", as_of_tick: 17, x: 8, y: 9 }];
  await tools.get("command")({ commands, request_id: "batch-retry", as_of_tick: 12, format: "json" });
  assert.deepEqual(sent[0], { commands, request_id: "batch-retry", as_of_tick: 12 });
  await tools.get("ready")({ format: "json" });
  assert.deepEqual(sent[1], { commands: [{ type: "ready" }] });
});


test("MCP preserves the explicit miner policy without enabling it during tool registration", async () => {
  const player = new Player("http://unused.invalid", { team: 0 }, null);
  const sent = [];
  player.call = async (route, opts) => {
    if (route === "/api/command") { sent.push(opts.body); return JSON.stringify({ results: [{ ok: true }] }); }
    return JSON.stringify({ new_alerts: [], last_alert_seq: 0, orders_version: 0 });
  };
  const tools = new Map();
  registerPlayTools({ registerTool: (name, _, fn) => tools.set(name, fn) }, () => player);
  assert.equal(sent.length, 0);
  const policy = Command.parse({ type: "set_policy", miners: { withdraw_on_attack: true, to: [8, 9], resume_after_s: 20 } });
  await tools.get("command")({ commands: [policy], format: "json" });
  assert.deepEqual(JSON.parse(JSON.stringify(sent[0])), { commands: [{ type: "set_policy", miners: { withdraw_on_attack: true, to: [8, 9], resume_after_s: 20 } }] });
});
