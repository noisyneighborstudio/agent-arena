# Review: artillery, long_range_artillery and mining_truck models (v2)

Date: 2026-10-03. Reviewer: art-director agent. Standard: `docs/art/ART_DIRECTION.md` (Pezz notes first). Contract: `docs/art/ASSETS.md`.

**What was judged:**

- **Models:** `art-src/tools/build_units_v2.py` and the glbs it writes (branch `art/artillery-truck`, uncommitted). Tris: 3100, 3640 and 2932. Visible team share from the game camera: 22.6%, 21% and 16.3%.
- **Motion:** as implemented in `WorldView.Artillery()`, `Dump()` and `ReportDock()`, and `PezMotion` (`SetBinTipped`, `SetParked`, `Ram`).
- **Frames:** real Unity frames in `/tmp/pezz-art2/cmp_v2` (hero and stream pairs) and `/tmp/pezz-art2/rec/*`, the generator previews in `/tmp/pezz-art2/prev4`, and the icons. I also made my own thresholded silhouettes of the stream frames (`/tmp/pezz-art2/sil_*.png`) and a 3× crop of the dock (`/tmp/pezz-art2/dock_zoom.jpg`, frames 92–152).

**Sim facts:**

- **Camera:** `RtsCamera` pitch is 55° (up to 68° zoomed out) at yaw 45°.
- **Artillery:** cooldown 3.5 s, range 11 (LRA 12), speed 1.3. It fires the tick it stops if its cooldown is ready.
- **Truck bay** (`World.cs`):
  - Align: 3.2 rad/s.
  - Reverse: 2 tiles at 1.26 t/s, about 1.6 s.
  - `UnloadSettle`: 0.3 s.
  - Unload: 1.5 s for 150 ore.
  - `BedLower`: 0.25 s.
  - Pull-out: 3.6 tiles at 1.8 t/s.

## 1. Role

- **Artillery:** a fragile 11-tile siege gun. Hero idea: *a gun that happens to have a hull, and digs in to fire.*
- **Long-range artillery:** the same family at range 12. Hero idea: *the same gun, but obviously longer, from any angle.*
- **Mining truck:** hauls 150 ore at a time from field to bay. Hero idea: *an absurd open hopper on tracks that you can see filling and emptying.*

## 2. Gameplay distance (40 px/tile, JPEG q60)

**Big wins over v0.**

- The artillery no longer reads as a light tank. In `stream_units_pair` it has a raked tracked hull, a forked pair of spades at the rear and a long barrel shadow. The silhouette test (`sil_stream_units_pair`) separates it from every tank.
- The truck is now the most readable vehicle in the set. The open hopper shows its load colour at 40 px/tile for all four ores (`stream_econ_*`), and the toothed cutter makes a notched front in the silhouette (`sil_stream_econ_iron`).

**Problems, in order:**

1. **The long gun depends on heading.** The barrel is fixed at 60° elevation and the camera looks down at 55°. A gun pointing toward the camera points almost straight at it, 5° off the view axis, so its projected length is about sin 5° ≈ 9% of its real length. Pointing away, it's about 91%.
   - `stream_units_pair` (guns toward the camera): the barrel is a 6–8 px stub, and only the shadow says "long gun".
   - `stream_units_pair_yaw225` (guns away from the camera): a 40 px lance.
   - So the LRA's main differentiator (barrel 1.85 vs 1.36) vanishes in about a quarter of headings. There it's carried by a hull that is only 11% longer (1.22 vs 1.10) and by the shadow.
2. **The travel and deployed states are illegible.** The 32° spade swing changes the spades by 2–3 px at stream scale (compare the two poses in `prev4/artillery_poses.png`). A parked artillery, a travelling one and a dug-in one look the same.
3. **The artillery hull is noisy at stream scale.** The team colour is split across five places: the fender stripes, the turret wings, the cradle jacket and the barrel bands, with an extra band on the LRA. Add the bow grilles, toolboxes, spare links and the kraft rack, and the hull speckles under JPEG (`crop_pair.png`, `hero_units_pair`). v0 had a clean team block; v2 is §10 "visual oatmeal" at 40 px.
4. **On the truck, the load merges with the team colour when they're close.** The hopper's top rail is `M_Team`, so a crowned load touches team colour directly.
   - Blue team carrying crystal reads as one blue blob (`stream_econ_crystal`).
   - From the palette, the same will happen for green team with uranium and yellow team with copper or uranium.
   - This hides the ore-type read for one in four team-and-ore pairings.
