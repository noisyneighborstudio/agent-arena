// Emulate a phone, open the observer page, swipe with one finger, pinch, and report whether the camera moved.
import { spawn } from "node:child_process";
const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const url = process.argv[2];
const chrome = spawn(CHROME, ["--headless=new", "--remote-debugging-port=9333", "--user-data-dir=/tmp/cdp/profile", "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let ws, id = 0; const pending = new Map();
const send = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
try {
  let target;
  for (let i = 0; i < 40 && !target; i++) { try { target = (await (await fetch("http://127.0.0.1:9333/json")).json()).find((t) => t.type === "page"); } catch {} if (!target) await sleep(250); }
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r) => ws.addEventListener("open", r));
  ws.addEventListener("message", (m) => { const d = JSON.parse(m.data); if (d.id && pending.has(d.id)) { pending.get(d.id)(d.result); pending.delete(d.id); } });
  await send("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 3, mobile: true });
  await send("Emulation.setTouchEmulationEnabled", { enabled: true, maxTouchPoints: 5 });
  await send("Page.navigate", { url });
  await sleep(5000);
  const ev = async (expr) => (await send("Runtime.evaluate", { expression: expr, returnByValue: true })).result.value;
  const slot = await ev("slot");
  let n = 0; const fs = await import("node:fs");
  const shot = async () => { const b = Buffer.from(await (await fetch(`http://127.0.0.1:7426/frames/7958/team/${slot}.jpg?${Date.now()}`)).arrayBuffer()); fs.writeFileSync(`/tmp/cdp/shot${n++}.jpg`, b); return b.length; };
  await ev("window.__cams=0; const _f=window.fetch; window.fetch=(u,...a)=>{ if(String(u).includes('/cam?')) window.__cams++; return _f(u,...a); }; 1");
  const before = await ev("document.getElementById('f').naturalWidth");
  const t = (type, pts) => send("Input.dispatchTouchEvent", { type, touchPoints: pts });
  const sizes0 = await shot();
  // one-finger swipe left
  await t("touchStart", [{ x: 300, y: 420 }]);
  for (let k = 1; k <= 10; k++) { await t("touchMove", [{ x: 300 - k * 18, y: 420 }]); await sleep(30); }
  await t("touchEnd", []);
  await sleep(1500);
  const sizes1 = await shot();
  // pinch out
  await t("touchStart", [{ x: 180, y: 420, id: 1 }, { x: 210, y: 420, id: 2 }]);
  for (let k = 1; k <= 10; k++) { await t("touchMove", [{ x: 180 - k * 8, y: 420, id: 1 }, { x: 210 + k * 8, y: 420, id: 2 }]); await sleep(30); }
  await t("touchEnd", []);
  await sleep(1500);
  const sizes2 = await shot();
  console.log(JSON.stringify({ slot, streamWidth: before, camRequests: await ev("window.__cams"), frameBytes: [sizes0, sizes1, sizes2] }));
} finally { chrome.kill(); }
