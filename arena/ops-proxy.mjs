// The operator's link to every Pezz session from anywhere: the sessions dashboard (arena/sessions.py, look-only
// viewers of every room, seat and agent) behind a secret, for pezz-ops.sethwebster.com via the Cloudflare tunnel.
//
//   node arena/ops-proxy.mjs [--port 7428]      (LaunchAgent com.sethwebster.pezz-ops)
//
// https://pezz-ops.sethwebster.com/k/<key> sets an HttpOnly cookie and lands on the dashboard; anything without the
// cookie is a bare 404, so the hostname gives nothing away. The key is ~/.config/pezz/ops-key (made on first run);
// replace the file and restart to revoke every browser. The dashboard is still on the tailnet at :8458 as before.
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";
import { spawn } from "node:child_process";

const args = process.argv.slice(2);
const PORT = Number(args.includes("--port") ? args[args.indexOf("--port") + 1] : 7428);
const UPSTREAM = { host: "127.0.0.1", port: 7426 };
const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname), "..");
const KEY_FILE = path.join(os.homedir(), ".config", "pezz", "ops-key");
const COOKIE = "pezz_ops";

const KEY = (() => {
  try { const k = fs.readFileSync(KEY_FILE, "utf8").trim(); if (k.length >= 24) return k; } catch {}
  fs.mkdirSync(path.dirname(KEY_FILE), { recursive: true, mode: 0o700 });
  const k = crypto.randomBytes(24).toString("base64url");
  fs.writeFileSync(KEY_FILE, k + "\n", { mode: 0o600 });
  return k;
})();

const same = (a) => { const x = Buffer.from(String(a)), y = Buffer.from(KEY); return x.length === y.length && crypto.timingSafeEqual(x, y); };
const cookieKey = (req) => (req.headers.cookie || "").split(/;\s*/).map((c) => c.split("=")).find(([n]) => n === COOKIE)?.[1];
const quiet = { "x-robots-tag": "noindex, nofollow", "referrer-policy": "no-referrer" };

// The dashboard isn't a LaunchAgent of its own: if it's down (a reboot), bring it up, at most once a minute.
let lastRevive = 0;
function revive() {
  if (Date.now() - lastRevive < 60000) return false;
  lastRevive = Date.now();
  spawn("/bin/zsh", [path.join(REPO, "arena/sessions.sh"), "up"], { stdio: "ignore", detached: true }).unref();
  return true;
}

function proxy(req, res, retry = true) {
  const headers = { ...req.headers, host: `${UPSTREAM.host}:${UPSTREAM.port}` };
  delete headers.cookie; // the dashboard has no use for our key
  const up = http.request({ ...UPSTREAM, method: req.method, path: req.url, headers }, (r) => {
    res.writeHead(r.statusCode, { ...r.headers, ...quiet });
    r.pipe(res); // MJPEG streams stay open: pipe, never buffer
  });
  up.on("error", () => {
    if (retry && revive()) return setTimeout(() => proxy(req, res, false), 2500);
    if (!res.headersSent) res.writeHead(502, { "content-type": "text/plain", ...quiet });
    res.end("The sessions dashboard isn't answering. On the host: arena/sessions.sh up\n");
  });
  req.pipe(up);
  res.on("close", () => up.destroy());
}

http.createServer((req, res) => {
  const m = req.url.match(/^\/k\/([A-Za-z0-9_-]+)\/?$/);
  if (m && same(m[1])) {
    // A month, on this hostname only; the redirect drops the key from the address bar and history.
    res.writeHead(302, { "set-cookie": `${COOKIE}=${KEY}; Path=/; Max-Age=2592000; HttpOnly; Secure; SameSite=Lax`, location: "/", ...quiet });
    return res.end();
  }
  if (!same(cookieKey(req) ?? "")) { res.writeHead(404, { "content-type": "text/plain", ...quiet }); return res.end("Not found\n"); }
  proxy(req, res);
}).listen(PORT, "127.0.0.1", () => console.log(`pezz ops proxy on 127.0.0.1:${PORT} -> ${UPSTREAM.host}:${UPSTREAM.port}`));
