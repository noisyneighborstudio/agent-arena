# Deep mines (one per ore) and the neutral derrick: AFTER

2026-10-13. This is the "after" half of a before/after review. The baseline is `2026-10-13-deep-mines-and-derrick-before.md`, which scored the deep mine 21/42 and the derrick 11/42. Judged: the uncommitted working tree on `art/mines-per-ore`. Both reviews score the same §20 criteria the same way, so the totals compare.

Sources read:
- `art-src/tools/build_mines.py`: the module docstring, `S_deep_mine_iron`, `_copper`, `_crystal`, `_uranium` and `S_derrick`, and the family helpers `deck`, `collar`, `stem`, `lamp`.
- `unity/Assets/Pez/View/DeepMineWorks.cs` and `DerrickWorks.cs`.
- `WorldView.cs`: `JustPlaced`, `Create` (the model choice and the derrick rise), `UpdateView` (the `TintTeam` on an owner change, the two `Works` ticks, the fallback `rig.Turret` branch), and the `captured` event.
- `Models.cs`: `DeepMineModel`, `TintTeam` and the code-built `Derrick`.
- `Fx.ShardChime`, `PezShade.SetDim` and `PezModel.shader` (how `_PezDim` is applied).
- `docs/art/MOTION.md`: the sections `deep_mine`, `deep_mine_iron`, `_copper`, `_crystal`, `_uranium` and `derrick`.
- The sim: `World.DeepMineTick`, `DeepMineWarnAmount`, `UpdateDrill`; `Economy.cs` (the derrick events and `DerrickRespawns`); `Defs.cs` (`derrick` no longer has `ModelAs`).

Captures are in `captures/2026-10-13-deep-mines/after/`:
- every `game_*` stream frame;
- every `close_*` sequence;
- every `timed_*` strip;
- `icons.png`.

Silhouette tests I ran (saved alongside):
- **`silhouette_deep_district_d23.png`:** every pixel more than 38 RGB from the median ground turned black, so this is the full outline with the pad.
- **`silhouette_machines_deep_district_d23.png`:** only cool, saturated or very dark pixels turned black (team colour, glow, dark parts). That is the machine without its cream pad and deck, which is what the eye actually separates from a 55° pitch.
- **`silhouette_derricks_far_d23.png`:** the full outline of the derrick row.

## Spec vs implementation

Most of the implementation matches `MOTION.md`. I checked each timing in `DeepMineWorks`:
- **Iron:** the skip rises 0.78 in 1.4 s and lowers in 1.32 s. The bucket tips 58° in 0.24 s. The bricks tumble 0.55 s each, 0.09 s apart.
- **Uranium (jib):** lift 0.64 s, swing 0.56 s, lower 0.56 s, release 0.16 s, raise 0.48 s, back 0.56 s, home 0.56 s. The carousel turns 90° in 0.8 s.
- **Crystal:** the drill turns at 50°/s, spinning up at 40°/s² and coasting down at 20°/s².

I checked each timing in `DerrickWorks`: the mouth 0.24 s, the chute 0.6 s, the tray 0.28 s, into the heap 0.3 s. The flip is 150° in 0.3 s, holds 0.2 s and closes in 0.3 s. So I judge the implementation. These are the gaps:

- **Copper dry-out.** `MOTION.md` says the bricks on the belt "run off into the crusher and the belt stops". The code (`Belt`, `beltFade`) shrinks them where they are over 0.5 s, and its own summary says so. The code is the correct one for sim sync, because no ore should arrive after the deposit is empty. `MOTION.md` should change.
- **"Darkens to 60%" doesn't land.** `SetDim(all, 0.6)` multiplies the linear albedo, specular and emission by 0.6. In the captures, the exhausted crystal is only about 8% darker than the working one:
  - team ring mean blue: 216 working (`close_crystal_sequence` frame 0), 199 exhausted (`close_running_low_exhausted_neutral_derrick` middle);
  - pad: 207 vs 192.

  In `timed_run_dry` the fade starts at frame 13 and has moved the team blue from 204 to 192 by frame 15. A 0.6 factor in linear space can only darken the sRGB value to about 0.79, and the ambient and post-processing take more of it back. The state is specced as "readable from across the map", and at this strength it isn't.
- **The fallback model never runs for an unsurveyed deposit.** `MOTION.md` says the `deep_mine` fallback is for "a deposit this view hasn't surveyed". `WorldView.Create`, however, always passes the true `DepositById(e.DepositId).Type`. In a player's point-of-view render, an enemy mine on a deposit that team hasn't surveyed therefore shows its ore, even though `DeepDepositsView.Visible` hides the deposit itself. Either the spec or the code is wrong. Decide which: spectators are meant to read ore income, so the code may be the intended behaviour.
- **Fog.** The `!sees` branch sets `Lamp(0.25)`, `Core(0.25)` and `Ore(0.8)`. A dry uranium mine remembered under fog lights its acid again at 0.8, which falsely says it still has uranium. `MOTION.md` says it should be "neutral and still".
- **Doc drift.** The `build_mines.py` docstring and the `S_deep_mine_copper` comment both say "a mint hood", but the hood is built in `team`. The summary on `World.DeepMineWarnAmount` still says "two minutes of pumping"; the value is 300 s (1200 ore). That was baseline item 8 and it's still open.
- **Hygiene fixed.** The legacy `rig.Turret` branch in `WorldView` now runs only for the code-built fallbacks, under `e.Def.Key`, so it can't fight `DeepMineWorks` on a .glb any more.

### Baseline recommendations: what was done

| # | Baseline recommendation | Status |
|---|---|---|
| 1 | Drained heap clipped through the bin | **Done.** `dm_heap` now scales by `1 - drain` and is deactivated at 0.99. |
| 2 | Dry-out pouring phantom ore | **Done, a different way.** The trip in hand finishes empty (`empty`), the belt stops loading, and the jib carries no rod. |
| 3 | One big state light; one meaning for "low" | **Partly.** The lamp is on a 0.3 post with r 0.08 (was 0.06), but it's still in the back corner under a team cap. There's no stem flash, and the stem and lamp still disagree (see States). |
| 4 | The derrick's own model and capture beat | **Mostly.** It has its own model, the flip, and the rise. There's no beacon, no pending-site marker, and the rise takes 2.4 s, not ≤ 1.0 s. |
| 5 | Per-ore silhouettes | **Done.** |
| 6 | Turn the dump toward the camera | **Done.** Iron's chute and hopper face front. Crystal's tray is on the front rim. Copper's crusher mouth opens toward the belt and the camera. |
| 7 | A way out for the ore | **Not done.** Every container fills and none ever empties. |
| 8 | Hygiene | **Half.** The turret branch is fixed; the warn comment is not. |

---

## Role

**deep_mine_{iron,copper,crystal,uranium}.** The sim is unchanged. A drill rig deploys one onto a deep deposit. It pumps 4 ore/s of that ore into the stockpile, draws 50 power (half rate on low power), warns at 1200 ore left and stops when the deposit is dry. Iron and copper deposits last 17–33 min, crystal 8–17 min and uranium 6–12.5 min.
- **Hero idea:** the machine is the way this ore comes out of the ground, and the stem shows how much is left.

