# Art review: the economy loop (mining_truck + mining_refinery, ore fields, command_center as drop-off)

Date: 2026-10-03. Reviewer: art-director agent. Standard: `docs/art/ART_DIRECTION.md` (§5 worked example).

**What I judged.** The models are the v0.1 blockouts in `unity/Assets/Pez/Resources/PezModels/`. Motion is judged **as implemented**: `WorldView.cs` (harvester branch, lines 351–363), `PezMotion.cs` and `TerrainView.cs`. Gaps against `docs/art/MOTION.md` are listed in their own section. The sim facts come from `World.cs` `UpdateHarvester` and `DockPoint` (lines 1345–1406) and from `Defs.cs`.

**Screenshots.** I looked at `/tmp/pezz-render/view/before_close_base.png`, `before_wide_base.png`, `p_ref.png` (the refinery before and after the render pass), `pair_close.png`, `pair_zoom.png`, `p_stream.png`, `after1_close_base.png`, `after2_overview.png` and `early_grid.png`. The targets were `docs/art/boards/02_Hero.png`, `hero_offaxis.png`, `01_Main.png` and `04_Motion.png`. For the gameplay-distance test I scaled `before_close_base` and `after1_close_base` (about 46 px/tile) down to about 40 px/tile and saved them as JPEG q60. The silhouette test thresholds that frame against the ground colour. Neither screenshot set shows a truck mid-dock or a live ore field near a base: at 21:26 the base fields are mined out. So the dock choreography below is judged from code and timings, not from frames.

---

## 1. Role

The truck hauls ore from a field to the nearest drop-off. The refinery turns stockpiled ore into steel and copper. This loop is on screen for the whole match.

**Hero idea:** *a candy press fed by a brick hauler.* The truck is an oversized open hopper on tracks, with a toothed cutter drum in front. The refinery's south bay reaches out with a chute to take the load.

## 2. Sim facts the choreography has to fit (authoritative)

| Fact | Value | Source |
|---|---|---|
| Truck speed | 1.8 tiles/s; no acceleration in the sim, so it stops dead | `Defs.cs:152`, `StepToward` |
| Vehicle turn | 5 rad/s (286°/s). If more than 0.6 rad off heading, it turns in place first | `World.cs:1871` |
| Capacity | 150, one ore type per trip | `Defs.cs:152` |
| Mining | 4 ore per 0.1 s, so a full bin takes about **3.8 s** on one tile | `World.cs:1374–1383` |
| Dock point | `(Origin.X + SizeX/2, Origin.Y − 0.5)`: centre of the south edge, **0.5 tiles outside the footprint** | `World.cs:1406` |
| Arrival tolerance | Stops anywhere within **0.6** of the dock point, at **whatever heading the path left it** | `World.cs:1392` |
| Unload | 10 ore per 0.1 s, so a full load takes **1.5 s** (a partial load takes ceil(cargo/10) × 0.1 s). Then it switches to Harvest and drives off on the next tick | `World.cs:1393–1403` |
| Events | **None.** `Unload()` and the `unloaded` event (line 1085) belong to transports and passengers, not ore. The view has to infer docking from `Order == ReturnOre` and falling `Cargo` | `World.cs:1085–1104` |
| Drop-offs | command_center, mining_refinery and outpost (`DropOff = true`). The truck goes to whichever is **nearest**, so the CC takes much of the traffic | `Defs.cs:129,131,149` |
| Refining | The refinery is `Working` whenever the team **stockpile** holds iron or copper ore, at half rate on low power. It is not tied to a particular truck | `World.cs:805–824` |
| Two trucks | Truck radius is 0.5 and tolerance is 0.6, so **two trucks can unload at once, side by side** about 1 tile apart. A third gets jostled by `Separate()` | `World.cs:1934` |

**Geometry consequences.** The refinery's bay is a 0.9 × 0.9 notch. Its kraft floor runs from local z −1.4 to −0.5 and the door is at z −0.48. A truck at the dock point has its centre at z −2.0, so even a perfectly reversed truck has its bin lip about 1 tile from the door. The bay faces −Z (south), and the dock point is south of the footprint row. For a truck to arrive heading due south (bin into the bay), its last path step would have to come out of the building. So **the bin never points squarely into the bay.** It arrives nose-first or side-on.

## 3. Gameplay distance (40 px/tile, JPEG q60)

