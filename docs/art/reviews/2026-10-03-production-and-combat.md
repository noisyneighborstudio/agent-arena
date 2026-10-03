# Review: production and combat (factory + light_tank + heavy_tank + artillery)

Date: 2026-10-03 · Reviewer: art-director agent · Standard: `docs/art/ART_DIRECTION.md`

**What was judged:**

- **Code:** `main` at b034f62 (`WorldView.cs`, `Fx.cs`, `FxSystems.cs`, `Models.cs`, `Art/PezMotion.cs`, `Art/PezEmerge.cs`, `Art/PezMotionProfiles.cs`). I also read the `render/quality-pass` diff (Plinths with driveway ramps, fx light pools, hotter tracers, 75 s scorches) and note where it changes things.
- **Spec:** `docs/art/MOTION.md`.
- **Models:** the `.glb` files in `Resources/PezModels/`.
- **Sim facts:** `Sim/Defs.cs` and `Sim/World.cs`.
- **Screenshots:** existing ones only, in `/tmp/pezz-render/view/` (`before_*`, `after*_*`, `p_fac`, `p_fx4*`, `p_fx7*`, `pair_*`, `early_grid`) and `docs/art/boards/02_Hero.png`, `04_Motion.png` and `hero_offaxis.png`.

None of the live screenshots shows a tank at hero distance. Tank judgments come from the board renders, the model geometry and the code.

## Sim facts the choreography must fit inside

| | light_tank | heavy_tank | artillery | factory |
|---|---|---|---|---|
| Build time (s) | 8 | 13 | 12 | 12 to build; produces the three |
| Speed (tiles/s) | 2.6 | 1.6 | 1.3 | — |
| Weapon cooldown (s) | 1.5 (`cannon`) | 2.0 (`heavy_cannon`) | 3.5 (`artillery`) | — |
| Projectile speed (tiles/s) | 18 | 16 | 8 | — |
| Range / max flight time | 5 / 0.28 s | 5.5 / 0.34 s | 11 / 1.38 s | — |
| Splash | — | 0.8 | 1.5 | — |
| Model tris / hull bounds | 200 / 0.62×0.90 | 292 / 0.80×1.20 | 200 / **0.62×0.90 (identical to light_tank)** | 200 |

**Sim behaviour that matters:**

- The tick is 20 Hz.
- Every vehicle turns its hull at 5 rad/s (286°/s) and its turret at 6 rad/s (344°/s). Mass does not change either.
- A vehicle stops dead when its target is within `Range × 0.95` (`Engage`, World.cs:1041), then fires as soon as `Cooldown ≤ 0` and the turret is within 0.35 rad (20°) of the target. **Nothing fires on the move.**
- Speed goes from 0 to full in one tick; the sim has no acceleration.
- `trained` fires when queue progress reaches `BuildTime` (rate 0.5 on low power). The unit is placed one tile south of the footprint (±0.3 jitter), facing south, and is immediately ordered 3 tiles further south or to the rally point.
- The view can read ahead of the event. It sees `team.UnitQueues[Producer.Factory][0].Progress` and `Def.BuildTime`, which give the seconds to `trained`. It sees `e.Cooldown`, which gives the seconds to the next earliest shot. It sees `e.Moving`, `e.Order` and `e.TargetId`, which give the stop-to-fire moment.

## 1. Role

- **Factory:** turns steel into armour. Its hero idea should be *you can watch a vehicle being made and know when it's coming out*.
- **Light tank:** the cheap, fast line tank. Its hero idea is *twitchy and snappy*.
- **Heavy tank:** the slow splash brawler. Its hero idea is *the ground notices it*.
- **Artillery:** the fragile 11-tile siege gun. Its hero idea is *a ceremonial, catastrophic lob*.

## 2. Gameplay distance (stream scale)

I took `02_Hero.png` down to about 40 px per tile, saved it as JPEG at quality 60 and thresholded it. I also used the `before_battle3` and `after1_battle3` frames, which are about 50 px per tile.

