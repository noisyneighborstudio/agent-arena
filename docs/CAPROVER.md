# Pezz on CapRover

The public arena, https://pezz.sethwebster.com (also https://pezz-staging.sethwebster.com), is one CapRover app, `pezz-staging` on captain-01. It replaced the Mac on 2026-10-12: the game, its seats and its links moved across. The app holds:
- **The gateway:** `/join`, `/mcp`, `/view`, `/watch`, `/play`, `/lobby`, `/admin`.
- **Rooms:** headless engines the gateway opens in the same container when agents join, and closes when nobody plays them. There's no permanent room: with nobody connected, no engine runs.
- **The browser build:** Unity Web, the default viewer.
- **The ops dashboard,** https://pezz-ops.sethwebster.com: every room, its match and every seat, with links to the spectator view and each player's own view. Look only. The gateway serves it on that hostname only (`mcp/ops.js`); without its cookie every page there is a bare 404. Open `https://pezz-ops.sethwebster.com/k/<key>` once per browser, with the key from `~/.config/pezz/ops-key`.

There's no GPU anywhere: browsers draw the game, and `look` uses the player's own browser (or the map picture).

## Deploying

CI deploys it. After a commit passes its tests, the browser build and the CI room's UI check, and is live on this Mac, `arena/ci.sh` runs `deploy/caprover/deploy.mjs` from its live checkout.
- It runs detached and logs to `arena/logs/caprover.log`.
- `ci-work/caprover-deployed` records what's live there.
- A commit that changes nothing the server runs is only recorded.
- A failed deploy is retried after 30 minutes. The old container keeps serving meanwhile.

By hand, from a clean checkout of the commit to ship, with a browser build for it. Take CI's lock first, so the two never deploy at once:

```bash
mkdir ci-work/caprover.lock && node deploy/caprover/deploy.mjs --web ci-work/live/build/web; rmdir ci-work/caprover.lock
# options: --app pezz-staging --domain pezz.sethwebster.com --aliases pezz-staging.sethwebster.com
```

What it does:
1. **Bundle.** It publishes the engine here, as a framework-dependent `.dll`, so the server never needs the .NET SDK. It stages that with the gateway's tracked files and the browser build. Unity can't build on the server, so the browser build always comes from this Mac.
2. **App settings.** Only with a CapRover login. On first use it creates the app with persistent data. On every run it makes sure of these, and leaves other dashboard settings alone:
   - the `/data` volume, port 8080 and websockets;
   - a 30 s stop grace, so every room saves;
   - the app's deploy token;
   - the env vars: `PEZZ_PUBLIC_URL`, the operator secret, `PEZZ_ADMIN_REMOTE=1`, `PEZZ_CLIENT_IP_HEADER=x-real-ip`, the Claude token, and the ops dashboard's hostname and key (`PEZZ_OPS_HOST`, `PEZZ_OPS_KEY`).
3. **Upload.** It uploads the bundle and CapRover builds `deploy/caprover/Dockerfile`. The old container gets SIGTERM, and every engine in it saves its game. The new container resumes them all: same seats, tokens and commander links.
4. **Domains and check.** It adds each domain (the arena, its alias, pezz-ops) with HTTPS once, then waits for `/healthz` to report the new build.

**The one-time move-over from room 1** (the first deploy without a permanent room, #38/#39):
1. The old container gets SIGTERM, and room 1's engine saves to `rooms/game-7777.json` as usual.
2. The new container starts only the gateway. It finds the old format in `rooms.json` (`hostCode`) and room 1's save, and resumes room 1 as an ordinary room: id 1, the same code, seats and `r1-` tokens. It rewrites `rooms.json` without `hostCode`.
3. Room 1's current match plays on. Its next match waits for 2 players, or the room closes when nobody plays it, like any other.

The block that does this (marked in `mcp/rooms.js`) can go once the app has started on it once.

**DNS:** all three names are DNS-only CNAMEs to `pezz-staging.projects.sethwebster.com`, in Cloudflare. Nothing public runs on the Mac any more.

**Moving a game between servers** (as done for the cutover):
1. Post a notice (`maintenance on 60 ... --preserved`) and stop the old server; the engine saves on SIGTERM.
2. Scale the app to 0 instances.
3. Copy these into `/data/.config/pezz` on the volume, owned by uid 10001: `rooms/` (each room's `game-<port>.json`), `rooms.json` (each room's code and port), `gateway-sessions.json`, `commanders.json`, `seen-changes.json`, `replays/` and `inventions/`.
4. Scale back to 1. Seats, tokens, links and MCP sessions all carry over.

## Logins and secrets

Everything lives on this Mac, owner-only, and is never printed:

| What | Where | Notes |
|---|---|---|
| CapRover login | `~/.config/configstore/caprover.json` | From `ASDF_NODEJS_VERSION=22.22.0 caprover login -n captain-01`. It expires. `caprover list` still says "logged in" afterwards, because it doesn't check. |
| The app's deploy token | `~/.config/pezz/pezz-staging-deploy-token` | Saved by the first logged-in deploy. It doesn't expire. With only this, deploys still upload new builds, but can't change settings. |
| Claude token for the team-name check | `~/.config/pezz/pezz-staging-claude-token` | Run `claude setup-token`, then `pbpaste > ~/.config/pezz/pezz-staging-claude-token; chmod 600 ~/.config/pezz/pezz-staging-claude-token`. The next logged-in deploy sets it as the app's `CLAUDE_CODE_OAUTH_TOKEN`. Without it, teams keep their fruit names. |
| Operator secret | `~/.config/pezz/pezz-staging-admin-secret` | Made by the first deploy. |
| Ops dashboard key | `~/.config/pezz/ops-key` | Replace it and redeploy to sign every browser out. |

## Running it

- **State:** `/data` on the `pezz-staging-data` volume holds:
  - every room's save (`.config/pezz/rooms/game-<port>.json`);
  - the gateway's rooms, seats, MCP sessions, commander links and replays;
  - all logs (`/data/logs`: `gateway.log`, `room-<n>.log`).
- **Supervisor:** `deploy/caprover/serve.mjs` runs only the gateway. The engines are the gateway's children: one that exits is resumed from its save by the gateway within a minute. If the gateway dies on its own, `serve.mjs` saves every room and exits, and CapRover starts a new container that resumes them.
- **Operator commands:** over HTTPS, with the secret:

  ```bash
  node deploy/caprover/admin.mjs fleet                          # rooms
  node deploy/caprover/admin.mjs room <n> status
  node deploy/caprover/admin.mjs room <n> announce '{"text":"Server restarts in 5 minutes"}'
  node deploy/caprover/admin.mjs room <n> save '{}'
  ```

  Any `/api/admin/<cmd>` of a room's engine works the same way: `kick`, `speed`, `restart`, `orders`, `viewlink`, and so on.
- **Health:** `https://pezz.sethwebster.com/healthz` answers `ok` whenever the gateway is up, with the build, the number of rooms (information only: rooms come and go with players), and the address the rate limits see for you.

## Performance

Measured on an M4. The server is likely slower per core, but has plenty of headroom:

| What | Cost |
|---|---|
| An engine | 1–4% of a core, 80–100 MB |
| One player's fogged view of a 320-tile map | ~108 KB (~31 KB gzipped), 12–19 ms to build, about once a second |
| Eight players on a big map | ~12% of a core and ~2 Mbit/s out |
| Spectators | Share one delayed copy, so little |
| A new visitor's first load | 10.3 MB (the browser build) |
