// Gateway tests for the browser build's routes: meta, snapshot headers, /web redirects, /watch with no room, /play caching,
// the seat-ended degrade, and the per-address caps. Starts a throwaway gateway the way CapRover runs it (temp HOME, so
// nothing under ~/.config/pezz is touched; no PEZZ_GAME, so no room until a join opens one, on the real headless engine),
// and stops exactly it and the room it opened at the end.
//
//   node --test mcp/web.test.js
//
// Env: PEZZ_TEST_PORT (the gateway; its room takes the port 2 above, default 7940), PEZZ_TEST_DIR (default /tmp/parity-gw-test).
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, "..");
const GW_PORT = Number(process.env.PEZZ_TEST_PORT || 7940);
const ENGINE_PORT = GW_PORT + 2; // the first room the gateway opens (PEZZ_ROOM_BASE_PORT)
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

  start(process.execPath, [path.join(HERE, "gateway.js")], {
    PATH: process.env.PATH, HOME: path.join(DIR, "home"), PEZZ_FRAMES: "none", PEZZ_GATEWAY_PORT: String(GW_PORT), PEZZ_ROOM_BASE_PORT: String(ENGINE_PORT),
    PEZZ_ENGINE: path.join(ROOT, "headless/bin/Release/net10.0/pez-headless.dll"), PEZZ_WEB_DIR: web, PEZZ_MAX_ROOMS: "1", PEZZ_WATCH_DELAY: String(DELAY),
  }, "gateway.log");
  await until("the gateway", async () => (await call("/lobby")).status === 200);
});

after(async () => {
  // The room's engine is the gateway's detached child: stop it by the pid the gateway saved.
  let pids = [];
  try { pids = JSON.parse(fs.readFileSync(path.join(DIR, "home", ".config", "pezz", "rooms.json"), "utf8")).rooms.map((r) => r.pid).filter(Boolean); } catch {}
  for (const pid of pids) { try { process.kill(pid, "SIGTERM"); } catch {} }
  for (const c of children) { try { c.kill("SIGTERM"); } catch {} }
  await sleep(500);
  for (const c of children) { if (c.exitCode === null) { try { c.kill("SIGKILL"); } catch {} } }
});

test("no game running: /watch is a plain page saying so, its feeds 404, and /changes lists everything", async () => {
  const w = await call("/watch");
  assert.equal(w.status, 200);
  assert.match(w.text, /No games right now/);
  assert.match((await call("/watch/web")).text, /No games right now/);
  assert.equal((await call("/watch/meta")).status, 404);
  assert.equal((await call("/watch/snapshot")).status, 404);
  const c = (await call("/changes")).json();
  assert.equal(c.room, null);
  assert.equal(c.changes.at(-1).id, 1, "the whole list, newest first");
  assert.equal((await call("/rules")).status, 503);
});

test("the first join opens room 1, waiting for players", async () => {
  const j = (await call("/join", { method: "POST", body: { name: "WebTest" } })).json();
  assert.equal(j.ok, true, JSON.stringify(j));
  assert.equal(j.room, 1);
  assert.equal(j.room_status, "waiting");
  assert.ok(j.tell_your_human.includes(j.commander_url), "one link: the commander view (the browser build by default)");
  assert.match(j.token, /^r1-[a-f0-9]{48}$/);
  const [viewUrl, commander] = j.commander_url.split("?c=");
  seat = { view: new URL(viewUrl).pathname, commander, team: undefined, flavor: j.flavor, token: j.token };
  // A bare token (no room in it) is refused, and says what to send instead.
  const bare = await call("/state", { headers: { authorization: `Bearer ${j.token.slice(3)}` } });
  assert.equal(bare.status, 401);
  assert.match(bare.json().error, /no room in it: send the whole token/);
  assert.equal((await call("/state", { headers: { authorization: `Bearer ${j.token}` } })).status, 200);
});

test("spectator meta: waiting at first, then ready; bare /watch goes to the busiest room", async () => {
  for (const [from, to] of [["/watch", "/watch/1"], ["/watch/web?cam=2", "/watch/1/web?cam=2"], ["/watch/meta", "/watch/1/meta"], ["/watch/classic", "/watch/1/classic"]]) {
    const r = await call(from);
    assert.equal(r.status, 302, from);
    assert.equal(r.headers.location, to);
  }
  // Nobody has watched yet, so the ring has nothing old enough: meta and the 503 both say when it starts.
  const w = (await call("/watch/1/meta")).json();
  assert.equal(w.ok, true);
  assert.equal(w.mode, "spectator");
  assert.equal(w.delay_s, DELAY);
  assert.equal(w.team, -1);
  assert.equal(w.room, 1);
  assert.match(w.waiting, /^Spectator feed starts in \d+s \(it runs \d+s behind the game\)\.$/);
  assert.ok(w.starts_in_s >= 1 && w.starts_in_s <= DELAY);
  const early = await call("/watch/1/snapshot");
  assert.equal(early.status, 503);
  assert.equal(early.headers["x-pezz-mode"], "spectator");
  assert.match(early.json().waiting, /^Snapshot feed starts in \d+s/);
  assert.ok(early.json().starts_in_s >= 1);
  // Ready after the delay: asking started the ring.
  const m = await until("meta ready", async () => { const x = (await call("/watch/1/meta")).json(); return x.waiting === null ? x : null; }, (DELAY + 10) * 1000);
  assert.equal(m.starts_in_s, null);
  assert.equal(m.results, null);
  assert.equal(m.rejoin, null);
  assert.ok("map_name" in m && "flavor" in m);
  const r = await call("/watch/1/snapshot");
  assert.equal(r.status, 200);
  assert.equal(r.headers["x-pezz-mode"], "spectator");
  assert.equal(r.headers["x-pezz-delay-s"], String(DELAY));
  assert.ok(Number(r.headers["x-pezz-snapshot-age-s"]) >= DELAY);
  assert.equal(r.headers["cache-control"], "no-store");
});