5. **Empty trucks show a cinnamon floor** (known issue; `hero_idle_trucks`, and `dock_zoom` f140 after the pull-out). An empty truck reads as an iron-laden one. The script now places `bin_ore` at y 0.006, under the 0.03 floor top. The worktree glb still shows the film, so regenerate it and re-check.

## 3. Hero distance

**Artillery family.**

- Function-driven form throughout:
  - trunnion cheeks;
  - two recuperators over the jacket and a buffer under it;
  - a breech block;
  - toothed spades on rams;
  - shells with steel noses in a kraft rack.
- The LRA adds a bore evacuator and a three-baffle brake. This rewards zoom.
- Gaps:
  - **The bow travel lock is a crutch that never holds anything.** The generator's own comment says it "waits empty: the gun lives elevated". That's §17's "unexplained mechanical part".
  - **The LRA's docstring promises a second ammo rack that doesn't exist.** `racks` is the same list in both branches; the only LRA addition is three charges on the left fender.

**Mining truck.**

- Correct and well judged:
  - a deep flared hopper, twice the cab's footprint;
  - steel ribs on team skins;
  - a dark interior;
  - a tail spout over the hinge;
  - a ladder, beacon and grille;
  - four-wheel track pods;
  - a full-width toothed drum on arms with rams.
- The rhythm works: busy cutter, quiet hopper skins, busy tail.
- **The "small offset cab" is mostly hidden** under the cream canopy lip, which extends 0.10 past the hopper front at y ≈ 0.57, over a cab roof at 0.495. From the game camera you lose the asymmetry, and with it the human-scale reference that makes the hopper look absurd. The cream lip is the brightest value on the model and reads as a plain white bar.

## 4. Motion (as implemented)

### Artillery: stop, deploy, fire

| Beat | What happens now |
|---|---|
| Arrives | The sim stops it at Range × 0.95. `ArtyHull` brake dip (0.5 s period, ζ 0.7). |
| Prepares | When `!Moving` and engaged (fired in the last 3 s, or `Order == Attack`): over 0.4 s the body drops 0.04 and the spades swing down 32°; `SpadeDust` fires at the blade tips when deploy reaches 0.9. |
| Interacts | Ram 0.4 s (cooldown 0.6 → 0.2). On `fire`: recoil 0.2 out in 0.05 s, back in 0.6 s (LRA: 0.24 and 0.7). Hull kick 3°. The shell leaves from the true muzzle (`lra_fire` sheet00, 0.4–0.9 s). |
| Reacts | Shell impact. |
| Completes / disengages | Undeploys in 0.25 s once `Moving`, or 3 s after the last shot. |

**Gaps:**

- `engaged` ignores `AttackMove`. Under attack-move the first shot goes off with the spades up, and the plant follows up to 0.4 s later. The view lags the sim here; it doesn't contradict it.
- Nothing in the cycle is visible at stream scale apart from the flash and the shell.

### Truck: dock (`truck_dock` log, from t = 8.0 s)

| Beat | t (s) | What happens now |
|---|---|---|
| Arrives | 8.0 → 9.2 | Leaves the queue spot and drives to the head of the lane. |
| Prepares | 9.2 → 9.9 | Pivots on the spot to face away from the bay. The bay goes Active: lamps, door, chute. |
| Interacts | 9.9 → 11.6 | Reverses 2 tiles. |
| Interacts | 11.6 → 11.9 | The bed tips 35° toward the building during the settle (`dock_zoom` f118). |
| Interacts | 12.0 → 13.3 | 150 → 0 ore. Ore-coloured bricks pour from the lip and `PourDust` puffs every 0.25 s (f121–f134). |
| Reacts / completes | 13.4 | The bed lowers in 0.25 s, the hull bounces, and one stubborn brick pops out. |
| Disengages | 13.65 → 15.6 | Drives out 3.6 tiles, then goes back to harvesting. |

The economy review's GREAT sequence is now essentially real, and it's synced to the sim's own dock steps.

**Open from that review:**

- No cutter dust or chips while mining (R8).
- Laden and empty trucks drive identically, apart from the 0.03 squat.

## 5. System cohesion

- **Truck and refinery:** cream cab and lip match the refinery; the tail spout meets the bay chute; the bin tips into the bay. "Of course this truck belongs with that refinery" now lands.
- **Artillery family:** it shares `track_pod` with the vehicles, and the palette discipline is consistent.
- **Weak spot:** the two artillery pieces read as one model at two scales, not as a standard gun and a specialist gun.

## 6. Bad → good → great

- **Bad (v0):** a light tank with a stick, and a dark slab with a closed lid.
- **Good (v2 now):**
  - artillery with spades, a cradle and a long gun that reads when it points away from the camera;
  - a truck with an open, colour-true hopper that reverses into the bay and tips toward it.
