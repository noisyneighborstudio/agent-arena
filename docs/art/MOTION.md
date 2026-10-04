# Pez — motion and mechanics spec (Unity)

Units are tiles (1 tile = 1 Unity unit) and seconds. Rotation speeds are in degrees per second, spinners in rpm. Node names match the .glb files. `PezMotion.cs` drives the named nodes and `PezEmerge.cs` runs the emerge and remove transforms.

## Global rules

- **Nothing pops in or out.** Every spawn, build, deploy, regrowth, sale and death is a transform over time; nothing is toggled with SetActive mid-frame.
- **Structures** rise out of their pad in four stages (`stage_0`…`stage_3`) as build progress advances, then their functional nodes unfold.
- **Units** come out of the building that produced them: a roll-up `door` on the factory, barracks and refinery, and a rising `lift` on the airfield.
- **Recoil** always moves along the barrel node's local −Z, so a pitched barrel (artillery, SAM) recoils along its own axis.
- **Turret aim:** yaw the `turret` node toward the target at `yaw_speed_deg` with no overshoot; fire when the error is within 5°.
- **Hull feel:** acceleration pitch and braking dip are springs on a visual child, never on the gameplay transform.
- **Snappy combat:** idle to firing within 3 frames. Turret slew and laser charge are deliberate telegraphs; the shot itself is instant. Recoil out is at most 0.05 s.
- **Ambient motion runs in vertex shaders:** flags, antennas, the crane hook, trees and hover bob are not bones or nodes. Use a height-masked sine on the vertex shader.
- **Glow shows state:** charge, load, low power and cooldown. Glow is never decoration.
- **Rigs:** vehicles and structures use rigid named nodes. Infantry use at most 20 bones, with the weapon rigid on `barrel`.

## Structures

### `command_center`
*HQ. Builds structures.* Produces: structures.

- **Mechanics:** Construction yard. While it is building, the spinner (crane) yaws toward the placement site at 30°/s and swings ±20° on a 2.5 s sine. The hook bobs 0.05. A cream construction beam runs from the hook to the site. The door opens only during the deploy intro.
- **`spinner`:** axis=Y, idle_rpm=0, active_rpm=0, mode=aim_then_swing, aim_speed_deg=30
- **`door`:** mode=rollup, open_s=0.35, close_s=0.5
- **Idle:** Crane idles with a slow ±10° sweep every 8 s; the amber beacon blinks once every 1.5 s.

### `outpost`
*Forward bunker and ore drop-off.*

- **Mechanics:** Created only by deploying an outpost_truck (see that entry). Mining trucks unload on the south drop pad: they reverse onto it and tip their bin. No door.
- **Idle:** Antenna tip blinks amber once every 2 s.

### `power_plant`
*Generates power.*

- **Mechanics:** The cyan cores brighten and dim with power load (emission 0.6–1.0). Steam particles rise from both towers. When power is low, the cores flicker at 4 Hz.
- **Idle:** Cores pulse on a 3 s sine.

### `mining_refinery`
*Turns ore into stockpile.* Produces: mining_truck (free).

- **Mechanics:** The dock door rolls up when a mining truck reverses in and closes after it leaves. While processing, the foil wrapper roll counter-rotates and the conveyor scrolls its UVs at 0.5/s. Dispenses one mining_truck through the dock when it finishes building.
- **`door`:** mode=rollup, open_s=0.35, close_s=0.5
- **Idle:** Dock lights blink alternately at 1 Hz while a truck is docking.

### `barracks`
*Trains infantry.* Produces: rifleman, rocket_soldier, laser_trooper, medic.