- **Refinery: readable.** The twin team-capped silos and the bay notch survive the downscale and the silhouette threshold. In the blacked-out frame the notch is still visible, so the "material enters here" point reads. The foil roll and steel arm read as "a grey cylinder" with no job.
- **Truck: weak.** In the silhouette it is a rounded rectangle about 40 × 25 px that could be any vehicle. In colour it is a dark slab with a red square. Without a barrel, nothing says "hauler". The bin is a *closed* 0.34 × 0.45 box (`bin__M_Dark`, 12 tris) about the same size as the team cab, so the "absurd cargo" idea from §3 is inverted. The cutter is a smooth 0.38-diameter cylinder: at 240 rpm it looks identical to 0 rpm.
- **Load state: partly there, and wrong for 3 of 4 ores.** `bin_ore` rises in 5 steps and shows as a cinnamon patch about 12 px across. But `bin_ore` uses `M_Ore_Cinnamon`, and nothing tints it by `CargoType`. In the art-model path `Models.TintOre` only swaps `M_OreTint*` materials, and WorldView's tint code (line 379) runs only for procedural placeholders. So copper, crystal and uranium loads all display as iron.
- **Idle vs working: no difference.** Trucks parked because surface ore is exhausted (`WarnSurfaceExhausted`) look exactly like trucks about to work. That strategic signal ("go deep") is invisible. `before_close_base` shows four of them around the refinery.
- **Ore fields: stale stains.** The ore-pad ellipses are baked once into the terrain vertex colours (`TerrainView.OrePads`, from `BuildGround`) and never update. Mined-out fields keep a full-strength rust or orange disc with nothing on it (`before_close_base`, both large discs). At stream distance that reads as "ore here". It misleads spectators and agents.
- **Stream framing.** In `p_stream.png` (about 20 px/tile, half the standard) the truck is about 20 × 14 px. The bin tip (0.26 tiles of lift) would be 5 px. Anything in the dock sequence meant to read on the stream must be **light and colour** (lamps, an ore-coloured burst), not a 35° hinge.
- **Render pass (`after*`, `p_ref` right).** It helps grounding: the plinths, the kraft dock ramp and the chamfers. In `after1` the bloom washed team red to salmon. The later `p_ref` restores it. Amber dock lights now bloom, which is good, but only once they mean something (see R3).

## 4. Hero distance

- **Truck: 260 tris** (budget for vehicles is 3,000–7,000 in `01_Main`). Licorice slab hull, smoke body, team cab box, closed bin. There are no tracks, so there is nothing for mass to show through. No wear. It is a blockout and reads as one.
- **Refinery: about 600 tris** (budget 10–18k). There is good rhythm: a quiet cream hall, then the busy silos and roll, then the clean bay. The amber lintel and kraft floor mark the interaction point, which is right. But the foil roll is baked into `stage_2` (no node) and the conveyor bar ends in mid-air. Nothing explains *output*: §1 asks for "enters here → happens inside → leaves here", and only the first part exists.
- **Command center as a drop-off.** It has no intake at all. A truck unloading at the CC parks in the CC's unit exit, and `OpenDoorNear` rolls up the CC's *vehicle door* (MOTION.md says that door opens only in the deploy intro). The outpost, by contrast, has a kraft drop pad and an amber strip (`stage_1__M_Kraft`, `stage_1__M_E_Amber`). The CC is the one drop-off with no receiving hardware.

## 5. Motion and interaction: beat sheets

### Harvest (as implemented)
| Beat | What happens now |
|---|---|
| Arrives | Drives to the tile centre; 3° brake dip (`HullFeel`). |
| Prepares | Nothing. |
| Interacts | `spinner` ramps to 240 rpm in 0.4 s (invisible on a smooth drum). `bin_ore` steps up 5 times over about 3.8 s. |
| Reacts | Ore clusters sink one at a time, every 200 ore (`TerrainView.Update`, checked every 0.5 s). **This is good**: the world responds. |
| Completes | Nothing. The truck turns and drives. |
| Disengages | Spinner stops in 0.4 s (spec says a 1.5 s coast). |

No dust, no chips thrown into the bin, no tracks, and no difference between laden and empty in how it drives.