- **Factory: passes, barely.** It reads as "big building with a door"; the blue roof band carries team at stream scale. It does not say *factory*: next to the command centre it's a grey box with a stripe (`p_fac.png`). Nothing about it changes while it is producing. `SetWorking(true)` drives only a `spinner`, and the factory has none.
- **Light and heavy tank: pass.** Team colour sits on the turret top, which is right for a high camera. The heavy reads as larger, and its twin barrel is a slightly thicker 2 px line. The two hulls are still the same rectangle at two sizes.
- **Artillery: fails the role read.**
  - Its hull is *byte-for-byte the light tank hull*: same bounds and same team strip (`artillery__*` vs `light_tank__*` in the .glb).
  - The only difference is a 0.85 barrel pitched 60°. From the high camera it foreshortens to a short stub.
  - At stream scale it's "a light tank with a funny gun", not "a thing that can only fire something catastrophically large" (§1).
  - This is the one gameplay-distance failure in the set. Players can't pick out the 300 HP unit they should be protecting.
- **Wrecks: fail.**
  - `PezEmerge.PlayRemove` drops a destroyed vehicle's hull 0.06 and leaves it for **20 s in full team colour**. MOTION.md's "darkens to 30% over 0.5 s" is in the doc comment but not in the code.
  - The smoulder (`FxSystems.Smoulder`, 4.5 s) ends long before the wreck goes.
  - That leaves about 15 s in which a turretless, clean, team-coloured hull reads as a live tank to a stream viewer. This breaks readability.

## 3. Hero distance

- **Factory (200 tris):** box, band, roof stack, black door panel and two amber dots (the `M_E_Amber` strip, mostly hidden by the door). Bevels and lighting are now good in the render pass (`p_fac.png`, right), but the form is the §17 "generic bevelled box".
  - There is no intake and no visible machinery.
  - The door opens onto **the foil wall behind it**: the door sits at z −1.18 and the wall face at z −1.17, so there is no bay. An open door therefore shows a grey wall, not a dark opening.
- **Tanks:** clean licorice tracks, smoke-plastic hull, team turret and steel barrel. Rhythm is fine: a rest hull and a busy turret. Restraint is good.
  - The models have nothing that rewards zoom: no muzzle brake, no engine deck, no tow hooks, no wear.
  - The heavy's twin barrels are one node (`barrel`, 0.16 wide), so they can't alternate as MOTION.md specifies.
- **Artillery** has no recoil mechanism, spades, ammunition or breech: none of the §3 "reinforced chassis, stabilizers digging in".

## 4. Motion and interaction: beat sheets (as implemented → target)

### 4a. Factory produces a vehicle (`trained`)

**As implemented** (`WorldView.PlayEvents` case `trained`, `UpdateView` spawn lerp):

1. **Arrives / prepares:** nothing. The queue fills and nothing in the world changes.
2. **Interacts:** at the `trained` tick, `OpenDoorNear` starts the door rolling up (0.35 s, linear) *at the same instant* the unit appears.
3. The unit's `SpawnFrom` is the door plane (`Origin.Y + 0.3`, against the door at `Origin.Y + 0.32`), not 0.6 behind it as MOTION.md says.
   - It lerps to the sim position over 0.9 s with **ease-out cubic**, so it leaves at about 3× its final speed from a standstill.
   - At t = 0.1 s it has covered 30% of the path while the door is 30% open. **The hull clips through the closed door.**