- **Mechanics:** Each soldier comes out through the roll-up door (see each infantry unit's emerge entry). The roof heads flip up 15° in sequence while a soldier is training.
- **`door`:** mode=rollup, open_s=0.25, close_s=0.4
- **Idle:** The flag sways ±6° on a 1.8 s sine.

### `factory`
*Builds vehicles.* Produces: mining_truck, repair_truck, outpost_truck, scout_buggy, light_tank, heavy_tank, artillery, laser_tank.

- **Mechanics:** While building, the team wrapper band pulses brighter by 10%. Each finished vehicle comes out through the roll-up door. The door takes 0.35 s to open.
- **`door`:** mode=rollup, open_s=0.35, close_s=0.5
- **Idle:** Exhaust stack puffs every 3 s.

### `electronics_plant`
*Turns copper into circuits.*

- **Mechanics:** The sour-acid panels scroll a circuit-trace UV pattern while producing. The roof vents spin (no named node; use a UV or shader effect).
- **Idle:** Panels flicker softly.

### `optics_lab`
*Turns crystal into lenses.*

- **Mechanics:** The spinner (prism) floats and turns at 20 rpm, bobbing ±0.04 on a 2 s sine. While producing, it speeds up to 60 rpm and casts cyan caustics on the dome.
- **`spinner`:** axis=Y, idle_rpm=20, active_rpm=60, spinup_s=1.0, bob=0.04
- **Idle:** Prism rotates and bobs.

### `enrichment_plant`
*Turns uranium into plasma feedstock.*

- **Mechanics:** Each centrifuge's acid bands chase upward (emission scrolls) while producing. Ambient centrifuge vibration is 0.003 at 25 Hz.
- **Idle:** Bands breathe slowly.

### `composite_foundry`
*Makes stealth composite.*

- **Mechanics:** Grape vents puff dark, non-glowing smoke every 2 s while producing. No moving nodes.
- **Idle:** Heat shimmer over the vents.

### `fusion_reactor`
*High-output power.*

- **Mechanics:** The spinner (magenta core and its inner ring) spins at 45 rpm and tumbles slowly, rotating 10°/s on X. Under high load it spins up to 120 rpm and the ring emission rises. Destroyed: before collapsing, the core shrinks to 0 over 0.5 s and fires a large magenta shockwave effect.
- **`spinner`:** axis=Y, idle_rpm=45, active_rpm=120, spinup_s=2.0, tumble_x_deg_s=10
- **Idle:** Core spins and tumbles.

### `airfield`
*Builds and rearms aircraft.* Produces: gunship, stealth_bomber.

- **Mechanics:** The lift node starts at y=−0.6 and rises to 0 to present each new aircraft (1.2 s). Landing aircraft descend onto the lift, which lowers 0.6 for rearming (3 s) and rises again. The runway lights chase toward −Z at 3 Hz during takeoff.
- **`lift`:** down_y=-0.6, rise_s=1.2, lower_s=0.8
- **Idle:** Tower beacon blinks every 1.5 s.

### `radar_dome`
*Reveals the minimap.*

- **Mechanics:** The spinner (dish) yaws at 12 rpm. When it detects a new enemy, it snaps to that direction at 180°/s, holds for 1 s, then resumes rotating. When power is low, the dish stops and the beacon goes out.
- **`spinner`:** axis=Y, idle_rpm=12, active_rpm=12, spinup_s=0.5
- **Idle:** The amber beacon blinks every 1 s.

### `gun_turret`
*Ground defence cannon.*

- **Mechanics:** The turret yaws at 120°/s and fires when within 5° of the target. Recoil is 0.1 over 0.05 s with a 0.3 s return. The turret has 360° of rotation.
- **`turret`:** yaw_speed_deg=120, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.1, recoil_out_s=0.05, return_s=0.3, pitch_deg=0
- **Idle:** Slow ±45° scan every 5 s.

### `sam_site`
*Anti-air missile launcher.*

- **Mechanics:** The turret yaws at 200°/s. The barrel (missile rack) pitches between −10° and −70° to lead air targets. It fires 4 missiles 0.15 s apart, and the rack kicks back 0.04 per launch. Reload is 3 s: the rack dips to −5° and comes back up.
- **`turret`:** yaw_speed_deg=200, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.04, recoil_out_s=0.03, return_s=0.15, pitch_deg=-25
- **Idle:** Rack rests at −25° and tracks the sky in a slow sweep.

### `laser_tower`
*Beam defence.*

- **Mechanics:** The turret (collar) yaws at 180°/s. Charge is 0.3 s, during which the pylon coils light from bottom to top. The beam lasts 0.6 s and the prism tip spins 360° during it. Cooldown is 1.0 s. No recoil.
- **`turret`:** yaw_speed_deg=180, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0, recoil_out_s=0.05, return_s=0.35, pitch_deg=0
- **Idle:** Coils breathe on a 2.5 s sine; the prism turns at 6 rpm.

**Every structure, emerge:** Rises out of its pad in stages; nothing appears instantly. stage_0 (pad) lifts from y=-0.15 to 0 during build progress 0–10%. stage_1, stage_2 and stage_3 each start at local y = -(stage height) and rise to 0 with ease-out cubic over progress 10–45%, 45–75% and 75–95%. The opaque terrain and pad hide whatever is still below ground. stage_3 ends with a 4% Y-scale overshoot that settles in 0.15 s (spring-loaded). Functional nodes (turret, spinner, door, lift) come last, at 95–100%: a turret rises 0.3 out of its housing, then a spinner spins up from 0 rpm.

**Every structure, remove:** Destroyed: shake ±0.03 for 0.4 s, then stages sink in reverse order (stage_3 first), 0.35 s each, with ease-in and dust bursts at the footprint edge. A rubble decal fades in under them. Sold: plays the build sequence in reverse over 1.5 s.

## Units

### `mining_truck`
*Harvests ore and returns it to a refinery or outpost.* Built at **factory**.

- **Locomotion:** tracked · max 1.6 t/s · accel 1.2 t/s² · decel 1.7999999999999998 t/s² · turn 110°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 110°/s. When it has to turn more than 60° it stops and turns first, then drives. Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s). Harvest loop: drive onto an ore tile; while harvesting, ramp the spinner up to 240 rpm over 0.4 s and creep forward at 0.15 tiles/s across the tile. Each 1 s tick takes one load, and the bin's ore bricks rise in steps of 1/5. When full, the bin bricks reach max height and the truck drives to the nearest refinery dock. It reverses into the dock along +Z, the dock door rolls up, the bin tips 35° about its rear edge for 1.2 s to unload, the bin resets and the door closes.
- **`spinner`:** axis=X, idle_rpm=0, active_rpm=240, spinup_s=0.4
- **`bin`:** mode=fill_steps, steps=5, tip_deg=35, tip_s=1.2, note=bin visible always; ore bricks inside scale Y 0→1 with load
- **Idle:** Spinner coasts down 240→0 over 1.5 s; tiny idle hull vibration 0.005 at 12 Hz while the engine runs.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `repair_truck`
*Repairs friendly vehicles and structures in range.* Built at **factory**.

