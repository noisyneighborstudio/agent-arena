# From the Spearhead: a doctrine of maneuver

*Written from the front. The vantage point is the commander in the lead vehicle, who can see the enemy and can't see the stockpile.*

## The core idea

Wars are not won by destroying everything the enemy owns. They are won by making the enemy's system stop working, faster than they can repair it. Strength is something to go around. What you look for are seams: the unguarded refinery, the mining truck alone in the middle, the power plant one hit from going dark.

This school descends from Sun Tzu ("attack where he is unprepared, appear where you are not expected"), from the German *Bewegungskrieg* and its mission-type orders (*Auftragstaktik*), from Liddell Hart's indirect approach, and from John Boyd's OODA loop: observe, orient, decide, act, and do it inside the enemy's cycle until their decisions describe a battlefield that no longer exists.

## Principles

1. **Tempo is the weapon.** A mediocre plan carried out now beats a perfect plan carried out in a minute. Every second the enemy spends reacting is a second they aren't building.
2. **Surfaces and gaps.** Probe everywhere, commit only where the probe goes through. Hard points such as turret lines and massed tanks are surfaces: don't bleed on them. Gaps such as trucks, outposts, unguarded production and the back door of the base are where the war is decided.
3. **Have a main effort (*Schwerpunkt*).** Concentrate overwhelming force at one decisive point, and accept being weak everywhere else. Two half-strength attacks lose to one full one.
4. **Recon pulls, it doesn't push.** Scouts find the gap, and the main body follows the scouts, not the plan. An army that doesn't know where the enemy is will be thrown at them blind.
5. **Paralysis over destruction.** Kill the things the enemy's other things depend on: harvesters feed refineries, refineries feed factories, power feeds everything. One dead power plant can halve their production. That's worth more than ten dead riflemen.
6. **Never fight fair.** Engage only with local superiority, at a time and place you chose. If the fight turns even, break off and hit somewhere else. Retreat is a move, not a defeat.
7. **Keep moving.** A parked army is a target and a wasted asset. Between raids, reposition toward the next gap.

## How this school sees the enemy

The enemy is a machine with a few critical joints. Find them and the rest of the machine seizes. Their army is something to evade or to fix in place while you strike behind it. The real target is their ability to decide and to produce.

## What it fears

- **Culmination.** Advancing past the point where the attack can be sustained. Deep raids die when reinforcements are a minute away.
- **Being out-produced while winning skirmishes.** You can win every fight and still lose the war to a bigger stockpile.
- **Losing the initiative.** Once you're reacting to them, the doctrine has failed.

## In Pezz terms

- **Opening:** take the minimum economy: one refinery, three or four trucks. Then go straight to a barracks and factory. Scout buggies and a recon drone go out within the first two minutes to find the enemy base and their ore fields.
- **First strike by about 3:00:** fast light tanks and scout buggies go after mining trucks and lone outposts. Every dead truck is income the enemy never gets.
- **Joints to hit, in order:**
  1. mining trucks
  2. power plants (low power halves their production and refining)
  3. refineries
  4. electronics and enrichment plants (which cut the tech tree)
  5. the factory and barracks
  6. the command center last
- **Engineers** follow the spearhead to capture damaged refineries and factories. Their economy becomes yours.
- **APCs and transport choppers** put infantry behind the turret line. Commandos plant C4 on production buildings while the army fixes the enemy's attention at the front.
- **Fix and flank:** a small force goes to `attack_move` at the obvious target while the main effort hits a different sector.
- **Alerts:** treat every priority alert on your own side as a cue to counter-raid. An enemy army at your base is an enemy army not at theirs.
- **Commands to favour:** `attack` (focus fire on the joint), `attack_move` along routes away from their turrets, and `load`/`unload` for deep strikes. Use `say` to announce feints you don't intend to make.

## Failure modes

The doctrine gambles. It loses to an opponent who has walled in their economy behind turrets and SAMs and then simply out-builds the raids. When the raids stop paying, the maneuver commander must convert to a real army before it's too late. Many don't.

## Standing orders (paste into Commander's Orders)

> Fight a war of maneuver. Tempo beats perfection: act now, adjust later. Get one refinery and 3-4 trucks, then barracks and factory fast. Send scout buggies and a recon drone out in the first 2 minutes to find the enemy base, their ore fields and their mining trucks. Strike by 3:00 with fast units. Priority targets: enemy mining trucks, then power plants, then refineries, then electronics and enrichment plants, then factory and barracks. Avoid turret lines and massed tanks: probe, and commit only where a probe finds a gap. Keep one main effort with overwhelming local force; never split into two half-attacks. If a fight turns even, break off and hit a different sector. Use engineers to capture damaged refineries and factories. Use APCs or transport choppers to drop infantry behind their defenses. Use commandos on production buildings while your army holds their attention elsewhere. Never let the army sit idle: between raids, move toward the next gap. When your base is attacked, answer with a counter-raid on theirs unless the attack threatens your command center. Keep the economy just big enough to sustain the tempo, and convert to a heavier army if the raids stop paying.

## How to beat it

Turtle the economy behind layered defenses, shadow every truck with escorts, keep a mobile reserve at home, and out-produce. Make every raid cost more than it takes. See [the Quartermaster's doctrine](QUARTERMASTER.md).
