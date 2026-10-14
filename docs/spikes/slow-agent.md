# Slow commander: does intent survive a 30-second turn?

## Question and decision criterion

Does each command layer let a commander that decides only every 30 game-seconds keep its explicit plan working between turns? The baseline compares today's commands with and without the P0 habits; later runs enable P3, P4/P5 and P6 on the same commander and seeds. A layer earns continuation if it reduces the failure it targets without hidden information, unsolicited tactical decisions or new movement oscillation. A small three-seed result establishes a repeatable signal, not statistical confidence or a general balance verdict.

## Setup held constant

Run on 2026-10-14 on the local Apple Silicon machine, offline and without an API listener or live arena. The baseline loads rules 45 after P1 implementation, before P3; the measured sim/API source is the P1 implementation committed as `40c928c` (the harness was measured before that commit was created). P1 changes turn transport safety, not the combat/mining plan measured here.

- Seeds 1, 2 and 3; ordinary 80 × 80 starting worlds, normal ore richness, no extra grants or staged armies.
- Twelve game-minute cap; stop naturally at knockout. SimpleAI games end at 424.35–549.25 seconds (7.07–9.15 minutes); all self-play games reach 720 seconds. The early endings are results, not artificially prolonged matches.
- Eighteen games: per seed and variant, slow vs SimpleAI in each seat plus slow self-play. Six measured slow seats in each opponent/variant group. The two seats in a self-play game are correlated.
- Plain/P0 comparison order alternates by seed; opposing seats use the same seed. One process, sequential games, same build and machine. The complete matrix took 10.7 seconds of wall time.
- The commander decides at ticks 0, 600, 1200, … (30 seconds at 20 Hz). Every order passes through `CommandSafety.ExecuteBatch`, the same command execution and observation tracking used by the API.
- The commander reads `StateData.Team` and its own fogged observation feed. It never reads enemy `StartPos`, hidden entities or opponent economy. A mirrored map coordinate is a fixed exploration choice using public geometry, not hidden knowledge.
- Static economy plan: power, mining refinery, barracks, factory; six trucks split iron/copper; two tanks and three riflemen per production batch when available. Idle groups of at least four attack visible or remembered targets, otherwise explore the mirrored map coordinate. Combat alerts produce one chosen defence order per turn; threatened nearby trucks receive an explicit move home.
- P0 changes only `set_retreat` at 40% for own units, `together:true` on army orders and producer rallies ten tiles toward the map centre. No miner policy, squad guard or objective is enabled.

### Metrics

The metric observer is separate from the commander and cannot send orders. It reads exact `destroyed` events and the own-unit positions immediately before the fatal tick (at most 50 ms earlier). An undefended miner death has no other armed mobile ally within 8 tiles. An unsupported armed-unit death has fewer than three other armed mobile allies within 8 tiles. Defences are excluded from this deliberately mobile-support metric; simultaneous deaths use the same before-tick roster.

Alert latency starts at the original `Alert.StartTick`, not receipt or the last merged hit. The counted kinds are `harvester_under_attack`, `units_ambushed`, `base_under_attack`, `enemy_near_base`, `combat`, `units_lost` and `structure_lost`. A response is a successful explicit defence/withdrawal order chosen from the fog feed; that order covers alerts delivered in the same turn within 12 tiles of its selected fight. A state read, unrelated production, movement starting and automatic retreat are not commander responses. Other alerts remain unresolved, including ones raised after the final turn. Percentiles use nearest rank; missing latency is null rather than zero. Later layers should report executor intervention latency separately from commander response latency.

Final score is the production `World.Scores()` value at the cap or knockout. Defeated seats are absent from the scoreboard and recorded as zero. Both seats' mean score in symmetric self-play is necessarily 150, so that group mean cannot establish stronger play.

## Baseline results

Committed raw input: [slow-agent-baseline.jsonl](slow-agent-baseline.jsonl). Each row records its rules version, seed, variant, opposing controller, seat, elapsed time, error count and per-seat metrics, including every resolved latency. All eighteen games recorded zero sim errors.

All cells below are **mean / p50 / p95 across six slow seats**, except alert response latency, which pools individual resolved alerts.

