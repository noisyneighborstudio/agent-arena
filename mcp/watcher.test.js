// The watcher script (/watcher.py), run for real with python3 against a fake gateway that answers /wait and /state
// from a script. Skipped where there's no python3.
//
//   node --test mcp/watcher.test.js
import { test } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { watcherScript } from "./watcher.js";

const hasPython = spawnSync("python3", ["--version"]).status === 0;
const state = (over = {}) => ({ tick: 100, time_s: 50, game_over: false, match: "3h to sudden death", alerts: "none", standing_orders: "none", orders_version: 0,
  you: { status: "playing", power: "100 produced / 40 used", stockpile: { iron_ore: 10 } }, ...over });
const quiet = { waited_s: 0, interrupted: false, new_priority_alerts: [], orders_changed: false, whats_new: [], state: state() };

/** Runs the watcher against a gateway that answers each call with the next of `answers` ([status, body]; the last repeats). */
async function watch(answers, args = []) {
  const calls = [];
  const server = http.createServer((req, res) => {
    calls.push({ url: req.url, auth: req.headers.authorization });
    const [status, body] = answers[Math.min(calls.length - 1, answers.length - 1)];
    // A real /wait blocks; a short pause keeps the timer test from spinning.
    setTimeout(() => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(body)); }, 100);
  });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "pezz-watcher-"));
  fs.writeFileSync(path.join(dir, "w.py"), watcherScript("https://pezz.example"));
  const child = spawn("python3", ["-I", path.join(dir, "w.py"), ...args], {
    env: { ...process.env, PEZZ_TOKEN: "r1-" + "a".repeat(48), PEZZ_BASE: `http://127.0.0.1:${server.address().port}` } });
  let out = ""; child.stdout.on("data", (d) => (out += d)); child.stderr.on("data", (d) => (out += d));
  const code = await new Promise((r) => child.on("exit", r));
  server.close(); fs.rmSync(dir, { recursive: true });
  return { code, out, calls };
}

test("waits through quiet stretches and wakes for a priority alert, with the token on every call", { skip: !hasPython }, async () => {
  const alert = { seq: 7, priority: "critical", x: 40, y: 41, text: "BASE UNDER ATTACK: 3 heavy tanks hitting your refinery" };
  const r = await watch([[200, quiet], [200, quiet], [200, { ...quiet, interrupted: true, new_priority_alerts: [alert] }]]);
  assert.equal(r.code, 0, r.out);
  assert.equal(r.calls.length, 3);
  for (const c of r.calls) { assert.match(c.url, /^\/wait\?format=json&seconds=([1-9]|[12]\d|30)$/); assert.equal(c.auth, "Bearer r1-" + "a".repeat(48)); }
  assert.match(r.out, /PEZZ WATCHER: priority alert/);
  assert.match(r.out, /alert \[critical\] at 40,41: BASE UNDER ATTACK/);
});

test("wakes for new orders and new capabilities, and says what they are", { skip: !hasPython }, async () => {
  const r = await watch([[200, { ...quiet, interrupted: true, orders_changed: true, whats_new: [{ title: "Watcher", text: "it waits" }], state: state({ standing_orders: "rush the north-east derrick" }) }]]);
  assert.equal(r.code, 0, r.out);
  assert.match(r.out, /new orders from your human commander/);
  assert.match(r.out, /standing orders: rush the north-east derrick/);
  assert.match(r.out, /new: Watcher: it waits/);
});

test("exits 2 when the seat is gone, and for a token the gateway refuses", { skip: !hasPython }, async () => {
  const gone = await watch([[200, { ...quiet, state: state({ you: { status: "eliminated: call join again for a new seat" } }) }]]);
  assert.equal(gone.code, 2, gone.out);
  assert.match(gone.out, /your seat is eliminated/);
  const refused = await watch([[401, { ok: false, error: "send your token" }]]);
  assert.equal(refused.code, 2, refused.out);
  assert.match(refused.out, /join again/);
});

test("waits out maintenance, then wakes on its timer with a fresh state", { skip: !hasPython }, async () => {
  const r = await watch([[503, { ok: false, maintenance: true, retry_after_s: 1, message: "restarting" }], [200, quiet]], ["--every", "5"]);
  assert.equal(r.code, 0, r.out);
  assert.match(r.out, /maintenance: restarting/);
  assert.match(r.out, /5 s passed/);
  assert.equal(r.calls.at(-1).url, "/state?format=json");
});
