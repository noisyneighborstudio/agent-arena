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
*Forward ore drop-off, refuelling point and lookout (vision 9).*

- **Mechanics:** A forward ore depot: a dispenser being filled, run by OutpostWorks from the sim's dock steps. Created only by deploying an outpost_truck (see that entry): while its stages rise the lookout's mast is down, and once stage_3 lands it telescopes up 0.24 in 0.6 s with a small overshoot. Mining trucks back up to the hopper on the south edge (no door). When a truck lines up in the bay (Align) the hopper's two team trapdoors (flap_l, flap_r, hinged on the rim's sides) swing open 105° in 0.4 s with a small overshoot; they stay open while it reverses and tips, and slam shut 0.3 s after it starts to pull out. The truck's ore-coloured pour falls into the open hopper. Every 20 ore that leaves the bin (the sim tips 10 every 0.1 s, so about 8 lumps a load) a lump of that ore comes up out of the hopper onto the cleated belt (feed_in) 0.4 s later and rides it up the ramp at 0.7 tiles/s to its head (feed_out); the head drum (drum) turns with the belt, which spins up in 0.3 s and runs down in 0.5 s. At the top each lump is tossed over the stem's lip into the dispenser stem (drop) in 0.35 s. The team flip-top head (head, hinged at its back edge) lifts 55° in 0.2 s as the first lump nears the top, holds open while the ore comes in, and drops shut with two bounces 0.5 s after the last. Every second lump a pressed brick of that ore drops onto the stack behind the stem's window (silo_0..4, pitch 0.085, up to five, tinted to the ore; crystal and uranium glow as they do in the field). Between deliveries the stack is fed away underground: every 3.5 s it slides down a slot in 0.6 s and the bottom brick leaves the window, down to one. The lookout (lookout, on lookout_mast) sweeps ±80° around the bay side at 25°/s with a 1–1.6 s pause at each end; an enemy unit its team can see within 9 tiles draws it round (up to 120°/s) to follow the nearest. The outpost makes its own power (+10), so low power doesn't slow it. Under fog (a render whose team only remembers it) the belt is empty and nothing moves.
- **`flap_l`:** axis=Z (hinge on the rim side), open_deg=105, open_s=0.4, close_s=0.3, note=flap_r mirrors it; open from Align until 0.3 s into PullOut
- **`drum`:** axis=X, mode=belt_head, belt_speed=0.7
- **`head`:** mode=hinge_open_while_fed, open_deg=55, open_s=0.2, close_delay_s=0.5
- **`silo_0`:** mode=stack, pitch=0.085, note=silo_0..4: a brick per two lumps, fed away every 3.5 s down to one
- **`lookout`:** axis=Y, mode=sweep_or_track, sweep_deg=80, deg_per_s=25, track_deg_s=120, range=9
- **`lookout_mast`:** mode=telescope_on_deploy, travel=0.24, s=0.6
- **Idle:** The trapdoors shut and the belt still; one or two pressed bricks in the stem's window; the lookout sweeping across the bay side and pausing at each end.

### `power_plant`
*Generates power.*

- **Mechanics:** A spring under load. The cyan cores brighten and dim with power load (emission 0.6–1.0) and steam rises from both stacks (stack_0, stack_1), faster under load. The cream springs round the towers (coil_0, coil_1, pivots at their feet) are pressed down by their collars as the base draws more: full height with nothing drawing, 78% at full load, eased over 0.4 s. The flywheel on the generator house turns with the load, 40 to 140 rpm. Electric arcs jump between the stacks' lips (and one in four down to an insulator on the transformer, insulator_N): every 5 to 8 s with nothing drawing, every 1.5 to 2.5 s at full load. When power is low the cores flicker at 4 Hz, the steam sputters, the springs bottom out at 70% and chatter at 4 Hz, the flywheel surges and stalls, and the arcs come in stuttering bursts of two or three.
- **`coil`:** mode=scale_y_with_load, full=1.0, loaded=0.78, low=0.7
- **`flywheel`:** axis=X, rpm=40–140 with load
- **Idle:** Cores pulse on a 3 s sine.

### `mining_refinery`
*Turns ore into stockpile.* Produces: mining_truck (free).

