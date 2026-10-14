# Deep mine and neutral derrick: BEFORE (baseline)

2026-10-13. Baseline for a before/after review. Judged: `origin/main` at f959194. The asset files last changed in 039b752 ("The deep mine is a toy headframe"). The working tree's redesign is not judged here.

Sources read (all from `origin/main`):
- `art-src/tools/build_models.py` `S_deep_mine`
- `unity/Assets/Pez/View/DeepMineWorks.cs`
- `WorldView.cs` (the capture tint at ~851, the headframe tick at ~864, the legacy sheave spin at ~915, the `captured` event at ~1537)
- `Models.cs` `TintOre`/`TintTeam`
- `Fx.PourDust`
- the sim: `Defs.cs` (`deep_mine`, `derrick`); `World.cs` `DeepMineTick`, `DeepMineWarnAmount`; `Economy.cs` derrick section; `Map.cs` `SeedDeep`
- `docs/art/MOTION.md` `deep_mine`, `docs/art/KITCHEN_SINK.md`

Captures:
- Stream frames: `deep_row_d8`, `deep_district_d23`, `derricks_d8`, `derricks_far_d23`.
- Close-ups: `Deep_mine_working_00..05`, `Deep_mine_exhausted`, `Deep_mine_on_low_power`, `Neutral_derrick`, `Derrick_held_00..03`.
- Silhouette test: thresholded `deep_row_d8` and `derricks_d8`.

**Spec vs implementation.** `DeepMineWorks` matches `MOTION.md` closely. I checked each timing:
- The skip rises 1.4 s and lowers 1.32 s; the bucket tips 58°.
- The winder's crawl is 0.43 s in each 2.7 s.
- The lamp pulses at 0.5 Hz and blinks at 0.9 Hz.
- The fan turns at 540°/s, or 270 on low power.

So this review judges the implementation, and these are the gaps:
- **No view handling for the asset's own events.** `drilled`, `depleted`, `derrick`, `derrick_captured` and `derrick_released` have none. A capture uses the generic `captured` beam.
- **Legacy code in `WorldView.cs` ~915.** It still spins `rig.Turret` for any `ModelKey == "deep_mine"`. That is the procedural fallback (`Models.DeepMine`, "Four-legged derrick leaning in over the shaft"). It's dead for the .glb, but it would fight `DeepMineWorks` if a model ever gained a `turret` node.
- **Stale sim comment.** `World.DeepMineWarnAmount`'s summary says "two minutes of pumping", but the value is 300 s (1200 ore).

---

## Role

**deep_mine.** A drill rig deploys it onto a deep deposit. It pumps 4 ore/s of that deposit's ore straight into the stockpile, draws 50 power (half rate on low power), warns at 1200 ore left, and stops when the deposit is dry. Deposits hold:

| Ore | Deposit size | Pumping time at 4/s |
|---|---|---|
| iron, copper | 4000–8000 | 17–33 min |
| crystal | 2000–4000 | 8–17 min |
| uranium | 1500–3000 | 6–12.5 min |

**Hero idea it should have:** *this machine pulls **this ore** out of the ground, and you can see how much is left.* The ore should be legible from the silhouette.

**derrick.** A neutral 2x2 landmark near the middle that any engineer captures. Held, it pays 1.5 steel/s with no power, no ore and no deposit. It can be destroyed for salvage, and a fresh neutral one rises on the spot 120 s later.

**Hero idea it should have:** *a prize in no-man's-land, and whose flag is on it.* It must never read as a team's deep mine.

On main the derrick has no model of its own: `ModelAs = "deep_mine"`.

---

## 1. Gameplay distance (40 px/tile and 14 px/tile, JPEG q60)

**The headline: every deep mine and every derrick is one silhouette.** The thresholded `deep_row_d8` shows the copper, exhausted-uranium and low-power-iron mines as the same blob. The thresholded `derricks_d8` shows the neutral, Blueberry-held and Cherry-held derricks as the same blob. Each blob is a tilted slab carrying a lattice cube with a taller block to its right, and nothing separates the two sets.

At 14 px/tile (`derricks_far_d23`), the derrick row (grey, blue, red) and the deep-mine row (blue, blue, green) are indistinguishable. A Cherry-held derrick reads as "Cherry has a deep mine here".

