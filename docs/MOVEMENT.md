# Movement: why units dance, and the system that stops it

Units (infantry, vehicles, aircraft) keep getting locked in "dances": stepping back and forth, sliding sideways,
snapping round on the spot, or hovering over a pad without landing. Each report got a point fix (rules 34, 37, 38),
and a new one appeared. This is the audit behind that pattern and the design that ends it. The goal: no unit can
oscillate, by construction, and if some new interaction ever does make one dance, the sim stops it within seconds and
CI and the ops dashboard say so.

## What the audit found (October 2026)

Evidence: live snapshots of rooms 1 and 2 replayed headless with `--dance-check` (path vs net travel, facing snaps,
sideways slides), traces of the units in each reported GIF, and a read of every line that moves a unit.

Twelve ground loops and seven air loops reduce to seven causes:

1. **Many writers per tick, no arbitration.** One unit's `Pos` can be written in one tick by `StepToward`,
   `DodgeVehicles`, `Separate`/`TryNudge`, `GiveWay`, the docking lerps and the axis slide, each unaware of the
   others. Two of them pulling opposite ways on alternate ticks is a dance. (Soldiers between two tanks; the dodge
   sideways and walk forward alternation; separation pushing a shooter out of range.)
2. **Hard thresholds with no band.** `Engage` stops at 0.95x range and chases beyond it; `IdleCombat` the same at
   1x; waypoint capture at 0.25; harvest re-centring at 0.45; the group pace switch. A push of a few hundredths
   across the line flips the unit every tick (the firing-line flicker of attack-moving armies).
3. **Shared goal points.** Several units get exactly the same target: every aircraft refuelling at an airfield
   flies to its centre (0.6 tolerance, while two aircraft need 1.2 to 1.4 between them); truck queue spots chosen
   by `Id % 2`; formation slots that fall inside a building collapse onto one tile; rally, retreat and spawn
   points. Nobody can arrive, so they shove each other forever. (The bomber and gunship over the airfield; eight
   trucks circling one outpost; aircraft running dry while hovering over a pad, which is how the reported bomber
   crashed.)
4. **Choices re-made with no stickiness.** Targets every tick (`AcquireTarget`), drop-offs every second, paths every
   second or whenever `Path` is cleared, the closest reachable point when a goal is cut off. Two near-equal options
   flip as the unit moves between them.
5. **Instant facing.** Infantry and aircraft snap to any direction in one tick (walking, aiming, dodging), and
   anything that moves a unit without turning it slides it crabwise (every separation push).
6. **Stuck detection that can't see a flicker.** `CheckJam` only runs while `Moving`, so a unit flipping
   stop/go every tick resets it; it only ghosts; `ProgressPos` is never read; `GiveWay`'s repeat guard can never
   trigger; `OwnBlockedSince` goes stale.
7. **View amplifiers.** Aircraft pitch flips with `Moving`, roll swings with every facing flip, drone tilt feeds
   on separation jitter, and a browser copy that missed an order slid units at every snapshot (fixed: orders now
   replay in the copy).

## The design: five invariants and a guard

### I1. One writer per tick

Each tick runs three phases for every mobile unit:

1. **Intent.** The unit's order (its state machine) produces a goal, a desired velocity and a priority. It writes
   no position.
2. **Resolve.** One function turns every unit's desired velocity into the displacement it actually makes:
   reciprocal avoidance of neighbours (the higher priority keeps its line, ties broken by id), getting out of
   vehicles' paths for infantry, terrain sliding, the speed cap. It writes `Pos` once.
3. **Facing.** Each unit turns toward its resolved motion at its class's turn rate. Aiming turns it only while it
   is standing.

`Separate`, `GiveWay` and `DodgeVehicles` stop being separate writers and become terms in Resolve. Teleports
(spawn, unload, carry, evict) are explicit and marked as jumps, so the view and the checks skip them.

### I2. Every goal point is owned

Any time units are sent to a point, a reservation service hands each one its own slot: formation slots placed on
open ground around the point (never inside buildings), numbered pad slots on airfields (as many as the footprint
holds, at least rA + rB apart), an ordered queue of spots at each drop-off (one truck per spot, the head advances
one at a time), and spread rally, retreat and spawn points. A unit arrives when it reaches its own slot, with a
tolerance larger than its step. No two units ever chase the same point.

### I3. Every threshold has a band, every choice has stickiness

Thresholds become enter/exit pairs: stop to fire at 0.9x range and resume the chase only beyond 1.0x; stop
re-centring on ore until 0.6 off; the group pace switch with a band. A choice is changed only if the old one is
gone or the new one is better by a margin, and at most at a fixed cadence: targets keep their target, drop-offs
switch only if 25% better and at most every 5 s, paths are only rebuilt when blocked or when the goal moves a tile.

### I4. Facing is never instant