- **Great:**
  - **Artillery:** it travels with its gun lowered into the bow travel lock, spades folded flat against the rear plate. When it stops to fight, the spades slam down with dust, the gun lifts 58° out of the crutch, the turret swings, and it fires. Parked, travelling and dug-in are three different shapes at 40 px.
  - **LRA:** visibly a longer hull with seven road wheels and a gun that overhangs the bow by more than a tile when stowed.
  - **Truck:** shows its cab, keeps its load framed by a dark keyline whatever the team colour, and throws ore-tinted dust from the cutter.

**The rest of the brief:**

- **Hero idea:**
  - Artillery: *the gun rises*.
  - Truck: *the absurd open hopper*.
- **Hero moment:**
  - Artillery: the deploy (spades down, gun up, first shot).
  - Truck: the bed tipping toward the bay with an ore-coloured pour. This exists.
- **Secondary motion:**
  - Artillery: the spade slam and dust; the barrel settling in the crutch with a 1° bounce.
  - Truck: laden squat; the bed-slam bounce (exists).
- **Gameplay read:**
  - Artillery: stowed vs raised gun, and team colour on the turret and fenders.
  - Truck: ore colour framed in dark, and the hopper shape.
- **World interaction:**
  - Artillery: spade dust (exists); a scrape mark left at the spade tips for 20 s.
  - Truck: pour dust (exists); cutter dust (missing).
- **Delight detail:**
  - Artillery: when it stows, the barrel drops into the crutch and the lock's dark latch block flips over it (0.1 s, 0.05 rise).
  - Truck: the stubborn last brick (exists).

## 7. Scores (§20, 0–3)

| Criterion | Arty | LRA | Truck | Evidence |
|---|---|---|---|---|
| Readability | 2 | 2 | 2 | Arty and LRA read as artillery in `stream_units_pair`, but the gun is a 6–8 px stub when it points toward the camera. The truck reads clearly, but blue-team crystal merges into one blob (`stream_econ_crystal`). |
| Silhouette | 2 | 2 | 2 | `sil_stream_units_pair`: spade fork and barrel shadow, no gun at camera-facing headings. `sil_stream_econ_iron`: hopper box with a toothed notch. |
| Function | 2 | 2 | 3 | Cradle, recuperators, spades and shells are all present, but the travel lock is never used (generator comment). The truck's hopper, hinge, spout and cutter all do their jobs. |
| Faction (world DNA) | 2 | 2 | 2 | Shared `track_pod`, licorice, smoke, kraft and cream across all three. |
| Motion (mass) | 2 | 2 | 2 | `ArtyHull` spring, 3° kick, ram, LRA's slower 0.7 s return. The truck has a 0.03 laden squat and the bed-slam bounce. |
| Interaction | 2 | 2 | 3 | Spade dust at the true blade tips, shell from the muzzle (`lra_fire`). The truck reverses in and tips and pours toward the bay (`dock_zoom` f112–f136). |
| States | 1 | 1 | 2 | The spade swing is 2–3 px (`artillery_poses.png`), so deployed, parked and travelling look the same. Truck: load level, ore colour and parked bin all read; the empty floor film misreads as laden. |
| Personality | 2 | 2 | 2 | LRA slower turret (50°/s) and longer recoil. Truck pivots, backs in and slams its bed. |
| Environment | 2 | 2 | 2 | Spade dust and blast. Pour dust and sinking clusters; no cutter dust and no track marks. |
| Detail | 3 | 3 | 3 | Trunnion cheeks, toothed spades, bore evacuator, three-baffle brake; ladder, ribs, beacon, hinge pins. |
| Cohesion | 2 | 2 | 3 | The LRA is the same model scaled, not a sibling (hull +11%, same rack). The truck's cream, spout and bay all match the refinery. |
| Delight | 1 | 1 | 2 | Nothing new on the artillery (the ram predates v2). Truck: the stubborn last brick. |
| Restraint | 1 | 1 | 2 | Five separate team areas plus grilles, links and toolboxes speckle at stream scale (`crop_pair.png`). The truck's cream lip is its loudest element. |
| Iconic quality | 2 | 2 | 2 | The LRA lance at yaw 225 and a hopper of glowing crystal are both screenshot-worthy, but only at certain headings and for certain ores. |
| **Total** | **26/42** | **26/42** | **32/42** | v0: artillery 19 (production review), truck 13 (economy review). |

**The item that most hurts the game is artillery States (1).** It compounds the heading-dependent gun: a spectator can't tell a dug-in, firing battery from one rolling past, and in a quarter of headings can't tell the long-range piece from the standard one.

