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

- **Mechanics:** Construction yard, built around a tower crane on its own footing (the tallest thing in any base). While a structure is building, the spinner (the crane's slewing top) yaws toward the site at 90°/s and swings ±5° at 0.4 Hz. The trolley runs along the jib at 0.35 tiles/s, out with the site's distance (0.45 at 2 tiles, the jib's end at 8 or more), and the hook drops 0.12 on its cable and bobs 0.02 on a 1.8 s sine. A cream construction beam runs from the hook to the site every 0.25 s, with welding sparks where it lands. When the structure is finished, the hook bounces up 0.06 and settles, and the flip-top head (the dispenser's head node, hinged at its back edge) pops open 35° in 0.12 s and drops shut with a bounce. Low power halves the crane's speed and the beam stutters. The door rolls up for a mining truck unloading at it.
- **`spinner`:** axis=Y, idle_rpm=0, active_rpm=0, mode=aim_then_swing, aim_speed_deg=90
- **`trolley`:** mode=slide_along_jib, speed=0.35, travel=0.45–1.55
- **`head`:** mode=hinge_pop, open_deg=35, open_s=0.12
- **`dish`:** axis=Y, deg_per_s=30
- **`door`:** mode=rollup, open_s=0.35, close_s=0.5
- **Idle:** The crane idles in a slow ±10° sweep every 8 s, its trolley home and the hook swaying 0.008 on a 3 s sine. The comms dish turns at 30°/s (15 on low power). The amber beacon on the crane's apex blinks once every 1.5 s.

### `outpost`
*Forward bunker and ore drop-off.*

- **Mechanics:** Created only by deploying an outpost_truck (see that entry). Mining trucks unload on the south drop pad: they reverse onto it and tip their bin. No door.
- **Idle:** Antenna tip blinks amber once every 2 s.

### `power_plant`
*Generates power.*

- **Mechanics:** A spring under load. The cyan cores brighten and dim with power load (emission 0.6–1.0) and steam rises from both stacks (stack_0, stack_1), faster under load. The cream springs round the towers (coil_0, coil_1, pivots at their feet) are pressed down by their collars as the base draws more: full height with nothing drawing, 78% at full load, eased over 0.4 s. The flywheel on the generator house turns with the load, 40 to 140 rpm. Electric arcs jump between the stacks' lips (and one in four down to an insulator on the transformer, insulator_N): every 5 to 8 s with nothing drawing, every 1.5 to 2.5 s at full load. When power is low the cores flicker at 4 Hz, the steam sputters, the springs bottom out at 70% and chatter at 4 Hz, the flywheel surges and stalls, and the arcs come in stuttering bursts of two or three.
- **`coil`:** mode=scale_y_with_load, full=1.0, loaded=0.78, low=0.7
- **`flywheel`:** axis=X, rpm=40–140 with load
- **Idle:** Cores pulse on a 3 s sine.

### `mining_refinery`
*Turns ore into stockpile.* Produces: mining_truck (free).

- **Mechanics:** The dock door rolls up when a mining truck reverses in and closes after it leaves. While refining, lumps of iron and copper ore ride the belt over the dock from the intake hood to the smelter hood at 0.5 tiles/s, flattened as they pass under the foil crusher roll (the spinner), which turns with the belt. Both smokestacks puff pale smoke from their mouths (stack_0, stack_1), each about every 0.3 s. Out of ore, the belt runs on until it's empty and stops, the roll spins down and the stacks give a thin wisp every few seconds. Low power halves the belt, the roll and the smoke. Dispenses one mining_truck through the dock when it finishes building.
- **`door`:** mode=rollup, open_s=0.35, close_s=0.5
- **`spinner`:** axis=Z, idle_rpm=0, active_rpm=32, spinup_s=0.5
- **Idle:** Dock lights blink alternately at 1 Hz while a truck is docking.

### `barracks`
*Trains infantry.* Produces: rifleman, rocket_soldier, laser_trooper, medic.

- **Mechanics:** A soldier dispenser, driven by the sim's training queue (BarracksWorks). The clear case under the flip-top head holds four soldiers stacked like candy: raw dough at the bottom, primed in the middle, finished with a team helmet at the top. While a soldier trains the stack (stack_lo to stack_hi) rises with its progress, each figure baking as it climbs, a fresh lump of dough swells in at the bottom, and the amber oven strip (oven) at the case's foot glows. 1.5 s before the soldier is out, the top figure rises out of the case into the head; at 1.25 s the head snaps open, and at 1.1 s it spits the soldier out of its mouth (pop), full size and tucked into a ball (0.65 of its height). It somersaults 540° on a ballistic arc to the ground in front (land) in 0.55 s, kicking up a puff of dust, rolls on to its feet (exit) through another 180° with a squash, and stands up as the real unit appears there. The head drops shut. No door.
- **`head`:** mode=flip_top, open_s=1.25 s before the soldier is out, close_s=after the spit
- **`stack`:** mode=rise_with_progress, slots=4
- **`soldier`:** mode=spit_tuck_roll, spit_s=0.55, roll_s=0.35, spin_deg=720, tuck=0.65
- **Idle:** The flag on the pole (flag) is cloth: it streams downwind and ripples, harder in a stronger wind.

### `factory`
*Builds vehicles.* Produces: mining_truck, repair_truck, outpost_truck, scout_buggy, light_tank, heavy_tank, artillery, laser_tank.

- **Mechanics:** While building, the team wrapper band pulses brighter by 10%. Each finished vehicle comes out through the roll-up door. The door takes 0.35 s to open.
- **`door`:** mode=rollup, open_s=0.35, close_s=0.5
- **Idle:** Exhaust stack puffs every 3 s.

### `electronics_plant`
*Turns copper and steel into circuits.*

- **Mechanics:** A circuit-candy line, one cycle per circuit the sim makes (1 s; 2 s on low power, which halves production). Each cycle the pick-and-place carriage (gantry, a little team head) dips its nozzle at the copper feeder, carries the chip (held) along its bridge, pecks it onto the board under it (chip_N appears, with a solder glint) and runs home; then the belt indexes one slot in 0.3 s: a blank drops out of the magazine, the boards (board_N) move on through the reflow oven, whose vent (fume) puffs a wisp of solder fumes, and the last goes into the dispenser stem, where the finished boards stack up behind its window (mag_N). Every fifth board the team flip-top head (head, hinged at its back edge) pops open 40° in 0.1 s, the stack empties from the top, a board (candy) flips out over the lip and away, and the head drops shut with a bounce. The copper reel (reel) turns at 40°/s while the line runs; the roof fan turns at 90 rpm while the plant has power (45 on low power). Starved (Working false: not enough copper or steel), the line finishes the board in hand and parks, and what ran out shows: the reel's copper (winding) runs down to the hub, the magazine's blanks are gone. Glow is state: the clean-room panes and skylight (panes) are lit while it runs, at half on low power, and stand by at 0.22 while it's starved; the signal tower's acid lamp (lamp_run) is steady while it runs and blinks at 2 Hz on low power; its amber lamp (lamp_wait) blinks at 0.9 Hz while it waits for inputs. All dark until it's built; under fog, neutral and still.
- **`gantry`:** mode=shuttle_z, cycle_s=1 (2 on low power)
- **`nozzle`:** mode=dip, pick_and_place=True
- **`head`:** mode=hinge_pop, open_deg=40, open_s=0.1, every_boards=5
- **`reel`:** axis=Z, deg_per_s=40
- **`fan`:** axis=Y, rpm=90 (45 on low power)
- **Idle:** Starved: parked, panes at standby, the amber lamp blinking; the fan still turns while it has power.

### `optics_lab`
*Turns crystal into lenses.*

- **Mechanics:** An observatory that grinds lenses, driven by OpticsWorks from the sim's Working (it had crystal to grind) and its team's power. While it works, the team dome (head) tracks the sky at 3°/s and every 9 to 14 s slews 40–120° to a new star at 25°/s, the telescope (scope) nodding to a new height (−12° to +8°) at 12°/s. The dome turns only through the 260° arc that keeps the telescope 50° clear of the dispenser's bearing either side (it reaches the dispenser within 29°), reversing at the arc's ends, and the telescope looks down (parks) only at home, so the barrel never passes through the dispenser, the hopper or the bench. The light (light, a cyan rod from the collimator on the drum) reaches out to the glass prism in 0.2 s, then the prism lights and fans it into three rays (rays) onto the lens blanks on the turntable (lathe), which turns at 80 rpm. Each lens finished (0.5 a second, the recipe's rate) pops out of the dispenser on the left, a Pez stem of lens candies: its team head (cap, hinged at the back) snaps open 40° in 0.12 s, the top lens (candy) rises 0.1 into the throat with a cyan glint and slips out over the lip, then the head drops shut with a bounce while the stack (stack) pushes the next lens up 0.12 into its place (1.1 s in all). Out of crystal, the rays and then the light go out, the turntable coasts to a stop, the dome turns home at 20°/s the way it came and the telescope parks, pointing down, its objective lens dim. On low power everything runs at half speed (a lens every 4 s), the dome turns in jerks, and the light and rays brown out: a 4 Hz flicker with a dropout every 2.5 s. A lab remembered under fog shows dark and still.
- **`head`:** axis=Y, track_deg_per_s=3, slew_deg_per_s=25, slew_every_s=9–14, arc_deg=260, clear_of_dispenser_deg=50
- **`scope`:** axis=X, nod_deg=−12 to +8, parked_deg=40
- **`light`:** mode=scale_on_off, s=0.2
- **`rays`:** mode=scale_on_off, s=0.2
- **`lathe`:** axis=Y, active_rpm=80, idle_rpm=0
- **`cap`:** mode=hinge_pop, open_deg=40, open_s=0.12, every_s=2
- **`stack`:** mode=push_up, step=0.12
- **Idle:** Dome home, telescope parked, light out, turntable still.

### `enrichment_plant`
*Turns uranium into plasma feedstock.*

- **Mechanics:** Each centrifuge's acid bands chase upward (emission scrolls) while producing. Ambient centrifuge vibration is 0.003 at 25 Hz.
- **Idle:** Bands breathe slowly.

### `composite_foundry`
*Makes stealth composite.*

- **Mechanics:** Steel and crystal in at the back, composite out at the front. One press cycle for every composite the sim makes: 2 s at full power, 4 s on low power. Each cycle a steel blank (blank) slides out of the furnace house's feed slot (press_feed) onto the press bed (press_bed) in 0.32 s; the ram drops fast, slows for the last tenth of its 0.3 stroke and clamps it; its seam glows hot and steam hisses out at both sides of the platen, and under it the blank fuses to black composite. The ram lifts, the tile slides along the rails onto the stack in the magazine (press_out) and the stack (stack) steps down a tile with it, 0.06. The grape vents (vent_0, vent_1) puff pale smoke one after the other after each stroke. The furnace's amber peephole glows while it works (at about half on low power). Out of steel or crystal the press finishes its cycle and stops with the ram up and the bed empty, the peephole cools over 1.5 s and the vents give a thin wisp every 3 to 6 s. A burning foundry gives no steam or puffs.
- **`ram`:** mode=press, stroke=0.3, down=to the blank on the bed, cycle_s=2 (4 on low power)
- **`blank`:** mode=path, path=press_feed > press_bed > press_out, material=steel until fused, then licorice
- **`stack`:** mode=step_down, step=0.06
- **Idle:** Peephole dark; a thin wisp from a vent every 3 to 6 s.

### `fusion_reactor`
*High-output power.*

- **Mechanics:** The spinner (magenta core and its inner ring) spins at 45 rpm and tumbles slowly, rotating 10°/s on X. Under high load it spins up to 120 rpm and the ring emission rises. Destroyed: before collapsing, the core shrinks to 0 over 0.5 s and fires a large magenta shockwave effect.
- **`spinner`:** axis=Y, idle_rpm=45, active_rpm=120, spinup_s=2.0, tumble_x_deg_s=10
- **Idle:** Core spins and tumbles.

### `airfield`
*Builds and rearms aircraft.* Produces: gunship, transport_chopper, stealth_bomber.

- **Mechanics:** The lift (deck flush in a collar with the team landing ring, in the middle of the runway) is where new and landing aircraft are. While an aircraft is in this airfield's queue the approach lights (approach_0..4: four amber chevrons pointing −Z and a threshold bar) glow a steady low amber and the tower radar (radar) turns at 120°/s (40°/s idle, eased between). 1.5 s before the aircraft is done the lift goes down its well (−0.6 in 1.2 s, eased); when it's done the aircraft stands on the deck and rides it up (0.7 s, eased), then lifts off with its usual spawn ease. If the lift wasn't sent down (another airfield's queue, a fogged view) the aircraft just lifts off the deck. From 2 s before until 1.5 s after a launch the approach lights chase toward −Z (a sweep every 0.5 s) and the tower beacon blinks at 2 Hz. Low power: the lift and radar run at half speed, the chase sweeps once a second, every glow is halved and the beacon blinks every 3 s. The windsock (windsock) turns about its pole to point downwind and droops as the wind drops.
- **`lift`:** down_y=-0.6, lower_s=1.2, rise_s=0.7, lead_s=1.5, ease=smoothstep
- **`radar`:** axis=Y, idle_deg_s=40, producing_deg_s=120
- **`approach`:** chase=−Z, sweep_s=0.5, from_s=-2.0, to_s=1.5
- **`windsock`:** mode=downwind, droop_deg=65 calm to 10 in wind
- **Idle:** The lights are dark, the radar turns at 40°/s, the tower beacon blinks once every 1.5 s and the windsock follows the wind.

### `radar_dome`
*Sees 16 tiles, finds stealth units in that range and warns of enemy aircraft out to 28.*

- **Mechanics:** The search antenna on top of the dome (the spinner; its beam looks toward the feed marker on the horn) is driven by RadarWorks, not by its rpm: while the base has power it sweeps clockwise at 12 rpm. When a new contact comes into this dome's range (an enemy aircraft on radar within 28 tiles, or a stealth unit it has found within 16) it breaks off and swings onto it (up to 180°/s, eased), follows it for 1 s, then spins back up into the sweep over 0.5 s. On low power (the sim switches the air search off; the dome still finds stealth) it coasts to a stop, and every 2.5 to 4.5 s the motor catches and throws it 20–30° round before it stalls again. Still while building.
- **`spinner`:** axis=Y, idle_rpm=0, active_rpm=0, mode=sweep, sweep_rpm=12, slew_deg_s=180, hold_s=1
- **Idle:** The amber beacon on the antenna blinks once a second while it sweeps, double-flashes while the dome holds contacts, and is dark on low power.

### `gun_turret`
*Ground defence cannon.*

- **Mechanics:** A revolver cannon under a team flip-top head. The turret has 360° of rotation and follows the sim's aim (at least 240°/s in the view, so the shot leaves along the barrel); the sim fires within 20° of the target, every 1.3 s. Shots and flashes leave from the brick muzzle at the barrel's far end. On each shot the barrel recoils 0.1 into its sleeve over 0.05 s and returns over 0.3 s, pressing the recoil spring round it against the sleeve (spring, scaled along the barrel to the gap the collar leaves). The drum (six candy-brick rounds) indexes one chamber, 60°, starting 0.05 s after the shot and taking 0.2 s, turning the fired chamber toward the ejection side (the eject marker) and the next round up to the barrel; halfway through, the spent round's foil wrapper flicks out of that side and tumbles to the ground. Low power doesn't slow it.
- **`turret`:** yaw_speed_deg=120, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.1, recoil_out_s=0.05, return_s=0.3, pitch_deg=0
- **`drum`:** axis=Z, index_deg=60, delay_s=0.05, index_s=0.2
- **`spring`:** mode=scale_y_with_recoil
- **Idle:** Holds its aim 3 s after a fight, settles forward, and now and then (every 20–45 s) glances up to 24° to one side for 2–3 s.

### `sam_site`
*Anti-air missile launcher.*

- **Mechanics:** A twin-pod launcher on a jacked turntable. The turret yaws at 200°/s to the sim's aim. The rack (barrel: the elevation axle with a two-tube pod on each end) rests 25° nose-up; while the site is engaging it raises to the air target it faces, 10° to 70° (eased, up to 140°/s), holds that 3 s after the last launch, then settles back. The search radar (radar) turns at 20 rpm (10 on low power); on contact it carries on round to face forward and locks there. Each launch (one per sim shot, every 1.6 s) empties the next loaded tube, the pods taking turns, top tubes first: its round (missile_N, pivot at the mouth, team nose out) drops out of sight, the shot and its flash leave from that mouth with a cream smoke trail, smoke blows out of the pod's back, a dust ring runs out on the ground and the rack bucks 3° (nose up from a top tube, down from a bottom one). No recoil. 2.4 s later a new round slides up into the tube over 0.45 s. Offline (upkeep unpaid) the rack lowers to 4° and the radar stops.
- **`turret`:** yaw_speed_deg=200, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0, pitch_deg=-25, pitch_range=10–70° nose-up toward the target, kick_deg=3.2
- **`radar`:** axis=Y, rpm=20, low_power_rpm=10, lock=faces forward while tracking
- **`missile`:** count=4, hidden_z=-0.16, reload_s=2.4, slide_s=0.45
- **Idle:** The rack rests at 25° nose-up and the radar sweeps round at 20 rpm; the turret glances aside now and then.

### `laser_tower`
*Beam defence.*

- **Mechanics:** The turret (collar) yaws at 180°/s. Charge is 0.3 s, during which the pylon coils light from bottom to top. The beam lasts 0.6 s and the prism tip spins 360° during it. Cooldown is 1.0 s. No recoil.
- **`turret`:** yaw_speed_deg=180, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0, recoil_out_s=0.05, return_s=0.35, pitch_deg=0
- **Idle:** Coils breathe on a 2.5 s sine; the prism turns at 6 rpm.

**Every structure, emerge:** Rises out of its pad in stages; nothing appears instantly. stage_0 (pad) lifts from y=-0.15 to 0 during build progress 0–10%. stage_1, stage_2 and stage_3 each start at local y = -(stage height) and rise to 0 with ease-out cubic over progress 10–45%, 45–75% and 75–95%. The opaque terrain and pad hide whatever is still below ground. stage_3 ends with a 4% Y-scale overshoot that settles in 0.15 s (spring-loaded). Functional nodes (turret, spinner, door, lift) come last, at 95–100%: a turret rises 0.3 out of its housing, then a spinner spins up from 0 rpm.

**Every structure, remove:** Destroyed: shake ±0.03 for 0.4 s, then poof: a soft cream cloud bursts out of the building while it swells 8% for 0.06 s and pops to nothing in 0.14 s. A rubble decal fades in under it. Sold: dust at the footprint corners, then the same poof. Nothing sinks into the ground.

## Units

### `mining_truck`
*Harvests ore and returns it to a refinery or outpost.* Built at **factory**.

- **Locomotion:** tracked · max 1.6 t/s · accel 1.2 t/s² · decel 1.7999999999999998 t/s² · turn 110°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 110°/s. When it has to turn more than 60° it stops and turns first, then drives. Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s). The cutter's teeth always turn at 60 rpm while the engine runs. Harvest loop: drive onto an ore tile; while harvesting, ramp the spinner up to 240 rpm over 0.4 s and creep forward at 0.15 tiles/s across the tile. Each 1 s tick takes one load, and the bin's ore bricks rise in steps of 1/5. When full, the bin bricks reach max height and the truck drives to the nearest refinery dock. It reverses into the dock along +Z, the dock door rolls up, the bin tips 35° about its rear edge for 1.2 s to unload, the bin resets and the door closes.
- **`spinner`:** axis=X, idle_rpm=60, active_rpm=240, spinup_s=0.4
- **`bin`:** mode=fill_steps, steps=5, tip_deg=35, tip_s=1.2, note=bin visible always; ore bricks inside scale Y 0→1 with load
- **Idle:** Spinner coasts down 240→60 rpm when harvesting stops, and to a stop when the truck parks; tiny idle hull vibration 0.005 at 12 Hz while the engine runs.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `repair_truck`
*Repairs friendly vehicles and structures in range.* Built at **factory**.

