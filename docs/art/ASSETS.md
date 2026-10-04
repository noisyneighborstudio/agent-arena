# Pez asset pack v0.1: The Dispenser War

These are blockout assets, built to the brief's exact technical contract (footprints, pivots, node names and team mask) so they can go into Unity today. Replace them with final art later without touching game code.

## What's inside

| Folder | Contents |
|---|---|
| `models/structures/` | 16 structures as `.glb` |
| `models/units/` | 14 units as `.glb` |
| `models/ores/` | 4 ore tiles as `.glb`, each with 5 `cluster_N` nodes |
| `models/terrain/` | 9 faceted boulders (`boulder_s1`…`l3`), plus `demo_massif.glb` and its rock grid as a reference |
| `models/manifest.json` | Per asset: file, triangle count, node list and bounds |
| `icons/` | 34 sidebar icons, 256×192 PNG, three-quarter view on a dark background, no text |
| `unity/MOTION.md` | How every asset moves and behaves, plus how it emerges and is removed. **Start here.** |
| `unity/motion.json` | The same spec as data |
| `unity/Scripts/` | `PezMotion.cs` (drives the nodes), `PezEmerge.cs` (build, exit, lift, regrow and remove transforms), `PezMotionProfiles.cs` (generated numbers), `PezPalette.cs`, `PezCliffBuilder.cs` and `PezCliffs.cs` (rocks and mountains from the pathing grid) |
| `palette/` | `pez_palette.json` and `pez.gpl` (GIMP/Aseprite/Krita) |
| `concept/` | Style guide, off-axis hero render, original on-axis illustration (PNG and SVG) |
| `renders/` | Gameplay map rendered from these models, on-axis perspective and off-axis orthographic, plus build-sequence strips and the rocks fix (`terrain_cliffs.png`) |
| `tools/` | The Python and three.js generators, so you can tweak and rebuild |

## Conventions (all models)

- **Scale and orientation:** 1 tile = 1 unit = 1 m. +Y is up and +Z is forward. The pivot sits at ground level, at the centre of the footprint. Structures fit their footprint with a 0.04 margin. Barrels can overhang it.
- **Doors face −Z (south).**
- **Functional nodes:**
  - `turret` yaws about local Y.
  - `barrel` is a child of `turret` and recoils along its own local −Z. The artillery barrel is pitched −60° and the SAM rack −25°; recoil follows that axis.
  - Artillery (`artillery`, `long_range_artillery`): −60° is the rest and firing pose. `cradle` (a child of `turret` on the same trunnion, pitched with the barrel) carries the recoil cradle. The view stows the gun for travel by pitching `cradle` and `barrel` +57° (to about −3°, onto the bow's travel lock); that pose is view-only.
  - `spade_l` / `spade_r` are the artillery's stabilisers, hinged at the hull's rear. Their rest pose is planted (deployed); the view folds them up 105° about local X for travel.
  - `spinner` is the cutter, crane, rotor, prism, core or dish. Its axis is listed in MOTION.md.
  - `bin` tips about its rear edge. Its child `bin_ore` scales Y with the load.
  - `door` is a roll-up door with its pivot at the top edge. Animate scale Y from 1 to 0.05 to open it.
  - `lift` is the airfield's aircraft lift (y from −0.6 to 0).
  - `stage_0` to `stage_3` are the build stages that rise out of the ground.
  - `cluster_0` to `cluster_4` are ore clusters.
- **Geometry nodes** are named `<parent>__<material>`. The node to animate is always the parent.
- **Materials:** `M_Team` is the team mask, exported in Blueberry blue; tint it at runtime with `PezPalette.Team(i)`. `M_E_*` materials are emissive. Everything else is a neutral material from the style guide.

## Unity setup

1. Install **glTFast** (`com.unity.cloud.gltfast`) from the Package Manager, then drop `models/` into `Assets/Pez/Models`.
2. Copy `unity/Scripts` into `Assets/Pez/Scripts`.
3. Make a prefab per model and add `PezMotion` and `PezEmerge` to its root. The asset key is read from the GameObject name.
4. Gameplay hooks:
   - **Combat:** `motion.AimAt(target)`, then `if (motion.IsAimed()) motion.Fire();`
   - **Structures:** `emerge.SetBuildProgress(p)` every frame while building.
   - **New units:** `StartCoroutine(emerge.PlayExit(factoryMotion))`, or `PlayLift(airfieldMotion, 2.4f)` for aircraft.
   - **Removal:** `StartCoroutine(emerge.PlayRemove(destroyed: true))`.
5. **Nothing pops.** Don't instantiate a visible model at full size outside these calls. The terrain must be opaque at y = 0, because it is what hides the stages while they rise.

## Rocks and mountains

Add `PezCliffs` (with a MeshFilter and MeshRenderer) to an empty GameObject at the map origin. Assign 5 materials in this order: crust T0 `#7A604C`, crust T1 `#8E7259`, crust T2 `#A58A6C`, licorice face `#2E2629`, talus `#4A3D3A`. Assign the 9 boulder prefabs, then call `cliffs.Build(blockedGrid)` once at map load.

- **Where the shapes come from.** The blocked grid stays the pathing truth. The visual outline follows it to within about 0.3 of a tile.
- **How the massifs are built.** Thick masses become 2–3 terraces; thin ridges stay as one mesa; a single blocked tile becomes a small butte.
- **Parity with the Python tool.** `tools/terrain_kit.py` is the same algorithm. Run through mono against a Unity stub, the C# port produced identical output on the demo map (3,970 triangles, 101 boulders, matching hashes).
- **Ground.** Keep value variation at or under 8%. Large dark noise clouds read as shadows.

## What these models are not (yet)

- **Not final art.** They are low-poly blockouts, far under the triangle budget. They have no LOD1, no texture sets, no damage states and no wrecks; those come in the later deliverables.
- **No FBX.** glTF is the delivery format; Blender converts to FBX in one step if you need it.
- **Untested inside Unity.** The C# compiles against a Unity API stub but hasn't run in a Unity project.
- **Check the axes once on import.** glTFast converts glTF to Unity by negating X, so models should come in facing +Z with doors toward the camera. Confirm this on the factory.
