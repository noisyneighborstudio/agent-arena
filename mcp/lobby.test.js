// Lobby and placement (#36) and the gateway side of waiting rooms (#37): placement order, private rooms listed without
// their code, the sudden-death 409, room:"new" options and how they reach the engine. Runs a throwaway gateway (temp
// HOME) against stub engines: tiny node servers that answer /api/status, /api/lobby and /api/register in the engine's
// shapes. Room 1's stub is started here; rooms the gateway opens run the same stub as PEZZ_ENGINE.
//
//   node --test mcp/lobby.test.js
//
// Env: PEZZ_TEST_LOBBY_PORT (room 1's stub; the gateway takes the next port, default 7960), PEZZ_TEST_DIR (default /tmp/pezz-lobby-test).
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { placementOrder } from "./rooms.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOM1 = Number(process.env.PEZZ_TEST_LOBBY_PORT || 7960);
const GW = ROOM1 + 1;
const DIR = process.env.PEZZ_TEST_DIR || "/tmp/pezz-lobby-test";
const STUBS = path.join(DIR, "stubs"); // each stub engine writes engine-<port>.json here: its pid and command line
const children = [];

// The stub engine: the lobby and register contract, with POST /stub to set its state from the test.
const STUB = `#!${process.execPath}
const http = require("node:http"), fs = require("node:fs"), { randomBytes } = require("node:crypto");
const args = process.argv.slice(2), arg = (k) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : null; };
const port = Number(arg("--port")), min = Number(arg("--min-players")) || null;
fs.writeFileSync(process.env.PEZZ_STUB_DIR + "/engine-" + port + ".json", JSON.stringify({ pid: process.pid, args }));
const s = { status: min ? "waiting" : "playing", joinable: true, why: null, verdict: "fresh", verdict_text: "fresh room", age_s: 0,
  match_hours: Number(arg("--match-hours")) || 4, sudden_death_in_s: 14400, ends_in_s: min ? null : 18000, next_match_in_s: null, teams: [] };
const outside = () => s.teams.filter((t) => !t.house).length;
const hex = () => randomBytes(24).toString("hex");
const reply = (res, code, body) => { res.writeHead(code, { "content-type": "application/json" }); res.end(JSON.stringify(body)); };
http.createServer((req, res) => {
  let b = ""; req.on("data", (c) => (b += c)); req.on("end", () => {
    const body = b ? JSON.parse(b) : {};
    if (req.url === "/api/status") return reply(res, 200, { ok: true });
    if (req.url === "/stub") { Object.assign(s, body); return reply(res, 200, { ok: true }); }
    if (req.url === "/api/lobby") return reply(res, 200, { open: true, rules_version: 30, players: s.teams.length, max_players: 8, seats_free: 8 - s.teams.length,
      ...s, ...(s.status === "waiting" ? { min_players: min, waiting_s: 10 } : {}) });
    if (req.url === "/api/register") {
      if (s.status === "closing" || s.status === "results") return reply(res, 409, { ok: false, error: "room_closed_to_joins: sudden death has started", code: "room_closed_to_joins", next_match_in_s: s.next_match_in_s });
      if (s.teams.length >= 8) return reply(res, 409, { ok: false, error: "the arena is full" });
      s.teams.push({ flavor: "Lime", team_name: "Lime", player: body.name, status: "playing", house: false, active: true, idle_s: 0, score: 0, structures: 1, kills: 0 });
      const waiting = s.status === "waiting" && outside() < min;
      if (s.status === "waiting" && !waiting) s.status = "playing";
      return reply(res, 200, { ok: true, token: hex(), view_token: hex(), team: s.teams.length - 1, flavor: "Lime", team_name: "Lime", name: body.name, base: "10,10 sector A8", map: "64x64",
        match: { phase: "normal" }, match_summary: "3h to sudden death", room_status: waiting ? "waiting" : "playing",
        ...(waiting ? { waiting: { players: outside(), min_players: min, waited_s: 0, choose_after_s: 300 } } : {}) });
    }
    reply(res, 404, { ok: false, error: "not found" });
  });
}).listen(port, "127.0.0.1");
`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(what, fn, ms = 20000) {
  const t0 = Date.now();
  for (;;) { let v; try { v = await fn(); } catch {} if (v) return v; if (Date.now() - t0 > ms) throw new Error(`timed out waiting for ${what}`); await sleep(200); }
}
async function call(port, pathname, { method = "GET", body, ip = "10.0.0.1" } = {}) {
  const r = await fetch(`http://127.0.0.1:${port}${pathname}`, { method, headers: { "content-type": "application/json", "x-forwarded-for": ip }, body: body ? JSON.stringify(body) : undefined });
  return { status: r.status, json: await r.json() };
}
const join = (body, ip) => call(GW, "/join", { method: "POST", body: { name: "Tester", ...body }, ip });
const stub = (port, state) => call(port, "/stub", { method: "POST", body: state });
const lobby = async () => (await call(GW, "/lobby")).json.rooms;
/** The stub engine the gateway started for a room (its port and command line), found by the room's port in the gateway's state. */
function engineOf(roomId) {
  const saved = JSON.parse(fs.readFileSync(path.join(DIR, "home", ".config", "pezz", "rooms.json"), "utf8")).rooms.find((r) => r.id === roomId);
  return { saved, ...JSON.parse(fs.readFileSync(path.join(STUBS, `engine-${saved.port}.json`), "utf8")) };
}