- **Locomotion:** wheeled · max 2.0 t/s · accel 1.6 t/s² · decel 2.4000000000000004 t/s² · turn 90°/s · turns in place: no
- **Mechanics:** Wheeled: cannot turn in place. Minimum turn radius 0.8 tiles; steering follows a curve and reverses for tight corners. Body rolls up to 4° into turns and pitches 2° under acceleration. Repair: parks 0.8 tiles from the target. The turret (crane base) yaws to face it at 120°/s and the barrel (crane arm) pitches down from −25° to −10°. The amber welder tip pulses 6 Hz and the welding beam effect plays. The arm sways ±3° on a 1.4 s sine while working.
- **`turret`:** yaw_speed_deg=120, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0, recoil_out_s=0.05, return_s=0.35, pitch_deg=-25
- **Idle:** Arm parked at −25° and centred; welder tip off.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `outpost_truck`
*Drives to a site and deploys into an outpost.* Built at **factory**.

- **Locomotion:** wheeled · max 1.3 t/s · accel 0.8 t/s² · decel 1.2000000000000002 t/s² · turn 70°/s · turns in place: no
- **Mechanics:** Wheeled: cannot turn in place. Minimum turn radius 1.0 tiles; steering follows a curve and reverses for tight corners. Body rolls up to 4° into turns and pitches 2° under acceleration. Deploy (2.5 s, all transforms): it must be on a clear 2×2 area. The truck stops and aligns to the grid (rotates to the nearest 90° over 0.4 s). Its body lowers 0.08 over 0.3 s as the jacks extend. The folded bunker lifts off the bed while the truck chassis sinks into the ground over 1.0 s. The outpost's stage_0 to stage_3 rise from the same spot using the structure build sequence, compressed to 1.8 s. Reverse this to undeploy.
- **Idle:** Engine idle bob 0.004 at 8 Hz.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `scout_buggy`
*Fast scout, light anti-infantry.* Built at **factory**.

