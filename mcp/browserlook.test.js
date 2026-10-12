// Browser look: the broker (queue, delivery, upload validation, timeout fallback, cooling off, first answer wins), the
// Player's use of it, and the gateway's routes with a mocked page. Starts a throwaway headless engine and gateway (temp
// HOME; room 1 without a renderer, as a headless room 1 will be) and stops exactly those two at the end.
//
//   node --test mcp/browserlook.test.js
//
// Env: PEZZ_LOOK_TEST_PORT (engine; the gateway takes the next port, default 7930), PEZZ_LOOK_TEST_DIR (default /tmp/look/gw-test).
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { LookBroker, isJpeg, LOOK_MAX_BYTES } from "./browserlook.js";
import { Player } from "./play.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, "..");
const ENGINE_PORT = Number(process.env.PEZZ_LOOK_TEST_PORT || 7930);
const GW_PORT = ENGINE_PORT + 1;
const DIR = process.env.PEZZ_LOOK_TEST_DIR || "/tmp/look/gw-test";
const WAIT_MS = 1200;
const children = [];

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
/** A little valid-looking JPEG (markers only: the gateway checks the framing, not the pixels). */
const jpeg = (n = 64, fill = 7) => Buffer.concat([Buffer.from([0xff, 0xd8, 0xff, 0xe0]), Buffer.alloc(n, fill), Buffer.from([0xff, 0xd9])]);

// ---------------------------------------------------------------- the broker

test("jpeg check: framing, not pixels", () => {
  assert.ok(isJpeg(jpeg()));
  assert.ok(isJpeg(Buffer.concat([jpeg(), Buffer.alloc(3)])), "a little padding after EOI is fine");
  assert.ok(!isJpeg(Buffer.from("GIF89a...........")));
  assert.ok(!isJpeg(Buffer.from([0xff, 0xd8, 0xff, 0x00, 0x00, 0x00])), "no end marker");
  assert.ok(!isJpeg(Buffer.alloc(0)));
  assert.ok(!isJpeg("not a buffer"));
});

test("broker: no page open means no request, at once", async () => {
  const b = new LookBroker({ waitMs: 5000 });
  const t0 = Date.now();
  assert.equal(await b.request("1:0", 3, 4), null);
  assert.ok(Date.now() - t0 < 100, "the fallback is immediate");
  assert.equal(b.available("1:0"), false);
});

test("broker: a polling page gets the request (with x,y) and its upload answers the agent", async () => {
  const b = new LookBroker({ waitMs: 3000, holdMs: 2000 });
  const poll = b.next("1:0", 0); // the page is waiting
  assert.ok(b.hasPage("1:0"));
  const asked = b.request("1:0", 40, 50);
  const req = await poll;
  assert.deepEqual({ x: req.x, y: req.y }, { x: 40, y: 50 });
  const img = jpeg(100);
  assert.deepEqual(b.submit("1:0", req.id, img), { ok: true });
  assert.equal(await asked, img);
  // The page asks again with the id it handled: nothing newer, so it holds (and ends empty on abort).
  const ac = new AbortController();
  const again = b.next("1:0", req.id, ac.signal);
  ac.abort();
  assert.equal(await again, null);
});

test("broker: x,y are optional (null means the base)", async () => {
  const b = new LookBroker({ waitMs: 3000, holdMs: 2000 });
  const poll = b.next("1:0", 0);
  b.request("1:0", NaN, undefined);
  const req = await poll;
  assert.equal(req.x, null);
  assert.equal(req.y, null);
});

test("broker: uploads are validated, and only a pending id of that seat is accepted", async () => {
  const b = new LookBroker({ waitMs: 3000, holdMs: 2000 });
  const poll = b.next("1:0", 0);
  const asked = b.request("1:0");
  const { id } = await poll;
  assert.equal(b.submit("1:0", id, Buffer.from("<html>not a jpeg</html>")).status, 415);
  assert.equal(b.submit("1:0", id, Buffer.alloc(0)).status, 400);
  assert.equal(b.submit("1:0", id, Buffer.concat([jpeg(), Buffer.alloc(LOOK_MAX_BYTES)])).status, 413);
  assert.equal(b.submit("1:0", id + 99, jpeg()).status, 410, "an id nobody asked for");
  assert.equal(b.submit("1:1", id, jpeg()).status, 410, "another seat's link can't answer this seat's request");
  assert.equal(b.submit("2:0", id, jpeg()).status, 410, "nor another room's");
  const good = jpeg(10);
  assert.equal(b.submit("1:0", id, good).ok, true, "after all the refusals the request is still open");
  assert.equal(await asked, good);
});

