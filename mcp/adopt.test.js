// The move-over (#39) and the room lifecycle on the real engine: a gateway started the way CapRover's serve.mjs starts it
// (no PEZZ_GAME), on state from when room 1 was the host's own game (rooms.json with hostCode, room 1's save on its port),
// adopts room 1 as an ordinary room: same code, its r1- tokens still work, rooms.json in the new format. An engine that
// dies is brought back from its save by the reaper, and the adopted room's next match waits for players.
//
//   node --test mcp/adopt.test.js        (needs dotnet on PATH and a Release build of headless/)
//
// Env: PEZZ_TEST_ADOPT_PORT (room 1's port; the gateway takes +2 and new rooms +4 on, default 7970), PEZZ_TEST_DIR
// (default /tmp/pezz-adopt-test). Takes about two minutes: it waits out a reaper pass.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DLL = path.resolve(HERE, "../headless/bin/Release/net10.0/pez-headless.dll");
const R1 = Number(process.env.PEZZ_TEST_ADOPT_PORT || 7970), GW = R1 + 2;
const DIR = process.env.PEZZ_TEST_DIR || "/tmp/pezz-adopt-test";
const HOME = path.join(DIR, "home"), CONFIG = path.join(HOME, ".config", "pezz");
const SECRET = "adopt-test-secret-adopt-test";
let gateway, token;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(what, fn, ms = 30000) {
  const t0 = Date.now();
  for (;;) { let v; try { v = await fn(); } catch {} if (v) return v; if (Date.now() - t0 > ms) throw new Error(`timed out waiting for ${what}`); await sleep(250); }
}
async function call(port, pathname, { method = "GET", body, headers = {} } = {}) {
  const r = await fetch(`http://127.0.0.1:${port}${pathname}`, { method, headers: { "content-type": "application/json", ...headers }, body: body ? JSON.stringify(body) : undefined });
  const text = await r.text();
  let json = null; try { json = JSON.parse(text); } catch {}
  return { status: r.status, json, text };
}
const saved = () => JSON.parse(fs.readFileSync(path.join(CONFIG, "rooms.json"), "utf8"));
const room1 = async () => (await call(GW, "/lobby")).json.rooms.find((x) => x.room === 1);
const commandLine = (pid) => execFileSync("ps", ["-o", "command=", "-p", String(pid)], { encoding: "utf8" });
const alive = (pid) => { try { process.kill(pid, 0); return true; } catch { return false; } };

before(async () => {
  fs.rmSync(DIR, { recursive: true, force: true });
  fs.mkdirSync(path.join(CONFIG, "rooms"), { recursive: true });
  // Room 1 as the old supervisor ran it: an open engine saving to game-<port>.json, with one outside player seated.
  const out = fs.openSync(path.join(DIR, "old-room1.log"), "w");
  const old = spawn("dotnet", [DLL, "--port", String(R1), "--open", "--controllers", "ai", "--fresh", "--snapshot", path.join(CONFIG, "rooms", `game-${R1}.json`)],
    { env: { ...process.env, HOME }, stdio: ["ignore", out, out] });
  await until("the old room 1", async () => (await call(R1, "/api/status")).status === 200);
  const reg = await call(R1, "/api/register", { method: "POST", body: { name: "Old Timer" } });
  assert.equal(reg.status, 200, reg.text);
  token = `r1-${reg.json.token}`;
  assert.equal((await call(R1, "/api/admin/save", { method: "POST", body: {} })).status, 200);
  old.kill("SIGTERM");
  await until("the old room 1 to save and stop", () => old.exitCode !== null || old.signalCode !== null);
  fs.writeFileSync(path.join(CONFIG, "rooms.json"), JSON.stringify({ hostCode: "pezz-oldroom1", hostAnnounced: 52, rooms: [] }));

  const gout = fs.openSync(path.join(DIR, "gateway.log"), "w");
  gateway = spawn(process.execPath, [path.join(HERE, "gateway.js")], { env: { PATH: process.env.PATH, HOME, PEZZ_ROOM1_PORT: String(R1), PEZZ_ENGINE: DLL,
    PEZZ_GATEWAY_PORT: String(GW), PEZZ_ROOM_BASE_PORT: String(R1 + 4), PEZZ_FRAMES: "none", PEZZ_ADMIN_SECRET: SECRET }, stdio: ["ignore", gout, gout] });
  await until("the gateway", async () => (await call(GW, "/healthz")).status === 200);
});

after(async () => {
  try { for (const r of saved().rooms) if (r.pid) process.kill(r.pid, "SIGTERM"); } catch {}
  gateway?.kill("SIGTERM");
  await sleep(500);
});

test("room 1 comes back as an ordinary room: same code, its r1- token works, rooms.json in the new format", async () => {
  const r = await until("room 1 in the lobby", async () => { const x = await room1(); return x?.status === "playing" && x; }, 60000);
  assert.equal(r.code, "pezz-oldroom1");
  const st = await call(GW, "/state?format=json", { headers: { authorization: `Bearer ${token}` } });
  assert.equal(st.status, 200, st.text.slice(0, 200));
  assert.equal(st.json.state.you.status, "playing");
  const s = saved();
  assert.equal("hostCode" in s, false, "no hostCode any more");
  assert.equal(s.rooms.length, 1);
  assert.deepEqual({ ...s.rooms[0], pid: undefined, createdAt: undefined },
    { id: 1, code: "pezz-oldroom1", kind: "headless", host: "main", region: "us", port: R1, pid: undefined, createdAt: undefined, announced: 52, minPlayers: 2 });
  assert.match(commandLine(s.rooms[0].pid), new RegExp(`--port ${R1} .*--resume --min-players 2`), "resumed from its save, waiting for players from its next match");
  assert.equal("room1" in (await call(GW, "/healthz")).json, false);
});

test("an engine that dies is brought back from its save by the reaper, seats and tokens intact", async () => {
  const pid = saved().rooms[0].pid;
  process.kill(pid, "SIGKILL"); // a crash: no save on the way out (the last periodic one is on disk)
  await until("the engine to go", () => !alive(pid));
  const back = await until("room 1 relaunched", () => { const p = saved().rooms[0]?.pid; return p && p !== pid && alive(p) && p; }, 100000);
  assert.match(commandLine(back), /--resume --min-players 2/);
  await until("room 1 answering", async () => (await room1())?.status === "playing", 30000);
  const st = await call(GW, "/state?format=json", { headers: { authorization: `Bearer ${token}` } });
  assert.equal(st.status, 200, st.text.slice(0, 200));
  assert.equal(st.json.state.you.status, "playing");
});

test("the adopted room's next match waits for players, like any other room's", async () => {
  const r = await call(GW, "/admin/rooms/1/api/admin/restart", { method: "POST", body: {}, headers: { "x-pezz-admin": SECRET } });
  assert.equal(r.status, 200, r.text);
  const l = await until("room 1 waiting", async () => { const x = await room1(); return x?.status === "waiting" && x; });
  assert.equal(l.min_players, 2);
  assert.equal(l.code, "pezz-oldroom1", "same code");
});