### Delivery (as implemented): between §5 BAD and GOOD
| t (s) | What happens now |
|---|---|
| −∞…0 | Drives at 1.8 tiles/s straight to within 0.6 of the dock point, with no slowdown. |
| 0 | Stops dead (3° dip). Faces wherever it came from: nose-in or side-on, never bin-in. |
| ≈0.1 | First cargo tick. `TipBin()` tips 35° about the rear hinge (0.48 s up, 0.36 s hold, 0.36 s down). At the same moment `OpenDoorNear` starts the door roll-up (0.35 s). **The refinery reacts after the ore has started moving.** |
| 0.1–1.3 | The bin tips, often **away from the refinery** or sideways. The ore patch shrinks. Nothing pours and there is no dust. Ore vanishes about 1 tile from the door. |
| 1.5 | Cargo reaches 0. The truck drives off on the next tick (it turns first if more than 34° off). |
| 2.1 | Door closes. |

It's functional, but it contradicts itself: a refinery opening its door while a truck dumps the other way is the kind of moment that makes a viewer stop believing the world.

## 6. System cohesion

The materials are coherent: cream plastic, licorice, spring steel, kraft and amber appear across the roster. The bay is 0.9 wide and the truck 0.62, so somebody sized one for the other. But the interface is missing:
- there is no shared connector (chute, hopper lip, clamp)
- the truck shares nothing with the refinery but the amber
- the CC drop-off has no hardware at all

"Of course this truck belongs with that refinery" doesn't land yet.

---

## 7. BAD → GOOD → GREAT

**BAD (roughly what ships).** A dark box drives up beside the refinery at any angle and stops. A lid tilts, sometimes toward the building, often not. The door opens a beat late. The ore number goes up and the box drives off. Mined-out fields stay painted as fields.

**GOOD.** The truck has a visible open hopper with ore in its true colour. It stops at the bay mouth, and the bed tips toward the bay. Ore particles fall and a dust puff follows. The door is already open. The foil roll turns while the refinery is working.

**GREAT, fitted inside the sim's real 1.5 s.** Everything before t = 0 is anticipation and everything after 1.5 s overlaps the drive-away, so nothing waits on the view.

| t (s, 0 = sim stop) | Beat | Mechanism |
|---|---|---|
| −1.4 (truck `ReturnOre`, within 2.5 tiles of its dock point) | **The refinery prepares.** Bay lamps start an amber chase at 2 Hz. The door rolls up (0.35 s). The chute slides out 0.5 tiles toward the apron (0.5 s, ease-out, 0.03 overshoot). | New `dock_lamps` and `chute` nodes on the refinery; `SetDoorOpen(true)` |
| −0.3…0 | **Brake and settle.** The laden truck dips 4° (not the empty 3°), and the hull squats 0.02 tiles while it's full. | `HullFeel` scaled by `Cargo/HarvestCapacity`; `Body` local y offset |
| 0…0.35 | **Pivot to the bay** (tracked, so a pivot is honest). The view yaws the model so its rear faces the chute (≤180° at up to 520°/s, with a 3° lean into the turn). It also eases the model up to 0.35 tiles back toward the bay mouth onto the plinth ramp (`Plinths.HeightAt`). Skip the pivot if the remaining unload is under 0.6 s. | View-only yaw and position offset on `rig.Root` |
| 0.35…0.5 | **Engage.** The chute lip drops 0.05 onto the bin lip ("clunk") and the lamps go to solid amber. | `chute` node |
| 0.5…0.85 | **Bed lifts** to 45°, ease-in-out. | `PezMotion.TipBin`, retimed |
| 0.6…1.2 | **Ore pours.** 14–18 bricks in the cargo's ore colour slide from the bin lip down the chute. A biscuit dust puff (6 particles) rises at the chute mouth. `bin_ore` drains with cargo. The chassis rises 0.02 as the weight leaves. | `Fx.Debris`-style chunks + `S.Dust` in `Fx`; `Body` offset |
| 1.2…1.45 | **Bed slams down** in 0.25 s with a 1.5° hull bounce. **The lamps turn steady cream**, which means clear. | `TipBin` curve; lamp emission |
| 1.5 (sim drives off) | **Disengage.** The model blends back to the sim's position and facing within 0.4 s. Departure is usually away from the building, so the pivot has already pre-turned it. The chute retracts (0.5 s). The door rolls down 0.5 s after the truck is 1.5 tiles clear. The lamps go off. | Existing slerp; `chute` and `door` |
| Overlap | **Refinery reacts.** The foil roll spins up while `Working`. Its rpm is halved on low power, and it stops when the stockpile has no ore. | New `spinner` node (axis X) |

