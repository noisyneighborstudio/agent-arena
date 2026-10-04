# The house AI's buggy swarm (#28)

**Question.** Test players kept losing heavy-tank armies to the house AI's swarm. Are scout buggies too cost-efficient, or is something else beating them? It's settled by equal-cost open-field fights.

**Setup.**
- Harness: `cd headless && dotnet run -c Release -- --bench-swarm` (Tests.BenchSwarm).
- Each fight: 96-tile map, seeds 1–5, both sides attack-move into each other, up to 120 s.
- Cost is all resources summed: heavy_tank 480, light_tank 240, scout_buggy 120, rocket_soldier 110, rifleman 40, gun_turret 180.
- "Clumped" puts units 0.9 tiles apart and "spread" 2.5 tiles apart, on the defending side.

| Fight (equal cost) | Clumped: wins | Clumped: survivors | Spread: wins | Spread: survivors |
|---|---|---|---|---|
| 10 heavy_tank vs 40 scout_buggy | heavies 5/5 | 84% / 0% | heavies 4/5 | 86% / 2% |
| 10 heavy_tank vs 44 rocket_soldier | rockets 4/5 | 6% / 40% | rockets 5/5 | 0% / 59% |
| 10 heavy_tank vs 120 rifleman | heavies 3/5 | 58% / 3% | heavies 2/5 | 78% / 3% |
| 10 light_tank vs 20 scout_buggy | light tanks 3/5 | 48% / 26% | light tanks 3/5 | 46% / 18% |
| 20 rocket_soldier vs 18 scout_buggy | buggies 5/5 | 0% / 52% | buggies 5/5 | 0% / 34% |

**Verdict.** The buggy isn't overtuned. The counters work as designed: heavies shred buggy swarms, rockets shred tanks, and buggies shred rockets. The AI wins because it fields a mixed blob of buggies, rocket soldiers and riflemen, while players sent single-type armies (all heavies) into it. That's a composition decision, which is strategy.

**Where it didn't settle.** These are open-field fights with no terrain, turrets or micro. The bench didn't test mixed against mixed.

**Decision.** No balance change. The rules tips now spell out the counter triangle, so players can plan for it. Revisit if mixed-vs-mixed benches show a dominant mix.