- **Locomotion:** wheeled · max 2.0 t/s · accel 1.6 t/s² · decel 2.4000000000000004 t/s² · turn 90°/s · turns in place: no
- **Mechanics:** Wheeled: cannot turn in place. Minimum turn radius 0.8 tiles; steering follows a curve and reverses for tight corners. Body rolls up to 4° into turns and pitches 2° under acceleration. Repair: parks 0.8 tiles from the target. The turret (crane base) yaws to face it at 120°/s and the barrel (crane arm) pitches down from −25° to −10°. The amber welder tip pulses 6 Hz and the welding beam effect plays. The arm sways ±3° on a 1.4 s sine while working.
- **`turret`:** yaw_speed_deg=120, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0, recoil_out_s=0.05, return_s=0.35, pitch_deg=-25
- **Idle:** Arm parked at −25° and centred; welder tip off.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `outpost_truck`
*Drives to a site and deploys into an outpost.* Built at **factory**.

- **Locomotion:** wheeled · max 1.3 t/s · accel 0.8 t/s² · decel 1.2000000000000002 t/s² · turn 70°/s · turns in place: no
- **Mechanics:** Wheeled: cannot turn in place. Minimum turn radius 1.0 tiles; steering follows a curve and reverses for tight corners. Body rolls up to 4° into turns and pitches 2° under acceleration. Deploy (2.5 s, all transforms): it must be on a clear 2×2 area. The truck stops and aligns to the grid (rotates to the nearest 90° over 0.4 s). Its body lowers 0.08 over 0.3 s as the jacks extend. The folded bunker lifts off the bed while the truck chassis sinks into the ground over 1.0 s. The outpost's stage_0 to stage_3 rise from the same spot using the structure build sequence, compressed to 1.8 s. Reverse this to undeploy.
- **Idle:** Engine idle bob 0.004 at 8 Hz.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `scout_buggy`
*Fast scout, light anti-infantry.* Built at **factory**.