| Opponent | Variant | Undefended miner losses | All miner losses | Unsupported armed losses | All armed losses | Final score |
|---|---|---:|---:|---:|---:|---:|
| simple_ai | plain | 7.17 / 7.00 / 8.00 | 8.00 / 8.00 / 9.00 | 19.67 / 19.00 / 28.00 | 28.33 / 26.00 / 35.00 | 0.00 / 0.00 / 0.00 |
| simple_ai | p0_habits | 7.17 / 7.00 / 8.00 | 7.67 / 8.00 / 8.00 | 18.33 / 18.00 / 21.00 | 27.33 / 26.00 / 32.00 | 0.00 / 0.00 / 0.00 |
| self | plain | 3.00 / 0.00 / 9.00 | 3.50 / 0.00 / 10.00 | 25.67 / 19.00 / 41.00 | 37.67 / 32.00 / 47.00 | 150.00 / 138.00 / 189.00 |
| self | p0_habits | 0.17 / 0.00 / 1.00 | 0.83 / 0.00 / 5.00 | 7.50 / 3.00 / 20.00 | 38.00 / 38.00 / 44.00 | 150.00 / 147.00 / 172.00 |

| Opponent | Variant | Alerts | Responses | Unresolved | Response seconds (mean / p50 / p95) |
|---|---|---:|---:|---:|---:|
| simple_ai | plain | 219 | 102 | 117 | 14.20 / 14.30 / 27.30 |
| simple_ai | p0_habits | 241 | 113 | 128 | 14.12 / 14.00 / 27.10 |
| self | plain | 292 | 238 | 54 | 15.88 / 16.90 / 27.40 |
| self | p0_habits | 294 | 260 | 34 | 13.12 / 12.55 / 27.55 |

P0 reduces unsupported self-play losses from 25.67 to 7.50 per seat (70.8%) and undefended miner losses from 3.00 to 0.17 (94.4%). Total armed-unit losses stay essentially flat (37.67 → 38.00): cohesion changes which deaths occur with nearby support, not whether this script wins. Against SimpleAI, undefended miner losses stay at 7.17; unsupported losses improve only 6.8% (19.67 → 18.33). All twelve slow-vs-SimpleAI games end with the slow seat eliminated, with or without P0 habits.

## Where it did not win and limits

The P0 habits do not protect this script's miners against a faster strategic opponent and do not beat SimpleAI. A 30-second commander delay remains visible: resolved alerts have pooled p95 latencies around 27 seconds, and 117/219 plain vs AI alerts and 128/241 P0 vs AI alerts remain unresolved. Self-play improves spatial support, but cannot prove a win-rate advantage because both sides use the same variant.

This is one deliberately simple scripted plan, not a real LLM, a strong tournament player or a model of network latency. It has no expansion, engineering, air defence or late tech, and SimpleAI's broader economy/army choices can dominate transport and support improvements. Alert response acceptance does not promise tactical success or movement completion. Some command batches reject after an earlier purchase spent resources because affordability was read before the turn's batch; rejections are retained in raw results. The observed game lengths are shorter than the requested 10–15 minute measurement window against AI because the ordinary match ends on elimination; self-play reaches twelve minutes. Three seeds and nearest-rank p95 over six seats are coarse, and the outcome needs more seeds before balance claims.

The explicit instruction to work and commit on `main` overrides the general spike-branch instruction in `docs/agents/spikes.md`. Nothing ran on the live arena, `unity/Build/` or any occupied port. This numerical spike has no new rendered state; there is no visual comparison recording. The deterministic harness and committed raw results provide the comparison evidence.

## Verification and reproduction

```sh
cd headless
dotnet build -c Release
dotnet run -c Release --no-build -- --test
dotnet run -c Release --no-build -- --slow-agent --slow-agent-layer baseline \
  --slow-agent-seeds 1,2,3 --slow-agent-minutes 12 \
  --slow-agent-out ../docs/spikes/slow-agent-baseline.jsonl
```

`SlowAgentTests.cs` locks the 30-second cadence, empty/nearest-rank latency statistics, undefended miner classification, the support radius/count threshold, unresolved/response accounting, fog-safe command choice and repeated deterministic match outputs. It is wired into `--test`. Full-suite verification is recorded by the phase owner with the P2 commit.

## Decision and next measurements

