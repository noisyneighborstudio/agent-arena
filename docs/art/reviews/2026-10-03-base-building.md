# Art review: base building and the base as a whole

Date: 2026-10-03. Subject: `command_center` as the construction yard (crane, placement, `stage_0`..`stage_3` emerge), `power_plant` (power and low-power states), and how a whole base composes and reads.

What was judged:
- **The live look:** `/tmp/pezz-render/view/before_*.png`.
- **The rendering pass in progress** (`render/quality-pass` at `c66b032`, "Chamfered edges, structure plinths with driveway ramps"): `after*_*.png`, `p_bev_cc.png`, `q_bev1-3.png`, `pair_*.png`, `p_wide.png`, `p_fac.png`, `p_ref.png`, `early_grid.png`. The code is in `unity/Assets/Pez/View/Plinths.cs` and `Models.BevelData` on that branch.
- **The target:** `docs/art/boards/01_Main.png` (§09 Nothing pops, §13 Buildings and animation), `02_Hero.png` and `hero_offaxis.png`.
- **Motion:** the spec in `docs/art/MOTION.md`, and the implementation in `Art/PezEmerge.cs`, `Art/PezMotion.cs`, `Art/PezMotionProfiles.cs` and `View/WorldView.cs`. The two differ a lot here, and the gaps are flagged below.
- **Gameplay distance:** `q_bev3.png` downscaled to about 40 px per tile and saved as JPEG q60. `after2_wide_base.png` at strategic zoom, about 20 px per tile. A thresholded blackout of the 40 px frame for the silhouette test.

## Sim facts the choreography has to fit

From `Sim/Defs.cs`, `Sim/World.cs` and `Sim/Commands.cs`:

- **Placement is instant and blocking.** `build` pays, calls `SpawnStructure(..., progress 0)` and emits `placed`. The footprint is occupied at once (`Map.Occupant`). Units standing on it are teleported off (`EvictUnits` sets `Pos` and `PrevPos`). HP starts at 10%.
- **One structure builds at a time per team, and only with a complete command center** (`UpdateProduction`, `World.cs:848`). Every other placed structure waits at `BuildProgress == 0` for as long as the queue ahead takes, which is often 10 to 30 s. If the CC dies, the whole queue freezes.
- **Build times at full power:** power_plant 6 s, barracks 6, gun_turret 6, sam_site 7, laser_tower 9, mining_refinery 10, electronics_plant 10, radar_dome 10, optics_lab 10, factory 12, enrichment_plant 12, composite_foundry 12, airfield 14, fusion_reactor 16. The command_center itself is never built from the queue (`Buildable = false`). It spawns complete at join.
- **Low power** is `PowerUsed > PowerProduced`, team-wide (`World.cs:91`). It halves structure building, unit production, tech, refining and deep-mine output, and it switches radar off. Only complete structures count toward power. A power plant adds +100 and the CC adds +20. **It does not switch defences off**, but the HUD says it does (`Hud.cs:930`: "Out: production slowed, defences offline").
- **The events the view gets:** `placed` (structure id, centre, key) and `built` (structure id, key). Neither one is handled in `WorldView.PlayEvents` today.
- **HP rises with build progress** (`+0.9 × MaxHp × Δprogress`), so a half-built structure is legitimately at low HP. Damage visuals must be gated on `IsComplete`.

## 1. Role and hero idea

- **command_center:** the HQ that builds every structure. **Hero idea: the base is built by its crane.** The crane is the tallest thing in the base, and it always points at whatever is being built.
- **power_plant:** the +100 power that every other building runs on. **Hero idea: a spring under load.** The cream coil rings on the twin towers compress as the base draws more power and bottom out when the base is over capacity. BRIEF_v2 already says "a visible coil means power". The model has the coils, but they do nothing.
- **The base as a whole:** a compound of machines connected to each other, not a car park of separate models.

## 2. Gameplay distance (40 px per tile, JPEG q60)

