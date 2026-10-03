# The kitchen sink

A secret, always-on live room that holds every unit and structure in every state the view can show, laid out as a
labelled gallery for art review. It runs its own private copy of the live build, so it always shows the art that's live.

> "Show me the kitchen sink" / "Let's see the live asset library":
>
> ```
> arena/kitchen-sink.sh open
> ```
>
> prints `https://seth-webster-m4.tail441c0f.ts.net:8456/` (tailnet only, no login) and the local URL.

## Commands

| Command | What it does |
| --- | --- |
| `arena/kitchen-sink.sh open` | Starts the room if it isn't running, makes sure the page is served on the tailnet, checks the URL answers and prints it. Fast and idempotent when it's already up. |
| `arena/kitchen-sink.sh status` | Running or not, game time, render fps, which build, and whether the live build has changed since. |
| `arena/kitchen-sink.sh restart` | Restarts it on a fresh copy of the live build. |
| `arena/kitchen-sink.sh stop` | Stops the room, its page and its tailnet serve. CI leaves it off until the next `open`. |
| `arena/kitchen-sink.sh ensure` | What CI runs: unless stopped by hand, starts it if it's down and restarts it if the live build changed. |

`PEZZ_SINK_APP=/path/to/Pezz.app arena/kitchen-sink.sh restart` runs a different build (a branch you built yourself) in
the same room; CI's next `ensure` puts it back on the live build.

### How it stays live

- **Its own process.** `~/pezz-kitchen-sink/app/Pezz.app` is an APFS clone of the live `unity/Build/Pezz.app` (read only;
  the live path is never run). It's launched with `-kitchensink -autostart -port 7947`, windowed at 1280x720, logging to
  `~/pezz-kitchen-sink/player.log`, and reniced to 10.
- **Ports.** Game API 7947, frames 7948, viewer page 7949, tailnet https 8456 (`tailscale serve`, never `funnel`). It is not
  registered with the gateway, not in the lobby, and never on the public site.
- **After every CI pass** (`arena/ci.sh`, every 3 minutes) `kitchen-sink.sh ensure` runs detached and niced, logging to
  `arena/logs/kitchen-sink.log`. Right after a deploy the live build has changed, so it restarts on the new build. It can't
  block, slow or fail a deploy. (A running `ci.sh loop` picks up a change to `ci.sh` when its LaunchAgent restarts:
  `launchctl kickstart -k gui/$(id -u)/com.sethwebster.pezz-ci`.)
- **Light on the M4.** No HUD, no player streams, 30 fps while someone watches the stream and 3 fps once nobody has for a
  minute (`View/KitchenSinkView.cs`). The scenario's sim costs about 0.15 ms a tick.

## The viewer page

`arena/kitchen_sink_web.py` serves `arena/kitchen_sink.html` on 7949: the live stream (drag to pan, scroll or pinch to
zoom, Q/E to rotate), the index of every district and exhibit (click to jump the camera; `/` to search), previous / next
(`[` `]`), a tour that visits every exhibit for 9 s each (`T`), the legend (`L`), and a speed bar (pause, ½×, 1×, 2×, 4×).
Each exhibit has its own URL fragment, so `…:8456/#fires-raging-8` opens on it. It speaks only to the room's own ports.

## The layout

The room is a 227x227 map built in code (`unity/Assets/Pez/Sim/KitchenSink.cs`), laid out for the game's default camera
(yaw 45): rows run straight across the screen. Twelve districts, 12 tiles apart, each framed by its index entry:

| District | What's in it |
| --- | --- |
| **Structures** | All 17 structures complete and powered (the deep mine on its deposit), Blueberry above Cherry: base buildings, then tech and defence. |
| **Works** (Lemon) | The command center's crane building a barracks (stake-out, stages 0–3 rising with stage dust, cream beam and sparks; loops every 28 s); a queued site with its pallet; a factory held at each stage landing (10%, 45%, 75%, 94%); a factory, barracks and airfield rolling units out on a loop (lamps, vent puffs, door, dark bay, airfield lift); two power plants. |
| **Economy loop** | Four drop-offs, one per ore: iron into a refinery, copper into a refinery, crystal into an outpost, uranium into a command center, three trucks each (harvest, queue beside the lane, align, reverse, unload with the ore-coloured pour, pull out). Two idle trucks parked and powered down. |
| **Power** | The same row of buildings on Blueberry (powered, with a fusion reactor) and on Lime (low power: plant flicker and dropouts, sputtering steam, dimmed consumers at half speed). |
| **Fires and damage** | Power plant, barracks and factory at 100%, 45% (smouldering), 25% (standing fire) and 8% (raging). The wind shows in their smoke and embers. |
| **Units** | Every unit idle, Blueberry above Cherry: infantry and the mine, vehicles, heavy armour with an invented unit per team (Hornet, Lancer, with the amber badge), aircraft hovering. |
| **Combat range** | Seventeen lanes, each a Blueberry shooter firing at a Cherry target that never takes damage: rifle, machine gun, rockets, laser, light cannon, heavy cannon, beam cannon, mammoth twin cannon, artillery (with a spotter drone), sniper, C4, flak, gunship rockets, stealth bomber, gun turret, SAM site, laser tower. |
| **Units in motion** | Eight lanes driving back and forth: infantry, buggy, light hull, heavy hull, super-heavy, artillery, laden truck, APC. |
| **Deep mining** | A deposit of each ore with its zone stake, deposits at two thirds and one third, a deep mine working and one exhausted, a surveyor surveying in turn at two spots, and a drill rig driving onto its zone and deploying (looping). |
| **Air and logistics** | Four aircraft on a circuit, a gunship that flies a beat then lands on its airfield, refuels and lifts off, an APC and a transport chopper loading and unloading infantry, and a stranded tank. |
| **Wrecks and explosions** | A vehicle destroyed every 6 s in one of four slots (each wreck lingers about 20 s), and nine explosion pads fired in turn (artillery, heavy cannon, rocket, bombs, mine, C4, vehicle, building, infantry) whose scorch marks build up. |
| **Terrain and ore** | Grass, dirt, rock and water; a full field of each ore; a field that runs down over 40 s, lies mined out for 12 s and regrows; and a mined-out field (the faded scar). |