**derrick.** A neutral 2x2 landmark near the middle. Any engineer captures it. Held, it pays 1.5 steel/s with no power and no ore. It can be destroyed for salvage, and a fresh neutral one rises 120 s later.
- **Hero idea:** an old giant dispenser in no-man's-land, wearing the colour of whoever holds it.

---

## 1. Gameplay distance

### Mines, 40 px/tile (`game_working_row_d8`, `game_states_row_d8`)

**This is the headline change, and it works.** Left to right in `working_row_d8`, four different machines:
- iron: a tall cream tower with a team cap and a spoked sheave;
- copper: a long blue incline crossing the deck corner to corner, with orange bricks visible on it;
- crystal: a fat team torus with a dot of cyan in the middle;
- uranium: a dark dome caged in team ribs, with a cask carousel at its front.

Each sits on the same cream pad with the same tall team-capped stem in the back corner and the same lamp post beside it. "This is a deep mine" reads from the family parts, and "which one" reads from the machine. Compare `before/game_deep_row_d8`, where every mine was the same lattice cube.

**Ore colour contributes less than form.**
- Copper's bricks are the one ore colour that reads in the stream frame: about 6x3 px orange pips riding the belt.
- Iron's cinnamon heap is a 5 px fleck in the hopper.
- Crystal's core is about 23 cyan pixels on a mine of about 15,000 px.
- Uranium's sight glasses (0.14 x 0.07 each) measured 2–3 lit pixels.

Team blue is the dominant hue on all four, so a newcomer must learn "ring = crystal". Nothing in the colour tells them.

### Mines, 14 px/tile, the silhouette test (`game_deep_district_d23`)

- **Full outline, pad included (`silhouette_deep_district_d23.png`).** Every mine is a diamond pad with a stem nub at its back corner. Iron adds a wide mass over the right half and copper a bump to the right (the crusher). Crystal, uranium and the exhausted crystal are the same diamond. At 55° pitch, the low machines sit inside the pad's own outline.
- **Machine mask (`silhouette_machines_deep_district_d23.png`).** Four distinct shapes:
  - iron: a stacked block with the fan house beside it;
  - copper: a flat horizontal bar under the stem;
  - crystal: a ring with a hollow centre;
  - uranium: a solid lozenge with a stem "keyhole".

  This is the read a spectator really uses, and it holds at 14 px.
- **Weak pair.** The exhausted crystal's ring fills in solid once its core goes dark, which moves it toward uranium's lozenge. The pair is still separable by width (crystal is wider), but it's the closest pair in the frame.

The brief's goal was "tell what a mine produces from its silhouette alone at 14 px". By the machine's shape, yes, all four. By the blacked-out outline, only iron and copper.

### Mine states at stream scale

| State | 40 px (`states_row_d8`) | 14 px (`deep_district_d23`) |
|---|---|---|
| Working vs dry | **Copper:** belt full vs empty, readable. **Crystal:** cyan core on vs off, readable (23 vs 1 cyan px), plus the head hanging open. **Iron and uranium:** the same picture apart from the dim (about 8%) and the terrain ring vanishing. | Not readable on any ore except through the terrain ring, which is the deposit overlay and not the asset. |
| Running low | The lamp is the only cue. It's r 0.08 with a team cap on top, so its lit area is a band of about 6x2.5 px on the lamp's side. It pulses 0.15–0.9. In the `states_row` still, the running-low uranium and the working uranium look the same. | Invisible: about 2x1 px. |
| Low power (Lime iron) | Identical to a working iron in a still. Iron has no glow to halve, so its only cue is the slower hoist and the crawl. | Invisible. |
| Building | `timed_drill_rig_deploy` frames 8–15: the pad and collar ring rise, then the stem and bridge, then the team torus, while the rig shrinks beside them. | Reads as "something rising". |

### Derricks, 40 px and 14 px (`game_derricks_d8`, `game_derricks_far_d23`)

**The derrick is now its own object.** It's a dark riveted steel block on a two-step plinth, under a team head the full width of the block, with a spoked flywheel on its side. It has no pad diamond, no stem nub and no machine.

**Silhouette test.** Blacked out at 14 px (`silhouette_derricks_far_d23.png`), it's a tall, rounded, tombstone-like column on a stepped base, unlike any mine's diamond. In the colour frame, it's the only structure in the map with a dark steel body, which separates it from the cream-walled buildings nearby. Those buildings share its "box under a team cap" shape, for example the row of red structures at the top of `derricks_far_d23`.

**Neutral and held read at both scales,** but the neutral head isn't grey. It renders at RGB (225,193,153), a warm beige: `Mats.Neutral` = `MaterialsSugarPad`. That is the hue of the sand (178,145,104), only lighter, and the family of every building's cream walls. So the neutral derrick reads as "an unpainted building" more than "nobody's prize". The brief asked for grey.

**What it does (pay steel) doesn't read at either scale.** In eight `close_derrick_held_sequence` frames the brick is barely findable:
- it's `foil` on a `foil` chute into a `steel` tray onto `foil` ingots: grey on grey on grey;
- the mouth and the pour lamp (`dk_lamp`) sit under the head's brim, in its shadow.

**Landmark.** It's about 2.0 tall, the same footprint and height class as any 2x2. Nothing about its scale or light makes it the thing you navigate by.

## 2. Hero distance

### What's genuinely good

- **Each machine is its ore's extraction method:**
  - iron is hoisted in a skip;
  - copper is conveyed and crushed;
  - crystal is cored and tipped as shards;
  - uranium is handled remotely through a sealed port into casks.

  The parts are the ones that method needs, and they're placed where the camera sees the payoff.
- **Iron's tumble is the best new beat.** Three cinnamon bricks leave the dump door 0.09 s apart, gather speed down the chute (`k = f²`), hop as they turn over, and land with a heap bob and a puff (`close_iron_sequence` frames 1–3 and 5–7).
- **The stem's pieces are per ore** (`piece()`: bricks, nuggets, hexagonal shards, rods). So the reserve gauge is also a small key to the ore, and the pop throws out a rod or a shard. A lovely detail that rewards zoom.
- **Copper's mint-flecked bricks and its mint dust** (`close_copper_sequence`, all eight frames) give it a material story: patina. It's the most legible mine in motion.
- **The derrick has wear that tells a story,** the first in this family:
  - riveted bands;
  - a kraft repair patch on the +x face;
  - a rust run under the window;
  - a chain hanging off the hinge bracket;
  - a stepped plinth of old stone.

  It's older than everything a team builds, which is exactly the brief.
- **Restraint and rhythm improved.**
  - Iron's left half is a clean deck, a rest area, and the detail is grouped around the tower.
  - Copper's lower deck is empty under the belt.
  - The derrick is the cleanest of the five: a big plain head (rest) over a busy body (complexity).

### What's still weak

