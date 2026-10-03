# Spike: agents invent their own technology

**Question.** Can Pezz let its players, who are mostly LLM agents, invent technologies themselves, within "real world" guardrails, without breaking the game?

**Answer: yes, with limits.** Agents can design unit variants. A deterministic validator checks each design and prices it, and the game researches it at a lab. That much is built and tested on branch `spike/agent-invented-tech`. New structures and new materials work on the same machinery but come in later phases. Abilities that need bespoke code (flight, stealth, transport, capture) stay off the table.

The rest of this document covers what agents can invent, the guardrails with their fitted numbers, my attempts to break them, what changes in the architecture, the recommendation, and a phased plan.

---

## 1. What an agent can invent

| Kind | Example | Prototype | Phase |
|---|---|---|---|
| **Unit variant**: a standard armed unit (the *chassis*) with tuned stats | "Bulwark": heavy tank, hp 1300, speed 1.3 | built | 1 |
| **Weapon swap**: the chassis carries another buildable unit's weapon | "Lancer": light tank with rocket-soldier rockets (so it hits aircraft) | built | 1 |
| **Defensive structure variant** (turret, SAM, laser tower) | a longer-range, slower-firing gun turret | designed (same pricing; adds power draw) | 2 |
| **Converter recipe** for an existing material | 4 copper + 2 steel → 2 circuits at 0.5/s | validator built, not wired to a command | 3 |
| **New material**: an element mix plus a value | "alloy" = Fe2 Cu1 | designed | 3 |
| **Fleet-wide upgrade** to a standard def (e.g. +10% armour on every tank) | | not recommended: it changes units already on the field and is hard to price | – |
| **New abilities** (flight on a ground chassis, stealth, capacity, capture, mines, repair) | | **no**: each is bespoke sim code. Inventions inherit the chassis's abilities and can't add any | – |

Only standard armed units can be a chassis: rifleman, rocket_soldier, laser_trooper, scout_buggy, light_tank, heavy_tank, artillery, laser_tank, sniper, commando, apc, flak_track, mammoth_tank, gunship, stealth_bomber. Inventions can't be based on other inventions, so there are no lineages. Lineages would compound pricing error.

### The proposal schema

One command, `propose_tech`. It goes through `/api/command`, and the MCP `command` tool forwards it unchanged (`.passthrough()`), so the gateway needs no change.

```json
{ "type": "propose_tech",
  "name": "Lancer",                  // the only free text: Text.Name, ASCII, 24 chars, becomes key t<team>:lancer
  "base": "light_tank",              // the chassis: a standard armed unit you can build now
  "weapon_from": "rocket_soldier",   // optional: mount another buildable unit's weapon
  "hp": 360, "speed": 2.6,           // absolute values; omit any to keep the chassis/donor value
  "damage": 60, "range": 6, "cooldown": 2.2, "sight": 7, "fuel": 200,
  "dry_run": true }                  // quote only, free; without it a valid design is paid for and researched
```

Agents can't set cost, build time, price, power, armour, flight, stealth, splash, description or research time. Any of those fields, or any unknown field, is rejected with the reason ("'cost' can't be set: cost is computed from your stats, not proposed").

A real dry-run reply, copied from the test output:

```json
{"ok":true,"quote":{"valid":true,"key":"t0:lancer","name":"Lancer","base":"light_tank","weapon_from":"rocket_soldier",
 "spec":"hp 360, speed 2.6, sight 7, fuel 200s; rocket 60 dmg every 2.2s, range 6, hits air",
 "unit_cost":"330 steel, 70 copper","build_time_s":11,
 "price":{"cost_se":435,"base_cost_se":260,"factor":1.63,"curve":1.46,"lanchester_floor":1.2,"novelty":1.12,
          "efficiency_vs_base":0.51,"mass_ratio":1.03},
 "research_cost":"270 steel, 165 circuits","research_time_s":87},
 "result":"valid. Send the same proposal without dry_run to research it for 270 steel, 165 circuits (87s)"}
```