**Hero idea:** a brick hauler feeding a candy press, with an oversized open hopper on the truck, a reaching chute on the refinery, and amber lamps that say "docking".
**Hero moment:** the chute reaches out, the bed tips, and a slide of ore-coloured bricks (glowing, for crystal and uranium) pours into the press.
**Secondary motion:** laden squat and unladen rise; a hull bounce when the bed slams; spinner coast-down over 1.5 s; foil roll turning with the refinery's work; idle engine shiver of 0.005 at 12 Hz.
**Gameplay read (must survive 20–40 px/tile):** ore colour in the hopper; amber lamp chase means "docking", cream means "clear"; the ore-colour burst at the bay; the "idle, no ore" posture (R6); and live ore stains.
**World interaction:** cutter dust and chips at the field; clusters sinking (already done); the refinery apron slowly staining in the colours of the ore it has received; field stains fading as they're mined out.
**Delight detail:** *the stubborn last brick.* When the bed slams down, one brick that stuck in the hopper pops out, bounces twice on the apron and rolls to rest, then fades after 3 s.

---

## 8. Scores (§20, 0–3)

| Criterion | Score | Evidence |
|---|---|---|
| Readability | 1 | Refinery reads (twin red silos and notch in `before_close_base` at 40 px/tile). The truck reads as a dark slab with a red square, and is only identifiable from context. |
| Silhouette | 1 | The blacked-out refinery keeps its silos and notch. The truck becomes a featureless rounded rectangle (silhouette test on `before_close_base`). |
| Function | 1 | The bay notch, kraft floor and amber lintel say "enters here". The bin is a closed box and the cutter a smooth drum. The roll and arm are unexplained, and nothing shows output. |
| Faction / world DNA | 1 | The palette is consistent, but rendered grey the truck is a generic box. The "everything dispenses" shape language (`01_Main` §04) is absent from the truck. |
| Motion (mass) | 1 | Only the generic 2° and 3° hull spring (`HullFeel`). It is the same laden or empty, with no tracks and an instant stop. |
| Interaction | 1 | Bed tips 35° (`PezMotion` bin) and the door rolls, but the door is reactive (it opens on the first unload tick). The bin never points into the bay and ore vanishes 1 tile from the door. |
| States | 1 | `bin_ore` steps exist, but copper, crystal and uranium show as iron. Idle trucks look like working ones. The refinery's working, starved and low-power states are invisible. The amber strip is always on. |
| Personality | 1 | Nothing authored: the RC-car timing §6 warns against. |
| Environment | 1 | Clusters sink as mined (good). The baked ore pads lie after mining out. No dust, tracks, pour or apron wear. |
| Detail | 0 | The truck is 260 tris and the refinery about 600 against budgets of 3–7k and 10–18k. Zooming in reveals only flat boxes. |
| Cohesion | 1 | Bay width suits the truck (0.9 vs 0.62), but there's no shared connector, and the CC drop-off has no hardware. |
| Delight | 0 | None. |
| Restraint | 2 | Clean 70/30 surfaces with no visual oatmeal. The one lapse is the always-on amber strip on the refinery and CC (decorative glow). |
| Iconic quality | 1 | The refinery's twin team-capped silos are close to iconic. The truck isn't. |
| **Total** | **13 / 42** | |

**Lowest scores:** Detail and Delight (0). **The item that most hurts the game is Interaction (1).** The dock is the most repeated moment in every match, and right now it can show a truck dumping ore away from the building it is feeding.

---

## 9. Recommendations (ranked by impact over cost)

**R1. Load shows its true ore. Cost S.**
- **What:** `bin_ore` on `mining_truck`.
- **When:** whenever `Entity.CargoType` changes.
- **Timing:** immediate; it is a material swap.
- **Mechanism:** rename the `bin_ore` material to `M_OreTint` in the .glb (art pack re-export), or extend `Models.TintOre` to match `M_Ore_*`. Then call it from the harvester branch of `WorldView.UpdateView` when the type changes. Crystal and uranium get glow through the existing `oreType >= 2` path.
- **Impact:** at gameplay distance, a glowing cyan or acid hopper tells spectators and agents what the economy is hauling. At hero distance the load matches the field it came from.

**R2. The dock prepares before the truck arrives. Cost S** (door and lamp-node driver), **M** with the chute.
- **What:**
  - Door: `door`.
  - Lamps: the amber strip `stage_1__M_E_Amber` becomes its own `dock_lamps` node.
  - Chute: a new `chute` node in the bay, a kraft slide 0.5 long.