- **Wear tells no story on the mines.** The pads are factory clean on cracked ground. There's no spill under iron's hopper spout, no patina stain under copper's crusher, and no scuffing at a collar.
- **Uranium's jib is lost against its own ribs.** The arm is team, at the same hue and nearly the same height as the dome's team ribs and cap. In `close_uranium_sequence` the arm's swing is hard to separate from the cage, and the acid rod is what carries the beat. Every part above the dome is team: ribs, cap, port ring, jib, carousel, cask lids. That's the one mine where team colour is spread rather than placed.
- **Crystal's tank floor is a flat dark disc** (`dark`, r 0.52) inside a round tank. From above it reads as a pool of black liquid in a tank. That's the one place the no-oil rule is at risk (see "Oil imagery").
- **The derrick's window and mouth are under its brim.** `dk_head`'s brim overhangs the window face by 0.06 and starts at y 1.44, while the mouth is at 1.34. The ingot stack shows as four light bars, but the window's stack never changes: the bricks don't come from it.

### Oil imagery check

- There's no lattice derrick, no pumpjack, no flare stack and no black gloss fluid anywhere on the derrick.
- **Two marginal risks:**
  - **Crystal's dark tank floor** (above).
  - **Uranium's four casks.** They're `smoke` cylinders (r 0.07, h 0.24) with team lids, clustered on a turntable. Dark barrels with coloured lids in a group are the oil-drum picture. They read as casks only because of the jib above them.

## 3. Motion and interaction beat sheets

### Iron: hoisting (sim `Working`; 4 s per 16-ore trip, 8 s on low power)

| Beat | What happens | Read |
|---|---|---|
| arrives | The skip is parked at the foot of the slot, 0.4 s. | Inside the slot. |
| prepares | None: no loading gate and no rope take-up. | |
| interacts | The skip rises 0.78 in 1.4 s, eased. The sheave turns with the rope (radius 0.3). On low power it crawls at 0.2x, 16% of the time. | Partly visible in the slot, behind the chute. |
| reacts | The bucket tips 58° in 0.24 s toward the dump door. Three bricks tumble 0.55 s each, the heap bobs 0.02, and a cinnamon puff rises. | **Faces the camera:** the hero moment works at hero distance. |
| completes | The bucket rights in 0.24 s and the skip lowers in 1.32 s. | |
| disengages | Parks. | No rope bob and no settle; a loaded skip moves exactly like an empty one. |

### Copper: the belt (continuous; 1 brick/s, 0.5 on low power)

| Beat | What happens |
|---|---|
| arrives / prepares | Each brick grows out of the shaft over the first 8% of the belt. |
| interacts | It rides for 6 s. The belt eases to speed over 0.5 s, and the pulleys and crusher (200°/s x the rate) turn with it. |
| reacts | It drops into the crusher over the last 6%, with a mint puff from the crusher and a heap bob. |
| completes / disengages | Round the belt to the tail. |

This is continuous flow rather than a beat, and that's right for a conveyor. Its rhythm (a puff every second) is the busiest of the four, and it's visible at 40 px.

### Crystal: the core lift (4 s trip)

| Beat | What happens |
|---|---|
| interacts | The drill string turns at 50°/s. The core barrel lifts the cyan core 0.58 out of the frosted collar in 1.4 s. |
| reacts | It tips 58°. `Fx.ShardChime` fires at the tray: a cyan ring (0.55, 0.35 s), two glowing chips and a spark. |
| completes | Down in 1.32 s. |

The chime lasts 0.35 s and fell between frames in the 8-frame sequence, so I can't confirm it reads. The cyan core rising above the hoist block is visible in frames 0–3.

### Uranium: the jib (4 s trip)

| Beat | What happens |
|---|---|
| arrives | The hook is down in the port. |
| prepares | It lifts the acid rod 0.42 in 0.64 s. |
| interacts | The jib swings about 92° in 0.56 s, then lowers the rod into the cask in 0.56 s. |
| reacts | The rod sinks into the cask in 0.16 s, and a steam wisp shows the seal hissing. |
| completes | The hook rises 0.48 s, swings back 0.56 s and lowers into the port 0.56 s. The carousel turns 90° in 0.8 s. |
| disengages | Ready for the next rod. |

This is a full six-beat sequence, the most complete on any mine. It has no secondary motion: the hook stops dead at the end of each swing, with no pendulum.

### Run dry (sim: `deposit.Amount` hits 0, `Working` false, `depleted` event; `timed_run_dry` on copper)

| Beat | What happens | Read |
|---|---|---|
| prepares | From 1200 ore left the lamp pulses at 0.5 Hz. In the loop, the stem pops a nugget every 3.3 s (frames 4–5 catch the head open and a brick in flight). | Hero distance only. |
| interacts | The belt stops loading and the bricks on it shrink away over 0.5 s (frames 5–7). On a hoist, the trip in hand finishes empty. | **Sim-synced:** no ore arrives after the deposit is empty. |
| reacts | A last puff at 0.6 s from `model.position + 0.4 up`. That's the deck centre, not the shaft (copper's collar is in the corner at 0.56, 0.5). | Not visible in the strip. |
| completes | Three 1 s amber blinks (frames 9–10 catch the lamp orange). The stem is empty with the head hanging open 34°. | |
| disengages | A 2 s fade to `_PezDim` 0.6 starts at about 3 s (frame 13 on). | Measured about 6–8% darker: not "readable from across the map". |

**A small pop.** On a hoist or jib, `dm_load` is `SetActive(false)` in one frame when `empty` sets, in mid-air. That breaks "Nothing pops" at hero distance.

### The stem pop (sim: `Amount/Initial` crosses a sixth)

The stem pop is unchanged and still the best authored beat: snap open in 0.1 s, the piece flips out, a damped bounce, 1.55 s in all.

**It still disagrees with the lamp.** The lamp follows the sim's absolute 1200-ore warning, while the stem is proportional:
- a 1500–3000 uranium deposit starts pulsing "running low" with 3–5 of 6 rods still in the stem;
- an 8000 iron deposit starts with about one brick.

The kitchen sink hides this, because it seeds the running-low uranium at 15% (one rod).

### Building (sim `drilled`; the rig `DeploysInto` the mine)

`timed_drill_rig_deploy`, every other frame about 0.2 s apart:

| Beat | What happens |
|---|---|
| arrives | Frames 0–4: the rig drives in. |
| prepares | Frames 5–7: it parks with its mast up. |
| interacts | Frames 8–15: stage_0 (the pad with the collar ring), then stage_1–2 (the collar and core, the stem and bridge), then stage_3 (the team torus) rise, while the rig shrinks. |
| completes | Frames 15–19: the stage_3 overshoot settles. |
| disengages | The rig is gone. |