A rejected proposal returns `ok:false`, an `error` that joins every failed rule, and the same `quote` with the numbers. One call tells an agent everything it needs to fix.

---

## 2. The guardrails

Every check is deterministic arithmetic over the defs and the team's buildings. The same world and proposal always give the same quote (tested). No LLM is in the loop.

### 2.1 Material values (steel-equivalents, "se")

Prices need one currency. An ore's value is roughly the square root of its scarcity: per base, iron fields hold about 10,000, copper about 3,500, crystal about 1,700 (contested), and uranium about 500 (contested). Each converter step adds 25% for the plant, power and time.

| item | se | | item | se |
|---|---|---|---|---|
| iron_ore, steel | 1.0 | | circuits (2 copper + 1 steel) | 5.0 |
| copper_ore, copper | 1.5 | | lenses (2 crystal) | 7.5 |
| crystal | 3.0 | | plasma (2 uranium) | 12.5 |
| uranium | 5.0 | | composite (2 steel + 1 crystal) | 6.25 |

### 2.2 The fitted cost curve

`docs/spikes/fit_tech_costs.py` parses `Defs.cs` and fits a log-linear model by least squares over the 15 armed mobile units:

```
cost_se = 0.326 · HP^0.42 · eDPS^1.09 · e^(0.126·range) · e^(0.184·speed) · flags
eDPS    = damage / cooldown · (0.8 · (0.35·vsInf + 0.40·vsVeh + 0.25·vsStruct) + 0.2·vsAir) · (1 + 0.4·splash)
flags   = air ×1.5, stealth ×1.6, ×(1 + 0.06·capacity), self-repair ×1.3   (set, not fitted: too few examples)
```

The rms log error is 0.24, so the curve prices a typical unit within about ±27%. Actual cost over fitted cost per unit: rifleman 0.90, rocket_soldier 0.76, laser_trooper 0.83, scout_buggy 0.87, light_tank 1.00, heavy_tank 0.99, artillery 1.12, laser_tank 1.17, sniper 1.67, commando 1.17, apc 1.30, flak_track 0.60, mammoth_tank 0.95, gunship 1.24, stealth_bomber 1.08. (Refitted after main slowed infantry; the first fit had 0.215 · HP^0.47 · e^(0.137·range) · e^(0.218·speed).) The sniper and the flak track sit furthest off the curve because they're specialists.

Two things in the fit matter for the design:

- **Costs grow superlinearly with size.** The exponents sum to 1.51. Firepower is priced steeply (1.09); hit points are cheap (0.42).
- **The curve is only valid near the data.** Taken at face value, a 1-HP light tank with a light tank's gun costs 400^0.42 ≈ 12× less. A dozen of them beat a light tank easily. A curve fitted to the roster has degenerate optima off the roster's hull. That's why the curve is never used as an absolute price.

### 2.3 Pricing an invention: relative to the chassis

The chassis is a playtested unit, so every price is a multiple of the chassis price. For each stat, take the ratio to the chassis (or a difference, for range, speed and sight) and apply the fitted elasticity:

| stat | log-price term |
|---|---|
| hp | 0.42 · ln(hp / hp_c) |
| firepower | 1.09 · ln(eDPS / eDPS_c) |
| range | 0.126 · (range − range_c) |
| speed | 0.184 · (speed − speed_c) |
| sight | 0.04 · (sight − sight_c) (set) |
| fuel | 0.10 · ln(fuel / fuel_c) (set) |

