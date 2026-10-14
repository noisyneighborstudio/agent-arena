// The observation feed through the gateway, on the real headless engine: long-poll and SSE for a player token, resuming
// a stream with Last-Event-ID, independent read-only readers on a view token, command ids, human orders, controller
// leases and fog between two players. Starts a throwaway gateway with a temp HOME (nothing under ~/.config/pezz is
// touched), and stops exactly it and the room it opened.
//
//   node --test mcp/observe.test.js
//
// Env: PEZZ_TEST_PORT (the gateway; its room takes the port 2 above, default 7950), PEZZ_TEST_DIR (default /tmp/pezz-observe-test).
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, "..");
const GW_PORT = Number(process.env.PEZZ_TEST_PORT || 7950);
const DIR = process.env.PEZZ_TEST_DIR || "/tmp/pezz-observe-test";
const children = [];

function call(pathname, { method = "GET", headers = {}, body } = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: "127.0.0.1", port: GW_PORT, path: pathname, method, headers: { ...(body ? { "content-type": "application/json" } : {}), ...headers } }, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => { const text = Buffer.concat(chunks).toString("utf8"); resolve({ status: res.statusCode, headers: res.headers, text, json: () => JSON.parse(text) }); });
    });
    req.on("error", reject);
    req.end(body ? JSON.stringify(body) : undefined);
  });
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(what, fn, ms = 30000) {
  const t0 = Date.now();
  for (;;) {
    let v; try { v = await fn(); } catch {}
    if (v) return v;
    if (Date.now() - t0 > ms) throw new Error(`timed out waiting for ${what}`);
    await sleep(250);
  }
}

/** Reads an SSE stream until `stop(events)` is true (or ms pass), then closes it. Returns [{id, event, data}]. */
function sse(pathname, headers, stop, ms = 20000) {
  return new Promise((resolve, reject) => {
    const events = [];
    const req = http.get({ host: "127.0.0.1", port: GW_PORT, path: pathname, headers }, (res) => {
      if (res.statusCode !== 200) { reject(new Error(`SSE ${res.statusCode}`)); return; }
      let buf = "";
      const done = () => { clearTimeout(timer); req.destroy(); resolve(events); };
      const timer = setTimeout(done, ms);
      res.on("data", (chunk) => {
        buf += chunk;
        let i;
        while ((i = buf.indexOf("\n\n")) >= 0) {
          const block = buf.slice(0, i); buf = buf.slice(i + 2);
          const e = { id: null, event: "message", data: "", at: Date.now() };
          for (const l of block.split("\n")) {
            if (l.startsWith("id: ")) e.id = l.slice(4);
            else if (l.startsWith("event: ")) e.event = l.slice(7);
            else if (l.startsWith("data: ")) e.data += l.slice(6);
          }
          if (!e.data) continue;
          e.data = JSON.parse(e.data);
          events.push(e);
          if (stop(events)) return done();
        }
      });
    });
    req.on("error", (e) => { if (e.code !== "ECONNRESET") reject(e); });
  });
}

let a, b; // two seats in one running room: { token, view, commander }
const auth = (s) => ({ authorization: `Bearer ${s.token}` });

before(async () => {
  fs.rmSync(DIR, { recursive: true, force: true });
  fs.mkdirSync(path.join(DIR, "home"), { recursive: true });
  const out = fs.openSync(path.join(DIR, "gateway.log"), "w");
  children.push(spawn(process.execPath, [path.join(HERE, "gateway.js")], {
    cwd: ROOT, stdio: ["ignore", out, out],
    env: { PATH: process.env.PATH, HOME: path.join(DIR, "home"), PEZZ_FRAMES: "none", PEZZ_GATEWAY_PORT: String(GW_PORT), PEZZ_ROOM_BASE_PORT: String(GW_PORT + 2),
      PEZZ_ENGINE: path.join(ROOT, "headless/bin/Release/net10.0/pez-headless.dll"), PEZZ_MAX_ROOMS: "1" },
  }));
  await until("the gateway", async () => (await call("/lobby")).status === 200);
  const seatOf = (j) => { const [v, c] = j.commander_url.split("?c="); return { token: j.token, view: new URL(v).pathname.replace(/\/web$/, ""), commander: c, room_code: j.room_code }; };
  const j1 = (await call("/join", { method: "POST", body: { name: "ObsA" } })).json();
  assert.equal(j1.ok, true, JSON.stringify(j1));
  a = seatOf(j1);
  const j2 = (await call("/join", { method: "POST", body: { name: "ObsB", room: a.room_code } })).json();
  assert.equal(j2.ok, true, JSON.stringify(j2));
  b = seatOf(j2);
  for (const seat of [a, b]) await call("/ready?format=json", { method: "POST", headers: auth(seat) });
  await until("the match to start", async () => (await call("/state?format=json", { headers: auth(a) })).json().state?.waiting == null);
});