- **When:** the truck has `Order == ReturnOre` and is within 2.5 tiles of `World.DockPoint(nearest drop-off)`. Add a read-only sim helper `DropOffFor(e)` so the view uses the same choice as `UpdateHarvester`.
- **Timing:**
  - Door rolls up over 0.35 s and the chute extends over 0.5 s, both starting about 1.4 s before arrival.
  - Lamps chase at 2 Hz until the bed is down, then show steady cream for 0.6 s, then go off.
  - Door closes 0.5 s after the truck is 1.5 tiles clear.
  - Replace the current `OpenDoorNear` call on the first unload tick.
- **Mechanism:** `PezMotion` (door exists; add `chute` slide and lamp emission), plus a model change (split nodes) via the art pack tools.
- **Impact:** at gameplay distance the bloomed amber chase is the stream-readable "a delivery is happening". At hero distance the building visibly expects the truck. This also fixes "glow means state" for the strip, which is decorative today.

**R3. Pivot, pour, slam: the delivery becomes a performance. Cost M.**
- **What:** the truck's `rig.Root` yaw and a position offset of ≤0.35 tiles toward the bay; `bin` tip curve retimed (45°: 0.35 s up, 0.35 s hold, 0.25 s slam); a new `Fx.OrePour(from, to, oreColor, n)` built on the `Chunks` and `S.Dust` systems; `Body` offset −0.02 × load.
- **When:**
  - Pivot: `ReturnOre && !Moving && dist ≤ 0.6`.
  - Pour: starts at the first `Cargo` decrease.
  - Slam: when `Cargo == 0`.
- **Timing:** exactly the GREAT table above. It scales with ceil(cargo/10) × 0.1 s and skips the pivot under 0.6 s. Everything resolves within 0.4 s of the sim's departure.
- **Mechanism:** view code in `WorldView` and `PezMotion`; particles in `Fx`.
- **Handling a second truck:** a second truck docked about 1 tile to the side gets the same pour into a floor grate beside the bay mouth (see R7) instead of the chute.
- **Impact:** this is the §5 GREAT sequence. At gameplay distance, an ore-coloured burst at the bay. At hero distance, the whole beat sheet.

**R4. The refinery's work is visible. Cost S.**
- **What:** make the foil roll in `stage_2__M_Foil` a `spinner` node (axis X). Set `PezMotionProfiles["mining_refinery"]` to idle 0 and active 24 rpm, with a 1.0 s spin-up.
- **When:** `e.Working`, which is already passed through `SetWorking(Producing(e))`. Halve the rpm when `team.LowPower`; this needs a small `PezMotion.SetRate`.
- **Mechanism:** model change (art pack re-export) plus a profile edit.
- **Impact:** at gameplay distance a turning roll means "processing". A stopped roll means "starved", which tells a viewer the economy has stalled. At hero distance the press is alive.

**R5. Ore stains tell the truth. Cost M.**
- **What:** the ore-pad weight from `TerrainView.OrePads`.
- **When:** re-evaluate each field every 2 s, only when its total has changed.
- **Timing:** the pad alpha fades from 0.34 to a 0.08 "spent ground" scar over 3 s once the field is empty.
- **Mechanism:** move the pads from the baked ground vertex colours to one decal mesh per field with a per-field alpha. Or rewrite only the affected ground vertex colours.
- **Impact:** at gameplay distance, empty fields stop reading as ore. At hero distance the world remembers where mining happened.

**R6. "Nothing to mine" posture. Cost S.**
- **What:** on `mining_truck`, `bin` raised to 12° (open, "empty and waiting") and `spinner` lowered 0.04.
- **When:** `IsHarvester && Order == Idle`, which is set after `WarnSurfaceExhausted`.
- **Timing:** ease into the posture over 0.6 s.
- **Mechanism:** `PezMotion`, plus a parked-bin target in the bin code.
- **Impact:** at gameplay distance a cluster of open-hoppered trucks says "go deep". At hero distance they look idle rather than dead.

**R7. Command center gets a real intake. Cost M, needs new art.**
- **What:** a kraft grate with an amber lamp pair on the CC plinth, south-east of its door, as a new `intake` node with a flip-up `intake_lid`. The refinery gets a matching grate beside its bay mouth for the second truck.
- **When:** same trigger as R2.
- **Timing:** the lid flips up over 0.3 s on approach and down 0.4 s after the truck leaves.
- **Mechanism:** new art plus `PezMotion`. Also stop `OpenDoorNear` from rolling up the CC's vehicle door for ore.
- **Impact:** the CC takes about half of early-game deliveries, and it stops swallowing ore through a garage door.

