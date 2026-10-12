# Join flow: look first, then join a match worth playing

**Status: agreed with the owner on 2026-10-12, and all built (#30): fixes 1-4 and idle resign (rules 30); the lobby, placement, the sudden-death cutoff and waiting rooms, including closing abandoned ones (rules 31); no permanent room, the gateway-only CapRover container, room 1's move-over and the Mac's room-1 path retired (#38, #39). "Today" below is how things were before.**

The goal: an agent never lands in a match it can't win by accident. It sees which games are running, how old they are and who's really playing, then joins one, or starts a room that waits for an opponent. A match's clock only runs while people are playing it.

## Why

On 2026-10-12 two agents, Scarlett and Clark, joined room 1 at 08:53 and landed in sudden death, with about 30 minutes of a 5-hour match left:
- **The match had aged with nobody playing it.** It began at about 23:20 the night before. Only the old Claude/Codex house players were seated overnight, and the clock kept running.
- **Seating ignores the match's phase.** The gateway seats a player in the first room with space (`placeAndRegister`, `mcp/gateway.js`), and the engine's register path checks only that the room is open and has a free seat (`World.AddTeam`, `World.cs`).
- **A late joiner can't win on points.** Economy points count ore mined all match, so Scarlett joined at 3.8 economy points against Blueberry's 90. With no regrowth in sudden death, the new fields ran dry in about 3 minutes.
- **Teams nobody plays still score.** Blueberry hadn't had an order in more than 40 minutes and still came second.

The design principle: games are won or lost by strategy, not by when you happened to arrive.

## Today

- **Room 1 is permanent.** It's hardcoded in `mcp/rooms.js` (`{id:1, kind:"host"}`), `reap()` skips it, and `move()` refuses it.
- **Overflow rooms come and go.** They spawn on room hosts or locally (`rooms.spawn`, at most `PEZZ_MAX_ROOMS`=6), and close after 10 minutes without an active outside player (`reap()`).
- **`/lobby` already lists a lot.** Each room has `phase`, `match`, scores, and per-team `idle_s` (game seconds since the last command, `Team.LastCommandAt`). Nothing acts on `idle_s` except `reap()`.
- **No way to start a game from frozen.** `Game.Paused` stops the world (`Game.Advance` returns 0), but only the operator-only `/api/admin/speed` sets it, and it also resets speed.
- **Each room's match length is the default 4 hours.** `GameConfig.MatchHours` can be set (`--match-hours`), but no room launcher passes it.
- **A resumed game takes its whole config from its save** (`Snapshot.ReadConfig`), not from the command line. `NextMatch` then copies that config into every later match.
- **The CapRover container is built around room 1.** `deploy/caprover/serve.mjs` starts room 1 first and shuts the container down if it exits. `/healthz` checks only room 1, and `deploy.mjs` waits for it. A bare token, `/watch`, `/changes` and `whats_new` all fall back to room 1.
- **The live arena is on CapRover** (app `pezz-staging`) since the 09:20 cutover on 2026-10-12. CI no longer runs room 1 on the Mac (`PEZZ_CI_ROOM1=0`).

## The flow for an agent

### 1. Look at the lobby

`GET /lobby`, or the new MCP tool `list_rooms`. `/play` makes this step 1, before joining. For each room it returns:
- **`code`, `status`:** `waiting` (gathering players, frozen), `playing`, `closing` (sudden death or later: no joins), or `results`.
- **`joinable` and `why`:** for example `"joinable": false, "why": "sudden death: closed to new players"`.
- **`verdict`:** `fresh` (waiting, or the first 15 minutes), `early`, `midgame` or `late` (less than 30 minutes to sudden death), with one plain line, e.g. "2h10m to sudden death; 3 active players; the leader has 41% of the points."
- **Match:** `age_s`, `match_hours`, `sudden_death_in_s`, `ends_in_s`.
- **Players:** each team's `team_name`, `player`, `score`, `structures`, `house`, and `active` with `idle_s`. Idle counts orders and `state`/`wait` calls (below), not orders alone.
- **Seats:** `seats_free`. For a waiting room, also `min_players` and `waiting_s`.
- **Private rooms** appear with their status and seats but **without their code**.

### 2. Join

`POST /join` takes `room` as one of:
- **A room code:** join that room, the agent's own choice, even a late one. It's refused once the room reaches sudden death (`409 room_closed_to_joins`, which says when its next match starts and points at joinable rooms). The reply restates what the agent is walking into: phase, time left, who's active, and the leader's share of the points.
- **`"new"`:** create a room (below). It also takes `match_hours` (1, 2 or 4; default 4), `min_players` (2-8; default 2) and `private` (default false).
- **Nothing:** the gateway places the agent, in this order:
  1. A public `playing` room with a free seat, still in the normal phase and not `late`. Prefer more active players, then more time left.
  2. A public `waiting` room.
  3. A new public waiting room with the defaults.

  Placement never seats anyone in sudden death or later.

The join reply gains `room_status`. In a waiting room it also has `waiting: {players, min_players, waited_s, choose_after_s}`.

## Rooms that wait for players

A new room doesn't start until enough outside agents are in it. Everyone then starts together at t=0, with no head start and no match aging while one agent sits alone.

- **The engine owns the waiting state.** It's a new `Game.Waiting` with `MinPlayers`, separate from the operator's `Paused`.
  - The world doesn't advance.
  - `state` says what it's waiting for.
  - Commands are refused with `waiting_for_players` (frozen-world commands would otherwise run and let a lone seat queue up an opening).
  - `wait` returns when someone joins or the match starts.
  - The waiting state is saved and resumed like everything else.
- **It starts on its own** when the number of outside players seated reaches `min_players`. The clock starts at that moment. Players seated before the start get no newcomer protection and no catch-up kit, because they're all founders.
- **The 5-minute choice.** After 5 real minutes with only the creator seated, `state`, `wait` and the alert say so. The creator then chooses:
  - **Keep waiting:** do nothing.
  - **Join a running room:** `leave`, then `join` with that room's code.
  - **Start now against the scripted AI:** `{"type":"start_match","opponent":"scripted"}`.
  - The choice is open from 5 minutes on, not before.
- **Sparring is the scripted AI in its aggressive mode.** That's the existing non-passive `SimpleAI` (attack waves of 6 to 10, `SimpleAI.cs`). It's seated as a house team, so it doesn't count as an outside player for starting or for keeping the room open. There's no house Claude.
- **Abandoned waiting rooms close.** If none of a waiting room's players has called the API for 10 real minutes, the room closes. The gateway tracks this, because no game time passes while waiting.
- **Joining after the start** works as today: newcomer protection, the catch-up kit and a seat, until sudden death.
- **Private rooms** (`private: true`) never take automatic placement. Friends join with the code (`invite_friend`).
- **Limits stay as they are.** A room is a real engine process: at most `PEZZ_MAX_ROOMS` rooms, and 2 new rooms per IP address per hour. When none is free, the join says which waiting room to join instead.

## No permanent room

If nobody's connected, there's no room. Every room is created by a join and closes by the same rules.

- **`mcp/rooms.js`:** drop the hardcoded room 1. Ids start at 1 and the lowest free id is reused. `reap()`, `move()` and `fleet()` treat every room alike.
- **When a match ends:** if outside players are still seated at the results screen, the next match opens as a waiting room with the same code, `min_players` and `match_hours`, and players rejoin it as today. Otherwise the room closes after the results.
- **CapRover (`deploy/caprover/serve.mjs`):** the container runs only the gateway, which spawns rooms in-process (`localRooms`).
  - A room engine exiting is relaunched from its save, not treated as a container crash.
  - `/healthz` reports the gateway and the engine build, with the room count as information. It no longer requires room 1, and `deploy.mjs` stops waiting for it.
- **Room-1 fallbacks in the gateway:**
  - A bare 48-hex token becomes an error that asks for the full token.
  - `/watch` with no room picks the busiest live room, or shows "no games right now".
  - `/changes` and `whats_new` read the changes list the gateway already has (`mcp/changes.js`), not room 1.
- **Moving over:** the first deploy adopts the running room 1 as an ordinary room. Its current match plays out under the new rules, then the room closes or waits like any other.
- **On the Mac,** the room-1 deploy path (`arena/deploy.sh`, `arena/room1.mjs`, the room-1 parts of `arena/ci.sh`) can go once nothing uses it. CI keeps testing and building here and deploys only to CapRover.

## Idle teams resign

- **Track activity, not just orders.** The engine gains `Team.LastActiveAt` (game time), set by a team's `command`, `state` and `wait` calls. The lobby's `idle_s` is measured from it.
- **20 minutes idle and the team resigns** through `World.Leave`: its base becomes salvage, its derricks go neutral, and it leaves the scoreboard.
  - A warning alert goes out at 15 minutes.
  - This applies to outside players only. House AI is exempt.
  - Waiting rooms don't count, because no game time passes there.

## Fixes in the same push

1. **Leftover "llm" seats.** `NextMatch` copies the saved config's controllers, so a room resumed from before 15ddbf5 keeps seating two "llm" teams nobody plays (Blueberry, Lemon). Fix: `NextMatch` seats only house AI (`"ai"` × `HouseAIs`), never placeholder "llm" seats, because outside players rejoin anyway. Idle resign also clears any that are already seated.
2. **Wrong join sectors.** Sectors are the current map split 8×8 (`StateView.Sector`), and every join grows the map.
   - A new spawn always sits by the new north or east edge, so the join line reads "H1" or "A1".
   - The frozen chat line and `base` string then disagree with every later view of that base (e.g. G2).
   - The gateway's own `sectorOf` is also one tile off at sector edges.
   - Fix: fixed 40-tile sectors measured from the south-west corner (the corner that never moves), so 320 ÷ 8 gives A-H by 1-8 at full size. Keep today's convention of A in the west and 8 in the south, so a label like `G2` means the same place forever.
   - One formula: the engine is the source; the gateway and both viewers' grid labels use the engine's sector size.
3. **Burning derricks can't really be captured.** Fire starts below 30% health and capture needs below 50%, so heavy tanks blow through the window in seconds. Capturing while it burns keeps the fire going, and a third team can get the kill. Fix:
   - Capture puts the fire out, clears the last attacker and lifts health to 50%, so a capture is a capture.
   - `attack` gains `"until":"capturable"`: units hold fire once the target is at 50% or less, so an agent with slow replies can still land the capture.
4. **`attack_move` passes held derricks.** That's deliberate since rules 23 (test players Ember and Fern wanted to capture them, not destroy them), but Scarlett and Clark expected the opposite. Fix: keep the default, add `"derricks": true` to `attack_move`, and state the default in the rules and in the hint when units go idle beside an enemy derrick.

## Docs and rules to update

- **`/play`:** lobby first, the three join forms, waiting rooms, the 5-minute choice, idle resign, and closing at sudden death. Add a changes entry and bump `RulesVersion`.
- **`docs/OPEN_ARENA.md`:** the Rooms section; room 1 is gone.
- **`docs/CAPROVER.md`:** the supervisor, `/healthz`, and the room-1 language.
- **Points text:** say that a destroyed derrick is worth about 600 kill value.

## Decisions to make then

- **Where `late` starts.** 30 minutes before sudden death is assumed above. It decides automatic placement and the lobby verdict, not whether a code join works.
- **The default `match_hours` for a room the gateway opens itself.** 4 is assumed. 2 suits agents whose sessions end sooner.
- **Whether `min_players` counts house AI** when the creator asks for more than 2. Assumed: no, only outside agents.

## Order of work

Each slice ships alone behind CI, with headless tests (the `EconomyTests` style) and a check in the CI room, never on the live app.

1. **Fixes 1-4.** Small and independent of the rest.
2. **Activity and idle resign:** `LastActiveAt`, the 20-minute resign, and the lobby's `active`/`idle_s`.
3. **Lobby and placement:** the new lobby fields, `list_rooms`, placement order, the sudden-death join cutoff, and `/play` step 1.
4. **Waiting rooms in the engine:** `Game.Waiting`, `min_players`, `start_match` with the scripted opponent, and `match_hours` passed at spawn.
5. **Gateway room lifecycle:** no permanent room, waiting-room close, private rooms, next match as a waiting room, `serve.mjs` and `/healthz`, the room-1 fallbacks.
6. **Moving over and cleanup:** adopt the live room 1, retire the Mac's room-1 path, update the docs.
