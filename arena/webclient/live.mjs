#!/usr/bin/env node
// Live-mode validation: load the Unity web build with live=N against a throwaway engine, log every console line with a wall
// clock, poll the server's tick beside it (--status http://127.0.0.1:7961/api/status), and report resync stats and drift.
//   node arena/webclient/live.mjs --live 1 --seconds 90 --name live1 [--cam x,y,zoom] [--seq 6] [--seq-gap 1000] [--burst 8]
//        [--page http://127.0.0.1:7962] [--size 1280x720] [--out /tmp/pezweb/live] [--speed 3]
// drift: (snapshot tick) - (local tick just before the swap), the local tick being estimated from the previous snapshot's tick
// plus wall time * 20 * speed (WebClient doesn't log it). Negative = the local world was ahead: time jumps back on swap.
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf("--" + n); return i >= 0 && args[i + 1] !== undefined ? args[i + 1] : d; };
const { chromium } = createRequire("/tmp/pezweb/pw/package.json")("playwright");
const live = opt("live", "1"), seconds = Number(opt("seconds", 90)), name = opt("name", "live" + live), out = opt("out", "/tmp/pezweb/live");
const pageBase = opt("page", "http://127.0.0.1:7962"), statusUrl = opt("status", "http://127.0.0.1:7961/api/status");
const [W, H] = opt("size", "1280x720").split("x").map(Number);
const cam = opt("cam", ""), seqN = Number(opt("seq", 0)), seqGap = Number(opt("seq-gap", 1000)), burst = Number(opt("burst", 0));
const TR = 20; // ticks per game second

const browser = await chromium.launch({ channel: "chrome", headless: true, args: ["--use-angle=metal", "--enable-gpu", "--ignore-gpu-blocklist", "--enable-gpu-rasterization",
  "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows", "--disable-features=CalculateNativeWinOcclusion"] });
const video = opt("video", ""); // a directory: frames from CDP screencast, named <ms since t0>.jpg
const ctx = await browser.newContext({ viewport: { width: W, height: H }, deviceScaleFactor: 1 });
const sp = await ctx.newPage();
const gpu = await sp.evaluate(() => { const g = document.createElement("canvas").getContext("webgl2"); const e = g && g.getExtension("WEBGL_debug_renderer_info"); return e ? g.getParameter(e.UNMASKED_RENDERER_WEBGL) : "?"; });
await sp.close();
const page = await ctx.newPage();
const lines = [], errors = [], resyncs = [], fps = [];
const t0 = Date.now(), sleep = (ms) => new Promise((r) => setTimeout(r, ms));
page.on("console", (m) => {
  const now = Date.now(), text = m.text();
  lines.push(`[${((now - t0) / 1000).toFixed(3)}] ${m.type()}: ${text}`);
  let x;
  if ((x = /pez resync: tick=(\d+) bytes=(\d+) parse=([\d.]+)ms restore=([\d.]+)ms rebind=([\d.]+)ms \((\w+)\)(?: local=(\d+))?/.exec(text))) resyncs.push({ at: now, tick: +x[1], bytes: +x[2], parse: +x[3], restore: +x[4], rebind: +x[5], kind: x[6], local: x[7] === undefined ? null : +x[7] });
  if ((x = /pez fps: avg=([\d.]+) p5=([\d.]+)/.exec(text))) fps.push({ at: now, avg: +x[1], p5: +x[2] });
  if (m.type() === "error" || /exception|error/i.test(text) && m.type() !== "log") errors.push(text);
});
page.on("pageerror", (e) => { lines.push(`pageerror: ${e.message}`); errors.push(String(e.message)); });
page.on("requestfailed", (r) => { lines.push(`requestfailed ${r.url()} ${r.failure()?.errorText}`); errors.push("requestfailed " + r.url().replace(/\?.*/, "") + " " + r.failure()?.errorText); });
await page.addInitScript(() => { window.__raf = []; const f = (t) => { window.__raf.push(t); requestAnimationFrame(f); }; requestAnimationFrame(f); });

// Server tick poll, 5/s: [wall ms, server tick]
const polls = []; let polling = true;
(async () => { while (polling) { const a = Date.now(); try { const j = await (await fetch(statusUrl)).json(); polls.push([(a + Date.now()) / 2, j.tick, j.speed]); } catch {} await sleep(200); } })();

const url = `${pageBase}/?snapshot=/snapshot&live=${live}&fps=1${cam ? "&cam=" + cam : ""}`;
await page.goto(url, { waitUntil: "domcontentloaded" });
await page.mouse.move(W / 2, H / 2);
const dl = Date.now() + 240000;
while (!resyncs.length && Date.now() < dl) await sleep(100);
if (!resyncs.length) { console.error("no resync"); process.exit(1); }
const tStart = Date.now();
if (video) {
  fs.mkdirSync(video, { recursive: true });
  const cdp = await ctx.newCDPSession(page);
  cdp.on("Page.screencastFrame", (f) => { fs.writeFileSync(path.join(video, `${Math.round(f.metadata.timestamp * 1000)}.jpg`), Buffer.from(f.data, "base64")); cdp.send("Page.screencastFrameAck", { sessionId: f.sessionId }).catch(() => {}); });
  await cdp.send("Page.startScreencast", { format: "jpeg", quality: 85, maxWidth: W, maxHeight: H, everyNthFrame: 1 });
}
await page.mouse.move(W / 2 + 1, H / 2 + 1);
const canvas = page.locator("canvas#unity-canvas");
const shots = [];
if (burst) { // screenshots back to back (as fast as they come), tagged with the time, for the window around resyncs
  await sleep(3000);
  const nResync = resyncs.length, until = Date.now() + burst * 1000;
  for (let i = 0; Date.now() < until; i++) { const a = Date.now(); const f = path.join(out, `${name}-burst-${String(i).padStart(2, "0")}.png`); await canvas.screenshot({ path: f }); shots.push({ f, at: (a + Date.now()) / 2 }); }
}
if (seqN) {
  await sleep(2000);
  for (let i = 0; i < seqN; i++) { const a = Date.now(); const f = path.join(out, `${name}-seq-${i + 1}.png`); await canvas.screenshot({ path: f }); shots.push({ f, at: (a + Date.now()) / 2 }); await sleep(Math.max(0, seqGap - (Date.now() - a))); }
}
await sleep(Math.max(0, seconds * 1000 - (Date.now() - tStart)));
polling = false;
const raf = await page.evaluate(() => window.__raf);
fs.writeFileSync(path.join(out, `${name}.console.txt`), lines.join("\n") + "\n");
await browser.close();