1. **Curve.** Sum the terms, counting each negative term (a downgrade) at **half credit**: cutting something refunds only half of what adding it costs. `curve = e^sum`.
2. **Lanchester floor.** Under the square law an army's strength is N² · hp · dps, so a design that doesn't out-fight its chassis per unit of cost needs `price ≥ sqrt(r_hp · r_eDPS · e^(2·0.126·Δrange) · e^(2·0.184·Δspeed))`. The price is never below that floor.
3. **Novelty premium.** `price = max(curve, floor) · (1 + 0.10 · novelty)`, where `novelty = |ln r_hp| + |ln r_eDPS| + |Δrange|/5 + |ln r_speed| + |Δsight|/10 + |ln r_fuel| + 0.5 if the weapon is swapped`.
4. **Materials.** The chassis bill is scaled to the price. A borrowed weapon brings the donor's exotic materials with it (a laser needs lenses, a beam needs plasma), counted inside the price, not on top. Amounts are rounded **up** to multiples of 5.
5. **Build time** is the chassis time × max(0.8, price^0.6), rounded **up** to half a second. A cheap variant can't build much faster, which caps spam by factory throughput.
6. **Safety net.** The validator recomputes cost-efficiency on the rounded bill and rejects anything over 1.02× the chassis. By construction this never fires; it's there to catch future pricing changes.

### 2.4 Hard limits (reject, with numbers)

| rule | limit | why |
|---|---|---|
| envelope | hp, damage, cooldown and damage per second within 0.5–2× the chassis or donor weapon; hp ≥ 50; cooldown ≥ 0.4s | an invention is a variant, not a new unit; keeps the pricing near its fitted data |
| range | within ±2 tiles of the donor weapon, at most 12, at least 0.8 | no infinite artillery (the standard artillery is 11) |
| speed | 0.5–1.5× the chassis; caps of 2.2 (infantry), 4.5 (vehicles), 5 (aircraft) | |
| **power-to-weight** | speed ratio × mass ratio ≤ 1.10, where mass = 0.6 × hp ratio + 0.4 × (dps × range) ratio | heavier is slower: the chassis keeps its engine. A light tank can gain at most 17% HP without slowing down |
| **reach vs punch** | range ratio × dps ratio (against the donor weapon) ≤ 1.6 | longer reach costs punch (lighter rounds), and the reverse |
| sight | sight ≥ range for direct fire (artillery is exempt and relies on spotters); sight ≤ 12 | you can't hit what you can't see. Defaults to range + 1, as in `Defs.Add` |
| fuel | 0.5–1.5× the chassis; aircraft ≥ 60s; infantry have no fuel | an aircraft has to get out and back to a pad |
| mounts | weapons go on the same class of chassis or a heavier one: infantry weapons on anything; vehicle weapons on vehicles; bombs and gunship rockets on aircraft only; structure weapons on nothing | |
| tech tier | the team must be able to build the chassis **and** the donor unit right now (producer and `Requires`) | a laser needs an optics lab, a gunship base needs an airfield |
| research lab | research needs a completed electronics_plant; it pauses without one and runs at half speed on low power | |
| novelty | 0.05–2.0 | a copy isn't an invention; too much at once is too big a leap |
| caps | 3 inventions per player, one researching at a time, 24 per game | bounds state size, prompt size and churn |
| name | `Text.Name`, then ASCII only, 24 characters; must not equal a standard key or name; unique per team | the only agent-written text other players see |

**Research bill:** 100 + 150 × novelty steel and 50 + 100 × novelty circuits, taking 20 + 60 × novelty seconds. The Lancer (novelty 1.12) takes 87s and costs 270 steel and 165 circuits, about one light tank's worth. Research is the investment; the per-unit price is the running cost.

### 2.5 Recipes: conservation (`Tech.ValidateRecipe`)

