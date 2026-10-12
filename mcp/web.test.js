// Gateway tests for the browser build's routes: meta, snapshot headers, /web redirects, /play caching, the seat-ended
// degrade, and the per-address caps. Starts a throwaway headless engine and a throwaway gateway (temp HOME, so nothing
// under ~/.config/pezz is touched) as child processes, and stops exactly those two at the end.
//
//   node --test mcp/web.test.js
//
// Env: PEZZ_TEST_PORT (engine; the gateway takes the next port, default 7940), PEZZ_TEST_DIR (default /tmp/parity-gw-test).
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, "..");
const ENGINE_PORT = Number(process.env.PEZZ_TEST_PORT || 7940);
const GW_PORT = ENGINE_PORT + 1;
const DIR = process.env.PEZZ_TEST_DIR || "/tmp/parity-gw-test";
const DELAY = 3;
const children = [];

/** A raw GET/POST: no redirects, no transparent decompression (the .br test file is not real brotli). */
function call(pathname, { method = "GET", headers = {}, body, port = GW_PORT } = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: "127.0.0.1", port, path: pathname, method, headers: { ...(body ? { "content-type": "application/json" } : {}), ...headers } }, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => { const buf = Buffer.concat(chunks); resolve({ status: res.statusCode, headers: res.headers, buf, text: buf.toString("utf8"), json: () => JSON.parse(buf.toString("utf8")) }); });
    });
    req.on("error", reject);
    req.end(body ? JSON.stringify(body) : undefined);
  });
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
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

let seat; // { view, commander, team }
before(async () => {
  fs.rmSync(DIR, { recursive: true, force: true });
  fs.mkdirSync(path.join(DIR, "home"), { recursive: true });
  const web = path.join(DIR, "web");
  fs.mkdirSync(path.join(web, "Build"), { recursive: true });
  fs.writeFileSync(path.join(web, "index.html"), "<!doctype html><html><head><title>Pezz</title></head><body>build</body></html>");
  fs.writeFileSync(path.join(web, "Build", "game.data.br"), "not really brotli");
  fs.writeFileSync(path.join(web, "Build", "game.loader.js"), "// loader");

  start("dotnet", [path.join(ROOT, "headless/bin/Release/net10.0/pez-headless.dll"), "--port", String(ENGINE_PORT), "--open", "--fresh", "--snapshot", path.join(DIR, "game.json")],
    { ...process.env, HOME: path.join(DIR, "home") }, "engine.log");
  await until("the engine", async () => (await call("/api/status", { port: ENGINE_PORT })).status === 200);
  start(process.execPath, [path.join(HERE, "gateway.js")], {
    PATH: process.env.PATH, HOME: path.join(DIR, "home"), PEZZ_GAME: `http://127.0.0.1:${ENGINE_PORT}`, PEZZ_FRAMES: "http://127.0.0.1:1", PEZZ_GATEWAY_PORT: String(GW_PORT),
    PEZZ_WEB_DIR: web, PEZZ_MAX_ROOMS: "1", PEZZ_WATCH_DELAY: String(DELAY),
  }, "gateway.log");
  await until("the gateway", async () => (await call("/lobby")).status === 200);
});

after(async () => {
  for (const c of children) { try { c.kill("SIGTERM"); } catch {} }
  await sleep(500);
  for (const c of children) { if (c.exitCode === null) { try { c.kill("SIGKILL"); } catch {} } }
});

test("spectator meta: waiting at first, then ready; room 1 is warm without anyone asking", async () => {
  // Straight after start the ring has nothing old enough: meta and the 503 both say when it starts.
  const w = (await call("/watch/meta")).json();
  assert.equal(w.ok, true);
  assert.equal(w.mode, "spectator");
  assert.equal(w.delay_s, DELAY);
  assert.equal(w.team, -1);
  assert.equal(w.room, 1);
  assert.match(w.waiting, /^Spectator feed starts in \d+s \(it runs \d+s behind the game\)\.$/);
  assert.ok(w.starts_in_s >= 1 && w.starts_in_s <= DELAY);
  const early = await call("/watch/snapshot");
  assert.equal(early.status, 503);
  assert.equal(early.headers["x-pezz-mode"], "spectator");
  assert.match(early.json().waiting, /^Snapshot feed starts in \d+s/);
  assert.ok(early.json().starts_in_s >= 1);
  // Ready after the delay, with the request above the only thing that ever asked (room 1 samples on its own).
  const m = await until("meta ready", async () => { const x = (await call("/watch/meta")).json(); return x.waiting === null ? x : null; }, (DELAY + 10) * 1000);
  assert.equal(m.starts_in_s, null);
  assert.equal(m.results, null);
  assert.equal(m.rejoin, null);
  assert.ok("map_name" in m && "flavor" in m);
  const r = await call("/watch/snapshot");
  assert.equal(r.status, 200);
  assert.equal(r.headers["x-pezz-mode"], "spectator");
  assert.equal(r.headers["x-pezz-delay-s"], String(DELAY));
  assert.ok(Number(r.headers["x-pezz-snapshot-age-s"]) >= DELAY);
  assert.equal(r.headers["cache-control"], "no-store");
});