**Ore type does not read from the model.** The only per-ore parts are the `M_OreTint` parts:
- the bin's heap (`dm_heap`): three spheres, r 0.06–0.09, about 5–7 px at 40 px/tile;
- the stem's bricks, behind smoke plastic;
- the skip's load, mostly inside the headframe.

In `deep_row_d8` the copper heap is a 4-px orange fleck and the iron heap is invisible. What does say the ore at stream scale is the dashed deposit ring, which is the terrain overlay and not the asset. The exhausted uranium mine has no ring at all, so it says nothing.

**The tallest solid mass is the dispenser stem, not the shaft.** The stem's team head (`dm_head`) tops out at about 1.72. The sheave (`dm_sheave`) reaches 2.19, but it's an open 3-spoke wheel with a 0.035 rim. At 40 px/tile that's about a 1.4-px line, and it dissolves under JPEG.

So the eye goes to a team-coloured block on the right. The headframe's four cream posts (0.08 wide, about 3 px) read as a cage. The cage over an open shaft is the "oil derrick" read the brief complains about.

**States don't read at stream scale:**
- **Working vs dry.** The skip is 0.23 wide and runs behind the front posts and girts. The fan is 0.2 across. The lamp (`dm_lamp`, r 0.06) is about 2.4 px. In `deep_row_d8` the exhausted mine (middle) and the running copper mine (left) are the same picture.
- **Low power.** It shows as a slower hoist and a half-speed fan, with no still-frame cue. The Lime mine in `deep_row_d8` looks like the others.

**Team colour reads strongly.** It's on the crown, the girts, the sheave, the drum flanges, the bin rim, the house band, the stem head, the fan-house band and the shaft collar. Neutral vs held derrick also reads: sugar-pad grey vs team at both 40 and 14 px. That is the one thing at stream scale that clearly works.

**Deploy.** The drill rig's deploy (the crystal zone in `deep_row_d8`) shows as a biscuit dust cloud over the pad. That's the generic `PlayDeployInto`, and it's the loudest event in the deep-mining row.

## 2. Hero distance

**What's genuinely good.** The mechanism is honest and reads as engineered:
- **Rope geometry.** The rope's run from drum to sheave is computed as the outer tangent between the two (`be = al + acos((rd - rs)/L)`).
- **Coupled rotation.** `dm_drum` and `dm_sheave` are driven by the skip's travel through each radius, so they speed up, slow down and reverse with it (frames 00–05).
- **Rope length.** `dm_rope` stretches to `dm_eye` exactly.
- **The dispenser stem is the asset's soul.** A brick stack sits on a spring under its follower plate, with a flip-top team head. It's the game's own namesake joke. Frame `Deep_mine_working_00` catches the head popped and a brick leaving. That's a real delight at zoom.

**The best mechanical beat faces away from the camera.** The bucket (`dm_bucket`) tips toward +z, to the bin at the back-left at yaw 45. The dump happens between the 0.98 girt and the 1.38 team girts, behind the front posts.

In six frames 0.6 s apart, the tip never shows. Frame 03 shows a fleck of load at the shaft collar, and frame 05 shows the skip half-hidden under the crown. The chute (`steel`, 0.16 wide) and the bin are on the far side.

**Rhythm is flat.** The 2x2 deck carries ten distinct features:

1. the headframe
2. the crown
3. the sheave
4. the drum
5. the backstays
6. the chute
7. the bin
8. the fan house
9. the stem
10. the lamp

There's also a door and three windows. The only rest surface is the hoist-house roof, and the drum and stem crowd it. This is complexity → complexity: visual oatmeal at stream scale (§10).

**Wear tells no story.** There's no wear anywhere: no ore spill round the bin, no rope grease, no scuffing at the shaft collar. The pad is factory-clean while the ground around it is cracked by the deposit.

**Bug: the drained heap clips through the bin.** When dry, `dm_heap` is translated down 0.3, to y 0.47. Its side spheres reach 0.14 from the axis, but the funnel's mouth is 0.08 at y 0.48. So the heap pokes out between the pedestal top (0.46) and about 0.53.

On uranium it glows: `Deep_mine_exhausted` shows an acid-green blob under the empty bin. That blob is the most visible thing on an exhausted mine, and it reads as "still has uranium".