Teams: Blueberry and Cherry everywhere they can sit side by side, Lime for the low-power base, Lemon for the works.

## How it holds still

Nothing here is staged in the view; it's the real sim, held in place:

- **Every team is protected** (`ProtectedUntil` forever), so nothing picks a fight or takes damage and mines never go off.
  The protection dome isn't drawn in this room. Shooters have explicit attack orders on their targets, and the defence
  structures are fired each tick by the hold, so the range fires forever without anyone getting hurt.
- **`KitchenSink.Hold()` runs after every tick** (the last line of `World.Step`, only when `World.Showcase` is set; real games
  never build a showcase, so their sim is unchanged). It keeps stockpiles full, puts pinned exhibits back on their spot and
  facing, sets health on the damage rows, holds construction progress, refills production queues and ore fields (except the
  depleting demo), tops up fuel (except the refuelling gunship and the stranded tank), and steps each looping demo's little
  state machine. One failing exhibit can't stop the others.
- **The headless test** `KitchenSinkRoom` (`cd headless && dotnet run -c Release -- --test-kitchen-sink`, also part of
  `--test`) builds the room and runs six game-minutes: every def is on show, nothing errs or dies, the entity count holds,
  every weapon fires, every ore docks, production, surveys, drilling, transports, refuelling and wrecks keep looping, and the
  power states hold.

## Adding a new asset or state

When something ships that the room doesn't show yet:

1. **A new unit or structure def** shows up as a failure of `KitchenSinkRoom` ("every unit and structure def is on show").
   Add its key to the right row in `KitchenSink.cs`: `BaseRow` or `TechRow` for structures, `Infantry`, `Vehicles`, `Heavy`
   or `Aircraft` for units. Rows place both teams and add the index entry. If it has a weapon, add a `Lane(...)` in
   `Combat()`; if it produces, add it to `Produce(...)` in `Works()`.
2. **A new state of an existing asset** goes in the district it belongs to. The building blocks:
   - `Bldg(team, key, a, b, progress)` and `Unit(team, key, a, b, facing, pin)` place things at district-local screen
     coordinates (`a` across the screen, `b` up it). Pinned units are held on their spot; unpinned ones are free to move.
   - `holds.Add(() => ...)` runs every tick: set whatever field the state depends on (health, progress, fuel, an order),
     or step a small state machine for a looping demo. Take any positions (`P(a, b)`, `Spot(...)`) *before* the closure:
     they're relative to the district being built.
   - `Item(name, states, a, b, zoom)` adds the index entry and its camera target.
   - A whole new district is a method that starts with `Begin(id, name, about, a, b, wide, tall)`; find it a free
     rectangle (12 tiles from its neighbours) and widen `A1`/`B1` if it doesn't fit.
3. **A state the view derives from something the sim doesn't hold** (a timer, a one-off event): drive it from a hold with
   the sim state that causes it, or emit the event on a cycle (see the explosion pads in `Wrecks()`).
4. Run `--test-kitchen-sink`, build, `PEZZ_SINK_APP=<your build> arena/kitchen-sink.sh restart`, and look at it.
   Add a line to the legend (`KitchenSink.Legend`) if the state needs explaining.

## Known gaps

- **Dirt reads as grass.** That's the art pack's quiet-ground rule (dirt is 2.5% darker), not a bug in the room; it's
  labelled so.
- **The factory's stage 0 and stage 1 look alike** at gameplay zoom: at 45% built it still reads as a bare pad, and the
  whole shell arrives in stage 2. Worth a look in the next construction review.
- **Aircraft in the units gallery** hover about 1.5 tiles up the screen, so their row is spaced wider and their shadows
  sit between the two teams' lines.
- **Effects the sim never causes on its own here** (an aircraft shot down, a structure captured by an engineer, mines
  laid) aren't on show; the explosion pads cover their blasts. Capture and minelaying could be added as loops.