**Go to P3:** miner protection has a clear baseline failure to address: 7.17 undefended miner deaths per slow seat against SimpleAI despite existing retreat/cohesion/rally habits. Keep its policy opt-in and explicit; this result authorizes no automatic tactical player. Re-run the same matrix after P3, then P4/P5, then P6, recording separate raw files and appending the numbers here. Prefer per-seed paired changes over pooled win claims. If a layer does not improve its targeted losses or plan continuity, record the result and revisit that design before the next layer.

Resume after the P2 phase commit: add only the shipped layer's explicit setup commands to the harness, preserving the baseline choices and metrics. At P2 the CLI accepted `baseline` only, so an unimplemented layer could not silently receive a baseline label. Shipped opt-in layers are added explicitly below.

## P3 rerun: explicit miner withdrawal (rules 46)

Run on the same machine and seeds after P3's sim source was frozen and its dedicated tests/gates passed. The `miners` layer adds exactly one first-turn command per slow commander: `set_policy` with `withdraw_on_attack:true`, explicit `[home.x,home.y]` destination and `resume_after_s:20`. Every other commander decision stays unchanged, including later direct orders taking priority over policy. Plain/P0 and the opposing seats still alternate as in P2. No player behaviour is added to SimpleAI.

Raw results: [slow-agent-miners.jsonl](slow-agent-miners.jsonl). Eighteen games, zero sim errors, rules 46. Slow-vs-AI knockouts occur at 431.85–549.35 seconds; self-play reaches 720 seconds. This matrix took 11.5 seconds wall time. A separate default-off matrix [slow-agent-floor-rules46.jsonl](slow-agent-floor-rules46.jsonl) reproduced **every one of the eighteen P2 match/seat metrics exactly**, after excluding the rules-version label. The floor-check process ran concurrently; these wall times are run metadata, not a performance comparison.

Cells remain mean / nearest-rank p50 / p95 across six slow seats.

| Opponent | Variant + miner policy | Undefended miner losses | All miner losses | Unsupported armed losses | All armed losses | Final score |
|---|---|---:|---:|---:|---:|---:|
| simple_ai | plain | 7.17 / 6.00 / 10.00 | 8.50 / 8.00 / 11.00 | 20.50 / 19.00 / 26.00 | 28.00 / 27.00 / 32.00 | 0.00 / 0.00 / 0.00 |
| simple_ai | p0_habits | 7.67 / 7.00 / 10.00 | 8.33 / 8.00 / 10.00 | 19.50 / 19.00 / 22.00 | 28.00 / 28.00 / 32.00 | 0.00 / 0.00 / 0.00 |
| self | plain | 0.67 / 0.00 / 2.00 | 1.17 / 0.00 / 4.00 | 28.00 / 27.00 / 42.00 | 40.50 / 40.00 / 47.00 | 150.00 / 148.00 / 168.00 |
| self | p0_habits | 1.00 / 0.00 / 5.00 | 1.67 / 0.00 / 6.00 | 10.33 / 6.00 / 22.00 | 38.33 / 37.00 / 47.00 | 150.00 / 149.00 / 173.00 |

| Opponent | Variant + miner policy | Alerts | Commander responses | Unresolved | Commander response seconds (mean / p50 / p95) |
|---|---|---:|---:|---:|---:|
| simple_ai | plain | 203 | 95 | 108 | 14.05 / 14.70 / 27.50 |
| simple_ai | p0_habits | 221 | 90 | 131 | 13.56 / 14.10 / 26.05 |
| self | plain | 281 | 222 | 59 | 13.90 / 13.20 / 27.05 |
| self | p0_habits | 315 | 272 | 43 | 13.08 / 12.10 / 28.05 |

Executor interventions are measured separately using `miners_withdrawn` observations, their `reason` and `triggered_at_s`. The raw event list includes each intervention's tick, units and trigger-to-order delay. A `hit` record is a matched damage-trigger reaction; `seen_weapon_range` is a visible-threat trigger, which can precede the first hit. `field_already_withdrawn` enrolls a new truck into an existing withdrawal and is counted separately below; it is included in the raw `unmatched` count because it is not a new initial hit/range transition. A missing trigger is never interpreted as zero.