- **Locomotion:** wheeled · max 3.6 t/s · accel 3.0 t/s² · decel 4.5 t/s² · turn 160°/s · turns in place: no
- **Mechanics:** Wheeled: cannot turn in place. Minimum turn radius 0.5 tiles; steering follows a curve and reverses for tight corners. Body rolls up to 4° into turns and pitches 2° under acceleration. Suspension bounce is exaggerated: body Y on a spring (k = 120, damping 10) driven by terrain noise when moving. It can fire on the move; the turret yaws independently of the body.
- **`turret`:** yaw_speed_deg=300, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.03, recoil_out_s=0.03, return_s=0.08, pitch_deg=0
- **Idle:** The pintle gun swings slowly ±30° when no target is in range.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `light_tank`
*Fast main battle tank.* Built at **factory**.

- **Locomotion:** tracked · max 2.4 t/s · accel 2.0 t/s² · decel 3.0 t/s² · turn 120°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 120°/s. When it has to turn more than 60° it stops and turns first, then drives. Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s). Fires on the move. Each shot: the barrel recoils 0.12 along −Z in 0.05 s and returns over 0.35 s, the hull kicks back 1.5° pitch, and the muzzle flash spawns at the barrel tip.
- **`turret`:** yaw_speed_deg=150, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.12, recoil_out_s=0.05, return_s=0.35, pitch_deg=0
- **Idle:** Turret scans ±40° every 3–5 s when idle.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `heavy_tank`
*Slow armoured tank with twin barrels.* Built at **factory**.