- **Mechanics:** The dock door rolls up when a mining truck reverses in and closes after it leaves. While refining, lumps of iron and copper ore ride the belt over the dock from the intake hood to the smelter hood at 0.5 tiles/s, flattened as they pass under the foil crusher roll (the spinner), which turns with the belt. Both smokestacks puff pale smoke from their mouths (stack_0, stack_1), each about every 0.3 s. Out of ore, the belt runs on until it's empty and stops, the roll spins down and the stacks give a thin wisp every few seconds. Low power halves the belt, the roll and the smoke. Dispenses one mining_truck through the dock when it finishes building. The truck bay is on the south, through the dock door; when terrain blocks that lane it moves to the first clear side (east, west or north), and the dock's kraft chute then comes out of that wall. Each of those walls has a hatch (hatch_0, hatch_1, hatch_2: a licorice frame round a dark opening and a cream roll-up shutter with a team lip): the shutter facing the bay rolls up in 0.3 s when a truck lines up, the chute waits for it, slides out through the opening to the truck and back, and the shutter rolls down in 0.4 s once the chute is inside. The shutter stays up while the chute is out, so it never passes through a closed one. Drop-offs the game places for you face south, so this is for terrain-blocked bays and the player's own placements; if the sim moves a bay to another wall mid-match (the building that blocked the south lane is destroyed, or one is put on it), the chute draws back, the old hatch shuts, the chute shrinks away and comes up on the new side. The dock lamps follow the bay: the two south lamps and their pips run the amber come-in chase and the steady cream clear only while the bay is south, and are dark otherwise. The south bay then reads as shut, behind a cream roll-up shutter with a team lip (bay_shutter) that is rolled up, out of sight, while the bay is south and rolls down in 0.4 s (once the chute is back inside) when the bay is on a side wall; it rolls up in 0.3 s, ahead of the chute, when the bay returns south. Each side hatch has its own amber lamp lens above its frame (dock_lens_0, dock_lens_1, dock_lens_2) with two pips either side, which run the same 2 Hz chase in toward the lens and the same cream clear and dark sequence for the wall the truck is using, so the truck is waved into the wall it docks at. When the bay changes side the old wall's signal fades out and the new one in over 0.2 s.
- **`door`:** mode=rollup, open_s=0.35, close_s=0.5
- **`hatch`:** mode=rollup, open_s=0.3, close_s=0.4, with_chute=True
- **`spinner`:** axis=Z, idle_rpm=0, active_rpm=32, spinup_s=0.5
- **Idle:** Dock lights blink alternately at 1 Hz while a truck is docking.

### `barracks`
*Trains infantry.* Produces: rifleman, rocket_soldier, laser_trooper, medic, engineer, sniper, commando.

- **Mechanics:** A soldier dispenser, driven by the sim's training queue (BarracksWorks). The clear case under the flip-top head holds four soldiers stacked like candy: raw dough at the bottom, primed in the middle, finished at the top. Each is a figurine of the soldier it becomes (its own model, a case figure tall): the top one the soldier in training, those below the next in the queue, the rest the last one queued (the rifleman before anything is). A long weapon that would reach the glass (the sniper's rifle) turns its figure aside, across the case. While a soldier trains the stack (stack_lo to stack_hi) rises with its progress, each figure baking as it climbs, a fresh lump of dough swells in at the bottom, and the amber oven strip (oven) at the case's foot glows. 1.5 s before the soldier is out, the top figure rises out of the case into the head; at 1.25 s the head snaps open, and at 1.1 s it spits the soldier out of its mouth (pop), full size and tucked into a ball (0.65 of its height), hugging its weapon down along its body (arms 60° down; it lets go as it stands). It somersaults 540° on a ballistic arc to the ground in front (land) in 0.55 s, kicking up a puff of dust, rolls on to its feet (exit) through another 180° with a squash, and stands up as the real unit appears there. The head drops shut. No door.
- **`head`:** mode=flip_top, open_s=1.25 s before the soldier is out, close_s=after the spit
- **`stack`:** mode=rise_with_progress, slots=4
- **`soldier`:** mode=spit_tuck_roll, spit_s=0.55, roll_s=0.35, spin_deg=720, tuck=0.65
- **Idle:** The flag on the pole (flag) is cloth: it streams downwind and ripples, harder in a stronger wind.

### `factory`
*Builds vehicles.* Produces: mining_truck, repair_truck, outpost_truck, scout_buggy, light_tank, heavy_tank, artillery, laser_tank.

