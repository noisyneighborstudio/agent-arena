# The observation feed

A human commander watches the screen the whole time. A player agent used to see the battlefield only when it called
`state` or `wait`, and missed what happened in between: an army that finished its target and stood idle, a contact
that crossed the edge of sight, a queue that ran dry. The observation feed closes that gap. It gives the agent a
continuous log of everything its side could notice, and leaves every decision to the agent.

Three principles:

- **Only what the player can know.** The feed is one team's fogged view. It isn't a debug log or a spectator feed:
  there is no all-seeing variant.
- **Observations, not instructions.** Severity and category are advisory metadata for filtering and waking. Nothing in
  the feed tells the agent what to do, and the agent may act, wait or ignore anything.
- **Delivery is separate from waking the model.** A stream can't wake an agent harness, so the feed is something to
  read (long-poll, SSE, MCP), and a separate bridge (`/watcher.py`, or `observe` with `wait_s`) hands control back
  to the model when conditions it chose are met.

## How it works

```
Sim (World.Step)                         ApiServer                 gateway                       agent
 ObservationFeed  ──every 1 s──▶ TeamLog  /api/observe  ──────▶  /observe (long-poll)    ──▶  watcher.py / MCP observe
  (per team: diff                (ring of    (long-poll,           /observe/stream (SSE)        (batches, then exits so
   own fogged view)               4000 obs)   cursors, gaps)       /view/<v>/observe[/stream]    the model decides)
 human orders, command results ─▶ logged at once (now:true)
```

- `unity/Assets/Pez/Sim/Observations.cs` holds `ObservationFeed` (one per world), `TeamLog` (one per seat) and
  `ObsFilter`. Every `IntervalS` game-seconds (default 1) each team's log diffs that team's view against what it last
  saw: its own units and structures, the enemies it can see, the enemy structures it knows of, its queues, economy
  and open commands. It also takes the game's events and priority alerts for that team. Human orders, command
  results and order failures are logged the moment they happen (`now: true`).
- `World.Emit` records, for each `destroyed` event, which teams could see the entity die (`GameEvent.SeenBy`). Only
  those teams are told it was destroyed.
- `World.SetOrder` reports which units a command touched, so each command gets the units it actually affected.
- `ApiServer` serves `/api/observe` and `/api/observe/snapshot`, `/api/controller` (leases) and `/api/admin/observe`
  (cadence). `/api/command` runs every command through `ObservationFeed.Track`.
- `mcp/gateway.js` exposes the feed to players (`/observe`, `/observe/stream`, `/observe/snapshot`, `/controller`)
  and to read-only viewers (`/view/<view token>/observe[/stream|/snapshot]`). `mcp/play.js` adds the `observe` and
  `controller` MCP tools. `mcp/watcher.js` serves `/watcher.py`, the bridge.

## Events

Every event has the same envelope:

```json
{"seq":57,"t":63.5,"tick":1270,"ms":1792000000000,"cat":"orders","kind":"command_completed","sev":"notice","src":"own","cmd":41,
 "type":"attack","duration_s":6.2,"outcomes":{"target_destroyed":3},"units":[11,12,13],"now_idle":[11,12,13],
 "text":"cmd 41 (attack) finished: 3 target_destroyed; 3 now idle"}
```

| field | meaning |
|---|---|
| `seq` | Monotonic per stream and contiguous, so a cursor is just the last `seq` you have |
| `t`, `tick` | Game time (seconds) and tick (20 per second) when it was logged |
| `ms` | Server wall clock (unix ms) when it was logged: receipt minus `ms` is delivery latency |
| `cat` | `orders`, `combat`, `recon`, `production`, `logistics`, `economy`, `match`, `human`, `chat`, `alert`, `heartbeat` |
| `kind` | What happened (below) |
| `sev` | `info`, `notice`, `warning`, `critical`. Advisory only |
| `src` | Provenance: `own` (your forces), `sight` (an enemy you see now), `memory` (what you saw earlier), `radar`, `server` (match, clock, restarts), `human` (your authenticated commander), `chat` (untrusted player chat), `command` (the reply to your command), `game` (an alert the game raised for you) |
| `now` | Delivered past any batching |
| `cmd` | The command it's about, if any |
| `text` | A readable line, with `detail=full` only |

The rest are kind-specific fields: entity ids, `pos` as `[x, y]`, and `[before, after]` pairs such as `hp` or `rate`.
Rows about a death carry `at_t`, the exact game time it happened.

### Kinds