test("a player's meta, snapshot headers and /web redirect", async () => {
  assert.ok(seat, "the join test ran first");
  const { commander } = seat;
  const m = (await call(`${seat.view}/meta`)).json();
  assert.equal(m.ok, true);
  assert.equal(m.mode, "player");
  assert.equal(m.delay_s, 0);
  assert.equal(m.flavor, seat.flavor);
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

  const w = await call("/watch/1/web");
  assert.equal(w.status, 302);
  assert.match(w.headers.location, /^\/watch\/1\/web\?.*snapshot=%2Fwatch%2F1%2Fsnapshot/);

  // The plain links open the browser build; the original page stays at /classic (and ?classic=1).
  const d = await call(`${seat.view}?c=${commander}`);
  assert.equal(d.status, 302);
  assert.equal(d.headers.location, `${seat.view}/web?c=${commander}`);
  assert.match((await call(`${seat.view}/classic?c=${commander}`)).text, /Pezz: your view/);
  assert.match((await call(`${seat.view}?classic=1`)).text, /Pezz: your view/);
  const dw = await call("/watch/1");
  assert.equal(dw.status, 302);
  assert.equal(dw.headers.location, "/watch/1/web");
  assert.match((await call("/watch/1/classic")).text, /Pezz: your view/);
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
  const bogus = `/view/r1-${"ab".repeat(24)}`;
  const m = await call(`${bogus}/meta`);
  assert.equal(m.json().mode, "spectator");
  assert.equal(m.json().results.outcome, "ended");
  const s = await call(`${bogus}/snapshot`);
  assert.equal(s.status, 200);
  assert.equal(s.headers["x-pezz-mode"], "spectator");
  assert.equal((await call(`/view/r7-${"ab".repeat(24)}/meta`)).status, 401);
  assert.equal((await call(`/view/${"ab".repeat(24)}/meta`)).status, 401, "a bare link has no room to go to");
});

test("lobby and briefing point to the watch page and its classic fallback", async () => {
  assert.match((await call("/lobby")).json().watch, /\/classic/);
  assert.match((await call("/")).text, /\/watch\/<room number>\/classic/);
});

test("/ is the landing page for a browser and the briefing for everyone else; /play, /llms.txt and the pictures", async () => {
  const browser = { accept: "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", "user-agent": "Mozilla/5.0 (Macintosh) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15" };
  const page = await call("/", { headers: browser });
  assert.match(page.headers["content-type"], /^text\/html/);
  assert.match(page.headers.vary, /Accept/);
  assert.match(page.headers.link, /<\/play>; rel="alternate"; type="text\/markdown"/);
  assert.match(page.text, /AI agent\?/);
  assert.match(page.text, /Room 1<\/span>/, "the room the join above opened is on the page");
  assert.match(page.text, /\/watch\/&lt;room number&gt;\/classic/, "the whole briefing is in the page");
  assert.match((await call("/", { headers: { ...browser, "user-agent": "Claude-User/1.0" } })).headers["content-type"], /^text\/markdown/);
  assert.match((await call("/play", { headers: browser })).headers["content-type"], /^text\/markdown/);
  const llms = await call("/llms.txt");
  assert.match(llms.headers["content-type"], /^text\/plain/);
  assert.match(llms.text, /^# Pezz arena/);
  assert.match((await call("/llms-full.txt")).text, /^# Pezz arena: you're invited to play/);
  const hero = await call("/site/hero.jpg");
  assert.equal(hero.status, 200);
  assert.equal(hero.headers["content-type"], "image/jpeg");
  assert.equal((await call("/site/nope.jpg")).status, 404);
});

test("per-address caps answer 429 slow down", async () => {
  let limited = null;
  for (let i = 0; i < 110 && !limited; i++) { const r = await call("/play/Build/game.loader.js"); if (r.status === 429) limited = r; }
  assert.ok(limited, "a burst of /play requests is limited");
  assert.equal(limited.json().error, "slow down");
});
