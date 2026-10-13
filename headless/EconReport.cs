using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>
    /// --econ-report: the economy's measurement harness, so a balance change can be checked with numbers (issue #56; the audit's
    /// method, kept). It plays the real sim only (the repo's own sources, nothing scratch) and prints a markdown report in five
    /// sections, each one a question about the rules text:
    ///   rates   what a converter, a deep mine, a derrick, a fusion reactor and a defence wall really make or cost, against what
    ///           the rules, the defs and the docs say (a table of claim, measured, verdict);
    ///   regrow  a mined-bare map left to regrow: ore a second, by ore, middle against edges;
    ///   maps    surface ore and deep deposits by map size and richness, per base site, and as joins grow the map;
    ///   kit     what a late joiner is given at each age of the arena;
    ///   matrix  scripted AI against scripted AI over seeds x map sizes x richness x war/passive: income by source over time,
    ///           unspent stock by item, low-power time, deep ore used, and the seat win split.
    /// Flags (all optional):
    ///   --econ-only rates,regrow,maps,kit,matrix   sections to run (default: all)
    ///   --econ-seeds 1-12        seeds of the matrix (a range or a list: 1,2,5)
    ///   --econ-sizes 80,128      map sizes of the matrix
    ///   --econ-ores 1,0.3        ore richness of the matrix
    ///   --econ-modes war,passive scripted AI (attacks in waves) or house AI (builds and defends only)
    ///   --econ-minutes 45        game-minutes per matrix game
    ///   --econ-jobs 8            games played at once (each game is its own World; they share nothing but the read-only defs)
    ///   --econ-out DIR           also write report.md, games.csv and minutes.csv there
    /// A matrix game is deterministic: the same seed, size, richness and mode give the same numbers on any run, whatever --econ-jobs says.
    /// The full default matrix (96 games of 45 game-minutes) takes about 10 minutes; DOTNET_gcServer=1 in the environment roughly halves that.
    /// Example: DOTNET_gcServer=1 dotnet run -c Release -- --econ-report --econ-out /tmp/econ
    /// </summary>
    public static class EconReport
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static string F(double v, string fmt = "0.##") => v.ToString(fmt, IC);

        public sealed class Options
        {
            public int[] Seeds = Enumerable.Range(1, 12).ToArray();
            public int[] Sizes = { 80, 128 };
            public float[] Ores = { 1f, 0.3f };
            public string[] Modes = { "war", "passive" };
            public int Minutes = 45;
            public int Jobs = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
            public string OutDir;
            public HashSet<string> Sections = new HashSet<string> { "rates", "regrow", "maps", "kit", "matrix" };
        }

        public static int Run(string[] args)
        {
            string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
            var o = new Options();
            o.Seeds = ParseInts(Arg("--econ-seeds", "1-12"));
            o.Sizes = ParseInts(Arg("--econ-sizes", "80,128"));
            o.Ores = Arg("--econ-ores", "1,0.3").Split(',').Select(s => float.Parse(s, IC)).ToArray();
            o.Modes = Arg("--econ-modes", "war,passive").Split(',');
            o.Minutes = int.Parse(Arg("--econ-minutes", "45"));
            o.Jobs = int.Parse(Arg("--econ-jobs", o.Jobs.ToString()));
            o.OutDir = Arg("--econ-out", null);
            if (Arg("--econ-only", null) != null) o.Sections = new HashSet<string>(Arg("--econ-only", "").Split(','));
            var unknown = o.Sections.Where(s => s != "rates" && s != "regrow" && s != "maps" && s != "kit" && s != "matrix").ToList();
            if (unknown.Count > 0) { Console.Error.WriteLine("--econ-only takes rates, regrow, maps, kit, matrix; not " + string.Join(", ", unknown)); return 2; }
            foreach (var m in o.Modes) if (m != "war" && m != "passive") { Console.Error.WriteLine("--econ-modes takes war and passive; not " + m); return 2; }
            var report = Build(o, Console.Error.WriteLine, out var games);
            Console.Write(report);
            if (o.OutDir != null)
            {
                Directory.CreateDirectory(o.OutDir);
                File.WriteAllText(Path.Combine(o.OutDir, "report.md"), report);
                if (games.Count > 0) WriteCsv(o.OutDir, games);
            }
            return games.Any(g => g.Errors > 0) ? 1 : 0;
        }

        /// <summary>"1-12" or "1,2,5" or "80".</summary>
        internal static int[] ParseInts(string s)
        {
            var list = new List<int>();
            foreach (var part in s.Split(','))
            {
                int dash = part.IndexOf('-', 1);
                if (dash > 0) { int a = int.Parse(part.Substring(0, dash), IC), b = int.Parse(part.Substring(dash + 1), IC); for (int i = a; i <= b; i++) list.Add(i); }
                else list.Add(int.Parse(part, IC));
            }
            return list.ToArray();
        }

        public static string Build(Options o, Action<string> progress, out List<GameRun> games)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Economy report");
            sb.AppendLine();
            sb.AppendLine($"Sections: {string.Join(", ", o.Sections)}. Rules as built: rules_version {StateView.RulesVersion}.");
            games = new List<GameRun>();
            if (o.Sections.Contains("rates")) { progress("rates..."); RatesSection(sb); }
            if (o.Sections.Contains("regrow")) { progress("regrowth..."); RegrowSection(sb); }
            if (o.Sections.Contains("maps")) { progress("maps..."); MapsSection(sb); }
            if (o.Sections.Contains("matrix"))
            {
                games = PlayMatrix(o, progress);
            }
            if (o.Sections.Contains("kit")) { progress("catch-up kit..."); KitSection(sb, games); }
            if (games.Count > 0) MatrixSection(sb, games, o);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ markdown helpers

        static void Heading(StringBuilder sb, string text) { sb.AppendLine(); sb.AppendLine("## " + text); sb.AppendLine(); }
        static void Sub(StringBuilder sb, string text) { sb.AppendLine(); sb.AppendLine("### " + text); sb.AppendLine(); }

        static void Table(StringBuilder sb, string[] head, IEnumerable<string[]> rows)
        {
            sb.AppendLine("| " + string.Join(" | ", head) + " |");
            sb.AppendLine("|" + string.Concat(head.Select(_ => " --- |")));
            foreach (var r in rows) sb.AppendLine("| " + string.Join(" | ", r) + " |");
            sb.AppendLine();
        }

        static double Mean(IEnumerable<double> xs) { var l = xs.ToList(); return l.Count == 0 ? 0 : l.Average(); }
        static double Median(IEnumerable<double> xs) { var l = xs.OrderBy(x => x).ToList(); return l.Count == 0 ? 0 : l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2; }
        static double Percentile(IEnumerable<double> xs, double p) { var l = xs.OrderBy(x => x).ToList(); return l.Count == 0 ? 0 : l[Math.Min(l.Count - 1, (int)(p * l.Count))]; }

        /// <summary>The two-sided exact binomial test of k successes in n trials against even odds.</summary>
        public static double BinomialP(int k, int n)
        {
            if (n == 0) return 1;
            var logFact = new double[n + 1];
            for (int i = 2; i <= n; i++) logFact[i] = logFact[i - 1] + Math.Log(i);
            double Pmf(int x) => Math.Exp(logFact[n] - logFact[x] - logFact[n - x] + n * Math.Log(0.5));
            double mine = Pmf(k), p = 0;
            for (int x = 0; x <= n; x++) if (Pmf(x) <= mine * (1 + 1e-9)) p += Pmf(x);
            return Math.Min(1, p);
        }

        /// <summary>The raw ore value of a stockpile or a cost (World.OreMix: steel is iron ore, a circuit three, and so on).</summary>
        static double OreValue(string item, double amount) => World.OreMix.TryGetValue(item, out var mix) ? amount * mix.Sum() : 0;
        static double OreValue(Dictionary<string, int> cost) => cost.Sum(kv => OreValue(kv.Key, kv.Value));

        // ------------------------------------------------------------------ rates

        /// <summary>One thing the rules or the code claim, and what was measured.</summary>
        public sealed class Rate
        {
            public string Name, Claimed, Where, Verdict;
            public double Expect, Measured, Tolerance;
            public Rate(string name, string where, double expect, double measured, double tolerance, string unit = "")
            {
                Name = name; Where = where; Expect = expect; Measured = measured; Tolerance = tolerance;
                Claimed = F(expect) + unit;
                Verdict = Math.Abs(measured - expect) <= tolerance * Math.Abs(expect) + 1e-9 ? "ok" : "DIFFERS";
            }
            public bool Ok => Verdict == "ok";
        }

        /// <summary>A fresh two-team map with the first team's trucks gone and `plants` power plants up: nothing moves, so a rate is only the thing measured.</summary>
        static World Bench(int plants, int seed = 7, int size = 96)
        {
            var w = new World(2, seed, size);
            w.SuddenDeathAt = 4 * 3600f;
            foreach (var e in w.Entities.Where(e => e.IsHarvester).ToList()) w.Remove(e);
            for (int i = 0; i < plants; i++) w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
            w.Teams[0].Stock.Clear();
            return w;
        }

        static float Stock(Team t, string item) => t.Stock.TryGetValue(item, out var v) ? v : 0f;

        static void Steps(World w, double seconds) { for (int i = 0; i < seconds * World.TickRate; i++) w.Step(); }

        /// <summary>What `read` gains a second over `seconds` of play, after `setup` has built the scene.</summary>
        static double PerSecond(World w, Func<World, double> read, double seconds)
        {
            double before = read(w);
            Steps(w, seconds);
            return (read(w) - before) / seconds;
        }

        /// <summary>A finished structure for team 0 where the build rules would put it (a refinery's free truck is sent away again: nothing moves in a bench).</summary>
        static Entity Build(World w, string key)
        {
            var spot = w.FindPlacement(0, key);
            var e = spot.HasValue ? w.SpawnStructure(0, key, spot.Value, 1f) : null;
            foreach (var truck in w.Entities.Where(x => x.IsHarvester).ToList()) w.Remove(truck);
            return e;
        }

        /// <summary>Every rate and cost the rules text, the defs and the docs state, measured on the real sim.</summary>
        public static List<Rate> MeasureRates()
        {
            var rs = new List<Rate>();
            // --- the command center's trickle
            {
                var w = Bench(2); // 2 plants + the command center's 20: nothing short
                rs.Add(new Rate("command center trickle, iron_ore/s", "rules (\"trickles 1 iron_ore/s\"), TECH_TREE", 1, PerSecond(w, x => Stock(x.Teams[0], "iron_ore"), 60), 0.05));
                var low = Bench(0);
                Build(low, "mining_refinery"); // 50 power wanted, 20 made: low power (and the refinery turns the trickle into steel as it comes, so count both)
                low.Teams[0].Stock.Clear();
                Steps(low, 1);
                rs.Add(new Rate("command center trickle in low power, iron_ore/s", "rules (\"low power halves every rate\")", 0.5, PerSecond(low, x => Stock(x.Teams[0], "iron_ore") + Stock(x.Teams[0], "steel"), 60), 0.05));
            }
            // --- converters, each with its inputs in plenty
            double Converter(string building, string[] give, string[] outputs, double seconds = 60)
            {
                var w = Bench(4);
                Build(w, building);
                foreach (var item in give) w.Teams[0].Add(item, 100000);
                return PerSecond(w, x => outputs.Sum(i => Stock(x.Teams[0], i)), seconds);
            }
            rs.Add(new Rate("mining refinery, steel/s", "TECH_TREE (5/s), #42", 5, Converter("mining_refinery", new[] { "iron_ore" }, new[] { "steel" }), 0.03));
            rs.Add(new Rate("mining refinery, copper/s", "TECH_TREE (5/s)", 5, Converter("mining_refinery", new[] { "copper_ore" }, new[] { "copper" }), 0.03));
            rs.Add(new Rate("mining refinery, steel + copper together, /s", "#42 (5 + 5/s)", 10, Converter("mining_refinery", new[] { "iron_ore", "copper_ore" }, new[] { "steel", "copper" }), 0.03));
            rs.Add(new Rate("electronics plant, circuits/s", "TECH_TREE (1/s)", 1, Converter("electronics_plant", new[] { "steel", "copper" }, new[] { "circuits" }), 0.05));
            rs.Add(new Rate("optics lab, lenses/s", "TECH_TREE (0.5/s)", 0.5, Converter("optics_lab", new[] { "crystal" }, new[] { "lenses" }), 0.05));
            rs.Add(new Rate("enrichment plant, plasma/s", "TECH_TREE (0.4/s)", 0.4, Converter("enrichment_plant", new[] { "uranium" }, new[] { "plasma" }), 0.05));
            rs.Add(new Rate("composite foundry, composite/s", "TECH_TREE (0.5/s)", 0.5, Converter("composite_foundry", new[] { "steel", "crystal" }, new[] { "composite" }), 0.05));
            {
                var w = Bench(0);
                Build(w, "mining_refinery"); // low power: 20 made, 50 wanted
                w.Teams[0].Add("iron_ore", 100000);
                Steps(w, 1);
                rs.Add(new Rate("mining refinery in low power, steel/s", "TECH_TREE (\"low power halves every rate\")", 2.5, PerSecond(w, x => Stock(x.Teams[0], "steel"), 60), 0.05));
            }
            // --- a derrick
            {
                var w = Bench(4);
                w.Derricks.First().Team = 0;
                rs.Add(new Rate("derrick, steel/s", "rules, OPEN_ARENA, TECH_TREE, World.DerrickSteel", 1.5, PerSecond(w, x => Stock(x.Teams[0], "steel"), 60), 0.03));
            }
            // --- a deep mine, in full and in low power
            foreach (bool low in new[] { false, true })
            {
                var w = Bench(low ? 0 : 4);
                var t = w.Teams[0];
                var d = w.Map.Deep.OrderBy(x => Vec2.Dist(x.Pos, t.StartPos)).First(x => x.Type != Map.Iron);
                t.Surveyed.Add(d.Id);
                var rig = w.SpawnUnit(0, "drill_rig", w.Owned(0).First(e => e.Def.Key == "command_center"));
                rig.Pos = rig.PrevPos = d.Pos;
                string why = w.Deploy(rig, d);
                if (why != null) { rs.Add(new Rate("deep mine could not be deployed: " + why, "", 4, 0, 0)); continue; }
                Steps(w, 1);
                rs.Add(new Rate(low ? "deep mine in low power, ore/s" : "deep mine, ore/s", "rules (\"pumps 4 ore/s\"), Defs, #42", low ? 2 : 4, -PerSecond(w, x => d.Amount, 60), 0.03));
            }
            // --- the deep mine's running-low warning, in seconds before it runs dry
            {
                var w = Bench(4);
                var t = w.Teams[0];
                var d = w.Map.Deep.OrderBy(x => Vec2.Dist(x.Pos, t.StartPos)).First();
                t.Surveyed.Add(d.Id);
                d.Amount = World.DeepMineWarnAmount + 400f;
                var rig = w.SpawnUnit(0, "drill_rig", w.Owned(0).First(e => e.Def.Key == "command_center"));
                rig.Pos = rig.PrevPos = d.Pos;
                w.Deploy(rig, d);
                double warnedAt = -1, startAmount = d.Amount;
                for (int i = 0; i < 600 * World.TickRate && warnedAt < 0; i++)
                {
                    w.Step();
                    if (w.Alerts.Active(w, 0).Any(a => a.Kind == "deep_mine_running_low")) warnedAt = d.Amount;
                }
                rs.Add(new Rate("deep mine warns this many minutes before it is dry", "World.DeepMineWarnAmount comment, rules (\"warn 5 minutes before\"), docs/playtests", 5, warnedAt < 0 ? 0 : warnedAt / EntityDef.DeepMineRate / 60, 0.05));
            }
            // --- the fusion reactor: burn, power, and what it makes short of plasma
            {
                var w = Bench(0);
                var t = w.Teams[0];
                var plain = Bench(0);
                int before = t.PowerProduced;
                Build(w, "fusion_reactor");
                t.Add("plasma", 1000);
                Steps(w, 2);
                rs.Add(new Rate("fusion reactor power", "rules, TECH_TREE (+500)", 500, t.PowerProduced - before, 0.001));
                double burn = -PerSecond(w, x => Stock(x.Teams[0], "plasma") - x.Teams[0].PlasmaOwed, 120);
                rs.Add(new Rate("fusion reactor burn, plasma/s", "rules, TECH_TREE, World.FusionBurn (0.03/s)", World.FusionBurn, burn, 0.03));
                rs.Add(new Rate("fusion reactor burn, uranium an hour", "rules (\"216 uranium an hour\")", 216, burn * 3600 * 2, 0.03));
                // Fed half of what it burns, it should make half of its power.
                var half = Bench(0);
                var ht = half.Teams[0];
                var reactor = Build(half, "fusion_reactor");
                for (int i = 0; i < 30 * World.TickRate; i++) { half.Step(); ht.Add("plasma", World.FusionBurn * World.Dt * 0.5f); }
                rs.Add(new Rate("fusion reactor fed half its burn: share of its power", "rules (\"proportionally less power instead of none\")", 0.5, reactor.Output, 0.1));
                // What it costs against five power plants, in raw ore.
                double fusion = OreValue(Defs.Get("fusion_reactor").Cost), plants = 5 * OreValue(Defs.Get("power_plant").Cost);
                rs.Add(new Rate("fusion reactor price, ore", "rules, TECH_TREE (about 750)", 750, fusion, 0.02));
                rs.Add(new Rate("five power plants' price, iron ore", "rules, TECH_TREE (1,250)", 1250, plants, 0.001));
                double breakEven = (plants - fusion) / (World.FusionBurn * 3600 * 2);
                rs.Add(new Rate("hours of fuel before the reactor costs more ore than five plants", "World.FusionBurn comment (2.3 hours)", 2.3, breakEven, 0.05));
            }
            // --- defence upkeep
            foreach (var (key, price) in new[] { ("gun_turret", 6.0), ("sam_site", 8.0), ("laser_tower", 10.0) })
            {
                var w = Bench(4);
                var t = w.Teams[0];
                int built = 0;
                for (int i = 0; i < 11 && Build(w, key) != null; i++) built++;
                t.Add("steel", 100000);
                Steps(w, 1);
                double paid = PerSecond(w, x => x.Teams[0].Stats.UpkeepPaid, 120) * 60;
                rs.Add(new Rate($"upkeep of a wall of {built} {key}s, steel a minute", "rules (4% of the steel price a minute past the first 4)", 7 * price, paid, 0.03));
            }
            // --- what a fresh team is given
            {
                var w = new World(2, 1, 80);
                var t = w.Teams[0];
                rs.Add(new Rate("starting iron_ore", "rules, TECH_TREE (500)", 500, t.Amount("iron_ore"), 0));
                rs.Add(new Rate("starting copper_ore", "rules, TECH_TREE (150)", 150, t.Amount("copper_ore"), 0));
                rs.Add(new Rate("starting mining trucks", "TECH_TREE (one)", 1, w.Owned(0).Count(e => e.IsHarvester), 0));
                rs.Add(new Rate("starting riflemen", "TECH_TREE (two)", 2, w.Owned(0).Count(e => e.Def.Key == "rifleman"), 0));
                rs.Add(new Rate("power plant, power", "TECH_TREE (+100)", 100, Defs.Get("power_plant").Power, 0));
                rs.Add(new Rate("power plant price, iron_ore", "TECH_TREE, rules (250)", 250, Defs.Get("power_plant").Cost["iron_ore"], 0));
                rs.Add(new Rate("mining truck load, ore", "TECH_TREE, Defs (150 per trip)", 150, Defs.Get("mining_truck").HarvestCapacity, 0));
                var open = new World(2, 1, 80) { Open = true, MaxPlayers = 8 };
                var late = open.AddTeam("llm", "Late", out _);
                rs.Add(new Rate("newcomer protection, seconds", "rules, OPEN_ARENA (5 minutes)", 300, late.ProtectedUntil - open.Time, 0));
            }
            // --- a truck's turn in the bay
            {
                var w = new World(2, 7, 96);
                var truck = w.Owned(0).First(e => e.IsHarvester);
                Commands.Execute(w, 0, new Dictionary<string, object> { ["type"] = "harvest", ["units"] = new List<object> { (double)truck.Id }, ["ore"] = "iron_ore" });
                var cycles = new List<double>();
                int started = -1;
                for (int i = 0; i < 240 * World.TickRate; i++)
                {
                    w.Step();
                    bool inBay = truck.Dock >= DockStep.Align;
                    if (inBay && started < 0) started = w.Tick;
                    if (!inBay && started >= 0) { cycles.Add((w.Tick - started) * World.Dt); started = -1; }
                }
                rs.Add(new Rate("a truck's turn in the bay (line up, back in, unload, pull out), seconds", "rules (\"about 6 s\")", 6, cycles.Count == 0 ? 0 : cycles.Average(), 0.1));
                // Ten trucks on the one drop-off keep its bay full: what it can take is a load per turn.
                var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
                var fleet = new List<object> { (double)truck.Id };
                for (int i = 0; i < 9; i++) fleet.Add((double)w.SpawnUnit(0, "mining_truck", hq).Id);
                Commands.Execute(w, 0, new Dictionary<string, object> { ["type"] = "harvest", ["units"] = fleet, ["ore"] = "iron_ore" });
                Steps(w, 60);
                double perSecond = PerSecond(w, x => x.Teams[0].Stats.OreMined, 120);
                rs.Add(new Rate("ore a second one drop-off's bay can take from a queue of trucks", "rules (\"about every 7 s (about 20 ore a second)\")", 20, perSecond, 0.1));
            }
            // --- what a kill leaves: salvage against the victim's value, and a derrick's worth
            {
                var w = Bench(0);
                var killer = w.SpawnUnit(0, "light_tank", w.Owned(0).First(e => e.IsStructure));
                var hq1 = w.Owned(1).First(e => e.Def.Key == "command_center");
                double Salvage(string key, int team)
                {
                    int before = w.Map.Ore.Sum();
                    var victim = w.SpawnUnit(team, key, hq1);
                    w.Hurt(victim, 100000, killer);
                    return w.Map.Ore.Sum() - before;
                }
                rs.Add(new Rate("salvage a light_tank leaves, share of its price", "rules (about a quarter), TECH_TREE", 0.25, Salvage("light_tank", 1) / OreValue(Defs.Get("light_tank").Cost), 0.05));
                rs.Add(new Rate("salvage a heavy_tank leaves, share of its price", "rules (about a quarter), TECH_TREE", 0.25, Salvage("heavy_tank", 1) / OreValue(Defs.Get("heavy_tank").Cost), 0.05));
                rs.Add(new Rate("salvage a rifleman leaves, share of its price", "rules (infantry a tenth), TECH_TREE", 0.10, Salvage("rifleman", 1) / OreValue(Defs.Get("rifleman").Cost), 0.05));
                var derrick = w.Derricks.First();
                derrick.Team = 1; // a neutral one can't be hurt: team 1 holds it
                int killValueBefore = w.Teams[0].Stats.KillValue, oreBefore = w.Map.Ore.Sum();
                w.Hurt(derrick, 100000, killer);
                rs.Add(new Rate("kill value of a derrick", "rules (about 600), World.DerrickWorth", World.DerrickWorth, w.Teams[0].Stats.KillValue - killValueBefore, 0.01));
                rs.Add(new Rate("salvage a derrick leaves", "TECH_TREE (a quarter of 600 steel)", World.DerrickWorth / 4.0, w.Map.Ore.Sum() - oreBefore, 0.05));
            }
            // --- derricks by map size
            foreach (int size in new[] { 80, 128, 160, 240, 320 })
            {
                var w = new World(2, 1, size);
                rs.Add(new Rate($"derricks on a {size}x{size} map", "OPEN_ARENA, TECH_TREE (map area / 9,000, 2 to 8)", Math.Clamp((int)Math.Round(size * size / 9000.0), 2, 8), w.Derricks.Count(), 0));
            }
            return rs;
        }

        static void RatesSection(StringBuilder sb)
        {
            Heading(sb, "Rates and costs against what the rules say");
            sb.AppendLine("Each one is measured on a fresh 96x96 map (seed 7) with the trucks gone and power to spare unless the row says low power; a rate is the stockpile's gain over 60 game-seconds.");
            sb.AppendLine();
            var rates = MeasureRates();
            Table(sb, new[] { "what", "claimed", "measured", "verdict", "where it is claimed" },
                rates.Select(r => new[] { r.Name, r.Claimed, F(r.Measured, "0.###"), r.Verdict, r.Where }));
            int bad = rates.Count(r => !r.Ok);
            sb.AppendLine(bad == 0 ? "Every claim above holds." : $"{bad} claim(s) above differ from the measurement: fix the text or the rule.");
        }

        // ------------------------------------------------------------------ regrowth

        /// <summary>A map mined bare (every ore tile cleared, no trucks) left alone: ore regrown after each minute, split by ore and by middle against edges.</summary>
        public sealed class RegrowRun
        {
            public int Size, Seed; public float Ore;
            public int FieldOre;
            public int[] Total = new int[31];                  // by minute
            public int[][] ByOre = new int[31][];              // by minute: iron, copper, crystal, uranium
            public int[] Middle = new int[31];
        }

        public static RegrowRun RegrowBare(int size, float ore, int seed, int minutes)
        {
            var w = new World(2, seed, size, ore);
            w.SuddenDeathAt = 4 * 3600f;
            foreach (var e in w.Entities.Where(e => e.IsHarvester).ToList()) w.Remove(e);
            var r = new RegrowRun { Size = size, Seed = seed, Ore = ore, FieldOre = w.Map.OreBase.Sum(), Total = new int[minutes + 1], ByOre = new int[minutes + 1][], Middle = new int[minutes + 1] };
            Array.Clear(w.Map.Ore, 0, w.Map.Ore.Length);
            float half = 0.5f * MathF.Sqrt(2) * size;
            for (int m = 1; m <= minutes; m++)
            {
                Steps(w, 60);
                var by = new int[4]; int mid = 0, total = 0;
                for (int i = 0; i < w.Map.Ore.Length; i++)
                {
                    if (w.Map.Ore[i] <= 0) continue;
                    total += w.Map.Ore[i];
                    float dx = i % size + 0.5f - size / 2f, dy = i / size + 0.5f - size / 2f;
                    if (MathF.Sqrt(dx * dx + dy * dy) < half * 0.4f) mid += w.Map.Ore[i];
                    if (w.Map.OreBase[i] > 0) by[w.Map.OreBaseType[i]] += w.Map.Ore[i];
                }
                r.Total[m] = total; r.ByOre[m] = by; r.Middle[m] = mid;
            }
            return r;
        }

        static void RegrowSection(StringBuilder sb)
        {
            Heading(sb, "Regrowth of a mined-bare map");
            sb.AppendLine("Every ore tile cleared at minute 0, no trucks, mean of seeds 1-4. \"Middle\" is within 40% of the map's half-diagonal of its centre (the share the rules text and `World.Regrow` quote).");
            sb.AppendLine();
            var rows = new List<string[]>();
            var cases = new[] { (96, 1f), (96, 0.3f), (128, 1f), (128, 0.3f) };
            var runs = new ConcurrentDictionary<(int, float, int), RegrowRun>();
            Parallel.ForEach(cases.SelectMany(c => Enumerable.Range(1, 4).Select(s => (c.Item1, c.Item2, s))), new ParallelOptions { MaxDegreeOfParallelism = 4 }, k => runs[k] = RegrowBare(k.Item1, k.Item2, k.Item3, 30));
            foreach (var (size, ore) in cases)
            {
                var rs = Enumerable.Range(1, 4).Select(s => runs[(size, ore, s)]).ToList();
                double Avg(Func<RegrowRun, double> f) => rs.Average(f);
                double Rate(int from, int to) => Avg(r => (r.Total[to] - r.Total[from]) / ((to - from) * 60.0));
                double Type(int k, int to) => Avg(r => r.ByOre[to][k] / (to * 60.0));
                rows.Add(new[]
                {
                    $"{size}x{size}", F(ore), F(Avg(r => r.FieldOre)), F(Rate(0, 20), "0.0"), F(Rate(0, 10), "0.0"), F(Rate(20, 30), "0.0"),
                    $"{Type(0, 20):0.0} / {Type(1, 20):0.0} / {Type(2, 20):0.0} / {Type(3, 20):0.0}",
                    F(100 * Avg(r => (double)r.Middle[20] / Math.Max(1, r.Total[20])), "0") + "%",
                    F(100 * Avg(r => (double)r.Total[30] / r.FieldOre), "0") + "%",
                });
            }
            Table(sb, new[] { "map", "richness", "field ore", "ore/s, min 0-20", "min 0-10", "min 20-30", "min 0-20 by ore: iron / copper / crystal / uranium", "in the middle", "of field ore back at min 30" }, rows);
        }

        // ------------------------------------------------------------------ maps

        static void MapsSection(StringBuilder sb)
        {
            Heading(sb, "Surface ore and deep deposits by map size");
            sb.AppendLine("Fresh maps, mean of seeds 1-12. \"Home\" is iron and copper within the 20 tiles `World.HomeOreRadius` calls a base's own; \"spread\" is the largest difference between the four base sites' home ore, as a share of the mean (0 = every corner alike).");
            sb.AppendLine();
            var rows = new List<string[]>();
            foreach (float ore in new[] { 1f, 0.3f })
                foreach (int size in new[] { 48, 64, 80, 96, 112, 128, 160, 240, 320 })
                {
                    var maps = Enumerable.Range(1, 12).Select(s => Map.Generate(size, size, s, ore)).ToList();
                    var bare = Enumerable.Range(1, 12).Select(s => Map.Generate(size, size, s, ore, false)).ToList();
                    double total = maps.Average(m => (double)m.OreBase.Sum()), baseOnly = bare.Average(m => (double)m.OreBase.Sum());
                    double Home(Map m, Vec2 s) { double sum = 0; for (int i = 0; i < m.OreBase.Length; i++) if (m.OreBase[i] > 0 && m.OreBaseType[i] <= Map.Copper && Vec2.Dist(new Vec2(i % m.W + .5f, i / m.W + .5f), s) <= World.HomeOreRadius) sum += m.OreBase[i]; return sum; }
                    var homes = maps.Select(m => m.Spawns.Take(4).Select(s => Home(m, s)).ToList()).ToList();
                    double home = homes.Average(h => h.Average());
                    double spread = homes.Average(h => h.Average() == 0 ? 0 : (h.Max() - h.Min()) / h.Average());
                    double deep = maps.Average(m => m.Deep.Sum(d => (double)d.Initial));
                    double deepKit = maps.Average(m => m.Deep.Where(d => Vec2.Dist(d.Pos, m.Spawns[0]) <= Map.DeepKitReach).Sum(d => (double)d.Initial));
                    var derricks = new World(2, 1, size, ore).Derricks.Count();
                    rows.Add(new[] { $"{size}x{size}", F(ore), F(total / 1000, "0.0") + "k", F((total - baseOnly) / 1000, "0.0") + "k", F(home / 1000, "0.0") + "k", F(100 * spread, "0") + "%",
                        F(maps.Average(m => (double)m.Deep.Count), "0.0"), F(deep / 1000, "0") + "k", F(deepKit / 1000, "0.0") + "k", derricks.ToString() });
                }
            Table(sb, new[] { "map", "richness", "surface ore", "of it neutral extra fields", "home ore per base (iron + copper)", "home spread", "deep deposits", "deep ore", "deep ore in one base's kit", "derricks" }, rows);

            Sub(sb, "Deep deposits by ore");
            sb.AppendLine("Every deposit on fresh maps of 80, 128 and 240 tiles, seeds 1-12, richness 1 (deep ore doesn't scale with richness). `Map.DeepAmount` says 4000-8000 iron or copper, 2000-4000 crystal, 1500-3000 uranium.");
            sb.AppendLine();
            var deposits = new List<DeepDeposit>();
            int deepMaps = 0;
            foreach (int size in new[] { 80, 128, 240 })
                foreach (int seed in Enumerable.Range(1, 12)) { deposits.AddRange(Map.Generate(size, size, seed).Deep); deepMaps++; }
            Table(sb, new[] { "ore", "deposits a map", "smallest", "mean", "largest" },
                Enumerable.Range(0, 4).Select(k =>
                {
                    var ds = deposits.Where(d => d.Type == k).ToList();
                    return new[] { Defs.Ores[k], F((double)ds.Count / deepMaps, "0.0"), F(ds.Count == 0 ? 0 : ds.Min(d => d.Initial), "0"), F(ds.Count == 0 ? 0 : ds.Average(d => d.Initial), "0"), F(ds.Count == 0 ? 0 : ds.Max(d => d.Initial), "0") };
                }));

            Sub(sb, "As joins grow the map");
            sb.AppendLine("A 2-player 80x80 open arena at game minute 0 that players join one by one (mean of seeds 1-4): the map grows with each join, and each join lays down a base's own fields and a share of neutral ones.");
            sb.AppendLine();
            var join = new List<string[]>();
            foreach (float ore in new[] { 1f, 0.3f })
            {
                var perPlayers = new Dictionary<int, List<(int size, double surface, double deep, int derricks)>>();
                for (int seed = 1; seed <= 4; seed++)
                {
                    var w = new World(2, seed, 80, ore) { Open = true, MaxPlayers = 8 };
                    for (int players = 2; players <= 8; players++)
                    {
                        if (players > 2 && w.AddTeam("llm", "Joiner" + players, out _) == null) break;
                        if (!perPlayers.TryGetValue(players, out var l)) perPlayers[players] = l = new List<(int, double, double, int)>();
                        l.Add((w.Map.W, w.Map.OreBase.Sum(), w.Map.Deep.Sum(d => (double)d.Initial), w.Derricks.Count()));
                    }
                }
                foreach (var kv in perPlayers.OrderBy(k => k.Key))
                {
                    double surface = kv.Value.Average(x => x.surface), deep = kv.Value.Average(x => x.deep);
                    join.Add(new[] { F(ore), kv.Key.ToString(), F(kv.Value.Average(x => x.size), "0") + "x" + F(kv.Value.Average(x => x.size), "0"), F(surface / 1000, "0.0") + "k", F(surface / kv.Key / 1000, "0.0") + "k", F(deep / 1000, "0") + "k", F(deep / kv.Key / 1000, "0.0") + "k", F(kv.Value.Average(x => x.derricks), "0.#") });
                }
            }
            Table(sb, new[] { "richness", "players", "map", "surface ore", "per player", "deep ore", "per player", "derricks" }, join);
        }

        // ------------------------------------------------------------------ catch-up kit

        sealed class Kit { public double Minute; public Dictionary<string, double> Stock = new Dictionary<string, double>(); public int Plants, Refineries, Trucks; public double Value; }

        static List<Kit> MeasureKits(IEnumerable<double> minutes)
        {
            var kits = new List<Kit>();
            foreach (double minute in minutes)
            {
                var w = new World(2, 1, 80) { Open = true, MaxPlayers = 8 };
                w.Tick = (int)(minute * 60 * World.TickRate); // an arena that age, with nothing played in it
                var t = w.AddTeam("llm", "Late", out _);
                var k = new Kit { Minute = minute };
                foreach (var item in Defs.Items) k.Stock[item] = t.Stock.TryGetValue(item, out var v) ? v : 0;
                k.Plants = w.Owned(t.Id).Count(e => e.Def.Key == "power_plant" && e.IsComplete);
                k.Refineries = w.Owned(t.Id).Count(e => e.Def.Key == "mining_refinery" && e.IsComplete);
                k.Trucks = w.Owned(t.Id).Count(e => e.IsHarvester);
                // Value in raw ore of everything it is given: the stockpile and the buildings and trucks it did not pay for (the founding gift's 650 ore and truck included).
                k.Value = k.Stock.Sum(kv => OreValue(kv.Key, kv.Value))
                          + w.Owned(t.Id).Where(e => e.GrantedShare > 0 && e.Def.Key != "command_center" && e.Def.Key != "rifleman").Sum(e => OreValue(w.ValueOf(e.Def)) * e.GrantedShare);
                kits.Add(k);
            }
            return kits;
        }

        static void KitSection(StringBuilder sb, List<GameRun> games)
        {
            Heading(sb, "The late joiner's catch-up kit");
            sb.AppendLine("A player who joins an open arena of a given age (nothing played in it), as `World.GiveCatchUp` gives it. \"Stock\" includes the founding 500 iron_ore and 150 copper_ore. \"Value\" is the ore value of the stock plus the power plant, refinery and truck, which are gifts too.");
            sb.AppendLine();
            var minutes = new[] { 0.0, 0.5, 1, 3, 5, 10, 20, 30, 60 };
            var kits = MeasureKits(minutes);
            // What a founding scripted team has had come in by then, for scale (mean over the matrix's war games, when it was played).
            double Founders(double minute)
            {
                var teams = games.Where(g => g.Mode == "war").SelectMany(g => g.Teams).ToList();
                var at = teams.Select(t => t.Rows.FirstOrDefault(r => r.Min == (int)minute)).Where(r => r != null && minute >= 1).Select(r => 650 + r.Mined + r.Derrick + r.Trickle).ToList();
                return at.Count == 0 ? double.NaN : at.Average();
            }
            Table(sb, new[] { "arena age (min)", "steel", "copper", "circuits", "iron_ore", "copper_ore", "power plant", "refinery", "trucks", "value (ore)", "a founding AI team has had in all by then (650 start + mined + derricks + trickle)" },
                kits.Select(k => new[] { F(k.Minute), F(k.Stock["steel"], "0"), F(k.Stock["copper"], "0"), F(k.Stock["circuits"], "0"), F(k.Stock["iron_ore"], "0"), F(k.Stock["copper_ore"], "0"),
                    k.Plants.ToString(), k.Refineries.ToString(), k.Trucks.ToString(), F(k.Value, "0"), double.IsNaN(Founders(k.Minute)) ? "-" : F(Founders(k.Minute), "0") }));

            Sub(sb, "The kit in use");
            sb.AppendLine("The house AI (builds and defends) joins an otherwise idle arena of each age; what it stands and has mined five minutes later, when newcomer protection ends (mean of seeds 1-3). The first row is a founding team with no kit, for scale. `GiveCatchUp` says the kit is \"enough to reach barracks, factory and defenses inside the protection window\".");
            sb.AppendLine();
            var use = new List<string[]>();
            foreach (double age in new[] { 0.0, 3, 10, 30 })
            {
                var runs = Enumerable.Range(1, 3).Select(seed => KitInUse(age, seed)).ToList();
                double M(string key) => runs.Average(r => r.TryGetValue(key, out var v) ? v : 0);
                use.Add(new[] { age == 0 ? "founder (no kit)" : $"joined at {F(age)} min", F(M("power_plant"), "0.#"), F(M("mining_refinery"), "0.#"), F(M("barracks"), "0.#"), F(M("factory"), "0.#"), F(M("gun_turret"), "0.#"), F(M("electronics_plant"), "0.#"), F(M("trucks"), "0.#"), F(M("units"), "0.#"), F(M("mined"), "0") });
            }
            Table(sb, new[] { "team", "power plants", "refineries", "barracks", "factories", "gun turrets", "electronics plants", "trucks", "units", "ore mined in the 5 minutes" }, use);
        }

        /// <summary>A house AI that joins (or, at age 0, founds) a 2-team 80x80 arena `age` minutes old, and what it has five minutes on: structures by key, trucks, other units, ore mined.</summary>
        static Dictionary<string, double> KitInUse(double age, int seed)
        {
            var w = new World(2, seed, 80) { Open = true, MaxPlayers = 8 };
            w.SuddenDeathAt = 4 * 3600f;
            Team t;
            if (age == 0) t = w.Teams[0];
            else
            {
                w.Tick = (int)(age * 60 * World.TickRate);
                t = w.AddTeam("ai", "Late", out _);
            }
            t.Controller = "ai";
            var ai = new SimpleAI(t.Id, true);
            for (int i = 0; i < 300 * World.TickRate; i++) { ai.Update(w); w.Step(); }
            var r = new Dictionary<string, double>();
            foreach (var e in w.Owned(t.Id).Where(e => e.IsStructure && e.IsComplete)) r[e.Def.Key] = (r.TryGetValue(e.Def.Key, out var n) ? n : 0) + 1;
            r["trucks"] = w.Owned(t.Id).Count(e => e.IsHarvester);
            r["units"] = w.Owned(t.Id).Count(e => !e.IsStructure && !e.IsMine && !e.IsHarvester);
            r["mined"] = t.Stats.OreMined;
            return r;
        }

        // ------------------------------------------------------------------ the matrix

        /// <summary>One minute of one team's game.</summary>
        public sealed class MinuteRow
        {
            public int Min;
            public int Mined, Deep, Salvage;          // cumulative: ore delivered by trucks (salvage included), of it deep mines', of it salvage
            public double Derrick, Trickle, Upkeep, LowPowerS, Spent; // cumulative: steel from derricks, iron_ore from command centers, steel paid in upkeep, seconds in low power, ore value of what it built
            public int Trucks, DeepMines, DerricksHeld, Refineries, PowerProduced, PowerUsed, Units, Structures, Kills;
            public double HomeOre;                    // iron and copper still in its home fields (World.HomeOreRadius)
            public double[] Stock;                    // by Defs.Items
            public double Surface => Mined - Deep - Salvage;
        }

        public sealed class TeamRun
        {
            public int Id;
            public bool Defeated;
            public double AliveS, LowPowerS, LowPowerIronShortS;
            public double[] StockSum = new double[10], StockSumLate = new double[10], StockPeak = new double[10];
            public double Samples, SamplesLate;
            public double[] FinalStock = new double[10];
            public int Mined, Deep, SalvageMined, Kills, MaxDeepMines;
            public double DerrickSteel, Upkeep, Trickle;
            public double DeepOnItsSide;                // deep ore in the deposits nearer to its base than to the other's, at the start
            public double HomeOreStart;
            public List<MinuteRow> Rows = new List<MinuteRow>();
        }

        public sealed class GameRun
        {
            public int Seed, Size; public float Ore; public string Mode;
            public bool GameOver; public int Winner = -1;
            public double EndMinute;
            public int Errors; public string LastError;
            public double SurfaceStart, DeepStart;
            public double DeepLeft;
            public double[] DeepStartByType = new double[4], DeepLeftByType = new double[4];
            public double[] SurfaceByMinute, RegrownByMinute;   // world totals after each minute
            public double FarOreEnd, SurfaceEnd;                 // at the end: surface field ore (salvage excluded) farther than LeashDist from every drop-off, and all of it
            public double WallSeconds;
            public List<TeamRun> Teams = new List<TeamRun>();
            public string Group => $"{Mode} {Size}x{Size} ore {F(Ore, "0.0")}";
        }

        static List<GameRun> PlayMatrix(Options o, Action<string> progress)
        {
            var jobs = new List<(string mode, int size, float ore, int seed)>();
            foreach (var mode in o.Modes) foreach (int size in o.Sizes) foreach (float ore in o.Ores) foreach (int seed in o.Seeds) jobs.Add((mode, size, ore, seed));
            var results = new ConcurrentBag<GameRun>();
            int done = 0;
            progress($"matrix: {jobs.Count} games of up to {o.Minutes} game-minutes, {o.Jobs} at a time...");
            // One game at a time per worker, taken as workers free up (the default chunking leaves the last workers with the long games).
            Parallel.ForEach(Partitioner.Create(jobs, EnumerablePartitionerOptions.NoBuffering), new ParallelOptions { MaxDegreeOfParallelism = o.Jobs }, j =>
            {
                results.Add(PlayGame(j.seed, j.size, j.ore, j.mode == "passive", o.Minutes));
                int n = System.Threading.Interlocked.Increment(ref done);
                if (n % 8 == 0 || n == jobs.Count) progress($"  {n}/{jobs.Count} games played");
            });
            return results.OrderBy(g => g.Mode).ThenBy(g => g.Size).ThenByDescending(g => g.Ore).ThenBy(g => g.Seed).ToList();
        }

        /// <summary>Scripted AI against scripted AI (seats 0 and 1) for up to `minutes` game-minutes, sampled every game-second and written down every game-minute.</summary>
        public static GameRun PlayGame(int seed, int size, float ore, bool passive, int minutes)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = new GameRun { Seed = seed, Size = size, Ore = ore, Mode = passive ? "passive" : "war" };
            var w = new World(2, seed, size, ore);
            w.SuddenDeathAt = 4 * 3600f; // as Game sets it (the match clock's default four hours)
            w.ErrorLog = _ => { };
            var ais = new List<SimpleAI>();
            for (int i = 0; i < 2; i++) { w.Teams[i].Controller = "ai"; w.Teams[i].PlayerName = "Scripted AI"; ais.Add(new SimpleAI(i, passive)); }
            var m = w.Map;
            g.SurfaceStart = m.Ore.Sum(x => (double)x);
            g.DeepStart = m.Deep.Sum(d => (double)d.Initial);
            foreach (var d in m.Deep) g.DeepStartByType[d.Type] += d.Initial;
            var items = Defs.Items.ToArray();
            for (int i = 0; i < 2; i++)
            {
                var t = w.Teams[i];
                var tr = new TeamRun { Id = i };
                int other = 1 - i;
                tr.DeepOnItsSide = m.Deep.Where(d => Vec2.Dist(d.Pos, t.StartPos) < Vec2.Dist(d.Pos, w.Teams[other].StartPos)).Sum(d => (double)d.Initial);
                tr.HomeOreStart = HomeOre(w, t);
                g.Teams.Add(tr);
            }
            g.SurfaceByMinute = new double[minutes + 1]; g.RegrownByMinute = new double[minutes + 1];
            int ticksPerMin = 60 * World.TickRate, maxTicks = minutes * ticksPerMin;
            double built = 0;
            while (w.Tick < maxTicks && !w.GameOver)
            {
                foreach (var ai in ais) ai.Update(w);
                w.Step();
                bool second = w.Tick % World.TickRate == 0;
                for (int i = 0; i < 2; i++)
                {
                    var t = w.Teams[i];
                    if (t.Defeated) continue;
                    var tr = g.Teams[i];
                    tr.AliveS += World.Dt;
                    if (t.LowPower) { tr.LowPowerS += World.Dt; if (t.Amount("iron_ore") < 250) tr.LowPowerIronShortS += World.Dt; }
                    if (!second) continue;
                    var own = w.Owned(i).ToList();
                    int ccs = own.Count(e => e.IsStructure && e.IsComplete && e.Def.Key == "command_center");
                    tr.Trickle += ccs * (t.LowPower ? 0.5 : 1.0);
                    for (int k = 0; k < items.Length; k++)
                    {
                        double v = t.Stock.TryGetValue(items[k], out var sv) ? sv : 0;
                        tr.StockSum[k] += v; tr.StockPeak[k] = Math.Max(tr.StockPeak[k], v);
                        if (w.Time >= 20 * 60) tr.StockSumLate[k] += v;
                    }
                    tr.Samples++;
                    if (w.Time >= 20 * 60) tr.SamplesLate++;
                    tr.MaxDeepMines = Math.Max(tr.MaxDeepMines, own.Count(e => e.Def.Key == "deep_mine"));
                }
                if (w.Tick % ticksPerMin != 0) continue;
                int min = w.Tick / ticksPerMin;
                g.SurfaceByMinute[min] = w.Map.Ore.Sum(x => (double)x);
                g.RegrownByMinute[min] = w.Regrown;
                for (int i = 0; i < 2; i++)
                {
                    var t = w.Teams[i]; var s = t.Stats; var tr = g.Teams[i];
                    if (t.Defeated && tr.Rows.Count > 0 && tr.Rows[tr.Rows.Count - 1].Min < min - 1) continue;
                    var own = w.Owned(i).ToList();
                    int K(string key) => own.Count(e => e.Def.Key == key && (!e.IsStructure || e.IsComplete));
                    built = s.Built.Sum(kv => { var d = Defs.Get(kv.Key); return d == null ? 0 : OreValue(d.Cost) * kv.Value; });
                    tr.Rows.Add(new MinuteRow
                    {
                        Min = min, Mined = s.OreMined, Deep = s.DeepMined, Salvage = s.SalvageMined, Derrick = s.DerrickSteel, Trickle = tr.Trickle, Upkeep = s.UpkeepPaid, LowPowerS = tr.LowPowerS, Spent = built,
                        Trucks = own.Count(e => e.IsHarvester), DeepMines = K("deep_mine"), DerricksHeld = K("derrick"), Refineries = K("mining_refinery"),
                        PowerProduced = t.PowerProduced, PowerUsed = t.PowerUsed, Units = own.Count(e => !e.IsStructure && !e.IsMine), Structures = own.Count(e => e.IsStructure), Kills = s.Kills,
                        HomeOre = HomeOre(w, t), Stock = items.Select(k => (double)(t.Stock.TryGetValue(k, out var v) ? v : 0)).ToArray(),
                    });
                }
            }
            g.GameOver = w.GameOver; g.Winner = w.GameOver ? w.Winner : -1;
            g.EndMinute = w.Time / 60.0;
            g.Errors = w.Errors; g.LastError = w.LastError;
            g.DeepLeft = m.Deep.Sum(d => (double)d.Amount);
            foreach (var d in m.Deep) g.DeepLeftByType[d.Type] += d.Amount;
            // Surface ore that no truck could be sent for by itself: farther than the leash from every drop-off.
            var drops = w.Entities.Where(e => !e.Dead && e.IsStructure && e.IsComplete && e.Def.DropOff).Select(e => e.Center).ToList();
            for (int i = 0; i < m.Ore.Length; i++)
            {
                int field = m.FieldOre(i);
                if (field <= 0) continue;
                g.SurfaceEnd += field;
                var p = new Vec2(i % m.W + .5f, i / m.W + .5f);
                if (!drops.Any(d => Vec2.Dist(d, p) <= World.LeashDist)) g.FarOreEnd += field;
            }
            for (int i = 0; i < 2; i++)
            {
                var t = w.Teams[i]; var tr = g.Teams[i]; var s = t.Stats;
                tr.Defeated = t.Defeated;
                tr.Mined = s.OreMined; tr.Deep = s.DeepMined; tr.SalvageMined = s.SalvageMined; tr.Kills = s.Kills; tr.DerrickSteel = s.DerrickSteel; tr.Upkeep = s.UpkeepPaid;
                for (int k = 0; k < items.Length; k++) tr.FinalStock[k] = t.Stock.TryGetValue(items[k], out var v) ? v : 0;
            }
            g.WallSeconds = sw.Elapsed.TotalSeconds;
            return g;
        }

        /// <summary>Iron and copper still in a team's home fields: the field ore (not salvage) of every iron or copper field tile within HomeOreRadius of its start.</summary>
        static double HomeOre(World w, Team t)
        {
            var m = w.Map;
            int cx = (int)t.StartPos.X, cy = (int)t.StartPos.Y, r = (int)World.HomeOreRadius + 1;
            double sum = 0;
            for (int y = Math.Max(0, cy - r); y <= Math.Min(m.H - 1, cy + r); y++)
                for (int x = Math.Max(0, cx - r); x <= Math.Min(m.W - 1, cx + r); x++)
                {
                    int i = m.Idx(x, y);
                    if (m.OreBase[i] <= 0 || m.OreBaseType[i] > Map.Copper || m.OreType[i] != m.OreBaseType[i]) continue;
                    if (Vec2.Dist(new Vec2(x + .5f, y + .5f), t.StartPos) <= World.HomeOreRadius) sum += m.FieldOre(i);
                }
            return sum;
        }

        // ------------------------------------------------------------------ the matrix report

        static readonly (int from, int to)[] Phases = { (0, 5), (5, 10), (10, 20), (20, 30), (30, 45) };

        /// <summary>Ore a second of each income source over a team's minutes from..to (only the minutes it was in the game): surface by truck, salvage, deep, derricks, command-center trickle.</summary>
        static double[] Income(TeamRun t, int from, int to)
        {
            var rows = t.Rows.Where(r => r.Min > from && r.Min <= to).ToList();
            if (rows.Count == 0) return null;
            MinuteRow Before(MinuteRow r) => t.Rows.FirstOrDefault(x => x.Min == r.Min - 1);
            double surface = 0, salvage = 0, deep = 0, derrick = 0, trickle = 0; int seconds = 0;
            foreach (var r in rows)
            {
                var b = Before(r);
                surface += r.Surface - (b?.Surface ?? 0); salvage += r.Salvage - (b?.Salvage ?? 0); deep += r.Deep - (b?.Deep ?? 0);
                derrick += r.Derrick - (b?.Derrick ?? 0); trickle += r.Trickle - (b?.Trickle ?? 0);
                seconds += 60;
            }
            return new[] { surface / seconds, salvage / seconds, deep / seconds, derrick / seconds, trickle / seconds };
        }

        static void MatrixSection(StringBuilder sb, List<GameRun> games, Options o)
        {
            Heading(sb, $"Scripted AI against scripted AI: {games.Count} games");
            sb.AppendLine($"Seeds {string.Join(",", o.Seeds.Take(3))}{(o.Seeds.Length > 3 ? "..." + o.Seeds.Last() : "")}, sizes {string.Join("/", o.Sizes)}, richness {string.Join("/", o.Ores.Select(x => F(x, "0.0")))}, modes {string.Join("/", o.Modes)}, up to {o.Minutes} game-minutes each. \"war\" is the scripted AI (attacks in waves), \"passive\" the house AI (builds and defends).");
            sb.AppendLine($"Sim errors in these games: {games.Sum(g => g.Errors)}{(games.Any(g => g.Errors > 0) ? " (" + string.Join("; ", games.Where(g => g.Errors > 0).Take(3).Select(g => $"{g.Group} seed {g.Seed}: {g.LastError}")) + ")" : "")}. Wall time {F(games.Sum(g => g.WallSeconds) / 60, "0.0")} min of CPU.");
            sb.AppendLine();
            var groups = games.GroupBy(g => g.Group).ToList();

            Sub(sb, "How the games ended, and the seat win split");
            sb.AppendLine("Decisive = one team knocked out inside the time limit. Ore@3m is `Stats.OreMined` at 3:00, seat 0 / seat 1; \"ahead\" counts the games each seat had mined more by then.");
            sb.AppendLine();
            string[] SplitRow(string label, List<GameRun> gs)
            {
                int w0 = gs.Count(g => g.GameOver && g.Winner == 0), w1 = gs.Count(g => g.GameOver && g.Winner == 1), undecided = gs.Count - w0 - w1;
                var at3 = gs.Where(g => g.Teams.All(t => t.Rows.Any(r => r.Min == 3))).ToList();
                double a0 = Mean(at3.Select(g => (double)g.Teams[0].Rows.First(r => r.Min == 3).Mined)), a1 = Mean(at3.Select(g => (double)g.Teams[1].Rows.First(r => r.Min == 3).Mined));
                int ahead0 = at3.Count(g => g.Teams[0].Rows.First(r => r.Min == 3).Mined > g.Teams[1].Rows.First(r => r.Min == 3).Mined), ahead1 = at3.Count(g => g.Teams[1].Rows.First(r => r.Min == 3).Mined > g.Teams[0].Rows.First(r => r.Min == 3).Mined);
                return new[] { label, gs.Count.ToString(), $"{w0} / {w1} / {undecided}", w0 + w1 == 0 ? "-" : F(BinomialP(w0, w0 + w1), "0.000"), F(Mean(gs.Select(g => g.EndMinute)), "0.0"), at3.Count == 0 ? "-" : $"{a0:0} / {a1:0}", at3.Count == 0 ? "-" : $"{ahead0} / {ahead1}" };
            }
            var split = new List<string[]>();
            foreach (var gr in groups) split.Add(SplitRow(gr.Key, gr.ToList()));
            var war = games.Where(g => g.Mode == "war").ToList();
            if (war.Count > 0) split.Add(SplitRow("**all war games**", war));
            var passive = games.Where(g => g.Mode == "passive").ToList();
            if (passive.Count > 0) split.Add(SplitRow("**all passive games**", passive));
            Table(sb, new[] { "group", "games", "wins seat 0 / seat 1 / undecided", "p (two-sided, even odds)", "mean minutes played", "ore@3m seat 0 / seat 1", "ahead at 3m seat 0 / seat 1" }, split);

            Sub(sb, "Income by source (ore a second per team, mean over the minutes a team was in the game)");
            sb.AppendLine("Each cell is `surface by truck (salvage) / deep mines / derricks (steel) / command-center trickle = total`.");
            sb.AppendLine();
            var inc = new List<string[]>();
            foreach (var gr in groups)
            {
                var cells = new List<string> { gr.Key };
                foreach (var (from, to) in Phases)
                {
                    var parts = gr.SelectMany(g => g.Teams).Select(t => Income(t, from, to)).Where(x => x != null).ToList();
                    if (parts.Count == 0) { cells.Add("-"); continue; }
                    double s = parts.Average(p => p[0]), sv = parts.Average(p => p[1]), d = parts.Average(p => p[2]), dr = parts.Average(p => p[3]), tr = parts.Average(p => p[4]);
                    cells.Add($"{s:0.0} ({sv:0.0}) / {d:0.0} / {dr:0.0} / {tr:0.0} = **{s + d + dr + tr:0.0}**");
                }
                inc.Add(cells.ToArray());
            }
            Table(sb, new[] { "group" }.Concat(Phases.Select(p => $"min {p.from}-{p.to}")).ToArray(), inc);

            Sub(sb, "Truck deliveries from the surface, by minute (ore a second, mean over team-minutes)");
            var minutesShown = new[] { 1, 2, 3, 5, 8, 10, 15, 20, 25, 30, 35, 40, 45 }.Where(m => m <= o.Minutes).ToArray();
            var byMin = new List<string[]>();
            foreach (var gr in groups)
            {
                var cells = new List<string> { gr.Key };
                foreach (int m in minutesShown)
                {
                    var xs = gr.SelectMany(g => g.Teams).Select(t => { var r = t.Rows.FirstOrDefault(x => x.Min == m); var b = t.Rows.FirstOrDefault(x => x.Min == m - 1); return r == null ? (double?)null : (r.Surface - (b?.Surface ?? 0)) / 60.0; }).Where(x => x.HasValue).Select(x => x.Value).ToList();
                    cells.Add(xs.Count == 0 ? "-" : F(xs.Average(), "0.0"));
                }
                byMin.Add(cells.ToArray());
            }
            Table(sb, new[] { "group" }.Concat(minutesShown.Select(m => "m" + m)).ToArray(), byMin);

            Sub(sb, "Low power");
            sb.AppendLine("Share of the time a team was in the game that its power used exceeded its power made (production and refining run at half).");
            sb.AppendLine();
            var lp = new List<string[]>();
            foreach (var gr in groups.Concat(new[] { (IGrouping<string, GameRun>)null }))
            {
                var teams = gr == null ? games.SelectMany(g => g.Teams).ToList() : gr.SelectMany(g => g.Teams).ToList();
                var pct = teams.Where(t => t.AliveS > 0).Select(t => 100 * t.LowPowerS / t.AliveS).ToList();
                double lowTotal = teams.Sum(t => t.LowPowerS), ironShort = teams.Sum(t => t.LowPowerIronShortS);
                lp.Add(new[] { gr == null ? "**all**" : gr.Key, teams.Count.ToString(), F(Mean(pct), "0.0") + "%", F(Median(pct), "0.0") + "%", F(Percentile(pct, 0.9), "0") + "%", F(pct.Count == 0 ? 0 : pct.Max(), "0") + "%", pct.Count(p => p > 20).ToString(), lowTotal <= 0 ? "-" : F(100 * ironShort / lowTotal, "0") + "%" });
            }
            Table(sb, new[] { "group", "teams", "mean", "median", "p90", "max", "teams over 20%", "of low-power time with iron_ore < 250" }, lp);

            Sub(sb, "Unspent stock (mean over each team's life, from minute 20 on in brackets; the largest stockpile any team reached in the last column)");
            var items = Defs.Items.ToArray();
            var stock = new List<string[]>();
            foreach (var gr in groups.Concat(new[] { (IGrouping<string, GameRun>)null }))
            {
                var teams = (gr == null ? games.SelectMany(g => g.Teams) : gr.SelectMany(g => g.Teams)).Where(t => t.Samples > 0).ToList();
                var cells = new List<string> { gr == null ? "**all**" : gr.Key };
                for (int k = 0; k < items.Length; k++)
                {
                    double life = teams.Average(t => t.StockSum[k] / t.Samples);
                    var late = teams.Where(t => t.SamplesLate > 0).ToList();
                    cells.Add(late.Count == 0 ? $"{life:0}" : $"{life:0} ({late.Average(t => t.StockSumLate[k] / t.SamplesLate):0})");
                }
                cells.Add(string.Join(" ", items.Select((it, k) => (it, peak: teams.Max(t => t.StockPeak[k]))).OrderByDescending(x => x.peak).Take(3).Select(x => $"{x.it} {x.peak:0}")));
                stock.Add(cells.ToArray());
            }
            Table(sb, new[] { "group" }.Concat(items).Concat(new[] { "biggest peaks" }).ToArray(), stock);

            Sub(sb, "Deep ore used");
            sb.AppendLine("Deep ore pumped out of the ground against what the map held. \"Used\" counts everything pumped; \"on its side\" is the share of a team's pumping that came from deposits nearer its base than the other's.");
            sb.AppendLine();
            var deep = new List<string[]>();
            foreach (var gr in groups)
            {
                double start = gr.Sum(g => g.DeepStart), left = gr.Sum(g => g.DeepLeft);
                var typeUse = Enumerable.Range(0, 4).Select(k => { double s = gr.Sum(g => g.DeepStartByType[k]); return s <= 0 ? "-" : F(100 * (s - gr.Sum(g => g.DeepLeftByType[k])) / s, "0") + "%"; });
                var teams = gr.SelectMany(g => g.Teams).ToList();
                deep.Add(new[] { gr.Key, F(start / gr.Count() / 1000, "0") + "k", F(100 * (start - left) / start, "0.0") + "%", string.Join(" / ", typeUse),
                    F(Mean(teams.Select(t => (double)t.MaxDeepMines)), "0.0"), teams.Count(t => t.MaxDeepMines > 0) + "/" + teams.Count, F(Mean(teams.Select(t => (double)t.Deep)), "0") });
            }
            Table(sb, new[] { "group", "deep ore per map", "of it pumped", "pumped by ore: iron / copper / crystal / uranium", "most deep mines at once (mean)", "teams that built one", "ore pumped per team (mean)" }, deep);

            Sub(sb, "Surface ore: how fast it goes, and how much is out of reach");
            sb.AppendLine("Surface ore left on the whole map as a share of what it started with, the home fields (iron + copper within 20 tiles of a base) used up by minute, and, in the games nobody won, the field ore left at the end lying farther than the 35-tile leash from every drop-off (what a truck won't fetch on its own).");
            sb.AppendLine();
            var surf = new List<string[]>();
            foreach (var gr in groups)
            {
                string Left(int m) { var xs = gr.Where(g => g.SurfaceByMinute.Length > m && g.SurfaceByMinute[m] > 0).Select(g => 100 * g.SurfaceByMinute[m] / g.SurfaceStart).ToList(); return xs.Count == 0 ? "-" : F(xs.Average(), "0") + "%"; }
                var home = gr.SelectMany(g => g.Teams).Select(t => { var r = t.Rows.FirstOrDefault(x => x.HomeOre < 0.1 * t.HomeOreStart); return r == null ? (double?)null : r.Min; }).ToList();
                var reg = gr.Where(g => (int)g.EndMinute > 30).Select(g => (g.RegrownByMinute[Math.Min(g.RegrownByMinute.Length - 1, (int)g.EndMinute)] - g.RegrownByMinute[30]) / (((int)g.EndMinute - 30) * 60.0)).ToList();
                surf.Add(new[] { gr.Key, F(gr.Average(g => g.SurfaceStart) / 1000, "0.0") + "k", Left(10), Left(20), Left(30), Left(45),
                    home.Count(x => x.HasValue) == 0 ? "never" : $"{Median(home.Where(x => x.HasValue).Select(x => x.Value)):0} ({home.Count(x => x.HasValue)}/{home.Count} teams)",
                    F(100 * gr.Where(g => !g.GameOver).Sum(g => g.FarOreEnd) / Math.Max(1, gr.Where(g => !g.GameOver).Sum(g => g.SurfaceEnd)), "0") + "%", reg.Count == 0 ? "-" : F(Mean(reg), "0.00") });
            }
            Table(sb, new[] { "group", "surface ore at start", "left min 10", "left min 20", "left min 30", "left min 45", "minute the home fields are 90% used (median)", "end of undecided games: field ore beyond the leash", "world regrowth ore/s, min 30-end" }, surf);

            Sub(sb, "What a founding team had built");
            var built = new List<string[]>();
            foreach (var gr in groups)
            {
                var teams = gr.SelectMany(g => g.Teams).ToList();
                string At(int m, Func<MinuteRow, double> f) { var xs = teams.Select(t => t.Rows.FirstOrDefault(r => r.Min == m)).Where(r => r != null).Select(f).ToList(); return xs.Count == 0 ? "-" : F(xs.Average(), "0"); }
                built.Add(new[] { gr.Key, At(10, r => r.Spent), At(20, r => r.Spent), At(30, r => r.Spent), At(30, r => r.Trucks), At(30, r => r.Refineries), At(30, r => r.DeepMines), At(30, r => r.Units), At(30, r => r.Structures) });
            }
            Table(sb, new[] { "group", "built by min 10 (ore value)", "by min 20", "by min 30", "trucks at 30", "refineries at 30", "deep mines at 30", "units at 30", "structures at 30" }, built);
        }

        static void WriteCsv(string dir, List<GameRun> games)
        {
            var g = new StringBuilder("mode,size,ore,seed,game_over,winner,end_min,errors,surface_start,deep_start,deep_left,far_ore_end,surface_end\n");
            foreach (var x in games) g.AppendLine(string.Join(",", x.Mode, x.Size, F(x.Ore, "0.0"), x.Seed, x.GameOver ? 1 : 0, x.Winner, F(x.EndMinute, "0.0"), x.Errors, F(x.SurfaceStart, "0"), F(x.DeepStart, "0"), F(x.DeepLeft, "0"), F(x.FarOreEnd, "0"), F(x.SurfaceEnd, "0")));
            File.WriteAllText(Path.Combine(dir, "games.csv"), g.ToString());
            var items = Defs.Items.ToArray();
            var m = new StringBuilder("mode,size,ore,seed,team,min,mined,deep,salvage,derrick_steel,cc_trickle,upkeep,lowpower_s,spent,trucks,deep_mines,derricks,refineries,power_made,power_used,units,structures,kills,home_ore," + string.Join(",", items) + "\n");
            foreach (var x in games)
                foreach (var t in x.Teams)
                    foreach (var r in t.Rows)
                        m.AppendLine(string.Join(",", x.Mode, x.Size, F(x.Ore, "0.0"), x.Seed, t.Id, r.Min, r.Mined, r.Deep, r.Salvage, F(r.Derrick), F(r.Trickle), F(r.Upkeep), F(r.LowPowerS), F(r.Spent, "0"), r.Trucks, r.DeepMines, r.DerricksHeld, r.Refineries, r.PowerProduced, r.PowerUsed, r.Units, r.Structures, r.Kills, F(r.HomeOre, "0"), string.Join(",", r.Stock.Select(v => F(v, "0")))));
            File.WriteAllText(Path.Combine(dir, "minutes.csv"), m.ToString());
        }
    }
}
