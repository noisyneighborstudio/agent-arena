// Strategy is a record, not an executor. The real gateway checks version advisories,
// exact retry receipts, authenticated human changes and the delayed spectator frame.
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
const DIR = fs.mkdtempSync(path.join(os.tmpdir(), "pezz-strategy-"));
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
      PEZZ_ENGINE: path.join(ROOT, "headless/bin/Release/net10.0/pez-headless.dll"), PEZZ_MAX_ROOMS: "1", PEZZ_ADMIN_SECRET: "strategy-test-admin-secret", PEZZ_WATCH_DELAY: "2" },
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

test("strategy records do not execute and version mismatches remain advisory", async () => {
  const a = (await call("/join", { body: { name: "StrategyA", room: "new" } })).json;
  const b = (await call("/join", { body: { name: "StrategyB", room: a.room_code } })).json;
  for (const seat of [a, b]) await call("/ready?format=json", { token: seat.token, method: "POST" });
  const state = async (seat = a) => (await call("/state?format=json", { token: seat.token })).json.state;
  const original = await state();
  assert.deepEqual(original.strategy, { text: "", priorities: [] });
  assert.equal(original.plan_version, 0);
  assert.deepEqual(original.objectives, []);
  assert.deepEqual(original.squads, []);
  const combatOrders = (s) => s.units.filter((unit) => unit.type === "rifleman").map((unit) => [unit.id, unit.order]);
  const client = new Client({ name: "strategy-test", version: "1" });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)));
  try {
    await client.callTool({ name: "rejoin", arguments: { token: a.token } });
    const execute = async (commands, metadata = {}) => {
      const r = await client.callTool({ name: "command", arguments: { commands, ...metadata, format: "json" } });
      assert.equal(r.isError, undefined, r.content[0].text);
      return JSON.parse(r.content[0].text).results;
    };
    const record = { type: "set_strategy", text: "Hold west", priorities: ["expand east", "protect miners"] };
    assert.equal((await execute([record])).results[0].ok, true);
    const recorded = await state();
    assert.deepEqual(recorded.strategy, { text: record.text, priorities: record.priorities });
    assert.equal(recorded.plan_version, 1);
    assert.deepEqual(combatOrders(recorded), combatOrders(original));
    assert.deepEqual(recorded.policies, original.policies);
    assert.deepEqual(recorded.objectives, []);
    assert.deepEqual(recorded.squads, [], "recording words must not create an executor");
    const feed = (await call("/observe/snapshot", { token: a.token })).json.snapshot;
    assert.deepEqual(feed.strategy, recorded.strategy);
    assert.equal(feed.plan_version, 1);
    assert.deepEqual((await state(b)).strategy, { text: "", priorities: [] });
    assert.equal((await state(b)).plan_version, 0);
    assert.equal((await execute([{ ...record, text: " Hold west " }])).plan_version, 1, "identical normalized record does not bump the plan");
    const invalid = await execute([{ type: "set_strategy", text: "x".repeat(2001), priorities: [] }]);
    assert.equal(invalid.results[0].ok, false, "strategy-only text bound is validated by the Sim");
    assert.equal((await state()).plan_version, 1);

    const stale = await execute([{ type: "say", text: "still execute this intent" }], { plan_version: 0 });
    assert.equal(stale.results[0].ok, true);
    assert.equal(stale.expected_plan_version, 0);
    assert.equal(stale.current_plan_version, 1);
    assert.equal(stale.plan_changed, true);
    const perCommand = await execute([{ type: "say", text: "per-command version", plan_version: 0 }], { plan_version: 1 });
    assert.equal(perCommand.plan_changed, false);
    assert.equal(perCommand.results[0].ok, true);
    assert.equal(perCommand.results[0].plan_changed, true);
    assert.equal(perCommand.results[0].expected_plan_version, 0);
    assert.equal(perCommand.results[0].current_plan_version, 1);
    const changed = await execute([{ type: "set_strategy", text: "Advance east", priorities: ["west holds"] },
      { type: "say", text: "same batch start", plan_version: 1 }], { plan_version: 1 });
    assert.equal(changed.plan_changed, false);
    assert.equal(changed.current_plan_version, 1);
    assert.equal(changed.plan_version, 2);
    assert.equal(changed.results[1].plan_changed, false, "own strategy update cannot create a false mismatch inside its batch");

    const retryBody = { request_id: "strategy-retry", plan_version: 2, commands: [{ type: "say", text: "once under this plan" }] };
    const first = (await call("/command?format=json", { token: a.token, body: retryBody })).json.results;
    const commandRetry = { type: "say", text: "one command receipt", request_id: "command-plan-retry", plan_version: 2 };
    const commandFirst = (await execute([commandRetry])).results[0];
    await execute([{ type: "set_strategy", text: "Probe the ridge", priorities: ["keep reserves"] }]);
    const commandRepeated = await execute([commandRetry]);
    assert.deepEqual(commandRepeated.results[0], commandFirst, "a per-command retry also keeps its original plan advisory");
    assert.equal(commandRepeated.plan_version, 3, "an uncached envelope reports the current final version");
    const retry = (await call("/command?format=json", { token: a.token, body: retryBody })).json.results;
    assert.deepEqual(retry, first, "old receipt retains original plan version and mismatch fields");
    assert.equal(retry.plan_version, 2);
    assert.equal((await state()).plan_version, 3);

    const human = new URL(a.commander_url);
    const ownView = human.pathname.replace(/\/web$/, "");
    const orders = { c: human.searchParams.get("c"), text: "Hold the west entrance until I return." };
    const humanReply = await call(`${ownView}/orders`, { body: orders });
    assert.equal(humanReply.json.ok, true);
    const redirected = await state();
    assert.equal(redirected.plan_version, 4);
    assert.equal(redirected.standing_orders, orders.text);
    await call(`${ownView}/orders`, { body: orders });
    assert.equal((await state()).plan_version, 4, "identical human orders do not bump the plan");
    const cleared = await execute([{ type: "set_strategy", text: "", priorities: [] }]);
    assert.equal(cleared.plan_version, 5);
    assert.deepEqual((await state()).strategy, { text: "", priorities: [] });
    assert.equal((await execute([{ type: "set_strategy", text: "", priorities: [] }])).plan_version, 5);

    // Warm this room's real spectator ring; it must stay at least two seconds behind planning changes.
    const watchRoute = `/watch/${a.room}/frame`;
    const findTeam = (frame) => frame.teams?.find((team) => team.id === original.you.team);
    await until(async () => {
      const frame = (await call(watchRoute)).json;
      return findTeam(frame)?.planning?.strategy.text === "";
    });
    const publicRecord = { type: "set_strategy", text: "Hold the eastern ridge", priorities: ["save the reserve"] };
    assert.equal((await execute([publicRecord])).plan_version, 6);
    const ownFrame = (await call(`${ownView}/frame`)).json;
    assert.equal(ownFrame.you.planning.strategy.text, publicRecord.text);
    assert.equal(ownFrame.you.planning.plan_version, 6);
    assert.ok(ownFrame.teams.filter((team) => team.id !== original.you.team).every((team) => team.planning == null), "own live frame contains no rival planning");
    const immediate = (await call(watchRoute)).json;
    assert.equal(immediate.delay_s, 2);
    assert.equal(findTeam(immediate).planning.strategy.text, "", "spectators must not receive the current plan immediately");
    const delayed = await until(async () => {
      const frame = (await call(watchRoute)).json;
      return findTeam(frame)?.planning?.strategy.text === publicRecord.text ? frame : null;
    });
    assert.equal(findTeam(delayed).planning.plan_version, 6);
    assert.deepEqual(findTeam(delayed).planning.objectives, []);
    assert.deepEqual(findTeam(delayed).planning.interventions, []);
  } finally { await client.close(); }
});