- **Locomotion:** wheeled · max 3.6 t/s · accel 3.0 t/s² · decel 4.5 t/s² · turn 160°/s · turns in place: no
- **Mechanics:** Wheeled: cannot turn in place. Minimum turn radius 0.5 tiles; steering follows a curve and reverses for tight corners. Body rolls up to 4° into turns and pitches 2° under acceleration. Suspension bounce is exaggerated: body Y on a spring (k = 120, damping 10) driven by terrain noise when moving. It can fire on the move; the turret yaws independently of the body.
- **`turret`:** yaw_speed_deg=300, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.03, recoil_out_s=0.03, return_s=0.08, pitch_deg=0
- **Idle:** The pintle gun swings slowly ±30° when no target is in range.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `light_tank`
*Fast main battle tank.* Built at **factory**.

- **Locomotion:** tracked · max 2.4 t/s · accel 2.0 t/s² · decel 3.0 t/s² · turn 120°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 120°/s. When it has to turn more than 60° it stops and turns first, then drives. Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s). Fires on the move. Each shot: the barrel recoils 0.12 along −Z in 0.05 s and returns over 0.35 s, the hull kicks back 1.5° pitch, and the muzzle flash spawns at the barrel tip.
- **`turret`:** yaw_speed_deg=150, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.12, recoil_out_s=0.05, return_s=0.35, pitch_deg=0
- **Idle:** Turret scans ±40° every 3–5 s when idle.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `heavy_tank`
*Slow armoured tank with twin barrels.* Built at **factory**.

- **Locomotion:** tracked · max 1.4 t/s · accel 1.0 t/s² · decel 1.5 t/s² · turn 70°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 70°/s. When it has to turn more than 60° it stops and turns first, then drives. Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s). Twin barrels share one barrel node and fire as a pair, alternating left and right muzzles 0.15 s apart. Recoil is 0.16 with a 0.5 s return. The hull kicks back 2.5°. It must stop to fire when the target is more than 30° off the hull heading.
- **`turret`:** yaw_speed_deg=80, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.16, recoil_out_s=0.05, return_s=0.5, pitch_deg=0
- **Idle:** Turret scans ±25° every 4–6 s.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `artillery`
*Long-range indirect fire. Must stop to fire.* Built at **factory**.