// ---- analysis
const serverTickAt = (t) => { // linear interpolation of the polls
  let lo = null, hi = null;
  for (const p of polls) { if (p[0] <= t) lo = p; else { hi = p; break; } }
  if (!lo || !hi) return null;
  return lo[1] + (hi[1] - lo[1]) * (t - lo[0]) / (hi[0] - lo[0]);
};
const speed = Number(opt("speed", polls.length ? polls[polls.length - 1][2] : 1));
const st = (a) => a.length ? { avg: +(a.reduce((x, y) => x + y, 0) / a.length).toFixed(2), max: +Math.max(...a).toFixed(2), min: +Math.min(...a).toFixed(2) } : null;
const steady = resyncs.slice(1);
const skip = 3; // the first swaps follow the load stall
const rows = steady.map((r, i) => {
  const ll = r.local === null ? null : r.tick - r.local; // from the new local= field: snapshot tick minus the view world's tick just before the swap
  const prev = resyncs[i];
  const dtw = (r.at - prev.at) / 1000;
  const localBefore = prev.tick + dtw * TR * speed;
  const srv = serverTickAt(r.at);
  return { interval_s: +dtw.toFixed(2), snap_tick_delta: r.tick - prev.tick, expected_delta: +(dtw * TR * speed).toFixed(1), drift_ticks: +(r.tick - localBefore).toFixed(1), logged_drift: ll, stale_ticks: srv == null ? null : +(srv - r.tick).toFixed(1) };
});
const rafLate = raf.filter((t) => t > (resyncs[0].at - t0) + 3000); // after the load stall
const raft = rafLate.slice(1).map((t, i) => t - rafLate[i]);
const logged = rows.slice(skip).map((r) => r.logged_drift).filter((x) => x != null);
const absd = rows.slice(skip).map((r) => Math.abs(r.drift_ticks)).sort((a, b) => a - b);
const sum = {
  logged_drift: st(logged), logged_abs_p95: logged.length ? logged.map(Math.abs).sort((a, b) => a - b)[Math.floor(logged.length * 0.95)] : null, caught_up: logged.filter((d) => d < 0 && d >= -40).length, behind_gt40: logged.filter((d) => d < -40).length, snapshot_ahead: logged.filter((d) => d > 0).length, equal: logged.filter((d) => d === 0).length,
  abs_drift_p50_p95: [absd[Math.floor(absd.length * 0.5)], absd[Math.floor(absd.length * 0.95)]],
  name, url, gpu, speed, live, seconds, resyncs: resyncs.length, rebound: resyncs.filter((r) => r.kind === "rebound").length, rebuilt: resyncs.filter((r) => r.kind === "rebuilt").length,
  bytes: st(resyncs.map((r) => r.bytes)), parse_ms: st(resyncs.map((r) => r.parse)), restore_ms: st(resyncs.map((r) => r.restore)), rebind_ms: st(resyncs.map((r) => r.rebind)),
  interval_s: st(rows.slice(skip).map((r) => r.interval_s)), drift_ticks: st(rows.slice(skip).map((r) => r.drift_ticks)), drift_s_of_game: null, stale_ticks: st(rows.slice(skip).filter((r) => r.stale_ticks != null).map((r) => r.stale_ticks)),
  backward_swaps: rows.slice(skip).filter((r) => r.drift_ticks < 0).length, forward_swaps: rows.slice(skip).filter((r) => r.drift_ticks > 0).length,
  fps_windows: fps.map((f) => ({ avg: f.avg, p5: f.p5 })), fps_avg_mean: st(fps.slice(1).map((f) => f.avg))?.avg, fps_p5_min: st(fps.slice(1).map((f) => f.p5))?.min, fps_p5_mean: st(fps.slice(1).map((f) => f.p5))?.avg,
  raf_longest_ms: Math.round(Math.max(...raft)), raf_over_50ms: raft.filter((g) => g > 50).length, raf_over_100ms: raft.filter((g) => g > 100).length,
  errors_unique: [...new Set(errors.map((e) => e.split("\n")[0].slice(0, 200)))],
  shots: shots.map((s) => ({ f: s.f, t_ms: s.at - t0, since_resync_ms: (() => { const r = [...resyncs].reverse().find((r) => r.at <= s.at); return r ? Math.round(s.at - r.at) : null; })() })),
  resync_times_ms: resyncs.map((r) => r.at - t0), resync_epoch_ms: resyncs.map((r) => r.at)
};
sum.drift_s_of_game = sum.drift_ticks && { avg: +(sum.drift_ticks.avg / TR).toFixed(2), min: +(sum.drift_ticks.min / TR).toFixed(2), max: +(sum.drift_ticks.max / TR).toFixed(2) };
fs.writeFileSync(path.join(out, `${name}.summary.json`), JSON.stringify({ ...sum, rows }, null, 1));
console.log(JSON.stringify(sum, null, 1));
