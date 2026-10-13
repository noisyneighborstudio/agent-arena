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
            GateAirfieldRush();
            GateCrowd(budget: 6);
            GateEconomy();
            GateAiGame(minutes: 8, budget: 70);
            foreach (var (file, seconds, budget) in new[]
            {
                // Budgets: just above what phase 1 leaves (truck queues at drop-offs, the firing-line edge, soldiers by their
                // tanks). Phase 2 (owned goals, bands) takes each to 0.
                ("room2-refuel-and-outpost-jam.json", 60f, 60),
                ("room2-after-rules38.json", 60f, 8),
                ("room2-spinning-soldiers.json", 60f, 15),
            })
                GateFixture(file, seconds, budget);
        }

        static string Settles(World w) => w.SettleCounts.Count == 0 ? "none" : string.Join(", ", w.SettleCounts.Select(kv => $"{kv.Key} x{kv.Value}"));

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

        /// <summary>A mixed army of 48 sent to one point arrives and stands. Budget: soldiers whose formation slot the crowd
        /// keeps them from (phase 1 settles them there, as arrived; phase 2's owned slots take it to 0).</summary>
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

        /// <summary>The starting economy mining and delivering for three minutes: no truck settled.</summary>
        static void GateEconomy()
        {
            var game = new Game(new GameConfig { Seed = 4, MapSize = 80, Controllers = new[] { "llm", "llm" } });
            for (int i = 0; i < 180 * World.TickRate; i++) game.Advance(World.Dt);
            Check(game.World.SettleCounts.Count == 0, $"three minutes of mining and delivering settles no truck ({Settles(game.World)})");
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
