# Pez

A Command & Conquer-style real-time strategy game in Unity. You can play it with the mouse, or hand a team to an LLM (Claude, Codex, Grok, Gemini) and let the models fight each other.

This is an MVP spike. All the art is procedural, built from primitives, so the game runs with no imported assets.

## What's in the game

- **Economy:** harvesters mine ore, bring it to refineries and turn it into credits. Power plants supply power; low power halves production speed.
- **Base building:** construction yard, power plant, refinery, barracks, war factory and gun turret, with prerequisites. Buildings rise in place while they're under construction.
- **Units:** rifleman, rocket soldier, harvester, light tank and heavy tank. Weapons differ in range, cooldown and projectile, and do different damage against infantry, vehicles and structures. The heavy tank has splash damage.
- **Combat:** A* pathfinding, move, attack-move, focus fire, auto-targeting, retaliation, and fog of war per team.
- **Win condition:** destroy every enemy structure.

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
open unity/Build/Pez.app                      # menu: pick Human / Scripted AI / LLM per team
open unity/Build/Pez.app --args -team0 human -team1 ai -autostart
```

Controls:

- **Selecting:** left-click or drag-box to select. Ctrl+A selects all combat units.
- **Orders:** right-click to move, attack, harvest or set a rally point. F then right-click attack-moves. X stops, Del sells.
- **Camera:** WASD, arrow keys or the screen edge to pan, middle-drag to grab, Q/E to rotate, mouse wheel to zoom. Click the minimap to jump.

## Battle of the LLMs

```bash
# Headless (no graphics), fastest:
node arena/battle.mjs claude codex

# Watch it in Unity: start the game first, then attach. The game switches to spectator mode.
open unity/Build/Pez.app
node arena/battle.mjs claude codex --attach

# Other pairings and options
node arena/battle.mjs grok gemini --speed 0.5 --minutes 20
node arena/battle.mjs claude ai --attach --model-claude opus
```

Each agent runs non-interactively, has only the `pez` MCP tools, and is relaunched if its session ends before the game does. Logs and `result.json` go to `arena/logs/<timestamp>/`. The LLMs' `say` messages and their commands scroll in the bottom-left of the Unity window.

## Play against an LLM by chatting

You command Blue with the mouse, and the LLM gets Red through the MCP server:

```bash
claude mcp add pez -e PEZ_TEAM=1 -e PEZ_PLAYER=Claude -- node /path/to/pez/mcp/server.js
```

Then tell Claude something like "you're Red in Pez, crush me". You can also coach an LLM teammate through chat: give it your own team (`PEZ_TEAM=0`) and tell it what to do.

### MCP tools

| tool | what it does |
|---|---|
| `get_rules` | stats, costs, prerequisites, command reference |
| `get_state` | your credits, power, queues, structures and units with ids, visible enemies, ore fields, events since the last call |
| `get_map` | ASCII map from your team's view, with fog |
| `command` | a batch of commands: `build`, `train`, `move`, `attack_move`, `attack`, `stop`, `harvest`, `rally`, `sell`, `cancel`, `say` |
| `wait` | lets the game run 1–30 seconds, then returns the new state |

### HTTP API (port 7777)

`GET /` lists all the endpoints. The main ones:

- `GET /api/state?team=N`
- `GET /api/map?team=N`
- `POST /api/command?team=N` with body `{"commands":[...]}`
- `POST /api/admin/restart` with body `{"controllers":["llm","ai"],"seed":5}`

The API listens only on 127.0.0.1 and has no authentication. Any local process can control any team.

## Develop

```bash
cd headless && dotnet run -- --selftest              # AI vs AI to the end, in under a second
cd headless && dotnet run -- --controllers llm,ai    # headless server for MCP clients
/Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath unity -executeMethod Pez.EditorTools.PezSetup.Build -quit
```

The sim sources must stay C# 9 so Unity can compile them. The headless csproj enforces this.

## Next steps

- Real art: swap `Models.Build` for imported meshes, keeping the `Rig` handles. Move to URP with post-processing.
- Audio, unit veterancy, repair, walls, an MCV, superweapons, tech tiers, more maps.
- Per-team API tokens, so an LLM can only control its own team.
- Replays: the sim is deterministic apart from its RNG seeding, so recording commands is enough.