**The derrick at hero distance.** `Neutral_derrick` is the deep mine with every team part greyed out. `Derrick_held_00..03` is a Blueberry iron deep mine, pixel for pixel. Its detail tells the wrong story:
- The stem stays full forever. It has no deposit, so `frac = 1`, and six bricks say "a full deposit".
- The hoist pours iron ore into a bin, though the derrick pays steel.
- The "running low" lamp can never light.

---

## 3. Motion and interaction beat sheets

### deep_mine: hoisting (sim: `Working`, 4 s per 16-ore trip)

| Beat | What happens now | Read |
|---|---|---|
| arrives | Skip at rest in the shaft, `u` 0–0.1 (0.4 s) | Hidden inside the deck. |
| prepares | none | No load-in at the bottom: no chute gate, no rope take-up. |
| interacts | Skip rises 1.1 in 1.4 s, eased both ends. Drum and sheave spin up with it. | Good mechanics, but behind the front posts. |
| reacts | Bucket tips 58° in 0.24 s, holds 0.2 s, ore scales out. `Spill()`: heap bobs 0.02, `Fx.PourDust` puff. | Faces away; the puff is a faint haze behind the frame (frame 00). |
| completes | Bucket rights in 0.24 s, skip lowers in 1.32 s. | Fine. |
| disengages | Parks, next trip | No rope bounce or settle at the top or bottom: the rope is rigid. |

**Mass.** The easing is correct but symmetric. A loaded skip and an empty skip move identically: up 1.4 s, down 1.32 s. There's no rope stretch at the start of the lift and no overshoot or bob when it parks at the top.

**Secondary motion.** The fan coasts down at 180°/s² when work stops, which is good. The heap bobs. Nothing else moves: no rope sway, no stem vibration, no exhaust from a hoist house that has a door and windows.

**Idle.** A working mine is never idle. Stopped (dry, or unbuilt) it is fully still, which is correct for dry. A neutral derrick is also fully still, which is wrong for a landmark: it looks abandoned, not "available".

### deep_mine: stopping (sim: `depleted`, `Working` false)

This sequence breaks the sim-sync rule.

| Beat | What happens now |
|---|---|
| arrives | none |
| prepares | none |
| interacts | The trip in hand finishes, including the dump: the skip carries ore up and pours it into the bin after the sim says the deposit is dry. |
| reacts | The lamp starts its 0.9 Hz blink, which is 2.4 px at stream. |
| completes | Over 1.5 s the heap slides down through the bin and clips out of it (see the bug above). |
| disengages | The head hangs open 34°. |

Finishing the trip contradicts the sim for up to 4 s (up to 8 s on low power). The fix belongs in the recommendations.

### deep_mine: the stem pop (sim: `deposit.Amount / Initial` crosses a sixth)

| Beat | What happens now |
|---|---|
| prepares | none |
| interacts | The head snaps open 48° in 0.1 s. The brick (`dm_candy`) rises 0.26, flips over the front lip and shrinks away. The stack rides up a slot. |
| completes | The head drops shut with a damped bounce, 1.55 s in all. |

This is the best authored beat on the asset, and it has real anticipation and follow-through. It fires every 62–125 s on uranium and every 167–333 s on iron and copper, and it's hero-distance only.

**It disagrees with the lamp.** The sim's warning is absolute (1200 ore), but the stem is proportional. A 1500-uranium deposit starts pulsing "running low" with five of six bricks still in the stem. An 8000-iron deposit pulses at about one brick.

### deep_mine: arrival (sim: `drilled`; the rig `DeploysInto` the mine)

The generic `PlayDeployInto` plus a dust cloud. There's no bespoke beat: no drill going in, no headframe raised, no shaft opened.

### derrick: capture (sim: `captured` then `derrick_captured`, at the instant the engineer is removed)

| Beat | What happens now |
|---|---|
| arrives | The engineer walks up. |
| prepares | none |
| interacts | none |
| reacts | `Fx.Beam`, a vertical team beam for 0.25 s, plus `MuzzleFlash` 0.4. `TintTeam` swaps every `M_Team` part in the same frame. The hoist starts from the bottom. |
| completes | none |
| disengages | none |

**Release** (`derrick_released`): an instant swap back to grey, no beat. **Respawn** (`derrick`, 120 s after `destroyed`): `PlaceDerrick` spawns it at progress 1. No rise is choreographed, and the empty site shows nothing while it waits, although the sim knows the time (`DerrickRespawns`).

---

