"""Pez motion & mechanics spec. Generates pack/unity/MOTION.md, pack/unity/motion.json
and pack/unity/Scripts/PezMotionProfiles.cs. Units: tiles (= Unity units) and seconds."""
import json, os
HERE = os.path.dirname(__file__)
OUT = os.path.join(HERE, 'pack', 'unity')

# ------------------------------------------------------------------ shared rules
EMERGE_STRUCTURE = ("Rises out of its pad in stages; nothing appears instantly. stage_0 (pad) lifts from y=-0.15 to 0 "
    "during build progress 0–10%. stage_1, stage_2 and stage_3 each start at local y = -(stage height) and rise to 0 "
    "with ease-out cubic over progress 10–45%, 45–75% and 75–95%. The opaque terrain and pad hide whatever is still below ground. "
    "stage_3 ends with a 4% Y-scale overshoot that settles in 0.15 s (spring-loaded). Functional nodes (turret, spinner, door, lift) "
    "come last, at 95–100%: a turret rises 0.3 out of its housing, then a spinner spins up from 0 rpm.")
REMOVE_STRUCTURE = ("Destroyed: shake ±0.03 for 0.4 s, then poof: a soft cream cloud bursts out of the building while it swells 8% "
    "for 0.06 s and pops to nothing in 0.14 s. A rubble decal fades in under it. Sold: dust at the footprint corners, then the same poof. "
    "Nothing sinks into the ground.")
EMERGE_FROM = {
    'factory': "Spawns inside the factory, 0.6 behind its door node. The door rolls up (localScale.y 1→0.05, 0.35 s), the unit drives out along −Z at 60% speed to a point 1.5 tiles south, then takes its rally order. The door rolls down 0.5 s after the unit clears.",
    'barracks': "Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.",
    'airfield': "Spawns on the airfield lift node, starting at y=−0.6 inside the pad. The lift rises to 0 over 1.2 s (ease-out). The aircraft spins up and climbs to cruise altitude (see its entry). The lift stays up until the aircraft is 1 tile away, then drops back down.",
    'refinery': "A free mining_truck is dispensed through the refinery dock door when the refinery finishes building. It reverses out along −Z for 1.2 tiles and turns toward the nearest ore.",
}
REMOVE_UNIT = ("Destroyed: the flip-top head (turret) is blown off on a ballistic arc (up 2.5, sideways 0.8, spinning 540°/s) and fades out over 0.6 s. "
    "The hull drops 0.06 and its material darkens to 30% over 0.5 s. The wreck stays 20 s, then goes poof.")

def U(key, role, built_at, loco, speed, accel, turn, in_place, mech, nodes, idle, extra=None):
    return dict(key=key, kind='unit', role=role, built_at=built_at,
                locomotion=dict(type=loco, max_speed=speed, accel=accel, decel=accel * 1.5,
                                turn_rate_deg=turn, turn_in_place=in_place),
                emerge=EMERGE_FROM.get(built_at, ''), remove=REMOVE_UNIT, mechanics=mech, nodes=nodes, idle=idle,
                **(extra or {}))

def S(key, role, mech, nodes=None, idle='', produces=None):
    return dict(key=key, kind='structure', role=role, mechanics=mech, nodes=nodes or {}, idle=idle,
                produces=produces or [], emerge=EMERGE_STRUCTURE, remove=REMOVE_STRUCTURE)

TURRET = lambda yaw, limit=360: dict(turret=dict(yaw_speed_deg=yaw, yaw_limit_deg=limit, idle_scan=True))
def BARREL(recoil, out_s=.05, back_s=.35, pitch=0): return dict(barrel=dict(recoil=recoil, recoil_out_s=out_s, return_s=back_s, pitch_deg=pitch))

TRACKED = ("Tracked: pivot-turns in place at {t}°/s. When it has to turn more than 60° it stops and turns first, then drives. "
           "Accelerating pitches the hull nose-up 2° and braking pitches it nose-down 3° (spring, 0.25 s). On slopes the hull follows the ground normal (lerp 8/s).")
WHEELED = ("Wheeled: cannot turn in place. Minimum turn radius {r} tiles; steering follows a curve and reverses for tight corners. "
           "Body rolls up to 4° into turns and pitches 2° under acceleration.")

