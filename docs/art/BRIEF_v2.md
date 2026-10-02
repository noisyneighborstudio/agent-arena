# Pez asset design brief, v2 (The Dispenser War)

This replaces the original brief. It folds in every decision made since then: the candy-dispenser setting, the readability fixes, the off-axis orthographic camera, the "nothing pops" rule, the motion spec and the production budgets. Paste it into Claude Design (or hand it to an artist) as-is.

---

## Prompt

You are the art director and lead asset designer for **Pez**, a high-resolution real-time strategy game built in Unity, in the tradition of Command & Conquer (Tiberian Sun / Red Alert 2 / C&C 3). Two to four armies fight over a resource map. Mining trucks harvest four kinds of ore, factories refine it, and players build bases, tanks, aircraft and laser weapons. AI models usually play the matches against each other while spectators watch, so **readability from the game camera matters more than close-up detail**.

### Setting: The Dispenser War

Four flavours go to war over sugar. Every army is built from molded plastic, spring steel and foil: toys engineered like real machines. The design is generic candy-dispenser DNA. Use no real-brand logos, character heads or exact product trade dress.

- **Shape language: everything dispenses.**
  - **Stem:** the body. Hulls and towers are stems, and a window shows the magazine.
  - **Head:** a hinged flip-top in team colour. Heads are turrets, cranes and hatches.
  - **Spring:** a visible coil means power or reload. A compressed coil means loaded.
  - **Brick:** a rounded candy brick. Bricks are ammo, ore and payload.
- **The world is unflavoured.** Terrain and props stay muted: biscuit plains, licorice cliffs, cola water, dusty cotton-candy trees. Saturation belongs to teams and glow only.

### Palette