test("a player's meta, snapshot headers and /web redirect", async () => {
  const j = (await call("/join", { method: "POST", body: { name: "WebTest" } })).json();
  assert.equal(j.ok, true, JSON.stringify(j));
  assert.match(j.tell_your_human, /\/web\?c=/);
  const [viewUrl, commander] = j.commander_url.split("?c=");
  seat = { view: new URL(viewUrl).pathname, commander, team: undefined, flavor: j.flavor, token: j.token };

  const m = (await call(`${seat.view}/meta`)).json();
  assert.equal(m.ok, true);
  assert.equal(m.mode, "player");
  assert.equal(m.delay_s, 0);
  assert.equal(m.flavor, j.flavor);
  assert.equal(m.room, 1);
  assert.equal(m.results, null);
  assert.equal(m.rejoin, null);
  assert.equal(m.waiting, null);
  assert.ok(Number.isInteger(m.team) && m.team >= 0);
  seat.team = m.team;

  const s = await call(`${seat.view}/snapshot`);
  assert.equal(s.status, 200);
  assert.equal(s.headers["x-pezz-mode"], "player");
  assert.equal(s.headers["x-pezz-delay-s"], "0");
  assert.ok(Number(s.headers["x-pezz-snapshot-age-s"]) >= 0);
  assert.ok(JSON.parse(s.text).world, "a snapshot with a world");

  const red = await call(`${seat.view}/web?c=${commander}`);
  assert.equal(red.status, 302);
  const loc = new URL(red.headers.location, "http://x");
  assert.equal(loc.searchParams.get("snapshot"), `${seat.view}/snapshot`);
  assert.equal(loc.searchParams.get("live"), "1");
  assert.equal(loc.searchParams.get("team"), String(seat.team));
  assert.equal(loc.searchParams.get("c"), commander);
  const page = await call(red.headers.location);
  assert.equal(page.status, 200);
  assert.match(page.text, /<base href="\/play\/">/);
  assert.match(page.text, /window\.PEZ_QUERY=/);

  const w = await call("/watch/web");
  assert.equal(w.status, 302);
  assert.match(w.headers.location, /snapshot=%2Fwatch%2Fsnapshot/);
});

test("/play: strong ETag, 304 on revalidation, 406 when br is not accepted", async () => {
  const a = await call("/play/Build/game.data.br", { headers: { "accept-encoding": "gzip, br" } });
  assert.equal(a.status, 200);
  assert.equal(a.headers["content-encoding"], "br");
  assert.equal(a.headers["cache-control"], "no-cache");
  assert.match(a.headers.etag, /^"[0-9a-f]+-[0-9a-f]+"$/);
  assert.equal(a.text, "not really brotli");
  const b = await call("/play/Build/game.data.br", { headers: { "accept-encoding": "br", "if-none-match": a.headers.etag } });
  assert.equal(b.status, 304);
  assert.equal(b.headers.etag, a.headers.etag);
  assert.equal(b.buf.length, 0);
  const c = await call("/play/Build/game.data.br", { headers: { "accept-encoding": "br", "if-none-match": '"nope", W/' + a.headers.etag } });
  assert.equal(c.status, 304, "a weak-prefixed match in a list still revalidates");
  const d = await call("/play/Build/game.data.br", { headers: { "accept-encoding": "gzip" } });
  assert.equal(d.status, 406);
  assert.match(d.json().error, /br/);
  const e = await call("/play/Build/game.loader.js", { headers: { "accept-encoding": "identity" } });
  assert.equal(e.status, 200, "uncompressed files need no negotiation");
  assert.equal(e.headers["content-type"], "application/javascript");
  assert.equal((await call("/play/Build/missing.js")).status, 404);
});

test("unknown rooms are a clean 404", async () => {
  assert.equal((await call("/watch/9/meta")).status, 404);
  assert.equal((await call("/watch/9/snapshot")).status, 404);
});

test("seat ended: meta turns spectator with results, and the snapshot is the delayed spectator one", async () => {
  assert.ok(seat, "the join test ran first");
  const k = await call("/api/admin/kick", { method: "POST", body: { team: seat.team }, port: ENGINE_PORT });
  assert.equal(k.status, 200, k.text);
  const m = await until("meta to say spectator", async () => { const x = (await call(`${seat.view}/meta`)).json(); return x.mode === "spectator" ? x : null; }, 10000);
  assert.equal(m.delay_s, DELAY);
  assert.equal(m.team, -1);
  assert.equal(m.results.outcome, "left");
  assert.equal(typeof m.results.kills, "number");
  assert.equal(typeof m.results.played_s, "number");
  assert.match(m.rejoin, /join again/);
  assert.equal(m.flavor, seat.flavor);
  const s = await until("a delayed snapshot", async () => { const r = await call(`${seat.view}/snapshot`); return r.status === 200 ? r : null; }, 15000);
  assert.equal(s.headers["x-pezz-mode"], "spectator");
  assert.equal(s.headers["x-pezz-delay-s"], String(DELAY));
  assert.ok(Number(s.headers["x-pezz-snapshot-age-s"]) >= DELAY, `age ${s.headers["x-pezz-snapshot-age-s"]} should be at least the delay`);
});

test("a view link that 401s at the game degrades too, and a bad room is a clean error", async () => {
  // A syntactically valid link the game has never heard of: its seat is not a seat, so there is nothing live to serve.
  const bogus = `/view/${"ab".repeat(24)}`;
  const m = await call(`${bogus}/meta`);
  assert.equal(m.json().mode, "spectator");
  assert.equal(m.json().results.outcome, "ended");
  const s = await call(`${bogus}/snapshot`);
  assert.equal(s.status, 200);
  assert.equal(s.headers["x-pezz-mode"], "spectator");
  assert.equal((await call(`/view/r7-${"ab".repeat(24)}/meta`)).status, 401);
});

test("lobby and briefing mention the 3D view", async () => {
  assert.match((await call("/lobby")).json().watch, /\/web/);
  assert.match((await call("/")).text, /\/watch\/web/);
});

test("per-address caps answer 429 slow down", async () => {
  let limited = null;
  for (let i = 0; i < 110 && !limited; i++) { const r = await call("/play/Build/game.loader.js"); if (r.status === 429) limited = r; }
  assert.ok(limited, "a burst of /play requests is limited");
  assert.equal(limited.json().error, "slow down");
});
