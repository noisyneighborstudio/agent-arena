# Pezz

A Command & Conquer-style real-time strategy game in Unity. You can play it with the mouse, or hand a team to an LLM (Claude, Codex, Grok, Gemini) and let the models fight each other.

This is an MVP spike. All the art is procedural, built from primitives, so the game runs with no imported assets.

## What's in the game

- **Production-chain economy.** You start with a Command Center, one Mining Truck and a little raw ore. Trucks mine four ore types (iron, copper, crystal, uranium) into a team stockpile. Converter buildings refine ore into steel, copper, circuits, lenses, plasma and composite, and higher tiers cost those materials. Every building produces something. The full hierarchy is in [docs/TECH_TREE.md](docs/TECH_TREE.md).
- **16 structures and 23 units.** The range runs from riflemen, medics, engineers, snipers, commandos, APCs, mine layers and repair trucks to artillery, flak tracks, mammoth tanks, transport choppers, laser tanks, laser towers, SAM sites, gunships and stealth bombers.
- **Territory.** You can only build within 6 tiles of your own structures. Outpost Trucks deploy into forward bases that claim remote ore fields.
- **Fog of war.** The map starts black. Bases reveal a radius around themselves, units and trucks reveal what they pass, and radar domes reveal a wide area and detect stealth aircraft.
- **Combat:** A* pathfinding, aircraft that ignore terrain, weapons that can or can't hit air, splash damage, projectiles, beams and artillery arcs.
- **Win condition:** destroy every enemy structure.

## Art

The look comes from the v0.2 art handoff, "The Dispenser War". The handoff is in [docs/art/](docs/art/): `HANDOFF.md`, `BRIEF_v2.md`, the motion spec and the boards.

- **Models:** 34 glTF blockout models (16 structures, 14 units, 4 ore tiles) in `unity/Assets/Pez/Resources/PezModels/`, imported with glTFast.
  - The `M_Team` material on each model is tinted per team flavour: Blueberry, Cherry, Lime, Lemon.
  - The game drives the pack's `PezMotion` and `PezEmerge` scripts from the simulation: turrets aim, barrels recoil, doors roll up for new units, truck bins fill and tip, airfield lifts rise, and buildings rise in four stages. Destroyed units leave wrecks for a while.
- **Placeholders:** units the pack doesn't cover yet use procedural placeholder shapes. Those are the engineer, sniper, commando, APC, flak track, mine layer, mine, mammoth tank, recon drone and transport chopper.
- **Camera:** off-axis orthographic (yaw 45°, pitch 55°).
- **Terrain:** biscuit ground with a faint tile grid, kept within the handoff's 8% "quiet ground" rule. Cola lakes have a shore band and glints, and cotton-candy trees line the edges.
- **Rocks (handoff v0.3):** tiered licorice massifs with faceted boulders, built from the pathing grid by the pack's `PezCliffBuilder`. The dark rim is exactly where movement is blocked.
- **Brown patches (open decision 4):** they were cosmetic dirt tiles with no gameplay meaning, so they're now subtle ground variation. The other brown areas are the cola lakes, which are impassable.
- **HUD:** the sidebar uses the pack's icons, and the minimap shows sectors A–H (west to east) by 1–8 (north to south). Alerts and the LLM order feed include the sector.
- **Regenerating the blockouts:** the pack's generators are in `art-src/`.

## Layout

```
unity/Assets/Pez/Sim     pure C# simulation (no UnityEngine): map, units, combat, economy, scripted AI
unity/Assets/Pez/Api     local HTTP control API (HttpListener; requests run on the game thread)
unity/Assets/Pez/View    Unity layer: procedural terrain/models/FX, RTS camera, input, IMGUI HUD
unity/Assets/Pez/Editor  one-shot setup + macOS build
headless/                dotnet runner that compiles the same Sim+Api sources, so there's no Unity dependency
mcp/                     MCP server: gives one LLM control of one team
arena/                   battle.mjs, which pits agent CLIs against each other
```

Humans, the scripted AI and LLMs all send their orders through the same `Commands.Execute`, so no kind of player can do anything the others can't.

## Play

```bash
open unity/Build/Pezz.app                      # menu: pick Human / Scripted AI / Claude / Codex / External per team
open unity/Build/Pezz.app --args -team0 human -team1 ai -autostart -mapsize 112   # map 48–160 tiles per side
```

Controls:

- **Selecting:** left-click or drag-box to select. Ctrl+A selects all combat units.
- **Orders:** right-click to move, attack or set a rally point. With Repair Trucks or Medics selected, right-clicking a damaged friendly unit or building repairs or heals it. Right-clicking ore with trucks selected assigns them that ore type. F then right-click attack-moves. G deploys an Outpost Truck. Right-click your own APC or chopper with infantry selected to board it, and U to unload. Right-click an enemy building with engineers to capture it. M then right-click lays mines (Shift for 5). X stops, Del sells.
- **Camera:** WASD, arrow keys or the screen edge to pan, middle-drag to grab, Q/E to rotate, mouse wheel to zoom. Click the minimap to jump.

## Battle of the LLMs

The easiest way: open the game, set Blueberry to **Claude** and Cherry to **Codex**, and press Start. The game launches both CLIs itself and switches to spectator mode. To start straight into a battle, run `open unity/Build/Pezz.app --args -team0 claude -team1 codex -autostart`.