before(async () => {
  fs.rmSync(DIR, { recursive: true, force: true });
  fs.mkdirSync(STUBS, { recursive: true });
  fs.mkdirSync(path.join(DIR, "home"), { recursive: true });
  const engine = path.join(DIR, "stub-engine");
  fs.writeFileSync(engine, STUB, { mode: 0o755 });
  const env = { PATH: process.env.PATH, HOME: path.join(DIR, "home"), PEZZ_STUB_DIR: STUBS };
  const out = fs.openSync(path.join(DIR, "room1.log"), "w");
  children.push(spawn(engine, ["--port", String(ROOM1)], { env, stdio: ["ignore", out, out] }));
  await until("room 1's stub", async () => (await call(ROOM1, "/api/status")).status === 200);
  const gout = fs.openSync(path.join(DIR, "gateway.log"), "w");
  children.push(spawn(process.execPath, [path.join(HERE, "gateway.js")], { env: { ...env, PEZZ_GAME: `http://127.0.0.1:${ROOM1}`, PEZZ_FRAMES: "none",
    PEZZ_GATEWAY_PORT: String(GW), PEZZ_ENGINE: engine, PEZZ_MAX_ROOMS: "3", PEZZ_ADMIN_SECRET: "test-secret-test-secret" }, stdio: ["ignore", gout, gout] }));
  await until("the gateway", async () => (await call(GW, "/lobby")).status === 200);
});

after(async () => {
  const pids = fs.readdirSync(STUBS).map((f) => JSON.parse(fs.readFileSync(path.join(STUBS, f), "utf8")).pid);
  for (const c of children) { try { c.kill("SIGTERM"); } catch {} }
  for (const pid of pids) { try { process.kill(pid, "SIGTERM"); } catch {} } // rooms the gateway opened (detached)
});

test("placement order: public, not late, not closing; more active players, then more time; waiting rooms after", () => {
  const p = (room, o) => ({ room, status: "playing", joinable: true, private: false, verdict: "midgame", sudden_death_in_s: 3600, teams: [], ...o });
  const active = (n) => [...Array(n)].map(() => ({ house: false, active: true })).concat([{ house: true, active: true }, { house: false, active: false }]);
  const order = placementOrder([
    p(1, { teams: active(1) }),
    p(2, { verdict: "late" }),
    p(3, { status: "closing", joinable: false }),
    p(4, { status: "waiting", teams: active(1) }),
    p(5, { private: true, teams: active(5) }),
    p(6, { teams: active(3), sudden_death_in_s: 600 }),
    p(7, { teams: active(1), sudden_death_in_s: 9000 }),
    p(8, { status: "results", joinable: false }),
    p(9, { status: "waiting", private: true }),
  ]).map((x) => x.room);
  assert.deepEqual(order, [6, 7, 1, 4]);
});

test("room:\"new\" options are checked, and only go with room:\"new\"", async () => {
  for (const [i, [body, re]] of [[{ room: "new", match_hours: 3 }, /match_hours must be 1, 2 or 4/], [{ room: "new", match_hours: "4" }, /match_hours/],
    [{ room: "new", min_players: 1 }, /min_players must be/], [{ room: "new", min_players: 9 }, /min_players/], [{ room: "new", min_players: 2.5 }, /min_players/],
    [{ room: "new", private: "yes" }, /private must be true or false/], [{ match_hours: 2 }, /with "room":"new"/]].entries()) {
    const r = await join(body, `10.1.0.${i}`);
    assert.equal(r.status, 400, JSON.stringify(body));
    assert.match(r.json.error, re);
  }
});

let privateRoom, waitingRoom;
test("a private waiting room: the engine gets its options, the lobby hides its code, the reply explains the wait", async () => {
  const r = await join({ room: "new", match_hours: 2, min_players: 3, private: true }, "10.2.0.1");
  assert.equal(r.status, 200, JSON.stringify(r.json));
  privateRoom = r.json.room;
  assert.equal(r.json.room_status, "waiting");
  assert.deepEqual(r.json.waiting, { players: 1, min_players: 3, waited_s: 0, choose_after_s: 300 });
  assert.match(r.json.note, /private/);
  assert.match(r.json.note, /"start_match","opponent":"scripted"/);
  const e = engineOf(privateRoom);
  assert.deepEqual(e.args.slice(e.args.indexOf("--match-hours")), ["--match-hours", "2", "--min-players", "3"]);
  assert.equal(e.saved.private, true);
  assert.equal(e.saved.matchHours, 2);
  assert.equal(e.saved.minPlayers, 3);
  const listed = (await lobby()).find((x) => x.room === privateRoom);
  assert.equal(listed.code, null);
  assert.equal(listed.private, true);
  assert.equal(listed.status, "waiting");
  assert.equal(listed.min_players, 3);
  assert.equal((await lobby()).find((x) => x.room === 1).code.startsWith("pezz-"), true, "public rooms keep their code");
});