after(async () => {
  let pids = [];
  try { pids = JSON.parse(fs.readFileSync(path.join(DIR, "home", ".config", "pezz", "rooms.json"), "utf8")).rooms.map((r) => r.pid).filter(Boolean); } catch {}
  for (const pid of pids) { try { process.kill(pid, "SIGTERM"); } catch {} }
  for (const c of children) { try { c.kill("SIGTERM"); } catch {} }
  await sleep(500);
  for (const c of children) { if (c.exitCode === null) { try { c.kill("SIGKILL"); } catch {} } }
});

test("a first read starts fresh with a snapshot of only this team's knowledge", async () => {
  const ra = (await call("/observe", { headers: auth(a) })).json();
  assert.equal(ra.ok, true, JSON.stringify(ra));
  assert.equal(ra.fresh, true);
  assert.ok(ra.snapshot.units.length > 0 && ra.snapshot.structures.length > 0);
  const hq = ra.snapshot.structures.find((s) => s.key === "command_center");
  const rb = (await call("/observe", { headers: auth(b) })).json();
  assert.ok(!rb.snapshot.known_structures.some((s) => s.id === hq.id), "the other player doesn't know where A's base is");
  assert.ok(!rb.snapshot.contacts.some((c) => ra.snapshot.units.some((u) => u.id === c.id)), "nor sees A's units across the map");
  assert.notEqual(ra.stream, rb.stream);
});

test("SSE: snapshot, heartbeats, then a reconnect with Last-Event-ID resumes after the last event, with the command's events", async () => {
  const first = await sse("/observe/stream", auth(a), (ev) => ev.filter((e) => e.event === "heartbeat").length >= 3, 30000);
  // Measured: from the engine logging an event to this client holding it, through the gateway's SSE (live events only).
  const live = first.filter((e) => e.event === "heartbeat" || e.event === "observation").slice(1).map((e) => e.at - e.data.ms).sort((x, y) => x - y);
  console.log(`LATENCY engine log -> SSE client via gateway (wall ms): n=${live.length} p50 ${live[Math.floor(live.length / 2)]} max ${live.at(-1)}`);
  assert.ok(live.length > 0 && live.at(-1) < 500, "live events reach an SSE client promptly");
  assert.equal(first[0].event, "snapshot");
  const hb = first.find((e) => e.event === "heartbeat");
  assert.ok(hb, "a heartbeat arrives even with nothing happening");
  const last = first.at(-1).id;
  const [stream, lastSeq] = [last.slice(0, last.lastIndexOf(":")), Number(last.slice(last.lastIndexOf(":") + 1))];
  const units = (await call("/observe/snapshot", { headers: auth(a) })).json().snapshot.units.filter((u) => u.key === "rifleman");
  const c = (await call("/command?format=json", { method: "POST", headers: auth(a), body: { commands: [{ type: "move", units: units.map((u) => u.id), x: units[0].pos[0] + 3, y: units[0].pos[1] }] } })).json();
  const r0 = c.results.results[0]; // the gateway's json reply wraps the engine's
  assert.equal(r0.ok, true, JSON.stringify(c));
  assert.ok(r0.cmd > lastSeq && r0.units.length === units.length, "the reply has the command id and the units it affected");
  const resumed = await sse("/observe/stream", { ...auth(a), "last-event-id": last }, (ev) => ev.some((e) => e.data.kind === "movement_started" && e.data.cmd === r0.cmd));
  const obs = resumed.filter((e) => e.event === "observation" || e.event === "heartbeat");
  assert.ok(!resumed.some((e) => e.event === "snapshot" || e.event === "gap"), "a resume on the same stream is no gap");
  assert.ok(obs.every((e) => e.data.seq > lastSeq), "nothing before the Last-Event-ID is sent again");
  assert.ok(obs.some((e) => e.data.kind === "command_accepted" && e.data.cmd === r0.cmd && e.data.now === true));
  assert.ok(obs.some((e) => e.data.kind === "movement_started" && e.data.cmd === r0.cmd));
  assert.ok(resumed.every((e) => !e.id || e.id.startsWith(stream + ":")));
  // A Last-Event-ID from another stream (a restart, a new game) is a gap, said so, with a snapshot.
  const gap = await sse("/observe/stream", { ...auth(a), "last-event-id": stream.replace(/\.[^.]+$/, ".00000000") + ":5" }, (ev) => ev.length >= 1);
  assert.equal(gap[0].event, "gap");
  assert.equal(gap[0].data.gap.reason, "server_restarted");
  assert.ok(gap[0].data.snapshot);
});