- **Orders and movement:** `command_accepted` and `command_rejected` (immediate), `movement_started` (with `at_t`
  and `after_s`: when the units really moved), `waypoint_reached`, `units_finished` (each unit's outcome: `arrived`,
  `stopped_short` with `short_by`, `target_destroyed`, `target_lost`, `stopped`, `mining`, `idle_no_ore`,
  `patrolling`, `retreated`, `boarded`, `diverted:<order>`, `replaced_by_cmd_<id>`, `destroyed`, `done`),
  `command_completed` (with `now_idle`), `unit_idle` (a unit went idle without finishing an order of yours), `unit_arrived` (info: a new unit at its rally point, or one that stepped aside, now idle),
  `auto_order` (a unit acting on its own: responding to an attack, refuelling), `no_progress` (6 s on the way
  somewhere and no closer, not because it's fighting) and `progress_resumed`, `formation_split`, `support_behind`
  and `formation_regrouped`, `retreat_started` and `retreat_ended`, and `order_failed` (immediate: a drill, survey or
  capture that can't be done, or a trip out of fuel range).
- **Combat:** `engagement_started` and `engagement_ended` (fights clustered by place, with a summary), `damage` (per
  entity `hp` before and after, and `by`: the attacker if you can see it, otherwise `"not visible"`), `losses`
  (with the killer if visible), `enemy_destroyed` (only kills you saw), `defence_firing` and `defence_quiet`,
  `enemy_in_range` (a visible enemy entered your weapons' reach), `entered_defence_range` (your unit inside the reach
  of an enemy defence you know of, with `seen_now`), and `repaired`.
- **Reconnaissance:** `contacts_appeared` (with `last_seen_t` when a remembered one reappears), `contacts_moved`
  (5+ tiles since last reported), `contacts_lost` (left sight: a memory, never a kill), `radar_contacts` (positions
  only, as the radar shows them), `structures_discovered` (with `expansion` for bases, refineries, mines and
  derricks), `structures_missing` (you see the spot and it's gone: not seen destroyed), `structures_changed_hands`,
  `ore_fields_discovered`, `zones_flagged`, and the `derrick` kinds.
- **Production and logistics:** `construction_placed`, `construction_started`, `construction_completed`,
  `training_started` (with `eta_s`), `production_progress` (25/50/75% for items of 20 s or more), `unit_trained`
  (with `spawn` and `rally`), `queue_empty`, `queue_blocked` and `queue_unblocked`, `research_started` and
  `researched`, `fuel_low` (crossing 50/25/10%), `refuel_trip`, `refuelled`, `landing`, `stranded`, `crashed`,
  `no_landing_pad` and `landing_available`.
- **Economy and bases:** `rates_changed` (an item's net rate flipping or moving by at least 30%, checked every 5 s),
  `power_changed` (used/produced before and after), `power_shortage`, `power_restored`, `defences_offline` and
  `defences_online` (upkeep), `miner_idle`, `delivery_congestion` and `delivery_clear` (trucks queueing for a bay),
  `depleted`, `surveyed`, `drilled`, `captured`, `structure_captured` (yours, by an enemy), `fire` and `fire_out`,
  `structure_repaired`, `structure_sold`, and `infrastructure_exposed` and `infrastructure_covered` (an armed enemy in
  sight within 10 tiles, and nothing armed of yours there).
- **Match and human input:** `stream_started` (with the reason: `new_game`, `joined`, `resumed` after a server
  restart, `new_seat`), `joined`, `left`, `defeated`, `game_over`, `match` (clock stages), `arena_cleared`,
  `server_message`, `seat_ended`, `controller_changed`, `human_orders` (`src: human`, `authenticated: true`, the
  `version` and full `orders`; immediate), and `chat` (`src: chat`, `trusted: false`, `from`, `message`). Human
  orders and player chat are different categories and different provenance, and are never mixed.
- **Alerts:** every new priority alert the game raises for the team (`cat: alert`, `kind` = the alert kind,
  severity from its priority).
- **Heartbeat:** every `heartbeat_s` (default 5) even when nothing changes. It carries unit count, idle ids, moving,
  firing, structures, what's building and training with percentages, stockpile, rates, power, visible and remembered
  contacts, engagements, open commands, stranded units, `orders_version` and `rules_version`.

Movement and damage are coalesced: one `damage` event per sample listing every hit entity, a contact reported moving
only after 5 tiles, and transitions (started, stopped, idle, entered range) rather than positions every tick.

## Reading

`GET /observe` (gateway, `Authorization: Bearer <token>`), `GET /view/<view token>/observe` (read-only), or
`GET /api/observe?team=N` (engine, local only). Parameters:

| param | meaning |
|---|---|
| `cursor`, `stream` | Where you are: the `next_cursor` and `stream` of your last read. Omit both for a fresh start |
| `wait` | Long-poll up to this many seconds (0-60, default 0) |
| `min_wait` | Gather at least this long before returning; immediate events (`now`) still end it at once |
| `wake`, `wake_kinds`, `wake_cats` | What ends a wait early (default: anything delivered) |
| `cats`, `exclude`, `kinds`, `min` | What's delivered: categories, categories or kinds to drop, only these kinds, severity floor (immediate events skip the floor) |
| `detail` | `full` adds `text` |
| `limit` | At most this many events (default 500); `more: true` says there's more |
| `snapshot=0` | Don't attach a snapshot to a gap |

The reply has `stream`, `events`, `next_cursor`, `head`, `first` (the oldest seq kept), `t`, `server_ms`,
`interval_s`, `heartbeat_s`, `waited_s` and `gap`. Filtered-out events still advance `next_cursor`.

**Cursors belong to the consumer.** The server keeps no per-reader state, so a read never consumes anything: the
human's dashboard on the view link, a second observer, and the player each read at their own pace. The MCP session
and the watcher each keep their own cursor (the watcher's is in `~/.pezz/observe-<hash>-<name>.json`).

**Gaps are explicit.** The stream id is `<game>.<team>.<seat>.<nonce>`, and the nonce changes whenever a feed is
created: a process start, or a game resumed from its save. A read whose cursor can't continue gets `gap` with a
`reason` and a fresh `snapshot` (your units and structures, enemies in sight, remembered contacts with `last_seen_t`,
known structures, queues, stockpile, power, standing orders, open commands, match), plus the cursor to go on from:

- `server_restarted`: same game and seat, new process.
- `new_game` or `new_seat`.
- `retention`: the cursor is older than the newest 4000 events. `missed_from` and `missed_to` say exactly which
  were lost, and the retained ones after them are still delivered.
- `cursor_ahead`.

A first read with no cursor returns `fresh: true` and a snapshot. `/observe/snapshot` returns one any time.

**Streaming.** `GET /observe/stream` (or `/view/<v>/observe/stream`) is server-sent events. Event types are
`snapshot`, `gap`, `observation`, `heartbeat`, `cursor` (filtered events moved the cursor), `unavailable` (the engine
is restarting; it retries) and `end` (the token stopped working). Every event's `id` is `<stream>:<seq>`, so a
reconnecting `EventSource` resumes with `Last-Event-ID`, and a reconnect after a restart gets a `gap`. Each token or
view link may hold 4 streams open at a time.

## Cadence

- **Sampling:** every `interval_s` game-seconds (default 1; 0.25-10). Set it with
  `POST /api/admin/observe {"interval_s":1,"heartbeat_s":5,"retention":4000}` (host only).
- **Immediate:** human orders, command acceptance and rejection, order failures and controller changes are logged
  when they happen, and end any long-poll at once.
- **Per consumer:** `min_wait` batches, `wake` and `wake_kinds` choose what's worth waking for, and filters and
  `detail` choose how much comes. Nothing is delivered per tick.

## Commands

Every result from `POST /command` now has `cmd` (its id: the `seq` of its `command_accepted` or `command_rejected`
event), `units` (the ids it affected), `not_affected` (ids you named that it didn't touch), and `tracking` (`units`,
`production`, `construction` or `none`). Accepted is not proof of movement: `movement_started` reports when the units
really moved (`after_s`). `units_finished` gives each unit's outcome, and `command_completed` closes the command and
lists the units now idle. A unit given a new order is reported as `replaced_by_cmd_<id>` on its old command, so
replacing orders is always safe. A unit that stops on its own shows as `unit_idle`, `no_progress`, `stopped_short` or
`diverted:<order>`. Train commands complete when their units are out, and build commands when the structure is
finished or lost.

## One controller per team

`POST /controller {"holder":"player","ttl_s":120}` returns a `lease`. While it's live, `/command` requires it in
`X-Pezz-Controller` and refuses anything else with `409 controller_conflict`, which names the holder and when the
lease expires. Commands renew it, and it lapses after `ttl_s` without one. `{"force":true}` takes over,
`{"action":"release","lease":"..."}` gives it up, and `GET /controller` shows it. Every change is a
`controller_changed` event. Readers (state, wait, observe, view links) never need the lease. Leases are optional: a
team nobody has claimed takes commands from its token as before.

## Waking the model: the bridge

A connection can stream events, but it can't start a model turn. Two supported bridges hand control back:

- **`/watcher.py`** (Python 3, standard library only) long-polls `/observe` with its saved cursor, gathers at least
  `--batch` seconds (default 1), and exits with a compact report once something at `--wake` severity or above
  arrives (default `notice`), or a `--wake-kinds` kind, or `--every` seconds pass (default 90; it then prints the last
  heartbeat). Your human's orders always wake it. It saves the cursor only when it reports, so a run that's killed
  loses nothing. Exit codes: 0 report, 2 seat gone, 1 gateway unreachable. With `--view <view token>` it reads
  read-only, and `--name` keeps a separate cursor. Run it in the background (Claude Code: Bash with
  `run_in_background`); the agent reads the report, decides (no action is a valid choice), and runs it again.
- **MCP `observe`** with `wait_s` (and `wake`, `wake_kinds`, `min_wait_s`) is the same for harnesses that only speak
  MCP: one tool call returns when something worth a decision arrives.

## Fog of war

The feed reports only what the team could know:

- Enemy units appear only while `World.IsVisibleTo` holds for that team. That includes the game's muzzle-flash rule:
  a unit that just fired at you is visible for 3 s. After that they're `contacts_lost`, kept as memories with
  `last_seen_t` and `last_pos` for 10 minutes.
- A destruction is confirmed only for teams that saw the entity die (`SeenBy`, taken at the moment of death). A
  death in the fog is never reported. A remembered structure that's gone is `structures_missing` only once its spot
  is back in sight.
- Attackers, killers and threats are identified only if visible at the time, otherwise `"not visible"` with no
  direction or position.
- Radar contacts carry positions only (no id or type), exactly what `radar_contacts` in state shows.
- Joins, leaves and eliminations name who, never where. Another team's production, economy, orders and chat
  provenance stay theirs. Human orders reach only their own team.
- There's no team −1 (spectator) feed: `/api/observe` without a team or token is refused. A recycled seat starts a
  new stream, so a new occupant never reads the old one's log.
- Ore fields are reported from explored ground only, and mining zones from the team's own surveys.

`ObserveFog` in `headless/ObservationTests.cs` checks these, and `mcp/observe.test.js` checks that one player's
snapshot doesn't contain another's base.

## Measured latency

Measured on 2026-10-12 on an Apple Silicon Mac: a real-time headless game at speed 1, the default 1 s sampling, and
clients on loopback.

- Engine-level numbers come from `headless/ObservationTests.cs` (`ObserveHttp`), which plays 5 rounds of: attack a
  fresh target over HTTP, long-poll until `command_completed`, issue the next move at once.
- The gateway number comes from `mcp/observe.test.js`.

| measure | result |
|---|---|
| Target destroyed → army's `command_completed` (outcome `target_destroyed`, `now_idle`) logged, game time | p50 400 ms, max 450 ms (n=5); always within one sample |
| Logged → agent holds it, engine long-poll | p50 0 ms, p95 4 ms, max 4 ms (n=55) |
| Logged → SSE client holds it, through the gateway | p50 1 ms, max 3 ms (n=4, live events) |
| Human orders sent → waiting reader has them | under 300 ms at the engine and under 1.5 s through the gateway (asserted; immediate, no batching) |
| Command → units actually moving (`movement_started.after_s`) | 50 ms (one tick), every time (n=10) |
| Command POST → `movement_started` delivered | about 1000 ms: the movement happens within a tick, and its report waits for the next 1 s sample |

So the agent learns that an order finished and the army is idle within about one second of the kill, plus network
and gateway time. It can issue the next command right away, and the units start moving on the next tick. What these
numbers don't include:

- The model's own turn time, which dominates in practice and is outside the game.
- Public-internet latency to a deployed gateway.
- Behaviour under a very large fight.

Lower `interval_s` trades CPU for quicker transition reports.

## Tests

```bash
cd headless && dotnet build -c Release && dotnet bin/Release/net10.0/pez-headless.dll --test-observe   # also part of --test
node --test mcp/observe.test.js mcp/watcher.test.js   # gateway on the real engine; the watcher against a fake gateway
```

They cover these scenarios:

- An army destroys its target, finishes and goes idle, with the next command following (and the latency printed).
- A replaced order.
- Fog: hidden enemies, sightings, leaving sight, a death in the fog.
- Production through to completion, and a rejected command.
- Blocked movement (`stopped_short`) and a pinned unit (`no_progress`).
- Retention gaps, independent cursors, filtered cursors, and a restart gap.
- Over HTTP: human-order interruption, chat provenance, controller leases.
- Through the gateway: SSE resume with `Last-Event-ID`, a view-token reader's own cursor, and fog between two players.

## Limits

- The feed isn't saved. After a restart readers get a `server_restarted` gap and a snapshot, by design. Command
  tracking also restarts, so commands from before the restart get no completion events.
- Only API commands get ids. Mouse orders in the Unity app and the scripted AI's orders aren't tracked, though their
  effects (idle units, fights) still show.
- Sampling at 1 s coalesces anything that starts and ends between samples. A unit that moves and stops within one
  second shows its final state. Exact times are kept where they matter (`at_t` on deaths and movement starts).
- `harvest` completes when each truck reaches its ore (`mining`), and patrols when they set off (`patrolling`).
  Later stops show as `miner_idle` or `unit_idle`.
- Retention is by count (4000 events per seat), not by time.
- Cost: sampling every team every second took 0.3 s of 9 s of simulation for 30 game-minutes of a 4-team AI game on
  the 320-tile map (`--selftest --profile`), about 3% of sim time.
