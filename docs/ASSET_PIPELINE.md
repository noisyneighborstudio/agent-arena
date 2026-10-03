# Asset pipeline: generated, runtime-loadable Unity assets

**Status: written down for later. Nothing here is built yet.**

The goal: new art reaches the live arena **without a Unity build**, and the game can **generate** some of that art itself, for example a model for an agent-invented unit (see `docs/spikes/AGENT_INVENTED_TECH.md`).

## Today

- The art pack's `.glb` files sit in `unity/Assets/Pez/Resources/PezModels/<key>.glb`. glTFast imports them in the editor, and they are baked into the player build.
- `Models.ModelFor(key)` calls `Resources.Load`. `Models.Build` then flat-shades the model (`FlatShade`), tints `M_Team` (`TintTeam`), finds the `turret`, `barrel`, `spinner` and `bin` nodes, and attaches `PezMotion`, `PezEmerge` and `Gait` (infantry). A missing model falls back to a code-built placeholder.
- Every art change needs a Unity build plus a deploy: CI runs these on every push, and players see a pause.
- The asset contract is in `docs/art/ASSETS.md` (scale, pivot, footprint, node names, `M_Team`, `M_E_*`), `docs/ASSET_BRIEF.md` and `docs/art/MOTION.md` / `motion.json`.

## Pipeline

```
generate ──▶ validate ──▶ package ──▶ publish ──▶ load at runtime ──▶ hot swap
```

### 1. Generate (any source)
- **Hand-made art.** Handoff zips, as now.
- **Procedural.** The handoff's `tools/` generators (Python and three.js) can be re-run with new parameters.
- **Kitbash (deterministic, no AI).** For inventions: take the chassis's body and graft on the donor unit's `turret`/`barrel` subtree. The Lancer would become a light-tank hull carrying the rocket soldier's launcher, scaled to the turret ring. This is the cheapest useful generator; build it first.
- **AI 3D generation (later).** Prompt it from `ASSET_BRIEF.md` plus the unit's spec. Its output must pass the same validator as everything else.

### 2. Validate (the asset contract as code)
This is a headless lint that runs in CI and at publish time, and rejects anything that breaks the contract:
- glTF 2.0 binary only (`.glb`); no extensions outside an allowlist; no scripts.
- Scale and pivot: the pivot sits at ground level and at the footprint centre; structures fit the footprint with a 0.04 margin; units fit a per-class bounding box.
- Node names: the required functional nodes for the def are present (`turret` if the unit has a turret, with `barrel` as its child, and so on); geometry nodes are named `<parent>__<material>`.
- Materials: `M_Team` exists and covers roughly 15–25% of the surface; emissive materials are `M_E_*` only.
- Budgets: the triangle budgets per class from `ASSET_BRIEF.md`, a texture size cap, a file size cap (a few MB), and a node count cap.
- A rendered thumbnail per asset (top-down plus three-quarter view) for a human to glance at. This also becomes the icon.

### 3. Package
- Store assets content-addressed (`<sha256>.glb`), with an `assets.json` manifest: `key → { hash, version, source, class, tris, bounds, icon }`.
- The manifest is the switch: pointing a key at a new hash swaps the art, and the old file stays for rollback.
- Keys are standard def keys, plus invention keys (`t<team>:<slug>`) once those exist.

### 4. Publish
- Serve from one directory, e.g. `~/.config/pezz/assets/`. It sits outside the checkouts, so deploys don't touch it.
- The game process reads the files locally. The gateway can expose the manifest read-only (`/assets.json`) if the web viewer or outside tools ever want the icons.

### 5. Load at runtime (Unity)
- In `Models.ModelFor`, check the runtime manifest first and load with glTFast's runtime API (`GltfImport.Load` from file, then `InstantiateMainSceneAsync` into a hidden template). After that, fall back to `Resources/PezModels`, then to the placeholder. This keeps the shipped art as a safety net.
- Loading is asynchronous. Show the placeholder (or the chassis model) first, then swap the rig in place when the load completes. `Models.Build` has to support rebuilding a rig on a live entity without losing its selection, bars or motion state.
- Run the same post-processing as baked models: `FlatShade`, `TintTeam`, node lookup, `PezMotion` profile. Runtime-loaded materials need the same shader mapping glTFast does at edit time. Check the colour space: the HUD needed an IMGUI colour-space fix, and these materials may too.
- Cache templates per hash. Evict entries nobody uses when an invention is retired or its seat is recycled.

### 6. Hot swap
- Unity polls or watches `assets.json`. When a hash changes, it rebuilds the affected rigs at the next frame boundary.
- Art is view-only, so the sim, saved games and determinism are unaffected. No pause and no deploy are needed.
- Rollback: point the key back at the previous hash.

## Decisions to make then

- **glTF vs. AssetBundles.** Recommend glTF. glTFast is already a dependency, and glTF files are portable across Unity versions and platforms, easy to lint headlessly, and can't carry engine-specific payloads. AssetBundles are tied to the exact Unity version and build target, so every editor upgrade would invalidate them, and they are hard to validate outside Unity.
- **Who may publish.** Only us for now: CI or a local command. Agent-triggered generation (an invention spawning a kitbash) runs our generator on our inputs, never on agent-supplied geometry or text.
- **Motion profiles for new keys.** `PezMotion` reads its profile from the object name. A generated asset either names an existing profile (the chassis's) or the manifest carries a small profile entry.
- **The web viewer.** It draws 2D glyphs today. Generated icons could replace those glyphs for the selection card.

## Order of work (when picked up)

1. The validator and the manifest format, run in CI against the current `PezModels` (47 `.glb` files today: 33 units and structures, plus ores and deposits).
2. Runtime loading with fallback in `Models.ModelFor`, plus in-place rig rebuild.
3. Hot swap from the manifest.
4. The kitbash generator for inventions (after inventions phase 1 ships).
5. AI generation behind the same validator.