test("a view-token reader has its own cursor: it never consumes or moves the player's", async () => {
  const p0 = (await call("/observe", { headers: auth(a) })).json();
  const mine = p0.next_cursor;
  await call("/command", { method: "POST", headers: auth(a), body: { type: "say", text: "cursor independence probe" } });
  const v1 = (await call(`${a.view}/observe?cursor=0&exclude=heartbeat`)).json();
  const v2 = (await call(`${a.view}/observe?cursor=0&exclude=heartbeat`)).json();
  assert.equal(v1.ok, true, JSON.stringify(v1));
  assert.equal(v1.stream, p0.stream, "the view reads the same team's feed");
  assert.deepEqual(v1.events.map((e) => e.seq), v2.events.map((e) => e.seq).slice(0, v1.events.length));
  const p1 = (await call(`/observe?cursor=${mine}&stream=${p0.stream}`, { headers: auth(a) })).json();
  const p2 = (await call(`/observe?cursor=${mine}&stream=${p0.stream}`, { headers: auth(a) })).json();
  assert.ok(p1.events.length > 0 && p1.events[0].seq === mine + 1, "the player reads from its own cursor, whatever the view read");
  assert.deepEqual(p1.events.map((e) => e.seq), p2.events.map((e) => e.seq).slice(0, p1.events.length));
  // The view link is read-only.
  assert.ok([401, 404].includes((await call(`${a.view}/command`, { method: "POST", body: { type: "stop", units: "all" } })).status), "a view link can't command");
});

test("human orders from the commander page end a player's long-poll at once", async () => {
  const p0 = (await call("/observe", { headers: auth(a) })).json();
  const started = Date.now();
  const poll = call(`/observe?cursor=${p0.next_cursor}&stream=${p0.stream}&wait=20&wake=critical`, { headers: auth(a) });
  await sleep(1000);
  const sent = Date.now();
  const o = await call(`${a.view}/orders`, { method: "POST", body: { c: a.commander, text: "Scout the east." } });
  assert.equal(o.status, 200, o.text);
  const r = (await poll).json();
  const got = Date.now();
  const ho = r.events.find((e) => e.kind === "human_orders");
  assert.ok(ho && ho.src === "human" && ho.authenticated === true && ho.orders === "Scout the east.", JSON.stringify(r.events));
  assert.ok(got - sent < 1500 && got - started < 5000, `orders arrived ${got - sent} ms after they were sent`);
  // The other team never hears them.
  const rb = (await call(`/observe?cursor=0&kinds=human_orders`, { headers: auth(b) })).json();
  assert.equal(rb.events.length, 0);
});

test("one command controller per team; readers never need the lease", async () => {
  const claim = (await call("/controller", { method: "POST", headers: auth(a), body: { holder: "subagent", ttl_s: 30 } })).json();
  assert.equal(claim.ok, true, JSON.stringify(claim));
  const no = await call("/command?format=json", { method: "POST", headers: auth(a), body: { type: "say", text: "hi" } });
  assert.equal(no.status, 409);
  assert.equal(no.json().code, "controller_conflict");
  const yes = await call("/command?format=json", { method: "POST", headers: { ...auth(a), "x-pezz-controller": claim.lease }, body: { type: "say", text: "hi" } });
  assert.equal(yes.status, 200, yes.text);
  assert.equal((await call("/observe", { headers: auth(a) })).status, 200);
  const rel = (await call("/controller", { method: "POST", headers: auth(a), body: { action: "release", lease: claim.lease } })).json();
  assert.equal(rel.controller.held, false);
});
