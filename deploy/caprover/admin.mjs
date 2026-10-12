#!/usr/bin/env node
// Operator commands for a Pezz server on CapRover, over HTTPS with the operator secret (~/.config/pezz/<app>-admin-secret,
// made by deploy.mjs). The secret is sent, never printed.
//
//   node deploy/caprover/admin.mjs fleet                          rooms and hosts
//   node deploy/caprover/admin.mjs room <n> status                a room's engine status
//   node deploy/caprover/admin.mjs room <n> <admin cmd> ['<json>']  POST /api/admin/<cmd> on room n (save, kick, announce,
//                                                                 speed, restart, orders, ...); GET when there's no JSON
// Options: --app pezz-staging, --domain pezz-staging.sethwebster.com
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const argv = process.argv.slice(2);
const opt = (n, d) => { const i = argv.indexOf("--" + n); if (i < 0) return d; const v = argv[i + 1]; argv.splice(i, 2); return v; };
const APP = opt("app", "pezz-staging");
const DOMAIN = opt("domain", "pezz-staging.sethwebster.com");
const secret = fs.readFileSync(path.join(os.homedir(), ".config", "pezz", `${APP}-admin-secret`), "utf8").trim();
const [what, n, cmd, json] = argv;
let p, body;
if (what === "fleet") p = "/admin/fleet";
else if (what === "room" && n && cmd) {
  p = `/admin/rooms/${Number(n)}/api/${cmd === "status" || cmd === "lobby" ? cmd : "admin/" + cmd}`;
  body = json;
} else { console.error("usage: admin.mjs fleet | room <n> status | room <n> <admin cmd> ['<json>']"); process.exit(2); }
const r = await fetch(`https://${DOMAIN}${p}`, { method: body ? "POST" : "GET", body, headers: { "x-pezz-admin": secret, ...(body ? { "content-type": "application/json" } : {}) } });
console.log(r.status, await r.text());
process.exit(r.ok ? 0 : 1);
