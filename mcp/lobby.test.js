// Lobby and placement (#36), the gateway side of waiting rooms (#37), and rooms that exist only while someone plays (#38):
// an empty lobby, the first join opening room 1, placement order, private rooms listed without their code, the
// sudden-death 409, room:"new" options and how they reach the engine. Runs a throwaway gateway (temp HOME, no PEZZ_GAME:
// no room until a join opens one) whose rooms run a stub engine (PEZZ_ENGINE): a tiny node server that answers
// /api/status, /api/lobby and /api/register in the engine's shapes.
//
//   node --test mcp/lobby.test.js
//
// Env: PEZZ_TEST_LOBBY_PORT (the gateway; its rooms take ports from 2 above it, default 7960), PEZZ_TEST_DIR (default /tmp/pezz-lobby-test).
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
const GW = Number(process.env.PEZZ_TEST_LOBBY_PORT || 7960);
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
/** The stub engine the gateway started for a room (its port and command line), found by the room's port in the gateway's state. */
function engineOf(roomId) {
  const saved = JSON.parse(fs.readFileSync(path.join(DIR, "home", ".config", "pezz", "rooms.json"), "utf8")).rooms.find((r) => r.id === roomId);
  return { saved, ...JSON.parse(fs.readFileSync(path.join(STUBS, `engine-${saved.port}.json`), "utf8")) };
}
const stub = (roomId, state) => call(engineOf(roomId).saved.port, "/stub", { method: "POST", body: state });
const lobby = async () => (await call(GW, "/lobby")).json.rooms;