test("broker: several tabs on one link get the request, the first answer wins", async () => {
  const b = new LookBroker({ waitMs: 3000, holdMs: 2000 });
  const tabA = b.next("1:0", 0), tabB = b.next("1:0", 0);
  const asked = b.request("1:0", 1, 2);
  const [ra, rb] = await Promise.all([tabA, tabB]);
  assert.equal(ra.id, rb.id);
  const first = jpeg(20, 1), second = jpeg(20, 2);
  assert.equal(b.submit("1:0", ra.id, first).ok, true);
  assert.equal(b.submit("1:0", rb.id, second).status, 410);
  assert.equal(await asked, first);
});

test("broker: a page that doesn't answer costs one wait, then the agent skips it for a while", async () => {
  let now = 1_000_000;
  const b = new LookBroker({ waitMs: 150, holdMs: 5000, coolMs: 20000, presenceMs: 3000, now: () => now });
  b.next("1:0", 0); // a (hidden, frozen) page holds its poll but never answers
  const t0 = Date.now();
  assert.equal(await b.request("1:0"), null);
  assert.ok(Date.now() - t0 >= 140, "it waited for the page");
  const t1 = Date.now();
  assert.equal(await b.request("1:0"), null);
  assert.ok(Date.now() - t1 < 50, "cooling off: straight to the fallback");
  assert.equal(b.available("1:0"), false);
  now += 21000;
  assert.equal(b.available("1:0"), false, "the page took its request and never polled again: it's gone, not just quiet");
  b.next("1:0", 99); // it comes back
  assert.equal(b.available("1:0"), true, "the cool-off is over");
});

test("broker: a page that comes back after a gap is tried again at once", async () => {
  let now = 1_000_000;
  const b = new LookBroker({ waitMs: 100, holdMs: 5000, coolMs: 60000, presenceMs: 3000, now: () => now });
  const ac = new AbortController();
  const held = b.next("1:0", 0, ac.signal);
  assert.equal(await b.request("1:0"), null); // nobody answered: cooling
  ac.abort(); await held;
  assert.equal(b.available("1:0"), false);
  now += 10000; // the tab was closed, then reopened
  const poll = b.next("1:0", 0);
  assert.equal(b.available("1:0"), true);
  const asked = b.request("1:0");
  const { id } = await poll;
  assert.equal(b.submit("1:0", id, jpeg()).ok, true);
  assert.ok(await asked);
});

test("broker: a page counts as open only while it polls (and a moment after)", async () => {
  let now = 1_000_000;
  const b = new LookBroker({ waitMs: 100, holdMs: 5000, presenceMs: 3000, now: () => now });
  const ac = new AbortController();
  const held = b.next("1:0", 0, ac.signal);
  assert.equal(b.hasPage("1:0"), true);
  ac.abort(); await held;
  assert.equal(b.hasPage("1:0"), true, "re-polling is about to happen");
  now += 3500;
  assert.equal(b.hasPage("1:0"), false);
});

test("broker: forget answers waiting agents with nothing and ends the page's poll", async () => {
  const b = new LookBroker({ waitMs: 5000, holdMs: 5000 });
  const poll = b.next("1:0", 0);
  const asked = b.request("1:0");
  await poll; // delivered
  const poll2 = b.next("1:0", 99);
  b.forget("1:0");
  assert.equal(await asked, null);
  assert.equal(await poll2, null);
  assert.equal(b.hasPage("1:0"), false);
});

// ---------------------------------------------------------------- the Player

test("player: a browser picture comes back as a JPEG that says who drew it; none falls through to the map", async () => {
  const p = new Player("http://127.0.0.1:1", { token: "t" }, null);
  const img = jpeg(30);
  p.browserLook = async () => img;
  const v = await p.look(5, 6);
  assert.equal(v.source, "browser");
  assert.equal(v.mime, "image/jpeg");
  assert.equal(v.data, img);
  assert.match(v.note, /^Rendered by your human's browser/);
});

// ---------------------------------------------------------------- the gateway

function call(pathname, { method = "GET", headers = {}, body, raw, port = GW_PORT, signal } = {}) {
  return new Promise((resolve, reject) => {
    const data = raw ?? (body ? JSON.stringify(body) : undefined);
    const req = http.request({ host: "127.0.0.1", port, path: pathname, method, signal, headers: { ...(body ? { "content-type": "application/json" } : {}), ...headers } }, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => { const buf = Buffer.concat(chunks); resolve({ status: res.statusCode, headers: res.headers, buf, text: buf.toString("utf8"), json: () => JSON.parse(buf.toString("utf8")) }); });
    });
    req.on("error", reject);
    req.end(data);
  });
}
async function until(what, fn, ms = 20000) {
  const t0 = Date.now();
  for (;;) {
    let v; try { v = await fn(); } catch {}
    if (v) return v;
    if (Date.now() - t0 > ms) throw new Error(`timed out waiting for ${what}`);
    await sleep(250);
  }
}
function start(cmd, args, env, log) {
  const out = fs.openSync(path.join(DIR, log), "w");
  const c = spawn(cmd, args, { cwd: ROOT, env, stdio: ["ignore", out, out] });
  children.push(c);
  return c;
}
/** A mocked page: long-polls look/next like the Unity client does, hands each request to `onRequest`, and uploads what it returns. */
function mockPage(view) {
  let after = 0, stopped = false;
  const ac = new AbortController();
  const seen = [];
  const loop = (async () => {
    while (!stopped) {
      let r;
      try { r = await call(`${view}/look/next?after=${after}`, { signal: ac.signal }); } catch { return; }
      if (r.status !== 200) { await sleep(100); continue; }
      const q = r.json().request;
      if (!q) continue;
      after = q.id; seen.push(q);
      const body = page.onRequest?.(q);
      if (body) page.uploads.push(await call(`${view}/look/${q.id}`, { method: "POST", headers: { "content-type": "image/jpeg" }, raw: body }));
    }
  })();
  const page = { seen, uploads: [], onRequest: null, stop: async () => { stopped = true; ac.abort(); await loop; } };
  return page;
}