## 8. Recommendations, ranked by impact over cost

### R1. Travel pose: the gun stows in the bow lock and rises to fire

**Cost:** M. **Mechanism:** model change plus view.

- **What:**
  - Model: move the cradle meshes (jacket, recuperators, buffer and end caps, all under `cr()` today) from `turret` into a new `cradle` node. Give it the same pivot (`tr`) and the same −60° rest rotation. `barrel` stays a child of `turret` with its rest pose unchanged, so the contract's −60° remains the firing pose.
  - View: in `Artillery()`, apply `Quaternion.Euler(+58° × (1 − raise), 0, 0)` on top of the rest rotation for both `barrel` and `cradle`. The barrel then lies at about −2° on the crutch (crutch top y 0.45, z hl − 0.1).
  - `PezMotion` recoil already uses `barrel.localRotation`, and `MuzzleOf` uses `TransformPoint`, so both stay correct.
  - Add a view-only `barrel_raise` note to MOTION.md and ASSETS.md: "−60° when deployed, stowed in travel".
- **When:**
  - Raise when deploy starts. Anticipate it: if `Moving` and `Order` is `Attack` or `AttackMove` with a target, start once `dist − Range × 0.95 < speed × 0.6` (0.78 tiles at 1.3 t/s).
  - Stow when `Moving` and not engaged, and only once the turret yaw is within 10° of forward.
- **Timing:**
  - Raise: 0.6 s, ease-out, with a 2° overshoot.
  - Stow: 0.8 s.
  - On any `fire` event, snap `raise` to 1 before computing the muzzle, so a shot is never late and never leaves a stowed gun.
- **Impact:**
  - Gameplay: a stowed barrel is roughly horizontal, so it projects at no less than about 82% of its length at any heading (sin 55°). The long gun and the LRA's extra length read from every angle, and stowed vs raised is a 30–40 px change in shape.
  - Hero: the empty crutch gets its job, and the deploy becomes the hero moment.

### R2. Truck: a dark keyline around the load, and show the cab

**Cost:** S. **Mechanism:** model change via `build_units_v2.py`.

- **What:**
  - Hopper top `rail`: material `team` → `licorice`, raised 0.01 so it stands proud of the crowned heap edge.
  - Canopy lip: overhang cut from 0.10 to 0.04 past `fz1`. Material `cream` → `team`, with a 0.02 cream strip on its front edge to keep the refinery tie. This exposes the cream cab and its team roof at the front left.
  - Re-run `visible_share` and keep the result at 16% or above. If it drops, raise the team skins' top from H − 0.03 to H − 0.01.
- **When:** always.
- **Impact:**
  - Gameplay: a 1–2 px dark frame separates the load from blue, green and yellow team walls, so crystal and uranium read on any team.
  - Hero: the small cab against the huge hopper finally gives the §3 scale joke.

### R3. The LRA becomes a sibling, not a scale-up

**Cost:** S. **Mechanism:** model change, plus re-rendered icons.

- **What:** `BUILDERS["long_range_artillery"]` changes:
  - hull L 1.22 → 1.40;
  - `track_pod` wheels 6 → 7;
  - `sp_len` 0.33 → 0.40;
  - `blade_w` 0.24 → 0.28;
  - a real second rear rack: `racks = [(-hl+0.05, -hl+0.19), (-hl+0.21, -hl+0.33)]`.
  - Keep barrel_len 1.85 (with R1 it overhangs the bow by about 1.1 when stowed).
- **Icons:** render both artillery icons at the same px-per-tile, framed on the hull and allowed to crop the barrel at the top edge. Right now the LRA icon looks smaller than the standard one.
- **Impact:**
  - Gameplay: at any heading the LRA's hull is visibly longer, by about 12 px at 40 px/tile instead of 5.
  - Hero: a second rack and bigger spades sell "longer reach, bigger charge".

### R4. Artillery restraint: fewer, bigger team reads

**Cost:** S. **Mechanism:** model change.

- **What:**
  - Delete the four bow grille slats and the three licorice spare links. Merge the right-hand toolbox into the deck.
  - Make the fender team stripes run full length (`-hl+0.05` to `hl-0.05`), 0.12 wide instead of 0.085.
  - The barrel's team band(s) stay. Change the muzzle brake from `licorice` to `steel`, so a gun pointing at the camera still shows a light ring above the dark turret.
  - Keep the cradle jacket in team colour.
- **Impact:**
  - Gameplay: two solid team shapes (turret and fenders) replace the speckle, and the bow becomes a rest area (complexity → rest → complexity).
  - Hero: the cradle and spades become the clear focal points.

