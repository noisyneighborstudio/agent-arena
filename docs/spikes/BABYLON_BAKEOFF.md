# Spike: three.js or Babylon.js for the browser renderer, and a WebSocket feed

## Question

The browser renderer (`BROWSER_RENDER.md`) is built on three.js. Would Babylon.js (WebGPU, a built-in glow layer and
particle system, and left-handed like Unity) serve viewers better? Separately, is a WebSocket delta feed worth it over
polling the view frame?

Babylon wins if it matches three.js on load time and frame rate on a slow phone, and looks clearly better.

## Setup

- **Branch:** `spike/browser-render`.
- **Renderers.** Both draw the same view data and the same 34 glTF models, with the same camera.
  - `arena/web3d/next.js` (three.js 0.186, postprocessing, N8AO, three.quarks), built to `three-bundle.js` and served
    as `/b3d/three.html`.
  - `arena/web3d/babylon/src/main.js` (Babylon 9.29: WebGPU with a WebGL2 fallback; glow layer, SSAO2, the default
    pipeline and particle systems), built to `bab.js` and served as `/b3d/bab.html`.
  - Build both with `cd arena/web3d/babylon && npm ci && npm run build && npm run build:three`.
- **Feed.** `arena/web3d/server/feed.mjs` sends the map, a full snapshot, then deltas (entities added, changed and
  removed, plus new effects) at 10 Hz. It uses JSON with permessage-deflate, and one poller serves every viewer of a
  view. The pages use it with `?feed=ws`.
- **Scenes.** The kitchen-sink copy (every model in every state, port 7947) and a test room. Unity's reference is the
  kitchen sink's team-1 observer camera. The live arena was never touched.
- **Machine.** Headless Chrome through CDP on this Mac (M4 Max). "Slow phone" means a 390×844 @3× viewport, CPU
  throttled 4×, a 150 KB/s link with 400 ms latency, and an empty cache.

## Results

| | three.js | Babylon.js |
|---|---|---|
| Download, gzipped (minified) | **360 KB** (1.1 MB) | 987 KB (4.1 MB) |
| Slow phone: ready at, then fps | **9.6 s, 60 fps** | 18 s, 45 fps |
| Desktop, kitchen sink (162 things, fire, steam) | 60 fps (WebGL2) | 60 fps (WebGPU) |
| Real iPhone | 60 fps (the plain renderer) | not measured |

**Feed, kitchen sink, 20 s:**

| | Wire | Updates |
|---|---|---|
| WebSocket deltas | **5.3 KB/s** | every sim tick |
| Polling the frame at 4 Hz, gzipped | 17.3 KB/s | 4 per second, with ticks skipped |

Both are far below the 0.5–0.8 MB/s of the MJPEG video.

### Looks

- **Glow** (`browser-render/bakeoff-glow.jpg`). Babylon's glow layer gave the power cores the soft halo the user
  liked.
  - three.js's bloom keyed on brightness, so it also lit sunlit white walls and could not match without them glowing.
  - Fix: a threshold (1.6) above any lit surface, with emissive parts at 6×. Only the glowing parts bloom, and the
    halo now matches Babylon's. That fix is in `fx.js` and `next.js`.
- **Fire** (`browser-render/bakeoff-fire.jpg`). Both show the smoulder-to-blaze stages, with smoke and firelight.
  Babylon's steam and smoke are denser; three's flames read better against the walls.
- **Orientation** (`browser-render/bakeoff-orientation.jpg`: Unity, three.js, Babylon). Comparing against Unity found
  two bugs, both now fixed:
  - **Both renderers** turned structures by `90° − facing`. Unity never turns a structure, only units.
  - **three.js** imported glTF with z negated, where glTFast negates x. That turned every model 180°.

  All three now agree.

## Where Babylon didn't win

- **It is 2.7× the download and twice as slow to first frame on a slow phone** (18 s against 9.6 s). It also ran at
  45 fps against 60 under the phone CPU throttle. Most of that is Babylon's core, not our code: the build already
  imports only what it uses.
- **Its look was not better once three.js was tuned.** Babylon's real advantage was the glow, and a threshold change
  in three.js matched it.
- **It has a trap.** A particle system that finishes disposes its texture by default. One finished explosion blanked
  every fire, smoke and steam system that shared the sprite, so a busy scene showed none of them. It took a wrapper
  to stop.

- **It has no soft particles.** Fire and smoke cards cut into roofs in hard horizontal lines, as the user saw on a
  phone. three.quarks has soft particles: they needed a patch for the orthographic camera and a depth pre-pass. In
  Babylon it would take a custom particle shader written twice (GLSL and WGSL). With fires on the roof and the 162
  things of the kitchen sink, Babylon also fell to 28 fps under the phone profile; three.js held 60 with the pre-pass.

## Where three.js didn't win

- **Babylon is left-handed like Unity**, so it needs no mirroring. The mirroring in three.js hid its 180° glTF bug
  until it was checked against Unity frame by frame.
- **Babylon's WebGPU path works today.** three.js runs WebGL2 here. That made no difference to fps at this scene
  size.
- **Babylon's glow layer and particle system come built in.** In three.js they are separate libraries
  (postprocessing, three.quarks).

## Decision

- **Keep three.js.** It loads twice as fast on the connections that matter most, which are phones on bad links, and it
  holds 60 fps there. Babylon's look advantage was one effect, now ported.
- **Adopt the WebSocket feed.** It needs about a third of the bandwidth of polling, sends every tick instead of four a
  second, and needs one poll per view however many people watch. It goes behind the gateway with the 3D view: the
  next step of `BROWSER_RENDER.md`'s plan.
- **Next for the look, in either engine:**
  - Unity's flat shading with bevelled edges (`Models.FlatShade`: a 3% bevel per part) and the structures' plinths
    (`Plinths.Apply`). The browsers only crease normals at 35°, which is most of why the models look soft and cheap
    next to Unity.
  - Denser steam and smoke, as Babylon had.