- **Mechanics:** A Pez dispenser lying on its side: the head house over the door is its flip-top head, the tray behind it the candy channel. It runs from the item the factory is making (f = progress / build time, so low power halves it with the sim). A hull blank rises onto the tray at the back (f 0 to 0.12). The gantry (gantry node, on the tray walls) dips its hoist over the press and picks up a team head (hook_head takes die_head's place, f 0.09), carries it over the hull (f 0.14 to 0.3), lets it down on its cable and sets it on (blank_head, f 0.44) with welding sparks every 0.2 s, and lifts the hook by f 0.55. Then the spring-loaded pusher (pusher; the cream spring node stretches to stay on it) feeds the hull up the tray into the head house, reaching the chamber mouth at f 1, the gantry riding with it to the end of its runway. On trained the hull is inside, the pusher snaps back and the spring recoils (a damped spring, about 12% overshoot), cocked for the next. A cancelled item's hull is dropped and its head goes back on the die. While producing the press ram strokes 0.25 every 1.5 s (fall 0.1 s, dwell, slow rise); each stroke restocks the die, indexes the foil wrapper roll (spool) 36°, puffs steam off the die and pale smoke from the stack (stack_0). Low power: a stroke every 3 s with a stall on the way up, sparks every 0.4 s. The flip-top head (head node, hinged at its back edge) lifts up to 22° as the roll-up door opens and drops back with a bounce when it closes. Roll-out anticipation (lamps, roof vents, the door open before the unit appears, the dark bay) is WorldView's RollOut. A remembered factory under fog shows idle; a burning factory's stack doesn't puff.
- **`door`:** mode=rollup, open_s=0.35, close_s=0.5
- **`head`:** mode=hinge_with_door, open_deg=22
- **`gantry`:** mode=slide_z_with_progress, speed=1.6
- **`hoist`:** mode=drop_on_cable, speed=0.8
- **`pusher`:** mode=slide_z_with_progress, push_from=0.55
- **`spring`:** mode=scale_y_to_pusher
- **`ram`:** mode=stroke, travel=0.25, period_s=1.5, low_power_period_s=3
- **`spool`:** axis=Z, index_deg=36
- **Idle:** The gantry parked over the press with its hook up and a head waiting on the die, the pusher cocked, the line empty; the stack gives a thin wisp every 3 s (every 4 to 7 s on low power).

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
*Turns uranium into plasma.*

- **Mechanics:** A centrifuge hall, one cycle per plasma the sim makes (2.5 s; 5 s on low power, which halves production). The cascade of six squat rotors (rotor_0..5, foil drums with team caps) spins under the skylight while the plant works: up to 450°/s in about 1.5 s, at half speed on low power, where it also sags to a third for half a second every 2.6 s as the supply browns out; starved, it coasts to a stop over about 3 s. Each cycle a slug of uranium runs out of the hopper: the heap (ore) slumps 0.015 and settles, and the sight glass on the hopper's pedestal (sight) flashes as it passes. At the end of the cycle a magenta plasma brick comes up into the dispenser stem from below and pushes the stack up a slot (mag_0..3, pitch 0.12), and the exhaust vent (vent) puffs. Every fourth brick the team flip-top head (head, hinged at its back edge) pops open 40° in 0.1 s, the stack empties from the top, a brick (candy) flips out over the lip and away, and the head drops shut with a bounce. Starved (Working false: out of uranium), the cycle in hand finishes, the rotors coast down, the hopper's heap drains out of sight in 1.5 s and the amber lamp (lamp_wait) blinks at 0.9 Hz. Glow is state: the sight glass sits at 0.45 while ore runs (0.3 on low power) and flashes to full with each slug, dark while starved; the uranium itself is unlit ore. All dark until it's built; under fog, neutral and still.
- **`rotor_0`:** axis=Y, deg_per_s=450 (225 on low power), spinup_s=1.5, coast_s=3, note=rotor_1..5 the same, each a few % faster
- **`ore`:** mode=slump_drain, slump=0.015, drain=0.14
- **`mag_0`:** mode=push_up, pitch=0.12, note=mag_0..3: the stack in the stem
- **`head`:** mode=hinge_pop, open_deg=40, open_s=0.1, every_bricks=4
- **Idle:** Starved: the cascade still, the hopper empty, the amber lamp blinking.

### `composite_foundry`
*Makes stealth composite.*

- **Mechanics:** Steel and crystal in at the back, composite out at the front. One press cycle for every composite the sim makes: 2 s at full power, 4 s on low power. Each cycle a steel blank (blank) slides out of the furnace house's feed slot (press_feed) onto the press bed (press_bed) in 0.32 s; the ram drops fast, slows for the last tenth of its 0.3 stroke and clamps it; its seam glows hot and steam hisses out at both sides of the platen, and under it the blank fuses to black composite. The ram lifts, the tile slides along the rails onto the stack in the magazine (press_out) and the stack (stack) steps down a tile with it, 0.06. The grape vents (vent_0, vent_1) puff pale smoke one after the other after each stroke. The furnace's amber peephole glows while it works (at about half on low power). Out of steel or crystal the press finishes its cycle and stops with the ram up and the bed empty, the peephole cools over 1.5 s and the vents give a thin wisp every 3 to 6 s. A burning foundry gives no steam or puffs.
- **`ram`:** mode=press, stroke=0.3, down=to the blank on the bed, cycle_s=2 (4 on low power)
- **`blank`:** mode=path, path=press_feed > press_bed > press_out, material=steel until fused, then licorice
- **`stack`:** mode=step_down, step=0.06
- **Idle:** Peephole dark; a thin wisp from a vent every 3 to 6 s.

### `fusion_reactor`
*High-output power.*

- **Mechanics:** A tokamak. While it has plasma to burn, the plasma ring in the vessel (plasma) circulates at 10 to 30 rpm with the team's power load, its helical strands crawling round under the coils. Every 3.2 s with nothing drawing (1.3 s at full load) the injector fires a pellet: the magazine's spring steps the stack (pellets) up one pellet into the head in 0.16 s, a small magenta zap leaves the muzzle (port) for the ring and the plasma flares. The steam generator puffs from its vent (vent_0), faster under load. Out of plasma the ring quenches (flickers and fades over 1.5 s, coasts to a stop over 2.5 s), the steam gives a last puff and the stack sinks into the magazine's base (1.2 s), leaving the cage empty. When plasma is back the stack rises (1 s) and the central solenoid strikes the plasma: three magenta arcs 0.12 s apart from its cap into the ring, a flash, and it spins up over 2 s. Unfinished it is dark with an empty magazine, so finishing one is its first light.
- **`plasma`:** axis=Y, rpm=10–30 with load, spinup_s=2, coast_s=2.5, quench_s=1.5
- **`pellets`:** mode=step_up, pitch=0.085, step_s=0.16, shot_s=3.2–1.3 with load, empty=sinks 0.68 in 1.2 s
- **Idle:** Plasma circulates; a pellet every few seconds.

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

- **Mechanics:** A searchlight on a coil pylon. The turret (the yoke on its turntable) yaws at 180°/s to follow the sim's aim. The emitter head (the barrel node, on trunnions between the yoke's arms) pitches toward the target's live position, −30° to +40° at up to 120°/s: down at tanks, up at aircraft; after a fight it holds 3 s, then levels off at 30°/s. The shot charges in the last 0.4 s before it's due (the 1.1 s cooldown after the last shot, while it's engaged): the four pylon coils (coil_0 at the bottom .. coil_3) light one after another from the bottom, to 1.3, and the lens brightens. On the shot the cyan beam leaves the lens, the lens flashes and fades over 0.3 s, the coils drain from the top down in 0.25 s and the head's rear vent breathes out a wisp of heat. A charge that isn't fired bleeds away over 0.4 s. No recoil. The coils cast light (GlowCaster): a cyan point light (no shadows) halfway up the coils on their near side lights the cream pylon, the pad and nearby units, and an additive pool splashes the pad and the ground round it (1.2 tiles); it follows the coils' glow, 0.53–0.59 on the idle breath, rising gently to about 0.6 as the coils fill and to about 0.66 in the flash of the shot (the brightest moment), then down with the drain. Low power halves every glow and the light; an offline tower (upkeep unpaid) is dark and still and casts no light, and a tower remembered under fog casts none either. Only the 3 nearest the middle of the view get a real light (handed over with 0.35 s fades); every tower keeps its pool.
- **`turret`:** yaw_speed_deg=180, yaw_limit_deg=360, idle_scan=True
- **`barrel`:** mode=trunnion_pitch, pitch_min_deg=-30, pitch_max_deg=40, deg_per_s=120, recoil=0
- **`coil`:** mode=charge_glow, charge_s=0.4, peak=1.3, drain_s=0.25, light=GlowCaster cyan, 2.8 x glow, range 2.4, pool 1.2
- **Idle:** The head faces forward and glances now and then (PezMotion's idle turret). The coils breathe on a 2.5 s sine (glow 0.25–0.45) and the lens glows at 0.5.

**Every structure, emerge:** Rises out of its pad in stages; nothing appears instantly. stage_0 (pad) lifts from y=-0.15 to 0 during build progress 0–10%. stage_1, stage_2 and stage_3 each start at local y = -(stage height) and rise to 0 with ease-out cubic over progress 10–45%, 45–75% and 75–95%. The opaque terrain and pad hide whatever is still below ground. stage_3 ends with a 4% Y-scale overshoot that settles in 0.15 s (spring-loaded). Functional nodes (turret, spinner, door, lift) come last, at 95–100%: a turret rises 0.3 out of its housing, then a spinner spins up from 0 rpm.

**Every structure, remove:** Destroyed: shake ±0.03 for 0.4 s, then poof: a soft cream cloud bursts out of the building while it swells 8% for 0.06 s and pops to nothing in 0.14 s. A rubble decal fades in under it. Sold: dust at the footprint corners, then the same poof. Nothing sinks into the ground.

### `deep_mine`
*Pumps ore from the deep deposit under it straight into the stockpile.*

- **Mechanics:** A headframe over a shaft, run by DeepMineWorks from the sim (Working: the deposit has ore and the mine is built; the deposit's Amount / Initial; the team's LowPower). One trip of the skip every 4 s while it works (16 ore at the sim's 4/s; every 8 s on low power, which halves the rate): the skip (dm_skip) hauls a load of ore candy (dm_load) up out of the shaft (1.1 up in 1.4 s, eased at both ends), its bucket (dm_bucket, pivoted at its back bottom edge) tips 58° at the top so the ore pours down the chute into the bin (dm_heap bobs 0.02 and a puff of ore-tinted dust rises), and it goes back down in 1.3 s. The winding drum (dm_drum) and the team sheave (dm_sheave) are driven by the skip's travel through the rope (the rope's radius on each), so they spin up, slow and reverse with it; the rope below the sheave (dm_rope) stretches to the skip's eye (dm_eye). On low power the winder labours: now and then, on the way up, the skip slows to a crawl for half a second. The ventilation fan (dm_fan) turns at 540°/s while it works (270 on low power) and coasts down when it stops. The dispenser stem shows what's left: six ore bricks (dm_res_0..5, one per sixth of the deposit) on a spring (dm_spring) under its follower (dm_follower). Each time the deposit drops below a sixth the team flip-top head (dm_head, hinged at its back edge) snaps open 48° in 0.1 s, the top brick (dm_candy) rises out and flips over the front lip, the rest ride up a slot on the spring, and the head drops shut with a bounce. Dry, the trip in hand finishes and the skip parks in the shaft, the bin's heap drains out of sight in 1.5 s, the stem is empty and the head hangs open 34°. Glow is state: the amber lamp (dm_lamp) is dark while the deposit holds up, pulses at 0.5 Hz once it runs low (the sim's running-low warning, 5 minutes left; half as bright on low power) and blinks at 0.9 Hz when it's dry. The ore parts are tinted to the deposit's ore (crystal and uranium glow, as they do in the field). The neutral derrick wears the same model: no deposit, so its stem stays full and its ore is iron (it pays steel); its hoist and fan run while a team holds it, with no power needed. All dark until it's built; under fog, neutral and still.
- **`dm_skip`:** mode=hoist, rise=1.1, trip_s=4 (8 on low power), up_s=1.4, down_s=1.3
- **`dm_bucket`:** mode=tip, tip_deg=58, tip_s=0.24, hold_s=0.2
- **`dm_sheave`:** mode=rope_driven, note=turns with the skip: travel / 0.262 rad; dm_drum the same at 0.128
- **`dm_fan`:** axis=Z, deg_per_s=540 (270 on low power), coast_s=3
- **`dm_res_0`:** mode=reserve_stack, bricks=6, note=dm_res_0..5, one per sixth of the deposit; dm_spring and dm_follower under them
- **`dm_head`:** mode=hinge_pop, open_deg=48, open_s=0.1, hang_open_deg=34
- **Idle:** Working: the skip running every 4 s. Dry: the skip parked in the shaft, the bin empty, the stem empty with its head hanging open, the amber lamp blinking.

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
- **Mechanics:** The candy trooper every soldier is built on (build_models.candy_trooper): a toy on short legs in licorice boots, a smoke-plastic uniform with kraft webbing, a big cream head under the team helmet, team shoulder boards, a kraft pack; the rifle is +40% for the silhouette. Biped: it turns in place almost instantly. Walking (Gait) drives the leg_l / leg_r nodes from the hips: the stride follows the ground it really covers, the swinging leg tucks, the hips drop while both feet are planted (two bobs per stride), sway over the planted foot and lean into the walk and into turns. The turret node is the torso: it twists at the waist to aim at 400°/s, up to ±70° from the body heading before the body turns. The arms node carries both arms and the rifle, authored lowered (16° down, a low ready). With a target in range (the sim is tracking one) it brings the rifle up level in 0.18 s, holds it there 2.5 s after the last shot and lowers it over 0.45 s; the head tips to the sights, cheek on the stock. Each shot: the muzzle flash and the tracer leave from the muzzle node at the rifle's tip, the barrel (rifle) slides back 0.025, and the arms kick the muzzle up 9° and settle in about 0.2 s. From the barracks: spat out of its head tucked into a ball, it somersaults, lands, rolls to its feet and stands up at the exit, where the real unit takes over at the same size, facing the same way, and walks off to its rally point. Boarding a transport it slides in and shrinks away; unloading it reappears beside it.
- **`turret`:** yaw_speed_deg=400, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0.025, recoil_out_s=0.02, return_s=0.1, pitch_deg=0
- **Idle:** Weight shift: the hips sway 0.008 over the feet on a 3 s sine and breathe. The head glances about (a look to one side for 1.2–2.4 s, back to the front for 2–5 s, ±42°, ±18° walking); now and then (every 20–45 s) the torso turns with it, ±24°.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `rocket_soldier`
*Anti-armour, anti-air infantry.* Built at **barracks**.

- **Locomotion:** biped · max 0.9 t/s · accel 3.5 t/s² · decel 5.25 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** A candy trooper (the rifleman's base) with a kraft candy tube on the right shoulder that fires wrapped sweets: a team sweet sits in the tube's licorice mouth (the `rocket` node), a licorice venturi bell flares at the open breech (`breech`), blast goggles are pushed up on the helmet and two spare sweets stand in a kraft carton over the left shoulder blade. Same movement as the rifleman. It doesn't fire on a plain move; given a target (attack, attack-move, or one in range while idle) it stops where it stands (the sim stops it in range: no delay) and shoots as soon as its body faces it; the rocket flies at 9 tiles/s and the sim's cooldown is 2.2 s. The arms node (authored 12° down) raises the tube level in 0.18 s; from its first rocket on it aims the tube at what it's shooting: the real elevation from the shoulder to the target, about level at the ground and up at aircraft (−5° to 40°, at 160°/s; the head looks up with it, up to 12°). Each rocket: the sweet leaves the tube's mouth (the muzzle node) and flies nose first as that sweet with an orange exhaust and trail; the backblast blows out of the breech (a flash, a cone of gun smoke thrown back, dust kicked up where it reaches the ground); the tube (barrel) slides back 0.035 and the arms kick up 6°. The reload fits inside the cooldown: from 0.35 s the tube dips 22° (by 0.75 s), the next sweet is pushed up the tube into the mouth (0.85–1.25 s), and it comes back up on target by 1.75 s. It lowers the tube 2.5 s after its last rocket (0.45 s), and the next fight starts level. Barracks hand-off, boarding and unloading as the rifleman.
- **`turret`:** yaw_speed_deg=300, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0.035, recoil_out_s=0.04, return_s=0.3, pitch_deg=0
- **Idle:** As the rifleman: the weight shift, the head's glances (the tube rests on the shoulder, 12° down) and now and then a turn of the torso.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `laser_trooper`
*Elite beam infantry.* Built at **barracks**.

- **Locomotion:** biped · max 0.85 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** The candy trooper (build_models.candy_trooper, as the rifleman) in foil webbing, with a foil crest over its team helmet and a power pack on its back: two cream capacitor cans with team caps, the laser tower's charge bank carried. Its beam rifle is chunkier and shorter than the rifleman's: a cream receiver with the cyan charge cell (the rail, cell node) along the top in a foil cradle, and a cream emitter with a foil cooling ring and the cyan lens (lens node) at its tip. Same movement as the rifleman (Gait walks it; the torso twists to aim at 380°/s, up to ±70° from the body heading). With a target in range it brings the rifle up level in 0.18 s, holds it there 2.5 s after the last shot and lowers it over 0.45 s; the head tips to the sights. The charge (SoldierWorks, as LaserWorks charges the laser tower): the sim fires the moment its 1.4 s cooldown runs out, so in the last 0.25 s before the next shot is due the cell brightens from its idle breath to full and the lens with it (the first shot of an engagement fires as the charge comes up). On the shot the cyan beam (the shared laser beam, fading in 0.22 s) leaves from the muzzle node on the lens, and the damage lands at once (no projectile); the lens flashes (fading over 0.3 s) and the cell drains in 0.2 s. A charge that isn't fired (the target died or left) bleeds away over 0.4 s. No recoil: the barrel doesn't slide and the arms don't kick. From the barracks it's spat out and stands up as the rifleman does, its cell at rest; boarding a transport it slides in and shrinks away, and steps back out beside it.
- **`turret`:** yaw_speed_deg=380, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0, recoil_out_s=0.05, return_s=0.35, pitch_deg=0
- **Idle:** The cell breathes dim at 30% emission (±6%) on a 2 s sine and the lens glows at 21%; the hips sway and the head glances about as the rifleman's.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `medic`
*Heals infantry in range. Unarmed.* Built at **barracks**.

- **Locomotion:** biped · max 0.95 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** A candy trooper (build_models.candy_trooper, pose 'carry', knees=True: jointed legs, knee_l / knee_r at the knee), unarmed: a bone-white med pack wider than its shoulders with a team plus on its back and its lid (never a red cross: red would read as Cherry), a bone roundel with a team plus on the team helmet, and a heal spray in the right hand (cream canister, team end cap, steel nozzle), hanging at the hip pointing 65° down. It walks like the rifleman (Gait), the swinging leg bending at the knee. Healing is the sim's Repair order: a medic heals friendly infantry for free at 15 HP/s from up to 1.5 tiles (RepairRange), and when idle it goes to tend anyone hurt within 6 tiles. Stopped in range, the sim turns it to face its patient. Settled there 0.15 s, it kneels (Gait.Kneel, the medic's pose: the right knee goes down with its shin lying back along the ground, the left foot planted ahead with its shin upright; the body comes down about 0.085 and leans in 14°; 0.2 s, eased) and reaches out: the arms node comes up 65° in 0.18 s, so the spray points level at the patient, and the head watches the patient. Every 0.4 s, a heal pulse: the team-neutral cream-white heal beam leaves the spray's tip (its muzzle node) for the patient, cream-white motes lift off the patient, and the patient turns its head to look at its medic (unless its weapon is up). When the patient is whole or moves off, the medic lowers the spray (0.45 s) and gets up (0.3 s). From the barracks and in and out of transports it moves like the rifleman.
- **Idle:** Weight shift: the hips sway 0.008 over the feet on a 3 s sine and breathe (barely, while kneeling). The head glances about as the rifleman's does; the torso stays square (no turret yaw).
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `engineer`
*Captures buildings: an enemy one below 50% health, or a neutral derrick at any health. Unarmed; used up by the capture.* Built at **barracks**.

- **Locomotion:** biped · max 0.85 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** A candy trooper (build_models.candy_trooper, the rifleman's walk, head glances and barracks hand-off) in a team hard hat with a wide brim, a cream ridge and a lamp on its front, a big foil spanner held in both hands (the barrel node, its muzzle node at the jaw), a team tool chest on his back and a kraft tool belt with a pouch and a hammer. The arms node is authored lowered (28°: the spanner carried low). The torso (turret) has no aim: PezMotion leaves it alone. What the sim does: on a capture order he walks to the building and, the tick he's within 0.6 of its footprint, the building is his team's (any fire put out, health patched up to 50%) and he's removed: he moves in for good. He doesn't repair or do anything else. View (SoldierWorks): the lamp node's amber lens is lit while he has a capture order and eases off (0.3 s) when he hasn't; within 1.4 tiles of the building he brings the spanner up to the ready (20° above level, 0.18 s), head tipped to the job. When the sim's capture lands, his model stays on as he leaves the sim: he turns to the building's walls (0.15 s) and steps up to them (a stride or two, at most 0.6 tiles, so the jaw reaches the wall), then knocks it three times, a blow every 0.3 s: the spanner winds up to 38° above level (the torso leans back 5°, the head looks up 10°, which tips the brim clear of it) and comes down accelerating to 30° below (the torso leans into it 16°), a flash and a spit of sparks off the jaw on each blow. The third blow is the capture: the building takes his team's colours as it lands, with the capture's team beam and flash (WorldView defers its tint flip to that moment, about 0.9–1.4 s after the sim's capture). The spanner rebounds to the ready (0.25 s), and he walks into the building, shrinking to nothing over 0.35 s as he goes. Off screen, the building simply changes colours. Boarding a transport he slides in and shrinks away; unloading he steps back out.
- **Idle:** As the rifleman: weight shift and head glances; the spanner held low, the lamp off.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `sniper`
*Range 9 rifle that one-shots infantry; nearly useless against armour and buildings.* Built at **barracks**.

- **Locomotion:** biped · max 0.8 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** A candy trooper (build_models.candy_trooper, with knees) in a ghillie of shredded candy wrappers: a floppy team boonie hat with a wrapper veil, a cape of mint, kraft and foil strips and crumpled wrappers over its back, and a rifle two thirds longer than the rifleman's, with a fat licorice scope. The sim gives it one 150-damage hitscan shot every 3 s at range 9; it isn't stealthy and never goes prone. It walks like the rifleman (Gait), but on knees (knee_l / knee_r): the swinging leg bends at the knee. With a target in range and standing still it settles to shoot: down on its right knee over 0.45 s (Gait.Kneel, the sniper's pose: its shin flat behind it, the left foot planted ahead, the hips drop a third of the leg and lean 8° into the stock) as the arms bring the rifle up in 0.35 s and its eye goes to the scope; kneeling, it holds its breath (the idle sway nearly stops). It stays down while the fight lasts, and stands in 0.3 s when it moves (it walks with the rifle lowered) or 2.5 s after its last shot, the rifle lowering over 0.5 s. The scope's cyan lens (lens node) shows the next shot coming: dim just after a shot, full when the sim's cooldown runs out; with a target in range, 0.35 s before the shot is ready, the lens glints (a cyan-white flash), once a shot. Each shot: the muzzle flash and the cyan tracer leave from the muzzle node at the brake's tip, the barrel (rifle) slides back 0.04 and the arms kick the muzzle up 14°, settling over ~0.4 s. Then it works the bolt (bolt node), 0.5–1.0 s after the shot: the handle lifts 45°, runs back 0.032 (the spent round's foil wrapper flicks out to the right), runs home and drops, the rifle rocking down 3° and the head lifting off the scope while it does. From the barracks: spat out of its head like the rifleman, standing at the exit as the real unit takes over. Boarding a transport it slides in and shrinks away; unloading it steps back out of it.
- **`turret`:** yaw_speed_deg=300, yaw_limit_deg=70, idle_scan=True
- **`barrel`:** recoil=0.04, recoil_out_s=0.03, return_s=0.25, pitch_deg=0
- **Idle:** As the rifleman: the hips sway and breathe, the head glances about, now and then the torso turns with it. The long rifle rests lower, 22° down.
- **Emerge:** Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.
- **Remove:** Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.

### `commando`
*Demolitions infantry: C4 that levels structures and wrecks vehicles. Can't fight infantry.* Built at **barracks**.

- **Locomotion:** biped · max 1.05 t/s · accel 4.0 t/s² · decel 6.0 t/s² · turn 360°/s · turns in place: yes
- **Mechanics:** A candy trooper (build_models.candy_trooper, pose 'carry') with a team beret slouched over his right ear (cream badge over the left eye), a licorice bandolier with two team-wrapped charges, a kraft satchel of three candy charges on his back, a compact candy SMG at the right hip and a candy charge (bone-white brick, team wrapper, licorice cap) in his left hand: the `charge` node. Walking, Gait drives the legs as for the rifleman (one-piece legs, no knees). The sim (Defs C4: 900 damage, range 1, cooldown 5 s, arming 1.5 s, no projectile): he walks to within 0.9 tiles of the target's footprint and stops. The charge has to be planted, so he must stay there, still and on the same target, for 1.5 s (Entity.Arming) before the first goes off; the `shot` event IS the blast, then one every 5 s while it stands and he stays on it. Walking off or switching target starts the arming again. So every charge is shown planted (SoldierWorks, C4Charge): in range he drops on one knee (Gait.Kneel, the commando's pose: right foot forward 58°, the left knee down behind, hips low, leaning in 18°, over 0.3 s); half way down (and after a blast once it has cleared, 0.3 s) he reaches back to the satchel (arms +55° over 0.32 s; a fresh charge grows into his hand), swings underhand (to −80° in 0.2 s) and lets go two thirds of the way up. The charge flies to the target in a 0.42 s arc, tumbling end over end, and sticks to the target's own surface nearest him that the player's camera can see (never its plinth, a pole or the far side), splatting flat against it (squash 0.55 springing back in 0.15 s). Its amber lamp blinks, faster and faster as the time to the bang runs out (the arming left, then the cooldown: from about 3 Hz on the first charge, 1 Hz on the later ones, to 10 Hz); the blast, the scorch and the debris go off where it is on the `shot`, and he ducks his head. On a vehicle the charge rides along. A charge that couldn't land before its bang (he came back with the cooldown nearly out) goes off where it would have stuck and leaves his hand. If he walks off or his target changes, a charge left on the wall fizzles (shrinks away in 0.35 s, lamp out); he stands up and draws another. Glow means state: only a planted, armed charge has a lamp. From the barracks: spat out tucked, he lands and stands up at the exit like the rifleman. Boarding a transport he shrinks into it; unloading he steps back out.
- **`turret`:** yaw_speed_deg=400, yaw_limit_deg=70, idle_scan=True
- **Idle:** As the rifleman: the hips sway and breathe, the head glances about; the charge stays in his left hand. Working a target he keeps his eyes on it.
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
