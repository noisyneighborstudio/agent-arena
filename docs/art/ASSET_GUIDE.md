# Making a Pezz asset: the guide to art and motion

This is how to design, build, animate, verify and ship any Pezz asset — a building, a unit, an effect, a moment. Follow
it for new assets, refinements and fixes alike. It's distilled from refining the whole building roster and the infantry
line with the owner, model by model, so most rules below exist because their absence once shipped something wrong.

**Where it sits.** `ART_DIRECTION.md` is the quality bar (what great means; the art-director agent scores against it).
`BRIEF_v2.md` is the setting, palette and readability rules. This guide is the practice: how the bar is met in this
codebase, what the owner expects, and the exact pipeline. Where an older doc disagrees with this one about process
(`HANDOFF.md`, `ASSETS.md`, `ASSET_BRIEF.md`, the art pack's original `MOTION.md` wording), this one wins.
`MOTION.md` / `motion.json` hold each asset's spec (generated from `art-src/tools/motion.py`); `KITCHEN_SINK.md`
describes the showcase room every asset is checked in.

---

## 1. The house style

Pezz is a world of toys engineered like real machines, in the language of a candy dispenser. Every shipped building
now speaks it; match them before inventing anything.

- **A chunky cream body with rounded edges, on its pad.** One readable silhouette at game zoom (about 40 px per tile,
  through a compressed stream, from a high camera at yaw 45°, pitch 55°). Not a row of separate gadgets on a slab.
- **Team colour as bands and a flip-top head.** The `team` material covers 15–25% of the surface: heads, caps, roofs,
  wrapper bands, grip slats. A dispenser-style head hinged at its back edge is the family signature (the command
  center's head, the barracks head, the factory's head house, the stems on the electronics plant, enrichment plant,
  outpost and deep mine).
- **Licorice and dark accents; smoke-plastic stems; foil, spring steel and kraft.** Materials come from `MATS` in
  `build_models.py` (`team cream smoke steel foil kraft licorice dark pad bone grape glass clear cyan magenta acid amber
  cinnamon copper mint crystal uranium oretint`). Add a material only when none of these says it.
- **Candy as the product.** What a building makes is visible as candy: bricks stacking behind a window, lens candies in
  a stem, plasma bricks, pressed ore bricks, soldiers baking in a case.
- **Glow means state.** Emissive is never decoration and never the dominant colour. It says working, starved, charging,
  low power. Acid green (uranium, electronics) is an accent only: the first enrichment plant, wrapped in glowing green
  tubes, was rejected as "very out of place". Ore in a hopper is unlit (`oretint`); glow belongs to the field and to
  state lamps.
- **Rounded, never sharp.** `round_models.py` fillets every part. Rounding is the material, not the design: form comes
  from function.
- **Function creates form.** Every part does something the motion uses: the factory's gantry carries a head to the
  hull, the outpost's belt carries ore to the stem, the deep mine's sheave turns with the skip.
- **Landmarks keep their height.** If an asset's silhouette was its identity (the deep mine's old lattice tower), the
  new one keeps a comparable landmark, or the change is called out for the owner.
- **Busy is a risk on small pads.** A 2×2 with a blockhouse, belt, stem, tank and hopper reads as a cluster. Prefer one
  body with features over many bodies.

**Units** share one body: the candy trooper (§5.4). Soldiers are chunky Pez figures with a big cream head, licorice
eyes, a team helmet or headgear on top (where it reads at full zoom-out), and each unit's kit — rifle, tube, med pack,
spanner, scoped rifle, charges — doing the identifying at game zoom. Vehicles share the candy chassis (§5.7): a cream
hull on licorice tracks or tyres with a team flip-top head as its turret or cab cap. Aircraft get the same treatment in
a family (a shared base first, then the variants).