| Opponent | Variant | Hit-trigger reactions | Visible-range triggers | New-enrollment events | Resume events |
|---|---|---:|---:|---:|---:|
| simple_ai | plain | 1 | 12 | 30 | 2 |
| simple_ai | p0_habits | 1 | 15 | 29 | 2 |
| self | plain | 2 | 14 | 25 | 2 |
| self | p0_habits | 3 | 18 | 8 | 12 |

Across all groups there are 158 withdrawal observations: 7 damage-trigger reactions, 59 visible-range triggers and 92 new-enrollment events, plus 18 resume observations. Every recorded trigger-to-order delay is 0 game-seconds: the rule issues the ordinary order in the same sim tick as its trigger. This is synchronous execution timing, **not** proof the trucks escaped or that a model received the event with zero latency. The 30-second commander's alert-response distributions above remain separate.

### Where P3 did not win

In plain self-play, undefended miner losses improve in every seed: paired two-seat totals 4 → 0, 5 → 2 and 9 → 2. The mean drops 3.00 → 0.67 (77.8% fewer). Against SimpleAI plain, mean undefended losses stay 7.17, with paired totals 13 → 15, 16 → 17 and 14 → 11; all six slow seats still lose. Adding policy to P0 habits worsens undefended losses from 7.17 → 7.67 against AI and 0.17 → 1.00 in self-play. It does not uniformly improve the targeted metric.

Withdrawal also costs production time. Mean ore mined falls 8.4% for plain vs AI, 5.1% for P0 vs AI, 1.1% for plain self-play and 2.2% for P0 self-play. The policy preserves only the field known to the team and requires visible clearance before resumption; the unchanged script can later give a direct withdrawal/mining order and take responsibility again. This is evidence for a tradeoff in an explicit safety policy, not a reason to add automatic tactical judgement or silently override the commander's next turn.

### P3 reconsideration and decision before P4/P5

**Retain P3 as an opt-in instrument, with a mixed verdict.** It executes the configured response promptly and improves plain self-play in all three seeds. It fails the stronger claim that home withdrawal alone protects slow commanders against SimpleAI, and combining it with P0 is worse in this small sample. The initial criterion is therefore met only for a narrower use case; do not advertise policy as an unconditional improvement.

Proceed to P4's non-behavioural addressing layer. For P5, test explicitly selected escorts/support/withdraw destinations rather than giving the executor permission to choose safer ground or whether to fight. Reconsider those guards if their own controlled rerun also fails to move the targeted metrics. Open question for broader testing: when does the chosen fallback destination or lost mining uptime make withdrawal worse than the existing retreat rule? Three seeds do not settle that strategic choice.

The added harness tests passed after P3 integration: default policy stays off, `miners` configures its explicit home destination/delay only once, event cursors consume interventions once, hit/range timing stays separate and unmatched triggers remain explicit. Run the layer with:

```sh
cd headless
dotnet run -c Release --no-build -- --slow-agent --slow-agent-layer miners \
  --slow-agent-seeds 1,2,3 --slow-agent-minutes 12 \
  --slow-agent-out ../docs/spikes/slow-agent-miners.jsonl
```

## P4 rerun: named addressing (rules 47)

Run before P5 after P4's source was frozen and its dedicated tests passed, using P3 commit `77961b4` plus the P4 source awaiting its phase commit. The `squads` layer creates one stable `army` squad from the current own armed units and adds only newly observed members as needed. Existing P0 producer rally commands enroll their future combat units in this same squad. It configures no miner policy or guard and issues no new movement.

An army order uses `units:"squad:army"` only when the requested IDs exactly equal the squad's current live members in the own fogged state. An idle advance affecting a subset retains explicit IDs. Fresh-unit `set_retreat` also retains explicit IDs. This matters: substituting the whole squad for an idle subset would silently change which units advance, and would no longer measure addressing alone. Tests lock both selector cases, incremental amendments, producer enrollment and the absence of redundant membership commands.

Raw results: [slow-agent-squads.jsonl](slow-agent-squads.jsonl), eighteen games with zero sim errors. A companion default-off run [slow-agent-floor-rules47.jsonl](slow-agent-floor-rules47.jsonl) again reproduces all eighteen P2 rows exactly after removing the rules-version label. The squads run reproduces **every battle, economy, elapsed-time, alert, response, unresolved, latency and rejected-command metric exactly** after excluding the intentional layer/rules labels and the added command counts. Both matrices hold the same seeds, cap, seat swaps and plain/P0 order as P2; wall times were 11.2 and 10.8 seconds respectively, with the two independent checks running concurrently.