test("placement: a playing room that isn't late; then (late) never the private room but a new public waiting room, then that one", async () => {
  await stub(ROOM1, { verdict: "midgame", verdict_text: "2h10m to sudden death; 1 active player; the leader has 41% of the points." });
  const a = await join({}, "10.3.0.1");
  assert.equal(a.json.room, 1, JSON.stringify(a.json));
  assert.equal(a.json.room_status, "playing");
  assert.equal(a.json.verdict, "midgame");
  assert.match(a.json.note, /2h10m to sudden death/);

  await stub(ROOM1, { verdict: "late", verdict_text: "20m to sudden death; 1 active player; the leader has 80% of the points." });
  const b = await join({}, "10.3.0.2");
  assert.equal(b.status, 200, JSON.stringify(b.json));
  assert.ok(b.json.room !== 1 && b.json.room !== privateRoom, `a new room, not ${b.json.room}`);
  waitingRoom = b.json.room;
  assert.equal(b.json.room_status, "waiting");
  const e = engineOf(waitingRoom);
  assert.deepEqual(e.args.slice(e.args.indexOf("--match-hours")), ["--match-hours", "4", "--min-players", "2"], "the gateway's own rooms take the defaults");
  assert.equal(e.saved.private, false);

  const c = await join({}, "10.3.0.3");
  assert.equal(c.json.room, waitingRoom, "the public waiting room before opening another");
  assert.equal(c.json.room_status, "playing", "the second player starts it");
});

test("a code join to a late room works and says what it walked into", async () => {
  const code = (await lobby()).find((x) => x.room === 1).code;
  const r = await join({ room: code }, "10.4.0.1");
  assert.equal(r.status, 200, JSON.stringify(r.json));
  assert.equal(r.json.room, 1);
  assert.equal(r.json.verdict, "late");
  assert.match(r.json.note, /20m to sudden death.*This match is late/);
});

test("at sudden death a code join is a 409 with the next match and the joinable rooms, not a seat elsewhere", async () => {
  await stub(ROOM1, { status: "closing", joinable: false, why: "sudden death: closed to new players", next_match_in_s: 1800 });
  const code = (await lobby()).find((x) => x.room === 1).code;
  const r = await join({ room: code }, "10.5.0.1");
  assert.equal(r.status, 409, JSON.stringify(r.json));
  assert.equal(r.json.code, "room_closed_to_joins");
  assert.equal(r.json.next_match_in_s, 1800);
  const waitingCode = (await lobby()).find((x) => x.room === waitingRoom).code;
  assert.deepEqual(r.json.joinable_rooms, [waitingCode], "public joinable rooms only: not the closed one, not the private one");
  assert.match(r.json.error, /closed to new players: sudden death has started\. Its next match starts in about 30 min/);
  // ...and placement never puts anyone there either.
  const p = await join({}, "10.5.0.2");
  assert.equal(p.json.room, waitingRoom);
});

test("with no room left to open, the refusal points at a waiting room", async () => {
  await stub(engineOf(waitingRoom).saved.port, { status: "waiting" });
  const r = await join({ room: "new" }, "10.6.0.1");
  assert.equal(r.status, 503, JSON.stringify(r.json));
  const code = (await lobby()).find((x) => x.room === waitingRoom).code;
  assert.match(r.json.error, new RegExp(`waiting for players instead: "room":"${code}"`));
});

test("MCP: list_rooms is the lobby, and join takes the new-room options", async () => {
  const client = new Client({ name: "lobby-test", version: "1" });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${GW}/mcp`)));
  const tools = (await client.listTools()).tools;
  assert.ok(tools.some((t) => t.name === "list_rooms") && tools.some((t) => t.name === "lobby"));
  const j = tools.find((t) => t.name === "join").inputSchema.properties;
  assert.ok(j.match_hours && j.min_players && j.private);
  const rooms = JSON.parse((await client.callTool({ name: "list_rooms", arguments: {} })).content[0].text).rooms;
  assert.equal(rooms.find((x) => x.room === privateRoom).code, null);
  const bad = await client.callTool({ name: "join", arguments: { name: "M", room: "new", min_players: 9 } }).catch(() => ({ isError: true }));
  assert.equal(bad.isError, true, "the schema refuses it");
  await client.close();
});