- **Locomotion:** tracked · max 1.4 t/s · accel 1.0 t/s² · decel 1.5 t/s² · turn 70°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 70°/s. When it has to turn more than 60° it stops and turns first, then drives. Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s). Twin barrels share one barrel node and fire as a pair, alternating left and right muzzles 0.15 s apart. Recoil is 0.16 with a 0.5 s return. The hull kicks back 2.5°. It must stop to fire when the target is more than 30° off the hull heading.
- **`turret`:** yaw_speed_deg=80, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0.16, recoil_out_s=0.05, return_s=0.5, pitch_deg=0
- **Idle:** Turret scans ±25° every 4–6 s.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

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
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

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
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `laser_tank`
*Beam tank that keeps a continuous lock on its target.* Built at **factory**.

- **Locomotion:** tracked · max 1.8 t/s · accel 1.4 t/s² · decel 2.0999999999999996 t/s² · turn 100°/s · turns in place: yes
- **Mechanics:** Tracked: pivot-turns in place at 100°/s. When it has to turn more than 60° it stops and turns first, then drives. Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s). Laser: no recoil. Charge 0.4 s (the cyan coils brighten one after another, back to front), then a beam lasts 0.8 s while the turret keeps tracking. Cooldown is 1.2 s and the coils fade out. The barrel vibrates 0.004 at 30 Hz while the beam is on.
- **`turret`:** yaw_speed_deg=140, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** recoil=0, recoil_out_s=0.05, return_s=0.35, pitch_deg=0
- **Idle:** The coils breathe at 20% emission on a 2 s sine.
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `gunship`
*Attack helicopter. Hovers at 2.4.* Built at **airfield**.

