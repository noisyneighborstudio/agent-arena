#!/usr/bin/env node
// Deploy Pezz to CapRover: the engine, the gateway and the browser build, in one app with a persistent volume.
//
//   node deploy/caprover/deploy.mjs [--app pezz-staging] [--machine captain-01] [--domain pezz-staging.sethwebster.com]
//                                   [--web <browser build dir>] [--dry-run <dir>]
//
// Run from a clean checkout of the commit to ship (CI runs it from its checkout of the commit that passed the gate). It:
//   1. publishes the engine here (`dotnet publish`; a framework-dependent .dll, so no .NET SDK on the server) and stages it
//      with the gateway's tracked files and the browser build (--web, default build/web: Unity can't build on the server);
//   2. sets up the app if it's new (persistent volume /data, port 8080, websockets, the domain with HTTPS, a 30 s stop grace
//      so every room saves, the operator secret for /admin), changing only those settings: anything set in CapRover's
//      dashboard stays;
//   3. uploads the bundle; CapRover builds deploy/caprover/Dockerfile and swaps containers (the old one saves on SIGTERM);
//   4. waits until https://<domain>/healthz reports this build with room 1 answering. Exit 0 live, 1 failed.
// Login: the CapRover CLI's (`caprover login -n <machine>`, kept in ~/.config/configstore/caprover.json). Once it has
// deployed, the app's deploy token (~/.config/pezz/<app>-deploy-token) keeps CI's deploys going after the login expires.
// The team-name check's Claude token: ~/.config/pezz/<app>-claude-token (from `claude setup-token`, chmod 600) becomes the
// app's CLAUDE_CODE_OAUTH_TOKEN. It's never printed. Without it, teams keep their fruit names.
import { execFileSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf("--" + n); return i >= 0 && args[i + 1] ? args[i + 1] : d; };
const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const APP = opt("app", "pezz-staging");
const MACHINE = opt("machine", "captain-01");
const DOMAIN = opt("domain", "pezz-staging.sethwebster.com");
const WEB = path.resolve(opt("web", path.join(ROOT, "build", "web")));
const DRY = opt("dry-run", "");
const log = (s) => console.log(`${new Date().toISOString().replace("T", " ").slice(0, 19)} caprover: ${s}`);
const temp = []; // the staged bundle, removed however this ends
const die = (s) => { log(s); for (const f of temp) fs.rmSync(f, { recursive: true, force: true }); process.exit(1); };
const sh = (cmd, a, o = {}) => execFileSync(cmd, a, { cwd: ROOT, encoding: "utf8", ...o });

// ------------------------------------------------------------------ 1. the bundle
if (sh("git", ["status", "--porcelain", "--untracked-files=no"]).trim()) die("the checkout has uncommitted changes: deploy a commit");
if (!fs.existsSync(path.join(WEB, "index.html"))) die(`no browser build at ${WEB} (--web <dir>)`);
const commit = sh("git", ["rev-parse", "HEAD"]).trim();
const build = `${commit.slice(0, 7)} ${new Date().toISOString().slice(0, 16)}Z`;
const stage = DRY ? path.resolve(DRY) : fs.mkdtempSync(path.join(os.tmpdir(), "pezz-caprover-"));
fs.rmSync(stage, { recursive: true, force: true });
fs.mkdirSync(stage, { recursive: true });
if (!DRY) temp.push(stage);
log(`staging ${commit.slice(0, 7)} in ${stage}`);
const tracked = sh("git", ["ls-files", "-z", "--", "mcp", "arena/web3d", "unity/Assets/Pez/Resources/PezIcons", "unity/Assets/Pez/Resources/PezModels", "deploy/caprover"])
  .split("\0").filter((f) => f && !/(^|\/)(node_modules|\.DS_Store)|\.meta$|\.test\.js$/.test(f));
for (const f of tracked) { fs.mkdirSync(path.join(stage, path.dirname(f)), { recursive: true }); fs.copyFileSync(path.join(ROOT, f), path.join(stage, f)); }
sh("dotnet", ["publish", "headless/Pez.Headless.csproj", "-c", "Release", "-p:UseAppHost=false", "-o", path.join(stage, "engine")], { stdio: ["ignore", "ignore", "inherit"] });
fs.cpSync(WEB, path.join(stage, "build", "web"), { recursive: true });
fs.writeFileSync(path.join(stage, "BUILD_INFO"), build + "\n");
sh("chmod", ["-R", "a+rX,go-w", stage]); // the container's user reads the files root owns (Unity writes the build owner-only)
fs.writeFileSync(path.join(stage, "captain-definition"), JSON.stringify({ schemaVersion: 2, dockerfilePath: "./deploy/caprover/Dockerfile" }, null, 1));
const tar = path.join(os.tmpdir(), `pezz-${APP}-${commit.slice(0, 7)}.tar`);
if (!DRY) temp.push(tar);
sh("tar", ["-cf", tar, "-C", stage, "."], { env: { ...process.env, COPYFILE_DISABLE: "1" } });
log(`bundle ${(fs.statSync(tar).size / 1e6).toFixed(1)} MB (${tracked.length} files, engine, browser build)`);
if (DRY) { log(`dry run: bundle at ${tar}, staged files in ${stage}`); process.exit(0); }