| Role | Colours |
|---|---|
| Teams (flavours) | Blueberry `#2E73FF`, Cherry `#F22E1F`, Lime `#33D94D`, Lemon `#FFD126` |
| Neutral materials | Cream plastic `#ECE4D2` (shells), smoke plastic `#4A4F57` (hulls), spring steel `#8E979F`, foil `#C8CDD3`, kraft `#A9845A`, licorice `#1E1B1D` (tracks, tyres), sugar pad `#B9AE98` |
| Glow (same for every team) | Rock-candy cyan `#3FE6FF`: lasers, optics, crystal, power cores. Taffy magenta `#FF3EC8`: plasma and fusion only. Sour acid `#B6FF3B`: uranium and electronics, always glowing so it never reads as Lime. Caramel amber `#FFA22E`: industry, docking, beacons |
| Hazard | Black and cream stripe, at most 3% of a model and never on roofs (so it isn't mistaken for Lemon) |
| Ores | iron_ore: cinnamon bricks `#8A3A24`. copper_ore: orange bricks `#C8742F` with mint patina `#6FC2A6`. crystal: rock-candy shards `#8FE4FF`. uranium: sour rods `#B6FF3B` |
| Terrain | Biscuit ground `#7D6E55`, cliff top `#4A3F40` and face `#241E20`, cola water `#3A2218`, shore `#A08A68`, trees `#8A7385` |

### Readability rules

1. **Silhouette first.** Every asset must be recognisable from its outline alone. Weapons and working parts are exaggerated +20%, and infantry weapons and heads +40%.
2. **70/30 surface.** 70% clean rest areas. 30% detail, made up of the team mask (15–25%; infantry and aircraft 35%), glow (≤5%) and grilles.
3. **Top is brightest.** The highest contrast and brightest values sit on top-facing surfaces. Undersides get minimal detail.
4. **Team colour is matte-gloss plastic, never emissive.** It goes on heads, caps, roofs and wrapper bands, through one mask channel tinted at runtime. Nothing else uses the four team hues at full saturation.
5. **Glow means function and shows state:** charging, load, low power, cooldown. It is never decoration.
6. **Status never uses team hues.**
   - Critical alerts use the hazard stripe and high alerts use amber.
   - Health bars go cream, then amber below 50%, then hazard stripe below 25%.
   - Selection rings are cream for every team.
   - The medic's mark is a team-coloured plus on a white pack, not a red cross. The radar beacon is amber.
7. **Laser vs plasma vs conventional.**
   - Laser: thin barrel, cyan coils, prism tip.
   - Plasma: stubby emitter with a magenta ring and core.
   - Conventional: no glow, thick steel barrel.
   - laser_tank uses cyan, not magenta. Magenta is reserved for plasma.
8. **Lighting.** Warm sun from the upper left of the screen, cool sky fill. Bake AO and a soft top-down gradient (+10% at the top, −15% at the base) into albedo. Never paint directional or cast shadows.

### Camera

- **Projection:** off-axis orthographic, yaw 45°, pitch 55°. Zoom changes the ortho height, from 8 (close) to 40 (strategic).
- **Faces:** two faces of every building are visible (south and east), so detail both.
- **Readability targets:** one tile is about 40 px at the default zoom and about 14 px fully zoomed out. At full zoom-out only the team-coloured head should still read, which is why it sits on top.
- **Minimap:** north-up, with sectors A–H and 1–8 for LLM grid orders.

### Technical contract (Unity, built-in render pipeline)

- **Scale and orientation:** 1 tile = 1 unit = 1 m. +Y is up and +Z is forward. The pivot sits at ground level, at the centre of the footprint. Structures fit their footprint with a 0.04 margin on every side.
- **Doors, docks and exits face −Z.**
- **Named nodes** (the game animates these; the geometry under each is named `<node>__<material>`):
  - `turret`: yaws about Y.
  - `barrel`: child of `turret`. Recoils along its own −Z, so a pitched barrel recoils along its axis.
  - `spinner`: cutter, crane, rotor, prism, core or dish.
  - `bin` and `bin_ore`: the truck hopper tips about its rear edge; the ore inside scales Y with the load.
  - `door`: roll-up door with its pivot at the top edge; open = scale Y 0.05.
  - `lift`: the airfield's aircraft lift, from y −0.6 to 0.
  - `stage_0`…`stage_3`: build stages: pad, walls, upper body, heads and caps.
  - `cluster_0`…`cluster_4`: ore clusters.
- **Rigs:** vehicles and structures use rigid nodes. Infantry use at most 20 bones. Ambient motion (flags, antennas, the crane hook, trees, hover bob) runs in vertex shaders.

| Asset | LOD0 triangles | Textures |
|---|---|---|
| Infantry | 1,000–2,000 | 512² |
| Vehicles | 3,000–7,000 | 1024² |
| Aircraft | 4,000–8,000 | 1024² |
| Small structures (1×1, 2×2) | 5,000–10,000 | 1024² unique + shared trim sheet |
| Large structures (3×3) | 10,000–18,000 | 2048² shared trim + 1024² unique |
| Ore tile | ≤ 800 | shared 512² atlas |

- **LOD1** at 50%.
- **PBR set per asset:** albedo (with baked AO and gradient), normal, metallic-smoothness, emission, team mask.
- **Formats:** glTF (or FBX) for models, PNG for textures.
- **Sidebar icon per asset:** 256×192 PNG, three-quarter view, dark background, no text.

### Nothing pops

Every spawn, build, deploy, regrowth, sale and death is a transform over time.

- **Structures** rise out of their pad: stage_0 at 0–10% of build progress, stage_1 at 10–45%, stage_2 at 45–75%, stage_3 at 75–95% with a 4% spring overshoot. Turrets rise and spinners spin up at 95–100%.
- **Vehicles** spawn behind the factory door, which rolls up, and drive out 1.5 tiles south. **Infantry** walk out of the barracks door. **Aircraft** rise on the airfield lift.
- **outpost_truck** deploys by sinking its chassis while the outpost rises in its place.
- **Ore** regrows from below ground.
- **Destroyed or sold** things sink in reverse. A unit's head is blown off on an arc, and the wreck stays 20 s, then sinks.
- **Snappy combat:** idle to firing within 3 frames. Turret slew and laser charge are deliberate telegraphs; the shot is instant. Recoil out is at most 0.05 s.

Full per-asset movement and mechanics are in `assets/unity/MOTION.md`.

### Assets in the game now

**Structures**

| key | footprint | design | moving parts |
|---|---|---|---|
| command_center | 3×3 | Cream hall, smoke dispenser stem with a blue flip-top head, steel crane | spinner (crane), door |
| outpost | 2×2 | Smoke bunker, team roof, antenna, south ore drop pad | – |
| power_plant | 2×2 | Twin steel spring towers, cream coil rings, cyan cores | – |
| mining_refinery | 3×3 | Candy press: cream hall, twin silos with team caps, foil wrapper roll, south dock | door (dock) |
| barracks | 2×2 | Cream bunkhouse, three team heads on the roof, flag | door |
| factory | 3×3 | Foil-wrapped pack hangar, team wrapper band, crimped ends | door |
| electronics_plant | 2×2 | Clean-room box, acid circuit panels | – |
| optics_lab | 2×2 | Frosted dome, floating cyan prism | spinner (prism) |
| enrichment_plant | 2×2 | Four centrifuges with acid bands and team caps | – |
| composite_foundry | 2×2 | Dark angular plant, grape vents (non-emissive) | – |
| fusion_reactor | 3×3 | Steel ring reactor, magenta core | spinner (core) |
| airfield | 3×3 | Runway, control tower, team landing ring | lift |
| radar_dome | 2×2 | Cream radome, steel dish, amber beacon | spinner (dish) |
| gun_turret | 1×1 | Team head on a cream base, brick muzzle | turret, barrel |
| sam_site | 1×1 | 4-tube rack | turret, barrel (rack) |
| laser_tower | 1×1 | Hexagonal pylon, cyan coils, prism emitter | turret, barrel (emitter) |

**Units**

| key | size | design | moving parts |
|---|---|---|---|
| mining_truck | 1.0 | Tracked harvester, front cutter, ore hopper | spinner, bin, bin_ore |
| repair_truck | 0.95 | Wheeled, crane arm with amber welder | turret, barrel |
| outpost_truck | 1.1 | Six wheels, folded bunker | – |
| scout_buggy | 0.7 | Roll-cage buggy, pintle gun | turret, barrel |
| light_tank | 0.9 | Stem hull, team head, single barrel | turret, barrel |
| heavy_tank | 1.2 | Wider, twin barrels | turret, barrel |
| artillery | 0.9 | Long barrel at 60° | turret, barrel |
| laser_tank | 1.1 | Cyan coils, prism tip | turret, barrel |
| gunship | 1.2 | Hovers at 2.4. Team cockpit head, rocket pods | spinner (rotor), turret, barrel |
| stealth_bomber | 1.0 | Licorice flying wing, team edge strip. Flies at 3.2 | – |
| rifleman | 0.55 tall | Team helmet and shoulder pads | turret (arms), barrel (rifle) |
| rocket_soldier | 0.55 tall | Kraft tube launcher | turret, barrel |
| laser_trooper | 0.55 tall | Cream armour, cyan rail rifle | turret, barrel |
| medic | 0.55 tall | White pack with a team plus. Unarmed | – |

**Ores:** iron_ore, copper_ore, crystal and uranium. Each tile holds five clusters, 0.1–0.35 tall, that shrink as they're mined.

### Still to design

1. **Damage states:** every building at 50% and 25% health (scorch decals, broken panels, fire and smoke sockets), plus rubble for each footprint size (1×1, 2×2, 3×3). Rubble keeps the exact footprint. Each vehicle class also needs a burnt hull wreck.
2. **Upgrade states:** factory, radar_dome, power_plant and mining_refinery each gain a visible add-on module that rises like a build stage.
3. **Ground anchoring:** a dark dirt-skirt decal 0.15 past each footprint, and a contact-shadow decal for units.
4. **Terrain:**
   - Tiling PBR biscuit ground in light and dark variants, plus an ore-stained variant for each ore.
   - Licorice cliffs and ramps, and a cola water surface.
   - Decals: tracks, scorch marks, craters.
   - Props: cotton-candy trees, boulders, wrecks, fences, ruined candy shops.
5. **Effects sheets:**
   - Muzzle flashes: brick, cannon, rocket.
   - Explosions: small, medium, large, building collapse.
   - Sugar-dust smoke and sparks.
   - Beams: cyan laser, magenta plasma bolt, amber welding beam, medic heal beam (a team-neutral cream-white).
   - Rally-point and move-order markers.
6. **HUD:** build from the HUD kit on the canvas (build buttons, stockpile icons, cursors, power bar, alerts, selection and health).

### Deliverables, in order

1. **Done (v0.1):** style guide, hero, sidebar icons, blockout glTF models with node contracts, motion spec and Unity scripts, HUD and HUD kit.
2. **Concept turnaround sheets** (front, side, back, three-quarter in the off-axis view) beside a 1-tile grid. Priority: command_center, mining_refinery, power_plant, factory, barracks, then mining_truck, light_tank, heavy_tank, rifleman, then everything else.
3. **Final models** replacing the blockouts in the same priority order, keeping node names, pivots and footprints identical.
4. Damage, upgrade and wreck variants.
5. Terrain, effects and the final HUD art.

Keep one design language across the whole set. Flag any requirement that conflicts with readability before you start.