- **Locomotion:** rotor · max 3.0 t/s · accel 2.0 t/s² · decel 3.0 t/s² · turn 140°/s · turns in place: yes
- **Mechanics:** Rotorcraft: it can strafe and turn in place. Cruise altitude is 2.4 above the terrain (lerp 2/s). It leans up to 12° into its velocity and 8° into turns. Takeoff: on the lift, the spinner (rotor) spins up 0→600 rpm over 1.0 s, then the aircraft climbs to 2.4 over 1.5 s. The turret is a chin gun (±120°). Rocket pods fire as salvos of 4, alternating sides 0.1 s apart. Landing reverses this.
- **`spinner`:** axis=Y, idle_rpm=600, active_rpm=600, spinup_s=1.0
- **`turret`:** yaw_speed_deg=220, yaw_limit_deg=120, idle_scan=True
- **`barrel`:** recoil=0.02, recoil_out_s=0.02, return_s=0.06, pitch_deg=0
- **Idle:** Hovering: altitude bob ±0.05 on a 2.2 s sine and yaw drift ±3°.
- **Emerge:** Spawns on the airfield lift node, starting at y=−0.6 inside the pad. The lift rises to 0 over 1.2 s (ease-out). The aircraft spins up and climbs to cruise altitude (see its entry). The lift stays up until the aircraft is 1 tile away, then drops back down.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `recon_drone`
*Unarmed flying scout (sight 12). Hovers at 2.8. The long-range drone is the same model at 1.35x.* Built at **factory**.

