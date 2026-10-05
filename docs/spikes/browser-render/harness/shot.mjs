// Open a page in headless Chrome (GPU via ANGLE/Metal if available), wait, report console errors + window.__perf, screenshot.
import { spawn } from "node:child_process"; import fs from "node:fs";
const [url, out, waitMs = "9000", w = "1280", h = "720", mobile = ""] = process.argv.slice(2);
const chrome = spawn("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", ["--headless=new", "--remote-debugging-port=9334", "--user-data-dir=/tmp/cdp/p2", "--use-angle=metal", "--enable-gpu", "--ignore-gpu-blocklist", "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let ws, id = 0; const pend = new Map(); const logs = [];
const send = (m, p = {}) => new Promise((r) => { const i = ++id; pend.set(i, r); ws.send(JSON.stringify({ id: i, method: m, params: p })); });
try {
  let t; for (let i = 0; i < 40 && !t; i++) { try { t = (await (await fetch("http://127.0.0.1:9334/json")).json()).find((x) => x.type === "page"); } catch {} if (!t) await sleep(250); }
  ws = new WebSocket(t.webSocketDebuggerUrl); await new Promise((r) => ws.addEventListener("open", r));
  ws.addEventListener("message", (m) => { const d = JSON.parse(m.data); if (d.id && pend.has(d.id)) { pend.get(d.id)(d.result); pend.delete(d.id); } if (d.method === "Runtime.consoleAPICalled" && (d.params.type === "error" || d.params.type === "warning")) logs.push(d.params.args.map((a) => a.value ?? a.description).join(" ")); if (d.method === "Runtime.exceptionThrown") logs.push("EXC " + JSON.stringify(d.params.exceptionDetails).slice(0, 400)); });
  await send("Runtime.enable");
  await send("Emulation.setDeviceMetricsOverride", { width: +w, height: +h, deviceScaleFactor: mobile ? 3 : 1, mobile: !!mobile });
  if (mobile) await send("Emulation.setCPUThrottlingRate", { rate: 4 });
  await send("Page.navigate", { url }); await sleep(+waitMs);
  const perf = (await send("Runtime.evaluate", { expression: process.env.EVAL || "JSON.stringify(window.__perf||null)+' '+document.getElementById('stats')?.textContent", returnByValue: true })).result.value;
  const gl = (await send("Runtime.evaluate", { expression: "(()=>{const c=document.createElement('canvas').getContext('webgl2');const e=c&&c.getExtension('WEBGL_debug_renderer_info');return e?c.getParameter(e.UNMASKED_RENDERER_WEBGL):'?'})()", returnByValue: true })).result.value;
  const shot = await send("Page.captureScreenshot", { format: "png" }); fs.writeFileSync(out, Buffer.from(shot.data, "base64"));
  console.log(JSON.stringify({ perf, gl, logs: logs.slice(0, 8) }, null, 1));
} finally { chrome.kill(); }