UNITS = [
 U('mining_truck', 'Harvests ore and returns it to a refinery or outpost.', 'factory', 'tracked', 1.6, 1.2, 110, True,
   TRACKED.format(t=110) + " The cutter's teeth always turn at 60 rpm while the engine runs. Harvest loop: drive onto an ore tile; while harvesting, ramp the spinner up to 240 rpm over 0.4 s and creep forward at 0.15 tiles/s across the tile. "
   "Each 1 s tick takes one load, and the bin's ore bricks rise in steps of 1/5. When full, the bin bricks reach max height and the truck drives to the nearest refinery dock. "
   "It reverses into the dock along +Z, the dock door rolls up, the bin tips 35° about its rear edge for 1.2 s to unload, the bin resets and the door closes.",
   dict(spinner=dict(axis='X', idle_rpm=60, active_rpm=240, spinup_s=.4),
        bin=dict(mode='fill_steps', steps=5, tip_deg=35, tip_s=1.2, note='bin visible always; ore bricks inside scale Y 0→1 with load')),
   "Spinner coasts down 240→60 rpm when harvesting stops, and to a stop when the truck parks; tiny idle hull vibration 0.005 at 12 Hz while the engine runs."),
 U('repair_truck', 'Repairs friendly vehicles and structures in range.', 'factory', 'wheeled', 2.0, 1.6, 90, False,
   WHEELED.format(r=0.8) + " Repair: parks 0.8 tiles from the target. The turret (crane base) yaws to face it at 120°/s and the barrel (crane arm) pitches down from −25° to −10°. "
   "The amber welder tip pulses 6 Hz and the welding beam effect plays. The arm sways ±3° on a 1.4 s sine while working.",
   dict(**TURRET(120), **BARREL(0, pitch=-25)), "Arm parked at −25° and centred; welder tip off."),
 U('outpost_truck', 'Drives to a site and deploys into an outpost.', 'factory', 'wheeled', 1.3, .8, 70, False,
   WHEELED.format(r=1.0) + " Deploy (2.5 s, all transforms): it must be on a clear 2×2 area. The truck stops and aligns to the grid (rotates to the nearest 90° over 0.4 s). "
   "Its body lowers 0.08 over 0.3 s as the jacks extend. The folded bunker lifts off the bed while the truck chassis sinks into the ground over 1.0 s. "
   "The outpost's stage_0 to stage_3 rise from the same spot using the structure build sequence, compressed to 1.8 s. Reverse this to undeploy.",
   {}, "Engine idle bob 0.004 at 8 Hz."),
 U('scout_buggy', 'Fast scout, light anti-infantry.', 'factory', 'wheeled', 3.6, 3.0, 160, False,
   WHEELED.format(r=0.5) + " Suspension bounce is exaggerated: body Y on a spring (k = 120, damping 10) driven by terrain noise when moving. "
   "It can fire on the move; the turret yaws independently of the body.",
   dict(**TURRET(300), **BARREL(.03, .03, .08)), "The pintle gun swings slowly ±30° when no target is in range."),
 U('light_tank', 'Fast main battle tank.', 'factory', 'tracked', 2.4, 2.0, 120, True,
   TRACKED.format(t=120) + " Fires on the move. Each shot: the barrel recoils 0.12 along −Z in 0.05 s and returns over 0.35 s, the hull kicks back 1.5° pitch, and the muzzle flash spawns at the barrel tip.",
   dict(**TURRET(150), **BARREL(.12)), "Turret scans ±40° every 3–5 s when idle."),
 U('heavy_tank', 'Slow armoured tank with twin barrels.', 'factory', 'tracked', 1.4, 1.0, 70, True,
   TRACKED.format(t=70) + " Twin barrels share one barrel node and fire as a pair, alternating left and right muzzles 0.15 s apart. "
   "Recoil is 0.16 with a 0.5 s return. The hull kicks back 2.5°. It must stop to fire when the target is more than 30° off the hull heading.",
   dict(**TURRET(80), **BARREL(.16, .05, .5)), "Turret scans ±25° every 4–6 s."),
 U('artillery', 'Long-range indirect fire. Must stop to fire.', 'factory', 'tracked', 1.5, 1.0, 80, True,
   TRACKED.format(t=80) + " Fire cycle: stop and deploy 0.6 s (hull lowers 0.04, spades plant). The turret yaws toward the target and the barrel stays at a fixed 60° elevation. "
   "On firing, the barrel recoils 0.2 along its own axis over 0.05 s and returns over 0.6 s, and the hull pitches back 3°. "
   "It must undeploy (0.4 s) before moving. Shells fly a ballistic arc with an apex at 0.35 × range.",
   dict(**TURRET(60), **BARREL(.2, .05, .6, pitch=-60)), "Barrel stays elevated; no idle scan."),
 U('long_range_artillery', 'Range-12 indirect fire. Needs a spotter. Must stop to fire.', 'factory', 'tracked', 1.3, 0.9, 70, True,
   TRACKED.format(t=70) + " Same family as artillery with a longer hull and a far longer gun (range 12). Fire cycle as artillery: stop and deploy (hull lowers 0.04; spade_l and spade_r, folded up 105° for travel, slam down in 0.25 s and bite, kicking dust at their blades; the gun, stowed for travel on the bow's travel lock at about −3°, rises to its fixed −60° over 0.6 s as it nears firing range and snaps up if a shot comes first; moving again, the spades fold and the gun stows once the turret faces forward). On firing, the barrel recoils 0.24 along its own axis over 0.05 s and returns over 0.7 s, and the hull pitches back 3°.",
   dict(**TURRET(50), **BARREL(.24, .05, .7, pitch=-60)), "Barrel stays elevated; no idle scan."),
 U('laser_tank', 'Beam tank that keeps a continuous lock on its target.', 'factory', 'tracked', 1.8, 1.4, 100, True,
   TRACKED.format(t=100) + " Laser: no recoil. Charge 0.4 s (the cyan coils brighten one after another, back to front), then a beam lasts 0.8 s while the turret keeps tracking. "
   "Cooldown is 1.2 s and the coils fade out. The barrel vibrates 0.004 at 30 Hz while the beam is on.",
   dict(**TURRET(140), **BARREL(0)), "The coils breathe at 20% emission on a 2 s sine."),
 U('gunship', 'Attack helicopter. Hovers at 2.4.', 'airfield', 'rotor', 3.0, 2.0, 140, True,
   "Rotorcraft: it can strafe and turn in place. Cruise altitude is 2.4 above the terrain (lerp 2/s). It leans up to 12° into its velocity and 8° into turns. "
   "Takeoff: on the lift, the spinner (rotor) spins up 0→600 rpm over 1.0 s, then the aircraft climbs to 2.4 over 1.5 s. "
   "The turret is a chin gun (±120°). Rocket pods fire as salvos of 4, alternating sides 0.1 s apart. Landing reverses this.",
   dict(spinner=dict(axis='Y', idle_rpm=600, active_rpm=600, spinup_s=1.0), **TURRET(220, 120), **BARREL(.02, .02, .06)),
   "Hovering: altitude bob ±0.05 on a 2.2 s sine and yaw drift ±3°."),
 U('recon_drone', 'Unarmed flying scout (sight 12). Hovers at 2.8. The long-range drone is the same model at 1.35x.', 'factory', 'rotor', 4.5, 4.0, 220, True,
   "Quadcopter: four ducted fans (spinner front left, spinner_1 front right, spinner_2 rear right, spinner_3 rear left); the diagonal pairs "
   "counter-rotate (spinner_1 and spinner_3 are authored upside down). Fans spin up 0→700 rpm over 0.6 s before it lifts off. "
   "It flies like a quadcopter, with no wings to bank with: the whole body tips toward its acceleration (nose down to set off, a roll toward "
   "the new heading when it changes direction, a rear-back to stop that ends in a small rock back to level, up to 30°), holding about 10° of "
   "forward lean at cruise. Hovering, it makes tiny ±1.5° corrections. The turret is its camera: a gimbal ball with a cyan lens, unarmed, so it only ever looks around.",
   dict(spinner=dict(axis='Y', idle_rpm=700, active_rpm=700, spinup_s=.6), **TURRET(240)),
   "Hovering: altitude bob ±0.08 on a 3.7 s sine. The camera holds forward, then glances up to ±72° and back now and then (curious)."),
 U('transport_chopper', 'Tandem-rotor transport for 6 infantry. Hovers at 2.6.', 'airfield', 'rotor', 3.5, 2.0, 120, True,
   "Rotorcraft: it can strafe and turn in place. Two three-blade rotors (spinner front, spinner_1 rear on the raised pylon) counter-rotate "
   "(spinner_1 is authored upside down); cream blade tips trace each disc. Takeoff: spins up 0→260 rpm over 1.2 s, then climbs to 2.6. "
   "Loading and unloading happen landed (the rear ramp faces the squad).",
   dict(spinner=dict(axis='Y', idle_rpm=260, active_rpm=260, spinup_s=1.2)),
   "Hovering: altitude bob ±0.08 on a 3.7 s sine."),
 U('stealth_bomber', 'High-altitude bomber. Never stops.', 'airfield', 'fixed_wing', 4.5, 1.5, 45, False,
   "Fixed wing: it must keep moving, with a minimum speed of 2.5. Its turn radius comes from the turn rate and its current speed. It banks up to 35° in turns. "
   "Takeoff: it rises on the lift, then accelerates along −Z and climbs to 3.2 over 2.5 tiles. "
   "Bombing run: it flies straight over the target and drops 6 bombs spaced 0.2 s apart, then circles back to the airfield to rearm. "
   "The team-colour edge strip pulses 0.6→1.0 emission while it is cloaked.",
   {}, "When it has no orders it loiters in a 3-tile circle at 3.2."),
 U('rifleman', 'Basic infantry.', 'barracks', 'biped', 1.1, 4.0, 360, True,
   "Biped: it turns in place almost instantly. The body bobs 0.02 at 2× its stride frequency while walking. A 4-soldier squad spreads out in a diamond 0.3 apart. "
   "The turret node (arms) aims at 400°/s and can aim ±70° from the body heading before the body turns. "
   "The barrel (rifle) recoils 0.02 per shot in 3-round bursts. When it is shot it goes prone: root lowers 0.25 and pitches 80° over 0.3 s.",
   dict(**TURRET(400, 70), **BARREL(.02, .02, .05)), "Weight shift: hips sway 0.01 on a 3 s sine; scans with the arms ±20°."),
 U('rocket_soldier', 'Anti-armour, anti-air infantry.', 'barracks', 'biped', 0.9, 3.5, 360, True,
   "Same movement as the rifleman. To fire, it stops (0.3 s), raises the launcher (barrel pitch 0 to −10° for ground targets and −45° for air), fires and recoils 0.06 back. "
   "Reloading takes 1.8 s: the launcher dips to +20° and comes back up.",
   dict(**TURRET(300, 70), **BARREL(.06, .05, .25)), "Rests the launcher on its shoulder (barrel pitch +10°)."),
 U('laser_trooper', 'Elite beam infantry.', 'barracks', 'biped', 1.0, 4.0, 360, True,
   "Same movement as the rifleman. It fires a 0.5 s beam after a 0.25 s charge, and the cyan rail brightens during the charge. No recoil.",
   dict(**TURRET(380, 70), **BARREL(0)), "The cyan rail breathes at 30% emission on a 2 s sine."),
 U('medic', 'Heals infantry in range. Unarmed.', 'barracks', 'biped', 1.2, 4.0, 360, True,
   "Same movement as the rifleman. To heal, it moves within 0.8 of the target and kneels (root lowers 0.12 over 0.2 s). The green heal beam effect plays from the pack. "
   "It stands up when the target is full or leaves range.",
   {}, "Checks around: the body yaws ±25° every 4 s."),
]