- **Locomotion:** rotor · max 4.5 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 220°/s · turns in place: yes
- **Mechanics:** Quadcopter: four ducted fans (spinner front left, spinner_1 front right, spinner_2 rear right, spinner_3 rear left); the diagonal pairs counter-rotate (spinner_1 and spinner_3 are authored upside down). Fans spin up 0→700 rpm over 0.6 s before it lifts off. It flies like a quadcopter, with no wings to bank with: the whole body tips toward its acceleration (nose down to set off, a roll toward the new heading when it changes direction, a rear-back to stop that ends in a small rock back to level, up to 30°), holding about 10° of forward lean at cruise. Hovering, it makes tiny ±1.5° corrections. The turret is its camera: a gimbal ball with a cyan lens, unarmed, so it only ever looks around.
- **`spinner`:** axis=Y, idle_rpm=700, active_rpm=700, spinup_s=0.6
- **`turret`:** yaw_speed_deg=240, yaw_limit_deg=360, idle_scan=True
- **Idle:** Hovering: altitude bob ±0.08 on a 3.7 s sine. The camera holds forward, then glances up to ±72° and back now and then (curious).
- **Emerge:** Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `transport_chopper`
*Tandem-rotor transport for 6 infantry. Hovers at 2.6.* Built at **airfield**.

