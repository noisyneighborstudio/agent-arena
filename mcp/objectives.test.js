// Literal objectives through MCP and an isolated real gateway/engine. Configuration
// is explicitly supplied, and decision packets remain owned by the commanding side.
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
const DIR = fs.mkdtempSync(path.join(os.tmpdir(), "pezz-objectives-"));
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

test("MCP objectives are opt-in, own-side and pause with a critical decision packet", async () => {
  const a = (await call("/join", { body: { name: "ObjectiveA", room: "new" } })).json;
  const b = (await call("/join", { body: { name: "ObjectiveB", room: a.room_code } })).json;
  for (const seat of [a, b]) await call("/ready?format=json", { token: seat.token, method: "POST" });
  const state = async (seat = a) => (await call("/state?format=json", { token: seat.token })).json.state;
  const initial = await state();
  assert.deepEqual(initial.objectives, [], "joining never creates an objective");
  const ids = initial.units.filter((unit) => unit.type === "rifleman").map((unit) => unit.id);
  const formed = await call("/command?format=json", { token: a.token, body: { commands: [{ type: "squad", name: "west", add: ids }] } });
  assert.equal(result(formed).ok, true);
  assert.deepEqual((await state()).objectives, [], "squad formation never creates an objective");
  const point = [initial.units.find((unit) => unit.id === ids[0]).x, initial.units.find((unit) => unit.id === ids[0]).y];
  const client = new Client({ name: "objective-test", version: "1" });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)));
  try {
    await client.callTool({ name: "rejoin", arguments: { token: a.token } });
    const execute = async (command) => {
      const r = await client.callTool({ name: "command", arguments: { commands: [command], format: "json" } });
      assert.equal(r.isError, undefined, r.content[0].text);
      return JSON.parse(r.content[0].text).results.results[0];
    };
    const invalidRadius = await execute({ type: "objective", id: "invalid-radius", squad: "west", verb: "hold", at: point, radius: 0 });
    assert.equal(invalidRadius.ok, false);
    assert.match(invalidRadius.error, /radius.*positive/);
    assert.deepEqual((await state()).objectives, [], "the Sim rejects an invalid objective radius before creating state");
    const legacyProspect = await execute({ type: "prospect", units: ids, radius: -1 });
    assert.equal(legacyProspect.ok, false);
    assert.match(legacyProspect.error, /no surveyors given/, "legacy numeric prospect arguments still reach the Sim's command validation");
    const full = { type: "objective", id: "west-exp", squad: "west", verb: "hold", at: point, radius: 6,
      assemble: { at: point, min: 8 }, reinforce: { keep: { rifleman: 1 }, budget: { steel: 800 } },
      wake: ["squad_below_50", "enemy_strength_seen>squad", "objective_done"] };
    assert.equal((await execute(full)).ok, true, "the documented indefinite hold can carry objective_done without inventing completion");
    const configured = (await state()).objectives.find((objective) => objective.id === full.id);
    for (const key of ["id", "squad", "verb", "at", "radius", "wake"]) assert.deepEqual(configured[key], full[key]);
    assert.deepEqual(configured.reinforce.keep, full.reinforce.keep);
    assert.deepEqual(configured.reinforce.budget, full.reinforce.budget);
    assert.equal(configured.paused, false);
    assert.equal((await state()).squads.find((squad) => squad.name === "west").objective, full.id);
    const snapshot = (await call("/observe/snapshot", { token: a.token })).json.snapshot;
    assert.ok(snapshot.objectives.some((objective) => objective.id === full.id));
    assert.deepEqual((await state(b)).objectives, [], "rival objective configuration never appears in state");
    assert.deepEqual((await call("/observe/snapshot", { token: b.token })).json.snapshot.objectives, []);
    assert.equal((await execute({ type: "stop", units: [ids[0]] })).ok, true);
    assert.ok((await state()).objectives.find((objective) => objective.id === full.id).detached.includes(ids[0]), "direct orders detach the named member from objective execution");
    assert.equal((await execute({ type: "objective", id: full.id, pause: true })).ok, true);
    assert.equal((await state()).objectives.find((objective) => objective.id === full.id).paused, true);
    assert.equal((await execute({ type: "objective", id: full.id, resume: true })).ok, true);
    assert.equal((await state()).objectives.find((objective) => objective.id === full.id).paused, false);
    assert.deepEqual((await state()).objectives.find((objective) => objective.id === full.id).detached, [], "explicit resume hands current squad members back");
    assert.equal((await execute({ type: "objective", id: full.id, cancel: true })).ok, true);
    assert.deepEqual((await state()).objectives, []);

    const baseline = (await call("/observe", { token: a.token })).json;
    const rivalBaseline = (await call("/observe", { token: b.token })).json;
    const waiting = client.callTool({ name: "observe", arguments: { cursor: baseline.next_cursor, stream: baseline.stream,
      kinds: "decision_needed", wake: "critical", wait_s: 3 } });
    const go = await call("/command?format=json", { token: a.token, body: { commands: [{ type: "objective", id: "arrival", squad: "west",
      verb: "go", at: point, radius: 6, wake: ["objective_done"] }] } });
    assert.equal(result(go).ok, true);
    const response = await waiting;
    assert.equal(response.isError, undefined, response.content[0].text);
    const event = JSON.parse(response.content[0].text).events.find((event) => event.kind === "decision_needed");
    assert.ok(event, "go arrival wakes the critical observer with a decision");
    assert.equal(event.sev, "critical");
    const paused = (await state()).objectives.find((objective) => objective.id === "arrival");
    assert.equal(paused.paused, true, "a decision packet pauses execution until commanded otherwise");
    assert.equal(paused.decision.reason, "objective_done");
    assert.equal(paused.decision.objective.id, "arrival");
    assert.ok(paused.decision.threats && paused.decision.commitments && paused.decision.changed_since_command);
    const rivalRead = (await call(`/observe?cursor=${rivalBaseline.next_cursor}&stream=${rivalBaseline.stream}`, { token: b.token })).json;
    assert.equal(rivalRead.events.some((event) => event.kind === "decision_needed"), false);
  } finally { await client.close(); }
});

