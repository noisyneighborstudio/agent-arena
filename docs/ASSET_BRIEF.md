# Pezz asset design brief

Paste the prompt below into Claude (Claude Design, or any Claude surface that can produce images, SVG or 3D files). It describes every asset the game currently uses, with the exact footprints, moving parts and naming the Unity code expects, so the results can be dropped straight in.

---

## Prompt

You're the art director and lead asset designer for **Pezz**, a modern, high-resolution real-time strategy game in the tradition of Command & Conquer (Tiberian Sun / Red Alert 2 / C&C 3), built in Unity. Two to four armies fight over a resource map: mining trucks harvest four kinds of ore, factories refine it, and players build bases, tanks, aircraft and laser weapons. Matches are often played by AI models against each other and watched by spectators, so **readability from a high, angled camera matters more than close-up detail**.

### Style

- **Grounded near-future military industrial, slightly stylized.** The look is chunky silhouettes and believable machinery, in the spirit of C&C 3 and Company of Heroes, with a cleaner palette. It is not cartoon and not grimdark.
- **Camera.** The view is perspective, 38° field of view, pitched 52–68° down, 8–70 world units from the ground. Every asset must read clearly at a distance of 25 units, where one tile covers about 40 px.
- **Silhouette first.** Each unit and building must be recognisable from its outline alone. Weapons, turrets and functional parts should be exaggerated about 20% so players can read them.
- **Team colour.** Each asset has a team-colour mask, covering about 15–25% of its surface (roof panels, stripes, flags, canopy trim). The team colours are Blue `#2E73FF`, Red `#F22E1F`, Green `#33D94D` and Yellow `#FFD126`. Everything else uses neutral industrial materials: concrete, gunmetal, olive steel, hazard yellow trim, glass.
- **Emissive accents carry meaning.** Use cyan for optics and lasers, magenta/pink for plasma and fusion, green for uranium and electronics, amber for refinery and docking lights. Keep emissive areas small and purposeful.
- **Lighting.** Warm sun from the upper left, cool sky fill. Don't paint shadows into textures.

### Technical requirements (Unity, built-in render pipeline)

- **Scale:** one map tile is one Unity unit (1 m in the file). +Y is up, +Z is forward (the direction units face and fire). Each pivot sits at ground level, at the centre of the footprint.
- **Buildings** must fit exactly inside their tile footprint, with a 0.04-unit margin on every side. Doors, docks and vehicle exits face −Z (south).
- **Moving parts** are separate child nodes with exactly these names. The game rotates and animates them:
  - `turret`: yaws around Y
  - `barrel`: recoils along −Z; a child of `turret`
  - `spinner`: spins (mining cutter, rotor, radar dish, fusion core)
  - `bin`: shows and hides (truck ore load)
- **Polygon budget:** infantry 1–2k tris; vehicles 3–8k; aircraft 4–8k; small buildings 6–12k; large buildings 12–25k. Include LOD1 at 50%.
- **Textures:** a single PBR set per asset (albedo, normal, metallic-smoothness packed, emission, team mask), 1024² for units and 2048² for 3×3 buildings.
- **Formats:** FBX or glTF for models, PNG for textures. For each asset also deliver a **sidebar icon**: 256×192 PNG, three-quarter view, dark background, no text.

### Assets that exist in the game now

**Ores** (four kinds; crystal clusters scattered over ore tiles, 4–6 per tile, 0.1–0.35 units tall, shrinking as the tile is mined):

| Ore | Look |
|---|---|
| iron_ore | rust-red metallic rock chunks |
| copper_ore | orange metal with verdigris patches |
| crystal | ice-blue glowing shards |
| uranium | toxic green glowing rods/shards |

**Structures:**

| key | footprint | look | moving parts |
|---|---|---|---|
| command_center | 3×3 | HQ: armoured hall, construction crane, comms | `spinner` = crane |
| outpost | 2×2 | forward bunker with antenna, ore drop pad | – |
| power_plant | 2×2 | twin cooling towers with cyan glow | – |
| mining_refinery | 3×3 | silos and processing hall; truck unload dock on the south side | – |
| barracks | 2×2 | military barracks, door south, flagpole | – |
| factory | 3×3 | big vehicle hangar, roll-up door south | – |
| electronics_plant | 2×2 | clean-room fab with green-lit circuit panels | – |
| optics_lab | 2×2 | glass dome with a floating crystal prism | `spinner` = prism |
| enrichment_plant | 2×2 | centrifuge tanks with green glow bands | – |
| composite_foundry | 2×2 | dark angular stealth-material plant, purple vents | – |
| fusion_reactor | 3×3 | ring reactor with a magenta plasma core | `spinner` = core |
| airfield | 3×3 | runway pad, control tower, landing ring | – |
| radar_dome | 2×2 | radome sphere plus rotating dish, red beacon | `spinner` = dish |
| gun_turret | 1×1 | armoured cannon turret on a concrete base | `turret`, `barrel` |
| sam_site | 1×1 | 4-tube missile launcher | `turret`, `barrel` (rack) |
| laser_tower | 1×1 | tall pylon with a cyan crystal emitter | `turret`, `barrel` (emitter) |