- **Locomotion:** tracked · max 1.5 t/s · accel 1.0 t/s² · decel 1.5 t/s² · turn 80°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 80°/s. When it has to turn more than 60° it stops and turns first, then drives. Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s). Fire cycle: stop and deploy 0.6 s (hull lowers 0.04; spade_l and spade_r, folded up 105° for travel, slam down in 0.25 s and bite, kicking dust at their blades; the gun, stowed for travel on the bow's travel lock at about −3°, rises to its fixed −60° over 0.6 s as it nears firing range and snaps up if a shot comes first; moving again, the spades fold and the gun stows once the turret faces forward). The turret yaws toward the target and the barrel stays at a fixed 60° elevation. On firing, the barrel recoils 0.2 along its own axis over 0.05 s and returns over 0.6 s, and the hull pitches back 3°. It must undeploy (0.4 s) before moving. Shells fly a ballistic arc with an apex at 0.35 × range.
- **`turret`:** yaw_speed_deg=60, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.2, recoil_out_s=0.05, return_s=0.6, pitch_deg=-60
- **`spade_l`, `spade_r`:** hinged at the hull's rear; rest pose planted (deployed), +105° about local X folded for travel
- **`cradle`:** the recoil cradle, a child of `turret` on the trunnion, pitched with `barrel` (−60° rest); the view pitches both +57° to stow the gun
- **Idle:** Barrel stays elevated; no idle scan.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `long_range_artillery`
*Range-12 indirect fire. Needs a spotter. Must stop to fire.* Built at **factory**.

- **Locomotion:** tracked · max 1.3 t/s · accel 0.9 t/s² · decel 1.35 t/s² · turn 70°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 70°/s. Same family as artillery with a longer hull and a far longer gun (range 12). Fire cycle as artillery: stop and deploy (hull lowers 0.04; spade_l and spade_r, folded up 105° for travel, slam down in 0.25 s and bite, kicking dust at their blades; the gun, stowed for travel on the bow's travel lock at about −3°, rises to its fixed −60° over 0.6 s as it nears firing range and snaps up if a shot comes first; moving again, the spades fold and the gun stows once the turret faces forward). On firing, the barrel recoils 0.24 along its own axis over 0.05 s and returns over 0.7 s, and the hull pitches back 3°.
- **`turret`:** yaw_speed_deg=50, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.24, recoil_out_s=0.05, return_s=0.7, pitch_deg=-60
- **`spade_l`, `spade_r`:** hinged at the hull's rear; rest pose planted (deployed), +105° about local X folded for travel
- **`cradle`:** the recoil cradle, a child of `turret` on the trunnion, pitched with `barrel` (−60° rest); the view pitches both +57° to stow the gun
- **Idle:** Barrel stays elevated; no idle scan.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `laser_tank`
*Beam tank that keeps a continuous lock on its target.* Built at **factory**.

- **Locomotion:** tracked · max 1.8 t/s · accel 1.4 t/s² · decel 2.0999999999999996 t/s² · turn 100°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 100°/s. When it has to turn more than 60° it stops and turns first, then drives. Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s). Laser: no recoil. Charge 0.4 s (the cyan coils brighten one after another, back to front), then a beam lasts 0.8 s while the turret keeps tracking. Cooldown is 1.2 s and the coils fade out. The barrel vibrates 0.004 at 30 Hz while the beam is on.
- **`turret`:** yaw_speed_deg=140, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0, recoil_out_s=0.05, return_s=0.35, pitch_deg=0
- **Idle:** The coils breathe at 20% emission on a 2 s sine.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `gunship`
*Attack helicopter. Hovers at 2.4.* Built at **airfield**.

- **Locomotion:** rotor · max 3.0 t/s · accel 2.0 t/s² · decel 3.0 t/s² · turn 140°/s · turns in place: yes
- **Mechanics:** Rotorcraft: it can strafe and turn in place. Cruise altitude is 2.4 above the terrain (lerp 2/s). It leans up to 12° into its velocity and 8° into turns. Takeoff: on the lift, the spinner (rotor) spins up 0→600 rpm over 1.0 s, then the aircraft climbs to 2.4 over 1.5 s. The turret is a chin gun (±120°). Rocket pods fire as salvos of 4, alternating sides 0.1 s apart. Landing reverses this.
- **`spinner`:** axis=Y, idle_rpm=600, active_rpm=600, spinup_s=1.0
- **`turret`:** yaw_speed_deg=220, yaw_limit_deg=120, idle_scan=True
- **`barrel`:** recoil=0.02, recoil_out_s=0.02, return_s=0.06, pitch_deg=0
- **Idle:** Hovering: altitude bob ±0.05 on a 2.2 s sine and yaw drift ±3°.
- **Emerge:** Spawns on the airfield lift node, starting at y=−0.6 inside the pad. The lift rises to 0 over 1.2 s (ease-out). The aircraft spins up and climbs to cruise altitude (see its entry). The lift stays up until the aircraft is 1 tile away, then drops back down.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `stealth_bomber`
*High-altitude bomber. Never stops.* Built at **airfield**.

- **Locomotion:** fixed_wing · max 4.5 t/s · accel 1.5 t/s² · decel 2.25 t/s² · turn 45°/s · turns in place: no
- **Mechanics:** Fixed wing: it must keep moving, with a minimum speed of 2.5. Its turn radius comes from the turn rate and its current speed. It banks up to 35° in turns. Takeoff: it rises on the lift, then accelerates along −Z and climbs to 3.2 over 2.5 tiles. Bombing run: it flies straight over the target and drops 6 bombs spaced 0.2 s apart, then circles back to the airfield to rearm. The team-colour edge strip pulses 0.6→1.0 emission while it is cloaked.
- **Idle:** When it has no orders it loiters in a 3-tile circle at 3.2.
- **Emerge:** Spawns on the airfield lift node, starting at y=−0.6 inside the pad. The lift rises to 0 over 1.2 s (ease-out). The aircraft spins up and climbs to cruise altitude (see its entry). The lift stays up until the aircraft is 1 tile away, then drops back down.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `rifleman`
*Basic infantry.* Built at **barracks**.

- **Locomotion:** biped · max 1.1 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** Biped: it turns in place almost instantly. The body bobs 0.02 at 2× its stride frequency while walking. A 4-soldier squad spreads out in a diamond 0.3 apart. The turret node (arms) aims at 400°/s and can aim ±70° from the body heading before the body turns. The barrel (rifle) recoils 0.02 per shot in 3-round bursts. When it is shot it goes prone: root lowers 0.25 and pitches 80° over 0.3 s.
- **`turret`:** yaw_speed_deg=400, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0.02, recoil_out_s=0.02, return_s=0.05, pitch_deg=0
- **Idle:** Weight shift: hips sway 0.01 on a 3 s sine; scans with the arms ±20°.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `rocket_soldier`
*Anti-armour, anti-air infantry.* Built at **barracks**.

- **Locomotion:** biped · max 0.9 t/s · accel 3.5 t/s² · decel 5.25 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** Same movement as the rifleman. To fire, it stops (0.3 s), raises the launcher (barrel pitch 0 to −10° for ground targets and −45° for air), fires and recoils 0.06 back. Reloading takes 1.8 s: the launcher dips to +20° and comes back up.
- **`turret`:** yaw_speed_deg=300, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0.06, recoil_out_s=0.05, return_s=0.25, pitch_deg=0
- **Idle:** Rests the launcher on its shoulder (barrel pitch +10°).
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `laser_trooper`
*Elite beam infantry.* Built at **barracks**.

- **Locomotion:** biped · max 1.0 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** Same movement as the rifleman. It fires a 0.5 s beam after a 0.25 s charge, and the cyan rail brightens during the charge. No recoil.
- **`turret`:** yaw_speed_deg=380, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0, recoil_out_s=0.05, return_s=0.35, pitch_deg=0
- **Idle:** The cyan rail breathes at 30% emission on a 2 s sine.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

### `medic`
*Heals infantry in range. Unarmed.* Built at **barracks**.

- **Locomotion:** biped · max 1.2 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** Same movement as the rifleman. To heal, it moves within 0.8 of the target and kneels (root lowers 0.12 over 0.2 s). The green heal beam effect plays from the pack. It stands up when the target is full or leaves range.
- **Idle:** Checks around: the body yaws ±25° every 4 s.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then sinks into the ground over 2 s.

## Ores

### `iron_ore`
*Resource field (one tile).*

- **Mechanics:** Five cluster nodes per tile. Mined: each cluster's scale equals its remaining fraction (min 0.25). When a cluster empties, it sinks 0.2 over 1.0 s and is then disabled. Regrowth: an empty cluster rises from y=−0.2 and scales 0.1 to 1 over 6 s (ease-out); nothing appears at full size.
- **`clusters`:** names=['cluster_0', 'cluster_1', 'cluster_2', 'cluster_3', 'cluster_4']
- **Emerge:** Grows from the ground (see mechanics).
- **Remove:** Sinks when empty.

### `copper_ore`
*Resource field (one tile).*

- **Mechanics:** Five cluster nodes per tile. Mined: each cluster's scale equals its remaining fraction (min 0.25). When a cluster empties, it sinks 0.2 over 1.0 s and is then disabled. Regrowth: an empty cluster rises from y=−0.2 and scales 0.1 to 1 over 6 s (ease-out); nothing appears at full size.
- **`clusters`:** names=['cluster_0', 'cluster_1', 'cluster_2', 'cluster_3', 'cluster_4']
- **Emerge:** Grows from the ground (see mechanics).
- **Remove:** Sinks when empty.

### `crystal`
*Resource field (one tile).*

- **Mechanics:** Five cluster nodes per tile. Mined: each cluster's scale equals its remaining fraction (min 0.25). When a cluster empties, it sinks 0.2 over 1.0 s and is then disabled. Regrowth: an empty cluster rises from y=−0.2 and scales 0.1 to 1 over 6 s (ease-out); nothing appears at full size. Shards glint: emission pulses per cluster with a random phase.
- **`clusters`:** names=['cluster_0', 'cluster_1', 'cluster_2', 'cluster_3', 'cluster_4']
- **Emerge:** Grows from the ground (see mechanics).
- **Remove:** Sinks when empty.

### `uranium`
*Resource field (one tile).*

- **Mechanics:** Five cluster nodes per tile. Mined: each cluster's scale equals its remaining fraction (min 0.25). When a cluster empties, it sinks 0.2 over 1.0 s and is then disabled. Regrowth: an empty cluster rises from y=−0.2 and scales 0.1 to 1 over 6 s (ease-out); nothing appears at full size. Rods pulse with emission 0.7–1.0 on a 1.5 s sine; heat shimmer above them.
- **`clusters`:** names=['cluster_0', 'cluster_1', 'cluster_2', 'cluster_3', 'cluster_4']
- **Emerge:** Grows from the ground (see mechanics).
- **Remove:** Sinks when empty.