let seats = []; // { token, view }
async function joinSeat(name) {
  const j = (await call("/join", { method: "POST", body: { name } })).json();
  assert.equal(j.ok, true, JSON.stringify(j));
  const view = new URL(j.commander_url.split("?c=")[0]).pathname;
  return { token: j.token, view, auth: { authorization: `Bearer ${j.token}` } };
}

before(async () => {
  fs.rmSync(DIR, { recursive: true, force: true });
  fs.mkdirSync(path.join(DIR, "home"), { recursive: true });
  start("dotnet", [path.join(ROOT, "headless/bin/Release/net10.0/pez-headless.dll"), "--port", String(ENGINE_PORT), "--open", "--fresh", "--snapshot", path.join(DIR, "game.json")],
    { ...process.env, HOME: path.join(DIR, "home") }, "engine.log");
  await until("the engine", async () => (await call("/api/status", { port: ENGINE_PORT })).status === 200);
  start(process.execPath, [path.join(HERE, "gateway.js")], {
    PATH: process.env.PATH, HOME: path.join(DIR, "home"), PEZZ_GAME: `http://127.0.0.1:${ENGINE_PORT}`, PEZZ_FRAMES: "none", PEZZ_GATEWAY_PORT: String(GW_PORT),
    PEZZ_MAX_ROOMS: "1", PEZZ_WATCH_DELAY: "3", PEZZ_LOOK_WAIT_MS: String(WAIT_MS),
  }, "gateway.log");
  await until("the gateway", async () => (await call("/lobby")).status === 200);
  seats = [await joinSeat("LookA"), await joinSeat("LookB"), await joinSeat("LookC")];
});

after(async () => {
  for (const c of children) { try { c.kill("SIGTERM"); } catch {} }
  await sleep(500);
  for (const c of children) { if (c.exitCode === null) { try { c.kill("SIGKILL"); } catch {} } }
});

test("gateway: with no page open, look is the map picture and says so", async () => {
  const [a] = seats;
  const t0 = Date.now();
  const r = await call("/look", { headers: a.auth });
  assert.equal(r.status, 200, r.text.slice(0, 200));
  assert.equal(r.headers["content-type"], "image/png");
  assert.equal(r.headers["x-pezz-look"], "map");
  assert.match(decodeURIComponent(r.headers["x-pezz-look-note"]), /^Map view \(no browser open\)/);
  assert.ok(Date.now() - t0 < WAIT_MS, "no waiting for a page that isn't there");
});