## 4. System cohesion

The deep mine belongs to the world's design language:
- the palette: cream, team, steel, licorice, smoke plastic;
- the rounded candy construction;
- the dispenser stem it shares with the outpost.

It's cohesive with the outpost, but not with the drill rig. Nothing on the mine says a rig became it: no shared mast, no drill head left in the shaft.

**There is no output path.** Ore goes up and into the bin, and the bin never empties. The sim sends that ore to the stockpile, but the model has no "leaves" step. Section 1 of the art direction asks for "material enters → something happens → output leaves", and the third clause is missing.

The derrick has no system at all. A neutral steel-paying landmark is drawn as a team ore extractor. Its relationship to the engineer, its one interaction partner, is a 0.25 s beam.

---

## 5. BAD → GOOD → GREAT

**deep_mine**
- **Bad:** one generic headframe on a pad for every ore, with a spinning wheel while working. Ore type, depletion and power state live only in the UI.
- **Good (main is mostly here at hero distance):** a mechanically honest hoist whose drum, sheave and rope agree. A remaining-ore gauge built into the model, team colour on the parts that matter, and a lamp for running low and dry.
- **Great:** four mines, one per ore, each with a silhouette that says its ore at 14 px/tile:
  - a squat heavy iron crusher;
  - a copper winder with a coil of cable;
  - a crystal lattice drill with a glowing core;
  - a uranium mine with a containment cask hoist.

  The dump faces the camera, so every trip lands a visible pour and dust burst. When a mine runs dry its silhouette changes: the crown beacon goes red, the sheave stops, the hoist cage drops. You can count a team's ore income by eye from the stream.

**derrick**
- **Bad (main):** another building's model, greyed for neutral and recoloured on capture.
- **Good:** its own model with its own silhouette, which says "neutral prize, pays steel": no ore, no stem, no deposit gauge. A team flag that changes hands.
- **Great:** a landmark you navigate by. Neutral, its beacon sweeps a slow grey light, and it's alive and waiting. On capture, the old flag drops, the engineer's team flag runs up the mast, and the beacon turns team colour. Held, it stamps out steel ingots onto a little stack. Destroyed, its site keeps a survey stake whose fill shows the 120 s until it rises again.

**Hero idea**
- **deep_mine:** the ore you're pulling up is the silhouette.
- **derrick:** the flag on the prize.

**Hero moment**
- **deep_mine:** the bucket tipping toward the camera, ore pouring in its colour, every 4 s. Second to it, the stem popping a brick.
- **derrick:** the capture: flag down, flag up, beacon flips.

**Secondary motion**
- **deep_mine:** the rope bobs as the loaded skip parks at the top, the fan coasts, the head bounces.
- **derrick:** the flag sway shader, the beacon sweep.

**Gameplay read**
- **deep_mine:** ore type and dry vs working, from silhouette plus one large state light on top of the frame.
- **derrick:** "derrick, not mine" and the holder's colour.

**World interaction**
- **deep_mine:** an ore-tinted spill stain round the bin that grows with ore mined, and a dust puff per dump.
- **derrick:** the beacon's light pool on the ground.

**Delight detail**
- **deep_mine:** the stem pop, which already exists.
- **derrick:** none exists. A candidate is the flag snagging for 0.2 s halfway up on capture.

---

## 6. Scores (§20, 0–3)

