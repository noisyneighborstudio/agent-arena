# Pez economy and tech tree

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
| Laser Trooper | 80 steel, 30 lenses | barracks, optics_lab | elite infantry, hits air |
| Scout Buggy | 100 steel, 20 copper | factory | fast, long sight |
| Light Tank | 200 steel, 40 copper | factory | all-rounder |
| Outpost Truck | 400 steel, 100 copper, 50 circuits | factory, electronics_plant | deploys into an Outpost |
| Heavy Tank | 400 steel, 80 circuits | factory, electronics_plant | armor, splash |
| Artillery | 300 steel, 100 circuits | factory, electronics_plant | range 11, splash, fragile |
| Laser Tank | 350 steel, 60 lenses, 40 plasma, 60 circuits | factory, optics_lab, enrichment_plant | beam weapon, hits air |
| Gunship | 300 steel, 120 circuits, 30 plasma | airfield | flies, rockets vs ground and air |
| Stealth Bomber | 300 composite, 150 circuits, 80 plasma | airfield, composite_foundry | flies, invisible unless within 3 tiles of an enemy or inside an enemy radar's range; devastating against structures |

Cannons, artillery and bombs can't hit aircraft. Rockets, lasers, SAMs and gunships can. Rifles can too, but weakly.

## Strategy

- **Economy:** trucks, then refineries. Without circuits there's no mid game, and without uranium there are no lasers or aircraft.
- **Expansion:** an Outpost Truck can claim a remote field. Its Outpost becomes a drop-off point and lets you build turrets around it.
- **Defense:** turrets, SAMs and laser towers protect what you own. Radar domes reveal stealth bombers.
- **Contested middle:** crystal and uranium sit between the bases, so you have to fight for the high tech.
