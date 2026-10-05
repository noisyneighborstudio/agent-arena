# Spike: render Pezz in the browser

## Question

Can a browser draw a live Pezz room in 3D from the game's own view data (`/api/view/frame` and `/api/view/map`), using
the same glTF models, close enough to the Unity look to replace the streamed video for most viewers?

It's settled if all four hold:
1. **Looks.** Side-by-side stills of the same scene, browser beside Unity's observer camera, read as the same game:
   the models, team colours, terrain and fog.
2. **Smooth.** 60 fps on a desktop browser, and at least 30 fps on a phone-class device (Chrome mobile emulation with
   4× CPU throttle).
3. **Cheaper.** Bandwidth per viewer far below the MJPEG stream (0.5–0.8 MB/s), measured.
4. **Every room.** It works for a room with no renderer (a room host's room), which video never can.

## Setup

- **Branch:** `spike/browser-render`. Code in `arena/web3d/` (three.js 0.170, vendored), served by the sessions
  dashboard at `/b3d/`.
- **Data:** the same per-team view frame the tactical viewer polls. Positions are interpolated between polls.
- **Test scene:** the preview copy (`arena/preview.sh`, AI vs AI) and room 1. The Unity reference is an observer
  camera aimed at the same spot.

## Results

All measured in headless Chrome 2026-10-05 on this Mac: Apple M4 Max GPU via ANGLE/Metal, three.js 0.170.

| Scene | Things drawn | fps | Draw calls | Models loaded (once) | Data, raw JSON | Data, gzipped (as Cloudflare serves it) |
|---|---|---|---|---|---|---|
| Preview game, team 0's base (1280×720) | 11–14 | 60 | 134–153 | 121–140 KB | 40–44 KB/s | ~6 KB/s |
| Same, phone emulation (390×844 @3×, CPU throttled 4×) | 14 | 60 | 151 | 140 KB | 44 KB/s | ~6 KB/s |
| Kitchen sink: every model in every state | 186 | 60 | 243 | 884 KB | 190 KB/s | ~20 KB/s |
| A room on the Mac mini's room host (no renderer there) | 9 | 60 | 113 | 112 KB | 26 KB/s | ~4 KB/s |

The streamed video it would replace runs at 0.5–0.8 MB/s per viewer: the browser needs **40–100× less data**. It
also needs **no GPU on our side** per viewer.

### Looks

- **First render** (`browser-render/compare-first.jpg`, Unity's observer on the left): the right layout, way round
  and models, but washed out. The palette's sRGB values had been read as linear.
- **After matching Unity's look** (`browser-render/compare-matched.jpg`): the `Look.cs` sun and trilight ambient (with
  three's ÷π), sRGB-correct colours, smooth ground with the faint tile grid, and soft ore fields. The two read as
  the same game: team colours, models, ore, fog and shadows all match.
- **Other scenes:** `browser-render/kitchen-sink-186.jpg` (stress) and `browser-render/remote-room-mac-mini.jpg`
  (a room only the browser can show in 3D).

### What it does

- **Data:** the per-team view frame, polled 4×/s. Positions and facing are interpolated 350 ms in the past, so motion
  is smooth at the display's rate.
- **Models:** the same 34 glTF models Unity uses. Normals are creased at 35°, like Unity's SmoothByAngle;
  `M_Team` is tinted per team; `M_E_*` materials glow.
- **Scene:** terrain with rock, water, dirt and ore fields; a soft per-tile fog of war; tracers, hits and explosions
  (with a flash of light).
- **Camera:** the RTS camera (orthographic, 55° pitch) with drag/swipe pan, pinch/wheel zoom, rotate, and tap to
  follow. Look-only: nothing goes back to the game.

## Where it didn't win

- **Effects and dressing are thin.**
  - Unity has the power plants' open glowing stacks (`PowerCores`), steam, smouldering-to-raging fire, dust, wrecks,
    construction rising stage by stage, the trucks' docking and tipping, turret aim apart from the hull,
    walk cycles, and bloom and ambient occlusion.
  - The spike draws the base models, simple tracers and blasts, and squashes buildings under construction.
  - All of that is porting work, not a blocker.
- **The view data lacks a few fields the look needs:** turret facing, dock step, cargo type, altitude for landing
  aircraft, and the working and burning flags. Adding them is cheap. The frame is built in one place
  (`ApiServer.ViewFrame`).
- **Rocks** are raised blocks, not Unity's boulders.
- **No real phone yet.** The phone emulation throttles the CPU but uses this Mac's GPU. A real iPhone or Android
  device must be measured before shipping. The draw calls (113–243) and model sizes (≤ 884 KB) suggest it will be
  comfortable.
- **Not tested at scale:** 300+ units. Instancing per model is the known fix if needed.

## Verdict

**Go.** Browser rendering reads as the same game. It runs at 60 fps on desktop with the full kitchen sink. It needs a
small fraction of the video's bandwidth. It gives 3D to every room, including rooms on room hosts, which can never
have a Unity renderer.

Its limits, plainly: effects parity is the work still ahead, and real-phone performance is unmeasured.

## Plan

1. **Ship it as a view.** The gateway serves `/view/<token>/3d` (a player's own fogged view) and `/watch/3d` (the
   delayed spectator), reading the frame and map endpoints those pages already use. Room hosts' rooms get 3D the
   moment this lands.
2. **Fill in the view data:** turret facing, dock step, cargo, altitude, working and burning.
3. **Port the visible systems, most noticed first:** explosions, fire stages and smoke; power-plant stacks and steam;
   construction rising; wrecks; docking and tipping; turret aim; infantry walk.
4. **Measure on real phones** (iPhone, mid-range Android), then make 3D the default view, with the MJPEG video kept
   for room 1's cinematic stream and as a fallback.
