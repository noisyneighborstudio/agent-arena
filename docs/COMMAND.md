# Command: squads, objectives and a strategy that outlasts the turn

Agents play Pezz through an API, and they think slowly: a turn (read state, deliberate, send commands) often takes
30 seconds or more while the game runs at 20 ticks a second. In an agent-against-agent duel this lost games in two
ways: advances arrived unsupported, and mining trucks stayed exposed to raids, both while the next turn was still being
written. The duel agents looped `/state` → `/command` → `/wait` and used little of what already exists (the
observation feed and its wakes, `set_retreat`, `together`, rally points, controller leases).

This document is the design for letting an agent's *intent* keep working between its turns, and the plan to build it.
It is also the handoff: the **Status** section at the end says what's done and what's next, so any agent can pick it up.

## The principle: the executor is an instrument, not a player

Agents must win by commanding and conquering: by having a strategy, setting objectives from it and adapting it. The
sim carries out what they set and never decides for them.

1. **The floor stays where it is.** With nothing configured, the game behaves exactly as today: idle units fight what
   they see and return to their spot, mining trucks mine and unload, vehicles and aircraft refuel when low. Nothing
   retreats, nothing protects miners, nothing waits for support or reinforces. Nothing new runs unless the agent turns
   it on. An agent that configures nothing should do no better than today, and should lose to one with a good plan.
