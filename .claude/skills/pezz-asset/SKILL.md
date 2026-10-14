---
name: pezz-asset
description: How to make, refine, animate, verify and ship any Pezz art asset — a building or unit model, its motion (a Works class, Gait, SoldierWorks, docks, lights), effects, icons and kitchen-sink exhibits. Use before touching art-src/tools/build_models.py, motion.py, unity/Assets/Pez/View/*Works.cs or other view motion, PezModels/PezIcons, docs/art/MOTION.md or KitchenSink.cs, and whenever asked to refine, redesign, fix or review how something looks or moves.
---

# Pezz asset work

Read `docs/art/ASSET_GUIDE.md` in full before starting, and follow it. It is the definitive practice for art and motion
in this repo: the house style (§1), the owner's rules (§2), the sim-sync rules (§3), designing (§4), the model and
motion pipeline (§5), motion docs (§6), the kitchen sink and verification (§7), parallel work (§8), shipping (§9) and
the definition of done (§10). The quality bar it applies is `docs/art/ART_DIRECTION.md`.

The non-negotiables, in short:

- The owner approves every model from a review pack (before/after, in game beside siblings, contact sheet, GIF, icon,
  clearance) before it ships.
- Match the family; nothing clips over the full range of motion; one system per transform; no snaps or size jumps;
  glass reads in game; glow casts light; unused parts go dark; the act is honest to the sim (propose a Rules change
  rather than faking it).
- `art-src/tools/motion.py` is the truth for motion docs: splice with `art-src/tools/kit/motion_splice.py`, never
  regenerate or hand-edit the shipped docs.
- Tools: `art-src/tools/kit/` (`one.py`, `preview.sh`, `icon.sh`, `kitchen.sh`, `clip.py`, `sheet.py`, `glbinfo.py`,
  `motion_splice.py`).
- Stop only your own app; never pkill; never touch `pez-headless` processes.

For a scored review, use the `art-director` agent.