- **Power plants read best in the base.** The twin cyan-capped stacks survive downscale, JPEG and the blackout (two round tops). In the Cherry base (`after2_wide_base`) there are five of them, and they give the base its only visual rhythm, by accident.
- **The command center doesn't read as the HQ.** At 40 px it's a red-roofed block on a large cream slab, about the same read as the electronics plant (a red roof on cream). The crane mast is 0.09 units wide (`spinner__M_SpringSteel`), about 3.6 px, and it disappears in JPEG. The factory's foil roof has more area and wins the eye.
- **Silhouette test (blackout):** almost every structure becomes a square slab with a lump on it. Only the power plant stacks, the refinery silos and the radar dome survive. The new plinths make this worse at silhouette level, because every building now starts with the same chamfered square.
- **State at stream scale is almost nothing:**
  - **Constructing:** stages rise and an amber bar fills. This works while the structure is being built.
  - **Queued (placed, waiting):** nothing is visible, because `SetBuildProgress(0)` sinks `stage_0` (and the 0.12 plinth) to −0.17, under the terrain. An empty amber bar floats over bare ground, and units are pushed off ground that looks empty. **The view contradicts the sim.**
  - **Low power:** invisible. The power plant cores glow at full strength in every state (`PezMotionProfiles` has nothing for `power_plant`, and nothing drives `M_E_Cyan`). Only the radar dish stops.
  - **Producing:** invisible on the CC (`Producing()` sets working, but `spinnerActiveRpm = 0`) and on most structures. Units still roll out of the doors.
  - **Damaged:** invisible. There are no smoke or fire sockets. The Cherry base in `before_close_base` is "under attack" and looks pristine.
  - **Construction bar vs health bar:** both use `Mats.Amber` (`Bars.cs`). A site at 45% looks the same as a building at 45% health.

A failure at this distance outranks everything below.

## 3. Hero distance

- **Form:** the blockouts are far under budget.
  - command_center is 548 triangles, against a budget of 10–18k for a 3×3.
  - power_plant is 1,464 triangles, against 5–10k for a 2×2.
  - The CC is a cream box (`stage_1__M_CreamPlastic`, 2.6 × 0.9 × 2.5) with a smoke-plastic stem and a team cap on top. It's a shed on a roof.
  - The large blank cream roof is rest, but there is no complexity next to it for the rest to set off. The crane, which should be that complexity, is a thin stick by the door.
- **Function:** nothing about the CC says that it builds things. Its hook hangs over its own roof, the jib reaches 0.9 tiles, and sites can be anywhere within 6 tiles of any friendly structure. The power plant says "generator", but nothing shows where the power goes.
- **Wear:** none. There's no stain at plinth edges, no soot downwind of the stacks, and no oil at the foot of the driveway ramps.
- **The rendering pass:** the bevels are a *shading* chamfer (normals in uv3 to uv5, width 0.012–0.05 units, with no geometry change). At hero distance they give clean highlight lines (`pair_zoom2`, `p_fac`), and the lit plastic finish clearly improves. The plinths (0.12 high, chamfered top edge, a ramp in front of every `door`) give a real contact line and shadow step. The ramps are the best thing in the pass, because they're function-driven: vehicles come out of that door.

## 4. Motion and interaction: beat sheets

### Structure construction, as it is today
- **Arrives:** nothing (the site is invisible).
- **Prepares:** nothing.
- **Interacts:** the stages rise with ease-out cubic over 0–10%, 10–45%, 45–75% and 75–95% (`PezEmerge.SetBuildProgress`).
- **Reacts:** none. There's no dust, no settle and no sound cue.
- **Completes:** a 4% overshoot on `stage_3`, and functional nodes come up at 95–100%. This is good.
- **Disengages:** nothing. The CC was never involved.

The MOTION.md command_center spec isn't implemented: crane yaws to the site at 30°/s, swings ±20°, hook bobs, cream beam to the site, idle ±10° sweep, beacon blink every 1.5 s. The spinner rpm is 0, and there's no aim and no beam.

Motion expresses no mass, and idle is dead.

### Power plant, as it is today
- **Arrives:** it rises like any structure.
- **Interacts:** none.
- **States:** none. MOTION.md's specs (cores at emission 0.6–1.0 with load, a 4 Hz flicker at low power, steam from both towers, a 3 s idle pulse) aren't implemented. Board 01_Main §13 repeats them.

### The great version (proposed choreography)

All times are fractions of `BuildProgress`. That makes them sim-authoritative: at low power everything automatically runs at half speed, and nothing delays `built`. The examples are for a 6 s power plant.

