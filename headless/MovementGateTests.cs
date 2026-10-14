using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>
    /// The movement gate (docs/MOVEMENT.md): scenarios where units used to dance, run for a while, failing on any unit the
    /// dance guard had to settle (World.SettleCounts). Designed scenarios allow none. The live-room fixtures and the AI
    /// game have budgets set at what they settle today; each fix that removes a cause lowers its budget, until all are 0.
    /// </summary>
    public static partial class Tests
    {
        /// <summary>Just the movement gate (--gate): the exit code is the failure count.</summary>
        public static int RunGate() { MovementGate(); Console.WriteLine(failures == 0 ? "Movement gate passed." : $"{failures} movement check(s) FAILED."); return failures == 0 ? 0 : 1; }

        static void MovementGate()
        {
            GateGuardCatchesADance();
            GateMinerProtection();
            GateSquadGuards();
            GateSupportRelease();
            GateMinerArbitration();
            GateObjectives();
            GateObjectiveInteractions();
            GateAirfieldRush();
            GateCrowd(budget: 0);
            GateEconomy(seed: 4, size: 80);
            GateEconomy(seed: 7, size: 80); // other maps: under Rules 42 alone, seeds 1 and 7 (at 80) and 2 (at 96) reached the beat of 8 too
            GateEconomy(seed: 2, size: 96);
            GateAiGame(minutes: 8, budget: 0);
            GateAiSeeds(new[] { 1, 2, 3, 4 }, minutes: 8, budget: 4); // other base layouts: a rare truck or surveyor (seed 1's three, seed 2's one)
            foreach (var (file, seconds, budget) in new[]
            {
                // Budgets: 0. Any settle here is a new dance (the budgets were ratcheted down from 55-86 as each cause went).
                ("room2-refuel-and-outpost-jam.json", 60f, 0),
                ("room2-after-rules38.json", 60f, 0),
                ("room2-spinning-soldiers.json", 60f, 0),
                // A scripted-AI game (seed 5, 96x96) 217 s in: a soldier that has just arrived is sent back the way it came, through a one-tile gap between buildings.
                ("ai-seed5-base-exit.json", 10f, 0),
            })
                GateFixture(file, seconds, budget);
        }

        static string Settles(World w) => w.SettleCounts.Count == 0 ? "none" : string.Join(", ", w.SettleCounts.Select(kv => $"{kv.Key} x{kv.Value}"));

        static void GateMinerProtection()
        {
            // A stationary raid remains visible from a scout while the miners withdraw: no repeated order writes or
            // return trips just because the withdrawing trucks leave weapon range. Its exit band belongs to the field.
            var (game, trucks, _) = MinerScene(); var w = game.World;
            EnableMiners(w, 2);
            var foe = Enemy(w, "rifleman", new Vec2(35.5f, 31.5f)); foe.Cooldown = 10000;
            w.Hurt(trucks[0], 1, null, 1);
            Step(game, 60);
            Check(w.SettleCounts.Count == 0, $"miner withdrawal under a visible 60-second raid has zero settles ({Settles(w)})");
            Check(w.EventCounts.TryGetValue("miners_withdrawn", out int wd) && wd == 1 && !w.EventCounts.ContainsKey("miners_resumed") &&
                trucks.All(t => !t.Dead && t.Order != Order.Harvest),
                "miners stay at their withdrawal point during a persistent raid, without withdraw/resume oscillation");

            // Withdraw, see the raid leave, then resume exactly once at each miner's previous ore stop.
            var clear = MinerScene(); w = clear.game.World;
            EnableMiners(w, 2);
            foe = Enemy(w, "rifleman", new Vec2(35.5f, 31.5f)); foe.Cooldown = 10000;
            w.Hurt(clear.trucks[0], 1, null, 1);
            Step(clear.game, 2); At(foe, new Vec2(40.5f, 31.5f));
            Step(clear.game, 58);
            Check(w.SettleCounts.Count == 0, $"miner withdrawal and clear-field resumption over 60 seconds has zero settles ({Settles(w)})");
            Check(w.EventCounts.TryGetValue("miners_withdrawn", out wd) && wd == 1 && w.EventCounts.TryGetValue("miners_resumed", out int rs) && rs == 1,
                "a raid seen leaving resumes mining once, without hold/advance or withdrawal oscillation");

            var loaded = MinerScene(); w = loaded.game.World;
            EnableMiners(w, 2, "base");
            foreach (var truck in loaded.trucks) { truck.Cargo = 90; truck.CargoType = Map.Iron; }
            w.Hurt(loaded.trucks[0], 1, null, 1); w.Remove(loaded.scout);
            Step(loaded.game, 60);
            Check(w.SettleCounts.Count == 0, $"loaded miners withdrawing to base and unloading over 60 seconds have zero settles ({Settles(w)})");
            Check(loaded.trucks.All(t => !t.Dead && t.Cargo == 0 && t.Order != Order.Harvest) && !w.EventCounts.ContainsKey("miners_resumed"),
                "unloading at base cannot restart mining while the policy still owns a withdrawn truck");

            var shared = MinerScene(); w = shared.game.World;
            for (int y = 30; y <= 33; y++) for (int x = 40; x <= 43; x++)
            {
                int i = w.Map.Idx(x, y); w.Map.Ore[i] = w.Map.OreBase[i] = 500;
                w.Map.OreType[i] = w.Map.OreBaseType[i] = Map.Iron;
            }
            w.Map.FieldsChanged();
            var allTrucks = shared.trucks.ToList();
            for (int i = 0; i < 3; i++)
            {
                var truck = At(Mine0(w, "mining_truck"), new Vec2(40.5f + i, 31.5f));
                w.SetOrder(truck, Order.Harvest, truck.Pos); allTrucks.Add(truck);
            }
            EnableMiners(w); w.Hurt(allTrucks[0], 1, null, 1); w.Hurt(allTrucks[3], 1, null, 1);
            Check(allTrucks.Select(t => t.OrderPos).Distinct().Count() == allTrucks.Count,
                "two withdrawing fields reserve distinct goals at the same agent-supplied destination");
            w.Remove(shared.scout); Step(shared.game, 60);
            Check(w.SettleCounts.Count == 0 && allTrucks.All(t => !t.Dead && t.Order != Order.Harvest),
                $"two mining fields withdrawing together over 60 seconds have zero settles and no return oscillation ({Settles(w)})");
        }

        /// <summary>Six aircraft low on fuel make for one airfield (four slots): they land in their own slots or hold until one
        /// frees, and none hovers over the pad shoving the others until it runs dry (they used to, and crashed).</summary>
        static void GateAirfieldRush()
        {
            var w = new World(2, 7, 80);
            var airfield = w.SpawnStructure(0, "airfield", w.FindPlacement(0, "airfield").Value, 1f);
            var keys = new[] { "stealth_bomber", "stealth_bomber", "gunship", "gunship", "recon_drone", "gunship" };
            var planes = keys.Select((k, i) => At(w.SpawnUnit(0, k, airfield), airfield.Center + new Vec2(2 + i * 1.7f, 10))).ToList();
            foreach (var p in planes) { p.Fuel = p.FuelMax * 0.3f; w.BeginRefuel(p, airfield); }
            int maxLanded = 0; float closest = float.MaxValue;
            var topped = new HashSet<int>();
            for (int i = 0; i < 90 * World.TickRate; i++)
            {
                w.Step();
                if (Environment.GetEnvironmentVariable("PEZ_AIRDBG") == "1" && i % 40 == 0)
                    Console.WriteLine($"{w.Time:0.0}s " + string.Join(" | ", planes.Select(p => $"#{p.Id} {p.Def.Key[..4]} {(p.Dead ? "DEAD" : $"{p.OrderName} at {p.Pos.X:0.0},{p.Pos.Y:0.0} to {p.OrderPos.X:0.0},{p.OrderPos.Y:0.0} f{p.FuelFraction:P0} L{(p.Landed ? 1 : 0)} m{(p.Moving ? 1 : 0)}")}")));
                foreach (var p in planes) if (!p.Dead && p.Fuel >= p.FuelMax * 0.95f) topped.Add(p.Id);
                var landed = planes.Where(p => !p.Dead && p.Landed).ToList();
                maxLanded = Math.Max(maxLanded, landed.Count);
                for (int a = 0; a < landed.Count; a++) for (int b = a + 1; b < landed.Count; b++) closest = MathF.Min(closest, Vec2.Dist(landed[a].Pos, landed[b].Pos));
            }
            Check(planes.All(p => !p.Dead) && topped.Count == planes.Count,
                  $"six aircraft refuel at one airfield, taking turns, and none runs dry ({string.Join(", ", planes.Select(p => $"{p.Def.Key} {(p.Dead ? "crashed" : $"{p.FuelFraction:P0}")}"))})");
            Check(maxLanded <= 4 && closest >= 1.4f,
                  $"they land in their own slots, never on top of each other (at most {maxLanded} at once, closest {closest:0.00} apart)");
            Check(w.SettleCounts.Count == 0, $"no aircraft had to be settled at the airfield ({Settles(w)})");
        }

        /// <summary>A mixed army of 48 sent to one point arrives and stands. Budget: a unit or two the crowd still wedges short
        /// of its slot (settled there, as arrived; phase 3 takes it to 0).</summary>
        static void GateCrowd(int budget)
        {
            var game = new Game(new GameConfig { Seed = 3, MapSize = 80, Controllers = new[] { "llm", "llm" } });
            var w = game.World;
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            string[] mix = { "mammoth_tank", "heavy_tank", "heavy_tank", "artillery", "light_tank", "rifleman", "rocket_soldier", "rifleman" };
            var ids = new List<object>();
            for (int i = 0; i < 48; i++)
            {
                var u = At(w.SpawnUnit(0, mix[i % mix.Length], hq), hq.Center + new Vec2(4 + (i % 8) * 1.6f, -6 + (i / 8) * 1.6f));
                ids.Add((double)u.Id);
            }
            Commands.Execute(w, 0, new Dictionary<string, object> { ["type"] = "move", ["units"] = ids, ["x"] = (double)(hq.Center.X + 22), ["y"] = (double)hq.Center.Y });
            for (int i = 0; i < 40 * World.TickRate; i++) game.Advance(World.Dt);
            Check(w.SettleCounts.Values.Sum() <= budget, $"a 48-unit army sent to one point arrives, within its budget of {budget} settled ({Settles(w)})");
        }

        /// <summary>The guard itself: a soldier an agent keeps sending back and forth every half second (it can only go
        /// back and forth) is settled within a few seconds, and stands.</summary>
        static void GateGuardCatchesADance()
        {
            var w = new World(2, 7, 80);
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var at = hq.Center + new Vec2(10, 10);
            var s = At(w.SpawnUnit(0, "rifleman", hq), at);
            float caught = -1f;
            for (int i = 0; i < 8 * World.TickRate; i++)
            {
                if (i % 10 == 0) w.SetOrder(s, Order.Move, at + new Vec2((i / 10) % 2 == 0 ? 3f : -3f, 0));
                w.Step();
                if (caught < 0 && w.Time < s.SettledUntil) caught = w.Time;
            }
            Check(caught > 0 && caught < 4f, $"the dance guard catches a soldier sent back and forth every half second (settled at {caught:0.0}s: {Settles(w)})");
        }

        /// <summary>The starting economy on thin ore, mining and delivering for three minutes: no truck settled, and none goes stop-go
        /// (drive a tile, stop, drive a tile: the slide-pause rhythm of trucks hopping across thin ore).</summary>
        static void GateEconomy(int seed, int size)
        {
            var game = new Game(new GameConfig { Seed = seed, MapSize = size, Controllers = new[] { "llm", "llm" } });
            var w = game.World;
            // Thin ore, as a field is late in a game: a few loads' worth per tile, where trucks used to hop tile to tile.
            for (int k = 0; k < w.Map.Ore.Length; k++) if (w.Map.Ore[k] > 12) w.Map.Ore[k] = 12;
            // Short stops (2-15 ticks between moves) by unit, with when: a beat is 8 or more of them inside 10 seconds.
            var runs = new Dictionary<int, (bool moving, int ticks)>();
            var stops = new Dictionary<int, List<float>>();
            for (int i = 0; i < 180 * World.TickRate; i++)
            {
                game.Advance(World.Dt);
                foreach (var e in w.Entities)
                {
                    if (e.Dead || e.IsStructure || e.IsCarried) continue;
                    bool mv = Vec2.Dist(e.Pos, e.PrevPos) > 0.01f;
                    (bool moving, int ticks) r = runs.TryGetValue(e.Id, out var x) ? x : (mv, 0);
                    if (mv == r.moving) r.ticks++;
                    else
                    {
                        if (!r.moving && r.ticks >= 2 && r.ticks <= 15)
                        {
                            if (!stops.TryGetValue(e.Id, out var l)) stops[e.Id] = l = new List<float>(); l.Add(w.Time);
                            if (Environment.GetEnvironmentVariable("PEZ_BEATDBG") == e.Id.ToString()) Console.WriteLine($"  stop {w.Time:0.00} #{e.Id} {r.ticks}t {e.OrderName} dock {e.Dock} at {e.Pos} tile {e.HarvestTile?.X},{e.HarvestTile?.Y} cargo {e.Cargo} settled {w.Time < e.SettledUntil}");
                        }
                        r = (mv, 1);
                    }
                    runs[e.Id] = r;
                }
            }
            int Beat(List<float> t) { int best = 0; for (int a = 0, b = 0; b < t.Count; b++) { while (t[b] - t[a] > 10f) a++; best = Math.Max(best, b - a + 1); } return best; }
            var stopGo = stops.Where(kv => Beat(kv.Value) >= 8).Select(kv => w.Get(kv.Key)).Where(e => e != null).ToList();
            Check(w.SettleCounts.Count == 0, $"three minutes of mining and delivering settles no truck (seed {seed}, {size}x{size}: {Settles(w)})");
            Check(stopGo.Count == 0, $"nothing goes stop-go on a beat while mining (seed {seed}, {size}x{size}: {string.Join(", ", stopGo.Select(e => $"{e.Def.Key} #{e.Id}: {Beat(stops[e.Id])} short stops in 10 s"))})");
        }

        /// <summary>Two scripted AIs fight for a while: settles within budget (the firing-line edge, phase 2), none of aircraft.</summary>
        static void GateAiGame(float minutes, int budget)
        {
            var game = new Game(new GameConfig { Seed = 5, MapSize = 96, Controllers = new[] { "ai", "ai" } });
            for (int i = 0; i < minutes * 60 * World.TickRate && !game.World.GameOver; i++) game.Advance(World.Dt);
            var w = game.World;
            int total = w.SettleCounts.Values.Sum();
            Check(total <= budget, $"{minutes:0} minutes of AI against AI: {total} settled, within its budget of {budget} ({Settles(w)})");
        }

        /// <summary>More AI games on other seeds (other maps and base layouts), with one budget for them all.</summary>
        static void GateAiSeeds(int[] seeds, float minutes, int budget)
        {
            int total = 0; var what = new List<string>();
            foreach (var seed in seeds)
            {
                var game = new Game(new GameConfig { Seed = seed, MapSize = 96, Controllers = new[] { "ai", "ai" } });
                for (int i = 0; i < minutes * 60 * World.TickRate && !game.World.GameOver; i++) game.Advance(World.Dt);
                int n = game.World.SettleCounts.Values.Sum();
                total += n;
                if (n > 0) what.Add($"seed {seed}: {Settles(game.World)}");
            }
            Check(total <= budget, $"{seeds.Length} more AI games ({minutes:0} min each, seeds {string.Join(",", seeds)}): {total} settled, within their budget of {budget} ({(what.Count == 0 ? "none" : string.Join("; ", what))})");
        }

        /// <summary>A live room's snapshot from a reported dance, run on: within its budget of settles, and no aircraft settled.</summary>
        static void GateFixture(string file, float seconds, int budget)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "fixtures"))) dir = dir.Parent;
            if (dir == null) { Check(false, $"movement fixture {file}: no fixtures folder"); return; }
            var game = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
            game.Restore(Snapshot.Parse(File.ReadAllText(Path.Combine(dir.FullName, "fixtures", file))));
            for (int i = 0; i < seconds * World.TickRate; i++) game.Advance(World.Dt);
            var w = game.World;
            int total = w.SettleCounts.Values.Sum();
            bool air = w.SettleCounts.Keys.Any(k => k.StartsWith("stealth_bomber") || k.StartsWith("gunship") || k.Contains("drone"));
            Check(total <= budget && !air, $"{file}: {total} settled in {seconds:0} s, within its budget of {budget}, no aircraft ({Settles(w)})");
        }
    }
}