STRUCTURES = [
 S('command_center', 'HQ. Builds structures.', "Construction yard, built around a tower crane on its own footing (the tallest thing in any base). "
   "While a structure is building, the spinner (the crane's slewing top) yaws toward the site at 90°/s and swings ±5° at 0.4 Hz. "
   "The trolley runs along the jib at 0.35 tiles/s, out with the site's distance (0.45 at 2 tiles, the jib's end at 8 or more), and the hook drops 0.12 on its cable "
   "and bobs 0.02 on a 1.8 s sine. A cream construction beam runs from the hook to the site every 0.25 s, with welding sparks where it lands. "
   "When the structure is finished, the hook bounces up 0.06 and settles, and the flip-top head (the dispenser's head node, hinged at its back edge) "
   "pops open 35° in 0.12 s and drops shut with a bounce. Low power halves the crane's speed and the beam stutters. "
   "The door rolls up for a mining truck unloading at it.",
   dict(spinner=dict(axis='Y', idle_rpm=0, active_rpm=0, mode='aim_then_swing', aim_speed_deg=90),
        trolley=dict(mode='slide_along_jib', speed=.35, travel='0.45–1.55'),
        head=dict(mode='hinge_pop', open_deg=35, open_s=.12),
        dish=dict(axis='Y', deg_per_s=30),
        door=dict(mode='rollup', open_s=.35, close_s=.5)),
   "The crane idles in a slow ±10° sweep every 8 s, its trolley home and the hook swaying 0.008 on a 3 s sine. The comms dish turns at 30°/s (15 on low power). "
   "The amber beacon on the crane's apex blinks once every 1.5 s.", ['structures']),
 S('outpost', 'Forward bunker and ore drop-off.', "Created only by deploying an outpost_truck (see that entry). Mining trucks unload on the south drop pad: they reverse onto it and tip their bin. No door.",
   {}, "Antenna tip blinks amber once every 2 s."),
 S('power_plant', 'Generates power.', "A spring under load. The cyan cores brighten and dim with power load (emission 0.6–1.0) and steam rises from both stacks (stack_0, stack_1), "
   "faster under load. The cream springs round the towers (coil_0, coil_1, pivots at their feet) are pressed down by their collars as the base draws more: "
   "full height with nothing drawing, 78% at full load, eased over 0.4 s. The flywheel on the generator house turns with the load, 40 to 140 rpm. "
   "Electric arcs jump between the stacks' lips (and one in four down to an insulator on the transformer, insulator_N): every 5 to 8 s with nothing drawing, "
   "every 1.5 to 2.5 s at full load. When power is low the cores flicker at 4 Hz, the steam sputters, the springs bottom out at 70% and chatter at 4 Hz, "
   "the flywheel surges and stalls, and the arcs come in stuttering bursts of two or three.",
   dict(coil=dict(mode='scale_y_with_load', full=1.0, loaded=.78, low=.7),
        flywheel=dict(axis='X', rpm='40–140 with load')),
   "Cores pulse on a 3 s sine."),
 S('mining_refinery', 'Turns ore into stockpile.', "The dock door rolls up when a mining truck reverses in and closes after it leaves. "
   "While refining, lumps of iron and copper ore ride the belt over the dock from the intake hood to the smelter hood at 0.5 tiles/s, "
   "flattened as they pass under the foil crusher roll (the spinner), which turns with the belt. Both smokestacks puff pale smoke from "
   "their mouths (stack_0, stack_1), each about every 0.3 s. Out of ore, the belt runs on until it's empty and stops, the roll spins "
   "down and the stacks give a thin wisp every few seconds. Low power halves the belt, the roll and the smoke. "
   "Dispenses one mining_truck through the dock when it finishes building.",
   dict(door=dict(mode='rollup', open_s=.35, close_s=.5), spinner=dict(axis='Z', idle_rpm=0, active_rpm=32, spinup_s=.5)), "Dock lights blink alternately at 1 Hz while a truck is docking.", ['mining_truck (free)']),
 S('barracks', 'Trains infantry.', "A soldier dispenser, driven by the sim's training queue (BarracksWorks). The clear case under the flip-top head holds four soldiers stacked like candy: "
   "raw dough at the bottom, primed in the middle, finished with a team helmet at the top. While a soldier trains the stack (stack_lo to stack_hi) rises with its progress, "
   "each figure baking as it climbs, a fresh lump of dough swells in at the bottom, and the amber oven strip (oven) at the case's foot glows. "
   "1.5 s before the soldier is out, the top figure rises out of the case into the head; at 1.25 s the head snaps open, and at 1.1 s it spits the soldier out of its mouth (pop), "
   "full size and tucked into a ball (0.65 of its height). It somersaults 540° on a ballistic arc to the ground in front (land) in 0.55 s, kicking up a puff of dust, "
   "rolls on to its feet (exit) through another 180° with a squash, and stands up as the real unit appears there. The head drops shut. No door.",
   dict(head=dict(mode='flip_top', open_s='1.25 s before the soldier is out', close_s='after the spit'),
        stack=dict(mode='rise_with_progress', slots=4), soldier=dict(mode='spit_tuck_roll', spit_s=.55, roll_s=.35, spin_deg=720, tuck=.65)),
   "The flag on the pole (flag) is cloth: it streams downwind and ripples, harder in a stronger wind.", ['rifleman', 'rocket_soldier', 'laser_trooper', 'medic']),
 S('factory', 'Builds vehicles.', "While building, the team wrapper band pulses brighter by 10%. Each finished vehicle comes out through the roll-up door. The door takes 0.35 s to open.",
   dict(door=dict(mode='rollup', open_s=.35, close_s=.5)), "Exhaust stack puffs every 3 s.", ['mining_truck', 'repair_truck', 'outpost_truck', 'scout_buggy', 'light_tank', 'heavy_tank', 'artillery', 'laser_tank']),
 S('electronics_plant', 'Turns copper and steel into circuits.', "A circuit-candy line, one cycle per circuit the sim makes (1 s; 2 s on low power, which halves production). "
   "Each cycle the pick-and-place carriage (gantry, a little team head) dips its nozzle at the copper feeder, carries the chip (held) along its bridge, pecks it onto the board "
   "under it (chip_N appears, with a solder glint) and runs home; then the belt indexes one slot in 0.3 s: a blank drops out of the magazine, the boards (board_N) move on "
   "through the reflow oven, whose vent (fume) puffs a wisp of solder fumes, and the last goes into the dispenser stem, where the finished boards stack up behind its window (mag_N). "
   "Every fifth board the team flip-top head (head, hinged at its back edge) pops open 40° in 0.1 s, the stack empties from the top, a board (candy) flips out over the lip and away, "
   "and the head drops shut with a bounce. The copper reel (reel) turns at 40°/s while the line runs; the roof fan turns at 90 rpm while the plant has power (45 on low power). "
   "Starved (Working false: not enough copper or steel), the line finishes the board in hand and parks, and what ran out shows: the reel's copper (winding) runs down to the hub, "
   "the magazine's blanks are gone. Glow is state: the clean-room panes and skylight (panes) are lit while it runs, at half on low power, and stand by at 0.22 while it's starved; "
   "the signal tower's acid lamp (lamp_run) is steady while it runs and blinks at 2 Hz on low power; its amber lamp (lamp_wait) blinks at 0.9 Hz while it waits for inputs. "
   "All dark until it's built; under fog, neutral and still.",
   dict(gantry=dict(mode='shuttle_z', cycle_s='1 (2 on low power)'),
        nozzle=dict(mode='dip', pick_and_place=True),
        head=dict(mode='hinge_pop', open_deg=40, open_s=.1, every_boards=5),
        reel=dict(axis='Z', deg_per_s=40),
        fan=dict(axis='Y', rpm='90 (45 on low power)')),
   "Starved: parked, panes at standby, the amber lamp blinking; the fan still turns while it has power."),
 S('optics_lab', 'Turns crystal into lenses.', "An observatory that grinds lenses, driven by OpticsWorks from the sim's Working (it had crystal to grind) and its team's power. "
   "While it works, the team dome (head) tracks the sky at 3°/s and every 9 to 14 s slews 40–120° to a new star at 25°/s, the telescope (scope) nodding to a new height (−12° to +8°) at 12°/s. The dome turns only through the 260° arc that keeps the telescope 50° clear of the dispenser's bearing either side (it reaches the dispenser within 29°), reversing at the arc's ends, and the telescope looks down (parks) only at home, so the barrel never passes through the dispenser, the hopper or the bench. "
   "The light (light, a cyan rod from the collimator on the drum) reaches out to the glass prism in 0.2 s, then the prism lights and fans it into three rays (rays) onto the lens blanks "
   "on the turntable (lathe), which turns at 80 rpm. Each lens finished (0.5 a second, the recipe's rate) pops out of the dispenser on the left, a Pez stem of lens candies: "
   "its team head (cap, hinged at the back) snaps open 40° in 0.12 s, the top lens (candy) rises 0.1 into the throat with a cyan glint and slips out over the lip, "
   "then the head drops shut with a bounce while the stack (stack) pushes the next lens up 0.12 into its place (1.1 s in all). "
   "Out of crystal, the rays and then the light go out, the turntable coasts to a stop, the dome turns home at 20°/s the way it came and the telescope parks, pointing down, its objective lens dim. "
   "On low power everything runs at half speed (a lens every 4 s), the dome turns in jerks, and the light and rays brown out: a 4 Hz flicker with a dropout every 2.5 s. "
   "A lab remembered under fog shows dark and still.",
   dict(head=dict(axis='Y', track_deg_per_s=3, slew_deg_per_s=25, slew_every_s='9–14', arc_deg=260, clear_of_dispenser_deg=50),
        scope=dict(axis='X', nod_deg='−12 to +8', parked_deg=40),
        light=dict(mode='scale_on_off', s=.2), rays=dict(mode='scale_on_off', s=.2),
        lathe=dict(axis='Y', active_rpm=80, idle_rpm=0),
        cap=dict(mode='hinge_pop', open_deg=40, open_s=.12, every_s=2),
        stack=dict(mode='push_up', step=.12)),
   "Dome home, telescope parked, light out, turntable still."),
 S('enrichment_plant', 'Turns uranium into plasma feedstock.', "Each centrifuge's acid bands chase upward (emission scrolls) while producing. Ambient centrifuge vibration is 0.003 at 25 Hz.",
   {}, "Bands breathe slowly."),
 S('composite_foundry', 'Makes stealth composite.', "Steel and crystal in at the back, composite out at the front. One press cycle for every composite the sim makes: "
   "2 s at full power, 4 s on low power. Each cycle a steel blank (blank) slides out of the furnace house's feed slot (press_feed) onto the press bed "
   "(press_bed) in 0.32 s; the ram drops fast, slows for the last tenth of its 0.3 stroke and clamps it; its seam glows hot and steam hisses out at both "
   "sides of the platen, and under it the blank fuses to black composite. The ram lifts, the tile slides along the rails onto the stack in the magazine "
   "(press_out) and the stack (stack) steps down a tile with it, 0.06. The grape vents (vent_0, vent_1) puff pale smoke one after the other after each stroke. "
   "The furnace's amber peephole glows while it works (at about half on low power). Out of steel or crystal the press finishes its cycle and stops with "
   "the ram up and the bed empty, the peephole cools over 1.5 s and the vents give a thin wisp every 3 to 6 s. A burning foundry gives no steam or puffs.",
   dict(ram=dict(mode='press', stroke=.3, down='to the blank on the bed', cycle_s='2 (4 on low power)'),
        blank=dict(mode='path', path='press_feed > press_bed > press_out', material='steel until fused, then licorice'),
        stack=dict(mode='step_down', step=.06)),
   "Peephole dark; a thin wisp from a vent every 3 to 6 s."),
 S('fusion_reactor', 'High-output power.', "A tokamak. While it has plasma to burn, the plasma ring in the vessel (plasma) circulates at 10 to 30 rpm with the "
   "team's power load, its helical strands crawling round under the coils. Every 3.2 s with nothing drawing (1.3 s at full load) the injector fires a pellet: "
   "the magazine's spring steps the stack (pellets) up one pellet into the head in 0.16 s, a small magenta zap leaves the muzzle (port) for the ring and the plasma flares. "
   "The steam generator puffs from its vent (vent_0), faster under load. Out of plasma the ring quenches (flickers and fades over 1.5 s, coasts to a stop over 2.5 s), "
   "the steam gives a last puff and the stack sinks into the magazine's base (1.2 s), leaving the cage empty. When plasma is back the stack rises (1 s) "
   "and the central solenoid strikes the plasma: three magenta arcs 0.12 s apart from its cap into the ring, a flash, and it spins up over 2 s. "
   "Unfinished it is dark with an empty magazine, so finishing one is its first light.",
   dict(plasma=dict(axis='Y', rpm='10–30 with load', spinup_s=2, coast_s=2.5, quench_s=1.5),
        pellets=dict(mode='step_up', pitch=.085, step_s=.16, shot_s='3.2–1.3 with load', empty='sinks 0.68 in 1.2 s')),
   "Plasma circulates; a pellet every few seconds."),
 S('airfield', 'Builds and rearms aircraft.', "The lift (deck flush in a collar with the team landing ring, in the middle of the runway) is where new and landing aircraft are. "
   "While an aircraft is in this airfield's queue the approach lights (approach_0..4: four amber chevrons pointing −Z and a threshold bar) glow a steady low amber "
   "and the tower radar (radar) turns at 120°/s (40°/s idle, eased between). 1.5 s before the aircraft is done the lift goes down its well (−0.6 in 1.2 s, eased); "
   "when it's done the aircraft stands on the deck and rides it up (0.7 s, eased), then lifts off with its usual spawn ease. If the lift wasn't sent down "
   "(another airfield's queue, a fogged view) the aircraft just lifts off the deck. From 2 s before until 1.5 s after a launch the approach lights chase toward −Z "
   "(a sweep every 0.5 s) and the tower beacon blinks at 2 Hz. Low power: the lift and radar run at half speed, the chase sweeps once a second, every glow is halved "
   "and the beacon blinks every 3 s. The windsock (windsock) turns about its pole to point downwind and droops as the wind drops.",
   dict(lift=dict(down_y=-.6, lower_s=1.2, rise_s=.7, lead_s=1.5, ease='smoothstep'), radar=dict(axis='Y', idle_deg_s=40, producing_deg_s=120),
        approach=dict(chase='−Z', sweep_s=.5, from_s=-2.0, to_s=1.5), windsock=dict(mode='downwind', droop_deg='65 calm to 10 in wind')),
   "The lights are dark, the radar turns at 40°/s, the tower beacon blinks once every 1.5 s and the windsock follows the wind.", ['gunship', 'transport_chopper', 'stealth_bomber']),
 S('radar_dome', 'Sees 16 tiles, finds stealth units in that range and warns of enemy aircraft out to 28.',
   "The search antenna on top of the dome (the spinner; its beam looks toward the feed marker on the horn) is driven by RadarWorks, not by its rpm: "
   "while the base has power it sweeps clockwise at 12 rpm. When a new contact comes into this dome's range (an enemy aircraft on radar within 28 tiles, "
   "or a stealth unit it has found within 16) it breaks off and swings onto it (up to 180°/s, eased), follows it for 1 s, then spins back up into the sweep over 0.5 s. "
   "On low power (the sim switches the air search off; the dome still finds stealth) it coasts to a stop, and every 2.5 to 4.5 s the motor catches and throws it "
   "20–30° round before it stalls again. Still while building.",
   dict(spinner=dict(axis='Y', idle_rpm=0, active_rpm=0, mode='sweep', sweep_rpm=12, slew_deg_s=180, hold_s=1)),
   "The amber beacon on the antenna blinks once a second while it sweeps, double-flashes while the dome holds contacts, and is dark on low power."),
 S('gun_turret', 'Ground defence cannon.', "A revolver cannon under a team flip-top head. The turret has 360° of rotation and follows the sim's aim "
   "(at least 240°/s in the view, so the shot leaves along the barrel); the sim fires within 20° of the target, every 1.3 s. Shots and flashes leave "
   "from the brick muzzle at the barrel's far end. On each shot the barrel recoils 0.1 into its sleeve over 0.05 s and returns over 0.3 s, pressing "
   "the recoil spring round it against the sleeve (spring, scaled along the barrel to the gap the collar leaves). The drum (six candy-brick rounds) "
   "indexes one chamber, 60°, starting 0.05 s after the shot and taking 0.2 s, turning the fired chamber toward the ejection side (the eject marker) and "
   "the next round up to the barrel; halfway through, the spent round's foil wrapper flicks out of that side and tumbles to the ground. "
   "Low power doesn't slow it.",
   dict(**TURRET(120), **BARREL(.1, .05, .3), drum=dict(axis='Z', index_deg=60, delay_s=.05, index_s=.2),
        spring=dict(mode='scale_y_with_recoil')),
   "Holds its aim 3 s after a fight, settles forward, and now and then (every 20–45 s) glances up to 24° to one side for 2–3 s."),
 S('sam_site', 'Anti-air missile launcher.', "A twin-pod launcher on a jacked turntable. The turret yaws at 200°/s to the sim's aim. The rack (barrel: the elevation axle "
   "with a two-tube pod on each end) rests 25° nose-up; while the site is engaging it raises to the air target it faces, 10° to 70° (eased, up to 140°/s), "
   "holds that 3 s after the last launch, then settles back. The search radar (radar) turns at 20 rpm (10 on low power); on contact it carries on round to "
   "face forward and locks there. Each launch (one per sim shot, every 1.6 s) empties the next loaded tube, the pods taking turns, top tubes first: "
   "its round (missile_N, pivot at the mouth, team nose out) drops out of sight, the shot and its flash leave from that mouth with a cream smoke trail, "
   "smoke blows out of the pod's back, a dust ring runs out on the ground and the rack bucks 3° (nose up from a top tube, down from a bottom one). No recoil. "
   "2.4 s later a new round slides up into the tube over 0.45 s. Offline (upkeep unpaid) the rack lowers to 4° and the radar stops.",
   dict(**TURRET(200), barrel=dict(recoil=0, pitch_deg=-25, pitch_range='10–70° nose-up toward the target', kick_deg=3.2),
        radar=dict(axis='Y', rpm=20, low_power_rpm=10, lock='faces forward while tracking'),
        missile=dict(count=4, hidden_z=-.16, reload_s=2.4, slide_s=.45)),
   "The rack rests at 25° nose-up and the radar sweeps round at 20 rpm; the turret glances aside now and then."),
 S('laser_tower', 'Beam defence.', "A searchlight on a coil pylon. The turret (the yoke on its turntable) yaws at 180°/s to follow the sim's aim. "
   "The emitter head (the barrel node, on trunnions between the yoke's arms) pitches toward the target's live position, −30° to +40° at up to 120°/s: "
   "down at tanks, up at aircraft; after a fight it holds 3 s, then levels off at 30°/s. "
   "The shot charges in the last 0.4 s before it's due (the 1.1 s cooldown after the last shot, while it's engaged): the four pylon coils "
   "(coil_0 at the bottom .. coil_3) light one after another from the bottom, to 1.3, and the lens brightens. On the shot the cyan beam leaves the lens, "
   "the lens flashes and fades over 0.3 s, the coils drain from the top down in 0.25 s and the head's rear vent breathes out a wisp of heat. "
   "A charge that isn't fired bleeds away over 0.4 s. No recoil. Low power halves every glow; an offline tower (upkeep unpaid) is dark and still.",
   dict(**TURRET(180), barrel=dict(mode='trunnion_pitch', pitch_min_deg=-30, pitch_max_deg=40, deg_per_s=120, recoil=0),
        coil=dict(mode='charge_glow', charge_s=.4, peak=1.3, drain_s=.25)),
   "The head faces forward and glances now and then (PezMotion's idle turret). The coils breathe on a 2.5 s sine (glow 0.25–0.45) and the lens glows at 0.5."),
]