Cells are mean / nearest-rank p50 / p95 across six slow seats. All other numerical metrics are identical to the baseline tables.

| Opponent | Variant + squad addressing | Undefended miner losses | Unsupported armed losses | Final score | Extra configuration commands |
|---|---|---:|---:|---:|---:|
| simple_ai | plain | 7.17 / 7.00 / 8.00 | 19.67 / 19.00 / 28.00 | 0.00 / 0.00 / 0.00 | 8.00 / 7.00 / 10.00 |
| simple_ai | p0_habits | 7.17 / 7.00 / 8.00 | 18.33 / 18.00 / 21.00 | 0.00 / 0.00 / 0.00 | 1.00 / 1.00 / 1.00 |
| self | plain | 3.00 / 0.00 / 9.00 | 25.67 / 19.00 / 41.00 | 150.00 / 138.00 / 189.00 | 17.67 / 19.00 / 19.00 |
| self | p0_habits | 0.17 / 0.00 / 1.00 | 7.50 / 3.00 / 20.00 | 150.00 / 147.00 / 172.00 | 1.00 / 1.00 / 1.00 |

### Where P4 did not win and decision

P4 causes no reduction in losses or commander reaction time and no win-rate change. That is the expected result for a naming and persistence layer with no executor. Plain commanders need repeated membership amendments as units are trained; P0 commanders need only one initial squad command because the existing rallies enroll later production. This is configuration persistence, not stronger autonomous play.

**Go to controlled P5 guards:** P4 passes the stricter criterion appropriate to addressing: selected units and every gameplay metric remain unchanged. Keep the stable named army for the next experiment; add only explicitly configured guards, then measure whether those conditions reduce unsupported loss or preserve intent. Do not turn this unchanged result into permission for the squad itself to choose a fight, target, build or destination.

The dedicated harness suite now has 21 passing checks, including exact full-squad versus subset selectors and rally enrollment. Reproduce with:

```sh
cd headless
dotnet run -c Release --no-build -- --slow-agent --slow-agent-layer squads \
  --slow-agent-seeds 1,2,3 --slow-agent-minutes 12 \
  --slow-agent-out ../docs/spikes/slow-agent-squads.jsonl
```

## P5 rerun: explicit guards (rules 48)

The `guards` layer retains the stable named `army`, economy, unit types and thirty-second commander decisions. It adds one complete initial guard profile: HP retreat below 40%, support distance 6 tiles, `rifleman` screen 4 tiles behind the commanded front, squad-strength withdrawal below 50%, and an explicit home-coordinate fallback. It enables no miner policy. This profile is a fixed commander's choice; the executor does not select a safer destination, a target, a build or whether to fight.

The comparison criterion is reduced unsupported armed-unit loss or improved plan continuity, with the default floor unchanged and no movement settles in the controlled guard scenarios. Existing P0 retreat/cohesion/rally habits remain a separate same-variant comparison. Both full-army defence selectors and explicit idle/fresh subsets retain the P4 semantics; no stronger plan is substituted into the baseline.

`guard_triggered` and `decision_needed` events are measured separately from ordinary alert-to-commander response. Raw events retain rule, reason, action, tick, trigger time and its basis. `trigger_basis:"condition_observed"` refers to the guard evaluation time, not the unknowable first instant HP or lag crossed the threshold. Missing trigger times remain unmatched/null. The observer also records movement-settle episodes in the slow team's own mobile units; these counts can include existing mining/combat movement issues and do not alone attribute a fault to a guard.

The unchanged scripted commander consumes alert-category observations and does not add a new decision policy for `decision_needed` packets. It still decides every 30 seconds. Packet emission is therefore measured; handling those packets as a new strategy belongs to a separately declared experiment, not a silent change to this comparison.

### Initial P5 result and movement reconsideration

The first frozen rules-48 matrix is retained in [slow-agent-guards-before-fix.jsonl](slow-agent-guards-before-fix.jsonl). All eighteen games had zero sim errors, and the default-off rules-48 matrix reproduced every baseline metric. Guards reduced mean unsupported armed losses versus P0+SimpleAI from 18.33 to 14.33 and in self-play from 25.67 to 1.33 (plain) and 7.50 to 0.17 (P0). Plain versus SimpleAI stayed at 19.67. All twelve slow-vs-SimpleAI seats still lost.

