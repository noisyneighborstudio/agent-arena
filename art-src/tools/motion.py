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
    'command_center': "A newly trained mining truck rolls out of the command center's door using WorldView's shared trained-event hand-off: the door opens on the event and remains open for 1.4 s; the truck begins 0.6 inside it and eases from rest to the simulation's spawn position over 0.9 s (smoothstep), down the plinth's driveway ramp, then follows its harvest order. The free starting truck is placed beside the command center at match creation.",
    'factory': "Rolls out of the factory's door with anticipation, from the sim's production queue. In the last 1.5 s of its build the factory's amber lamps blink at 2 Hz; in the last 0.8 s three steam puffs vent 0.2 s apart; the door rolls up (localScale.y 1→0.05, 0.35 s; slower on low power) so it's fully open 0.1 s before the unit appears, and the flip-top head over it lifts with the door. The unit starts 0.6 inside the door, in the dark bay, and eases out from rest to the position the sim gave it (smoothstep over 0.9 s), down the plinth's driveway ramp: it never pops in at its sim position. The door closes 1.4 s later (the head drops back with a bounce), and the unit takes its rally order.",
    'barracks': "Spawns 0.4 behind the barracks door. The door rolls up (0.25 s) and the soldier walks out along −Z for 1.0 tile, then takes its rally order. The door closes after the last soldier in the batch.",
    'airfield': "Spawns on the airfield lift node, starting at y=−0.6 inside the pad. The lift rises to 0 over 1.2 s (ease-out). The aircraft spins up and climbs to cruise altitude (see its entry). The lift stays up until the aircraft is 1 tile away, then drops back down.",
    'refinery': "A free mining_truck is dispensed through the refinery dock door when the refinery finishes building. It reverses out along −Z for 1.2 tiles and turns toward the nearest ore.",
}
REMOVE_UNIT = ("Destroyed vehicle: a blast and a cook-off, debris in its colours, a scorch and 12 s of smoke. The flip-top head (turret) is "
    "blown off on a ballistic arc (up 2.5, about 1 sideways, tumbling 540°/s), bounces once and lands beside the hull, cocked 8–22° on its "
    "base, and stays with the wreck: it doesn't fade. Every part, head included, darkens to about 30% as seen over 0.5 s while the hull "
    "settles 0.06. The wreck lies 20 s, then goes poof (it swells 8% for 0.06 s and pops to nothing in 0.14 s inside a cream cloud). "
    "A soldier goes poof where it falls; an aircraft falls burning and blows up where it hits the ground. Removed any other way, just the poof.")

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

# The candy chassis (build_models.py candy_chassis; VehicleWorks.cs): what every vehicle built on it does with no code of its own.
CHASSIS_SIM = ("The sim turns every vehicle at 286°/s: more than 34° off its way it pivots on the spot first, otherwise it drives "
    "where it faces, curving round (Rules 42); it starts and stops at full speed, so the view's springs carry the mass. ")
CHASSIS_HULL = ("The sprung `hull` (the running gear stays on the ground) pitches nose-up {up}° pulling away and {down}° nose-down braking "
    "(a spring of period {period} s, damping {damp}; the dip starts 0.3 s before the sim stops it), each shot kicks it {kick}° nose-up, "
    "and it rolls up to {roll}° out of a curve. The whole vehicle (the rig's body, never the gameplay root) tips to the slope under it "
    "on a plinth's driveway ramp, eased at 8/s. Dust puffs off the tail on the move (every 0.3 s slow to 0.1 s fast) and scrubs off the bow "
    "and stern while it pivots. The `exhaust` stack coughs three puffs 0.12 s apart pulling away, and breathes a wisp every 3–5 s parked.")
CHASSIS_NODES = {
    'gear': "the unsprung running gear, static on the ground",
    'hull': "the sprung body: VehicleWorks pitches, rolls (and on wheels heaves) it about its suspension pivot",
    'exhaust': "an empty at the stack's mouth: the puffs leave from it",
    'slot_bed`, `slot_crane`, `slot_dish`, `slot_pod`, `slot_stack`, `slot_lights': "empty mounts on the hull for other vehicles' kit (build_models.py candy_chassis)",
    'crown`, `cheek_l`, `cheek_r': "empty mounts on the head that turn with it",
}

# The artillery family's act (ArtilleryWorks.cs, ticked by VehicleWorks): what artillery and long-range artillery share.
ARTILLERY_ACT = ("Travel (the model's rest pose): the head shut, the gun level in a licorice fork on a steel roller at the top of the "
    "glacis (the travel lock), the spades folded back nearly flat behind the stern (they read from above as a forked tail). The "
    "dispenser act is the gun's laying: the hollow team head is hinged at its back bottom edge (`cradle`) and tips back like a Pez "
    "dispenser's to elevate the gun in its mouth, uncovering the dispenser's stem top on the turret: a cream plate with a team cradle "
    "and the next wrapped candy round (`arty_round`, the same candy as on the rack) lying in it. The elevation is the shell's real "
    "launch angle: across the ground the shell flies at 8 tiles/s and its arc falls under one gravity (16 tiles/s²), so its apex over D "
    "tiles is D²/32 (2 at 8 tiles, 3.8 at 11, 4.5 at 12) and the gun lays to atan(D/8 + rise/D): about 45° at 8 tiles, 54° at 11, 56° "
    "at 12 (at least 14°, at most 62°). The head tips back at 90°/s and settles at 70°/s. It travels with the gun in its lock until it's "
    "about to stop to fire (its target within Speed × 0.6 tiles of 95% of its range), when the head tips back to lay the gun. Stopped "
    "and engaged, it deploys: the spades (`spade_l`, `spade_r`, on a bracket under the tail bar) swing down 55° in the first 0.25 s "
    "(ease-in) and bite, kicking dust off each blade, and the body squats 0.04 over 0.4 s. Each reload (in the last 0.6 s of the 3.5 s "
    "cooldown) the barrel rams (slides back 0.06 and returns, 0.4 s, ending before the earliest shot) as the candy round is pushed up "
    "out of the cradle into the open breech (0.3 s); it stays chambered, inside the tube, through the shot, and 0.8 s after the shot "
    "(the recoil over) the next round rises out of the stem into the cradle (0.35 s). A shot never waits on the view: the frame it fires "
    "the gun snaps to its lay, the round goes up into the breech at once if it hadn't been rammed, and, if it hadn't dug in yet, the "
    "spades plant. The head only turns with the gun lifted at least 14° (from there it clears the deck, the rack, the fork and the spades "
    "all round), and below that the gun only lies forward: it lifts out of the lock before the head turns, and lowers back into it once "
    "the head faces forward again. Moving again, the spades fold (0.25 s). A radio whip with a team tip stands on the turret beside the "
    "hinge (sight 7: it fires at full range on what a spotter sees; it turns with the head, never under the gun). ")
ARTILLERY_NODES = {
    'cradle': "the team head's hinge at its back bottom edge, under `turret`: ArtilleryWorks tips it back (−x) to the gun's lay; 0 is shut, the gun level in its lock",
    'cradle_head': "the hollow head itself (four walls open underneath, its loaf crown over a dark ceiling, eyes, mouth, mantlet and recuperators) on the hinge; static in it",
    'arty_round': "the candy round, under `turret`: rests in the stem's cradle; ArtilleryWorks rams it up into the breech, keeps it chambered inside the tube until the shot is over, and raises the next one out of the stem",
    'spade_l`, `spade_r': "hinged on the pin under the tail bar, splayed 14° out; rest pose folded back 10° above flat for travel; ArtilleryWorks swings them down 55° to plant",
}