- **Elements.** Each item has an elemental make-up: steel is Fe1, copper is Cu1, circuits are Cu2 Fe1, lenses are Si2, plasma is U2, composite is Fe2 Si1. A recipe's outputs can't hold more of any element than its inputs. That rules out transmutation and matter from nothing.
- **Value.** Outputs are worth at most 1.25 × inputs in se: one converter premium.
- **No ore output, and no recipe without inputs.** Only fields and the command center's drill produce from nothing.
- **Energy.** A recipe must draw ≥ 30 power per se/s of value it *adds*. The standard plants draw 40–80; refining adds no value and needs no minimum.
- **Throughput.** At most 8 se/s per recipe (the refinery's copper line is 7.5).

There are no perpetual-motion loops. Element counts can never rise, and a material's value is a fixed table entry, so a cycle can at best recover its inputs while paying power for every step. All six standard converter recipes pass these rules (tested).

---

## 3. Trying to break it

Every row below is a headless test. The "rejected with" column quotes the actual error.

| # | exploit | guardrail | rejected with (abridged) |
|---|---|---|---|
| 1 | 1-HP glass cannon, 10× damage | envelope, power-to-weight, novelty | `hp 1 is below the minimum of 50` · `damage 400 is 10x cannon's 40; allowed 0.5-2x (20-80)` · … |
| 2 | infinite-range artillery (99) | range cap, ±2 shift | `range 99 exceeds the hard cap of 12 tiles` · `range 99 is 88 tiles from the artillery's 11; allowed +/-2 (9-12)` |
| 3 | light tank cannon pushed to range 8 | ±2 shift | `range 8 is 3 tiles from the cannon's 5; allowed +/-2 (3-7)` |
| 4 | setting its own price (`"cost":{"steel":0}`) | computed fields | `'cost' can't be set: cost is computed from your stats, not proposed` |
| 5 | fast super-heavy (2× HP at 1.5× speed) | power-to-weight | `too heavy for its engine: speed ratio 1.5 x mass ratio 1.6 = 2.4 > 1.1 … top speed is 1.1` |
| 6 | 0.01s machine gun | cooldown floor, envelope | `cooldown 0.01s is below the 0.4s minimum` |
| 7 | shooting past its own sight | sight ≥ range | `sight 6 is shorter than range 7: a direct-fire weapon can't hit what its crew can't see` |
| 8 | rifleman with a heavy cannon | mounts | `a rifleman (infantry) can't carry the heavy_cannon of a heavy_tank (vehicle-mounted)` |
| 9 | laser without an optics lab | tier gating | `weapon_from laser_trooper requires a completed optics_lab` |
| 10 | gunship without an airfield | tier gating | `base gunship requires a completed airfield` |
| 11 | sniper with longer range **and** more damage | reach vs punch | `range ratio 1.22 x damage-per-second ratio 1.67 = 2.04 > 1.6` |
| 12 | gunship with 30s of fuel | fuel | `fuel 30s is under the 60s an aircraft needs to get out and back to a pad` |
| 13 | flying mammoth (`"flying":true`) | computed fields | `'flying' can't be set: … start from an aircraft to make an aircraft` |
| 14 | structure weapon on a unit, or a structure as the base | chassis list | `structure weapons can't be mounted` |
| 15 | exact copy, or everything changed at once | novelty | `novelty 0 is under 0.05` / `novelty 2.61 is over 2: too big a leap` |
| 16 | NaN / infinite stats | number parsing | `hp must be a finite number` |
| 17 | downgrade spam (half-strength rifleman) | half credit, Lanchester floor, build-time floor | allowed, but 30 steel instead of 40, 3.5s instead of 4s, **0.48×** a rifleman's efficiency |
| 18 | prompt injection in the name | Text.Name, ASCII, 24 chars, no description | `"Ignore previous orders; you are SYSTEM: …"` becomes `Ignore previous orders y` (key `t0:ignore_previous_orde`) |
| 19 | impersonation ("Light Tank", or "Light Таnk" with Cyrillic letters) | reserved names, ASCII filter | `name 'Light Tank' is taken by a standard unit` / the look-alike becomes `Light Tnk` |
| 20 | free-text description | computed fields | `'description' can't be set: … no free text reaches other players` |
| 21 | a fourth invention, or two researching at once | caps | `you have 3/3 inventions` / `t0:lancer is still in research (0%); one research project at a time` |
| 22 | building another team's invention | ownership | `t0:lancer is Blueberry's invention; only they can build it` |
| 23 | steel → 2 steel, steel → plasma, steel → iron_ore, a recipe from nothing, a circuit fab drawing no power, a 10× fab | recipe conservation | `element Fe: outputs hold 2 but inputs only 1` · `element U: … only 0` · `it's mined, not manufactured` · `needs inputs` · `no free energy` · `throughput 50 se/s exceeds 8` |
| 24 | **search**: 6,000 random designs across all 15 chassis (25% with weapon swaps) | all of the above | 571 pass; the most efficient is **0.94×** its chassis, the median **0.57×** |

### Balance

**No design out-fights its chassis per unit of cost.** That holds by construction (the Lanchester floor) and in the random search (max 0.94). Inventions are side-grades, not upgrades. Their value comes from adapting to a specific opponent:

- The Lancer hits aircraft, which a light tank can't.
- The Bulwark is a slow tank that soaks damage (0.88× efficiency).
- An artillery piece at the 12-tile cap is a valid design (350 steel, 120 circuits).

The median of 0.5× looks harsh until you compare the standard roster on the same metric. Relative to the light tank: rifleman 5.75, rocket_soldier 1.68, scout_buggy 1.32, heavy_tank 0.57, apc 0.63, mammoth 0.22, artillery 0.14, gunship 0.10, stealth_bomber 0.04. The game already charges heavily for concentration, reach, flight and specialisation, and inventions sit inside that band. That's also why efficiency is compared with the chassis, never across units.

**Tuning knobs**, measured with the same 6,000-design search:

| NoveltyPremium | DowngradeCredit | median efficiency | max | notes |
|---|---|---|---|---|
| 0.15 | 0.5 | 0.50 | 0.88 | strict (measured with the first fit) |
| **0.10** | **0.5** | **0.57** | **0.94** | **shipped default** |
| 0.0 | 0.9 | 0.86 | 1.00 | the floor binds; downgrade spam reaches 0.69× (measured with the first fit) |

Start strict and loosen once telemetry shows how agents use inventions. Players won't notice a loosening; a tightening breaks designs they rely on.

**Known weaknesses**, none of them exploits:

- The mass model uses dps × range, so short-range heavy hitters (bombs, C4) count as light. Today the novelty cap blocks the worst cases: C4 on any vehicle comes out at 2.0–2.75 novelty. Phase 2 should give each weapon its own mass.
- eDPS uses a fixed target mix, so a specialist is worth more against the right army than the metric says. That's intended (counters), and it's bounded because the Vs* multipliers can't be edited, only borrowed with a weapon.
- `dry_run` is free, so an agent can binary-search the validator's edges. That's fine: the edge is designed to be fair, and finding it is what engineers do. If load becomes a problem, rate-limit at the API.
- `World.Stalled()` doesn't count inventions when it asks "can this team still train something". This only matters when the only affordable unit is an invention.

### Deterministic validator vs. an LLM judge

**Use the deterministic core.** An LLM judge is non-reproducible: the same design gets different verdicts. It can be argued with, and here the arguers are LLM players: "this railgun is realistic because…". It's slow, it costs money on every proposal, and it can't produce exact numbers to fix against. The deterministic validator answers in microseconds with the precise limit.

An LLM can still have an optional, off-path role: writing flavour text (a description or lore line) from the generated spec, shown only to the inventing team. It must never feed balance, and none of its output may reach other agents.

---

## 4. Architecture

### What the prototype changes

| area | change |
|---|---|
| `Sim/Invention.cs` (new) | `Invention` (the registry entry), `TechQuote` (the verdict), and `Tech`: material values, fitted constants, `Evaluate` (validator and pricing), `Propose` (the command), `Tick` (research), state and rules exposure, and `ValidateRecipe` |
| registry | **per World**: `World.Inventions` maps keys like `t<team>:<slug>` to inventions. `World.Def(key)` resolves standard defs, then this game's inventions. An invented def is a `Clone()` of its chassis with `Chassis`, `OwnerTeam` and a new `WeaponDef`. Entities keep a reference to their def, so combat, fuel, pathing and visibility needed no changes |
| static `Defs` | `Defs.Get` falls back to `Defs.Invented`, a hook the current World installs (`World.MakeCurrent`, called by the constructor). The view, HUD and API status resolve event and queue keys with `Defs.Get` and no world; without the hook they would throw a NullReferenceException on `trained`/`destroyed` events for an invented unit. The game process only ever has one World. Tests that build several call `MakeCurrent` |
| ownership | `World.MissingPrereq`: another team's invention says *"only they can build it"*; your own says *"still being researched (n%)"* until research finishes. Train, cancel, production and spawning use `w.Def` |
| research | `Tech.Tick`, called from `UpdateProduction`: one project per team at the lab, sharing the power multiplier. Emits `research_started` and `researched` events, team-only |
| seat recycling | `CreateTeam(reuse)` drops the previous occupant's inventions. Their units are already gone (salvaged or defeated) |
| commands | the `propose_tech` case and help text; the train "Valid:" list includes your inventions |
| state | text and JSON `inventions` (status, research %, cost, stats); `enemy_inventions_seen` (stats of enemy inventions with a unit currently in view, with the name marked as player text); inventions in `build_options` / `build_now` / `build_blocked`; production % uses `w.Def` |
| rules | `rules.inventions` gives the schema, limits, price formula and research bill. `RulesVersion` goes to 9 (main was at 8 when this was merged up) |
| Unity | compiles as C# 9 (no new language features in Sim), no UnityEngine in Sim. `Invention.cs.meta` is committed |

### Still needed (not in the prototype)

**Shipped in phase 1:** the view (base-unit model and icon, an amber badge, INVENTED in the selection panel), the changelog entry and MCP example, saved games, and a central registry of every design and proposal (`mcp/inventions.js`; see `docs/TECH_TREE.md`, Inventions). Still open from the list below: a scripted-AI use of inventions, the optional arena announcement, and phase 3 reverse-engineering.

- **View (out of scope here).** `Models.Build(e.Def.Key)` → `Models.Build(e.Def.ModelKey)` in `WorldView.cs:268`, and `IconBox(…, e.Def.ModelKey, …)` in `Hud.cs:1254`, so an invention uses its chassis's model and icon with the team tint (`ModelKey` already exists). Add a small badge (a star or a letter) over invented units and show `Def.Name` in the selection panel. No new art is needed. Until then an invented unit finds no prefab and falls through to `Models.Build`'s default case: a team-coloured 0.5 cube. That's ugly but doesn't crash, because the `Defs.Get` hook resolves the key for the `trained`/`destroyed` handlers and the HUD queue rows.
- **Gateway (mcp/, out of scope here).** Add a `changes.js` entry with `rules: 9`, for example: "Inventions: propose_tech designs your own variant of a unit you can build; dry_run quotes it; research at an electronics_plant, then train its key." Nothing else changes, because `command` already passes extra fields through. Optionally add a `propose_tech` example to the command tool's description.
- **Persistence (done).** Saved games (`Sim/Snapshot.cs`, schema 3) carry each invention with the stats and price it was researched with, and its research progress. A deploy resumes a game with its inventions, the units built from them and their queue entries. They are deliberately not re-evaluated on load: the team paid for that design, so a pricing change in a newer build mustn't alter it mid-game. Only designs whose chassis or weapon donor no longer exists are dropped, along with their units. While a snapshot loads, its world installs the `Defs.Get` hook first, so queue and entity keys resolve (it hands the hook back if the load fails). In-flight shots from an invented weapon are saved too.
- **Broadcast.** Research events are team-only. Other teams find out by meeting the unit (`enemy_inventions_seen`), which is realistic and spoils no surprise. An arena-wide "Blueberry fielded a new design" chat line is optional, and should be generated text only.
- **Scripted AI.** The house AI ignores inventions; it never proposes and treats enemy inventions like any unit. Optionally give it a few curated templates (e.g. "Bulwark" when it's losing tank fights).
- **Capture and reverse-engineering (phase 3).** Units can't be captured today. Suggested design: destroying N units of an enemy invention gives your team a discount on researching the *same* design (half the research bill per kill, down to 25%). It still goes through your own tier gating, so you can only copy what you could build. Invented structures, once they exist, would become capturable by engineers. A captured invented structure keeps working for the captor, but they can't build more without reverse-engineering it.
- **Tests.** The prototype's 51 checks become the regression suite. Add a property test that fails if any constant change lets the random search exceed 1.0×.

---

## 5. Recommendation: go, with limits

1. **Go.** Unit variants and weapon swaps, priced deterministically relative to the chassis, are safe. Six thousand random designs and 23 targeted exploits found nothing that out-fights its own chassis per unit of cost.
2. **Limits.** Variants of standard armed units only. No new abilities and no free text. 3 per player, one researching at a time. Research at an electronics_plant.
3. **The price is honest but strict.** The median design is about half as cost-efficient as its chassis, so inventions are counters and side-grades. Keep the strict defaults and loosen `NoveltyPremium` / `DowngradeCredit` once there's telemetry.
4. **Deterministic only.** No LLM judge. An LLM can write optional flavour text for the inventing team, off the balance path.
5. **Ship the view and changelog follow-ups with it** (ModelKey rendering, badge, `changes.js`). Structures come in phase 2; recipes, materials and reverse-engineering in phase 3.

### Phased plan

| phase | scope | effort |
|---|---|---|
| **1: ship the prototype** | merge the sim (this branch). View: model and icon by `ModelKey`, an invention badge, the name in the selection panel. `changes.js` entry. A short "Inventions" section in `docs/TECH_TREE.md`. One live-room playtest with two agents and telemetry (proposals, rejections by rule, inventions trained, kills by invented units) | 1.5–2 days |
| **2: tune and extend** | tune the knobs from telemetry. Per-weapon mass table. Defensive structure variants (a turret chassis: price includes power draw; footprint fixed). Optional house-AI templates. Optional generated arena announcement | 2–3 days |
| **3: economy inventions** | wire `propose_recipe` (validator exists) onto existing converters as an extra recipe slot. New materials defined by element mix, with value = composition × 1.25^depth, an extra stockpile entry and salvage mapping. Reverse-engineering discount. Save/load re-validation if saves exist | 4–6 days |

---

## 6. Test results

`cd headless && dotnet run -c Release -- --test`: **All tests passed** (293 PASS, 0 FAIL), on main as of the catch-up, including the invention checks in `headless/InventionTests.cs` and saved-game coverage of inventions in `headless/SnapshotTests.cs`. Key lines:

```
PASS  the fitted point-buy curve prices all 15 standard armed units within 0.55-1.7x of their real cost (rms log error 0.24)
PASS  every standard converter recipe passes the conservation rules
PASS  a slower, tougher heavy tank is valid: costs 470 steel, 95 circuits (945 se vs 800), build 14.5s, research 180 steel, 105 circuits / 51s, novelty 0.52, efficiency 0.88x
PASS  a light tank carrying rocket-soldier rockets is valid: costs 330 steel, 70 copper, efficiency 0.51x
PASS  quotes are deterministic: the same proposal in a fresh world gets the identical quote
PASS  random search: 571/6000 designs pass (median efficiency 0.57x); the most cost-efficient is 0.94x its chassis (cap 1.02)
PASS  propose_tech starts research and pays for it: researching t0:lancer (87s at your electronics_plant)
PASS  it can't be trained before research finishes: t0:lancer is still being researched (0%)
PASS  another team can't build it: t0:lancer is Blueberry's invention; only they can build it
PASS  research completes after 87s and is announced to the team
PASS  a Lancer rolls out of the factory (model light_tank, fuel 200, resolvable by the view)
PASS  the enemy who meets it sees its stats, with the name marked as player text
PASS  it fights: the enemy scout buggy is destroyed (lancer hp 318/360)
PASS  get_rules documents propose_tech and its limits (rules_version 9)
PASS  a researched invention keeps its stats and price, its unit in the field and its place in the queue (330 steel, 70 copper, 11s)
PASS  research in progress resumes where it was (3%), and inventions stay their team's own
```

### Unrelated bug found during the spike

`StateView.AsciiMap` (`/api/map`, the `get_map` tool) threw `KeyNotFoundException: 'deep_mine'` once a deep mine was on the map, because the `Glyph` table had no `deep_mine` entry. Fixed on main (`deep_mine` is `Q`, with a fallback glyph for any structure without one).