ORES = [dict(key=k, kind='ore', role='Resource field (one tile).', mechanics=(
   "Five cluster nodes per tile. Mined: each cluster's scale equals its remaining fraction (min 0.25). When a cluster empties, it sinks 0.2 over 1.0 s and is then disabled. "
   "Regrowth: an empty cluster rises from y=−0.2 and scales 0.1 to 1 over 6 s (ease-out); nothing appears at full size." + glow),
   nodes=dict(clusters=dict(names=[f'cluster_{i}' for i in range(5)])), emerge="Grows from the ground (see mechanics).", remove="Sinks when empty.")
   for k, glow in (('iron_ore', ''), ('copper_ore', ''), ('crystal', " Shards glint: emission pulses per cluster with a random phase."), ('uranium', " Rods pulse with emission 0.7–1.0 on a 1.5 s sine; heat shimmer above them."))]

ALL = STRUCTURES + UNITS + ORES

def md():
    L = ["# Pez — motion and mechanics spec (Unity)", "",
         "Units are tiles (1 tile = 1 Unity unit) and seconds. Rotation speeds are in degrees per second, spinners in rpm. "
         "Node names match the .glb files. `PezMotion.cs` drives the named nodes and `PezEmerge.cs` runs the emerge and remove transforms.", "",
         "## Global rules", "",
         "- **Nothing pops in or out.** Every spawn, build, deploy, regrowth, sale and death is a transform over time; nothing is toggled with SetActive mid-frame.",
         "- **Structures** rise out of their pad in four stages (`stage_0`…`stage_3`) as build progress advances, then their functional nodes unfold.",
         "- **Units** come out of the building that produced them: a roll-up `door` on the factory, barracks and refinery, and a rising `lift` on the airfield.",
         "- **Recoil** always moves along the barrel node's local −Z, so a pitched barrel (artillery, SAM) recoils along its own axis.",
         "- **Turret aim:** yaw the `turret` node toward the target at `yaw_speed_deg` with no overshoot; fire when the error is within 5°.",
         "- **Hull feel:** acceleration pitch and braking dip are springs on a visual child, never on the gameplay transform.",
         "- **Snappy combat:** idle to firing within 3 frames. Turret slew and laser charge are deliberate telegraphs; the shot itself is instant. Recoil out is at most 0.05 s.",
         "- **Ambient motion runs in vertex shaders:** flags, antennas, the crane hook, trees and hover bob are not bones or nodes. Use a height-masked sine on the vertex shader.",
         "- **Glow shows state:** charge, load, low power and cooldown. Glow is never decoration.",
         "- **Rigs:** vehicles and structures use rigid named nodes. Infantry use at most 20 bones, with the weapon rigid on `barrel`.", ""]
    def sec(title, items):
        L.extend([f"## {title}", ""])
        for a in items:
            L.append(f"### `{a['key']}`")
            L.append(f"*{a['role']}*" + (f" Built at **{a['built_at']}**." if a.get('built_at') else '') +
                     (f" Produces: {', '.join(a['produces'])}." if a.get('produces') else ''))
            L.append("")
            if 'locomotion' in a:
                lo = a['locomotion']
                L.append(f"- **Locomotion:** {lo['type']} · max {lo['max_speed']} t/s · accel {lo['accel']} t/s² · decel {lo['decel']} t/s² · turn {lo['turn_rate_deg']}°/s · turns in place: {'yes' if lo['turn_in_place'] else 'no'}")
            L.append(f"- **Mechanics:** {a['mechanics']}")
            for n, v in a['nodes'].items():
                L.append(f"- **`{n}`:** " + ", ".join(f"{k}={vv}" for k, vv in v.items()))
            if a.get('idle'): L.append(f"- **Idle:** {a['idle']}")
            if a['kind'] != 'structure':
                L.append(f"- **Emerge:** {a['emerge']}")
                L.append(f"- **Remove:** {a['remove']}")
            L.append("")
        if items and items[0]['kind'] == 'structure':
            L.extend(["**Every structure, emerge:** " + EMERGE_STRUCTURE, "", "**Every structure, remove:** " + REMOVE_STRUCTURE, ""])
    sec("Structures", STRUCTURES); sec("Units", UNITS); sec("Ores", ORES)
    return "\n".join(L)