**Units** (length in tiles):

| key | size | look | moving parts |
|---|---|---|---|
| mining_truck | 1.0 | tracked harvester, front rotary cutter, ore hopper | `spinner` (cutter), `bin` |
| repair_truck | 0.95 | utility truck with welding crane arm | `turret` (crane), `barrel` (welder tip) |
| outpost_truck | 1.1 | heavy carrier hauling a folded bunker | – |
| scout_buggy | 0.7 | fast 4-wheel buggy with a pintle machine gun | `turret`, `barrel` |
| light_tank | 0.9 | fast tracked tank, single barrel | `turret`, `barrel` |
| heavy_tank | 1.2 | big tracked tank, twin barrels | `turret`, `barrel` |
| artillery | 0.9 | self-propelled gun with a long barrel raised 60° | `turret`, `barrel` |
| laser_tank | 1.1 | tank with a beam emitter and magenta energy coils | `turret`, `barrel` |
| gunship | 1.2 | attack helicopter with rocket pods (flies 2.4 units up) | `spinner` (rotor), `turret`, `barrel` |
| stealth_bomber | 1.0 | black flying wing, faint team-colour edge light (flies 3.2 up) | – |
| rifleman | 0.55 tall | infantry, rifle | `turret` (arms), `barrel` (rifle) |
| rocket_soldier | 0.55 tall | infantry, shoulder launcher | `turret`, `barrel` |
| laser_trooper | 0.55 tall | elite armour, glowing cyan rifle | `turret`, `barrel` |
| medic | 0.55 tall | white medical pack with a red cross, no weapon | – |
| engineer | 0.55 tall | yellow hard hat, toolbox, no weapon | `turret`, `barrel` (toolbox) |
| sniper | 0.55 tall | ghillie camouflage, long scoped rifle | `turret`, `barrel` |
| commando | 0.55 tall | beret, satchel of glowing red C4 charges | `turret`, `barrel` |
| apc | 0.95 | six-wheeled armoured carrier, rear door, roof machine gun | `turret`, `barrel` |
| flak_track | 0.85 | tracked anti-air with quad guns angled up | `turret`, `barrel` |
| minelayer | 0.9 | tracked vehicle with a hazard-yellow mine hopper | – |
| mine | 0.32 wide | flat disc mine with team ring and blinking red light | – |
| mammoth_tank | 1.5 | super-heavy twin-cannon tank with side missile pods | `turret`, `barrel` |
| recon_drone | 0.5 | quadcopter drone with a cyan camera eye (flies 2.8 up) | – |
| transport_chopper | 1.3 | twin-rotor transport helicopter (flies 2.6 up) | `spinner` (rotors) |

### Assets the game doesn't have yet (design these too)

1. **Damage states:** every building at 50% and 25% health (scorching, broken panels, fire and smoke sockets), plus a destroyed rubble version for every footprint size (1×1, 2×2, 3×3) and a burnt hull wreck for each vehicle class.
2. **Construction state:** scaffolding and foundation versions of each footprint size, for buildings under construction.
3. **Terrain:** tiling PBR ground textures (grass, dirt, rock cliff, sand/seabed, and an ore-stained variant of each ore), cliff and ramp pieces, a water surface, decals (tank tracks, scorch marks, craters), and props (trees, boulders, wrecks, fences, civilian ruins).
4. **Effects sprite sheets:** muzzle flashes (bullet, cannon, rocket), explosions (small, medium, large, building collapse), smoke, sparks, laser beam and impact, plasma beam, a welding repair beam, a green medic heal beam, and a rally-point and move-order marker.
5. **UI:** sidebar frame, build buttons (normal, hover, disabled, building with progress), resource icons for the 10 stockpile items (iron_ore, copper_ore, crystal, uranium, steel, copper, circuits, lenses, plasma, composite), a power bar, a minimap frame, a priority-alert banner (critical red, high amber), selection rings, health bars and cursors (select, move, attack, harvest, repair, deploy, invalid).

### Deliverables, in this order

1. **A one-page style guide:** palette, materials, team-colour usage and emissive rules, plus one hero illustration of a Blue base under attack by Red, viewed from the game camera.
2. **Concept turnaround sheets** (front, side, back, three-quarter top) for every structure and unit above, each at game-camera scale beside a 1-tile grid.
3. **Sidebar icons** for every structure and unit.
4. **3D models** (FBX or glTF) with the node names above, in this priority order:
   1. command_center, mining_refinery, power_plant, factory, barracks
   2. mining_truck, light_tank, heavy_tank, rifleman
   3. everything else
5. **UI kit and effects sheets.**

Keep a consistent design language across the whole set. Players should be able to tell a laser unit, a plasma unit and a conventional unit apart at a glance. Flag any requirement that conflicts with readability before you start.