- **Locomotion:** rotor · max 3.5 t/s · accel 2.0 t/s² · decel 3.0 t/s² · turn 120°/s · turns in place: yes
- **Mechanics:** Rotorcraft: it can strafe and turn in place. Two three-blade rotors (spinner front, spinner_1 rear on the raised pylon) counter-rotate (spinner_1 is authored upside down); cream blade tips trace each disc. Takeoff: spins up 0→260 rpm over 1.2 s, then climbs to 2.6. Loading and unloading happen landed (the rear ramp faces the squad).
- **`spinner`:** axis=Y, idle_rpm=260, active_rpm=260, spinup_s=1.2
- **Idle:** Hovering: altitude bob ±0.08 on a 3.7 s sine.
- **Emerge:** Spawns on the airfield lift node, starting at y=−0.6 inside the pad. The lift rises to 0 over 1.2 s (ease-out). The aircraft spins up and climbs to cruise altitude (see its entry). The lift stays up until the aircraft is 1 tile away, then drops back down.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `stealth_bomber`
*High-altitude bomber. Never stops.* Built at **airfield**.

- **Locomotion:** fixed_wing · max 4.5 t/s · accel 1.5 t/s² · decel 2.25 t/s² · turn 45°/s · turns in place: no
- **Mechanics:** Fixed wing: it must keep moving, with a minimum speed of 2.5. Its turn radius comes from the turn rate and its current speed. It banks up to 35° in turns. Takeoff: it rises on the lift, then accelerates along −Z and climbs to 3.2 over 2.5 tiles. Bombing run: it flies straight over the target and drops 6 bombs spaced 0.2 s apart, then circles back to the airfield to rearm. The team-colour edge strip pulses 0.6→1.0 emission while it is cloaked.
- **Idle:** When it has no orders it loiters in a 3-tile circle at 3.2.
- **Emerge:** Spawns on the airfield lift node, starting at y=−0.6 inside the pad. The lift rises to 0 over 1.2 s (ease-out). The aircraft spins up and climbs to cruise altitude (see its entry). The lift stays up until the aircraft is 1 tile away, then drops back down.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `rifleman`
*Basic infantry.* Built at **barracks**.