| Criterion | deep_mine | Evidence | derrick | Evidence |
|---|---|---|---|---|
| Readability | 1 | `deep_row_d8`: reads as "a mine machine", but ore type only shows through the terrain ring; the exhausted mine has none. | 0 | `derricks_d8`/`_far_d23`: a held derrick is identified as that team's deep mine. |
| Silhouette | 1 | Thresholded `deep_row_d8`: copper, uranium and iron mines are one blob. | 0 | Thresholded `derricks_d8`: identical to the deep-mine blob. |
| Function | 2 | Shaft → skip → chute → bin explains extraction (`S_deep_mine`), but there's no output path to the stockpile. | 0 | Geometry says "extracts ore from a depleting deposit"; it pays steel from nothing. |
| Faction (world DNA) | 2 | Cream/team/steel/licorice candy, the stem shared with the outpost. | 1 | World DNA holds, but "neutral" is only the absence of team colour (`Neutral_derrick`). |
| Motion | 2 | Drum and sheave coupled to the rope, eased skip, fan coast-down (`DeepMineWorks.Tick`); no rope settle, loaded = empty. | 2 | The same hoist, run while held. |
| Interaction | 1 | Drill-rig arrival is a generic `PlayDeployInto` dust cloud (`deep_row_d8`); no bespoke `drilled` beat. | 1 | Capture is a 0.25 s `Fx.Beam` plus an instant `TintTeam`; release and respawn have no beat. |
| States | 1 | Dry, low-power and working look the same at 40 px (`deep_row_d8`); the lamp is 2.4 px; stem and lamp disagree on "low". | 2 | Neutral grey vs team colour reads at 40 and 14 px (`derricks_far_d23`); a pending respawn shows nothing. |
| Personality | 2 | Stem pop with head bounce (`working_00`); the labouring winder on low power. | 1 | Borrowed; neutral is inert. |
| Environment | 1 | A faint pour puff (frame 00); a clean pad on cracked ground; no spill, stain or light. | 1 | An iron pour puff only. |
| Detail | 2 | Zoom rewards it (spring, follower, rope tangents), but the tip is never visible in six frames, and there's the heap clip (`Deep_mine_exhausted`). | 1 | The same detail tells a false story: a full stem forever, iron ore. |
| Cohesion | 2 | Shares the stem with the outpost, the palette with the world; nothing links it to the drill rig. | 1 | A copy of a team building; no tie to the engineer. |
| Delight | 2 | The brick pop, every 1–5.5 min, hero distance only. | 0 | The pop never fires (no deposit), and nothing replaces it. |
| Restraint | 1 | Ten features on a 2x2 deck, and the stem outranks the headframe at stream. | 1 | The same clutter, half of it meaningless here. |
| Iconic quality | 1 | Reads as an oil derrick; the stem joke is lost at stream. | 0 | An oil derrick in a game without oil, borrowed. |
| **Total** | **21 / 42** | | **11 / 42** | |

**Combined: 32 / 84.**

**The lowest item that most hurts the game: Silhouette** (derrick 0, deep mine 1). One silhouette stands for four ores, a neutral landmark and a captured prize. A spectator or agent counting "Cherry's deep mines" from the stream counts Cherry's derricks too, and can't tell copper income from uranium. Every other fix sits on top of this one.

---

## 7. Recommendations (ranked by impact over cost)

**1. Fix the drained heap clip.**
- **What:** `dm_heap` in `DeepMineWorks.Tick`.
- **When:** `drain` goes from 0 to 1 (`complete && dry && !running`).
- **Timing:** within the existing 1.5 s. Shrink the heap with `localScale = Vector3.one * (1 - drain)` instead of translating it 0.3 down, and `SetActive(false)` at `drain ≥ 0.98`.
- **Mechanism:** `PezMotion`-style node pose in `DeepMineWorks`.
- **Cost:** S.
- **Impact:** at hero distance, it removes a glowing uranium blob that says "not empty" on an empty mine. At stream, a crystal or uranium mine stops glowing when dry.

**2. Stop the dry-out from pouring phantom ore (the sim-sync fix).**
- **What:** `DeepMineWorks.Tick`, its running/`u` logic.
- **When:** `working` turns false (`depleted`, or the deposit hits 0).
- **Timing:** if `u < TipOut`, hide `dm_load` and lower the skip from where it is, at the `Down` speed (≤ 1.3 s). If `u ≥ TipOut`, finish as now. The view then never shows ore arriving after the sim says none exists.
- **Mechanism:** node pose in `DeepMineWorks`.
- **Cost:** S.
- **Impact:** sync correctness. At hero distance the dry-out reads as a stop, not one more delivery.

**3. One big state light, and one meaning for "low".**
- **What:** move `dm_lamp` from the hoist-house roof (r 0.06) to the crown, above the sheave, as a 0.16-radius dome beacon. That's 6–7 px at 40 px/tile.
- **When:** dark while working; amber 0.5 Hz pulse from the sim's `DeepMineWarnAmount` crossing; red 0.9 Hz blink on dry.
- **Timing and stem cue:** at the warn crossing, also flash the remaining stem bricks once (`dm_res_*`, 0.3 s emissive). The stem keeps its proportional meaning, and the warning gets its own beat instead of contradicting the stack.
- **Mechanism:** a model change in `S_deep_mine` (re-export), plus the existing `Lamp()`.
- **Cost:** S–M.
- **Impact:** dry vs working vs running low becomes readable at 40 px. Today it needs zoom.