4. **Completes:** the door closes 1.6 s after `trained`. Closing is 0.35 s, against 0.5 s in the spec.
5. **Disengages:** the unit drives off. In the render pass it rides down the plinth ramp (`Plinths.HeightAt`) with a y offset only, so the hull stays level on the slope.
6. `PezEmerge.PlayExit` (the spec'd exit) exists but is never called.

**Target**, all view-only. Let `T` be the predicted `trained` time = `(BuildTime − Progress) / rate`.

| Beat | Trigger | Time | What |
|---|---|---|---|
| Producing | queue non-empty | continuous | Roof stack puffs every 1.2 s (idle: 3 s, per spec). New `gantry` node slides along the roof band from the back to the door end, driven by `Progress / BuildTime`. |
| Warning | T − 1.5 s | 1.5 s | `stage_2__M_E_Amber` blinks at 2 Hz (MaterialPropertyBlock emission 0 ↔ 3). |
| Vent | T − 0.8 s | 0.6 s | 3 steam puffs (`S.Dust`, cream, size 0.35, up 0.8 t/s) from the roof stack top. |
| Unlock | T − 0.5 s | 0.05 s | Door drops 0.015, a visible "clunk". |
| Open | T − 0.45 s | 0.35 s | Door scale Y 1 → 0.05 with ease-in-out. Fully open 0.1 s *before* `trained`. |
| Roll out | `trained` | 0.9 s | Unit starts 0.6 behind the door plane, hidden in the new dark bay. It moves to the sim position with **smoothstep**: zero speed at start, offset fully resolved at 0.9 s. On the ramp the hull pitches to the ramp slope (2.3°). |
| Settle | unit leaves the ramp | 0.25 s | Hull nose-dip 1.5° as the tracks hit flat ground; 4 dust puffs at the rear track corners. |
| Close | `trained` + 1.4 s | 0.5 s | Door rolls down (spec close 0.5 s); amber lamps off. |
| Idle | queue empty | — | Gantry parks at the back, stack goes back to the 3 s idle cadence. |

**Low power:** the prediction uses `rate`, so the sequence stretches with it. If the queue is cancelled mid-warning, the lamps stop and the door closes, which is harmless. None of this moves `trained`.

### 4b. Tank moves, stops, aims and fires

**As implemented:**

- `HullFeel` derives acceleration from a speed smoothed at rate 6. Because the sim's speed is a step, the peak is `speed × 6 × 1.5`. That is −23° for the light tank, −14° for the heavy and −12° for artillery, and **all of them are clamped to the same −2° / +3°**. Every vehicle pitches identically.
- Hull yaw uses `Slerp(…, 14/s)` for all three.
- `Models.Build` raises every turret to at least 240°/s, which erases the 150 / 80 / 60°/s profiles. That is forced by the sim's 344°/s turret: the shell leaves along the sim's facing.
- `Kick` subtracts 1.5° for every vehicle (the spec says light 1.5, heavy 2.5, artillery 3).
- Idle scan is ±40° for all three (the spec says heavy ±25° and artillery none).
- **Brake and fire collide.** `Engage` stops the unit and calls `FireAt` in the same tick. The braking dip (+3°) and the fire kick (−1.5°) land together and cancel, so the first shot after arriving has neither a visible brake nor a visible kick.
- **The muzzle is in the wrong place.** `MuzzleOf` = barrel pivot + turret-forward × 0.35.
  - Light tank: the flash appears 0.17 short of the tip (mid-barrel).
  - Heavy tank: 0.34 short (halfway down the barrel).
  - Artillery: it uses the *turret's* horizontal forward, so the flash sits at the turret face (y ≈ 0.36), not the elevated tip (y ≈ 1.10).
- **The shell starts somewhere else again.** `SyncProjectiles` starts every projectile at the sim muzzle in 2D at `root.y + 0.3`. The artillery shell therefore leaves from the front of the hull about 0.8 below the visible muzzle, the flash is at the turret, and the barrel tip is above both.
- Muzzle flash size on `fire` is 0.13 for both artillery and light tank (heavy: 0.2). The flash is an omnidirectional billboard with a puff of smoke going up.
- Firing does not affect the ground: no blast ring, no dust.
- The projectile is a glowing sphere, 0.10 (heavy 0.14), with a 0.12 s trail. The artillery shell is the same 0.10 dot.

**Target, light tank** (cooldown 1.5 s, flight ≤ 0.28 s):

1. **Target acquired:** `e.Order` is `Attack`/`AttackMove` and the target is visible. Turret slews at the sim's rate (unchanged).
2. **Brake (anticipated):** when `dist − Range × 0.95 < speed × 0.3` while moving toward the target, start the brake dip 0.3 s early (view pitch only). Light: +3°, 0.18 s spring, damping 0.35, so it bobs twice.
3. **Fire** (`fire` event, t = 0):
   - Flash at the true barrel tip, size 0.13.
   - Recoil 0.12 in 0.05 s.
   - Hull kick: an impulse of 1.5° on `HullPitchVel`, not a position offset, so it rides on top of the settling dip.
   - Two muzzle-brake side puffs (`S.Dust`, 0.15, ±90° to the barrel, 0.6 t/s).
4. **Reaction:** t + 0.05 to 0.40, recoil returns.
5. **Impact:** at t + ≤ 0.28, `Hit` (existing Blast 0.3).
6. **Residual:** the barrel-tip heat glow fades over exactly `Cooldown` (1.5 s). That shows reloading as a glow state, in line with MOTION.md's "glow shows cooldown".

**Target, heavy tank** (cooldown 2.0 s):

- Same structure, heavier numbers: brake dip +2°, 0.45 s spring, damping 0.55, one slow overshoot.
- Shots alternate between `barrel_l` and `barrel_r`: one sim shot, one barrel, alternating L/R per shot. Splitting the node needs a re-export.
- Hull kick 2.5° impulse. Ground ring under the hull (`S.Ring`, size 1.4, 0.35 s, dust alpha 0.5) on every shot: "the ground notices it".
- Heat glow fades over 2.0 s.

**Target, artillery** (cooldown 3.5 s, flight 0.5 to 1.38 s):

| Beat | Trigger | Time | What |
|---|---|---|---|
| Deploy | `!e.Moving` and (`Order==Attack` or engaged in the last 3 s) | 0.4 s | Body drops 0.04; 2 dust puffs at the rear corners ("spades plant"). Undeploy on `e.Moving` takes 0.25 s: the sim moves it at once, so this is view-only and short. |
| Load | `e.Cooldown < 0.6` while engaged | 0.4 s | Barrel slides back 0.06 along its own axis and returns: the ram. It ends 0.2 s before the earliest shot, so it never delays one. |
| Fire | `fire` | 0.05 s | Flash 0.3 at the true elevated tip; Lamp intensity 3; recoil 0.2 along the barrel axis (already correct); hull impulse 3°. |
| Blast | `fire` | 0.4 s | `S.Ring` size 2.0 on the ground around the vehicle; 6 dust puffs outward. |
| Flight | projectile live | 0.5 to 1.38 s | Shell 0.16, dark core with a hot tail, trail 0.35 s. Its ground shadow (a small `Scorch`-material blob, alpha 0.35, life tied to the projectile) tracks under it, so viewers can read where it will land. |
| Impact | `hit` | — | Existing `Shell(1.05)` is right. |
| Recovery | `fire` + 0.05 | 0.6 s | Recoil return (existing). |
| Residual | `fire` + 0.1 | 1.5 s | 3 smoke puffs drift from the barrel tip; barrel heat glow fades over 3.5 s. |

### 4c. Vehicle destroyed (`destroyed`)

**As implemented:**

- `Fx.VehicleDestroyed`: Blast(1.0), one cook-off at 0.18 to 0.35 s, 12 team-coloured shards, a 1.4 scorch, and a 4.5 s smoulder.
- `PlayRemove(wreck)`: the turret is flung (good, and in the spec) but **scaled to 0 over 0.6 s**, a shrink-pop. The hull drops 0.06 and is left **undarkened** for 20 s, then sinks over 2 s.
- All three vehicles die identically.

**Target:**

- **Darken:** darken the hull with a MaterialPropertyBlock (all renderers, base colour × 0.3 over 0.5 s, team mask included) at the `destroyed` event.
- **Turret:** let it land and stay (tumble on the existing ballistic path, then lie on the ground beside the hull as part of the wreck, darkened the same way) instead of shrinking away.
- **Smoulder:** extend it to 12 s at a lower rate.
- **Scale by class:** light Blast 0.85; heavy 1.25 plus two cook-offs; artillery 1.0 plus **4 small "ammo" pops** (`S.Later`, 0.3 to 1.4 s, size 0.25, spread 0.4). The artillery dies like it was carrying shells.

## 5. System cohesion

- The factory door (1.5 wide) fits the widest of the three (heavy, 0.8), and the render-pass ramps line up with the door.
- The palette and materials are shared: licorice, smoke plastic, spring steel, team mask. It reads as one world.
- What's missing is any shared *mechanism*. Nothing on the factory says "tracked vehicles are made here": no gantry, no track-width bay, no interior. Nothing on the tanks carries a factory motif.
- The artillery doesn't look engineered as a separate product; it looks like a light tank with a part swapped.

## 6. Bad → good → great

**Bad (much of what ships now):**

- A box spawns a tank at its door with an ease-out pop.
- Three tanks with the same hull pitch, turn rate, traverse, kick and death.
- Shells that leave from somewhere other than the muzzle.
- Wrecks that look alive.

**Good:**

- The door is open before the vehicle arrives, and the vehicle rolls out from a dark bay and down the ramp.
- Recoil and kick are scaled per class.
- The muzzle flash and shell come from the real barrel tip.
- Artillery visibly deploys and has a bigger blast.
- Wrecks darken.
- The factory shows that it's producing.

**Great:**

- **Factory:** the roof gantry crawls toward the door as the vehicle nears completion. The amber lamps blink, the stack vents, the door clunks and rolls, and the vehicle noses down the ramp and settles.
- **Each tank has a mass signature you can read with no labels:**
  - The light tank bobs twice when it brakes.
  - The heavy rocks once, slowly, and its twin guns alternate L-R-L with a dust ring each shot.
  - The artillery squats, rams a shell, fires with a blast ring and a lingering smoke ribbon, and its shell's shadow crosses the ground for a full second before it lands.
- Barrel heat glow shows reload at a glance.
- **Death:** wrecks go dark, the turret lies beside the hull, and artillery cooks off its ammo.

## Hero idea, hero moment and the rest

- **Hero idea (system):** *the line that makes tanks, and tanks that feel their tonnage.*
- **Hero moment:** the artillery cycle (squat, ram, fire, blast ring, a 1 s shell shadow across the ground, the impact), and the factory's door opening exactly as the gantry reaches it.
- **Secondary motion:**
  - per-class hull springs;
  - muzzle-brake puffs;
  - barrel heat fade;
  - rear-corner track dust on start, stop and ramp exit;
  - roof stack vents.
- **Gameplay read:**
  - Team colour on turret tops and the roof band; keep both.
  - The artillery must read as artillery at 40 px per tile.
  - Wrecks must read as dead.
  - The factory's producing state must be visible without the HUD.
- **World interaction:**
  - ground blast rings on heavy and artillery shots;
  - shell shadows;
  - scorches (75 s in the render pass, which fits);
  - dust from tracks;
  - fx light pools (render pass) under muzzle flashes.
- **Delight detail:** one factory door in eight sticks at 30% for 0.25 s, then jerks open. Start the open beat at T − 0.75 s on those runs so it still finishes before `trained`.

## Scores (§20)

| Criterion | Score | Evidence |
|---|---|---|
| Readability | 2 | Factory, light tank and heavy tank read at 40 px per tile (hero board downscaled; `before_battle3`). The artillery hull is identical to the light tank's (.glb bounds), so its role doesn't read. |
| Silhouette | 1 | Thresholded board: the tanks are rectangles that differ by size and barrel only; the factory is a box (`p_fac.png`). |
| Function | 1 | Factory: no intake, machinery or bay; the open door shows a foil wall (door z −1.18 vs wall z −1.17). Artillery has no spades or recoil mechanism. |
| Faction (world DNA) | 2 | Consistent licorice, smoke-plastic and team-mask language across all four (.glb materials). |
| Motion (mass) | 1 | `HullFeel` saturates its ±2/3° clamp for all three; Slerp 14/s for all; turret floor of 240°/s (`Models.Build`); 1.5° kick for all (`Kick`). |
| Interaction | 1 | The door opens at `trained` while the unit eases out from the door plane: clipping (`PlayEvents` case `trained`). Shells and flashes leave from three different points (`MuzzleOf`, `SyncProjectiles`). |
| States | 1 | No producing state on the factory (no spinner); no reload state; no damaged state below 35% HP; wrecks keep team colour for 20 s (`PlayRemove`). |
| Personality | 1 | The three tanks behave identically apart from recoil distance and return time. |
| Environment | 2 | Explosions are strong: scorch, shockwave ring, debris, smoke, and light pools in the render pass (`p_fx7z.png`). Driving and firing leave no dust, tracks or blast rings. |
| Detail | 1 | 200- to 292-tri models with nothing to discover up close; only the fx reward zoom. |
| Cohesion | 2 | The door is sized for the vehicles and the ramps line up with doors (Plinths). No shared mechanism between factory and vehicles. |
| Delight | 1 | The turret fling on death is the one moment, and it ends in a shrink-to-zero (`PezEmerge.Fling`). |
| Restraint | 2 | The models aren't oatmeal; the fx are well layered per event. |
| Iconic quality | 1 | Nothing here would make a stranger ask what game it is; the hero board's laser beams carry more identity than these four. |
| **Total** | **19 / 42** | |

**Lowest item that most hurts the game: States.** The worst part is that wrecks keep full team colour for 20 s. On a stream that inflates army counts and misleads every viewer and agent. It is also the cheapest fix in this review.

## Recommendations (ranked by impact over cost)

1. **Darken wrecks.**
   - *What:* every renderer under `rig.Model`. In `PezEmerge.PlayRemove(destroyed: true)`, lerp a MaterialPropertyBlock base colour (`_BaseColor` / `baseColorFactor`) to × 0.3 over 0.5 s, team mask included. Extend `Fx.VehicleDestroyed`'s `Smoulder` from 4.5 s to 12 s (rate 4).
   - *When:* the `destroyed` event, which already sets `EV.Destroyed`.
   - *Timing:* 0.5 s; the wreck stays 20 s.
   - *Mechanism:* PezEmerge.
   - *Cost:* S.
   - *Impact:* at gameplay distance, dead units stop reading as alive. At hero distance, a charred hull.
2. **Muzzle and shell origin from the real barrel tip.**
   - *What:* in `Models.Build`, cache `rig.MuzzleLocal` = (0, 0, max z of the barrel mesh bounds in barrel space): 0.52 light, 0.69 heavy, 0.85 artillery.
   - `MuzzleOf` returns `rig.Barrel.TransformPoint(MuzzleLocal)`.
   - In `SyncProjectiles`, start each projectile at that 3D point. Blend the xz difference from the sim muzzle to zero over k ∈ [0, 0.15], so the sim path is followed after about 0.1 tiles.
   - *When:* `fire` and `shot`.
   - *Timing:* instant.
   - *Mechanism:* WorldView code.
   - *Cost:* S.
   - *Impact:* at gameplay distance, artillery shells visibly leave a raised gun. At hero distance, flash, barrel and shell agree.
3. **Factory spawn sequence with anticipation** (the §4a table).
   - *What:* in `UpdateView` for factory structures, compute `T` from the queue. Drive `stage_2__M_E_Amber` emission blink (T − 1.5 s), stack vent puffs (T − 0.8 s), door unlock drop (T − 0.5 s) and door open (T − 0.45 s, ease-in-out).
   - On `trained`, set `SpawnFrom` 0.6 behind the door plane (`Origin.Y + 0.32 + 0.6`) and replace the ease-out cubic with smoothstep over 0.9 s.
   - Close the door at `trained` + 1.4 s over 0.5 s (`PezMotion` door close speed: separate open and close rates).
   - *Mechanism:* PezMotion door rates, Fx puffs and a MaterialPropertyBlock.
   - *Cost:* S/M.
   - *Impact:* at gameplay distance, blinking lamps say "vehicle coming". At hero distance, no clipping and a real exit.
4. **Dark door bay in `factory.glb`.**
   - *What:* recess the wall behind `door` by 0.6 with an `M_Dark` interior (floor, two side walls, back wall), 1.5 × 0.8.
   - *When:* visible whenever the door is open.
   - *Mechanism:* re-export through the art pack tools.
   - *Cost:* M.
   - *Impact:* an open door finally reads as an opening at both distances, and the unit can spawn hidden inside it.
5. **Per-class hull feel and kick.**
   - *What:* replace `HullFeel` with an underdamped spring per class: light (period 0.25 s, ζ 0.35, ±2/3°), heavy (0.6 s, ζ 0.55, ±1.5/2.5°, body y −0.015 squat on start), artillery (0.5 s, ζ 0.7).
   - Drive it from anticipated braking: start when `dist − Range × 0.95 < speed × 0.3` toward the attack target, or when the remaining path is under `speed × 0.3`.
   - `Kick` becomes a velocity impulse of 1.5 / 2.5 / 3°. Read these from `PezMotionProfile` (new fields `hullKick`, `hullPeriod`, `hullDamping`).
   - Add 2 rear-corner `S.Dust` puffs on start and stop (size 0.2 light, 0.35 heavy).
   - *Timing:* inside the existing stop tick; nothing is delayed.
   - *Mechanism:* WorldView, PezMotionProfiles and Fx.
   - *Cost:* M.
   - *Impact:* at gameplay distance, a heavy visibly rocks and a light visibly bobs. At hero distance, the brake and kick no longer cancel.
6. **Artillery identity: model.**
   - *What:* a new `artillery.glb` hull. It should be longer (1.1) and lower, with rear `spade_l` / `spade_r` nodes, an oversized recoil cradle under the barrel, a 1.1 barrel and a muzzle brake.
   - Keep the team mask on the turret top.
   - *Cost:* needs new art (L).
   - *Impact:* at gameplay distance this fixes the readability failure. At hero distance it gives the §3 "stabilisers digging in".
7. **Artillery cycle** (the §4b artillery table).
   - *What:* deploy squat and spade dust on stop; ram at `Cooldown < 0.6`; flash 0.3 at the tip; `S.Ring` 2.0 blast on the ground; 0.16 shell; a ground shadow particle tracking the projectile's xz; 3 lingering smoke puffs.
   - *Mechanism:* WorldView (`Cooldown` read), PezMotion (new `Ram()`), Fx and FxSystems.
   - *Cost:* M.
   - *Impact:* at gameplay distance, a 1 s shadow tells viewers where the shell will land. At hero distance, this is the hero moment.
8. **Producing state on the factory.**
   - *What:* a `gantry` node: a trolley riding the roof team band, travelling −Z with `Progress / BuildTime`. A new `PezMotion.SetGantry(float)`.
   - Until the gantry exists: the spec's +10% team-band emission pulse while the queue is non-empty, and the roof stack puff cadence at 1.2 s (producing) vs 3 s (idle).
   - *Cost:* stopgap S; gantry needs new art (M).
   - *Impact:* at gameplay distance, "this factory is busy and nearly done" reads without the HUD.
9. **Heavy twin barrels alternate.**
   - *What:* split `barrel` into `barrel_l` / `barrel_r` in `heavy_tank.glb`. `PezMotion.Fire()` alternates between them.
   - Each sim shot fires one barrel. Do not invent a second shell: the sim deals damage once.
   - Add the ground ring (`S.Ring` 1.4) on each heavy shot.
   - *Cost:* M (re-export plus code).
   - *Impact:* at gameplay distance, a rhythm signature. At hero distance, it matches the spec.
10. **Barrel heat as reload state.**
    - *What:* a MaterialPropertyBlock emission on the last 15% of the barrel (or a new `muzzle` child node, `M_E_Amber`). Set it to full at `fire` and fade it to 0 as `e.Cooldown / Weapon.Cooldown` goes to 0.
    - *Cost:* S with the property block, M with a node.
    - *Impact:* "reloading" becomes visible at hero distance. At gameplay distance it is subtle by design.
11. **Death by class.**
    - *What:* `Fx.VehicleDestroyed` takes a scale: 0.85 light, 1.25 heavy plus 2 cook-offs, artillery plus 4 ammo pops (`S.Later`, 0.3 to 1.4 s, 0.25).
    - The flung turret stays, darkened, instead of `Fling` scaling it to 0.
    - *Cost:* S.
12. **Damaged state.** Below 35% HP, a thin smoke trickle from the engine deck (body local (0, 0.25, −0.35), 2 puffs/s, `S.Smoke` 0.2). *Cost:* S.
13. **Turret traverse.** Leave the 240°/s floor; it's forced by the sim's 344°/s turret and fire-within-20° rule. If design wants heavy turrets to feel slow, that is a per-def sim turret rate and a balance decision, not an art change. Express mass through recommendations 5, 7 and 9 instead.
14. **Small fixes:**
    - Artillery `idleScan = false` (spec: no idle scan); heavy scan ±25°.
    - Hull pitch from the ramp slope while `Plinths.HeightAt` is changing (render pass).
    - Have `trained` carry the producer id in `ev.B`, so `OpenDoorNear` can't open a neighbouring building's door. This is a one-argument sim emit change with no gameplay effect.

## Readability and sim-sync flags

- **Readability breaks:**
  - Wrecks in full team colour for 20 s (recommendation 1).
  - Artillery indistinguishable from the light tank by hull (recommendation 6).
- **Sim-sync:**
  - The spawn lerp resolves within 0.9 s, so it complies.
  - The shell origin is about 0.8 tiles vertically off the visible muzzle for artillery. It complies with position sync, but it contradicts the visual (recommendation 2).
  - Every recommendation above anticipates from sim state (queue progress, `Cooldown`, distance to `Range × 0.95`), compresses inside existing timings, or reacts to events. None of them delays `trained`, `fire` or `hit`, and none moves a unit away from its sim position by more than 0.6 tiles for more than 0.9 s.
- **Spec vs implementation gaps** (MOTION.md):
  - `PezEmerge.PlayExit` unused.
  - Spawn not 0.6 behind the door.
  - Door close at 0.35 s, not 0.5 s.
  - No factory band pulse or stack puffs.
  - No heavy twin alternation.
  - Hull kick not per class.
  - No artillery deploy or undeploy.
  - Idle scan not per class.
  - No wreck darkening.
  - `PezMotionProfile.barrelPitch`, `maxSpeed`, `accel`, `turnRate` and `turnInPlace` are read nowhere. The artillery's 60° pitch is baked into the .glb node, so its recoil axis is correct.