test("strategy and plan metadata pass through exactly without registration defaults", async () => {
  const player = new Player("http://unused.invalid", { team: 0 }, null);
  const sent = [];
  player.call = async (route, opts) => {
    if (route === "/api/command") { sent.push(opts.body); return JSON.stringify({ results: [{ ok: true }], plan_version: 0 }); }
    return JSON.stringify({ new_alerts: [], last_alert_seq: 0, orders_version: 0 });
  };
  const tools = new Map();
  registerPlayTools({ registerTool: (name, _, fn) => tools.set(name, fn) }, () => player);
  assert.deepEqual(sent, []);
  const commands = [{ type: "set_strategy", text: "Hold west", priorities: ["expand east"], plan_version: 4 }];
  assert.deepEqual(Command.parse(commands[0]), commands[0]);
  await tools.get("command")({ commands, plan_version: 3, request_id: "batch-plan", as_of_tick: 21, format: "json" });
  assert.deepEqual(sent[0], { commands, plan_version: 3, request_id: "batch-plan", as_of_tick: 21 });
  await tools.get("command")({ commands: [{ type: "say", text: "ordinary chat" }], format: "json" });
  assert.deepEqual(sent[1], { commands: [{ type: "say", text: "ordinary chat" }] });
});

test("plan metadata and priorities are bounded without limiting legacy chat text", () => {
  assert.equal(Command.safeParse({ type: "say", text: "x".repeat(2001) }).success, true);
  for (const plan_version of [0, 2147483647]) assert.equal(Command.safeParse({ type: "say", plan_version }).success, true);
  for (const plan_version of [-1, 0.5, 2147483648]) assert.equal(Command.safeParse({ type: "say", plan_version }).success, false);
  assert.equal(Command.safeParse({ type: "set_strategy", text: "", priorities: [] }).success, true);
  assert.equal(Command.safeParse({ type: "set_strategy", text: "plan", priorities: Array(16).fill("x".repeat(120)) }).success, true);
  assert.equal(Command.safeParse({ type: "set_strategy", text: "plan", priorities: Array(17).fill("x") }).success, false);
  assert.equal(Command.safeParse({ type: "set_strategy", text: "plan", priorities: ["x".repeat(121)] }).success, false);
});