**Budgets after rounding:** 1×1 structure ~3–7k triangles, 2×2 ~5–12k, 3×3 ~10–18k; a soldier ≤ ~3.2k; a light
vehicle ~6–7k, a heavy one up to ~10k (the owner's figure for the candy chassis). Going over is allowed with a reason in
the hand-back.

---

## 2. The owner's rules

These come from the owner's reviews. They override convenience.

1. **Every model goes past the owner, with a GIF, before it ships.** Present it, wait for a yes, then ship. Several can
   be in review at once; each is approved on its own.
2. **Nothing clips, over the full range of motion.** Every moving part clears every other part at every frame — the
   optics lab's telescope swinging through its crystal stack was sent back. Check the extremes with a contact sheet or
   sample the moving part's bounds against its neighbours in a script, and say which you did.
3. **No two systems drive one transform.** Before animating a node, check `PezMotion` (generic `turret`, `barrel`,
   `spinner`, `door`, `lift` handling), `PezMotionProfiles.cs` and `WorldView.cs` for anything already moving it. If your
   Works class takes over a node, zero its profile speeds or rename the node.
4. **No snaps, no wrong size changes.** Watch loop boundaries, hand-offs and pivots. A unit that grows at a hand-off
   (the barracks stand-in at 1/1.35 scale) or pops for one frame (drawn at its sim position before its spawn ease)
   is a bug. Use `Models.DrawScale` for any stand-in of a real unit.
5. **Glass must be see-through in game.** The three.js preview lies about materials; check the in-game still. Use the
   `clear` material (alpha → BLEND); a figure behind glass needs contrast with what's behind it.
6. **The dispenser act is earned, not forced.** A head that pops and spits a figure was the barracks' nod; "no need to
   have a pez dispenser act here" was the airfield's note. A head pop is right when it tells that machine's story — a
   batch released (electronics plant), a reserve running out (deep mine), a lens dispensed (optics lab). Otherwise keep
   the Pez DNA in shapes, materials and proportions, and make the motion what the machine would really do.
7. **If something glows, it lights things.** Strong emissive parts cast light on their own body and the ground through
   `GlowCaster` (§5.6) — "Blue should cast light" — tuned to tint, not wash out.
8. **Show the act honestly.** If the sim can't support a readable act, don't fake it out of sync: propose the sim change
   (with a Rules entry) and let the owner decide. "Add the delay. It's real." (the commando's arming time).
9. **Every visible part means something in every state.** Unused parts go dark and closed: a dock whose trucks use a side
   hatch shutters its south bay and darkens its lamps.
10. **Fit the family.** Prove it with a side-by-side in game next to at least three shipped siblings.

---

## 3. The sim is authoritative; the view performs

The simulation is deterministic, 2D and authoritative. The view (`unity/Assets/Pez/View/`) performs what the sim says.

- **Choreography may anticipate, compress or overlap gameplay; it never delays or contradicts it.** Lead with what's
  coming (the airfield's lift goes down 1.5 s before the aircraft is due; the laser trooper's cell charges in the last
  0.25 s of its cooldown; the barracks' figure rises 1.5 s before it's trained). A unit is where the sim says it is, give
  or take a visual offset that resolves within a second.
- **Drive motion from sim state, never from timers that can drift.** Inputs: `Entity.Working`, `IsComplete`, `Hp`,
  build progress, queue progress (`QueueHead`-style lookups of the producer's queue), cooldowns, `World.Teams[t].LowPower`
  and power load, dock steps (`DockStep`), carried cargo, `LastFire`, events (`trained`, `shot`, `hit`, `captured`,
  `unloaded`, `destroyed`…).
- **Read the sim before believing a spec.** Specs were often wrong: no charge on the laser trooper, no kneel on the
  medic, no pause before the rocket soldier fires, the medic's heal range 1.5 not 0.8. Fix `motion.py` to say what the
  code does.
- **Small, bounded view delays are acceptable when they make an act read** (the engineer's captured building takes his
  colours on the third spanner blow, about a second after the sim's instant capture). Say so in the hand-back; the
  alternative is a sim change.
- **Fog and audiences.** Live state must not leak to players who can't see it. Use `SeesState(e, PovTeam)` for state
  you animate, wrap effect emission in `Fx.Audience = AudienceFor(e); … Fx.Audience = null;`, and show a building
  remembered under fog as neutral and still.
- **Sim changes are rules changes.** They need a numbered entry in `mcp/changes.js`, a bump of `RulesVersion` in
  `unity/Assets/Pez/Sim/StateView.cs`, a commit titled `Rules N: …`, snapshot fields for any new state (and the
  snapshot test's field list), and a headless test. Rules numbers collide when several branches add one: re-check
  `origin/main` right before finishing and take the next free number.

---

## 4. Designing the asset

Before modelling, write down:

1. **Role** in one line, from `Defs.cs` and the sim: what it does, its timings (build, cooldown, unload, speed), the
   events the view receives.
2. **States** it has in play: building, idle, working, starved, low power, offline (upkeep), damaged/burning, under
   fog, destroyed — plus unit states (moving, engaging, kneeling, carried, unloading, emerging).
3. **The story beats** of its main act: arrives → prepares → acts → reacts → completes → disengages
   (`ART_DIRECTION.md` §5). Each beat names the node that moves and the sim state that triggers it.
4. **The hero idea**: the one Pez-machine metaphor (a headframe skip, a press fusing blanks, a centrifuge hall, a
   revolver drum, a candy rocket).
5. **The siblings** it must sit beside, and the shared base it should build on (a family's base first, see §8).

Look at the shipped siblings' builders and Works classes first; copy their idioms.

---

## 5. Building it

### 5.1 The model pipeline

All commands from the repo root. Python deps: a venv with `art-src/tools/kit/requirements.txt`
(`python3 -m venv /tmp/pezart-venv && /tmp/pezart-venv/bin/pip install -r art-src/tools/kit/requirements.txt`).
Previews and icons need three.js once: `(cd art-src/tools/kit && npm ci)`.

```
python art-src/tools/kit/one.py <key> /tmp/pezart-<key>/raw            # build one model, unrounded; prints tris, nodes, bounds
python art-src/tools/round_models.py --in /tmp/pezart-<key>/raw/<kind> --out /tmp/pezart-<key>/round --only <key>
art-src/tools/kit/preview.sh /tmp/pezart-<key>/round/<key>.glb /tmp/pezart-<key>/p.png "cy=0.8&zoom=2.3&yaw=45"
cp /tmp/pezart-<key>/round/<key>.glb unity/Assets/Pez/Resources/PezModels/<key>.glb
python art-src/tools/kit/glbinfo.py unity/Assets/Pez/Resources/PezModels/<key>.glb   # tris, nodes, materials
art-src/tools/kit/icon.sh <key> [structures|units]                                   # re-render the build icon
```

`<kind>` is `structures`, `units` or `ores`. Render the shipped model first as your "before". Look at every render
yourself and iterate on form before touching Unity.

### 5.2 `build_models.py` conventions

- **Builders:** `S_<key>()` for structures, `V_<key>()` for units, `O_ore(key)` for ores; register in `BUILDERS`.
  Build with the `Asset` API: `box cyl cone sphere torus rev strut coil sheet tube hoop octa`, and
  `a.node(name, parent, pos, rot=, scale=)` for named nodes. Materials: `mat(name, hex, metal, rough, emissive, alpha)`;
  `alpha < 255` makes a blended (see-through) material.
- **Stages:** `export()` assigns loose parts to `stage_0..3` by height so the building rises out of its pad. A moving
  node must sit under a stage node (`a.node('stage_2', key)` first, then `a.node('head', 'stage_2', …)`), or it floats
  while the building rises. Empty marker nodes (`pop`, `land`, `exit`, `muzzle`, `press_feed`) are fine and are how the
  view finds positions.
- **Node names are a contract.** Generic names are driven by `PezMotion`: `turret`, `barrel`, `spinner`, `bin`,
  `door`, `lift`. Use them only when you want that behaviour; otherwise prefix your own (`dm_skip`, `dock_lens_0`).
  Grep a node name in `unity/Assets/Pez/View` before renaming or reusing it. Known hidden dependencies: drop-off
  buildings' `stage_1__M_E_Amber` is the dock lamp pair (DockView); `power_plant`'s stage_3 dark/cyan parts feed
  PowerCores; `door` rolls up for units.
- **Helpers get distinctive names.** A new helper is `<asset>_<thing>` or clearly unique (`scoop_bay_hatch`,
  `laser_trooper_pack`); two agents once added different `tube()` helpers. Don't change a shared helper's behaviour;
  add an optional keyword whose default leaves every existing model byte-identical, and prove it (rebuild the others and
  compare).
- **glTF x is mirrored on import.** Rotations about x are unchanged. Derive directions in Unity from node positions,
  not hard-coded signs.
- **Build meshes by hand** when trimesh's sweep/boolean helpers need an engine we don't ship (see `coil`, `sheet`,
  `hoop`). Don't add dependencies.

### 5.3 Motion: one Works class per asset

- Motion lives in `unity/Assets/Pez/View/<Name>Works.cs`: a static `For(model, …)` that finds nodes with
  `PezMotion.FindDeep` and caches rest poses, and a `Tick(…)` per frame from sim state. It owns its transforms and
  renderers outright. Patterns to copy: `RefineryWorks`, `PowerWorks`, `HqWorks`, `BarracksWorks`, `FactoryWorks`,
  `DeepMineWorks`, `OutpostWorks`.
- **The WorldView hook is one EV field line and one call line**, next to the others, so parallel work merges:
  `public FooWorks Foo;   // what it drives` and
  `if (e.Def.Key == "foo" && e.IsComplete && e.Team >= 0) { Fx.Audience = AudienceFor(e); (v.Foo ??= FooWorks.For(rig.Model, e.Id)).Tick(…); Fx.Audience = null; }`.
  Key on `e.Def.ModelKey` when other defs borrow the model (derricks use the deep mine's).
- **Lamps and glows:** `PezShade.Set(renderer, dim, glow)`. If a building's generic power glow also writes your
  renderer, set it every frame.
- **Effects** are in `Fx.cs` (`Steam`, `StackSmoke`, `Arc`, `Beam`, `Tracer`, `MuzzleFlash`, `Backblast`, `SpadeDust`,
  `StageDust`, `OreBrick`, `PourDust`, `SpentWrapper`, `ScopeGlint`, `Healed`, `GroundRing`, `Poof`…). Add a new one
  there with a clear name rather than building particles in a Works class.
- **Unit spawns:** the `trained` event sets the new unit's `SpawnFrom` (where the producer releases it) and the unit
  eases from there to its sim position over 0.9 s; set `Rig.Root.position` the same frame so it never flashes at its
  sim position. A producer with its own release point (the barracks' `Exit`, the airfield's lift deck) supplies it.

### 5.4 Infantry: the candy trooper

- Every soldier is `candy_trooper(a, pose=…, …)` in `build_models.py` plus its own kit functions; read the API comment
  above it. Nodes: `hips`, `leg_l/leg_r` (+ `knee_l/knee_r` with `knees=True`) — **Gait owns these** —
  `turret` (waist, aimed by PezMotion), `head`, `hat` (headgear mount), `pack` (back mount), `arms` (raised to fire),
  `barrel` (weapon mount, recoils along −Z) with an empty `muzzle` at the tip (shots and tracers leave from it).
- `SoldierWorks` is the one class for soldier behaviour, with a small per-unit branch keyed on kit nodes. Kneeling is
  `Gait.Kneel` with a per-unit `Gait.KneelPose`; SoldierWorks only says when.
- A soldier must also work as a barracks case figurine (built from the unit being trained, baked dough → finished), in
  the spit-out tuck-and-roll, and boarding/unloading transports.
- Icons: soldiers and candy-chassis vehicles turn three-quarters to the camera, front on (render.html); pass
  `ICONQ=turn=<rad>` to `icon.sh` to override.

### 5.5 Docks and drop-offs

Trucks dock at a bay that's south unless blocked (`World.Bay`; AI placement keeps it south, Rules 41). `DockView` runs
the lamps, chute and door; it follows a bay that changes side (`Rebay`). A refinery side bay opens a hatch with its own
lamp, and the south bay shutters and goes dark. A new drop-off must handle every side, or say what it shows on one.

### 5.6 Light from glowing parts: `GlowCaster`

`GlowCasters.cs` gives steady light from a building's own glow: a ground pool for every source (one draw call for
all), plus a small budget of real point lights (3) handed to the brightest sources near the middle of the view, fading
between holders. Adopt it in two lines: `caster = GlowCaster.For(model, localPos, color, light:, range:, pool:, …)` once,
then `caster.Set(glow, Fx.Audience)` every frame with the same glow value that drives the emissive. Keep peaks so the
pad is tinted, not washed out (the laser tower's shot was cut by a third).

### 5.7 Vehicles: the candy chassis

- Every ground vehicle is `candy_chassis(a, …)` in `build_models.py` plus a team head (`chassis_head`), a weapon
  (`chassis_cannon`, `chassis_mg`) and its own kit on the hull's slots (`slot_bed`, `slot_crane`, `slot_dish`, `slot_pod`,
  `slot_stack`, `slot_lights`). Read the API comment above it; the light tank (tracks) and the scout buggy (wheels) are the
  references. Its nodes (`gear`, `hull`, `tread_*`, `sprocket_*`, `idler_*`, `wheel_*`, `steer_*`, `turret`, `lid`,
  `barrel`, `muzzle`, `exhaust`) are a contract with `VehicleWorks`.
- `VehicleWorks` is the one class for running gear and hull feel (treads, wheels, steering, the sprung hull's pitch, roll
  and bounce, slope, dust, exhaust), with a small per-vehicle branch for a special act; PezMotion keeps the turret and
  barrel. Don't animate those nodes anywhere else.
- **The owner's decisions (2026-10):**
  - Wheeled vehicles keep pivoting on the spot, as the sim turns every vehicle (286°/s, Rules 42), shown as a skid-steer
    with the front wheels at full lock. No turning circle and no Rules change.
  - The tread speed stays capped at 1.2 tiles/s (0.4 of a cleat per 1/30 s), so a 30 fps stream never sees the treads
    strobe backwards, even though a fast tank drives faster than that.
  - Triangle budget: about 6–7k for a light vehicle, up to about 10k for a heavy one.
- Prove clearance with a sampled sweep: turret yaw all round, full recoil, the hull's pitch, roll and heave extremes,
  steering lock, and the treads slid a full cleat (the light tank's and buggy's sweep found 0 clips).

---

## 6. Motion docs: `motion.py` is the truth

Each asset's entry in `art-src/tools/motion.py` (`S(...)` structures, `U(...)` units) describes what the code actually
does: mechanics in plain sentences with real numbers, each named node and its behaviour (node values may be a dict of
`key=value` facts or a plain sentence), and idle. Update it, then splice only your keys into the shipped docs:

```
python art-src/tools/kit/motion_splice.py <key> [key ...]           # writes MOTION.md, motion.json, PezMotionProfiles.cs
python art-src/tools/kit/motion_splice.py --check <key> [key ...]   # ok / DIFF per file, changes nothing
```

Never regenerate the shipped files whole, and never hand-edit `MOTION.md` without putting the same words in `motion.py`
(hand edits get erased by the next splice). `--check --all` must be all ok before you ship: CI (`arena/ci.sh`) runs it
and won't deploy a commit that fails it.

---

## 7. The kitchen sink and verification

The kitchen sink (`docs/art/KITCHEN_SINK.md`, built in `unity/Assets/Pez/Sim/KitchenSink.cs`) is where every asset is
seen in every state. Your asset isn't done until its states are exhibited there and you've watched them.

### 7.1 Exhibits

- Show every state that matters: working and starved on a loop (a `holds` lambda that empties a stock or clears
  `Working` part of each cycle), low power (the Lime row), damage (Fires), the unit's act in the Combat range or its own
  lane, a deploy loop, a capture loop.
- **Use a genuinely free spot.** Read the district's existing `Bldg`/`Unit` calls; buildings are centred on screen
  points and `Room` rejects overlaps; widen a district's `Begin(...)` box if needed without running into the next
  district. Exhibits from parallel work collided twice — check again after rebasing.
- Watch the power budget of the team you add to (the headless test prints it; Lemon must stay powered).
- Update the district's line in `KITCHEN_SINK.md` and the exhibit's `Item(...)` text.
- `dotnet build headless/Pez.Headless.csproj -o /tmp/pezart-<key>/hl` then `dotnet /tmp/pezart-<key>/hl/pez-headless.dll
  --test-kitchen-sink` (and `--test` for any sim change) must pass. Add a check for a new behaviour when it can regress.

### 7.2 Building and running Unity

```
cp -c -R <a worktree that has it>/unity/Library unity/Library      # once per worktree (APFS clone, instant)
/Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath "$PWD/unity" \
  -executeMethod Pez.EditorTools.PezSetup.Build -pezOut /tmp/pezart-<key>/build/Pezz.app -logFile /tmp/pezart-<key>/build.log -quit
grep -n "error CS" /tmp/pezart-<key>/build.log
PID=$(art-src/tools/kit/kitchen.sh /tmp/pezart-<key>/build/Pezz.app <port> /tmp/pezart-<key>)
curl -s http://127.0.0.1:<port>/api/admin/showcase          # districts → items with map x, y
curl -s -X POST http://127.0.0.1:<port>/api/admin/camera -d '{"x":..,"y":..,"distance":3,"yaw":45}'
curl -s -X POST "http://127.0.0.1:<port>/api/admin/screenshot?path=/abs/out.png" -d '{}'
PEZPORT=<port> python art-src/tools/kit/clip.py /tmp/pezart-<key>/<key>.gif <x> <y> 3 <frames> <cropW> <cropH> 45 0.05
python art-src/tools/kit/sheet.py /tmp/pezart-<key>/sheet.png /tmp/pezart-<key>/<key>_frames --cols 6 --crop 900x600
kill $PID
```

- Each app's frame server takes port+1: parallel workers use ports 10 apart, and only their own.
- Stop only your own app. Never `pkill`; never touch `pez-headless` processes (live CI rooms) or anyone else's app.
- Unity regenerates a stray untracked `unity/Assets/Pez/Sim/Observations.cs.meta`; delete it, never commit it. Unity
  may also touch icon `.png.meta` files; `git checkout` any you didn't mean to change.
- With many apps running the frame rate drops and GIFs get choppy; contact sheets show motion better than a slow GIF.

### 7.3 The review pack

Every asset goes to the owner with:

- **Before and after** preview renders (same angle).
- **In game at game zoom**, beside shipped siblings (§2.10), and for units beside a rifleman, a light tank and the
  barracks.
- **A contact sheet** of the full cycle, frame by frame, plus each state (starved, low power, offline…).
- **A GIF** of it in action (under ~12 MB).
- **The old and new icon.**
- **Clearance**: how you proved nothing clips.
- **Notes for the owner**: anything over budget, any spec that was wrong, any sim change proposed, anything you weren't
  sure of. Lead with what the owner must decide.

Look at every image yourself before presenting it, and say what you see — including flaws.

---

## 8. Working in parallel

Most assets are refined by background agents in their own worktrees, coordinated by one session that reviews and
ships. What keeps that from breaking:

- **Start from current main** (`git fetch origin && git reset --hard origin/main` in a fresh worktree) and rebase before
  handing back — main moves while you work. Set work aside with a temporary WIP commit, never a bare `git stash` (the
  stash is shared across worktrees).
- **Families go base-first.** When several assets share structure (soldiers, a vehicle chassis), one agent builds the
  shared base plus a reference asset; after the owner approves it, the rest fan out on that base. Seven agents editing
  one shared function at once drift apart.
- **Expect overlaps at the merge**: the `WorldView` EV field and call lines, `motion.py`, `MOTION.md`/`motion.json`,
  `PezMotionProfiles.cs`, `KitchenSink.cs`/`KITCHEN_SINK.md`, `build_models.py` helpers and `BUILDERS`, `Fx.cs`,
  `Gait.cs`, `SoldierWorks.cs`, `mcp/changes.js` and `RulesVersion`. When parallel agents each invent the same
  mechanism (three kneels, two knee options), unify them in one integration pass before shipping, and re-verify every
  affected asset together.
- **Agents never commit or push.** They hand back uncommitted work; the coordinator ships.
- **Someone else's uncommitted work in a checkout isn't yours.** Don't stash, reset or ship it; ship from a clean
  worktree instead and ask.
- Scratch goes in `/tmp/pezart-<key>/`; never write in another worker's.

---

## 9. Shipping

After the owner approves:

1. In a worktree on current `origin/main`, with only this change: `motion_splice.py --check --all` all ok, Unity build clean,
   headless build and `--test-kitchen-sink` (and `--test` for sim changes) passing, and a last look at the kitchen sink
   for exceptions in `~/Library/Logs/Pezz/Pezz/Player.log` and overlapping exhibits.
2. Commit with a plain-language subject saying what the asset now is or does ("The factory is a Pez dispenser on its
   side: …"); `Rules N: …` for sim changes. End with the co-author line your session uses.
3. `git fetch`, rebase if main moved (rebuild if anything you touch moved), `git push origin HEAD:main`. CI deploys.

---

## 10. Definition of done

- [ ] Role, states, story beats and hero idea written down; siblings studied.
- [ ] Model built, rounded, installed; triangles within budget (or the reason stated); nodes named per §5.2.
- [ ] Motion in its own Works class (or SoldierWorks), driven by sim state; hook is one field and one call line.
- [ ] No clipping over the full range, proven; no two systems on one transform; no snaps or size jumps.
- [ ] Glass reads in game; glowing parts cast light where they're strong; unused parts dark and closed.
- [ ] Fog: no live state leaks; effects go to the right audience.
- [ ] Every state exhibited in the kitchen sink, in a free spot; `KITCHEN_SINK.md` updated.
- [ ] `motion.py` describes what the code does; spliced; `--check` all ok.
- [ ] Icon re-rendered.
- [ ] Unity and headless builds clean; kitchen-sink test (and `--test` for sim changes) passing.
- [ ] Review pack shown to the owner and approved.
- [ ] Shipped from a clean worktree on current main.

---

## 11. Lessons from the reviews

| What happened | The rule it became |
|---|---|
| Enrichment plant v1: four glowing acid-green tubes and a crate on a slab — "very out of place" | Match the family; one body; acid is an accent (§1) |
| Optics lab's telescope swung through the crystal stack | Clear the full range of motion (§2.2) |
| Barracks case read as opaque; the soldier shrank and grew on the slide | See-through in game; no wrong size changes (§2.4, §2.5) |
| Airfield had odd motion and a forced dispenser act | One system per transform; the act must fit the machine (§2.3, §2.6) |
| Outpost: a cluster of parts on a 2×2, glowing ore bricks | Busy is a risk; ore is unlit (§1) |
| Deep mine lost the old tower's landmark height | Keep landmarks or call it out (§1) |
| Refinery chute slid out of a solid side wall | Trace it to the sim: AI placement put a third of bays on a side; fixed placement (Rules 41), hatches for the rest (§5.5) |
| South dock kept signalling while trucks used a side hatch | Unused parts go dark and closed (§2.9) |
| Laser tower's blue coils lit nothing | Glow casts light (§2.7, §5.6) |
| Commando's first charge could never be shown planted | Show the act honestly; propose the sim change (§2.8) |
| Specs claimed charges, kneels and pauses the sim never had | Read the sim; fix the spec (§3) |
| Parallel agents added two `tube()` helpers, three kneels, two knee options, colliding exhibits and Rules numbers | Distinct names, base-first families, an integration pass, re-check after rebase (§8) |
| Hand-edited `MOTION.md` entries diverged from `motion.py` | `motion.py` is the truth; splice with the tool (§6) |