UNITS = [
 U('mining_truck', 'Mines ore (150 a trip, one type at a time) and unloads it at the nearest drop-off.', 'command_center', 'tracked', 1.8, 1.4, 286, True,
   "Candy chassis on tracks, a work truck: the shared work cab up front (a cream cab with a dark windscreen, the team flip-top head as "
   "its cap, an amber work beacon on the crown), a deep flared hopper riding high on its subframe behind it, and a toothed cutter drum "
   "on two arms at the bow, exposed ahead of a short cream mudguard. " + CHASSIS_SIM +
   "The treads, sprockets and idlers run with each side's travel as on the light tank (capped at 1.2 tiles/s). "
   + CHASSIS_HULL.format(up=2, down=3, period=.3, damp=.5, kick=1.5, roll=1.5) +
   " Mining: the sim parks it on an ore tile and takes 4 ore every 0.1 s (150 fills it in about 4 s). While it's parked mining, the "
   "cutter drum (`spinner`, about X) runs up from 60 to 240 rpm over 0.4 s, ore chips spit off the drum about three times a second "
   "while the cargo rises with a puff of ore-tinted dust every 0.5 s, and the cab's beacon flashes (1.5 Hz, fading in over 0.3 s) and "
   "throws a small amber pool on the ground. The load (`bin_ore`) rises in steps of a fifth (2/s), heaping above the rim, in the ore's "
   "colour as an irregular pile of its own shapes (`mt_ore_0`..`3`: iron slabs, copper nuggets, standing crystal shards, crossing uranium rods; "
   "crystal and uranium glow, with dark crevices between pieces); "
   "laden, the body squats 0.03. Unloading at a drop-off's bay (DockView, from the sim's dock steps): it turns on the spot at the head "
   "of the lane (Align), backs in (Reverse), and in Unload's 0.3 s settle the hopper (`bin`) tips 35° about its rear hinge, the "
   "dock's chute out under its tail spout (Rules 44 chooses the clear bay nearest the ore; the drop-off turns its door toward that side); every 10 ore that leaves "
   "pours an ore brick off the spout into the bay with a tinted puff every 0.25 s; in PullOut's first 0.25 s the hopper slams down "
   "(a 1.5° hull bounce) and one last brick pops out. Beside a drop-off whose lane is built over it tips once in place (1.2 s).",
   dict(spinner=dict(axis='X', idle_rpm=60, active_rpm=240, spinup_s=.4),
        bin=dict(mode='fill_steps', steps=5, tip_deg=35, tip_s=.3, note='the hopper, hinged on its rear bottom edge; bin_ore inside scales Y 0→1 with the load'),
        **{'mt_ore_0`, `mt_ore_1`, `mt_ore_2`, `mt_ore_3': "the load's heap per ore type under bin_ore: VehicleWorks shows the load's ore's and switches the others off",
           'wt_beacon': "the work beacon on the cab's cap: dark, flashing amber while the truck cuts ore",
           'cab_head': "the cab's team cap (fixed)",
           'tread_l`, `tread_r`, `sprocket_l`, `sprocket_r`, `idler_l`, `idler_r': "the running gear, as the light tank's"}, **CHASSIS_NODES),
   "With nothing left to mine it parks and powers down: the drum stops and lowers 0.04, the empty hopper lifts 12° (0.6 s); the "
   "beacon is dark; the stack wisps every 3–5 s."),
 U('repair_truck', 'Repairs friendly vehicles, aircraft and structures (30 HP/s, 1 steel per 10 HP); auto-repairs within 6 tiles when idle.', 'factory', 'wheeled', 2.2, 1.8, 286, True,
   "Candy chassis on wheels, a work truck: the shared work cab (as the mining truck's) behind a short bonnet over the steered front "
   "wheels, and on the rear deck a crane: a smoke post with grip rings on a slewing turntable, a team boom resting forward and up over the cab, "
   "and a welding torch hanging from its knuckle on a short jib; gas bottles and lockers beside the post. " + CHASSIS_SIM +
   "Pivoting, it skid-steers with its front wheels at full lock (28°), as the buggy. "
   + CHASSIS_HULL.format(up=2, down=3, period=.3, damp=.5, kick=1.5, roll=4) +
   " The springs bounce the body as it drives (heave spring k 120, damping 10, up to ±0.025). Repair: the sim drives it to within 1.5 "
   "tiles of its patient (or, idle, picks the nearest damaged friend within 6 tiles) and repairs while stopped there, 30 HP/s, a weld "
   "event every 0.4 s, and waits with no steel. Parked at its patient, the crane (`turret`) turns to the sim's TurretFacing (PezMotion, "
   "240°/s), the boom (`rt_boom`) lifts 28° over 0.6 s and the jib (`rt_jib`) swings the torch (`barrel`) to point at the weld (35–115° "
   "below the boom, 140°/s). While the welds land the boom sways ±2° on a 1.4 s sine, the torch tip glows and flares with each weld, the "
   "amber welding beam leaves the `muzzle` with sparks where it lands (a building's fire turns to steam there), and the cab's beacon "
   "flashes. Done, the jib swings the torch back down to hang and the boom settles onto its rest.",
   dict(**TURRET(120), **{'rt_boom': "the boom, hinged on the post's knuckle: rests 20° up over the cab, lifts 28° more to work",
        'rt_jib': "the jib at the boom's tip: hangs the torch 85° below the boom at rest, aims it at the weld while working",
        'barrel`, `muzzle': "the torch (no recoil) and its tip: the welding beam leaves the muzzle",
        'wt_beacon': "the work beacon on the cab's cap: dark, flashing amber while welds land",
        'cab_head': "the cab's team cap (fixed)",
        'wheel_l0`, `wheel_r0`, `wheel_l1`, `wheel_r1': "the tyres (axle 0 at the front): turned about x by their side's travel",
        'steer_l0`, `steer_r0': "the front knuckles with their cycle fenders: yawed into turns, full lock pivoting",
        'wing_l1`, `wing_r1': "the rear wings, on the axle (unsprung)"}, **CHASSIS_NODES),
   "Parked: the crane faces forward, the boom at rest over the cab and the torch hanging dark; now and then the crane glances aside "
   "(every 20–45 s, ±24°, held 2–3 s); the beacon is dark; the stack wisps every 3–5 s."),
 U('outpost_truck', 'Drives to a site and deploys into an outpost.', 'factory', 'wheeled', 1.4, 1.0, 286, True,
   "Candy chassis on six wheels (small steered ones at the front, the bow narrowed for their wings at full lock), carrying the "
   "outpost folded up: the outpost's own dispenser, its smoke stem and team flip-top head, lying forward and rolled onto its solid "
   "side so the clear window and its pressed-ore bricks face up and the team grip ribs face the right flank, the head over the front "
   "wheels as the truck's nose. It rests in a cream bed frame (two cream cradles, cream side boards with a team stripe), "
   "pivoted on the tail's right corner. " + CHASSIS_SIM + "Pivoting it skid-steers, the front wheels at full lock (28°), each side's tyres "
   "turning opposite ways; curving, the front wheels steer by Ackermann from the yaw rate. The hull heaves on its springs (k 120, damping 10) "
   "as it drives. " + CHASSIS_HULL.format(up=2, down=3, period=.3, damp=.5, kick=1.5, roll=4) + " " +
   "Deploy (the sim turns the truck into its structure at once, whole and working; the view hands over in about 2.1 to 2.55 s): it pivots and slides onto the site (0.25 to 0.7 s, a pivot at up to 360°/s and a slide at up to 1.6 tiles/s, its gear running with it and dust scrubbing off it), facing the structure's door side with the dispenser at its tail, so the part's foot is over that part's place in the structure; the hull's springs settle; it stands the dispenser up off its tail about the corner pivot (0.5 s, nodding 3° past upright as it settles), still rolled with its window to the tail, and its turntable turns it a quarter turn on the pivot (0.35 s, smoothstep) to face the way the outpost's stem does, swinging clear behind the tail; then the structure rises (its build sequence over 1 s) while the truck sinks under the ground (smoothstep, gone by 55% of the rise). As the part's foot reaches its spot it stays there, planted (a puff of dust each side), and the structure rises round it: the dispenser is the outpost's own stem and head turned to face the truck's bow, built by the same function and 2% fuller across, so the outpost's stem rises up inside it and it gives way to it as stage_2 lands (75% of the rise), and its head to the outpost's head as stage_3 lands (95%); then the outpost's lookout mast telescopes up. Nothing pops: whatever goes away is underground or inside its twin.",
   {'ot_stem': "the carried dispenser, pivoted on the tail's right corner at the bed's height: lying (tilted forward 90° about x and rolled a quarter turn about its own axis, window up) on the way; to deploy, stood upright (the tilt taken out), then turned to identity",
    'ot_stem_body`, `ot_head': "the outpost's stem and head inside it (2% fuller across, turned to face the bow); ot_head gives way last",
    'ot_anchor': "an empty at the stem's foot: it lands on the outpost's silo axis at the stem's own height",
    'steer_l0`, `steer_r0': "the front knuckles with their wings: yawed into turns, full lock pivoting",
    'wheel_l0`, `wheel_r0`, `wheel_l1`, `wheel_r1`, `wheel_l2`, `wheel_r2': "the tyres (axle 0 at the front): turned about x by their side's travel",
    **CHASSIS_NODES},
   "Parked: the springs settle and the stack (on the left flank, under the load) wisps every 3–5 s."),
 U('scout_buggy', 'Very fast scout with a machine gun against infantry.', 'factory', 'wheeled', 4.0, 3.0, 286, True,
   "Candy chassis on wheels: big smooth balloon tyres at the back, small steered ones in cycle fenders at the front, and its gun on a "
   "little Pez dispenser (a smoke stem carrying the team head high, so the gun sweeps clear over the engine, the wings and the rear tyres). "
   + CHASSIS_SIM + "So the buggy has no turning circle either: pivoting, it skid-steers, its front wheels at full lock (28°) and each side's "
   "tyres turning opposite ways; curving, the front wheels steer by Ackermann from the yaw rate (at most 28°, easing at 220°/s). Every tyre "
   "turns with its side's travel / its radius (capped at 0.4 of its three-bolt hub's period per 1/30 s, so it never strobes). The springs are "
   "soft: the hull heaves on a spring (k 120, damping 10) driven by ground noise as it drives, up to ±0.025. One smooth wing over each "
   "tyre, seated low: the front ones steer with their tyres, the rear ones ride with the axle, so the body's bounce never meets them. " + CHASSIS_HULL.format(up=2, down=3, period=.25, damp=.35, kick=1.5, roll=4) +
   " Fires on the move (machine gun, every 0.5 s, range 5, hitscan): a tracer and a small flash from the `muzzle`, the barrel kicks 0.03 "
   "back in 0.03 s and returns over 0.08 s, the hull kicks 1.5°.",
   dict(**TURRET(300), **BARREL(.03, .03, .08), **{
        'wheel_l0`, `wheel_r0`, `wheel_l1`, `wheel_r1': "the tyres (axle 0 at the front): turned about x by their side's travel",
        'steer_l0`, `steer_r0': "the front knuckles with their cycle fenders: yawed into turns, full lock pivoting",
        'wing_l1`, `wing_r1': "the rear wings, on the axle (unsprung) over the big tyres"}, **CHASSIS_NODES),
   "Parked: the head faces forward and now and then glances aside (every 20–45 s, ±24°, held 2–3 s); the stack wisps every 3–5 s; "
   "the springs settle."),
 U('light_tank', 'Fast all-round tank. Can\'t hit aircraft.', 'factory', 'tracked', 2.6, 2.0, 286, True,
   "Candy chassis on tracks, the reference: a Pez dispenser on licorice tracks, its team head the turret (hinged at the back like a "
   "dispenser's, porthole eyes on its face) and the gun coming out of the head's mouth. " + CHASSIS_SIM +
   "Each side's travel is the forward speed plus or minus the yaw rate times its half gauge, so pivoting the two treads counter-run. "
   "The treads (`tread_l`, `tread_r`: the cleats on each belt's top run) slide back with their side's travel and wrap every 0.1 (a cleat), "
   "rounded track guards over the belt ends hiding the wrap, capped at 0.4 of a cleat per 1/30 s (1.2 tiles/s) so a stream never sees them strobe backwards; "
   "the toothed sprockets and bolted idlers turn with them. " + CHASSIS_HULL.format(up=2, down=3, period=.25, damp=.35, kick=1.5, roll=1.5) +
   " Fires on the move (cannon, every 1.5 s, range 5): the barrel recoils 0.12 along −Z in 0.05 s and returns over 0.35 s (its breech stays "
   "inside the head), the hull kicks back 1.5°, and the flash and the shell leave the `muzzle`.",
   dict(**TURRET(150), **BARREL(.12), **{
        'tread_l`, `tread_r': "the cleats: slid back along −Z by their side's travel, wrapping every 0.1",
        'sprocket_l`, `sprocket_r`, `idler_l`, `idler_r': "turned about x by their side's travel / radius"}, **CHASSIS_NODES),
   "Parked: the turret faces forward, holds its aim 3 s after a fight, and now and then glances aside (every 20–45 s, ±24°, held 2–3 s); "
   "the stack wisps every 3–5 s."),
 U('geological_surveyor', 'Unarmed. Stops for 8 s and flags every deep deposit within 12 tiles as a mining zone.', 'factory', 'wheeled', 2.6, 2.0, 286, True,
   "Candy chassis on four wheels, a candy seismograph: the team cab head at the bow (no gun), a cream listening dish on a telescoping "
   "mast amidships, and at the stern the thumper, a smoke dispenser stem standing on the tail (the deep mines' stem: a clear window, three "
   "team grip ribs, a team flip-top cap) with the team hammer on its steel spring behind the window and the deep mines' team shaft collar, "
   "small, as its foot. " + CHASSIS_SIM + "Pivoting it skid-steers with its front wheels at full lock, as the buggy does. " +
   CHASSIS_HULL.format(up=2, down=3, period=.3, damp=.5, kick=1.5, roll=4) +
   " The survey (the sim: it stands within 0.6 of its site for 8 s, e.WorkTimer, then every deep deposit within 12 tiles gets its flag and "
   "DeepDepositsView raises the stakes that tick; SurveyorAct follows e.WorkTimer, smoothed between ticks): it stops, the foot goes down "
   "0.033 onto the ground and the mast telescopes up 0.14 (0.4 s); the dish turns, easing up to 90°/s, and the feed horn's amber bead lights. "
   "The hammer is drawn up 0.09 over 0.5 s (the spring stretches to it) and slams down in 0.05 s five times, at 1.2, 2.6, 4.0, 5.4 and 6.8 s: "
   "each slam sends a ripple across the ground out to the survey radius, 12 tiles, over 1.2 s, kicks dust off the foot and jolts the hull "
   "nose-down 14°/s; the last ripple reaches 12 tiles at 8 s, as the flags go in. The four amber lamps on the thumper's cap count the survey "
   "up, lighting at 1.6, 3.6, 5.6 and 7.6 s. It packs up ahead of the sim, which may send a prospecting surveyor straight on at 8 s: "
   "the dish turns home from 6.8 s at 180°/s, the foot lifts at 7.8 s, the mast drops (0.4 s) once the dish faces forward. When the "
   "flags go in, the four lamps blink together three times (0.9 s) and go dark. Moving off mid-survey packs up the same way.",
   {'sv_mast': "telescopes up 0.14 while it surveys (0.4 s), down once the dish is home",
    'sv_dish`, `sv_bowl': "the dish's yoke and the dish, tipped 35° back: yawed while it listens (up to 90°/s), home before the mast drops",
    'sv_feed': "the feed horn's amber bead: lit while the mast is up",
    'sv_hammer': "the team hammer in the thumper's window: drawn up 0.09 and slammed down at each beat",
    'sv_spring': "the steel spring from under the cap to the hammer: stretched to follow it",
    'sv_foot': "the collar foot: 0.033 down onto the ground while it surveys",
    'sv_cap`, `sv_tick_0`…`sv_tick_3': "the thumper's team cap and its four amber progress lamps (dark until the survey lights them)",
    'wheel_l0`, `wheel_r0`, `wheel_l1`, `wheel_r1`, `steer_l0`, `steer_r0`, `wing_l1`, `wing_r1': "the running gear, as the scout buggy's",
    **CHASSIS_NODES},
   "Parked: the dish faces back, the mast down, the lamps dark; the springs settle."),
 U('drill_rig', 'Drives to a mining zone and deploys into the deep mine for its ore.', 'factory', 'tracked', 1.2, 1.0, 286, True,
   "Candy chassis on heavy tracks, a Pez dispenser lying on its back: its mast is a deep mine's stem (smoke, a clear window down its "
   "length over the auger inside, three team grip ribs, a team band at its foot) with the team flip-top head at its tip, face up, resting "
   "in the bow's travel cradle, hinged off the stern. A winch (a team-rimmed sheave) beside it, the deep mines' amber lamp at the bow's "
   "left corner, a light bar and a stack. " + CHASSIS_SIM +
   "Each side's travel turns its treads, sprockets and idlers, as on the light tank (the treads capped at 1.2 tiles/s; the rig never "
   "drives faster). " + CHASSIS_HULL.format(up=1.5, down=2.5, period=.6, damp=.55, kick=2.5, roll=1) +
   " On 'drill' it drives to its zone; within 4 tiles of it the amber lamp blinks (0.6 s a beat). The sim deploys it in one tick when it "
   "arrives (within 1.2 tiles of the deposit): the rig is removed and the deposit's own deep mine (deep_mine_iron, _copper, _crystal or "
   "_uranium, from the deposit's ore) is placed whole on the 2×2 over the shaft. The view hands over (DrillRigAct, from WorldView.Retire): "
   "0–1.0 s the mast stands up off the stern, eased, the winch winding 540° and the lamp lit, and locks upright with a puff of dust at its "
   "foot; 1.0–1.4 s the auger runs out of the mast's foot to the ground, turning 720°/s; at 1.4 s it bites: five chips of the deposit's own "
   "ore fly off the hole (crystal and uranium glowing) with a puff of dust tinted to the ore, and it drills on 0.14 into the ground for 0.4 s, "
   "a chip every 0.12 s; 1.8–3.6 s the mine rises through its build stages over the shaft while the rig drills itself down, sinking its whole "
   "height (eased, under the ground by 70% of the rise) with the auger turning, and is removed out of sight: nothing pops. It never deploys "
   "into a derrick: derricks are neutral landmarks the sim places, taken by engineers.",
   {'dr_mast': "the mast, hinged at its foot off the stern: folded along +Z; stood up −90° about x in 1.0 s to deploy",
    'dr_drill': "the auger inside the mast: runs out along its local +Y (0 → the ground + 0.14) and turns about it, 720°/s",
    'dr_tip`, `cab_head': "the team flip-top head at the mast's tip (face up, folded; looking out over the shaft, stood up)",
    'dr_winch': "the winch's sheave: winds 540° as the mast stands up",
    'dr_lamp': "the deep mines' amber lamp: dark on the road, blinking within 4 tiles of its zone, lit to deploy",
    'tread_l`, `tread_r': "the cleats: slid back along −Z by their side's travel, wrapping every 0.1",
    'sprocket_l`, `sprocket_r`, `idler_l`, `idler_r': "turned about x by their side's travel / radius",
    **CHASSIS_NODES},
   "Parked: the mast folded in its cradle, the lamp dark; the stack wisps every 3–5 s."),
 U('construction_truck', 'Drives anywhere and deploys into a command center.', 'factory', 'tracked', 1.1, 0.8, 286, True,
   "Candy chassis on tracks, long and heavy, carrying the command center folded up: the bottom three panels of the command center's "
   "own crane mast, a steel lattice lying along the deck on a licorice saddle, hinged on the tail; its cab is the command center's "
   "dispenser, a smoke stem with team grip slats and a dark lip under the team flip-top head. " + CHASSIS_SIM +
   "Each side's travel is the forward speed plus or minus the yaw rate times its half gauge, so pivoting the two treads counter-run; "
   "the treads slide and wrap every 0.1 (capped at 1.2 tiles/s), the sprockets and idlers turn with them. "
   + CHASSIS_HULL.format(up=.8, down=1, period=.7, damp=.6, kick=0, roll=.75) +
   " (Its own shallow class: the 1.5-long hull's ends travel the most, and at these limits its track guards stay clear of the sprockets "
   "and idlers.) " +
   "Deploy (the sim turns the truck into its structure at once, whole and working; the view hands over in about 1.75 to 2.2 s): it pivots and slides onto the site (0.25 to 0.7 s, a pivot at up to 360°/s and a slide at up to 1.6 tiles/s, its gear running with it and dust scrubbing off it), facing the structure's door side with the mast at its tail, so the part's foot is over that part's place in the structure; the hull's springs settle; it stands the mast up off its tail about the hinge (0.5 s, nodding 3° past upright as it settles); then the structure rises (its build sequence over 1 s) while the truck sinks under the ground (smoothstep, gone by 55% of the rise). As the part's foot reaches its spot it stays there, planted (a puff of dust each side), and the structure rises round it: the mast is the command center's own lowest three panels turned to face the truck's bow, built by the same function and 2% fuller across, standing on the crane's footing; the command center's mast rises up inside it and it gives way to it as stage_2 lands (75% of the rise), and the crane's top comes up last as the build sequence finishes. Nothing pops: whatever goes away is underground or inside its twin.",
   {'ct_mast': "the carried mast, hinged on the tail: lying (+90° about x) on the way, stood upright to deploy",
    'ct_mast_body': "the command center's lowest three mast panels inside it (2% fuller across, turned to face the bow)",
    'ct_anchor': "an empty at the mast's foot: it lands on the crane's axis (the spinner) at the footing's top",
    'tread_l`, `tread_r': "the cleats: slid back along −Z by their side's travel, wrapping every 0.1",
    'sprocket_l`, `sprocket_r`, `idler_l`, `idler_r': "turned about x by their side's travel / radius",
    **CHASSIS_NODES},
   "Parked: the hull settles in one slow rock and the stack wisps every 3–5 s."),
 U('minelayer', 'Unarmed. Lays hidden mines (30 steel each) with lay_mines.', 'factory', 'tracked', 2.0, 1.5, 286, True,
   "Candy chassis on tracks with a Pez dispenser standing on its middle: a clear stem stacked with six team candy lozenges (the mines) under "
   "the team flip-top head, its crown (`lid`) hinged at the back. " + CHASSIS_SIM + "Treads, sprockets and idlers as the light tank's (capped "
   "at 1.2 tiles/s). " + CHASSIS_HULL.format(up=2, down=3, period=.3, damp=.5, kick=1.5, roll=1.5) +
   " Laying ('lay_mines', up to 8 spots round a point, 30 steel each, paid as each is laid): it drives to each spot and the sim lays the "
   "mine under its middle, where the stem stands, the moment it arrives (within 0.3), then drives on. Within 0.8 tiles of the spot the head "
   "flips back (70° over 0.35 s). When the sim lays it, the stack steps down one candy (0.25 s, the next one sliding out of the head into "
   "the top slot), the bottom candy drops through the collar into the floor, a low ring of dust puffs out from under the hull, and the head "
   "shuts with a bounce 0.45 s after. The new mine (the same team candy, pressed into a ring of turned earth) is left behind as it drives "
   "off. The stack shows how many mines the team's steel still buys, up to six; with no steel the stem is empty and the sim lays nothing. "
   "Fog: all of it is its own team's to see (and a spectator's). Every other team's view shows the head shut and a full, still stack, gets "
   "no puff, and never sees a mine unless the sim reveals it (an enemy within 1.5 tiles, or under a radar dome).",
   dict(**{
        'tread_l`, `tread_r': "the cleats: slid back along −Z by their side's travel, wrapping every 0.1",
        'sprocket_l`, `sprocket_r`, `idler_l`, `idler_r': "turned about x by their side's travel / radius",
        'lid': "the head's crown, hinged at its back edge: flips back 70° as it nears a spot, shuts with a bounce after the drop (its own team's view only)",
        'ml_candy_0` … `ml_candy_5': "the candy stack in the stem, bottom first: steps down one candy per mine laid; shows the mines the team's steel buys (its own team's view only)",
        'ml_drop_candy': "the candy that drops into the floor as the stack steps down; hidden otherwise",
        'cab_head': "the dispenser's head on the stem (static)"}, **CHASSIS_NODES),
   "Parked: the head shut, the stack still; the stack wisps every 3–5 s."),
 U('heavy_tank', 'Slow, heavily armoured tank with splash damage.', 'factory', 'tracked', 1.6, 1.0, 286, True,
   "Candy chassis on tracks, the light tank's big brother (1.12 long to its 0.9): the same Pez dispenser a size up, with a bigger, "
   "taller team head, cream armour skirts with a team stripe over the tracks' upper run (on the running gear, so the hull's rock never "
   "meets them; they stop short of the sprocket and idler), appliqué plates on the head's cheeks, a commander's cupola with a periscope "
   "on its crown, and one long gun with a fume extractor. " + CHASSIS_SIM +
   "The treads, sprockets and idlers run as the light tank's (`tread_l`, `tread_r` slide back and wrap every 0.1, capped at 1.2 tiles/s; "
   "counter-running in a pivot). The body squats 0.015 as it pulls away. " + CHASSIS_HULL.format(up=1.5, down=2.5, period=.6, damp=.55, kick=2.5, roll=1) +
   " Fires on the move (heavy cannon, every 2 s, range 5.5, a shell with 0.8 splash): the barrel recoils 0.16 along −Z in 0.05 s and "
   "returns over 0.5 s (its breech stays inside the head), the hull kicks back 2.5°, a big flash and the shell leave the `muzzle`, and a "
   "dust ring rolls out over the ground under it.",
   dict(**TURRET(80), **BARREL(.16, .05, .5), **{
        'tread_l`, `tread_r': "the cleats: slid back along −Z by their side's travel, wrapping every 0.1",
        'sprocket_l`, `sprocket_r`, `idler_l`, `idler_r': "turned about x by their side's travel / radius"}, **CHASSIS_NODES),
   "Parked: the turret faces forward, holds its aim 3 s after a fight, and now and then glances aside (every 20–45 s, ±15°, held 2–3 s); "
   "the stack wisps every 3–5 s."),
 U('artillery', 'Range-11 splash shells, lobbed. Fragile: it stops and digs in to fire.', 'factory', 'tracked', 1.3, 1.0, 286, True,
   "Candy chassis on tracks: a Pez dispenser that lobs. Its hollow team head lays a short, fat howitzer (0.58 long, 0.042 radius) out "
   "of its mouth under two recuperators; wrapped candy rounds ride on a rack on the rear deck. " + CHASSIS_SIM + ARTILLERY_ACT +
   "Treads, sprockets and idlers run as on the light tank. " + CHASSIS_HULL.format(up=2, down=3, period=.5, damp=.7, kick=3, roll=1) +
   " Fires every 3.5 s (range 11, a splash shell at 8 tiles/s): the barrel recoils 0.12 along the head's axis in 0.05 s and returns over "
   "0.6 s (its breech stays inside the head), the hull kicks back 3°, a big flash, a blast ring on the ground and lingering smoke at the "
   "`muzzle`.",
   dict(**TURRET(60), **BARREL(.12, .05, .6), **ARTILLERY_NODES, **CHASSIS_NODES),
   "Parked: the head shut, the gun in its lock, the spades folded back; it holds the gun's lay 3 s after a fight; no idle glances; the stack wisps every 3–5 s."),
 U('long_range_artillery', 'Range-12 splash shells. Sight 7: needs a spotter. Fragile: it stops and digs in to fire.', 'factory', 'tracked', 1.3, 0.9, 286, True,
   "The same dispenser, built bigger to reach a tile further: a longer hull (1.14 against 1.0), a bigger head (0.38 × 0.42 against "
   "0.34 × 0.36), a far longer, slimmer gun (1.08 against the artillery's stubby 0.58, with a bore evacuator and two team bands) under "
   "longer recuperators, bigger spades, three rounds on the rack against two, and a taller whip. " + CHASSIS_SIM + ARTILLERY_ACT +
   "Treads, sprockets and idlers run as on the light tank. " + CHASSIS_HULL.format(up=2, down=3, period=.5, damp=.7, kick=3, roll=1) +
   " Fires every 3.5 s (range 12, a splash shell at 8 tiles/s): the barrel recoils 0.18 along the head's axis in 0.05 s and returns over "
   "0.7 s, the hull kicks back 3°, a big flash, a blast ring and lingering smoke at the `muzzle`.",
   dict(**TURRET(50), **BARREL(.18, .05, .7), **ARTILLERY_NODES, **CHASSIS_NODES),
   "Parked: the head shut, the gun in its lock, the spades folded back; it holds the gun's lay 3 s after a fight; no idle glances; the stack wisps every 3–5 s."),
 U('laser_tank', 'Beam tank: hits ground and air.', 'factory', 'tracked', 2.0, 1.6, 286, True,
   "Candy chassis on tracks with the laser tower on it: the tower's smoke pylon, wound with its cyan coils (coil_0 at the bottom .. "
   "coil_2), stands on the hull and carries the team head as the turret; the beam projector (barrel: a steel lens barrel with the "
   "tower's cyan lens and three glowing cyan coil sleeves along it, barrel_coil_0 at the breech .. 2, on a smoke ball mantlet in a round port in the head's face) pitches in the head; the sleeves carry the tower's coils out where the game's high camera sees them; foil heat-sink fins on the "
   "crown, the rear vent (vent) under the hinge, and the tower's charge bank (three cream capacitor cans with team caps) on the rear "
   "deck. " + CHASSIS_SIM + "Treads, sprockets and idlers run as the light tank's. " + CHASSIS_HULL.format(up=2, down=3, period=.25, damp=.35, kick=1.5, roll=1.5) +
   " The beam cannon (LaserTankAct, ticked by VehicleWorks): hitscan, every 2 s, range 6.5, ground and air. The projector pitches toward "
   "what it's shooting at (the target of its last beam, or its attack order's target before the first), −3° to +45° at up to 200°/s: "
   "level at tanks, up at aircraft; after a fight it holds 3 s, then levels off at 30°/s. The beam charges in the last 0.5 s before it's "
   "due (the 2 s cooldown after the last beam, while engaged): the six coils light one after another, up the pylon and out along the barrel, to 1.3, and the "
   "lens brightens. On the shot the cyan beam leaves the lens (`muzzle`), the lens flashes and fades over 0.3 s, the coils drain "
   "back from the lens (0.04 s apart, 0.25 s each), the head's rear vent breathes out a wisp of heat 0.12 s later and the hull kicks 1.5°. No recoil. A charge "
   "that isn't fired bleeds away over 0.4 s. The coils cast light (GlowCaster): a cyan point light (no shadows) halfway up the pylon, "
   "toward the camera, lights the head, the deck and the ground beside it, with an additive pool on the ground (0.75 tiles); it follows "
   "the coils' glow, about 0.45 on the idle breath, rising to about 0.8 as they fill and 0.95 in the flash. Only the 3 nearest the middle "
   "of the view get a real light (shared with every other glow; handed over with 0.35 s fades); every tank keeps its pool.",
   dict(**TURRET(140), barrel=dict(mode='trunnion_pitch', pitch_min_deg=-3, pitch_max_deg=45, deg_per_s=200, recoil=0),
        coil=dict(mode='charge_glow', count=6, order='coil_0..2 up the pylon, then barrel_coil_0..2 out to the lens', charge_s=.5, peak=1.3, drain_s=.25, light='GlowCaster cyan, 1.5 x glow, range 1.6, pool 0.75'),
        **{'tread_l`, `tread_r': "the cleats: slid back along −Z by their side's travel, wrapping every 0.1",
           'sprocket_l`, `sprocket_r`, `idler_l`, `idler_r': "turned about x by their side's travel / radius",
           'vent': "an empty behind the head's rear vent: the heat wisp leaves from it",
           'muzzle': "an empty at the lens: the beam leaves from it"}, **CHASSIS_NODES),
   "Parked: the turret faces forward and now and then glances aside (every 20–45 s, ±24°, held 2–3 s); the projector is level; the "
   "coils breathe on a 2.5 s sine (glow 0.25–0.45) and the lens glows at 0.5; the stack wisps every 3–5 s."),
 U('apc', 'Armoured transport for 5 infantry, with a machine gun. Passengers die with it.', 'factory', 'wheeled', 2.8, 2.0, 286, True,
   "Candy chassis on six balloon tyres (front pair steered in cycle fenders, the other four under the troop box's sponsons: no wings): "
   "a dispenser's stem laid on its wheels, carrying soldiers as its candy, a rear ramp, five tinted glass roof domes on team rims and the gunner's team head "
   "on a short smoke stem at the front of the roof. " + CHASSIS_SIM + "Wheels and steering as the scout buggy's (skid-steer pivots, "
   "Ackermann curves, 28° at most; the springs heave it up to ±0.025). " + CHASSIS_HULL.format(up=2, down=3, period=.3, damp=.5, kick=1.5, roll=4) +
   " Fires on the move (machine gun, every 0.5 s, range 5, hits aircraft): a tracer and a small flash from the `muzzle`, the barrel kicks "
   "0.03 back in 0.03 s and returns over 0.08 s, the hull kicks 1.5°; its gun sweeps clear over the domes. "
   "Loading ('load': a soldier boards when he's within 1.3 tiles of its middle, and the sim has him aboard at once): the ramp drops "
   "(127° about its hinge at the deck, 0.35 s; on the move only to level, so its tip never drags) as soon as a boarder is within 2.2 tiles of "
   "its edge. Each boarder then walks round the hull (if he came from the side or the front) to the ramp's foot and up it at a trot, at full "
   "size (his path length / 2.2 tiles/s, 0.45 to 1 s, a view delay after the sim's instant boarding), to the full-height wrapper curtain: its telescopic screen rises behind the low roof as the ramp opens, "
   "then seven strips unroll as the ramp clears (the final 40% of its opening). He passes behind it at the threshold without changing size; the strips sway (up to 18°, settling in about "
   "0.7 s), and a helmeted head rises into the next roof dome (0.25 s, ease-out back). The ramp shuts (0.5 s) once nobody is on the way. "
   "Unloading ('unload': the sim puts every passenger on the ground round it at once): the ramp drops in 0.2 s, and the soldiers come out "
   "seat by seat, seat 0 first, 0.22 s + 0.12 s per seat after the unload (after the screen has risen) (unseen inside until then), each stepping out through the wrapper "
   "curtain at full size (the strands swing), down the ramp and round to where the sim put him in 0.85 s, facing his way, so all five are where the sim says within 1.6 s; each one's head drops "
   "out of its dome as he leaves the bay. The heads are its own team's to see: every other team's view shows the domes empty (the sim "
   "tells nobody else who's aboard). Passengers already carried in a restored snapshot are seated as their views become available, without replaying boarding avatars. Destroyed with soldiers aboard, nobody gets out: the ramp and wrapper curtain shut under the blast. "
   "When the vehicle view retires, all private occupant heads are hidden before the model becomes a detached wreck shared by every camera; its empty glass dome shells darken with the hull, revealing no occupant count.",
   dict(**TURRET(240), **BARREL(.03, .03, .08), **{
        'wheel_l0`, `wheel_r0`, `wheel_l1`, `wheel_r1`, `wheel_l2`, `wheel_r2': "the tyres (axle 0 at the front): turned about x by their side's travel",
        'steer_l0`, `steer_r0': "the front knuckles with their cycle fenders: yawed into turns, full lock pivoting",
        'apc_ramp': "the rear ramp, hinged at the deck: VehicleWorks drops it 127° to the ground (0.55 of that on the move) for loading and unloading",
        'apc_seat_0` … `apc_seat_4': "a seated soldier's head in each roof dome: raised 0.07 into the dome as a soldier sits, dropped as he leaves; hidden from other teams",
        'apc_bead_0` … `apc_bead_6': "seven wrapper strips: unroll once the ramp clears, sway up to 18° as a soldier passes, then roll back up before the ramp shuts",
        'apc_screen_0` … `apc_screen_2': "the boarding shelter's top rail and two telescopic rods: rise 0.53 tiles as the ramp opens, stow just above the rear roof when it closes",
        'apc_bay`, `apc_foot': "empties: the threshold, and the ramp's tip (the soldiers' path)"}, **CHASSIS_NODES),
   "Parked: the gunner's head faces forward and now and then glances aside (every 20–45 s, ±24°, held 2–3 s); the ramp up; the stack wisps every 3–5 s; "
   "the springs settle."),
 U('mammoth_tank', 'Super-heavy twin-cannon tank. Hits ground and air, splash, and repairs itself up to 50% HP.', 'factory', 'tracked', 1.2, .8, 286, True,
   "Candy chassis on tracks, the biggest of the three (1.52 long, 1.12 wide, for the sim's 0.7 radius): a whole Pez dispenser on tracks, "
   "its team head riding 0.3 up on the dispenser's body (a rounded smoke stem nearly as wide as the head, with team grip ribs down its "
   "front, sides and back) over a long, wide hull, twin cannons through a drum mantlet for its mouth, a big amber beacon in a clear dome "
   "on its crown, and twin stacks either side of the engine cover. " + CHASSIS_SIM +
   "The treads, sprockets and idlers run as the light tank's (capped at 1.2 tiles/s). The body squats 0.015 as it pulls away. "
   + CHASSIS_HULL.format(up=1, down=1.8, period=.7, damp=.6, kick=2, roll=.8) + " The stack puffs alternate between its two stacks."
   " Fires on the move (mammoth cannon, every 2.2 s, range 6, a shell with 1.0 splash, at ground or air): the sim fires one shell a shot, "
   "so the barrels take turns, left then right: the one that fires recoils 0.1 along −Z in 0.05 s and returns over 0.45 s, and the flash "
   "and the shell leave its own muzzle; the hull kicks back 2° and a dust ring rolls out under it. At aircraft: the drum (`trunnion`) "
   "raises both barrels toward the aircraft the sim is fighting (its attack order's target, the enemy aircraft its turret faces while it "
   "engages, or the one it last shot at, for 2 s), up to 35° at 120°/s, and snaps the rest of the way if the shot comes first; the "
   "anti-air shell is white-hot with a long grey smoke trail and bursts at the aircraft like flak (a pop and hanging black puffs), where a "
   "ground shell is the heavy's amber one and throws dirt and scorches. Self-repair: while the sim repairs it (below 50% HP, 6 HP/s, "
   "not carried) the amber beacon burns bright and turns 1.5 times a second, and its beam turns with it: an audience-aware GlowCaster amber light and its pool, "
   "0.5 out on the lamp's open side, sweep round over the head, the deck and the ground. Weld sparks fountain off its flanks every "
   "0.14–0.28 s, each with an audience-scoped white-hot particle flash; at 50% the beacon goes dark and winds down to a stop.",
   dict(**TURRET(60), **BARREL(.1, .05, .45), **{
        'barrel_l`, `barrel_r': "the twin cannons under `barrel`: one recoils per shot, left first",
        'muzzle_l`, `muzzle_r': "empties at each barrel's tip: the shot leaves the one that fired",
        'trunnion': "the drum mantlet the barrels pass through: pitched up to 35° at aircraft (VehicleWorks)",
        'beacon': "the amber repair lamp and its reflector, in the crown's clear dome: lit and turning only while self-repairing",
        'exhaust`, `exhaust_l': "the two stacks' mouths: the puffs alternate",
        'tread_l`, `tread_r': "the cleats: slid back along −Z by their side's travel, wrapping every 0.1",
        'sprocket_l`, `sprocket_r`, `idler_l`, `idler_r': "turned about x by their side's travel / radius"}, **CHASSIS_NODES),
   "Parked: the turret faces forward, holds its aim 3 s after a fight, and now and then glances aside (every 20–45 s, ±24°, held 2–3 s); "
   "the barrels level, the beacon dark and still unless it's repairing; the stacks wisp every 3–5 s."),
 U('flak_track', 'Mobile anti-air: splash flak bursts. Can\'t hit ground targets.', 'factory', 'half-track', 2.4, 2.0, 286, True,
   "Candy chassis as a half-track (candy_chassis gear='halftrack'): small steered tyres up front in cycle fenders, licorice tracks under "
   "the back, a plain deck (no lamps, no grille, nothing on the flanks) so the mount carries the silhouette. On the rear deck the SAM site's launcher, "
   "small: a cream drum with its team band and steel slewing ring carries the team head (turret, its dark tracker visor in the mouth); "
   "the elevation axle (barrel) runs through the head with a gun pod on each end (barrel_l, barrel_r: the SAM's cream pods with their "
   "foil wrappers and team caps, two steel autocannons with licorice flash hiders each), and a small search radar (flak_radar) stands on "
   "the head's back. " + CHASSIS_SIM + "Pivoting, the tracks counter-run and the front tyres go to full lock (28°); curving, the tyres "
   "steer by Ackermann about the belts' middle. It rides as a tracked hull: no springs. " + CHASSIS_HULL.format(up=2, down=3, period=.3, damp=.5, kick=1.5, roll=1.5) +
   " The flak mount (FlakAct, ticked by VehicleWorks) rests 20° nose-up. When there's an aircraft to shoot (the one it last fired at, its "
   "attack order's air target, or an enemy aircraft within range in the 45° its turret faces) it snaps up to it, 10° to 80°, fast (a "
   "0.07 s follow, up to 400°/s), follows it as it flies, holds 3 s after it's gone and settles back over about a second. Each burst (a "
   "flak shell every 0.8 s, range 8, at 22 tiles/s, splash 1.0, air only) leaves the pod whose turn it is, left then right: that pod "
   "recoils 0.03 in 0.03 s and returns over 0.12 s, the mount bucks 2° nose-up, the hull kicks 1.5°, and the pod's two spent foil "
   "wrappers flick out of its outer side; the shell bursts at the aircraft in a flash and a hanging black puff. The search radar turns "
   "at 20 rpm; while the mount tracks it carries on round to face forward and locks there.",
   dict(**TURRET(300), barrel=dict(mode='elevation_axle', rest_deg=20, pitch_min_deg=10, pitch_max_deg=80, follow_s=.07, deg_per_s=400, recoil=.03, recoil_out_s=.03, return_s=.12, kick_deg=2),
        flak_radar=dict(axis='Y', rpm=20, lock='faces forward while tracking'),
        **{'barrel_l`, `barrel_r': "the gun pods on the axle's ends: PezMotion recoils them in turn along −Z (left first)",
           'muzzle_l`, `muzzle_r': "empties at each pod's mouths: FlakAct points the rig's muzzle at the next pod's",
           'eject_l`, `eject_r': "empties on each pod's outer side: the spent wrappers flick out of them",
           'tread_l`, `tread_r': "the cleats on the rear belts: slid back along −Z by their side's travel, wrapping every 0.1",
           'sprocket_l`, `sprocket_r`, `idler_l`, `idler_r': "turned about x by their side's travel / radius",
           'wheel_l0`, `wheel_r0': "the front tyres: turned about x by their side's travel",
           'steer_l0`, `steer_r0': "the front knuckles with their cycle fenders: yawed into turns, full lock pivoting"}, **CHASSIS_NODES),
   "Parked: the mount rests 20° nose-up, the radar turns, the turret faces forward and now and then glances aside (every 20–45 s, "
   "±24°, held 2–3 s); the stack wisps every 3–5 s."),
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
   "The candy trooper every soldier is built on (build_models.candy_trooper): a toy on short legs in licorice boots, a smoke-plastic uniform with kraft webbing, "
   "a big cream head under the team helmet, team shoulder boards, a kraft pack; the rifle is +40% for the silhouette. Biped: it turns in place almost instantly. "
   "Walking (Gait) drives the leg_l / leg_r nodes from the hips: the stride follows the ground it really covers, the swinging leg tucks, "
   "the hips drop while both feet are planted (two bobs per stride), sway over the planted foot and lean into the walk and into turns. "
   "The turret node is the torso: it twists at the waist to aim at 400°/s, up to ±70° from the body heading before the body turns. "
   "The arms node carries both arms and the rifle, authored lowered (16° down, a low ready). With a target in range (the sim is tracking one) "
   "it brings the rifle up level in 0.18 s, holds it there 2.5 s after the last shot and lowers it over 0.45 s; the head tips to the sights, cheek on the stock. "
   "Each shot: the muzzle flash and the tracer leave from the muzzle node at the rifle's tip, the barrel (rifle) slides back 0.025, "
   "and the arms kick the muzzle up 9° and settle in about 0.2 s. "
   "From the barracks: spat out of its head tucked into a ball, it somersaults, lands, rolls to its feet and stands up at the exit, "
   "where the real unit takes over at the same size, facing the same way, and walks off to its rally point. "
   "Boarding a transport it slides in and shrinks away; unloading it reappears beside it.",
   dict(**TURRET(400, 70), **BARREL(.025, .02, .1)),
   "Weight shift: the hips sway 0.008 over the feet on a 3 s sine and breathe. The head glances about (a look to one side for 1.2–2.4 s, "
   "back to the front for 2–5 s, ±42°, ±18° walking); now and then (every 20–45 s) the torso turns with it, ±24°."),
 U('rocket_soldier', 'Anti-armour, anti-air infantry.', 'barracks', 'biped', 0.9, 3.5, 360, True,
   "A candy trooper (the rifleman's base) with a kraft candy tube on the right shoulder that fires wrapped sweets: a team sweet sits in the tube's licorice mouth "
   "(the `rocket` node), a licorice venturi bell flares at the open breech (`breech`), blast goggles are pushed up on the helmet and two spare sweets stand in a kraft "
   "carton over the left shoulder blade. Same movement as the rifleman. It doesn't fire on a plain move; given a target (attack, attack-move, or one in range while idle) "
   "it stops where it stands (the sim stops it in range: no delay) and shoots as soon as its body faces it; the rocket flies at 9 tiles/s and the sim's cooldown is 2.2 s. "
   "The arms node (authored 12° down) raises the tube level in 0.18 s; from its first rocket on it aims the tube at what it's shooting: the real elevation from the shoulder "
   "to the target, about level at the ground and up at aircraft (−5° to 40°, at 160°/s; the head looks up with it, up to 12°). Each rocket: the sweet leaves the tube's mouth (the muzzle node) and flies nose first as "
   "that sweet with an orange exhaust and trail; the backblast blows out of the breech (a flash, a cone of gun smoke thrown back, dust kicked up where it reaches the ground); "
   "the tube (barrel) slides back 0.035 and the arms kick up 6°. "
   "The reload fits inside the cooldown: from 0.35 s the tube dips 22° (by 0.75 s), the next sweet is pushed up the tube into the mouth (0.85–1.25 s), and it comes back up on "
   "target by 1.75 s. It lowers the tube 2.5 s after its last rocket (0.45 s), and the next fight starts level. Barracks hand-off, boarding and unloading as the rifleman.",
   dict(**TURRET(300, 70), **BARREL(.035, .04, .3)),
   "As the rifleman: the weight shift, the head's glances (the tube rests on the shoulder, 12° down) and now and then a turn of the torso."),
 U('laser_trooper', 'Elite beam infantry.', 'barracks', 'biped', 0.85, 4.0, 360, True,
   "The candy trooper (build_models.candy_trooper, as the rifleman) in foil webbing, with a foil crest over its team helmet and a power pack on its back: "
   "two cream capacitor cans with team caps, the laser tower's charge bank carried. Its beam rifle is chunkier and shorter than the rifleman's: "
   "a cream receiver with the cyan charge cell (the rail, cell node) along the top in a foil cradle, and a cream emitter with a foil cooling ring and the cyan lens (lens node) at its tip. "
   "Same movement as the rifleman (Gait walks it; the torso twists to aim at 380°/s, up to ±70° from the body heading). With a target in range it brings the rifle up level in 0.18 s, "
   "holds it there 2.5 s after the last shot and lowers it over 0.45 s; the head tips to the sights. "
   "The charge (SoldierWorks, as LaserWorks charges the laser tower): the sim fires the moment its 1.4 s cooldown runs out, so in the last 0.25 s before the next shot is due "
   "the cell brightens from its idle breath to full and the lens with it (the first shot of an engagement fires as the charge comes up). "
   "On the shot the cyan beam (the shared laser beam, fading in 0.22 s) leaves from the muzzle node on the lens, and the damage lands at once (no projectile); "
   "the lens flashes (fading over 0.3 s) and the cell drains in 0.2 s. A charge that isn't fired (the target died or left) bleeds away over 0.4 s. "
   "No recoil: the barrel doesn't slide and the arms don't kick. "
   "From the barracks it's spat out and stands up as the rifleman does, its cell at rest; boarding a transport it slides in and shrinks away, and steps back out beside it.",
   dict(**TURRET(380, 70), **BARREL(0)),
   "The cell breathes dim at 30% emission (±6%) on a 2 s sine and the lens glows at 21%; the hips sway and the head glances about as the rifleman's."),
 U('medic', 'Heals infantry in range. Unarmed.', 'barracks', 'biped', 0.95, 4.0, 360, True,
   "A candy trooper (build_models.candy_trooper, pose 'carry', knees=True: jointed legs, knee_l / knee_r at the knee), unarmed: a bone-white med pack "
   "wider than its shoulders with a team plus on its back and its lid (never a red cross: red would read as Cherry), a bone roundel with a team plus on the "
   "team helmet, and a heal spray in the right hand (cream canister, team end cap, steel nozzle), hanging at the hip pointing 65° down. It walks like the rifleman (Gait), the swinging leg bending at the knee. "
   "Healing is the sim's Repair order: a medic heals friendly infantry for free at 15 HP/s from up to 1.5 tiles (RepairRange), and when idle it goes to tend anyone "
   "hurt within 6 tiles. Stopped in range, the sim turns it to face its patient. Settled there 0.15 s, it kneels (Gait.Kneel, the medic's pose: the right knee goes down with its shin "
   "lying back along the ground, the left foot planted ahead with its shin upright; the body comes down about 0.085 and leans in 14°; 0.2 s, eased) and reaches out: "
   "the arms node comes up 65° in 0.18 s, so the spray points level at the patient, and the head watches the patient. Every 0.4 s, a heal pulse: the team-neutral "
   "cream-white heal beam leaves the spray's tip (its muzzle node) for the patient, cream-white motes lift off the patient, and the patient turns its head to look "
   "at its medic (unless its weapon is up). When the patient is whole or moves off, the medic lowers the spray (0.45 s) and gets up (0.3 s). "
   "From the barracks and in and out of transports it moves like the rifleman.",
   {}, "Weight shift: the hips sway 0.008 over the feet on a 3 s sine and breathe (barely, while kneeling). The head glances about as the rifleman's does; "
   "the torso stays square (no turret yaw)."),
 U('engineer', 'Captures buildings: an enemy one below 50% health, or a neutral derrick at any health. Unarmed; used up by the capture.', 'barracks', 'biped', 0.85, 4.0, 360, True,
   "A candy trooper (build_models.candy_trooper, the rifleman's walk, head glances and barracks hand-off) in a team hard hat with a wide brim, a cream ridge "
   "and a lamp on its front, a big foil spanner held in both hands (the barrel node, its muzzle node at the jaw), a team tool chest on his back and a kraft tool belt "
   "with a pouch and a hammer. The arms node is authored lowered (28°: the spanner carried low). The torso (turret) has no aim: PezMotion leaves it alone. "
   "What the sim does: on a capture order he walks to the building and, the tick he's within 0.6 of its footprint, the building is his team's "
   "(any fire put out, health patched up to 50%) and he's removed: he moves in for good. He doesn't repair or do anything else. "
   "View (SoldierWorks): the lamp node's amber lens is lit while he has a capture order and eases off (0.3 s) when he hasn't; within 1.4 tiles of the building "
   "he brings the spanner up to the ready (20° above level, 0.18 s), head tipped to the job. When the sim's capture lands, his model stays on as he leaves the sim: "
   "he turns to the building's walls (0.15 s) and steps up to them (a stride or two, at most 0.6 tiles, so the jaw reaches the wall), "
   "then knocks it three times, a blow every 0.3 s: the spanner winds up to 38° above level (the torso leans back 5°, the head looks up 10°, which tips the brim clear of it) "
   "and comes down accelerating to 30° below (the torso leans into it 16°), a flash and a spit of sparks off the jaw on each blow. The third blow is the capture: "
   "the building takes his team's colours as it lands, with the capture's team beam and flash (WorldView defers its tint flip to that moment, about 0.9–1.4 s after the sim's capture). "
   "The spanner rebounds to the ready (0.25 s), and he walks into the building, shrinking to nothing over 0.35 s as he goes. Off screen, the building simply changes colours. "
   "Boarding a transport he slides in and shrinks away; unloading he steps back out.",
   {}, "As the rifleman: weight shift and head glances; the spanner held low, the lamp off."),
 U('sniper', 'Range 9 rifle that one-shots infantry; nearly useless against armour and buildings.', 'barracks', 'biped', 0.8, 4.0, 360, True,
   "A candy trooper (build_models.candy_trooper, with knees) in a ghillie of shredded candy wrappers: a floppy team boonie hat with a wrapper veil, "
   "a cape of mint, kraft and foil strips and crumpled wrappers over its back, and a rifle two thirds longer than the rifleman's, with a fat licorice scope. "
   "The sim gives it one 150-damage hitscan shot every 3 s at range 9; it isn't stealthy and never goes prone. "
   "It walks like the rifleman (Gait), but on knees (knee_l / knee_r): the swinging leg bends at the knee. With a target in range and standing still it settles to shoot: "
   "down on its right knee over 0.45 s (Gait.Kneel, the sniper's pose: its shin flat behind it, the left foot planted ahead, the hips drop a third of the leg and lean 8° into the stock) as the arms bring "
   "the rifle up in 0.35 s and its eye goes to the scope; kneeling, it holds its breath (the idle sway nearly stops). It stays down while the fight lasts, "
   "and stands in 0.3 s when it moves (it walks with the rifle lowered) or 2.5 s after its last shot, the rifle lowering over 0.5 s. "
   "The scope's cyan lens (lens node) shows the next shot coming: dim just after a shot, full when the sim's cooldown runs out; with a target in range, "
   "0.35 s before the shot is ready, the lens glints (a cyan-white flash), once a shot. Each shot: the muzzle flash and the cyan tracer leave from the muzzle node "
   "at the brake's tip, the barrel (rifle) slides back 0.04 and the arms kick the muzzle up 14°, settling over ~0.4 s. Then it works the bolt (bolt node), "
   "0.5–1.0 s after the shot: the handle lifts 45°, runs back 0.032 (the spent round's foil wrapper flicks out to the right), runs home and drops, "
   "the rifle rocking down 3° and the head lifting off the scope while it does. "
   "From the barracks: spat out of its head like the rifleman, standing at the exit as the real unit takes over. Boarding a transport it slides in and shrinks away; "
   "unloading it steps back out of it.",
   dict(**TURRET(300, 70), **BARREL(.04, .03, .25)),
   "As the rifleman: the hips sway and breathe, the head glances about, now and then the torso turns with it. The long rifle rests lower, 22° down."),
 U('commando', 'Demolitions infantry: C4 that levels structures and wrecks vehicles. Can\'t fight infantry.', 'barracks', 'biped', 1.05, 4.0, 360, True,
   "A candy trooper (build_models.candy_trooper, pose 'carry') with a team beret slouched over his right ear (cream badge over the left eye), a licorice "
   "bandolier with two team-wrapped charges, a kraft satchel of three candy charges on his back, a compact candy SMG at the right hip and a candy charge "
   "(bone-white brick, team wrapper, licorice cap) in his left hand: the `charge` node. Walking, Gait drives the legs as for the rifleman (one-piece legs, no knees). "
   "The sim (Defs C4: 900 damage, range 1, cooldown 5 s, arming 1.5 s, no projectile): he walks to within 0.9 tiles of the target's footprint and stops. The charge "
   "has to be planted, so he must stay there, still and on the same target, for 1.5 s (Entity.Arming) before the first goes off; the `shot` event IS the blast, then "
   "one every 5 s while it stands and he stays on it. Walking off or switching target starts the arming again. So every charge is shown planted "
   "(SoldierWorks, C4Charge): in range he drops on one knee (Gait.Kneel, the commando's pose: right foot forward 58°, the left knee down behind, hips low, leaning in 18°, "
   "over 0.3 s); half way down (and after a blast once it has cleared, 0.3 s) he reaches back to the satchel (arms +55° over 0.32 s; a fresh charge grows into his hand), "
   "swings underhand (to −80° in 0.2 s) and lets go "
   "two thirds of the way up. The charge flies to the target in a 0.42 s arc, tumbling end over end, and sticks to the target's own surface nearest him that the "
   "player's camera can see (never its plinth, a pole or the far side), splatting flat against it (squash 0.55 springing back in 0.15 s). Its amber lamp blinks, "
   "faster and faster as the time to the bang runs out (the arming left, then the cooldown: from about 3 Hz on the first charge, 1 Hz on the later ones, to 10 Hz); the blast, the scorch and the debris go off where it is on the `shot`, and he ducks his head. "
   "On a vehicle the charge rides along. A charge that couldn't land before its bang (he came back with the cooldown nearly out) goes off where it would "
   "have stuck and leaves his hand. If he walks off or his target changes, a charge left on the wall fizzles (shrinks away in 0.35 s, lamp out); he stands up "
   "and draws another. Glow means state: only a planted, armed charge has a lamp. "
   "From the barracks: spat out tucked, he lands and stands up at the exit like the rifleman. Boarding a transport he shrinks into it; unloading he steps back out.",
   dict(**TURRET(400, 70)),
   "As the rifleman: the hips sway and breathe, the head glances about; the charge stays in his left hand. Working a target he keeps his eyes on it."),
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
 S('outpost', 'Forward ore drop-off, refuelling point and lookout (vision 9).', "A forward ore depot: a dispenser being filled, run by OutpostWorks from the sim's dock steps. "
   "Created only by deploying an outpost_truck (see that entry): while its stages rise the lookout's mast is down, and once stage_3 lands it telescopes up 0.24 in 0.6 s with a small overshoot. "
   "Mining trucks back up to the hopper on the south edge (no door). When a truck lines up in the bay (Align) the hopper's two team trapdoors (flap_l, flap_r, hinged on the rim's sides) "
   "swing open 105° in 0.4 s with a small overshoot; they stay open while it reverses and tips, and slam shut 0.3 s after it starts to pull out. "
   "The truck's ore-coloured pour falls into the open hopper. Every 20 ore that leaves the bin (the sim tips 10 every 0.1 s, so about 8 lumps a load) a lump of that ore comes up out of the hopper "
   "onto the cleated belt (feed_in) 0.4 s later and rides it up the ramp at 0.7 tiles/s to its head (feed_out); the head drum (drum) turns with the belt, which spins up in 0.3 s and runs down in 0.5 s. "
   "At the top each lump is tossed over the stem's lip into the dispenser stem (drop) in 0.35 s. The team flip-top head (head, hinged at its back edge) lifts 55° in 0.2 s as the first lump nears the top, "
   "holds open while the ore comes in, and drops shut with two bounces 0.5 s after the last. Every second lump a pressed brick of that ore drops onto the stack behind the stem's window "
   "(silo_0..4, pitch 0.085, up to five, tinted to the ore; crystal and uranium glow as they do in the field). Between deliveries the stack is fed away underground: every 3.5 s it slides down a slot "
   "in 0.6 s and the bottom brick leaves the window, down to one. The lookout (lookout, on lookout_mast) sweeps ±80° around the bay side at 25°/s with a 1–1.6 s pause at each end; "
   "an enemy unit its team can see within 9 tiles draws it round (up to 120°/s) to follow the nearest. The outpost makes its own power (+10), so low power doesn't slow it. "
   "Under fog (a render whose team only remembers it) the belt is empty and nothing moves.",
   dict(flap_l=dict(axis='Z (hinge on the rim side)', open_deg=105, open_s=.4, close_s=.3, note='flap_r mirrors it; open from Align until 0.3 s into PullOut'),
        drum=dict(axis='X', mode='belt_head', belt_speed=.7),
        head=dict(mode='hinge_open_while_fed', open_deg=55, open_s=.2, close_delay_s=.5),
        silo_0=dict(mode='stack', pitch=.085, note='silo_0..4: a brick per two lumps, fed away every 3.5 s down to one'),
        lookout=dict(axis='Y', mode='sweep_or_track', sweep_deg=80, deg_per_s=25, track_deg_s=120, range=9),
        lookout_mast=dict(mode='telescope_on_deploy', travel=.24, s=.6)),
   "The trapdoors shut and the belt still; one or two pressed bricks in the stem's window; the lookout sweeping across the bay side and pausing at each end."),
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
   "Dispenses one mining_truck through the dock when it finishes building. "
   "The truck bay is on the south, through the dock door; when terrain blocks that lane it moves to the first clear side (east, west or north), "
   "and the dock's kraft chute then comes out of that wall. Each of those walls has a hatch (hatch_0, hatch_1, hatch_2: a licorice frame round a dark opening "
   "and a cream roll-up shutter with a team lip): the shutter facing the bay rolls up in 0.3 s when a truck lines up, the chute waits for it, slides out through "
   "the opening to the truck and back, and the shutter rolls down in 0.4 s once the chute is inside. The shutter stays up while the chute is out, so it never passes through a closed one. "
   "Drop-offs the game places for you face south, so this is for terrain-blocked bays and the player's own placements; if the sim moves a bay to another wall mid-match "
   "(the building that blocked the south lane is destroyed, or one is put on it), the chute draws back, the old hatch shuts, the chute shrinks away and comes up on the new side. "
   "The dock lamps follow the bay: the two south lamps and their pips run the amber come-in chase and the steady cream clear only while the bay is south, and are dark otherwise. "
   "The south bay then reads as shut, behind a cream roll-up shutter with a team lip (bay_shutter) that is rolled up, out of sight, while the bay is south and rolls down in 0.4 s "
   "(once the chute is back inside) when the bay is on a side wall; it rolls up in 0.3 s, ahead of the chute, when the bay returns south. "
   "Each side hatch has its own amber lamp lens above its frame (dock_lens_0, dock_lens_1, dock_lens_2) with two pips either side, which run the same 2 Hz chase in toward the lens "
   "and the same cream clear and dark sequence for the wall the truck is using, so the truck is waved into the wall it docks at. "
   "When the bay changes side the old wall's signal fades out and the new one in over 0.2 s.",
   dict(door=dict(mode='rollup', open_s=.35, close_s=.5), hatch=dict(mode='rollup', open_s=.3, close_s=.4, with_chute=True), spinner=dict(axis='Z', idle_rpm=0, active_rpm=32, spinup_s=.5)), "Dock lights blink alternately at 1 Hz while a truck is docking.", ['mining_truck (free)']),
 S('barracks', 'Trains infantry.', "A soldier dispenser, driven by the sim's training queue (BarracksWorks). The clear case under the flip-top head holds four soldiers stacked like candy: "
   "raw dough at the bottom, primed in the middle, finished at the top. Each is a figurine of the soldier it becomes (its own model, a case figure tall): "
   "the top one the soldier in training, those below the next in the queue, the rest the last one queued (the rifleman before anything is). "
   "A long weapon that would reach the glass (the sniper's rifle) turns its figure aside, across the case. While a soldier trains the stack (stack_lo to stack_hi) rises with its progress, "
   "each figure baking as it climbs, a fresh lump of dough swells in at the bottom, and the amber oven strip (oven) at the case's foot glows. "
   "1.5 s before the soldier is out, the top figure rises out of the case into the head; at 1.25 s the head snaps open, and at 1.1 s it spits the soldier out of its mouth (pop), "
   "full size and tucked into a ball (0.65 of its height), hugging its weapon down along its body (arms 60° down; it lets go as it stands). It somersaults 540° on a ballistic arc to the ground in front (land) in 0.55 s, kicking up a puff of dust, "
   "rolls on to its feet (exit) through another 180° with a squash, and stands up as the real unit appears there. The head drops shut. No door.",
   dict(head=dict(mode='flip_top', open_s='1.25 s before the soldier is out', close_s='after the spit'),
        stack=dict(mode='rise_with_progress', slots=4), soldier=dict(mode='spit_tuck_roll', spit_s=.55, roll_s=.35, spin_deg=720, tuck=.65)),
   "The flag on the pole (flag) is cloth: it streams downwind and ripples, harder in a stronger wind.", ['rifleman', 'rocket_soldier', 'laser_trooper', 'medic', 'engineer', 'sniper', 'commando']),
 S('factory', 'Builds vehicles.', 'A Pez dispenser lying on its side: the head house over the door is its flip-top head, the tray behind it the candy channel. It runs from the item the '
   'factory is making (f = progress / build time, so low power halves it with the sim). A hull blank rises onto the tray at the back (f 0 to 0.12). The '
   "gantry (gantry node, on the tray walls) dips its hoist over the press and picks up a team head (hook_head takes die_head's place, f 0.09), carries it "
   'over the hull (f 0.14 to 0.3), lets it down on its cable and sets it on (blank_head, f 0.44) with welding sparks every 0.2 s, and lifts the hook by f '
   '0.55. Then the spring-loaded pusher (pusher; the cream spring node stretches to stay on it) feeds the hull up the tray into the head house, reaching '
   'the chamber mouth at f 1, the gantry riding with it to the end of its runway. On trained the hull is inside, the pusher snaps back and the spring '
   "recoils (a damped spring, about 12% overshoot), cocked for the next. A cancelled item's hull is dropped and its head goes back on the die. While "
   'producing the press ram strokes 0.25 every 1.5 s (fall 0.1 s, dwell, slow rise); each stroke restocks the die, indexes the foil wrapper roll (spool) '
   '36°, puffs steam off the die and pale smoke from the stack (stack_0). Low power: a stroke every 3 s with a stall on the way up, sparks every 0.4 s. '
   'The flip-top head (head node, hinged at its back edge) lifts up to 22° as the roll-up door opens and drops back with a bounce when it closes. '
   "Roll-out anticipation (lamps, roof vents, the door open before the unit appears, the dark bay) is WorldView's RollOut. A remembered factory under fog "
   "shows idle; a burning factory's stack doesn't puff.",
   dict(door=dict(mode='rollup', open_s=.35, close_s=.5),
        head=dict(mode='hinge_with_door', open_deg=22),
        gantry=dict(mode='slide_z_with_progress', speed=1.6),
        hoist=dict(mode='drop_on_cable', speed=.8),
        pusher=dict(mode='slide_z_with_progress', push_from=.55),
        spring=dict(mode='scale_y_to_pusher'),
        ram=dict(mode='stroke', travel=.25, period_s=1.5, low_power_period_s=3),
        spool=dict(axis='Z', index_deg=36)),
   'The gantry parked over the press with its hook up and a head waiting on the die, the pusher cocked, the line empty; the stack gives a thin wisp every 3 s (every 4 to 7 s on low power).', ['mining_truck', 'repair_truck', 'outpost_truck', 'scout_buggy', 'light_tank', 'heavy_tank', 'artillery', 'laser_tank']),
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
 S('enrichment_plant', 'Turns uranium into plasma.', "A centrifuge hall, one cycle per plasma the sim makes (2.5 s; 5 s on low power, which halves production). "
   "The cascade of six squat rotors (rotor_0..5, foil drums with team caps) spins under the skylight while the plant works: up to 450°/s in about 1.5 s, at half speed on low power, "
   "where it also sags to a third for half a second every 2.6 s as the supply browns out; starved, it coasts to a stop over about 3 s. "
   "Each cycle a slug of uranium runs out of the hopper: the heap (ore) slumps 0.015 and settles, and the sight glass on the hopper's pedestal (sight) flashes as it passes. "
   "At the end of the cycle a magenta plasma brick comes up into the dispenser stem from below and pushes the stack up a slot (mag_0..3, pitch 0.12), and the exhaust vent (vent) puffs. "
   "Every fourth brick the team flip-top head (head, hinged at its back edge) pops open 40° in 0.1 s, the stack empties from the top, a brick (candy) flips out over the lip and away, "
   "and the head drops shut with a bounce. Starved (Working false: out of uranium), the cycle in hand finishes, the rotors coast down, the hopper's heap drains out of sight in 1.5 s "
   "and the amber lamp (lamp_wait) blinks at 0.9 Hz. Glow is state: the sight glass sits at 0.45 while ore runs (0.3 on low power) and flashes to full with each slug, "
   "dark while starved; the uranium itself is unlit ore. All dark until it's built; under fog, neutral and still.",
   dict(rotor_0=dict(axis='Y', deg_per_s='450 (225 on low power)', spinup_s=1.5, coast_s=3, note='rotor_1..5 the same, each a few % faster'),
        ore=dict(mode='slump_drain', slump=.015, drain=.14),
        mag_0=dict(mode='push_up', pitch=.12, note='mag_0..3: the stack in the stem'),
        head=dict(mode='hinge_pop', open_deg=40, open_s=.1, every_bricks=4)),
   "Starved: the cascade still, the hopper empty, the amber lamp blinking."),
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
   "A charge that isn't fired bleeds away over 0.4 s. No recoil. The coils cast light (GlowCaster): a cyan point light (no shadows) halfway up the coils on their near side lights the cream pylon, the pad and nearby units, and an additive pool splashes the pad and the ground round it (1.2 tiles); it follows the coils' glow, 0.53–0.59 on the idle breath, rising gently to about 0.6 as the coils fill and to about 0.66 in the flash of the shot (the brightest moment), then down with the drain. "
   "Low power halves every glow and the light; an offline tower (upkeep unpaid) is dark and still and casts no light, and a tower remembered under fog casts none either. Only the 3 nearest the middle of the view get a real light (handed over with 0.35 s fades); every tower keeps its pool.",
   dict(**TURRET(180), barrel=dict(mode='trunnion_pitch', pitch_min_deg=-30, pitch_max_deg=40, deg_per_s=120, recoil=0),
        coil=dict(mode='charge_glow', charge_s=.4, peak=1.3, drain_s=.25, light='GlowCaster cyan, 2.8 x glow, range 2.4, pool 1.2')),
   "The head faces forward and glances now and then (PezMotion's idle turret). The coils breathe on a 2.5 s sine (glow 0.25–0.45) and the lens glows at 0.5."),
 S('deep_mine', 'Pumps ore from the deep deposit under it straight into the stockpile.', "One family, one machine per ore. The view draws each deep mine with the "
   "model for the ore under it (deep_mine_iron, deep_mine_copper, deep_mine_crystal, deep_mine_uranium: Models.DeepMineModel, from the deposit's type when the "
   "view creates it) and runs every one with DeepMineWorks from the sim (Working: the deposit has ore and the mine is built; the deposit's Amount / Initial; the "
   "team's LowPower). This model, deep_mine, is the fallback for a mine whose deposit the view's world doesn't hold (a player's own copy of the "
   "world, which has only the deposits that team surveyed) or whose model is missing. The game's streams render the host's world, so they show the "
   "ore's machine to anyone who sees the mine, as the old model's ore-tinted stem did: a headframe over the shaft whose skip (dm_skip) hauls ore candy up 1.1, tips its bucket (dm_bucket) 58° into the bin and goes back "
   "down, the drum (dm_drum) and sheave (dm_sheave) turning with the rope (dm_rope), the fan (dm_fan) breathing. "
   "What every mine shares: the sugar pad and cream deck, the team shaft collar, and the dispenser stem in the back corner, the reserve gauge. Its window shows "
   "six pieces of the ore (dm_res_0..5, M_OreTint, tinted to the deposit's ore: bricks, nuggets, crystals or rods) on a spring (dm_spring) under its follower "
   "(dm_follower), one per sixth of the deposit. Each time the deposit drops below a sixth the team flip-top head (dm_head, hinged at its back edge) snaps open "
   "48° in 0.1 s, the top piece (dm_candy) rises out and flips over the front lip, the rest ride up a slot on the spring, and the head drops shut with a bounce. "
   "One trip of the mine's machine every 4 s while it works (16 ore at the sim's 4/s); every 8 s on low power, which halves the rate, with its working lights at a fifth "
   "so a starved mine reads at a glance. Glow is state: each mine's working lights (dm_glow and dm_glow_side) are lit while it works, at a fifth on low power, and out when it stops; the "
   "amber lamp on its beacon post (dm_lamp) and the amber strips beside the stem's window (dm_warn) are dark while the deposit holds up and pulse at 0.5 "
   "Hz once it runs low (the sim's running-low warning, 5 minutes left). Run dry, the trip in hand finishes and the machine parks, a last puff of ore dust rises from the shaft 0.6 s later, the lamp "
   "blinks three times (1 s each) and goes out, the stem is empty with the head hanging open 34°, and the whole mine darkens (dim 0.35 linear, about 60% in the stream) over 2 s: dark and still, "
   "finished, readable from across the map. Built from a drill rig: the rig sinks as stage_0..stage_3 rise (PezEmerge). Sold or destroyed: the shared poof. "
   "Dark until built; under fog, neutral and still.",
   dict(dm_res_0=dict(mode='reserve_stack', pieces=6, note='dm_res_0..5, one per sixth of the deposit; dm_spring and dm_follower under them'),
        dm_head=dict(mode='hinge_pop', open_deg=48, open_s=.1, hang_open_deg=34),
        dm_lamp=dict(mode='state_glow', running_low_hz=.5, dry='three 1 s blinks, then dark', note='dm_warn pulses with it'),
        dm_glow=dict(mode='state_glow', note='each mine\'s working lights: 1 working, 0.2 on low power, off stopped'),
        dm_skip=dict(mode='hoist', rise=1.1, trip_s='4 (8 on low power)', note='fallback model only; the per-ore models below')),
   "Working: its machine running a trip every 4 s. Dry: parked, the stem empty with its head hanging open, the lamp out, the mine darkened."),
 S('deep_mine_iron', 'The deep mine on an iron deposit: the pithead.', "A stout enclosed winding tower in the right corner, its crown a team head with the "
   "team sheave (dm_sheave) on the roof. Each trip the skip (dm_skip, a steel bail with a team bucket, dm_bucket, pivoted at its +x bottom edge, and a load of "
   "cinnamon bricks, dm_load) runs up the tall slot in the tower's front face on its guide rails, 0.78 to the dump door (dm_top) in 1.4 s, eased at both ends; "
   "the bucket tips 58° in 0.24 s toward the hopper (dm_bin), and three cinnamon bricks (dm_tumble_0..2) tumble out of the dump door end over end, 0.09 s "
   "apart, down the chute (from dm_chute) into the hopper on its legs in front of the tower, 0.55 s each; as the last lands the heap (dm_heap) bobs 0.02 and a "
   "puff of cinnamon dust rises. The skip goes back down in 1.3 s. The sheave is driven by the skip's travel through the rope (the rope leaves it at radius "
   "0.3), so it spins up, slows and reverses with the skip; the rope below it (dm_rope) stretches to the skip's eye (dm_eye). On low power the winder labours: "
   "now and then, on the way up, the skip slows to a crawl for half a second. The fan in the left corner (dm_fan) turns at 540°/s while it works (270 on low "
   "power) and coasts down when it stops. The winder's window in the crown (dm_glow) glows amber while it winds. The tallest of the four.",
   dict(dm_skip=dict(mode='hoist', rise=.78, trip_s='4 (8 on low power)', up_s=1.4, down_s=1.3),
        dm_bucket=dict(mode='tip', tip_deg=58, tip_s=.24, hold_s=.2),
        dm_tumble_0=dict(mode='tumble', bricks=3, each_s=.55, stagger_s=.09, note='dm_chute to dm_bin, end over end'),
        dm_sheave=dict(mode='rope_driven', note='turns with the skip: travel / 0.3 rad'),
        dm_fan=dict(axis='Z', deg_per_s='540 (270 on low power)', coast_s=3)),
   "Working: the skip running up the slot every 4 s and the bricks tumbling into the hopper. Dry: the skip parked at the foot of the slot, the hopper drained."),
 S('deep_mine_copper', 'The deep mine on a copper deposit: the incline.', "A long belt climbs from the shaft collar in the left corner to the crusher house at "
   "its head over the hopper in the right corner, on two steel trestles; a team hood covers its upper run and team stripes run down its edges, and a mint "
   "patina pipe runs up beside it. Six copper bricks (dm_carry_0..5, mint-flecked) ride the belt evenly spaced from its tail (dm_belt_a) to its head "
   "(dm_belt_b), one into the crusher a second (each 6 s on the belt; half the rate on low power); each grows up out of the shaft over the first 8% of the belt "
   "and drops into the crusher over the last 6%. As each drops, the hopper's heap (dm_heap) bobs and a puff of mint dust rises from the crusher. The belt "
   "eases to speed over 0.5 s; the crusher drum (dm_crusher, steel with dark teeth, in the open mouth of its mint housing under a team roof) turns at 200°/s "
   "at full rate, and the head and tail pulleys (dm_pulley_0, dm_pulley_1) turn with the belt. Stopped (dry), the tail loads no more bricks; those on the belt "
   "shrink away where they are in 0.5 s and the belt coasts to a stop (no ore after the sim's last). The crusher's mouth (dm_glow) and a slot in the side of its house facing the camera (dm_glow_side) glow amber while it "
   "crushes. The longest and lowest of the four.",
   dict(dm_carry_0=dict(mode='belt', bricks=6, per_s='1 (0.5 on low power)', note='dm_belt_a to dm_belt_b; in at the tail, off into the crusher'),
        dm_crusher=dict(axis='X', deg_per_s='200 x the belt rate'),
        dm_pulley_0=dict(mode='belt_driven', radius=.05, note='dm_pulley_0 and dm_pulley_1')),
   "Working: bricks riding up the belt and dropping into the crusher every second. Dry: the belt run off and stopped, empty."),
 S('deep_mine_crystal', 'The deep mine on a crystal deposit: the core drill.', "A low, wide ring tank round the shaft, its rim in team colour, a frosted-glass "
   "collar over the shaft in its middle with the rotary table on top, and a low steel bridge from the rim to the hoist block (team) over the collar. The drill "
   "string and rotary table (dm_drill) turn at 50°/s while it works (25 on low power), spinning up and coasting down. Each trip the core barrel (dm_skip) "
   "lifts a glowing core (dm_load, rock-candy cyan) up out of the frosted collar 0.58 (dm_top) in 1.4 s, its cage (dm_bucket) tips 58° toward the tray on the "
   "rim (dm_bin), the shards slide down the chute and ring as they land: a thin cyan ring flashes out of the tray, two glowing chips bounce off it and a spark "
   "flies, and the tray's heap (dm_heap) bobs. The barrel goes back down into the collar in 1.3 s. The hoist's sheave (dm_sheave) turns with the rope. Cyan "
   "only while it works: the core and a ring of light in the tank's floor (dm_glow) glow at full (a fifth on low power) and go dark when the mine stops. "
   "The widest and flattest of the four.",
   dict(dm_skip=dict(mode='hoist', rise=.58, trip_s='4 (8 on low power)', up_s=1.4, down_s=1.3),
        dm_bucket=dict(mode='tip', tip_deg=58, tip_s=.24, hold_s=.2),
        dm_drill=dict(axis='Y', deg_per_s='50 (25 on low power)', spinup='40°/s²', coast='20°/s²'),
        dm_load=dict(mode='state_glow', note='M_E_Cyan: 1 working (0.5 on low power), 0.12 stopped')),
   "Working: the drill string turning and a glowing core lifted out and tipped every 4 s. Dry: the barrel parked down in the collar, the core dark."),
 S('deep_mine_uranium', 'The deep mine on a uranium deposit: the containment.', "An armoured dome sealed over the shaft: a smoke drum with a team foot and a "
   "sour-acid sight band for its lip (dm_glow), a smoke dome with team ribs and a team cap. A jib crane (dm_jib) on a steel post at the front edge "
   "swings its hook (dm_skip) between the port on the dome's shoulder (a team ring) and the cask under it on the carousel (dm_carousel) in the front corner. "
   "Each 4 s trip: the hook lifts an acid rod (dm_load) out of the port 0.42 (dm_top) in 0.64 s, the jib swings about 92° over the cask in 0.56 s, lowers the "
   "rod into it in 0.56 s, lets it go (the rod sinks into the cask in 0.16 s and the seal hisses: a wisp of steam), raises the hook in 0.48 s, swings back in "
   "0.56 s and lowers into the port in 0.56 s to take the next. After each rod the carousel turns the next cask round, 90° in 0.8 s. Sour acid is always lit "
   "while there's uranium: the rods and the sight band glow at full (a fifth on low power) and go out when the deposit is dry. The roundest of the four.",
   dict(dm_jib=dict(mode='swing', swing_deg='about 92 (from the model: the hook to dm_bin)', out_s=.56, back_s=.56),
        dm_skip=dict(mode='jib_hook', rise=.42, lower='to dm_bin', trip_s='4 (8 on low power)'),
        dm_carousel=dict(mode='index', deg=90, s=.8, note='after each rod'),
        dm_glow=dict(mode='state_glow', note='M_E_Acid sight glasses and rods: lit while there is uranium')),
   "Working: a glowing rod lifted out of the dome and swung into a cask every 4 s. Dry: the hook parked in the port, no rod, the sight glasses dark."),
 S('derrick', "A neutral landmark near the middle of the map: any engineer captures it, and it pays its holder 1.5 steel/s (no power, no ore).", "A pre-war "
   "landmark, run by DerrickWorks from the sim: a giant riveted steel dispenser on a stepped stone plinth, older and heavier than anything a team builds. Its "
   "battered stem has riveted bands, cream buttresses, a kraft repair patch and a rust run, a window on a stack of steel ingots over a great spring, and a "
   "flywheel (dk_wheel) on its side; its huge team flip-top head (dk_head, hinged at its back edge, closing on a team collar) is sugar-pad grey while nobody "
   "holds it (a cool steel grey on its head and collar, set by DerrickWorks: the sugar pad's beige is the sand's hue). Held: the flywheel spins up to 90°/s (and coasts down when it's released), and every 2 s (3 steel a brick, the sim's 1.5 steel/s) a steel brick "
   "(dk_brick, hot amber, fresh off the press) is pushed out of the mouth under the head's chin (0.24 s), slides down the dark chute gathering speed (0.6 s, from dk_chute_top to "
   "dk_chute_end), drops off the lip into the tray (dk_tray) with a bounce, and settles into the heap of ingots there (dk_pile jolts 0.015, a puff of steel "
   "dust). The head kicks 4° as each brick comes out, and the amber pour lamp in the mouth (dk_lamp) lights the pour: up as the brick comes out, out once "
   "it's in the tray (the team colour itself never glows). Neutral: still, the lamp dark; every 5–9 s grit sifts off its head (waiting). Captured or released: "
   "the head flips open 150° in 0.3 s, holds 0.2 s, turns over into its new owner's colours at the top, and slams shut in 0.3 s with a bounce and a puff of "
   "dust. A fresh derrick (a new map's, or one rising again 2 minutes after the last was destroyed) rises out of the ground stage by stage in 1 s (the sim has it whole and capturable already) with "
   "stage dust; the sim places it whole. Destroyed: the shared poof (it can't be sold). Under fog: still.",
   dict(dk_wheel=dict(axis='X', deg_per_s='90 while held', spinup='45°/s²', coast='20°/s²'),
        dk_brick=dict(mode='pour', every_s=2, note='mouth 0.24 s, chute 0.6 s, tray 0.28 s, into the heap 0.3 s'),
        dk_head=dict(mode='capture_flip', open_deg=150, open_s=.3, hold_s=.2, close_s=.3, kick_deg=4),
        dk_lamp=dict(mode='pour_glow', note='amber, only while a brick pours')),
   "Held: the flywheel turning and a brick poured every 2 s. Neutral: still and grey, grit sifting off its head now and then."),
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
         "Node names match the .glb files. `PezMotion.cs` drives the named nodes and `PezEmerge.cs` runs the emerge and remove transforms."
         " This file is generated from `art-src/tools/motion.py`: change an entry there and splice it in with `art-src/tools/kit/motion_splice.py` (see `docs/art/ASSET_GUIDE.md` §6).", "",
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
                L.append(f"- **`{n}`:** " + (v if isinstance(v, str) else ", ".join(f"{k}={vv}" for k, vv in v.items())))
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
