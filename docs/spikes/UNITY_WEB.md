# Spike: the Unity game itself in the browser

**Question.** Can viewers get the real Pezz look (the Unity renderer, its shaders, post-processing and art) in their own
browser, rendering on their own GPU from game state the server sends, instead of the MJPEG video room 1 renders today?

**Answer: yes.** A Unity Web build of the existing view code renders a saved or live game in Chrome at the native app's
quality and 60 fps, from a 9.6 MB download. Next to the native app at the same camera pose, the two are hard to tell
apart. The server only has to send game state, about 42 KB a second gzipped instead of about 1.8 MB a second of video, and no
longer needs a GPU, so the arena can move to plain cloud machines (CapRover).

Branch `spike/unity-web`. This replaces the three.js approach in `BROWSER_RENDER.md`: that one re-implemented the
look and lost it. This one ships the look.

## How it works

```
headless engine / room 1 ──GET /api/view/snapshot──► gateway (/watch/<n>/snapshot, 45 s delayed)
                                                          │  JSON snapshot, gzip
                                                          ▼
                     browser: Unity Web build (WebClient) ── Snapshot.Parse → Game.Restore → the usual WorldView
                     ticks the same deterministic Sim locally between snapshots; swaps each fresher world in under the view
```

- **The client is the game.** `GameRunner` on the web player skips the API, frame server, player streams, saves and
  agent launching, and adds `View/WebClient.cs`. That fetches a snapshot, restores it and lets the normal Sim tick it,
  AIs included. The Sim has no UnityEngine dependencies and its own saved RNG, so the browser's copy runs in step with the
  server's.
- **Live mode** (`live=N` seconds): each fresher snapshot is restored into a new `World` and swapped under the running
  view (`GameRunner.AdoptWorld` → `WorldView.Rebind`). Entity ids are stable, so models, animation state and effects
  carry over. The swap:
  - steps the new world up to the local tick (`Game.CatchUp`), so time never runs backwards;
  - eases corrected positions over about 0.2 s;
  - keeps shells in flight.
- **Page params:**

  | Param | Meaning |
  |---|---|
  | `snapshot=` | the snapshot URL |
  | `live=` | seconds between fetches; 0 means static |
  | `team=` | the team whose view to show; -1 for a spectator |
  | `cam=x,y,zoom` | the starting camera pose |
  | `q=0..5` | quality level; 5 = Ultra, as on the Mac |
  | `fps=1` | show a frame-rate label |
  | `dpr=` | override the render scale |
- **Server:** `GET /api/view/snapshot` returns the snapshot as JSON, gzipped and `no-store`. It's spectator only and
  strips tokens, orders text and `orders` events. The gateway serves it at `/watch[/<n>]/snapshot` behind the same
  45 s delay as the `/watch` frames.
- **Build:** `PezSetup.BuildWeb`
  ```
  Unity -batchmode -buildTarget WebGL -projectPath unity -executeMethod Pez.EditorTools.PezSetup.BuildWeb -pezOut ../build/web -quit
  ```
  - Settings: WebGL2, Brotli, `-pezDev` for a development build with strict shader variants.
  - The page template is `Assets/WebGLTemplates/PezWeb`: full window, a progress bar, `window.unityInstance`.
  - The build takes 2–4 minutes.

## Results

All measured on this Mac (M4 Max) in Chrome, through Metal (ANGLE), at 1728×1080. Tools: `arena/webclient/measure.mjs`,
`live.mjs` and `native-shot.sh`.

| | |
|---|---|
| Download | **9.6 MB** Brotli: wasm 6.4 MB, data 3.1 MB, framework 0.08 MB |
| Engine ready | 1.0–1.5 s |
| Game on screen | 3.3–3.7 s (snapshot fetch, parse 7–13 ms, restore 3–14 ms); smooth from ~4 s |
| Frame rate | 60 fps at the vsync cap, at dpr 1 and dpr 2 (3456×2160). Uncapped: 139 fps (early game), 91 fps (92 entities, dpr 1), 65 fps (dpr 2) |
| Live resync | ~5 ms per swap (parse 3 ms + restore 1.4 ms + rebind 0.1 ms, ~180 KB snapshots); 60 fps avg, p5 56, no frame over 50 ms at 1 s intervals |
| Drift | the browser's world was at exactly the snapshot's tick in 83 of 88 swaps, one tick behind in the rest (then caught up). 99.5% of units within 0.05 tiles of the server between snapshots; only units given a new command differ, ≤1.6 tiles at 1 s spacing, eased. Shells keep id, shooter and target across a swap, so their trails carry on |
| State bandwidth | a mid-game snapshot is 237 KB raw / 42 KB gzip; at 1 Hz that's ~0.04 MB/s per viewer vs ~1.8 MB/s for the MJPEG stream |
| Look | native vs browser at the same pose: same models, shadows, AO, bloom, tonemap and HUD. Only the minimap reads a little darker |