The initial profile nevertheless exposed nine own movement-settle episodes in self-play with P0, despite the isolated guard gates being clean. Shipment and the P6 decision were held for investigation. The diagnostic control [slow-agent-floor-diagnostics-rules48.jsonl](slow-agent-floor-diagnostics-rules48.jsonl) found eight existing baseline episodes in the same three self/P0 seeds; its other metrics remain exactly baseline-equivalent. The diagnostic guard replay [slow-agent-guards-diagnostics-before-fix.jsonl](slow-agent-guards-diagnostics-before-fix.jsonl) preserves all nine unit/tick/order/cause records without changing any gameplay metric.

All nine guard-profile episodes were `rifleman attack_move (nowhere)` after explicit slow-commander orders, rather than observed active guard movement: the first cases occurred at about 242.9 seconds following the 240-second turn, during the guard's five-second direct-order cooldown. Controlled snapshots showed the prior guard retreat's velocity average surviving into a much slower direct movement in the opposite direction. That makes inherited dance-detector history a specific handoff hypothesis; the raw counts alone do not prove nine new guard oscillations. The owner is checking a reset only when a guard relinquishes control to a direct order, preserving the never-configured floor. Separately, the screen's commanded bearing is being anchored to the original route, so frontline combat displacement cannot reverse a direction the commander supplied. Final numbers and a decision follow the corrected-source rerun below.

Diagnostic export is opt-in: add `--slow-agent-settle-fixtures /tmp/pez-guard-spike-fixtures`. It retains the six one-second offline Game snapshots spanning at least five seconds at the first own settle per match, plus unit metadata and future logged commands for `Game.Replay` when a thirty-second turn falls between a saved snapshot and the settle. The newest original frames used in this investigation already contain the last direct turn and need no subsequent command before the first settle. The flag never changes commander decisions; ordinary baseline output keeps its existing schema.

### Corrected P5 results and default-floor control

The corrected rules-48 source resets the detector's stale motion window once when handing guard control to a direct order or a different guard action, anchors screen bearing to the original commanded route, and keeps screen formation slots on the requested side of the line. These corrections were validated before this final paired rerun; they are limited to explicitly enabled guards.

Final raw results: [slow-agent-guards.jsonl](slow-agent-guards.jsonl), eighteen games and zero sim errors. The guard-profile matrix now has **zero own movement-settle episodes across all eighteen games**, including all six self/P0 seats. The final default-off diagnostic [slow-agent-floor-diagnostics-rules48-final.jsonl](slow-agent-floor-diagnostics-rules48-final.jsonl) retains the original eight existing episodes and every prior gameplay/economy/alert/response metric exactly. That is an unchanged floor, not a claim the existing game globally settles nothing. The two diagnostic matrices ran concurrently, taking 31.5 and 23.6 seconds wall time; snapshot export overhead is not a game-mechanic performance result.

For the regression fixture, an isolated offline copy restored the original screen route/slot semantics and disabled the new motion-window resets. Its eighteen-match gameplay results match the archived initial P5 matrix exactly and reproduce nine settles. [Handoff diagnostic records](slow-agent-guards-handoff-diagnostics.jsonl) retain the settled-unit/group/speed/action/route metadata and all match metrics; repeated detailed guard events are available in the earlier complete raw file. The exported fixture before the 240-second handoff starts at 237 seconds and includes six actual logged commands at 240 seconds for `Game.Replay`. A snapshot after the handoff cannot validate the fix: it already contains the stale detector window. Fixture integration and the full gate are owned by the sim phase author.

Final cells are mean / nearest-rank p50 / p95 across six slow seats.