### R5. Spades that fold, and a deploy gate that matches the sim

**Cost:** S. **Mechanism:** view (`WorldView.Artillery`) plus Fx.

- **What:**
  - Travel angle from 32° to 75°, so the spades fold flat up against the rear plate. Check the hydraulic ram meshes still clear the plate at 75°.
  - `engaged` also true when `Order == AttackMove && e.TargetId != 0`.
  - At the plant, add a 0.3 s scrape: 3 `S.Dust` puffs per blade, plus a `Scorch`-material decal 0.25 × 0.1 at each tip that lasts 20 s.
- **When and timing:**
  - Plant: the spades come down in the first 0.25 s of the 0.4 s deploy, ease-in, so they slam rather than glide.
  - Fold: over 0.25 s once `Moving`. The sim is already moving by then, which is acceptable (well under 1 s).
- **Impact:**
  - Gameplay: the folded and planted spades differ by about 8 px instead of 2–3, and the first shot under attack-move isn't fired with the spades up.
  - Hero: dug-in batteries leave marks on the ground.

### Also outstanding (view and Fx, not model)

- Cutter dust and chips while harvesting (economy review R8).
- Regenerate the truck glb so the empty-floor film is gone. Confirm with a frame of a truck just after pull-out.

## Readability and sim-sync flags

- **Readability breaks:**
  - The empty-hopper ore film (known; regenerate).
  - Blue-team crystal (and, by the palette, green or yellow with uranium) merging with the team colour (R2).
  - The artillery gun vanishing at camera-facing headings (R1).
- **Sim sync:**
  - Nothing places a unit where the sim says it isn't.
  - Under `AttackMove` the spade plant lags the first shot by up to 0.4 s, which is view lag rather than a contradiction (R5).
  - R1 must snap the barrel up on `fire`, so a stowed gun never fires and the shot is never delayed.

## v3 follow-up (R1–R5 applied)

**Evidence:**

- `/tmp/pezz-art2/cmp_v3/*`
- `/tmp/pezz-art2/final/artillery_deploy_close_sheet.jpg`
- `/tmp/pezz-art2/final/artillery_driving_travel_pose/sheet01.jpg`
- `/tmp/pezz-art2/v3/dock2.png`

**Re-scored criteria (others unchanged):**

| Criterion | Arty | LRA | Truck | Evidence |
|---|---|---|---|---|
| Readability | 2→3 | 2→3 | 2→3 | The stowed gun reads as a 1-tile lance at both headings (`stream_units_pair`, `stream_units_pair_yaw225`). Crystal is framed in dark on the blue truck (`stream_econ_crystal`). |
| Silhouette | 2→3 | 2→3 | 2 | Long hull, overhanging gun and folded-spade crest at the rear. The LRA is visibly longer at any heading. |
| Function | 2→3 | 2→3 | 3 | The bow travel lock now holds the gun. |
| States | 1→2 | 1→2 | 2→3 | Stowed vs raised is a whole-shape change (close sheet). The empty hopper is dark (top truck, `hero_econ_crystal`); the film is gone. |
| Cohesion | 2 | 2→3 | 3 | The LRA is a sibling: 1.40 hull, 7 wheels, second rack. |
| Restraint | 1→2 | 1→2 | 2 | Two solid team reads (turret wings, full-length fenders) and a quiet bow (`hero_units_pair`). |
| **Total** | **31/42** | **32/42** | **34/42** | Was 26, 26 and 32. |

**Regressions and open items:**

1. **The gun pops up in the shot frame (regression).** A stationary, stowed artillery that acquires a target goes from stowed to −60° within one frame of the first shot (close sheet, frame 4 → 5). The spades then plant *after* the shot, so the sequence reads backwards.
   - Start the raise on acquisition: `!Moving && e.TargetId != 0`, 0.6 s, overlapping the sim's turret slew.
   - When the snap is unavoidable (cooldown ready and already aligned), set `Deploy = 1` in the same frame. The spades and their dust then land with the flash, so the shot is visibly braced.
   - Cost: S, view only.
2. **Travelling, the artillery now reads as a long-gun tank.** The role read in travel relies on the folded-spade crest and the hull length. That's acceptable, because the raised gun is the firing state. Keep the 105° crest; don't shrink it.
3. **Not done:**
   - Icons are still framed per model bounds, so the LRA icon looks smaller than the standard one. Render both at one scale.
   - The R5 scrape decal.
   - Cutter dust (economy R8).

**No sim-sync breaks.** The snap on `fire` guarantees that no shot leaves a stowed gun and no shot is delayed.