before(async () => {
  fs.rmSync(DIR, { recursive: true, force: true });
  fs.mkdirSync(STUBS, { recursive: true });
  fs.mkdirSync(path.join(DIR, "home"), { recursive: true });
  const engine = path.join(DIR, "stub-engine");
  fs.writeFileSync(engine, STUB, { mode: 0o755 });
  const gout = fs.openSync(path.join(DIR, "gateway.log"), "w");
  children.push(spawn(process.execPath, [path.join(HERE, "gateway.js")], { env: { PATH: process.env.PATH, HOME: path.join(DIR, "home"), PEZZ_STUB_DIR: STUBS,
    PEZZ_GATEWAY_PORT: String(GW), PEZZ_ROOM_BASE_PORT: String(GW + 2), PEZZ_ENGINE: engine, PEZZ_MAX_ROOMS: "3", PEZZ_ADMIN_SECRET: "test-secret-test-secret",
    PEZZ_CLIENT_IP_HEADER: "x-forwarded-for" }, stdio: ["ignore", gout, gout] }));
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

test("no permanent room: an empty lobby that says how to open one, and /healthz without room 1", async () => {
  const l = (await call(GW, "/lobby")).json;
  assert.deepEqual(l.rooms, []);
  assert.match(l.join, /with none open, that opens a room waiting for players/);
  const h = await call(GW, "/healthz");
  assert.equal(h.status, 200);
  assert.equal(h.json.ok, true);
  assert.equal(h.json.rooms, 0);
  assert.equal("room1" in h.json, false);
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

let privateRoom, playingRoom, waitingRoom;
test("a private waiting room is room 1: the engine gets its options, the lobby hides its code, the reply explains the wait", async () => {
  const r = await join({ room: "new", match_hours: 2, min_players: 3, private: true }, "10.2.0.1");
  assert.equal(r.status, 200, JSON.stringify(r.json));
  privateRoom = r.json.room;
  assert.equal(privateRoom, 1, "ids start at 1");
  assert.equal(r.json.room_status, "waiting");
  assert.deepEqual(r.json.waiting, { players: 1, min_players: 3, waited_s: 0, choose_after_s: 300 });
  assert.match(r.json.note, /private/);
  assert.match(r.json.note, /"start_match","opponent":"scripted"/);
  const e = engineOf(privateRoom);
  assert.deepEqual(e.args.slice(e.args.indexOf("--match-hours")), ["--match-hours", "2", "--min-players", "3"]);
  assert.equal(e.saved.port, GW + 2, "the first local room takes the first port");
  assert.equal(e.saved.private, true);
  assert.equal(e.saved.matchHours, 2);
  assert.equal(e.saved.minPlayers, 3);
  const listed = (await lobby()).find((x) => x.room === privateRoom);
  assert.equal(listed.code, null);
  assert.equal(listed.private, true);
  assert.equal(listed.status, "waiting");
  assert.equal(listed.min_players, 3);
});

test("a join with no code opens a public waiting room (never the private one), and the next join starts it", async () => {
  const a = await join({}, "10.3.0.1");
  assert.equal(a.status, 200, JSON.stringify(a.json));
  playingRoom = a.json.room;
  assert.equal(playingRoom, 2);
  assert.equal(a.json.room_status, "waiting");
  assert.match(a.json.note, /No open room could take you, so you opened room 2/);
  const e = engineOf(playingRoom);
  assert.deepEqual(e.args.slice(e.args.indexOf("--match-hours")), ["--match-hours", "4", "--min-players", "2"], "the gateway's own rooms take the defaults");
  assert.equal(e.saved.private, false);
  assert.equal((await lobby()).find((x) => x.room === playingRoom).code, e.saved.code, "public rooms keep their code");
  const b = await join({}, "10.3.0.2");
  assert.equal(b.json.room, playingRoom, "the public waiting room before opening another");
  assert.equal(b.json.room_status, "playing", "the second player starts it");
});

test("placement: a playing room that isn't late; when it's late, a new public waiting room, then that one", async () => {
  await stub(playingRoom, { verdict: "midgame", verdict_text: "2h10m to sudden death; 2 active players; the leader has 41% of the points." });
  const a = await join({}, "10.4.0.1");
  assert.equal(a.json.room, playingRoom, JSON.stringify(a.json));
  assert.equal(a.json.room_status, "playing");
  assert.equal(a.json.verdict, "midgame");
  assert.match(a.json.note, /2h10m to sudden death/);

  await stub(playingRoom, { verdict: "late", verdict_text: "20m to sudden death; 1 active player; the leader has 80% of the points." });
  const b = await join({}, "10.4.0.2");
  assert.equal(b.status, 200, JSON.stringify(b.json));
  waitingRoom = b.json.room;
  assert.equal(waitingRoom, 3, "a new room, not the late one or the private one");
  assert.equal(b.json.room_status, "waiting");
  const c = await join({}, "10.4.0.3");
  assert.equal(c.json.room, waitingRoom);
  assert.equal(c.json.room_status, "playing");
});

test("a code join to a late room works and says what it walked into", async () => {
  const code = (await lobby()).find((x) => x.room === playingRoom).code;
  const r = await join({ room: code }, "10.5.0.1");
  assert.equal(r.status, 200, JSON.stringify(r.json));
  assert.equal(r.json.room, playingRoom);
  assert.equal(r.json.verdict, "late");
  assert.match(r.json.note, /20m to sudden death.*This match is late/);
});

test("at sudden death a code join is a 409 with the next match and the joinable rooms, not a seat elsewhere", async () => {
  await stub(playingRoom, { status: "closing", joinable: false, why: "sudden death: closed to new players", next_match_in_s: 1800 });
  const code = (await lobby()).find((x) => x.room === playingRoom).code;
  const r = await join({ room: code }, "10.6.0.1");
  assert.equal(r.status, 409, JSON.stringify(r.json));
  assert.equal(r.json.code, "room_closed_to_joins");
  assert.equal(r.json.next_match_in_s, 1800);
  const waitingCode = (await lobby()).find((x) => x.room === waitingRoom).code;
  assert.deepEqual(r.json.joinable_rooms, [waitingCode], "public joinable rooms only: not the closed one, not the private one");
  assert.match(r.json.error, /closed to new players: sudden death has started\. Its next match starts in about 30 min/);
  // ...and placement never puts anyone there either.
  const p = await join({}, "10.6.0.2");
  assert.equal(p.json.room, waitingRoom);
});

test("with no room left to open, the refusal points at a waiting room", async () => {
  await stub(waitingRoom, { status: "waiting" });
  const r = await join({ room: "new" }, "10.7.0.1");
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
  // Before joining, whats_new is the whole list (no room to ask).
  const news = (await client.callTool({ name: "whats_new", arguments: {} })).content[0].text;
  assert.match(news, /#1 /);
  await client.close();
});

// Joins that name no room fail with a 404 before any seat is taken, so they cost a join from the limit and nothing else.
const nowhere = { room: "pezz-no-such-room" };
const mcpClient = async (port, ip) => {
  const client = new Client({ name: "join-limit-test", version: "1" });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`), { requestInit: { headers: { "x-forwarded-for": ip } } }));
  return client;
};
const mcpJoin = async (client, extra = {}) => { const r = await client.callTool({ name: "join", arguments: { name: "M", ...nowhere, ...extra } }); return { error: !!r.isError, text: r.content[0].text }; };

test("joins: 5 per 10 minutes per address, shared by POST /join and the MCP join tool (switch_seat too), keyed on the proxy's right-most address", async () => {
  // The left-most address is whatever the client sent: changing it must not buy more joins.
  const via = (n) => `192.0.2.${n}, 10.55.0.1`;
  for (let i = 0; i < 3; i++) assert.equal((await join(nowhere, via(i))).status, 404);
  const a = await mcpClient(GW, via(7)), b = await mcpClient(GW, via(8));
  assert.match((await mcpJoin(a)).text, /no room has the code/);
  assert.match((await mcpJoin(b, { switch_seat: true })).text, /no room has the code/); // five used
  const http6 = await join(nowhere, via(9));
  assert.equal(http6.status, 429, JSON.stringify(http6.json));
  assert.match(http6.json.error, /too many joins.*5 per 10 minutes.*rejoin/);
  const mcp6 = await mcpJoin(a);
  assert.equal(mcp6.error, true);
  assert.match(mcp6.text, /too many joins/);
  const sw = await mcpJoin(b, { switch_seat: true });
  assert.equal(sw.error, true);
  assert.match(sw.text, /too many joins/);
  // Another address is unaffected, and reads are not limited.
  assert.equal((await join(nowhere, "10.55.0.2")).status, 404);
  assert.equal((await call(GW, "/lobby", { ip: "10.55.0.1" })).status, 200);
  await a.close(); await b.close();
});

test("joins: without PEZZ_CLIENT_IP_HEADER, forwarding headers are ignored and the socket address is the client", async () => {
  const port = GW + 20, home = path.join(DIR, "home2");
  fs.mkdirSync(home, { recursive: true });
  const out = fs.openSync(path.join(DIR, "gateway2.log"), "w");
  children.push(spawn(process.execPath, [path.join(HERE, "gateway.js")], { env: { PATH: process.env.PATH, HOME: home, PEZZ_GATEWAY_PORT: String(port),
    PEZZ_ROOM_BASE_PORT: String(port + 2), PEZZ_ENGINE: "none", PEZZ_MAX_ROOMS: "1", PEZZ_ADMIN_SECRET: "test-secret-test-secret" }, stdio: ["ignore", out, out] }));
  await until("the second gateway", async () => (await call(port, "/lobby")).status === 200);
  const spoof = (n) => fetch(`http://127.0.0.1:${port}/join`, { method: "POST", headers: { "content-type": "application/json", "x-forwarded-for": `198.51.100.${n}`, "cf-connecting-ip": `203.0.113.${n}` }, body: JSON.stringify({ name: "S", ...nowhere }) });
  for (let i = 0; i < 5; i++) assert.equal((await spoof(i)).status, 404);
  assert.equal((await spoof(99)).status, 429, "five fake addresses did not buy a sixth join");
  const me = await (await fetch(`http://127.0.0.1:${port}/healthz`, { headers: { "x-forwarded-for": "198.51.100.1" } })).json();
  assert.match(me.you, /^(127\.0\.0\.1|::1|::ffff:127\.0\.0\.1)$/);
  const a = await mcpClient(port, "198.51.100.200");
  assert.match((await mcpJoin(a)).text, /too many joins/); // the MCP join shares the same (used up) socket address
  await a.close();
});
