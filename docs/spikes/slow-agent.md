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

Resume after the P2 phase commit: add only the shipped layer's explicit setup commands to the harness, preserving the baseline choices and metrics. The CLI currently accepts `baseline` only so an unimplemented layer cannot silently receive a baseline label.