def cs():
    rows = []
    for a in STRUCTURES + UNITS:
        n = a['nodes']; t = n.get('turret', {}); b = n.get('barrel', {}); s = n.get('spinner', {}); lo = a.get('locomotion', {})
        rows.append('            {"%s", new PezMotionProfile { turretYawSpeed=%sf, turretYawLimit=%sf, recoil=%sf, recoilOut=%sf, recoilReturn=%sf, barrelPitch=%sf, spinnerAxis=\'%s\', spinnerIdleRpm=%sf, spinnerActiveRpm=%sf, spinupTime=%sf, maxSpeed=%sf, accel=%sf, turnRate=%sf, turnInPlace=%s }},' % (
            a['key'], t.get('yaw_speed_deg', 0), t.get('yaw_limit_deg', 360), b.get('recoil', 0), b.get('recoil_out_s', .06), b.get('return_s', .35),
            b.get('pitch_deg', 0), s.get('axis', 'Y'), s.get('idle_rpm', 0), s.get('active_rpm', 0), s.get('spinup_s', .5),
            lo.get('max_speed', 0), lo.get('accel', 0), lo.get('turn_rate_deg', 0), 'true' if lo.get('turn_in_place') else 'false'))
    return """// Generated from motion.py. Values: tiles, seconds, degrees, rpm.
using System.Collections.Generic;

namespace Pez
{
    [System.Serializable]
    public class PezMotionProfile
    {
        public float turretYawSpeed, turretYawLimit = 360f, recoil, recoilOut = 0.06f, recoilReturn = 0.35f, barrelPitch;
        public char spinnerAxis = 'Y';
        public float spinnerIdleRpm, spinnerActiveRpm, spinupTime = 0.5f;
        public float maxSpeed, accel, turnRate;
        public bool turnInPlace;
    }

    public static class PezMotionProfiles
    {
        public static readonly Dictionary<string, PezMotionProfile> All = new Dictionary<string, PezMotionProfile>
        {
%s
        };

        public static PezMotionProfile Get(string key) =>
            All.TryGetValue(key, out var p) ? p : new PezMotionProfile();
    }
}
""" % "\n".join(rows)

if __name__ == '__main__':
    os.makedirs(os.path.join(OUT, 'Scripts'), exist_ok=True)
    open(os.path.join(OUT, 'MOTION.md'), 'w').write(md())
    json.dump(ALL, open(os.path.join(OUT, 'motion.json'), 'w'), indent=1)
    open(os.path.join(OUT, 'Scripts', 'PezMotionProfiles.cs'), 'w').write(cs())
    print(len(STRUCTURES), len(UNITS), len(ORES))