**4. Give the derrick its own model, with its own capture beat.**
- **What:** a new `S_derrick` with no shaft, stem or ore parts. Drop `ModelAs = "deep_mine"` from the `derrick` def, which is view-only.
- **Silhouette:** a single tall mast with a flag (`flag` node, vertex-shader sway) and a beacon (`beacon` node).
- **When and timing:**
  - Neutral (`Team < 0`): a slow grey beacon sweep, 0.25 rev/s.
  - `captured` / `derrick_captured`: the old flag drops in 0.3 s and the new team flag runs up in 0.7 s. The view may run the swap over this 1.0 s, with the beam kept.
  - `derrick_released`: the flag drops in 0.6 s.
  - `derrick` (respawn): the `PezEmerge` stages rise in ≤ 1.0 s.
  - A pending site (`World.DerrickRespawns`): a ground stake whose ring fills over the 120 s.
- **Mechanism:** new art, `PezMotion`/`PezEmerge` nodes, the existing flag shader, and an `Fx` light for the beacon.
- **Cost:** needs new art, then M for the view.
- **Impact:** at 40 and 14 px, a held derrick stops being mistaken for a deep mine (Readability, Silhouette and Function go from 0 toward 2). At hero distance it gains a capture moment.

**5. Per-ore deep mine silhouettes.**
- **What:** four .glb variants. The sim key stays `deep_mine`, and the view picks the model by `world.Map.DepositById(e.DepositId).Type` at view creation in `Models`/`WorldView`.
- **The read:** each variant differs in its top 0.6 tiles, the only part that survives 55° pitch at 14 px/tile. It's carried by a solid mass (not open spokes or thin posts) in the ore's colour family, plus team colour.
- **When:** on view creation; the deposit type never changes.
- **Timing:** the hoist timing stays at 4 s/trip, matching 16 ore per trip.
- **Mechanism:** new art via the art pack (`build_models.py` / re-export). `DeepMineWorks` is shared, and node names are kept per variant.
- **Cost:** needs new art (L).
- **Impact:** at stream scale, ore income is countable by eye, and this is the asked-for fix. At hero distance, four mechanisms where there was one.

**6. Turn the dump toward the camera.**
- **What:** in `S_deep_mine` (or each variant), put the bin and chute on the camera-facing side at default yaw 45 (−z/+x), and raise the dump above the 1.38 girts so the tip clears the frame.
- **When:** every trip, at `u` = 0.45–0.62. That's 0.68 s of tip and pour inside the existing 4 s.
- **Mechanism:** a model change plus `tipDir`, which already derives from `dm_bin`'s position.
- **Cost:** M.
- **Impact:** the hero moment becomes visible at hero distance, and the pour puff reads as "ore landed" at 40 px.

**7. Give the ore a way out.**
- **What:** every 4th trip, which is 64 ore and 16 s, the bin's `dm_heap` drops 0.04 and a capsule (new node `dm_capsule`) drops into a ground hatch beside the pad, with a puff.
- **When:** keyed to the trip counter while `Working`.
- **Timing:** 0.5 s.
- **Mechanism:** `PezMotion` node plus `Fx.PourDust`.
- **Cost:** M.
- **Impact:** at hero distance the pipeline's last beat exists ("output leaves"). At stream it adds a ground-level pulse that says "producing".

**8. Hygiene.**
- Delete the `rig.Turret` deep_mine branch in `WorldView.cs` (~915).
- Fix the `DeepMineWarnAmount` summary ("two minutes" should say 5 minutes / 1200 ore).
- **Cost:** S.
- **Impact:** none visible; it prevents a future fight between two drivers of the same node.

---

## What breaks readability or sim sync

- **Readability:**
  - A held derrick is indistinguishable from the holder's deep mine, at both 40 and 14 px.
  - The four ores share one silhouette.
  - A dry mine looks like a working one at stream scale.
- **Sim sync:** on `depleted` the view finishes the trip in hand, including the dump. It shows ore delivered for up to 4 s (8 s on low power) after the sim's deposit is empty. Recommendation 2 fixes it.
- **False state:** the derrick's stem shows a full deposit forever, and the drained uranium or crystal heap glows through an exhausted mine's bin.