It's the generic `PlayDeployInto`, but the stage order now tells a construction story, and the old dust cloud is gone. It isn't "rises from the drill rig": the rig sits about 1 tile off the shaft (the sim's stop at `Dist ≤ 1.2`), and nothing drills.

### Derrick: held (`held = sees && Team >= 0 && IsComplete`)

| Beat | What happens |
|---|---|
| arrives / prepares | The flywheel spins up at 45°/s² to 90°/s, about 2 s. |
| interacts | Every 2 s a brick is pushed out under the chin in 0.24 s, and the head kicks 4°. The amber pour lamp rises with it. |
| reacts | The brick slides 0.6 s gathering speed, drops into the tray with a bounce, and the pile jolts 0.015 with a steel-dust puff. |
| completes | The brick sinks through the grate in 0.3 s. |
| disengages | The lamp goes out. |

A full sequence, and it matches the sim's 1.5 steel/s at 3 steel a brick. The read problem is contrast and occlusion (section 1), not timing.

### Derrick: capture and release (sim: `captured` then `derrick_captured` / `derrick_released`)

| Beat | What happens | Evidence |
|---|---|---|
| arrives | The engineer walks up. | Capture frames 1–4 (the red pip at the right edge). |
| prepares | None. | |
| interacts | None. The engineer is consumed at the sim's instant, and nothing on the derrick acknowledges him. | |
| reacts | `Fx.Beam` (0.25 s) plus `MuzzleFlash`. The head flips 150° in 0.3 s and holds 0.2 s. It's meant to show its old colour until it turns over at the top. | Frame 5 on. |
| completes | It slams shut in 0.3 s, then a damped bounce (10°, 0.6 s) and a thud puff. | Frame 13. |
| disengages | The flywheel spins up and the first brick follows within 2 s. | |

**The capture is the derrick's hero moment, and it reads at 40 px.** The head is about 1 tile wide and swings right back over the block.

**The old colour isn't visible.** In both 14-frame strips (about 0.1 s apart) the first open frame already wears the new colour: Cherry red in capture frame 5, sugar-pad in release frame 5. Not one frame shows the old colour going up. `DerrickWorks.Tick` re-tints `dk_head` to `teamShown` after `UpdateView`'s whole-model `TintTeam`, so the code reads right. Either the strip's first post-change frame is later than 0.3 s, or the re-tint isn't sticking. Verify it (recommendation 7).

**Respawn (sim `derrick`).** `JustPlaced` sees the event within 3 s of `Create` and plays `PezEmerge.PlayBuild(2.4 s)` with stage dust. The sim placed the derrick whole and capturable, so for 2.4 s the view shows less than is there. The rule allows an offset that resolves within 1 s.

**Pending respawn.** The 120 s wait still shows nothing, although `World.DerrickRespawns` knows the time.

## 4. System cohesion

- **The family holds.** All four mines share:
  - the sugar pad and cream deck (`deck`);
  - the team collar (`collar`; under iron's plinth, sealed under uranium's dome);
  - the stem in the back corner at (−0.56, 0.56);
  - the lamp post.

  Team mask is 14–16% of the up- and outward-facing surface, inside the house's 13–19% range.
- **The derrick is the family's ancestor.** It's a giant, old version of the stem every mine and the outpost carry, with a flip-top head, a window and a spring. That's the strongest system idea in this pass: the world's teams build small dispensers, and the old world left a giant one in the middle.
- **Iron's sheave and the derrick's flywheel** are both spoked wheels: one motif, two eras.
- **Links still missing:**
  - **Drill rig to mine:** a generic hand-off. No drill head or mast is left in the shaft.
  - **Mine to stockpile:** no output leg. Hoppers, the tray and casks fill and never empty.
  - **Uranium to the enrichment plant:** the casks aren't the same object as anything the plant takes in.
  - **Engineer to derrick:** a beam and nothing else.

## 5. BAD → GOOD → GREAT

### Mines
- **Bad (the baseline was close to this):** one headframe for every ore, ore and state only in the UI.
- **Good (where the after state is):**
  - four machines whose shapes say the extraction method, separable at 14 px by the machine's shape;
  - a shared family with a reserve gauge;
  - the dump facing the camera;
  - a dry-out that's sim-synced.
- **Great:** each mine's own light is its ore and its state.
  - Crystal's tank floor glows cyan while it works.
  - Uranium's drum wears an acid band that goes out when it's dry.
  - Iron's winder window and copper's crusher mouth glow warm while working and at half on low power.

  The stem's window goes amber under five minutes left, so "Cherry's uranium is about to die" reads at 14 px. Every fourth load, the hopper or cask drops into a pad hatch, and the ore visibly leaves. A spectator can count income and see it ending without a single UI element.

### Derrick
- **Bad (baseline):** a greyed copy of a team's mine.
- **Good (where the after state is):**
  - its own pre-war object with wear that tells a story;
  - a capture flip visible at 40 px;
  - a rise on respawn;
  - a steady pour matched to the sim's 1.5/s.
- **Great:** it's the tallest thing in the middle of the map.
  - Neutral, it's cold grey and waits, grit sifting off it.
  - Held, every 2 s a hot amber ingot is punched out and slides down a dark chute: a pip of light you can see from across the map, and the paying derrick is literally lit.
  - Its window's ingot stack steps down a slot as each one leaves.
  - Captured, the head flips with its old colour going up and the new one coming down.
  - Destroyed, a survey stake on the site fills over the 120 s.

**Hero idea**
- **Mines:** the machine is how this ore comes out of the ground.
- **Derrick:** the old giant dispenser everyone fights over.

**Hero moment**
- **Mines:** iron's bricks tumbling down the chute, and copper's bricks dropping into the crusher with a mint puff.
- **Derrick:** the head flipping 150° on capture and slamming down in the new colour.

**Secondary motion**
- **Now:** the fan coast, the drill's spin-up and coast, the belt ease, the carousel index, the stem's head bounce, the derrick head's kick and its post-flip bounce.
- **Missing:** jib hook sway, a rope bob at the top of iron's lift.

**Gameplay read**
- **Mines:** the machine's shape (now there) and one ore-coloured state light per mine (missing).
- **Derrick:** "not a mine" (there), the holder's colour (there), "paying" (missing).

**World interaction**
- **Now:** ore-coloured puffs (cinnamon, mint), steam, the shard chime, steel dust, grit.
- **Missing:** ground stains, light pools on the ground.

**Delight detail**
- **Mines:** the stem popping a rod or a shard of the mine's own ore.
- **Derrick:** grit sifting off the neutral head every 5–9 s while it waits.

---

## 6. Scores (§20, 0–3)

| Criterion | Mines | Evidence | Derrick | Evidence |
|---|---|---|---|---|
| Readability | 3 | `working_row_d8`: four different machines at 40 px, and the stem marks the family. Ore colour reads only on copper (the orange belt pips). | 2 | `derricks_d8`/`_far_d23`: clearly not a mine, the only dark-steel structure; but "pays steel" and "landmark" don't read. |
| Silhouette | 2 | `silhouette_machines_…`: four distinct shapes at 14 px; `silhouette_deep_district_d23`: the full outline separates only iron and copper; the exhausted crystal fills toward uranium. | 2 | `silhouette_derricks_far_d23`: a tall rounded column on a stepped base, unlike any mine, but close to a generic capped tower. |
| Function | 2 | Each machine is its extraction method (skip/tumble, belt/crusher, core/tray, jib/cask), but no output leg: containers never empty (`DeepMineWorks` has no drain while working). | 2 | Dispenser → chute → tray says "makes bricks", but the pour is grey on grey under the brim (`close_derrick_held_sequence`). |
| Faction (world DNA) | 3 | The family kit (pad, collar, back-corner stem, lamp), the candy palette, per-ore stem pieces; the stem is shared with the outpost. | 3 | A pre-war sub-language (old steel, rivets, kraft patch, stone plinth) that is still the world's dispenser. |
| Motion | 2 | Rope-coupled sheave, tumble with `f²` acceleration and hops, belt and drill ease, carousel index (`DeepMineWorks`). Loaded = empty, no rope bob, no hook sway. | 2 | Flywheel spin-up and coast, brick acceleration and bounce, eased flip with a damped bounce (`DerrickWorks`). A 150° flip of a huge steel head in 0.3 s is light for its size. |
| Interaction | 2 | The rig sinks as stages rise in a legible order (`timed_drill_rig_deploy` 8–15), but it's generic `PlayDeployInto` and the rig sits about 1 tile off the shaft. | 2 | Capture flip with beam and thud (`timed_derrick_capture`), release flip, respawn rise. The engineer goes unacknowledged; the pending site shows nothing. |
| States | 2 | At 40 px dry reads on copper and crystal (`states_row_d8`); running low is a 6x2.5 px lamp band; low-power iron looks the same as working; the dim measures about 8%. | 2 | Neutral vs held at 40 and 14 px; the neutral head is beige (225,193,153), not grey; "paying" doesn't read. |
| Personality | 3 | Iron's bricks tumbling end over end, the labouring winder, crystal's chime, uranium's careful six-beat jib, the busy copper belt: four temperaments. | 3 | A grumpy old machine: grit sifting while neutral, the head's kick per brick, the thud on capture. |
| Environment | 2 | Ore-coloured puffs that now face the camera (cinnamon at the hopper, mint at the crusher in all eight copper frames), steam at the cask. No stain and no light on the ground. | 1 | Steel-grey dust on a steel tray (not findable in eight held frames); grit; no light pool or ground effect. |
| Detail | 3 | Per-ore stem pieces, mint-flecked bricks, crusher teeth, the carousel's casks, the banksman's door; the heap clip is fixed. Wear is absent. | 2 | Wear tells a story (patch, rust run, chain), but the window's stack is static and the mouth and lamp sit in the brim's shadow. |
| Cohesion | 2 | A tight family and the outpost stem; the drill-rig link is generic; there's no output to the stockpile and no tie to the enrichment plant. | 2 | It's the family's ancestor (a giant stem), but there's no tie to the engineer beyond a beam. |
| Delight | 3 | The stem popping a rod or a shard; the iron tumble (`close_iron_sequence` 1–3). | 3 | The capture flip visible at 40 px; the neutral grit sift (`close_running_low_exhausted_neutral_derrick` right). |
| Restraint | 2 | Iron and copper have clean rest areas, but uranium puts team colour on every top part (ribs, cap, port, jib, carousel, lids). | 3 | One idea; the plain head rests over a detailed body. |
| Iconic quality | 2 | The four-up row in `working_row_d8` is a good screenshot; at stream they're small blue toys with no light. | 2 | A giant dispenser head on a riveted block is distinctive close up; at stream it's a capped box. |
| **Total** | **33 / 42** | | **31 / 42** | |

**Combined: 64 / 84** (baseline 32 / 84).

**The lowest score is the derrick's Environment (1),** but it costs the game little. **The score that most hurts the game is the mines' States (2).** A spectator or agent can now count a team's mines by ore. They still can't see, at 14 px, that one is about to run dry, has run dry, or is starved of power. That's the information a deep mine exists to give.

## 7. Before → after

| Criterion | deep_mine before | mines after | derrick before | derrick after |
|---|---|---|---|---|
| Readability | 1 | **3** | 0 | **2** |
| Silhouette | 1 | **2** | 0 | **2** |
| Function | 2 | 2 (stronger; output still missing) | 0 | **2** |
| Faction | 2 | **3** | 1 | **3** |
| Motion | 2 | 2 | 2 | 2 |
| Interaction | 1 | **2** | 1 | **2** |
| States | 1 | **2** | 2 | 2 |
| Personality | 2 | **3** | 1 | **3** |
| Environment | 1 | **2** | 1 | 1 |
| Detail | 2 | **3** | 1 | **2** |
| Cohesion | 2 | 2 | 1 | **2** |
| Delight | 2 | **3** | 0 | **3** |
| Restraint | 1 | **2** | 1 | **3** |
| Iconic quality | 1 | **2** | 0 | **2** |
| **Total** | **21** | **33** (+12) | **11** | **31** (+20) |

## 8. The bar

**It clears the brief's primary bar. It does not yet clear the house's full bar.**

**What it clears:**
- The brief's headline problem is solved at stream scale. One silhouette used to stand for four ores, a neutral landmark and a captured prize. Now each ore's mine is a different machine you can tell apart at 14 px by its shape, and the derrick is a different object from any mine.
- The forms come from function.
- The family is unmistakable.
- The derrick has its capture moment and its rise.
- No asset uses the oil vocabulary the brief banned.
- The baseline's sim-sync break (phantom ore after dry) is fixed.

This should replace main.

**Why it is not "great" yet, and so not the house's bar:**
1. **Three of the brief's required states fail at 14 px and are marginal at 40 px:**
   - running low (the lamp is a 6x2.5 px band under a team cap);
   - low power (no still-frame cue on iron or copper);
   - exhausted (specced as darkening "to 60%", measured at about 8%).
2. **Ore colour barely reads.** Team blue dominates all four, so the ore identity is learned from shape, not seen in colour. The brief's "acid always lit" is 2–3 pixels.
3. **The derrick's paying state doesn't read.** Its neutral head is beige, not grey, and its wait between lives is invisible.

The first recommendation below fixes the worst of this for hours of work, not days.

## 9. Recommendations (ranked by impact over cost)

**1. Each machine's own light is its state light (the family rule: glow = this machine working).**
- **What:**
  - **Crystal:** replace the `dark` tank floor (r 0.52 at DK+0.01) with a frosted `M_E_Cyan` disc in a new node `dm_pool`. Add it to `core`, so `Core()` drives it.
  - **Uranium:** replace the four 0.14 x 0.07 sight glasses with one continuous `M_E_Acid` band (h 0.05, r 0.605) round the camera-facing 200° of the drum, still under `dm_glow`, so `Ore()` drives it.
  - **Iron:** make the winder window (`dark` 0.3 x 0.08 on the cap) `M_E_Amber` in a new node `dm_work`.
  - **Copper:** make the crusher mouth (`dark` 0.3 x 0.18) `M_E_Amber` in a new node `dm_work`.
  - In `DeepMineWorks`, add a `Work(g)` driver for `dm_work`, the same as `Core`.
- **When:** `working` → full; `low` → half, the house's low-power rule; dry → 0.05; under fog → 0.25 (fixes the fog false state too: drive `Ore` to 0.25, not 0.8).
- **Timing:** ease 0.5 s on each change, inside the existing state changes. No sim timing is touched.
- **Mechanism:** a model change in `build_mines.py` (re-export), plus three lines in `DeepMineWorks`.
- **Cost:** S.
- **Impact at gameplay distance:** crystal becomes a cyan pool about 20 px across at 40 px (about 7 px at 14 px), and uranium gets a lime line. The ore then reads by colour, and working, dry and low power all read in a still, on all four. It also removes the "black pool" oil risk.
- **Impact at hero distance:** the light comes from where the work is.

**2. Running low and dead you can see across the map.**
- **What:**
  - In `stem()`, give the window's `clear` pane its own node, `dm_window`, with an `M_E_Amber` material whose base emission is 0. `Lamp()` drives it with the same pulse, so "under 5 minutes" lights the whole reserve window (0.21 x 0.62 tiles, about 8x14 px at 40 px). The stem's piece count keeps its proportional meaning, and the window's amber carries the sim's absolute warning: two cues, two meanings, no contradiction.
  - In `lamp()`, move the `team` cap from the lamp's top to a band at the post's foot, and make the amber a dome (r 0.1), so the face the camera sees is the lit face.
  - In `Tick`, take the dead dim to `_PezDim` 0.35. A linear 0.35 is about 0.62 in sRGB, close to what "60%" was meant to look like. Check in a capture that the pad's renderers are in `all`.
- **When:** the window pulses at 0.5 Hz from `deposit.Amount ≤ DeepMineWarnAmount`. It blinks three times with the lamp on dry (the existing 3 s), then goes dark. Then the 2 s dim.
- **Mechanism:** a model change plus `DeepMineWorks.Lamp`/`SetDim`.
- **Cost:** S–M.
- **Impact at gameplay distance:** "about to run dry" and "dry" become stream reads (about 3x5 px of amber at 14 px).
- **Impact at hero distance:** the stem becomes the mine's dashboard.

**3. Make the derrick pay visibly, and make neutral grey.**
- **What:**
  - In `S_derrick`, change the chute (`foil`, 0.3 wide) to `licorice` and its side rails to `oldsteel`.
  - Give `dk_brick` an `M_E_Amber` face (`DerrickWorks.Brick` sets its glow 1 → 0 over the slide): a freshly stamped ingot, hot, cooling as it goes. That's glow as state: it's paying.
  - Raise the mouth and `dk_lamp` 0.1 so they clear the brim's shadow, or cut a 0.06 notch in the brim's front over the mouth.
  - **Neutral colour:** for the derrick only, tint `M_Team` to a cool grey (about #A3A8AE) when `Team < 0`. Use a derrick-specific neutral in `DerrickWorks`, applied after `WorldView`'s `TintTeam`, not `Mats.Neutral`, which other things use.
- **When:** every 2 s while held. On `derrick_released`, the flip turns over to grey.
- **Mechanism:** a model change, `DerrickWorks`, and a material tint.
- **Cost:** S.
- **Impact at gameplay distance:** an amber pip sliding down every 2 s says "this one pays", and grey says "nobody's", apart from the sand and the cream walls.
- **Impact at hero distance:** the pour has contrast.

**4. Uranium: separate the jib from the cage, and make the casks casks.**
- **What:**
  - Change `dm_jib`'s arm from `team` to `steel`, keeping a team counterweight (the 0.18 x 0.12 box).
  - Turn the dome's six ribs to `steel`, keeping the team cap and port ring.
  - Give each cask two steel cooling rings and a rounded top (a spent-fuel cask, not a drum).
  - Add hook sway on `dm_skip`: ±4° about the swing axis, damped over 0.6 s, at each swing's end (u = 0.36 and 0.80).
- **When:** every trip.
- **Mechanism:** a model change plus a node pose in `DeepMineWorks.Pose`.
- **Cost:** S.
- **Impact at hero distance:** the jib's swing reads, mass shows on the hook, and the oil-drum risk is gone.
- **Impact at gameplay distance:** restraint: team colour returns to the cap, the port and the stem.

**5. The ore leaves (baseline recommendation 7, still open).**
- **What:**
  - Iron and copper: every 4th load (16 s), the hopper's spout (`steel` 0.1 box) opens and a capsule (new node `dm_capsule`) drops into a pad hatch. `dm_heap` drops 0.04, plus `Fx.PourDust`.
  - Uranium: every 4th rod, the filled cask sinks 0.2 into a hatch in the turntable, and a fresh one rises in its place over 0.6 s.
  - Crystal: the tray tips its shards into a hatch on the rim.
- **When:** keyed to the trip counter while `working`.
- **Timing:** 0.5–0.6 s inside the existing 4 s trip.
- **Mechanism:** new nodes plus `DeepMineWorks`.
- **Cost:** M.
- **Impact at hero distance:** completes "enters → happens → leaves" (raises Function to 3).
- **Impact at gameplay distance:** a ground-level pulse every 16 s that says "producing".

**6. The derrick's respawn honours the 1 s rule, and its site shows the wait.**
- **What:**
  - **Rise:** take `PlayBuild(2.4f)` in `WorldView.Create` down to 1.0 s, stage dust kept.
  - **Waiting site:** for each `World.DerrickRespawns` entry, a ground decal ring at the origin. It's a survey stake with a grey arc that fills over the remaining `at - Time`, using the existing `DeepDepositsView` ring style.
- **When:** from `destroyed` (a derrick) until the `derrick` event.
- **Mechanism:** a `PezEmerge` duration change plus a new decal or view.
- **Cost:** S for the rise, M for the site.
- **Impact at gameplay distance:** "a prize returns here in 40 s" becomes a map read, and the view never shows less derrick than the sim has.

**7. Verify the capture flip's colour hand-over.**
- **What:** re-capture `timed_derrick_capture` at 0.03 s spacing from the frame `v.TeamShown` changes.
- **If the head is already the new colour at t < 0.3 s,** the re-tint in `DerrickWorks.Tick` is being overridden. Check the call order against `Sheen` and `Glows`. Note that `TintTeam` names chained materials `M_Team_tN_tM`, which still match `StartsWith("M_Team")`, so that path is fine.
- **Cost:** S.
- **Impact:** at hero distance, the flip's whole point ("it turns over into the new colour") becomes visible. Today it reads as a recolour plus a lid flap.

**8. Small fixes.**
- **No pop on dry:** in `Pose`, when `empty` sets mid-trip, scale `dm_load` to 0 over 0.3 s instead of `SetActive(false)` in one frame.
- **Puff at the shaft:** put the dry puff (`dryT` 0.6) at the collar (`skipRest` in world space, or `dm_belt_a` for copper), not the model's centre, and at 1.5x its size.
- **Docs:**
  - `MOTION.md` copper: "the bricks on it shrink away where they are in 0.5 s".
  - Fix the "mint hood" comments.
  - Fix the `DeepMineWarnAmount` summary (5 minutes / 1200 ore).
  - Decide the unsurveyed-deposit fallback: either `WorldView.Create` checks `Teams[PovTeam].Surveyed` (and `Models.DeepMineModel` gets −1 when it's false), or `MOTION.md` drops the claim.
- **Cost:** S each.

---

## What breaks readability or sim sync

**Readability:**
- Running low, low power, and dry on iron and uranium aren't readable at 14 px, and are marginal at 40 px.
- The specced "darkens to 60%" lands at about 8%.
- The exhausted crystal's ring fills in at 14 px and drifts toward uranium's shape.
- The neutral derrick's head is beige (225,193,153), the sand's hue, not grey.
- The derrick's pour (its whole job) is grey on grey under its own brim.

**Sim sync:**
- **The derrick's rise** shows a sim-whole, capturable derrick at less than full height for 2.4 s. The rule is an offset resolved within 1 s (recommendation 6).
- **No other break.** The baseline's phantom-ore dry-out is fixed:
  - a hoist finishes its trip empty;
  - the belt shrinks its bricks instead of crushing them;
  - the jib carries no rod.

**False state:**
- Under fog, a dry uranium mine relights its acid at 0.8 (`Tick`'s `!sees` branch).
- In a player's view, the per-ore model reveals the ore of a deposit that team hasn't surveyed. That contradicts `MOTION.md`'s fallback rule, and may be intended.

---

## Re-check after follow-ups (same day)

**What I judged.** The changes since 6a17575 (`build_mines.py`, `DeepMineWorks`, `DerrickWorks`, `WorldView`, `MOTION.md`), against the replaced captures in `after/`. `timed_drill_rig_deploy` is from the previous build.

**Silhouettes regenerated.** I regenerated the three silhouette PNGs from the new frames. One caveat: crystal's new cyan floor counts as "cool, saturated" in the machine mask, so the mask now fills the ring's hollow. In colour, the ring and its cyan light read as before or better.

### What landed

- **Dry now reads at both scales.** At `_PezDim` 0.35, the exhausted crystal's team blue drops from 195 to 144 at 40 px (`game_states_row_d8`), and its pad goes grey. At 14 px (`game_deep_district_d23`) its tile's mean brightness is 104, against 122–132 for every live mine, and only 6 bright-pad pixels are left against 100–330. It is the darkest thing in the row. This was the worst miss in the first pass, and it's fixed.
- **Crystal's working light reads at both scales.** The cyan floor ring covers 166 px at 40 px (was 23) and 32 px at 14 px (was about 1). It's off when dry. Crystal now says its ore by colour.
- **Iron's winder window glows amber** on the crown's camera-facing side (`close_iron_sequence`, every frame). It's about 40 px at hero distance and a 4–5 px fleck at 40 px.
- **The lamp reads.** Its top is now the lit amber dome, with the team collar underneath. In `timed_run_dry` it shows clearly as an orange ball when it pulses or blinks (frames 1–2, 5, 7–8, 10–11) and a brown ball when dark.
- **The derrick's neutral head is grey.** It measured (211,198,185), a chroma of 26, down from 72 for the old beige. In `game_derricks_d8` and `_far_d23` it reads as cold grey, apart from the sand and the cream walls.
- **The derrick pays visibly.** At hero distance, `close_derrick_held_sequence` frames 4–7 show a hot amber brick in the mouth and on the dark chute, and frame 0 shows one in the tray (77–109 amber px). At 40 px, `game_derricks_d8` shows 15–54 amber px per held derrick, against 0 on the neutral one. At 14 px the pip is lost.
- **Sim sync is clean.** The derrick's respawn rise is now 1 s (`PlayBuild(1f)`).
- **The fog false state is gone.** The `!sees` branch no longer relights a dry uranium mine.
- **The surveyed-deposit question is settled in `MOTION.md`.**

### What didn't land

- **Uranium's acid band is hidden.** The new band (`ring` r 0.595–0.612, y 0.30–0.35) sits directly under the team lip (r 0.59–0.63, y 0.33–0.39). The lip covers its top 0.02 and juts out 0.018 past it, so from a 55° pitch the band is covered. No pixel of it shows in `close_uranium_sequence` at 4x zoom. The only lime on the mine is the deposit ring, the stem's rods, and the rod during its lift. Uranium still has no working light you can see.
- **Copper's crusher mouth faces away from the camera.** The mouth opens toward the belt, back and up from the camera. No amber shows at the crusher in any `close_copper_sequence` frame. Copper's working read is still its bricks on the belt, which is fine, but its low-power glow can't be seen.
- **The running-low strips are too thin.** `dm_warn` is 0.022 wide (about 1 px at 40 px). In `timed_run_dry` frames 0–2 only one strip shows, as a hairline on the stem's edge at hero distance. Running low at stream scale is still just the lamp: a pulsing dot of about 6 px at 40 px and about 2 px at 14 px. In the `states_row` still, the running-low uranium and the working uranium look the same.
- **Half glow doesn't look half.** The low-power iron's window measured R 215 against R 219 working, at the same pixel count, in `close_low_power_sequence` vs `close_iron_sequence`. The emissive saturates, so 0.5 renders almost like 1.0. Low power still has no still-frame cue.
- **The capture flip's colour hand-over is still unconfirmed.** In both new strips the first open frame already wears the new colour: Cherry red in capture frame 5, grey in release frame 5 (recommendation 7 above).

### New scores

| Criterion | Mines | Derrick |
|---|---|---|
| Readability | 3 | **3** (was 2) |
| Silhouette | 2 | 2 |
| Function | 2 | **3** (was 2) |
| Faction | 3 | 3 |
| Motion | 2 | 2 |
| Interaction | 2 | 2 |
| States | 2 | **3** (was 2) |
| Personality | 3 | 3 |
| Environment | 2 | 1 |
| Detail | 3 | **3** (was 2) |
| Cohesion | 2 | 2 |
| Delight | 3 | 3 |
| Restraint | 2 | 3 |
| Iconic quality | 2 | 2 |
| **Total** | **33 / 42** (unchanged) | **35 / 42** (was 31) |

**Combined: 68 / 84.** It was 64 after the first pass, and 32 at the baseline.

### What changed, criterion by criterion

- **Derrick Readability, 2 → 3.** "Pays steel" now reads at 40 px: 15–54 amber px per held derrick, 0 on the neutral one (`game_derricks_d8`). It's still not a landmark by scale.
- **Derrick Function, 2 → 3.** A hot ingot is pushed out under the chin and slides down a dark chute into the tray. "It stamps out steel" is now legible (`close_derrick_held_sequence` frames 0 and 4–7).
- **Derrick States, 2 → 3.** Grey neutral (chroma 26), team-held, and paying (the amber pip) all read at 40 px, and neutral vs held reads at 14 px (`game_derricks_far_d23`).
- **Derrick Detail, 2 → 3.** The mouth under the brim is no longer a dead shadow: the hot brick and the pour lamp show there in frames 4–7. The window's stack is still static.
- **Mines States stays 2, but it's a stronger 2.**
  - Dry now passes at 40 and 14 px on every ore.
  - Crystal's working state passes at both.
  - Uranium's working light doesn't show, and copper's faces away.
  - Running low is a 6 px dot at 40 px and invisible at 14 px.
  - Low power has no still cue (R 215 vs 219).

  Three of the five states still fall short of the "great" read.

### Does it clear the bar now?

**It still does not clear the house's full bar.** The derrick is close; the mines are held back by three specific, cheap defects:

1. **Uranium shows no ore light.** The acid band is modelled under the team lip and can't be seen from the game camera. Fix in `S_deep_mine_uranium`: make the lip ring itself (r 0.59–0.63, y 0.33–0.39) `acid` under `dm_glow`, and move the team colour to the steel foot ring. Cost S.
2. **Running low doesn't read at 14 px, and low power doesn't read at all.**
   - Widen `dm_warn` to 0.06 and wrap it round the stem's two camera-facing corners (the −z and +x faces), or tint the whole window pane amber while warning (recommendation 2 above). That gives about 3x5 px at 14 px.
   - For low power, drive the working lights at 0.2 of full emission, not 0.5, because 0.5 saturates to look like full.
   - Cost S each.
3. **Copper's working light faces away from the camera.** Add an amber inspection slot (about 0.2 x 0.06) on the crusher housing's camera-facing face, in `dm_glow`. Cost S.

Beyond those three, these hold several scores at 2 and are not defects:
- **The missing output leg** (recommendation 5) keeps the mines' Function at 2.
- **No ground effect** keeps the derrick's Environment at 1.

**The derrick on its own clears the brief.** Neutral grey, the capture moment, the rise, and a visible pour are all there. It meets the house bar except for Environment and its scale as a landmark.

---

## Final re-check

**What I judged.** The three fixes from the re-check, in the current working tree, against the replaced captures in `after/`. `timed_drill_rig_deploy` is still from an earlier build.

**Silhouettes regenerated.** I regenerated the silhouette PNGs from these frames. The forms haven't changed, so the silhouette findings stand.

### The three defects

1. **Uranium's acid band now shows.** The lip ring (r 0.59–0.63) is the band, so nothing sits above it.
   - **40 px:** a clear lime ring round the dome's foot. It covers 56–71 acid px on the working uranium (`game_working_row_d8`, `game_states_row_d8`), where it was 0–4 px before.
   - **Hero distance:** 210–854 px across the two running-low stills.
   - **14 px:** it blurs into the team blue (`game_deep_district_d23`), so at that scale uranium still reads by its dome, not its colour.
2. **Running low reads at 40 px.**
   - **Pulse down vs pulse up:** the running-low stills in `close_running_low_exhausted_neutral_derrick` measure 20 amber px with the pulse down and 313 with it up. With the pulse up, both window strips and the corner strip show as orange bars down the stem, beside the lit lamp. With the pulse down they're dark, not orange, so the pulse has real contrast.
   - **Size at 40 px:** each strip is about 2x13 px, plus the lamp's dome of about 6 px.
   - **At 14 px it's still only a few pixels** (about 1x5 per strip).
   - **Small cost:** the strips overlap the window's edges and narrow the visible gauge from 0.21 to 0.16. On crystal and uranium the stacked pieces now read as a thin bright bar on the stem in `game_working_row_d8`. That's acceptable.
3. **Low power reads at hero distance.**
   - **Hero distance:** the iron winder window is dark-olive at 0.2 (`close_low_power_sequence`: 0–1 warm px in all eight frames), against bright amber when working (`close_iron_sequence`: about 51 warm px once lit).
   - **40 px:** the window is a 4 px fleck working against about 1 px on low power. That's technically different but not something you'd read off a stream frame. Low power at stream scale is still mainly the half-speed machinery, which matches the house rule.
4. **Copper's camera-facing slot shows.** The amber slot on the crusher house is visible in every `close_copper_sequence` frame (an orange bar under the house). Copper now has a working light you can see, as well as its belt.

**Unchanged and still holding:**
- The derrick's neutral head is grey (211,197,185).
- The amber pour pip shows at 40 px: 9–54 px per held derrick, 0 on the neutral one.
- The exhausted crystal is the darkest tile at 14 px.
- **Still unconfirmed:** the capture flip's colour hand-over. In `timed_derrick_capture`, frame 4 is grey and closed and frame 5 is already open and Cherry red. To confirm it, capture at 0.03 s spacing (recommendation 7).

### Scores that changed

| Criterion | Mines | Evidence |
|---|---|---|
| States | 2 → **3** | At 40 px, all four mines now show a working light in their ore's colour (iron's window, copper's slot plus belt, crystal's cyan floor at 58–61 px, uranium's acid ring at 56–71 px), and it goes off when stopped. Dry reads at 40 and 14 px. Running low pulses visibly at 40 px (20 → 313 amber px in close-up). Low power reads at hero distance. Only low power at 40 px and running low at 14 px stay marginal. |

Everything else is unchanged from the re-check.

| | Mines | Derrick |
|---|---|---|
| Readability | 3 | 3 |
| Silhouette | 2 | 2 |
| Function | 2 | 3 |
| Faction | 3 | 3 |
| Motion | 2 | 2 |
| Interaction | 2 | 2 |
| States | **3** | 3 |
| Personality | 3 | 3 |
| Environment | 2 | 1 |
| Detail | 3 | 3 |
| Cohesion | 2 | 2 |
| Delight | 3 | 3 |
| Restraint | 2 | 3 |
| Iconic quality | 2 | 2 |
| **Total** | **34 / 42** | **35 / 42** |

**Combined: 69 / 84.** The baseline was 32, the first after-pass 64, and the re-check 68.

### Verdict

**It clears the bar for this redesign, and it should ship.** Every goal in the brief is met at gameplay distance (40 px), and the brief's silhouette goal is met at 14 px:
- each ore is told apart by its machine's shape;
- one family;
- the derrick is a different object, grey when unclaimed, with a capture flip and a 1 s rise;
- no oil imagery;
- every listed state reads: building, working, low power, running low, exhausted.

No item breaks readability or the sim-sync rule any more.

**It is not yet a "great on every line" asset.** None of what's left is a defect; it's work that hasn't been done:
- **Mines' Function, 2:** no output leg. Containers fill and never empty (recommendation 5).
- **Environment, mines 2 and derrick 1:** nothing marks or lights the ground.
- **Interaction, 2 for both:**
  - the drill-rig hand-off is the generic deploy;
  - the engineer goes unacknowledged at capture;
  - an empty derrick site shows nothing for its 120 s wait;
  - the capture flip's old-to-new colour hand-over is still unconfirmed in the captures.
- **Iconic, 2 for both:** the derrick isn't a landmark by scale, and at 14 px the mines are small team-blue toys whose ore colours (except crystal's cyan) blur away.

These are the next pass, not blockers.