| Beat | Trigger (sim) | What the viewer sees | Time (6 s build) |
|---|---|---|---|
| Arrives | `placed` event | Site staked out. A footprint outline decal (cream dashes, amber corner ticks) fades in, and 4 corner stakes rise. If the structure is queued behind others, a small pallet of candy bricks sits on the site, and that is the "waiting" state. | 0.25 s, then holds for any queue wait |
| Prepares | `placed` when this structure heads the queue, else the first tick with `BuildProgress > 0` | The CC crane slews to face the site at 90°/s (180° in ≤ 2 s; MOTION.md's 30°/s is too slow for 6 s builds). The hook drops 0.1. | 0–2 s, overlapping the start |
| Interacts | `BuildProgress` 0 → 0.95 | A cream construction beam (`Fx.Beam`, #ECE4D2, width 0.04) runs from the hook to the top of the current stage, re-emitted every 0.25 s with ±0.1 jitter at the site end, like welding. A low scaffold cage rises just ahead of the stages. | 5.7 s |
| Reacts | `BuildProgress` crosses 0.10, 0.45, 0.75 | Each stage lands: the body drops 0.02 over 0.08 s, 4 corner dust puffs (`FxSystems.Dust`, biscuit) kick out, and an amber spark flashes at the beam tip. | 0.1 s each |
| Completes | `built` event | The beam cuts with a 0.1 s flash. The scaffold sinks over 0.4 s and the stakes retract. Emissives get "first light": off, two flickers of 0.08 s, then steady over 0.3 s. For a power plant, the cores light bottom-up and every plant's coils relax a notch. | 0.5 s after `built` (the structure is complete in the sim; only effects trail) |
| Disengages | 0.5 s after `built`, or the next queue head | The crane returns to its idle ±10° sweep (8 s period), or slews straight to the next site. | 1–2 s |

When the CC dies with a non-empty queue, the sim freezes every site. The view cuts all beams, and the amber corner ticks switch to the hazard stripe ("halted"). This is truthful and costs S.

## 5. System cohesion

- **The CC and its sites:** they share nothing today. The beam and the crane aim are the interface.
- **The power plant and its consumers:** nothing connects them. Power is team-wide in the sim, so the honest visual is a network, not point-to-point links: a ground-level conduit grid in the 1-tile walkways that joins every plinth. It shows cyan pulses when power is healthy and goes dark or stutters at low power.
- **Shared language is the one strength.** Cream plastic, smoke stems, team caps, sugar pads and amber door beacons are consistent across every structure. World DNA holds.
- **Composition:** `FindPlacement` spirals out from the start position with `padding: 1`, so AI bases come out as a lattice of separate slabs with uniform 1-tile gaps. The view can't move buildings, so composition has to come from the ground: aprons, conduits and stains.

### Do foundations and bevels alone get structures from BAD to GOOD?

**No.** They improve the finish, not the form. Each contributes this much:

- **Plinths:** the real gain at stream scale. The 0.12 riser is about 5 px at 40 px per tile, and that contact line and shadow step survive JPEG. Buildings stop looking pasted on (Environment goes from 0 to 1). The door ramps are function-driven and should stay.
- **Shading chamfer:** a gain at hero distance only. 0.012–0.05 units is 0.5–2 px at 40 px per tile, and JPEG q60 eats most of it. Only the long roof edges of the factory and CC survive. It doesn't change the silhouette.
- **What they don't fix:** §17's "generic beveled boxes" is a form failure. The CC is still a box on a box, and every building's silhouette now starts with the same slab. Meanwhile the problems that make the base BAD are untouched: invisible states (queued, low power, damage), the missing CC-to-site interaction, and dead idle.

GOOD needs four things:
1. State legibility (recommendations 1, 2 and 3).
2. Construction as an interaction (recommendation 2).
3. One function-driven hero form per key building: the CC crane (recommendation 5) and the power plant coils (recommendation 1b).
4. Ground connective tissue so the base reads as one compound (recommendation 6).

A note for the rendering agent: vary the plinth by function rather than giving every footprint the same slab.
- **Power plant:** a grated slab with conduit stubs.
- **CC:** a two-tier plinth with a crane footing outside the hall.
- **Production buildings:** keep the ramp.

## 6. Bad, good and great

- **Bad (where it is now):** structures rise out of the ground on a timer while a bar fills. The HQ sits there. Power is a number in the HUD. A base is a grid of tidy, identical-looking slabs that look the same whether they're thriving, starving or burning.
- **Good:**
  - Sites are visible from the moment they're placed.
  - The CC's crane turns toward the active site and a beam builds it.
  - Stages land with dust.
  - Power plants glow with load and flicker when the base is over capacity.
  - Damaged buildings smoke.
  - Structures sit on plinths with dirt skirts.
- **Great:**
  - You can read a base's economy from the stream.
  - The power plant coils visibly compress with every consumer that comes online and bottom out, chattering, when the base overbuilds.
  - When a new plant lights, the base's lights come back on in a ripple outward from it.
  - The crane, the tallest silhouette in every base, swings from site to site like a conductor.
  - Conduits pulse cyan along the walkways between plinths.
  - Burning buildings stain their slabs.
  - You can tell which team is starved, which is building, and where, without reading the HUD.

| | |
|---|---|
| **Hero idea** | The CC builds with its crane, and the power plant is a spring under load. |
| **Hero moment** | A new power plant finishes in a low-power base: the beam cuts, its cores ignite, the coils on every plant relax, and lights across the base relight in a ripple outward from it. |
| **Secondary motion** | Hook bob on the crane (a vertex-shader sine, per MOTION.md), coil chatter at overload, steam wisps off the towers, the settle bump and dust puff when each stage lands. |
| **Gameplay read** | Must read at 40 px: the crane silhouette and its heading, a staked site vs a rising one vs a finished one, core brightness plus coil height (power), smoke columns (damage). |
| **World interaction** | Dirt skirts and soot stains downwind of the stacks, construction dust, conduit pulses across walkways, burning slabs that leave scorch on the plinth. |
| **Delight detail** | When low power ends, base lights relight in an outward ripple from the newest plant over 0.8 s. Second choice: the crane hook gives a little "release" bounce when a building completes. |

## 7. Scores (§20, 0–3)

| Criterion | Score | Evidence |
|---|---|---|
| Readability | 2 | Power plants and refinery silos read at 40 px (`q_bev3` downscaled). The CC reads as a red-roofed block, about the same as the electronics plant. |
| Silhouette | 1 | In the blackout most structures are a square slab plus a lump. The CC crane (0.09 wide, about 3.6 px) vanishes. Plinths make every base silhouette start the same way. |
| Function | 1 | CC geometry doesn't say "builds things" (the 0.9-tile jib hangs over its own roof). Power plant towers say "generator", but nothing shows power going anywhere. |
| Faction (world DNA) | 2 | Consistent cream plastic, smoke stems, team caps, sugar pads and amber door beacons across every structure (`p_wide`). |
| Motion | 1 | `PezEmerge` stages ease out and overshoot 4%, but nothing has mass (no dust, no settle). CC spinner rpm is 0, and the power plant has no named nodes. |
| Interaction | 0 | The CC never interacts with a site. MOTION.md's crane aim and beam aren't implemented, and `placed`/`built` aren't handled in `WorldView.PlayEvents`. Power plant and consumers never interact. |
| States | 1 | Rising stages show construction. Queued sites are invisible, low power is invisible (except radar), damage is invisible, CC "producing" is invisible, and the construction bar uses the same amber as low health. |
| Personality | 1 | Everything is static. The CC idle sweep and beacon blink from MOTION.md are absent. |
| Environment | 1 | Plinths add contact and shadow (`p_bev_cc` right). No dirt-skirt decal (board §13), no stains, no construction dust. |
| Detail | 1 | 548 triangles on the CC and 1,464 on the power plant, against 10–18k and 5–10k. The shading chamfer adds highlight lines but no form (`pair_zoom2`). |
| Cohesion | 1 | The materials match, but buildings don't connect. The refinery dock ramp is the only engineered interface between structures. |
| Delight | 0 | None found. |
| Restraint | 2 | Emissives are limited to cores and beacons, there's no greebling, and roofs are clean. Good discipline. |
| Iconic quality | 1 | A tidy toy town. The twin cyan stacks are the closest thing to a signature. |
| **Total** | **15 / 42** | |

**Lowest score that most hurts the game: Interaction (0).** Construction is the most frequent event in every base, and nothing in the world performs it. States (1) is the gameplay-critical companion. An invisible queued site that blocks pathing, and invisible low power, both mislead viewers and agents.

## 8. Recommendations (ranked by impact over cost)

### 1. Power state on the plant and its consumers (S)
- **What:**
  - **Cores:** set `_EmissionColor` through a MaterialPropertyBlock on the `stage_3__M_E_Cyan` renderer of every `power_plant`, at intensity `lerp(0.6, 1.0, PowerUsed / PowerProduced)`.
  - **When `LowPower`:**
    - The cores flicker at 4 Hz, between 0.25 and 1.0, with a random dropout of 0.1 s every 2–3 s.
    - Every consumer's `M_E_*` emissives (structures with `Def.Power < 0`) drop to 50%.
    - `PezMotion.profile` spinner rpm and door speed run at 0.5×, which is exactly what the sim does to their rates.
  - **Steam:** cream `FxSystems.Smoke` wisps (size 0.3, 2 s life) from both tower tops. Rate: one every `0.8 / load` s. At low power they sputter (bursts of 3, then 1 s gaps).
- **When:** every frame in `WorldView.UpdateView`, from `World.Teams[e.Team]` (`PowerUsed`, `PowerProduced`, `LowPower`). Cores stay dark until `IsComplete`, which matches `UpdatePower` counting only complete structures.
- **Timing:** brightness lerps over 0.4 s on any change. The flicker starts within one frame of `LowPower` flipping.
- **Mechanism:** a particle or light effect in `Fx`, plus a property block in `WorldView`. No model change.
- **Impact:** at gameplay distance, the most important hidden economic state becomes visible on the stream at a glance (dim, flickering stacks plus sputtering steam). At hero distance, the base breathes.
- **Also fix (S):** `Hud.cs:930` claims "defences offline", but the sim doesn't do that. Say "production and building at half speed, radar off".

**1b. Coil compression (S–M).**
- **What:** a height-masked vertex-shader Y-compress on the power plant's coil-ring materials (`stage_1/2/3__M_CreamPlastic`). Compression is 0 to 15% of height with load, and fully compressed plus ±0.02 chatter at 4 Hz when `LowPower`.
- **When:** whenever team power changes.
- **Timing:** a 0.4 s spring with 8% overshoot. A new consumer completing (`built` with `Def.Power < 0`) pushes every plant down a notch, and a new plant relaxes them.
- **Mechanism:** a vertex-shader sway (the documented mechanism for ambient motion), driven by a material float set per team.
- **Impact:** about 10 px of height change at 40 px per tile, which reads on stream. It's also the signature form that answers §17.

### 2. Visible sites and the crane-and-beam construction interaction (M)
- **What:**
  - **(a) Site.** On the `placed` event, spawn a site decal (footprint outline, using the existing `PezDecal` shader as in `DeepDepositsView`) and 4 corner-stake quads. Add a small pallet of bricks when the structure isn't the queue head. Remove them on `built` or when the entity disappears.
  - **(b) Crane aim.** Give the `command_center` spinner MOTION.md's `aim_then_swing` mode in `PezMotion`. While `StructureQueue.Count > 0`, yaw toward `StructureQueue[0]`'s centre at 90°/s with a ±5° swing at 0.4 Hz. When the queue is empty, idle at ±10° per 8 s.
  - **(c) Beam.** While the queue head has `0 < BuildProgress < 1`, `Fx.Beam` runs from the hook tip (the end of `spinner__M_Dark`) to the site at the current stage's top. Cream, width 0.04, re-emitted every 0.25 s. It stutters at 4 Hz when `LowPower`.
  - **(d) Stage landings.** When `BuildProgress` crosses 0.10, 0.45 or 0.75: a settle of 0.02 over 0.08 s on `rig.Body`, plus 4 corner `Dust` puffs.
  - **(e) Construction bar.** Stop using health amber. Either use a cream fill with segment ticks, or move construction progress onto the site decal as a perimeter line that fills clockwise.
- **When:** the `placed` and `built` events, and the queue head's `BuildProgress`.
- **Timing:** as in the beat sheet in section 4. It is driven entirely by `BuildProgress`, so it never leads or lags the sim.
- **Mechanism:** `WorldView.PlayEvents` cases for `placed` and `built`, a `PezMotion` spinner aim mode, `Fx.Beam`, `FxSystems.Dust`, and a decal.
- **Impact:** at gameplay distance, it fixes a sim-sync violation (invisible blocking footprints). A viewer can see where the base is building and what's queued, and the crane's heading becomes a base-level "busy" signal. At hero distance, construction becomes a performance.

### 3. Damage states (S)
- **What:**
  - **Below 50% HP:** `FxSystems.Smoulder` from the top of `stage_2` (at 2/s, size 0.6), plus a scorch decal on the plinth.
  - **Below 25%:** rate 6/s, early flames, and the structure's `M_E_*` flicker at 3 Hz with a spark burst every 1.5 s.
- **When:** `e.IsComplete && e.Hp / MaxHp < 0.5` and `< 0.25`. It must be gated on `IsComplete`, because sites legitimately sit at low HP while they build. It clears automatically as `repair` raises HP.
- **Timing:** fade in over 0.5 s and out over 2 s.
- **Mechanism:** `FxSystems` (Smoulder already exists).
- **Impact:** smoke columns read at any zoom, including strategic. "Base under attack" becomes visible in the world, not just in the HUD banner.

### 4. Nothing pops: structures spawned complete (S)
- **What:** structures created already complete pop in today. That covers the CC at `joined`, the head-start power_plant and refinery (`World.cs:335`), and resumed saves. Play `PezEmerge.PlayBuild(1.0 s)` on them, with the CC `door` opening for the deploy intro (MOTION.md).
- **When:** `Create` for a structure whose first observed `BuildProgress` is 1, except on the first frame after a load (skip it there, so resumed games don't replay every build).
- **Timing:** 1.0 s, which stays inside the "visual offset resolves within a second" rule.
- **Mechanism:** `PezEmerge`.
- **Impact:** small on its own, but it finishes the §09 "Nothing pops" promise for a new player's arrival.

### 5. Command center crane re-model (needs new art)
- **What:** rebuild the `spinner` as a real tower crane.
  - **Mast:** a lattice 0.2 wide, with its top at 2.8 (the tallest thing in any base; today's CC top is 2.38).
  - **Jib:** 1.6 long, with a trolley and hook. The hook is a vertex-shader sine, per MOTION.md.
  - **Counter-jib:** carries a team-colour flip-top cab. BRIEF_v2 says heads are cranes.
  - **Footing:** move the crane out of the hall onto its own footing at the north-east corner of the plinth, so the hall becomes the "rest" and the crane the complexity.
  - **Triangle count:** bring the CC to the 10–18k budget, keeping node names, pivots and footprint identical.
- **When:** the next art pack export.
- **Mechanism:** a .glb model change, re-exported with the art pack tools.
- **Impact:** at gameplay distance, the HQ becomes the iconic silhouette of every base, and recommendation 2's crane heading becomes legible. At hero distance, it's the signature asset.

### 6. Ground connective tissue: base composition (M–L)
- **What:**
  - **(a) Dirt skirt:** a decal 0.15 past every footprint (board §13 and BRIEF_v2 ground anchoring; it isn't implemented).
  - **(b) Aprons:** in the 1-tile walkways between adjacent plinths, a lower-value `SugarPad` paved apron decal (flat, so the plinth height lookup in `Plinths.HeightAt` is untouched) turns the islands into one compound.
  - **(c) Conduits:** a licorice conduit strip, 0.12 wide, runs along the aprons and links every plinth. It carries a cyan dash pulse every 3 s when power is fine and goes dark or stutters when `LowPower`.
  - **(d) Wear:** soot stain downwind of each power plant, and oil at the foot of each door ramp.
- **When:** rebuild the decals on structure create and remove. Conduit state follows the team's power every frame.
- **Mechanism:** decals with `PezDecal` in a new `BaseGround` view (or in `TerrainView`), plus a shader scroll for the pulse.
- **Impact:** at gameplay distance, a base reads as a powered network, which is the base-level composition fix that plinths can't give. At hero distance, it adds environmental storytelling.

### 7. Sim-sync tidy (S)
- **What:** `EvictUnits` hard-teleports units (it sets `PrevPos` too). In the view, detect a ground unit whose position jumps more than 0.8 tiles in one tick without a move, and ease it over 0.3 s.
- **When:** in the `UpdateView` interpolation.
- **Mechanism:** `WorldView`.
- **Impact:** minor, but placing a building on top of your own units currently makes them pop.

## Readability and sim-sync breaks (summary)

1. **Queued or placed structures are invisible** (`stage_0` is sunk at progress 0), but they block tiles and push units off. The view contradicts the sim. Fix: recommendation 2a.
2. **Low power has no world representation** except the radar, and the HUD misstates its effect ("defences offline"). Fix: recommendation 1.
3. **The construction bar and the low-health bar are the same amber** (`Bars.cs`), so state is ambiguous. Fix: recommendation 2e.
4. **Complete-on-spawn structures and evicted units pop**, against "nothing pops". Fix: recommendations 4 and 7.
