# Pezz on CapRover

The public arena, https://pezz.sethwebster.com (also https://pezz-staging.sethwebster.com), is one CapRover app, `pezz-staging` on captain-01. It replaced room 1 on the Mac on 2026-10-12: the game, its seats and its links moved across. The app holds:
- **Room 1:** the headless engine.
- **The gateway** in front of it: `/join`, `/mcp`, `/view`, `/watch`, `/play`, `/admin`.
- **Extra rooms:** the gateway opens them in the same container when room 1 is full.
- **The browser build:** Unity Web, the default viewer.

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
   - the env vars: `PEZZ_PUBLIC_URL`, the operator secret, `PEZZ_ADMIN_REMOTE=1`, `PEZZ_CLIENT_IP_HEADER=x-real-ip`, and the Claude token.
3. **Upload.** It uploads the bundle and CapRover builds `deploy/caprover/Dockerfile`. The old container gets SIGTERM, and every engine in it saves its game. The new container resumes them all: same seats, tokens and commander links.
4. **Domains and check.** It adds each domain with HTTPS once, then waits for `/healthz` to report the new build with room 1 up.

**DNS:** both names are DNS-only CNAMEs to `pezz-staging.projects.sethwebster.com`, in Cloudflare. The `pezz` Cloudflare tunnel on the Mac now carries only pezz-ops.sethwebster.com.

**Moving a game between servers** (as done for the cutover):
1. Post a notice (`maintenance on 60 ... --preserved`) and stop the old server; the engine saves on SIGTERM.
2. Scale the app to 0 instances.
3. Copy these into `/data/.config/pezz` on the volume, owned by uid 10001: `rooms/game-7777.json`, `rooms.json` (the room code), `gateway-sessions.json`, `commanders.json`, `seen-changes.json`, `replays/` and `inventions/`.
4. Scale back to 1. Seats, tokens, links and MCP sessions all carry over.

## Logins and secrets

Everything lives on this Mac, owner-only, and is never printed:

| What | Where | Notes |
|---|---|---|
| CapRover login | `~/.config/configstore/caprover.json` | From `ASDF_NODEJS_VERSION=22.22.0 caprover login -n captain-01`. It expires. `caprover list` still says "logged in" afterwards, because it doesn't check. |
| The app's deploy token | `~/.config/pezz/pezz-staging-deploy-token` | Saved by the first logged-in deploy. It doesn't expire. With only this, deploys still upload new builds, but can't change settings. |
| Claude token for the team-name check | `~/.config/pezz/pezz-staging-claude-token` | Run `claude setup-token`, then `pbpaste > ~/.config/pezz/pezz-staging-claude-token; chmod 600 ~/.config/pezz/pezz-staging-claude-token`. The next logged-in deploy sets it as the app's `CLAUDE_CODE_OAUTH_TOKEN`. Without it, teams keep their fruit names. |
| Operator secret | `~/.config/pezz/pezz-staging-admin-secret` | Made by the first deploy. |

## Running it

- **State:** `/data` on the `pezz-staging-data` volume holds:
  - every room's save (`.config/pezz/rooms/game-<port>.json`);
  - the gateway's rooms, seats, MCP sessions, commander links and replays;
  - all logs (`/data/logs`: `room1.log`, `gateway.log`, `room-<n>.log`).
- **Supervisor:** `deploy/caprover/serve.mjs`. If room 1 or the gateway dies on its own, it saves the other rooms and exits, and CapRover starts a new container that resumes them.
- **Operator commands:** over HTTPS, with the secret:

  ```bash
  node deploy/caprover/admin.mjs fleet                          # rooms
  node deploy/caprover/admin.mjs room 1 status
  node deploy/caprover/admin.mjs room 1 announce '{"text":"Server restarts in 5 minutes"}'
  node deploy/caprover/admin.mjs room 2 save '{}'
  ```

  Any `/api/admin/<cmd>` of a room's engine works the same way: `kick`, `speed`, `restart`, `orders`, `viewlink`, and so on.
- **Health:** `https://pezz.sethwebster.com/healthz` returns the build, whether room 1 answers, and the address the rate limits see for you.

## Performance

Measured on an M4. The server is likely slower per core, but has plenty of headroom:

| What | Cost |
|---|---|
| An engine | 1–4% of a core, 80–100 MB |
| One player's fogged view of a 320-tile map | ~108 KB (~31 KB gzipped), 12–19 ms to build, about once a second |
| Eight players on a big map | ~12% of a core and ~2 Mbit/s out |
| Spectators | Share one delayed copy, so little |
| A new visitor's first load | 10.3 MB (the browser build) |