2. **No judgement in the executor.** It never chooses where to go, what to target, what to build, whether to fight or
   how much risk to take. It carries out conditions the agent wrote ("squad West holds here; a unit below 40% pulls back
   to there; if the escort dies, the miners go to that field and wake me"). Every threshold, place and budget comes
   from the agent. Defaults may exist for *how* (e.g. "back to base" when no retreat point is given) but never turn a
   behaviour on.
3. **Three layers; only the bottom two execute.**
   - **Strategy:** the agent's intent in words, plus priorities. Stored, versioned, shown to the agent's human and to
     spectators. Never executed.
   - **Objectives:** a squad, a place or target, a stance (`go`, `hold`, `escort`, `patrol`), guards, a budget and wake
     conditions. Executed literally.
   - **Orders:** today's commands.
4. **Objective verbs stay few and literal.** No `secure_mining_area` that decides how to secure an area. The strategy
   is in which objectives the agent combines and how it sets them. The executor must not grow into a second SimpleAI.
5. **Same tools for humans.** Anything an agent can set, a human commander can set (the viewer UI for it can come
   later).

## Architecture rules (all phases)

- **Everything lives in the Sim** (`unity/Assets/Pez/Sim`), shared by Unity and the headless engine: deterministic,
  stepped in `World.Step`, saved in snapshots (`Snapshot.cs`; `headless/SnapshotTests.cs` `SavedFields` must list
  every new Entity/World/Team field). Not in the gateway, not in a helper script. The browser's copy of the world
  replays logged commands (`World.LogCommand`, `Game.Replay`) and runs the same step, so new commands and executor
  state stay in step with the server for free, provided they are commands and sim state.
- **Only what the team can know.** Guards and wake conditions trigger on that team's knowledge: its own units, enemies
  it can see now, what it remembers (the same basis as `ObservationFeed`/`TeamLog` in `Observations.cs`). Never on
  the true world state.
- **One order source per unit, through `SetOrder`.** The executor issues ordinary orders (with cadence and
  stickiness); it never writes positions. See `docs/MOVEMENT.md`: every executor reaction needs an enter/exit band and
  a cooldown, or it becomes a dance at squad scale (miners shuttling between field and base every few seconds). Each
  guard gets a scenario in the movement gate (`headless/MovementGateTests.cs`, budget 0 settles).
- **A direct order wins.** A command naming a unit that belongs to an objective detaches that unit from it, and the
  feed says so (`detached_by_order`), until the agent hands it back.
- **Every intervention is explained.** Each executor action emits an observation event (own category, e.g. `cat:
  "orders"`, kinds such as `guard_triggered`, `objective_paused`, `decision_needed`) with the rule that fired, the
  facts it saw and what it did. `decision_needed` is `critical`, so the watcher and `observe` wakes deliver it.
- **Shipping.** Each phase that changes sim behaviour is a new rules version: bump `StateView.RulesVersion`, add a
  rules note in `StateView.cs`, an entry in `mcp/changes.js` (next id after the current `LATEST`), the command help in
  `Commands.cs` (`Help`), `/rules`, and the briefing in `mcp/gateway.js` (`briefing()`, section "Your plan has to
  outlast your turn") and the MCP instructions there.

## Phases

Each phase is shippable alone and lands on `main` only with `headless --test` green ("All tests passed") and the
`mcp` tests touched by it green. CI (`arena/ci.sh`, status in `ci-work/status.json`, log `arena/logs/ci.log`) deploys
`main` to pezz.sethwebster.com in about 10–15 minutes.

### P0. The briefing (done)

`/play` teaches strategy first, orders that keep working, and waking on the observation feed (commit `358144e`).

### P1. Turn safety: ready handshake, idempotency, stale commands

- **Ready handshake.** A waiting room starts when `min_players` outside players are seated *and ready*
  (`{"type":"ready"}`, or the MCP tool), or 120 s after the last required seat filled, whichever is first. The join
  reply, `wait` and the lobby say who is ready. `docs/JOIN_FLOW.md` updated.
- **Idempotency.** `/command` accepts `"request_id"` per batch (or per command); a repeat within 10 minutes returns
  the first result and does nothing. Kept per team, bounded, saved with the world.
- **Stale commands.** A batch may carry `"as_of_tick"` (the tick of the state it was decided on). The result lists,
  per affected unit, what changed since: destroyed, given another order (by whom), retreated. A command whose target
  is gone answers `obsolete` (with why) rather than a bare error. Nothing is rejected for being stale; the agent is told.

### P2. The slow-agent spike (measure before building more)

Per `docs/agents/spikes.md`, one question: *does each layer let a slow commander keep its plan working?* A headless
harness (`headless --slow-agent`, deterministic seeds) runs a scripted commander that uses only API-equivalent
commands and decides only every 30 s of game time, against SimpleAI and against itself. Metrics: miner losses while
undefended, units lost in fights with fewer than N allies nearby, alert-to-response time, final score. Baseline:
today's commands, with and without the P0 habits (`set_retreat`, `together`, rally). Re-run after P3, P4 and P5;
results in `docs/spikes/`. A layer that doesn't move the numbers is reconsidered before the next is built.

### P3. Miner protection, a team policy (opt-in)

`{"type":"set_policy","miners":{"withdraw_on_attack":true,"to":[x,y] | "base","resume_after_s":20}}` (and `false` to
turn it off). When a truck of the team is hit or an enemy combat unit the team can see comes within its weapon range of
a truck, the trucks working that field go to `to`, then resume mining after `resume_after_s` with no enemy seen near the
field (band: a raid must be seen gone, and a field can't be withdrawn from more than once per 20 s). Events:
`miners_withdrawn`, `miners_resumed`. Off by default.

### P4. Named squads

- `{"type":"squad","name":"west","add":[IDS]}`, `"remove":[IDS]`, `"disband":true`. A unit is in at most one squad.
  Dead units leave. Names: 1–24 chars, per team.
- Anywhere a command takes `"units"`, `"squad":"west"` (or `"units":"squad:west"`) addresses its live members.
- `rally` takes `"squad":"west"`: new units from that building join the squad (and go to its rally point or objective).
- `/state` and the observation snapshot list squads: members, strength (HP sum / at formation), centre, objective.
- No behaviour of its own: a squad is addressing and persistence.

### P5. Guards on squads

`{"type":"guard","squad":"west","retreat_below_pct":40,"hold_for_support":{"within":6},"screen":{"roles":["artillery"],"behind":4},"withdraw_below_strength_pct":50,"to":[x,y]}`;
each field optional, each off unless given. Every guard has a band and a cooldown and emits `guard_triggered` with its
reason. `hold_for_support`: the squad's leaders stop while members are more than `within` tiles behind (reuse the
`together` pacing). `screen`: units of those roles keep `behind` tiles behind the squad's front toward the enemy it
knows. `withdraw_below_strength_pct`: the squad falls back to `to` and raises `decision_needed`. Gate scenarios for
each guard: zero settles, no oscillation between hold and advance.

### P6. Objectives and decision packets

`{"type":"objective","id":"west-exp","squad":"west","verb":"hold","at":[x,y],"radius":6,"assemble":{"at":[x,y],"min":8},"reinforce":{"keep":{"rifleman":6,"light_tank":2},"budget":{"steel":800}},"wake":["squad_below_50","enemy_strength_seen>squad","objective_done"]}`.

- Verbs: `go` (attack-move there, then hold), `hold` (stay within radius, engage what comes in, return), `escort`
  (stay with a unit or squad, e.g. miners), `patrol` (loop waypoints). Assembly first if given: nobody advances until
  `min` members are at the assembly point.
- `reinforce` trains to keep the listed counts while the budget lasts, at the buildings rallied to this squad. Spending
  is counted and reported; an exhausted budget wakes the agent.
- When a wake condition fires, or the objective can't continue (escort dead, squad gone, budget spent), the objective
  pauses (units hold or follow their guards) and emits `decision_needed` (`critical`): a packet with the objective, why
  it paused, the facts that changed since the agent's last command, the threats the team knows of near it, and its open
  commitments (other objectives, queued production, reserves). Full entity data stays available through `/state`.

### P7. Strategy record, plan versions, and showing it

- `{"type":"set_strategy","text":"...","priorities":["expand east","deny their crystal"]}`: versioned (`plan_version`
  in state). A command batch may carry `"plan_version"`; if the strategy changed since (by the agent, or by its human's
  standing orders), the result says so. Strategy never executes.
- The human viewer (commander view and spectator) shows each team's strategy text, objectives and the latest guard
  interventions. This is view work: it follows `docs/art/ASSET_GUIDE.md`.

## Testing

- Headless tests per feature in `headless/` (wired into `--test`). Snapshot round-trip for all new state.
- Movement gate scenarios for every guard and verb (P3, P5, P6): zero settles, no hold/advance or withdraw/resume
  oscillation inside 60 s.
- Fog tests: a guard or wake condition never fires on an enemy the team can't see or remember.
- The spike (P2) re-run per layer.

## Working rules for whoever builds this

- Never `pkill -f pez-headless` (it kills CI rooms and other sessions' tests). Stop processes by PID or port.
- Tests that open ports use `FreePort()`; other worktrees run tests on this machine at the same time.
- Never print the ops key (`~/.config/pezz/ops-key`) or private per-seat view links.
- Commits end with `Co-Authored-By:` for the model that wrote them. Commit each phase to `main` when its tests pass,
  push, and watch `ci-work/status.json` until it deploys (if CI fails on an unrelated flake, delete `ci-work/failed` to
  retry).
- After every phase, update **Status** below (state, commit, what's next, open questions). That is the handoff.

## Status

| Phase | State | Commit | Notes |
|---|---|---|---|
| P0 briefing | done | 358144e | live |
| P1 turn safety | done; deployed | 40c928c (in e310cb9) | Rules 45: ready/grace, saved retry receipts, advisory stale/obsolete replies. Release build + full `--test`: All tests passed; MCP turn safety 2/2, lobby 12/12, observe 5/5; site/watcher 14/14. CI live e310cb9 at 16:43 EDT. |
| P2 slow-agent spike | done; deployed | 2111cb5 (in d797248) | 18 offline matches at rules 45, zero sim errors; full `--test`: All tests passed. Baseline and reproduction in `docs/spikes/slow-agent.md`. AI undefended miners 7.17 both; unsupported losses 19.67→18.33; all slow seats lose. Self-play unsupported losses 25.67→7.50. CI live d797248 at 16:51 EDT. |
| P3 miner protection | done; deployed | 77961b4 | Rules 46; full `--test`: All tests passed; 54 focused assertions, 4 zero-settle gates; 20 MCP tests. 18-match default floor exactly preserved. Plain self undefended miners 3.00→0.67; AI plain unchanged 7.17; P0 combinations worsen. Mixed verdict/reconsideration recorded in spike; CI live 77961b4 at 17:00 EDT (handoff 48c5ed4 live 17:05). |
| P4 squads | done; deployed | daf2e7a (in 31f77b5) | Rules 47; full `--test`: All tests passed; 34 focused squad assertions, MCP 5/5. 18 named-squad matches preserve every gameplay/economy/response metric; 18 default-floor matches exactly equal baseline. CI live 31f77b5 at 17:14 EDT. |
| P5 guards | done; deployed | c81ec57 + 82bf54f (in 6e821bc) | Rules 48 guards + 49 held-medic follow-up. Latest full `--test`: All tests passed; 65 focused assertions, 9 zero-settle scenarios, MCP 32/32. Final 18 guard games: zero own settles vs 8 unchanged floor; all floor metrics preserved. P0 vs AI unsupported losses 18.33→13.67; all slow AI opponents still win. CI live 82bf54f at 18:08 EDT; handoff 6e821bc live 18:13. |
| P6 objectives | verified; ready to ship | | Rules 50/schema 9: literal verbs, assembly, joint guard/base-intent writer, explicit reinforcement budgets and critical decision packets. Release build + full `--test`: All tests passed; 118 focused assertions, seven formal zero-settle gates (combined maximum one order write/unit/tick), 47 harness checks, MCP 12/12. 18 objective matches: zero errors/own settles; 18 floor matches exactly equal baseline. AI unsupported losses plain 19.67→14.17, P0 18.33→16.67; all slow AI seats still lose. |
| P7 strategy record + view | not started | | |

P6 conservative choices: radius is explicit and positive for every verb. `objective_done` is accepted on every verb but only `go` has a completion condition; `hold`, `escort` and `patrol` remain indefinite. Escort targets must be own units or another own squad, outside the escorting squad. Assembly releases once, additions enroll naturally, and explicit removal/transfer/disband pauses the affected objective. A native retreat also pauses with a critical packet; only an explicit command hands control back. Paid reinforcement stays pinned to its original compatible producer and rally squad. Full replacement authorizes a new budget; resume preserves spending.

**Supervision:** the build is delegated to a child agent (codex `gpt-6.1-sol`, high effort, started 2026-10-14) from
the T3 thread that wrote this doc, as T3 task
`node:delegated-task:command%3Amcp%3A4e640e0d-6108-4b2c-bc48-e821f55989b3%3Adelegate-task%3Apezz-command-build-r1`
(its brief: implement P1-P7 in order, test, ship each phase to main, update this table). A supervisor checks it with
`task_status` on that id and reviews each phase's commits against the principle and the architecture rules above. If the supervisor runs out, any agent continues from this table: read the
latest commits touching the files above, check `ci-work/status.json`, and carry on with the first phase not marked done.

**Next step:** commit/push the verified P6 implementation and spike, watch CI, then implement P7 strategy records, advisory plan versions and private/delayed human planning views. P6's final suite and measurement are green; no further executor behavior is needed for the observed mixed outcomes.

**Open questions:** P1 conservatively pauses the saved readiness countdown during host downtime, matching the existing waiting clock. Retry cache capacity refuses new keys before executing (512 receipts per team), preserving every accepted key for ten real minutes. Stale order facts use strictly later ticks: changes in the same tick as a supplied `as_of_tick` cannot be ordered relative to that state read without a separate revision. Own unavailable-unit history is bounded to the latest 2048 removals. P3: `base` is literal Team.StartPos; salvage-only tiles are excluded; fog may hold miners indefinitely; changing a destination affects future withdrawals/enrollment without reordering existing withdrawals. The chosen retreat point and lost mining time can worsen outcomes, especially with P0 habits: no automatic tactical fix was added. P4: exact case-sensitive names; conflicting selectors fail; an explicit add/remove (even empty arrays) reforms current-HP baseline, casualties preserve it and rally additions extend it; a rally omitting squad clears prior enrollment while preserving legacy producer-kind scope. P5: full configuration replaces omitted guards with off; HP/support use bands and cooldowns, HP healing never advances, strength withdrawal latches until replacement, direct orders exempt current units from that old latch. Miner withdrawal/refuelling/carried/stranded units take precedence; stationary guard firing stays in range. P6 budget choice: only listed resources are capped, but every kept unit type must consume a capped cost resource to keep training finite; ordinary selected-unit prices authorize uncapped cost items and all spending is reported (the document's steel-only tank example stays valid).