- **Locomotion:** biped · max 1.1 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** Biped: it turns in place almost instantly. The body bobs 0.02 at 2× its stride frequency while walking. A 4-soldier squad spreads out in a diamond 0.3 apart. The turret node (arms) aims at 400°/s and can aim ±70° from the body heading before the body turns. The barrel (rifle) recoils 0.02 per shot in 3-round bursts. When it is shot it goes prone: root lowers 0.25 and pitches 80° over 0.3 s.
- **`turret`:** yaw_speed_deg=400, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0.02, recoil_out_s=0.02, return_s=0.05, pitch_deg=0
- **Idle:** Weight shift: hips sway 0.01 on a 3 s sine; scans with the arms ±20°.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `rocket_soldier`
*Anti-armour, anti-air infantry.* Built at **barracks**.

- **Locomotion:** biped · max 0.9 t/s · accel 3.5 t/s² · decel 5.25 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** Same movement as the rifleman. To fire, it stops (0.3 s), raises the launcher (barrel pitch 0 to −10° for ground targets and −45° for air), fires and recoils 0.06 back. Reloading takes 1.8 s: the launcher dips to +20° and comes back up.
- **`turret`:** yaw_speed_deg=300, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0.06, recoil_out_s=0.05, return_s=0.25, pitch_deg=0
- **Idle:** Rests the launcher on its shoulder (barrel pitch +10°).
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `laser_trooper`
*Elite beam infantry.* Built at **barracks**.

- **Locomotion:** biped · max 1.0 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** Same movement as the rifleman. It fires a 0.5 s beam after a 0.25 s charge, and the cyan rail brightens during the charge. No recoil.
- **`turret`:** yaw_speed_deg=380, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0, recoil_out_s=0.05, return_s=0.35, pitch_deg=0
- **Idle:** The cyan rail breathes at 30% emission on a 2 s sine.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `medic`
*Heals infantry in range. Unarmed.* Built at **barracks**.

- **Locomotion:** biped · max 1.2 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** Same movement as the rifleman. To heal, it moves within 0.8 of the target and kneels (root lowers 0.12 over 0.2 s). The green heal beam effect plays from the pack. It stands up when the target is full or leaves range.
- **Idle:** Checks around: the body yaws ±25° every 4 s.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

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
