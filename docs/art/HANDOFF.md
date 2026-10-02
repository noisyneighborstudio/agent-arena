# Pez art handoff, v0.2

**Start here:** read `BRIEF_v2.md` for the art direction, then `assets/README.md` to get the models into Unity, then `assets/unity/MOTION.md` for how everything moves.

## Contents

| Path | What it is |
|---|---|
| `BRIEF_v2.md` | The complete revised brief, with every decision below folded in. Ready to paste into Claude Design or hand to an artist |
| `design/boards/` | PNG exports of every board on the canvas: style guide, hero, icons, motion reference, HUD, camera comparison, HUD kit |
| `design/source/` | Canvas source (`.dc.html` and `canvas.json`), for editing in Claude Design |
| `assets/models/` | 34 glTF blockout models: 16 structures, 14 units, 4 ore tiles, plus `manifest.json` |
| `assets/icons/` | 34 sidebar icons, 256×192 PNG |
| `assets/unity/` | `MOTION.md`, `motion.json`, and C# scripts (`PezMotion`, `PezEmerge`, `PezMotionProfiles`, `PezPalette`) |
| `assets/palette/` | `pez_palette.json` and `pez.gpl` |
| `assets/renders/` | The map in both cameras, plus build-sequence strips |
| `assets/concept/` | Style guide PNG, the off-axis hero, and the original hand-drawn on-axis hero (`hero_onaxis_illustration_v1`, SVG and PNG) |
| `assets/tools/` | The Python and three.js generators for models, motion spec, icons and renders |

## Decisions log

| # | Decision | Why |
|---|---|---|
| 1 | Candy-dispenser setting, kept generic | Requested direction. No PEZ logos, heads or exact trade dress |
| 2 | Teams are flavours: Blueberry, Cherry, Lime, Lemon | Ties team colour to the fiction |
| 3 | The world and neutral materials stay muted | In a candy world everything wants to be loud, which would kill team readability |
| 4 | Uranium and electronics glow is sour acid `#B6FF3B`, always emissive | The original green collided with Lime |
| 5 | Hazard trim is a black and cream stripe | Hazard yellow collided with Lemon |
| 6 | laser_tank coils are cyan; magenta is plasma only | The brief broke its own laser/plasma rule |
| 7 | Medic plus is team-coloured on white; radar beacon is amber | A red cross reads as Cherry, and the Red Cross emblem is legally protected |
| 8 | Status colours never use team hues | Alerts, health and selection would otherwise read as ownership |
| 9 | Off-axis orthographic camera, yaw 45°, pitch 55° | Constant unit scale, two readable faces per building, the C&C look |
| 10 | Minimap stays north-up with A–H, 1–8 sectors | LLM orders use grid coordinates, so spectators need a stable frame |
| 11 | Nothing pops: everything emerges or leaves through a transform | Requested. Built into the models as stage, door and lift nodes |
| 12 | Bake AO and a top-down gradient; no cast shadows | Readability in fog of war (adopted from the RTS guidance) |
| 13 | Budgets: infantry 1–2k tris at 512², structures on shared trim sheets, large structures capped at 18k | Hundreds of units on screen across four armies |
| 14 | Snappy combat, vertex-shader ambient motion, ≤ 20 bones for infantry | Responsiveness and CPU cost (adopted from the RTS guidance) |

## Open decisions

1. **Plasma unit.** Magenta and the plasma bolt exist, but no unit fires plasma. Either add one, or retire plasma to the fusion reactor only.
2. **The name.** "Pez" plus a dispenser theme leans on PEZ Candy's trademark and trade dress. Get a trademark check before anything goes public.
3. **Infantry scale.** I recommend 1.3–1.4× real proportions so infantry read at 14 px per tile. The models are built at the spec's 0.55 height; scale them in Unity until final art decides.
4. **Upgrade scope.** Upgrade states are new scope that wasn't in the original brief. Confirm which buildings get them.

## Known limits

- **Blockout models, not final art.** They're far under budget, with no LOD1, texture sets, damage states or wrecks yet.
- **C# is compile-checked but not run in Unity.** It compiles against a Unity API stub but hasn't run in a real project. Check the glTFast axes once on import, using the factory doors.
- **The hero is a render of the blockouts,** not a painted illustration.

## Next step

Make the turnaround sheets for command_center, mining_refinery, power_plant, factory and barracks in the off-axis view. They're what a modeller needs to replace the blockouts without breaking the node contract.