Comparison stills are on the progress dashboard: native on the left, browser on the right.

## What it took

- **Web guards.** `GameRunner.IsWeb` and `GameRunner.Args`; the latter is empty in the browser. Nothing that needs
  sockets, threads, processes or files starts on the web.
- **The fluid fire is off on the web.** `FireSim.Supported` returns false there, and its worker thread would never finish. Burning buildings
  use the existing particle fire (`BuildingFire`).
- **Engine code stripping off.** The view builds parts from primitives at runtime, and no scene references their
  collider classes, so Unity stripped them and models failed to build.
- **Instancing variants kept** (`m_InstancingStripping: 2`). Model materials are made at runtime, so the build
  couldn't see that instancing is used, and models drew magenta.
- **Unity Analytics and In-App Purchasing removed.** Both were unused leftovers from the template, and they made the player
  call `config.uca.cloud.unity3d.com` and `cdp.cloud.unity3d.com`. **The macOS build presumably does the same today.
  Remove them on main too.**
- **Ultra quality on the web.** The web player's default quality level was High; Standalone's is Ultra.
- **The browser view only watches.** The HUD hides speed and pause, the open-arena banner and the orders box, because in the
  browser those would only change the local copy.
- **`HullFeel` hardened.** Its spring diverged to NaN on long frames (a slow device, a throttled tab). Clamped dt and
  added a NaN reset. Native benefits too.

## Limits found

- **No WebGPU.** Unity 6.3's WebGPU backend needs URP; Pezz is on the Built-in pipeline. WebGL2 runs everywhere and
  is plenty for this look. The one loss is the GPU fluid fire, which needs compute shaders. Moving to URP is a large
  project: every custom shader would need porting. Not worth it for one effect yet.
- **Spectator only.** The snapshot is the whole world. Player views need a per-team filter first. That means the fog
  rules in `ApiServer.ViewFrame` applied to the snapshot, plus stripping other teams' stock, queues, orders, visibility
  and remembered structures. Until then, the public route stays delayed like `/watch`.
- **Safari (2026-10-12):** ran at ~17 fps at 2880×1800, whatever the quality level or render scale. Unity calls
  `invalidateFramebuffer` ~4 times a frame, and WebKit's ANGLE-on-Metal then zero-fills those attachments on the CPU in its GPU
  process at the next `clear()` (`RenderbufferMtl::initializeContents` → `replaceRegion`), ~27 ms a frame, which surfaced as Unity's
  per-frame `clientWaitSync` taking 27 ms. The page template now ignores `invalidateFramebuffer` on WebKit (a hint, so always
  correct): 60 fps, same picture. `?glinvalidate=1` turns the workaround off. Measured in Playwright's WebKit on the Apple GPU;
  to profile again, time the WebGL calls from the page and `sample` the `com.apple.WebKit.GPU` process.
- **Not yet measured:** phones, Firefox, weak GPUs, and a congested network.
- **HUD:** in the 4-AI test the team cards switch between their compact and full forms for ~12 s at a time, and the right panel's first team tab is truncated. Possibly native behaviour too; check before shipping.
- **Throttled tabs.** A hidden or throttled tab runs at about 1 fps. That's fine for watching, and the next resync
  catches it up.

## To make it the product

1. **Fogged per-team snapshots** (`/api/view/snapshot?team=N` through the `ViewFrame` rules) and a live gateway route
   on the player's view token: `/view/<token>/snapshot`.
2. **Serve the build** from the gateway, for example `/view/<token>/3d` and `/watch/3d`, in place of the three.js routes. Use
   hashed file names (`nameFilesAsHashes`) with long cache lifetimes so Cloudflare serves the 9.6 MB once.
3. **CI** builds the web player after the macOS build and deploys it with the gateway.
4. **The viewer page**: decide whether the Unity IMGUI HUD replaces the gateway's `viewer.html` overlay. Commander
   orders need a path back: the page's existing `/view/<V>/orders` POST, called from a small `.jslib` or from the page.
5. **Bandwidth and latency later**: deltas over the existing WebSocket feed (`arena/web3d/server/feed.mjs`) instead
   of whole snapshots, once viewer counts make 0.04 MB/s matter.
6. **Then the cloud.** Room 1's Unity renderer becomes optional. The gateway and headless rooms can run on CapRover;
   the Mac is just the CI builder.
7. **Retire the three.js view** (`arena/web3d`, the gateway's `/3d` routes) once 1–2 ship.