| Opponent | Variant + guards | Undefended miner losses | All miner losses | Unsupported armed losses | All armed losses | Final score |
|---|---|---:|---:|---:|---:|---:|
| simple_ai | plain | 7.50 / 7.00 / 9.00 | 8.50 / 8.00 / 10.00 | 19.00 / 17.00 / 22.00 | 28.17 / 27.00 / 32.00 | 0.00 / 0.00 / 0.00 |
| simple_ai | p0_habits | 6.17 / 7.00 / 8.00 | 8.33 / 8.00 / 9.00 | 13.67 / 12.00 / 17.00 | 28.50 / 26.00 / 33.00 | 0.00 / 0.00 / 0.00 |
| self | plain | 0.00 / 0.00 / 0.00 | 0.67 / 0.00 / 3.00 | 2.00 / 1.00 / 5.00 | 21.17 / 20.00 / 28.00 | 150.00 / 146.00 / 175.00 |
| self | p0_habits | 0.00 / 0.00 / 0.00 | 0.17 / 0.00 / 1.00 | 0.17 / 0.00 / 1.00 | 4.33 / 3.00 / 8.00 | 150.00 / 139.00 / 167.00 |

| Opponent | Variant + guards | Alerts | Commander responses | Unresolved | Commander response seconds (mean / p50 / p95) |
|---|---|---:|---:|---:|---:|
| simple_ai | plain | 282 | 118 | 164 | 11.68 / 9.80 / 29.05 |
| simple_ai | p0_habits | 209 | 116 | 93 | 16.76 / 18.20 / 29.50 |
| self | plain | 235 | 210 | 25 | 24.03 / 26.00 / 29.65 |
| self | p0_habits | 111 | 91 | 20 | 19.87 / 25.00 / 29.00 |

| Opponent | Variant | Guard triggers | Decision packets | Guard releases | Direct-order detachments | Own settles |
|---|---|---:|---:|---:|---:|---:|
| simple_ai | plain | 316 | 6 | 31 | 133 | 0 |
| simple_ai | p0_habits | 787 | 6 | 65 | 272 | 0 |
| self | plain | 3083 | 3 | 395 | 1616 | 0 |
| self | p0_habits | 5314 | 2 | 753 | 2194 | 0 |

Across all groups the trigger reasons are: `hold_for_support:support_behind` 4442, `retreat_below_pct:hp_below_threshold` 417, `screen:commanded_front` 4486, `withdraw_below_strength_pct:strength_below_threshold` 17, `withdraw_below_strength_pct:withdrawal_still_latched` 138. Every observed-condition-to-issued-intent delay is zero game-seconds, with no unmatched trigger timestamps. Those synchronous guard records and decision emissions do not measure first-threshold-crossing time, model wake latency or tactical success. Commander p95 remains near thirty seconds.

### Where corrected P5 did not win and decision before P6

Unsupported armed losses improve 25.5% for P0 against SimpleAI (18.33 → 13.67), 92.2% in plain self-play (25.67 → 2.00), and 97.8% in P0 self-play (7.50 → 0.17). Plain against SimpleAI improves only 3.4% (19.67 → 19.00). All twelve slow-vs-SimpleAI seats are still eliminated after 427–532.30 seconds, and final score stays zero. Symmetric self-play's mean score remains 150 by construction; it cannot establish improved win rate.

The chosen guard profile produces many support/screen adjustments and direct overrides. Its reduction in deaths can include fighting less or spending more time holding; a loss count alone cannot prove faster conquest. The script still does not handle the new critical decision packets, and a latched withdrawal deliberately needs a commander's new decision. No executor strategy was added to compensate for that limitation. Mean ore mined differs little from baseline: plain vs AI −0.4%, P0 vs AI +2.4%, plain self-play +2.9%, P0 self-play −0.9%.

**Go to a controlled P6 objective experiment:** the corrected guards now demonstrate a targeted support-loss benefit with zero observed guard-profile settles while preserving the default floor exactly. Their stronger win-rate claim fails against SimpleAI and remains unproved in self-play. P6 must test explicit intent persistence and decision handling, with its assembly point, counts, budget, destination and wake conditions all chosen by the scripted commander before execution. Do not have the executor choose a stronger plan to improve these numbers.

The dedicated harness has 30 passing checks, including guards staying disabled in every other layer, one-time explicit configuration, exact subset addressing and separate matched/unmatched guard/decision timing. Reproduce the final matrix with:

```sh
cd headless
dotnet run -c Release --no-build -- --slow-agent --slow-agent-layer guards \
  --slow-agent-seeds 1,2,3 --slow-agent-minutes 12 \
  --slow-agent-out ../docs/spikes/slow-agent-guards.jsonl
```