test("objective transport preserves literal intent and never creates defaults", async () => {
  const player = new Player("http://unused.invalid", { team: 0 }, null);
  const sent = [];
  player.call = async (route, opts) => {
    if (route === "/api/command") { sent.push(opts.body.commands[0]); return JSON.stringify({ results: [{ ok: true }] }); }
    return JSON.stringify({ new_alerts: [], last_alert_seq: 0, orders_version: 0 });
  };
  const tools = new Map();
  registerPlayTools({ registerTool: (name, _, fn) => tools.set(name, fn) }, () => player);
  assert.deepEqual(sent, []);
  const commands = [
    { type: "squad", name: "west", add: [7] },
    { type: "objective", id: "west-exp", squad: "west", verb: "hold", at: [40, 40], radius: 6,
      assemble: { at: [35, 35], min: 8 }, reinforce: { keep: { rifleman: 6, light_tank: 2 }, budget: { steel: 800 } },
      wake: ["squad_below_50", "enemy_strength_seen>squad"] },
    { type: "objective", id: "west-exp", squad: "west", verb: "go", at: [40, 40], radius: 6 },
    { type: "objective", id: "escort", squad: "west", verb: "escort", target_id: 3, radius: 6 },
    { type: "objective", id: "escort-squad", squad: "west", verb: "escort", target_squad: "miners", radius: 6 },
    { type: "objective", id: "patrol", squad: "west", verb: "patrol", waypoints: [[20, 20], [30, 30]], radius: 6, wake: [] },
    { type: "objective", id: "west-exp", pause: true },
    { type: "objective", id: "west-exp", resume: true },
    { type: "objective", id: "west-exp", cancel: true },
  ];
  for (const command of commands) {
    const parsed = Command.parse(command);
    assert.deepEqual(parsed, command, "schema does not supply an assembly, cap, target or wake condition");
    await tools.get("command")({ commands: [parsed], format: "json" });
    assert.deepEqual(sent.at(-1), command);
  }
  assert.equal(sent.length, commands.length);
});

test("MCP objective schema bounds literal verbs and explicit assembly/budget shapes", () => {
  const base = { type: "objective", id: "west-exp", squad: "west", verb: "hold", at: [20, 20], radius: 6 };
  assert.equal(Command.safeParse(base).success, true);
  for (const wake of ["squad_below_1", "squad_below_100", "objective_done"]) {
    assert.equal(Command.safeParse({ ...base, verb: "go", wake: [wake] }).success, true);
  }
  assert.equal(Command.safeParse({ ...base, reinforce: { keep: { rifleman: 0 }, budget: { steel: 0 } } }).success, true);
  for (const patch of [{ verb: "secure_mining_area" }, { assemble: { at: [20, 20], min: 0 } },
    { reinforce: { keep: { rifleman: 1.5 }, budget: { steel: 80 } } },
    { reinforce: { keep: { rifleman: 2 }, budget: { steel: -1 } } },
    { reinforce: { keep: { rifleman: 2 }, budget: { steel: 80.5 } } },
    { reinforce: { keep: { rifleman: 2 }, budget: { steel: Infinity } } }, { wake: ["choose_a_target"] }, { wake: ["squad_below_0"] }, { wake: ["squad_below_101"] }]) {
    assert.equal(Command.safeParse({ ...base, ...patch }).success, false);
  }
});

test("legacy prospect radius values retain passthrough to command-specific Sim validation", () => {
  for (const radius of [-40, -1, 0, 6, 40, 1000000]) {
    for (const units of ["all", "idle", "squad:west", [7, 8]]) {
      const command = { type: "prospect", units, radius };
      assert.deepEqual(Command.parse(command), command);
    }
  }
  assert.deepEqual(Command.parse({ type: "prospect", units: [7] }), { type: "prospect", units: [7] });
});