// ------------------------------------------------------------------ 2. the app
// The CLI's login can configure the app. It expires (CapRover tokens do), so the first logged-in deploy also turns on the
// app's own deploy token (~/.config/pezz/<app>-deploy-token, owner-only): with only that, a deploy still uploads the new
// build (CI keeps shipping), it just can't change the app's settings.
const PEZZ = path.join(os.homedir(), ".config", "pezz");
const machines = (() => { try { return JSON.parse(fs.readFileSync(path.join(os.homedir(), ".config", "configstore", "caprover.json"), "utf8")).CapMachines ?? []; } catch { return []; } })();
const M = machines.find((m) => m.name === MACHINE);
const BASE = M?.baseUrl ?? opt("captain", "https://captain.projects.sethwebster.com");
const deployTokenFile = path.join(PEZZ, `${APP}-deploy-token`);
const writeSecret = (f, v) => { fs.mkdirSync(path.dirname(f), { recursive: true, mode: 0o700 }); fs.writeFileSync(f, v + "\n", { mode: 0o600 }); };
async function api(p, body, { raw = false, appToken = null } = {}) {
  const headers = appToken ? { "x-captain-app-token": appToken, "x-namespace": "captain" } : { "x-captain-auth": M?.authToken ?? "", "x-namespace": "captain" };
  const init = { method: body === undefined ? "GET" : "POST", headers, signal: AbortSignal.timeout(300000) };
  if (body instanceof FormData) init.body = body;
  else if (body !== undefined) { init.body = JSON.stringify(body); headers["content-type"] = "application/json"; }
  const j = await (await fetch(`${BASE}/api/v2${p}`, init)).json();
  if (!raw && j.status !== 100 && j.status !== 101) throw new Error(`${p}: ${j.status} ${j.description}`); // 101: a detached deploy started
  return j;
}
const loggedIn = M ? (await api("/user/apps/appDefinitions", undefined, { raw: true })).status === 100 : false;
const deployToken = fs.existsSync(deployTokenFile) ? fs.readFileSync(deployTokenFile, "utf8").trim() : "";
if (!loggedIn && !deployToken) die(`not logged in to CapRover (${MACHINE}) and no deploy token: run ASDF_NODEJS_VERSION=22.22.0 caprover login -n ${MACHINE}`);
const appDef = async () => (await api("/user/apps/appDefinitions")).data.appDefinitions.find((a) => a.appName === APP);
/** An app definition as appDefinitions/update takes it: domains, versions and build state are managed elsewhere. */
const writable = (d) => { const { customDomain, versions, deployedVersion, isAppBuilding, hasDefaultSubDomainSsl, ...rest } = d; return rest; };

