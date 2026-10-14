// Named squads through the real gateway and Sim: addressing, own-only projection,
// explicit membership and producer rally enrollment. No formation behavior is enabled.
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
const DIR = fs.mkdtempSync(path.join(os.tmpdir(), "pezz-squads-"));
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

test("named squads only address their members and remain private to their side", async () => {
  const a = (await call("/join", { body: { name: "SquadA", room: "new" } })).json;
  const b = (await call("/join", { body: { name: "SquadB", room: a.room_code } })).json;
  for (const seat of [a, b]) await call("/ready?format=json", { token: seat.token, method: "POST" });
  const state = async (seat = a) => (await call("/state?format=json", { token: seat.token })).json.state;
  const command = async (commands, seat = a) => {
    const reply = await call("/command?format=json", { token: seat.token, body: { commands } });
    assert.equal(reply.status, 200);
    return reply.json.results.results;
  };
  const initial = await state();
  assert.deepEqual(initial.squads, [], "no squads exist unless the player creates them");
  const army = initial.units.filter((unit) => unit.type === "rifleman");
  assert.ok(army.length > 1);
  const ids = [army[0].id];
  assert.equal((await command([{ type: "squad", name: "west", add: ids }]))[0].ok, true);
  const formed = await state();
  const west = formed.squads.find((squad) => squad.name === "west");
  assert.deepEqual(west.members, ids);
  assert.equal(west.strength_pct, 100);
  assert.equal(west.hp, west.formation_hp);
  assert.equal(west.objective, null, "naming a squad creates no objective");
  assert.ok(Array.isArray(west.centre));
  assert.equal(formed.units.find((unit) => unit.id === ids[0]).order, army[0].order, "membership itself never issues an order");
  assert.equal((await command([{ type: "set_retreat", squad: "west", below_pct: 40 }]))[0].ok, true);
  let after = await state();
  assert.equal(after.units.find((unit) => unit.id === ids[0]).retreat_below_pct, 40);
  assert.ok(after.units.filter((unit) => !ids.includes(unit.id)).every((unit) => !unit.retreat_below_pct), "a squad selector never affects the rest of the army");
  const missing = await command([{ type: "set_retreat", units: "squad:missing", below_pct: 60 }]);
  assert.equal(missing[0].ok, false);
  after = await state();
  assert.ok(after.units.filter((unit) => !ids.includes(unit.id)).every((unit) => !unit.retreat_below_pct), "an unknown squad never means all");
  const enemy = await state(b);
  const enemyIds = [enemy.units.find((unit) => unit.type === "rifleman").id];
  assert.equal((await command([{ type: "squad", name: "east", add: enemyIds }], b))[0].ok, true);
  const feed = (await call("/observe/snapshot", { token: a.token })).json.snapshot;
  assert.deepEqual(feed.squads.map((squad) => squad.name), ["west"]);
  assert.deepEqual((await state(b)).squads.map((squad) => squad.name), ["east"]);

  const client = new Client({ name: "squad-test", version: "1" });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)));
  try {
    await client.callTool({ name: "rejoin", arguments: { token: a.token } });
    for (const selector of [{ squad: "west" }, { units: "squad:west" }]) {
      const reply = await client.callTool({ name: "command", arguments: { commands: [{ type: "set_retreat", ...selector, below_pct: 50 }], format: "json" } });
      assert.equal(reply.isError, undefined, "both selectors pass the public MCP schema");
      assert.equal(JSON.parse(reply.content[0].text).results.results[0].ok, true);
    }
  } finally { await client.close(); }

  // Build and train through player commands; accelerated time belongs to this test room alone.
  const speed = await call(`/admin/rooms/${a.room}/api/admin/speed`, { body: { speed: 20 }, headers: { "x-pezz-admin": "squad-test-admin-secret" } });
  assert.equal(speed.status, 200);
  async function build(type) {
    await until(async () => (await command([{ type: "build", structure: type }]))[0].ok, 30);
    return until(async () => (await state()).structures.find((structure) => structure.type === type && structure.complete), 30);
  }
  await build("power_plant");
  await build("mining_refinery");
  const barracks = await build("barracks");
  assert.equal((await command([{ type: "rally", structure_id: barracks.id, squad: "west", x: west.centre[0], y: west.centre[1] }]))[0].ok, true);
  const oldIds = new Set((await state()).units.map((unit) => unit.id));
  await until(async () => (await command([{ type: "train", structure_id: barracks.id, unit: "rifleman", count: 1 }]))[0].ok, 30);
  const recruited = await until(async () => (await state()).units.find((unit) => unit.type === "rifleman" && !oldIds.has(unit.id)), 30);
  assert.ok((await state()).squads.find((squad) => squad.name === "west").members.includes(recruited.id), "new units explicitly rallied to a squad enroll there");
  const [removed] = await command([{ type: "squad", name: "west", remove: [recruited.id] }]);
  assert.equal(removed.ok, true);
  assert.ok(!(await state()).squads.find((squad) => squad.name === "west").members.includes(recruited.id));
  assert.equal((await command([{ type: "squad", name: "west", disband: true }]))[0].ok, true);
  assert.deepEqual((await state()).squads, []);
});

test("MCP squad selectors retain bounded names and existing selectors", () => {
  for (const units of ["all", "idle", "squad:west", "squad:" + "x".repeat(24), [1, 2]]) {
    assert.equal(Command.safeParse({ type: "stop", units }).success, true);
  }
  for (const units of ["squad:", "squad:" + "x".repeat(25), "unknown-selector"]) {
    assert.equal(Command.safeParse({ type: "stop", units }).success, false);
  }
  assert.equal(Command.safeParse({ type: "move", squad: "west", x: 1, y: 2 }).success, true);
  assert.equal(Command.safeParse({ type: "move", squad: "", x: 1, y: 2 }).success, false);
});
