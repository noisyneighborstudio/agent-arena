#!/usr/bin/env node
// Load the Unity WebGL build in real Chrome on the real GPU, record frame times and load timings, take a screenshot.
//
//   node arena/webclient/measure.mjs --url 'http://127.0.0.1:7431/?snapshot=snapshot.json&cam=40,30,9' \
//        --size 1728x1080 --seconds 30 --shot /tmp/pezweb/compare/web.png [--dpr 1] [--early-shot /tmp/x.png --early-delay 3]
//        [--headed] [--uncap] [--name web-snapshot] [--channel chrome|chromium] [--out /tmp/pezweb/compare]
//
// What it prints (JSON): the GPU renderer string, engine-ready ms, time to the first "pez resync", the "pez fps:" lines
// (the first window is skipped; it includes warm-up), error counts and the first 5 unique error messages.
// The whole console goes to <out>/<name>.console.txt (name defaults to the --shot file's base name).
//
// GPU: headless Chrome (new headless) with --use-angle=metal reaches the Apple GPU. The renderer string is verified in the
// page and the run is flagged "gpu_ok": false if it says SwiftShader / llvmpipe / software; --headed then runs a real window
// positioned off screen instead. Playwright is resolved from /tmp/pezweb/pw (npm i playwright) or the usual module path.
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";

const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf("--" + name); return i >= 0 && args[i + 1] !== undefined ? args[i + 1] : def; };
const flag = (name) => args.includes("--" + name);

let chromium;
try { ({ chromium } = await import("playwright")); }
catch { chromium = createRequire("/tmp/pezweb/pw/package.json")("playwright").chromium; }

const url = opt("url");
if (!url) { console.error("usage: measure.mjs --url URL [--size WxH] [--seconds N] [--shot png] [--dpr N] [--headed]"); process.exit(2); }
const [W, H] = opt("size", "1728x1080").split("x").map(Number);
const seconds = Number(opt("seconds", 30));
const dpr = Number(opt("dpr", 1));
const shot = opt("shot", "");
const earlyShot = opt("early-shot", "");
const earlyDelay = Number(opt("early-delay", 3)); // seconds after the first resync
const outDir = opt("out", "/tmp/pezweb/compare");
const name = opt("name", shot ? path.basename(shot).replace(/\.[a-z]+$/, "") : "run");
const headed = flag("headed");
const channel = opt("channel", "chrome");
fs.mkdirSync(outDir, { recursive: true });

const gpuArgs = ["--use-angle=metal", "--enable-gpu", "--ignore-gpu-blocklist", "--enable-gpu-rasterization", "--enable-zero-copy",
  "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows",
  "--disable-features=CalculateNativeWinOcclusion", "--autoplay-policy=no-user-gesture-required"];
if (flag("uncap")) gpuArgs.push("--disable-frame-rate-limit", "--disable-gpu-vsync"); // show headroom past the 60 fps rAF cap
if (headed) gpuArgs.push("--window-position=-3000,0", `--window-size=${W},${H + 120}`);

const launch = { channel: channel === "chromium" ? undefined : channel, headless: !headed, args: gpuArgs };
const browser = await chromium.launch(launch);
const ctx = await browser.newContext({ viewport: { width: W, height: H }, deviceScaleFactor: dpr });
const page = await ctx.newPage();

const t0 = Date.now();
const rel = () => ((Date.now() - t0) / 1000).toFixed(2);
const lines = [];       // every console message, timestamped
const errors = [];      // { kind, text }
let firstResyncMs = null, engineReadyMs = null;
const fps = [];
let resyncs = 0;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const record = (kind, text) => {
  lines.push(`[${rel()}s] ${kind}: ${text}`);
  if (kind === "error" || kind === "pageerror") errors.push({ kind, text });
  let m;
  if (/pez resync:/.test(text)) { resyncs++; if (firstResyncMs === null) firstResyncMs = Date.now() - t0; }
  if ((m = /pez: engine ready in (\d+) ms/.exec(text))) engineReadyMs = Number(m[1]);
  if ((m = /pez fps: avg=([\d.]+) p5=([\d.]+) frames=(\d+) w=(\d+) h=(\d+)/.exec(text)))
    fps.push({ avg: +m[1], p5: +m[2], frames: +m[3], w: +m[4], h: +m[5], at_s: +rel() });
};
page.on("console", (msg) => record(msg.type(), msg.text()));
page.on("pageerror", (e) => record("pageerror", String(e && e.message || e)));
page.on("requestfailed", (r) => record("requestfailed", `${r.url()} ${r.failure()?.errorText}`));
page.on("response", (r) => { if (r.status() >= 400) record("http" + r.status(), r.url()); });