if (loggedIn) {
  let def = await appDef();
  if (!def) {
    log(`creating app ${APP} (persistent)`);
    await api("/user/apps/appDefinitions/register", { appName: APP, hasPersistentData: true });
    def = await appDef();
  }
  if (!def.hasPersistentData) die(`${APP} exists without persistent data; its games would be lost on every deploy. Delete it in CapRover and rerun.`);
  const env = new Map((def.envVars ?? []).map((e) => [e.key, e.value]));
  env.set("PEZZ_PUBLIC_URL", `https://${DOMAIN}`);
  const tokenFile = [`${APP}-claude-token`, "caprover-claude-token"].map((f) => path.join(PEZZ, f)).find((f) => fs.existsSync(f));
  const claude = tokenFile ? fs.readFileSync(tokenFile, "utf8").trim() : "";
  if (claude) env.set("CLAUDE_CODE_OAUTH_TOKEN", claude);
  else if (!env.has("CLAUDE_CODE_OAUTH_TOKEN")) log(`no Claude token (${path.join(PEZZ, `${APP}-claude-token`)}): team names stay fruit names until one is set`);
  // The operator secret for /admin over HTTPS (deploy/caprover/admin.mjs uses it): made here once, kept owner-only.
  const adminFile = path.join(PEZZ, `${APP}-admin-secret`);
  if (!fs.existsSync(adminFile)) writeSecret(adminFile, randomBytes(24).toString("hex"));
  env.set("PEZZ_ADMIN_SECRET", fs.readFileSync(adminFile, "utf8").trim());
  env.set("PEZZ_ADMIN_REMOTE", "1");
  env.set("PEZZ_CLIENT_IP_HEADER", "x-real-ip"); // CapRover's nginx sets it to the caller; X-Forwarded-For can be forged
  await api("/user/apps/appDefinitions/update", {
    ...writable(def),
    appName: APP,
    instanceCount: 1,                // one game state; rooms live in this container
    containerHttpPort: 8080,
    notExposeAsWebApp: false,
    websocketSupport: true,
    volumes: (def.volumes ?? []).some((v) => v.containerPath === "/data") ? def.volumes : [...(def.volumes ?? []), { containerPath: "/data", volumeName: `${APP}-data` }],
    envVars: [...env].map(([key, value]) => ({ key, value })),
    // Docker's default 10 s stop is tight for several rooms saving at once.
    serviceUpdateOverride: def.serviceUpdateOverride || JSON.stringify({ TaskTemplate: { ContainerSpec: { StopGracePeriod: 30e9 } } }),
    appDeployTokenConfig: def.appDeployTokenConfig?.enabled ? def.appDeployTokenConfig : { enabled: true },
  });
  const tok = (await appDef()).appDeployTokenConfig?.appDeployToken;
  if (tok && tok !== deployToken) writeSecret(deployTokenFile, tok);
  log(`app ${APP} configured (volume /data, port 8080, websockets, deploy token kept${env.has("CLAUDE_CODE_OAUTH_TOKEN") ? ", Claude token set" : ""})`);
} else log(`CapRover login expired: deploying with ${APP}'s deploy token (settings unchanged; caprover login to change them)`);

// ------------------------------------------------------------------ 3. upload, build, swap
const form = new FormData();
form.append("sourceFile", new Blob([fs.readFileSync(tar)]), path.basename(tar));
log(`uploading to ${BASE} (CapRover builds the image, then swaps containers; the old one saves every room)`);
await api(`/user/apps/appData/${APP}?detached=1`, form, { appToken: loggedIn ? null : deployToken });
fs.rmSync(tar, { force: true });
fs.rmSync(stage, { recursive: true, force: true });
if (loggedIn) {
  let last = 0, failed = false;
  for (const end = Date.now() + 25 * 60000; Date.now() < end;) {
    await new Promise((r) => setTimeout(r, 4000));
    const d = (await api(`/user/apps/appData/${APP}`, undefined, { raw: true })).data ?? {};
    const lines = d.logs?.lines ?? [];
    for (const l of lines.slice(last)) if (/^Step \d+\/|^#\d+ \[|error|Error|Successfully built|Deployed/.test(l.trim())) log("  " + l.trim().slice(0, 160));
    last = lines.length;
    if (!d.isAppBuilding) { failed = !!d.isBuildFailed; break; }
  }
  if (failed) die("the image build failed (log above; the old container keeps running)");

  // The domain, and HTTPS for it (once; Let's Encrypt needs the DNS record to point at the server already).
  let def = await appDef();
  if (!(def.customDomain ?? []).some((d) => d.publicDomain === DOMAIN)) {
    await api("/user/apps/appDefinitions/customdomain", { appName: APP, customDomain: DOMAIN });
    log(`added ${DOMAIN}`);
    def = await appDef();
  }
  if (!(def.customDomain ?? []).find((d) => d.publicDomain === DOMAIN)?.hasSsl) {
    await api("/user/apps/appDefinitions/enablecustomdomainssl", { appName: APP, customDomain: DOMAIN });
    log(`HTTPS on for ${DOMAIN}`);
    def = await appDef();
  }
  if (!def.forceSsl) await api("/user/apps/appDefinitions/update", { ...writable(def), forceSsl: true });
}

// ------------------------------------------------------------------ 4. live?
for (const end = Date.now() + (loggedIn ? 5 : 25) * 60000; Date.now() < end;) {
  try {
    const h = await (await fetch(`https://${DOMAIN}/healthz`, { signal: AbortSignal.timeout(5000) })).json();
    if (h.build === build && h.ok) { log(`live: https://${DOMAIN} on ${build}`); process.exit(0); }
  } catch {}
  await new Promise((r) => setTimeout(r, 5000));
}
die(`https://${DOMAIN}/healthz never reported ${build} with room 1 up`);