**Watching from another machine:** the game streams its screen, read-only, at `http://127.0.0.1:7778/`. To reach it from your tailnet, run `tailscale serve --bg --https=8454 http://127.0.0.1:7778`. The stream has no authentication, and it can't control the game.

From a terminal:

```bash
# Headless (no graphics), fastest:
node arena/battle.mjs claude codex

# Watch it in Unity: start the game first, then attach. The game switches to spectator mode.
open unity/Build/Pezz.app
node arena/battle.mjs claude codex --attach

# Other pairings and options
node arena/battle.mjs grok gemini --speed 0.5 --minutes 20 --map-size 112
node arena/battle.mjs claude ai --attach --model-claude opus
```

Each agent runs non-interactively, has only the `pez` MCP tools, and is relaunched if its session ends before the game does. Logs and `result.json` go to `arena/logs/<timestamp>/`. The LLMs' `say` messages and their commands scroll in the bottom-left of the Unity window.

## Play against an LLM by chatting

You command Blueberry with the mouse, and the LLM gets Cherry through the MCP server:

```bash
claude mcp add pez -e PEZ_TEAM=1 -e PEZ_PLAYER=Claude -- node /path/to/pez/mcp/server.js
```

Then tell Claude something like "you're Cherry in Pezz, crush me". You can also coach an LLM teammate through chat: give it your own team (`PEZ_TEAM=0`) and tell it what to do.

### MCP tools

| tool | what it does |
|---|---|
| `get_rules` | stats, costs, prerequisites, command reference |
| `get_state` | your stockpile with rates, power, converter status, queues, what you can build and its cost, structures and units with ids, visible enemies, explored ore fields by type, events since the last call |
| `get_map` | ASCII map from your team's view, with fog |
| `command` | a batch of commands: `build`, `train`, `move`, `attack_move`, `attack`, `stop`, `harvest` (optionally by `ore` type), `deploy`, `repair`/`heal`, `load`/`unload`, `capture`, `lay_mines`, `rally`, `sell`, `cancel`, `say` |
| `wait` | lets the game run up to 1–30 seconds, then returns the new state; returns early if a priority alert fires |

### Commander's orders

You can give each LLM standing instructions, like adding to its system prompt:

- **Before a match:** each LLM team in the menu has an instructions box. From the command line, use `-orders0 "rush with infantry"`.
- **During a match:** use the Commander's Orders panel. It's always shown when spectating; when you're playing, press **O** or use its button. From scripts, `POST /api/admin/orders?team=N` with `{"text": "..."}`.

The model sees the current orders as `standing_orders` in every `get_state`. When the orders change, its `wait` is cut short and its next response starts with a "📣 NEW ORDERS FROM YOUR HUMAN COMMANDER" banner. The prompt tells it that these orders take precedence over its own plans.

The agents' actual system prompts are fixed when they launch, so orders are delivered through the game instead. That works the same for every model and takes effect immediately.

### Priority alerts

The game raises alerts the way a human commander hears an alarm:

- **Critical:** a building under attack, a building destroyed, or a stealth bomber detected.
- **High:** mining trucks under attack, units hit while idle or on the move, or enemies spotted within 10 tiles of your buildings.
- **Medium:** fights your own army started, and casualty reports.

Repeated hits in the same area merge into one alert, so a long fight raises one alert, not hundreds.

For LLMs, `wait` returns early as soon as a high or critical alert fires. That response, and any other tool response arriving after a new alert, starts with a `⚠️ PRIORITY ALERT` banner. The banner says where it happened, what's attacking, which of your units and buildings were hit, and which of your combat units are within 18 tiles to respond. `get_state` also lists active alerts near the top.

Human players get a flashing alert banner (click it to jump the camera there), minimap pings, and alert lines in the feed. Spectators see both sides' alerts in the feed, so you can watch how fast each commander reacts.

### HTTP API (port 7777)

`GET /` lists all the endpoints. The main ones:

- `GET /api/state?team=N`
- `GET /api/map?team=N`
- `POST /api/command?team=N` with body `{"commands":[...]}`
- `GET /api/alerts?team=N&since=SEQ&min=high`
- `GET /api/wait?team=N&seconds=S&since=ALERT_SEQ`: a long-poll that returns early on a new priority alert
- `POST /api/admin/restart` with body `{"controllers":["llm","ai"],"seed":5,"map_size":112}`

The API listens only on 127.0.0.1 and has no authentication. Any local process can control any team.

## Develop

```bash
cd headless && dotnet run -- --selftest              # AI vs AI to the end in a few seconds, then reports what each side built
cd headless && dotnet run -- --test                  # scenario tests (repair truck, medic, alerts, interruptible wait)
cd headless && dotnet run -- --trace 2 --seed 23     # print one entity's state every second (debugging)
cd headless && dotnet run -- --controllers llm,ai    # headless server for MCP clients
/Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath unity -executeMethod Pez.EditorTools.PezSetup.Build -quit
```

The sim sources must stay C# 9 so Unity can compile them. The headless csproj enforces this.

## Next steps

- Real art: swap `Models.Build` for imported meshes, keeping the `Rig` handles. Move to URP with post-processing.
- Audio, unit veterancy, walls, an MCV, superweapons, tech tiers, more maps.
- Per-team API tokens, so an LLM can only control its own team.
- Replays: the sim is deterministic apart from its RNG seeding, so recording commands is enough.
