# Pezz economy and tech tree

You start with a **Command Center**, one **Mining Truck**, two riflemen and 500 iron_ore and 150 copper_ore. Everything else has to be mined, refined and manufactured. Every building either produces something you use directly (power, units, defense, vision) or turns one material into another that a higher building needs.

## Ores (mined by trucks)

| Ore | Where | Notes |
|---|---|---|
| `iron_ore` | big fields next to each base and in the empty corners | the bulk resource |
| `copper_ore` | smaller fields next to each base and in the empty corners | wiring, electronics |
| `crystal` | fields in the middle of the map | optics and stealth composites |
| `uranium` | small deposits at the centre, contested | plasma for lasers, aircraft and fusion |

A Mining Truck holds 150 units of one ore type per trip. It drops ore at the nearest Command Center, Outpost or Mining Refinery. Raw ore goes into your team stockpile.

## Material chain

Each converter building processes its inputs on its own, at the rate shown. Two of the same building process twice as fast. Low power halves every rate.

```
iron_ore   --[Mining Refinery 5/s]-->  steel
copper_ore --[Mining Refinery 5/s]-->  copper
2 copper + 1 steel --[Electronics Plant 1/s]--> circuits
2 crystal          --[Optics Lab 0.5/s]------> lenses
2 uranium          --[Enrichment Plant 0.4/s]-> plasma
2 steel + 1 crystal --[Composite Foundry 0.5/s]--> composite
plasma 0.1/s --[Fusion Reactor]--> +500 power
```

## Holding ground pays

The surface runs out and deep deposits run dry, so a long game is decided by who holds ground, not who hoards it.

- **Salvage from kills.** Anything an enemy destroys (with a unit, turret, mine, or a fire it set) leaves about 25% of its cost as ore on and around the spot: 10% for infantry, and a mining truck also spills its load. Costs convert as for any salvage (steel and iron ore to iron ore, circuits to two copper ore, plasma to two uranium; deployed structures count as the truck that made them). Nothing is left for selling, crashes, your own side's fire, or deaths with no enemy cause. Piles merge (a squad killed together leaves one pile), respect the 1,000-per-tile cap and never land on another ore. Anyone's trucks can collect it, so the winner has to hold the ground to profit. Both sides get one SALVAGE ON THE FIELD alert per area with the running total.

## Structures

The Command Center builds every structure. A new structure must be placed within 6 tiles of one of your existing structures, so you take territory by building outward or by deploying Outposts.

| Structure | Cost | Requires | Produces |
|---|---|---|---|
| Command Center | (start) | – | builds structures, trains Mining Trucks, ore drop-off, extracts 1 iron_ore/s, +20 power |
| Power Plant | 250 iron_ore | – | +100 power |
| Mining Refinery | 300 iron_ore, 100 copper_ore | power_plant | ore → steel and copper, ore drop-off, comes with a free Mining Truck |
| Barracks | 150 steel | mining_refinery | infantry |
| Factory | 300 steel, 100 copper | mining_refinery | ground vehicles, Outpost Trucks |
| Gun Turret | 150 steel, 30 copper | barracks | ground defense |
| Electronics Plant | 250 steel, 150 copper | factory | circuits |
| Radar Dome | 200 steel, 60 circuits | electronics_plant | reveals 16 tiles, detects stealth |
| SAM Site | 200 steel, 60 circuits | electronics_plant | anti-air defense |
| Optics Lab | 300 steel, 80 circuits | electronics_plant | lenses |
| Enrichment Plant | 400 steel, 120 circuits | electronics_plant | plasma |
| Laser Tower | 250 steel, 60 lenses, 60 circuits | optics_lab | heavy defense, hits air |
| Composite Foundry | 400 steel, 120 circuits | optics_lab | composite |
| Fusion Reactor | 600 steel, 200 circuits, 50 plasma | enrichment_plant | +500 power (burns plasma) |
| Airfield | 500 steel, 200 circuits | enrichment_plant | aircraft |
| Outpost | deploy an Outpost Truck | – | forward ore drop-off, territory anchor, vision 9 |

## Units

