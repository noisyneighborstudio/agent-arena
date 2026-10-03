# Building fire (FireSim) review, 2026-10-03

**Judged:** the implementation (`FireSim.cs`, `FireVoxels.cs`, `PezFireSim.compute`, `PezFireVolume.shader`, `PezFireSpark.shader`), from the `-fxdemo fires` captures. I also checked them downscaled to about 35 px per tile at JPEG q60.

**Role:** shows damage, and whether a building is being repaired, without a health bar. **Hero idea:** the building itself burns, breathing fire out of its openings, and repair answers it with a white billow.

## The 7 asks (0–3)

| Ask | Score | Evidence |
|---|---|---|
| 1. Emanates from the building | 2 | The command center (CC) burns off its own roof and walls. At fire 0.4, the factory's roof patches read as orange stains (`closeup_v2`). |
| 2. Licks out of openings | 2 | The factory door glows and throws flame, but as a round blob in front of the door that doesn't climb the wall. On the CC the openings are lost in the flames covering the walls. |
| 3. Follows the shape | 2 | Wraps the real shape. At 1.0 it's a see-through orange veil over every wall, and the CC reads as a block of orange jelly (`cc_crop`). |
| 4. Integrated | 1 | Smoke shadow and the depth stop work. The fire light on the ground is faint, and the cream walls stay bright under the flames. |
| 5. Sparks | 1 | Embers and pops are present (`motion_strip` frame 4). They render as dark specks ringed in orange (`sparks_crop`), under 1 px at gameplay distance. |
| 6. Black smoke | 2 | A dense column that leans downwind and shadows the ground. The plume particles show hard rectangular edges (`plume_crop`). |
| 7. White when fought | 2 | Clear at gameplay distance. Muddied by a pale plume over the power plant while nothing is fighting it (right frame). |

**Total: 12/21.**

**§20 (fire as an effect):** Readability 2, Motion 2, States 2, Environment 1, Delight 2 (the pops), Restraint 2. The team colour still reads through the fire.

**What hurts most:** ask 4. Clean walls make the fire look like an overlay.

**Bad:** looping flame sprites and a grey puff. **Good (now):** simulated flames that wrap the building, a soot column, steam under repair. **Great:** the walls char, fire breathes from the tops of the openings and rolls up the wall, sparks streak bright, and white smoke appears only where the beam hits.

## Changes, ranked by impact over cost

1. **Char the walls.** In `WorldView.Damage()`, call `PezShade.SetDim` with lerp(1, 0.5, FireK), eased over 2 s, recovering as HP recovers. Skip the `M_Team` and `_Opening` renderers. S. At gameplay distance: a dark mass with orange tongues. Up close: the flames get contrast.
2. **Fix the sparks.** In `PezFireSpark`, set `o.col = min(o.col, 8)`. In `PezPost.fragFinal`, add `c = min(c, 64)` before the `pow`: a dark core in a bloom halo suggests a non-finite pixel that `PbrNeutral` turns into NaN. Check with `_PezDebug` 2. Raise the pop size from 0.026 to 0.05. S.
3. **Shaped flames, not a veil.** In `Emitters`, lower the wall `face` from 0.55 to 0.2. In `PezFireVolume`, change `flame * 1.2` to `* 3.0` and the edge `smoothstep(0.08,0.55,R)` to `(0.15,0.45)`. In `Advect`, change the burn lifetime from `lerp(0.28,0.42)` to `(0.32,0.55)`, and `_Buoy` from 3.2 to 4.0. S–M.
4. **Openings exhale from the top.** In `MarkOpening`, multiply k by `saturate((wp.y-p.y)/r+0.5)`. Lower the jet from 30 to 18. Make the `OpeningMats` lists in `WorldView.cs` and `FireVoxels.cs` match: `door*`, `M_Licorice` and `M_E_` burn without glowing. S.
5. **White means fought.** Stop the tower steam in `WorldView.Steam` while FireK > 0. Change steam decay from `0.45` to `0.45 + 1.2*(1-_Ext)*_Fire`. S.
6. **Plume and light.** Make the `S.Plume` sprite's alpha reach 0 at the quad edges. Raise `_LightGain` from 2.2 to 3.5. S.

**Sim sync:** no violations. Everything is view-only, driven by HP and `RepairAt`.