**R8. Harvest reads as cutting. Cost S** (Fx), **M** (drum model).
- **What:**
  - Cutter dust: `S.Dust` at the `spinner` world position, one puff every 0.4 s, biscuit tinted 30% toward the ore colour.
  - Chips: one brick thrown from the drum into the hopper every 0.3 s, scaled 0.4.
  - Drum: licorice and hazard teeth bands on the drum (≤3% of the surface), so rotation is visible.
  - Coast-down: 1.5 s, per the spec.
- **When:** `Order == Harvest && !Moving && HarvestTile != null`.
- **Mechanism:** `Fx` particles; model change for the drum.
- **Impact:** at gameplay distance a working truck has a dust plume. At hero distance the drum visibly bites.

**R9. Truck model rework. Cost L, needs new art.** The concept turnaround is already priority 2 in `BRIEF_v2.md`.
- Keep `spinner`, `bin` and `bin_ore`, the 1.0-tile length and the pivot at ground centre.
- **Hopper:** open-topped, 0.55 wide × 0.6 long × 0.28 deep, inner faces darker, team rim on the lip. It should be about twice the cab's footprint.
- **Cab:** small, 0.2 × 0.2, offset to the left (asymmetry, like a real haul truck), with a team roof.
- **Tracks:** licorice track pods 0.14 wide that stand proud of the body, with tread blocks.
- **Cutter:** a full-width toothed drum on short arms.
- **Lights:** two small rear amber brake lamps on a `brake_lamps` node, lit for 0.4 s on each stop.
- **Wear:** ore-coloured grime on the lower hopper and the cutter arms only.
- **Budget:** 3–5k tris.
- **Impact:** at gameplay distance, a silhouette that says "hauler" when blacked out. At hero distance, it fixes Detail and Personality.

**Delight (in R3): the stubborn last brick. Cost S.** At the bed slam (Cargo 0), spawn one `Fx` brick at the bin lip in the cargo colour. It bounces twice (restitution 0.35), rolls to rest and fades after 3 s.

---

## 10. Spec vs implementation gaps

| MOTION.md says | Implementation does |
|---|---|
| Truck reverses into the dock along +Z | Stops wherever it is within 0.6 tiles, at whatever heading; never bin-in. The `Plinths.cs` header on the render branch also says "a truck reversing into the refinery dock". It doesn't. |
| Dock door rolls up when the truck reverses in | Rolls up on the first unload tick, after ore starts moving |
| Dock lights blink alternately at 1 Hz while docking | Amber strip is static and always on |
| Foil roll counter-rotates and conveyor scrolls while processing | No node; nothing moves |
| Harvest: 1 s ticks, creep 0.15 tiles/s | 0.1 s ticks of 4 ore each, full in about 3.8 s, no creep. Keep the sim; drop creep from the spec, since it would exceed the 1 s offset rule. |
| Spinner coasts down over 1.5 s; idle shiver 0.005 at 12 Hz | Stops in 0.4 s; no shiver |
| Truck max 1.6 tiles/s, built at the factory | Sim: 1.8 tiles/s, built at the command center |
| CC door opens only during the deploy intro | Also opens for every ore unload at the CC |

## 11. Readability and sim-sync flags

1. **Readability lies today:**
   - Mined-out ore pads still show as fields (R5).
   - Non-iron loads display as iron (R1).
   - Idle trucks look like working ones (R6).
   - The bin can tip away from the refinery it is feeding (R3).
2. **No current sim-sync violation.** Today's dock is in sync, just badly staged.
3. **Rules for the proposed choreography:**
   - The pivot is a rotation-only view override for at most 1.5 s.
   - The bay offset is ≤0.35 tiles, eased out within 0.4 s of departure.
   - All refinery anticipation runs before arrival.
   - Nothing waits on the view.
4. **Don't implement the spec's "reverse into the dock" in the sim.** It needs an approach point and a reverse leg, which adds about 0.5 s or more per trip and changes the economy rate. That is a gameplay change for the rules owner (rules_version), not an art change. R3's pivot gets the same read without it.
5. **Two trucks can dock at once**, about 1 tile apart. Any single-bay choreography has to give the second truck somewhere believable to pour (R7's grate).