// The GPU check runs in a scratch page, so it doesn't touch the game's own context.
async function gpuString() {
  const p = await ctx.newPage();
  const s = await p.evaluate(() => {
    const c = document.createElement("canvas");
    const gl = c.getContext("webgl2");
    if (!gl) return "no webgl2";
    const e = gl.getExtension("WEBGL_debug_renderer_info");
    return e ? gl.getParameter(e.UNMASKED_RENDERER_WEBGL) + " | " + gl.getParameter(e.UNMASKED_VENDOR_WEBGL) : "no debug info: " + gl.getParameter(gl.RENDERER);
  });
  await p.close();
  return s;
}
const gpu = await gpuString();
const gpuOk = !/swiftshader|llvmpipe|software|no webgl2/i.test(gpu) && /apple|metal|angle/i.test(gpu);
if (!gpuOk) console.error("WARNING: GPU renderer is not the hardware GPU: " + gpu + (headed ? "" : "  (try --headed)"));

// Browser-side frame log, independent of Unity's own meter: every requestAnimationFrame timestamp.
await page.addInitScript(() => { window.__raf = []; const f = (t) => { window.__raf.push(t); requestAnimationFrame(f); }; requestAnimationFrame(f); });
await page.goto(url, { waitUntil: "domcontentloaded" });
// Unity edge-pans the camera when the mouse sits at the window edge, and a fresh headless page reports (0,0), so the view
// would drift to the corner for the whole run. Park the pointer mid-canvas (and again once the game is up).
await page.mouse.move(W / 2, H / 2);
const deadline = Date.now() + 240000;
while (firstResyncMs === null && Date.now() < deadline) await sleep(100);
if (firstResyncMs === null) record("harness", "no 'pez resync' within 240 s");
const tResync = Date.now();
await page.mouse.move(W / 2 + 1, H / 2 + 1);

if (earlyShot && firstResyncMs !== null) {
  await sleep(Math.max(0, earlyDelay * 1000 - (Date.now() - tResync)));
  await page.locator("canvas#unity-canvas").screenshot({ path: earlyShot });
}
await sleep(Math.max(0, seconds * 1000 - (Date.now() - tResync)));

const raf = await page.evaluate(() => window.__raf);
const gaps = raf.slice(1).map((t, i) => t - raf[i]);
const lastBig = gaps.reduce((a, g, i) => (g > 500 ? i : a), -1); // the last stall of half a second or more
const tail = raf.slice(lastBig + 1);
const rafStats = {
  frames_total: raf.length,
  longest_stall_ms: Math.round(Math.max(0, ...gaps)),
  smooth_from_s: lastBig < 0 ? 0 : +(raf[lastBig + 1] / 1000).toFixed(1), // page time after which no frame gap exceeded 0.5 s
  fps_after_smooth: tail.length > 2 ? +(((tail.length - 1) * 1000) / (tail[tail.length - 1] - tail[0])).toFixed(1) : null,
  fps_last_10s: (() => { const e = raf[raf.length - 1]; const w = raf.filter((t) => t > e - 10000); return w.length > 2 ? +(((w.length - 1) * 1000) / (w[w.length - 1] - w[0])).toFixed(1) : null; })(),
};
const canvas = await page.evaluate(() => {
  const c = document.querySelector("canvas#unity-canvas");
  return { width: c.width, height: c.height, clientWidth: c.clientWidth, clientHeight: c.clientHeight, dpr: window.devicePixelRatio, pezLoadMs: window.pezLoadMs ?? null };
});
if (shot) { fs.mkdirSync(path.dirname(shot), { recursive: true }); await page.locator("canvas#unity-canvas").screenshot({ path: shot }); }

const consoleFile = path.join(outDir, `${name}.console.txt`);
fs.writeFileSync(consoleFile, lines.join("\n") + "\n");
await browser.close();

const first = (t) => t.split("\n")[0].slice(0, 300);
const uniq = [...new Set(errors.map((e) => first(e.text)))];
const steady = fps.slice(1); // the first window includes shader/warm-up
const mean = (a) => a.length ? +(a.reduce((x, y) => x + y, 0) / a.length).toFixed(1) : null;
const summary = {
  name, url, size: `${W}x${H}`, dpr, headed, channel,
  gpu_renderer: gpu, gpu_ok: gpuOk,
  canvas, raf: rafStats,
  engine_ready_ms: engineReadyMs,
  first_resync_s: firstResyncMs === null ? null : +(firstResyncMs / 1000).toFixed(2),
  resyncs,
  fps_windows_skipped_first: steady,
  fps_mean_avg: mean(steady.map((f) => f.avg)), fps_mean_p5: mean(steady.map((f) => f.p5)),
  fps_first_window: fps[0] ?? null,
  errors_total: errors.length, errors_unique: uniq.length, first_5_unique_errors: uniq.slice(0, 5),
  console_file: consoleFile, screenshot: shot || null, early_screenshot: earlyShot || null,
};
fs.writeFileSync(path.join(outDir, `${name}.summary.json`), JSON.stringify(summary, null, 2));
console.log(JSON.stringify(summary, null, 2));