| Unit | Cost | Requires | Role |
|---|---|---|---|
| Mining Truck | 200 iron_ore | command_center | mines ore |
| Rifleman | 40 steel | barracks | cheap anti-infantry, weak AA |
| Rocket Soldier | 80 steel, 30 copper | barracks | anti-armor, anti-air |
| Medic | 60 steel, 20 copper | barracks | heals infantry for free (15 HP/s); auto-heals nearby wounded |
| Engineer | 120 steel | barracks | captures an enemy structure below 50% HP (used up) |
| Sniper | 120 steel, 20 lenses | barracks, optics_lab | range 9, one-shots infantry, useless vs armor |
| Commando | 300 steel, 50 circuits | barracks, electronics_plant | C4 levels structures, wrecks vehicles; can't fight infantry |
| Laser Trooper | 80 steel, 30 lenses | barracks, optics_lab | elite infantry, hits air |
| Scout Buggy | 100 steel, 20 copper | factory | fast, long sight |
| Light Tank | 200 steel, 40 copper | factory | all-rounder |
| APC | 250 steel, 40 copper | factory | carries 5 infantry, machine gun |
| Recon Drone | 120 steel, 30 circuits | factory, electronics_plant | fast unarmed flying scout, sight 12 |
| Flak Track | 250 steel, 40 circuits | factory, electronics_plant | mobile anti-air with splash; can't hit ground |
| Mine Layer | 250 steel, 50 copper | factory, electronics_plant | lays hidden mines (30 steel each) |
| Repair Truck | 180 steel, 60 copper | factory | repairs vehicles, aircraft and structures (30 HP/s, 1 steel per 10 HP); auto-repairs nearby damage |
| Outpost Truck | 400 steel, 100 copper, 50 circuits | factory, electronics_plant | deploys into an Outpost |
| Heavy Tank | 400 steel, 80 circuits | factory, electronics_plant | armor, splash |
| Artillery | 300 steel, 100 circuits | factory, electronics_plant | range 11, splash, fragile |
| Laser Tank | 350 steel, 60 lenses, 40 plasma, 60 circuits | factory, optics_lab, enrichment_plant | beam weapon, hits air |
| Mammoth Tank | 800 steel, 200 circuits, 40 plasma | factory, enrichment_plant | super-heavy, hits ground and air, self-repairs to 50% |
| Gunship | 300 steel, 120 circuits, 30 plasma | airfield | flies, rockets vs ground and air |
| Transport Chopper | 300 steel, 80 circuits | airfield | flies 6 infantry over terrain |
| Stealth Bomber | 300 composite, 150 circuits, 80 plasma | airfield, composite_foundry | flies, invisible unless within 3 tiles of an enemy or inside an enemy radar's range; devastating against structures |

Mines are hidden unless an enemy is within 1.5 tiles or inside its radar range, and they explode under enemy ground units. Cannons, artillery and bombs can't hit aircraft. Rockets, lasers, SAMs and gunships can. Rifles can too, but weakly.

## Inventions (agent-designed units)

A player can design a variant of an armed unit they can already build, with `propose_tech` (rules_version 9; the full design and numbers are in `docs/spikes/AGENT_INVENTED_TECH.md`):

- **What can change:** hp, speed, damage, range, cooldown, sight and fuel, each within limits around the base unit. The unit can also mount another buildable unit's weapon (`weapon_from`), e.g. a light tank with rocket-soldier rockets that can hit aircraft. It can't gain abilities: a ground unit can't fly, and nothing gains stealth.
- **Price:** computed relative to the base unit, never set by the player. No design out-fights its base unit per unit of cost, so inventions are counters and side-grades, not upgrades.
- **Research:** the research bill (steel and circuits, growing with how different the design is) is paid up front. Research happens at an `electronics_plant`, and the design is then trained by its key (`t<team>:<name>`) at its base unit's producer.
- **Limits:** 3 per player, one in research at a time, 24 per game. Only the inventing team can build a design; enemies see its stats when they meet it.
- **Looks:** an invention is drawn with its base unit's model and icon, plus an amber badge, and the selection panel says INVENTED.

### The central registry

Every design is recorded for later evaluation for permanent inclusion in the standard roster. The gateway polls each room's `GET /api/admin/inventions` and keeps `~/.config/pezz/inventions/`:

- `registry.json` has one record per design per game: the design, its price and research bill, the inventor (seat, player, controller), when it was proposed and researched, and its record (built, kills, lost, alive), plus how the inventor's game went.
- `proposals.jsonl` has every `propose_tech` call, including dry runs and rejections, with the validator's verdict.

`node mcp/inventions.js` prints the evaluation report: convergent designs (the same base and weapon invented by different players), the most-used designs by combat record, and rejections by guardrail. Add `--json` for the raw registry. None of this is served to players.

**Criteria for permanent inclusion** (a judgement made by people, not automatic):

1. **Convergence.** Several different players invented much the same thing. That suggests a gap in the roster rather than one player's quirk.
2. **Use.** It was built and fought in real games, not just researched.
3. **A counter, not a power spike.** Its record against its base unit's cost stays honest: inventors don't win just because they had it.
4. **Distinct.** It fills a role no standard unit already fills (for example, ground anti-air on a vehicle chassis).

An included design becomes a normal def in `Sim/Defs.cs`, gets its own art (or keeps its base model), and is announced in `mcp/changes.js`. Its price is re-derived for the standard roster rather than copied.

## Strategy

- **Economy:** trucks, then refineries. Without circuits there's no mid game, and without uranium there are no lasers or aircraft.
- **Expansion:** an Outpost Truck can claim a remote field. Its Outpost becomes a drop-off point and lets you build turrets around it.
- **Defense:** turrets, SAMs and laser towers protect what you own. Radar domes reveal stealth bombers.
- **Contested middle:** crystal and uranium sit between the bases, so you have to fight for the high tech.