Turn rates per class (infantry about 10 rad/s, vehicles 5, aircraft 3 with bank). A unit faces where it is
actually going; being pushed doesn't turn it, and a push large enough to matter is a move, made facing forward.
Vehicles correct a small misplacement by creeping or reversing, not by turning round.

### I5. Aircraft land in slots and stay put

An airfield or outpost has numbered pads. A refuelling aircraft is given a free pad, lands on it and is pinned
there (no separation while landed). Aircraft waiting for a pad hold in a spread ring outside, each at its own
angle. An idle aircraft at full fuel leaves its pad when another needs it.

### The guard: a sim that refuses to dance

Every unit keeps two running averages over about two seconds of its per-tick displacement: the average velocity
v̄ and the average speed s̄, plus a count of facing reversals. A unit is **dancing** when it is moving
(s̄ above a crawl) but getting nowhere (|v̄| < 0.25 s̄) for two seconds, or reverses its facing four times in two
seconds. A dancing unit **settles**: it stops, holds its facing and is treated as fixed by everyone else for three
seconds, and its order re-resolves its goal (a new slot, or "arrived" if it's within two tiles). The world counts
settles per unit kind and cause.

This is the "never": whatever new interaction appears, no unit can dance for more than about two seconds. It is a
backstop, not the fix. A settle in a designed scenario is a bug, and the gate below fails on it.

### The gate

`headless --movement-check` runs a scenario corpus and fails CI on any spin, dance or settle:

- a 48-unit mixed army into one point; two tanks crossing at a soldier; an attack-moving firing line of 30 against a
  building; formation slots overlapping buildings;
- six aircraft refuelling at one airfield; ten trucks at one refinery, and at one outpost;
- 20 minutes of AI against AI;
- fixtures: saved live snapshots from every reported dance (the Room 2 refuel and outpost jams, the spinning
  soldiers), replayed for 60 s.

In production the settle counters go to the ops dashboard, so a new dance shows up as a number on a room before
anyone has to send a GIF.

## Rollout

Each phase is its own rules version and is gated by the movement check as it grows.

1. **Stop the bleeding.** The guard (detect and settle), facing turn rates, airfield pad slots with pinned landed
   aircraft, the gate harness with the scenarios above and the live fixtures, settle counters in state.
2. **Owned goals and bands.** Reservations for queues, formations, rally, retreat and spawn points; hysteresis on
   every threshold; sticky targets and drop-offs.
3. **One writer.** The Intent / Resolve / Facing split, folding separation, give-way and dodging into Resolve.

After phase 3 the guard should never fire in play; its counter staying at zero is the measure.

## Status

- **Phase 1 (rules 39):** done. The guard, turn rates for infantry and aircraft, airfield pad slots with holding and
  pinning, the gate (`headless --gate`, also part of `--test`), settles in `/api/status` and on the ops dashboard.
- **Phase 2 (rules 40):** mostly done. Single-file truck queues with an Enter step (only the front truck claims the
  bay), sticky drop-offs, fire and harvest bands, the idle leash hold, dodgers keeping their spot, formation slots by
  position on open ground. Left: sticky target choice, the group pace band, spread rally, retreat and spawn points.
- **Rules 42:** the slide-pause rhythm (trucks hopping tile to tile on thin ore: a mining reach and driving to where a
  stop collects most), vehicles driving where they face, occupied goals counting as reached, stale paths through new
  buildings planned again, and the guard counting only time spent moving (it settled trucks that had just moved up a
  queue). The gate now also checks the guard catches a deliberate dance, and that nothing goes stop-go on a beat while
  mining thin ore.
- **Rules 44 (the economy audit merged in):** the thin-ore gate still has a beat in it. Once a truck has picked a field
  over, the best stop collects a few units and the hop is a tile or two, so it hops about every 1.3 s and, with the
  8-in-10-s threshold, 3 of 16 seeds and sizes of Rules 42 itself failed it (the gate only passed on its own seed).
  A loaded truck whose best stop collects under 8 ore now sets out for the next only on a 2-second beat of the world's
  clock; the ore regrows no faster for the hurry (the 16 seeds mine 0.7% less, and the longest beat falls from 8 to 5).
  A truck pulling into a bay swings in from where it was stopped short by a unit parked on the lane's head, and the
  bay shoves that unit aside (Align holds the line), instead of waiting in Enter for it to leave. It only swings in
  over open ground: stopped across rock beside the lane, it stays in Enter until the unit moves.
- **Phase 3:** not started. What's left for it: units shoved while standing (a queued truck nudged by one passing, an
  idle soldier pushed by separation) still slide without turning.

Gate budgets (settles in the run): every scenario 0, except four more AI games on other seeds, which share a budget of
4 (a truck rocking on its way to ore, a surveyor at its site). Over 12 seeds of 8-minute AI games the guard settles 5
units in all (9 of the 12 settle none); before phase 1's fixes the live fixtures settled 55 to 86 a minute.