test("gateway: an open page renders the look, with the requested x,y, and the agent gets its bytes", async () => {
  const [a] = seats;
  const page = mockPage(a.view);
  const img = jpeg(5000, 9);
  page.onRequest = () => img;
  await until("the page's poll to land", async () => true, 1000); await sleep(300);
  const r = await call("/look?x=40&y=50", { headers: a.auth });
  assert.equal(r.status, 200, r.text.slice(0, 200));
  assert.equal(r.headers["content-type"], "image/jpeg");
  assert.equal(r.headers["x-pezz-look"], "browser");
  assert.match(decodeURIComponent(r.headers["x-pezz-look-note"]), /^Rendered by your human's browser/);
  assert.deepEqual(r.buf, img);
  assert.deepEqual({ x: page.seen[0].x, y: page.seen[0].y }, { x: 40, y: 50 });
  // Without x,y the page is told to use the base (null).
  const r2 = await call("/look", { headers: a.auth });
  assert.equal(r2.headers["x-pezz-look"], "browser");
  assert.deepEqual({ x: page.seen[1].x, y: page.seen[1].y }, { x: null, y: null });
  await page.stop();
});

test("gateway: bad uploads are refused and the real one still gets through", async () => {
  const [a] = seats;
  const page = mockPage(a.view);
  const img = jpeg(300, 3);
  let id;
  page.onRequest = (q) => { id = q.id; return null; }; // the page answers by hand below
  await sleep(300);
  const asked = call("/look", { headers: a.auth });
  await until("the request to reach the page", () => id);
  const post = (body) => call(`${a.view}/look/${id}`, { method: "POST", raw: body, headers: { "content-type": "image/jpeg" } });
  assert.equal((await post(Buffer.from("<html>nope</html>"))).status, 415);
  const big = await post(Buffer.concat([jpeg(), Buffer.alloc(LOOK_MAX_BYTES)])).catch((e) => ({ status: e.code })); // (a reset is how some clients see the early 413)
  assert.ok(big.status === 413 || big.status === "ECONNRESET" || big.status === "EPIPE", `oversized: ${big.status}`);
  assert.equal((await post(img)).status, 200);
  assert.equal((await post(img)).status, 410, "answered once");
  assert.equal((await call(`${a.view}/look/${id + 100}`, { method: "POST", raw: img })).status, 410, "an id nobody asked");
  const r = await asked;
  assert.equal(r.headers["x-pezz-look"], "browser");
  assert.deepEqual(r.buf, img);
  await page.stop();
});

test("gateway: another seat's link can't answer, and neither can a spectator page", async () => {
  const [a, b] = seats;
  const page = mockPage(a.view);
  let id;
  page.onRequest = (q) => { id = q.id; return null; };
  await sleep(300);
  const asked = call("/look", { headers: a.auth });
  await until("the request to reach the page", () => id);
  assert.equal((await call(`${b.view}/look/${id}`, { method: "POST", raw: jpeg() })).status, 410, "B's link, A's request");
  // The spectator routes have no look at all: nothing to poll, nothing to upload to.
  for (const spec of ["/watch", "/watch/1"]) {
    const n = await call(`${spec}/look/next`);
    assert.ok(n.status === 401 || n.status === 404, `${spec}/look/next is ${n.status}`);
    assert.equal(n.headers["content-type"]?.includes("json") ? n.json().request : undefined, undefined);
    const up = await call(`${spec}/look/${id}`, { method: "POST", raw: jpeg() });
    assert.ok(up.status === 401 || up.status === 404, `${spec}/look/${id} is ${up.status}`);
  }
  // A's real answer still lands.
  assert.equal((await call(`${a.view}/look/${id}`, { method: "POST", raw: jpeg(40) })).status, 200);
  assert.equal((await asked).headers["x-pezz-look"], "browser");
  await page.stop();
});

test("gateway: B's open page doesn't make A's look a browser look", async () => {
  const [a, b] = seats;
  const page = mockPage(b.view);
  page.onRequest = () => jpeg();
  await sleep(300);
  const r = await call("/look", { headers: a.auth });
  assert.equal(r.headers["x-pezz-look"], "map");
  assert.equal(page.seen.length, 0, "B's page was never asked");
  await page.stop();
});

test("gateway: a page that never answers costs one wait, then the map picture; later looks skip it", async () => {
  const a = seats[2]; // (a seat of its own: the earlier tests' closed pages leave their seats cooling off)
  const page = mockPage(a.view);
  page.onRequest = () => null; // renders nothing
  await sleep(300);
  const t0 = Date.now();
  const r = await call("/look", { headers: a.auth });
  const took = Date.now() - t0;
  assert.equal(r.status, 200);
  assert.equal(r.headers["x-pezz-look"], "map", "the fallback");
  assert.ok(took >= WAIT_MS - 100 && took < WAIT_MS + 4000, `waited about ${WAIT_MS}ms (${took})`);
  assert.equal(page.seen.length, 1);
  const t1 = Date.now();
  const r2 = await call("/look", { headers: a.auth });
  assert.equal(r2.headers["x-pezz-look"], "map");
  assert.ok(Date.now() - t1 < WAIT_MS / 2, "cooling off: no second wait");
  assert.equal(page.seen.length, 1, "and the page wasn't bothered again");
  await page.stop();
});

test("gateway: a link that 401s at the game is refused, and the rate limit answers 429", async () => {
  const bogus = `/view/${"ab".repeat(24)}`;
  assert.equal((await call(`${bogus}/look/next`)).status, 401);
  assert.equal((await call(`${bogus}/look/7`, { method: "POST", raw: jpeg() })).status, 401);
  const [a] = seats;
  let limited = null;
  for (let i = 0; i < 30 && !limited; i++) { const r = await call(`${a.view}/look/${1000 + i}`, { method: "POST", raw: jpeg() }); if (r.status === 429) limited = r; }
  assert.ok(limited, "a burst of uploads on one link is limited");
});
