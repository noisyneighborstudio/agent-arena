using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>The economy and endgame rules: salvage from kills, ore regrowth, construction trucks, the match clock, derricks, upkeep.</summary>
    public static partial class Tests
    {
        /// <summary>--test-economy: just these.</summary>
        public static int RunEconomy()
        {
            EconomyAndEndgame();
            Console.WriteLine(failures == 0 ? "\nAll tests passed." : $"\n{failures} test(s) FAILED.");
            return failures == 0 ? 0 : 1;
        }

        static void EconomyAndEndgame()
        {
            AiEconomy();
            SalvageFromKills();
            OneValuation();
            GrantedValue();
            OreRegrowth();
            ParallelProduction();
            ConstructionTruck();
            SymmetricPlacement();
            BaysFaceTheOre();
            UnitsLeaveByTheDoor();
            BayFollowsABlockedLane();
            SeatSymmetry();
            MatchClock();
            Derricks();
            SellRules();
            AttackUntilCapturable();
            AttackMoveDerricks();
            DefenceUpkeep();
            FusionReactor();
            DeepDepositKits();
            DeepKitForJoiners();
            SurfaceOreScalesWithArea();
        }

        /// <summary>Share of the map's open ground (sampled every 4th tile) that is more than `far` tiles from any surface ore.</summary>
        static double FarFromOre(Map m, float far)
        {
            var ore = new List<Vec2>();
            for (int i = 0; i < m.Ore.Length; i++) if (m.Ore[i] > 0) ore.Add(new Vec2(i % m.W + 0.5f, i / m.W + 0.5f));
            int open = 0, gap = 0;
            for (int y = 0; y < m.H; y += 4)
                for (int x = 0; x < m.W; x += 4)
                {
                    if (m.Tiles[m.Idx(x, y)] > Terrain.Dirt) continue;
                    open++;
                    var p = new Vec2(x + 0.5f, y + 0.5f);
                    if (!ore.Any(o => Vec2.Dist(o, p) <= far)) gap++;
                }
            return open == 0 ? 0 : (double)gap / open;
        }

        /// <summary>
        /// Surface ore grows with the map's area at a fixed richness, but only by a modest share on top of the corner fields (the
        /// ore per base stays near an 80x80 map's), in sets turned round the centre that fill the gaps; joins add a base's worth
        /// plus a little.
        /// </summary>
        static void SurfaceOreScalesWithArea()
        {
            long Ore(Map m) => m.Ore.Sum(o => (long)o);
            foreach (float richness in new[] { 1f, 0.3f })
            {
                var seeds = new[] { 1, 2, 3 };
                var sizes = new[] { 80, 96, 128, 160, 240, 320 };
                var ore = sizes.Select(size => seeds.Average(seed => (double)Ore(Map.Generate(size, size, seed, richness)))).ToList();
                bool grows = ore[1] > ore[0] * 1.05 && ore[2] > ore[1] * 1.05;
                bool modest = ore.All(o => o < ore[0] * 1.6) && ore.Skip(2).All(o => o > ore[0] * 1.15);
                Check(grows && modest, $"at richness {richness} surface ore grows with map area, by a modest share of the corner fields' (80 to 320 tiles: {string.Join(", ", ore.Select(o => $"{o / 1000:0.0}k"))})");
            }
            double gapWith = new[] { 1, 2, 3 }.Average(seed => FarFromOre(Map.Generate(160, 160, seed), 35f)), gapWithout = new[] { 1, 2, 3 }.Average(seed => FarFromOre(Map.Generate(160, 160, seed, 1f, false), 35f));
            Check(gapWith < 0.05 && gapWithout > 2 * gapWith, $"the extra fields fill the far gaps: on a 160x160 map {gapWithout:P0} of the open ground is more than 35 tiles from any ore without them, {gapWith:P0} with them");

            // The extra fields (the map with them, less the map without) are the same in every quarter of the map, and keep off the base sites.
            var uneven = new List<string>(); var close = new List<string>(); int maps = 0, tilesTurned = 0;
            foreach (int size in new[] { 128, 160, 240 })
                foreach (int seed in Enumerable.Range(1, 20).Append(99))
                {
                    var with = Map.Generate(size, size, seed); var without = Map.Generate(size, size, seed, 1f, false);
                    var quarter = new long[4]; bool turned = true;
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            int extra = with.Ore[with.Idx(x, y)] - without.Ore[without.Idx(x, y)];
                            // The tile a quarter turn about the map's centre carries this one to has exactly the same extra ore.
                            if (extra != with.Ore[with.Idx(size - 1 - y, x)] - without.Ore[without.Idx(size - 1 - y, x)]) turned = false;
                            if (extra <= 0) continue;
                            tilesTurned++;
                            quarter[(x < size / 2 ? 0 : 1) + (y < size / 2 ? 0 : 2)] += extra;
                            if (with.Spawns.Any(s => Vec2.Dist(s, new Vec2(x + 0.5f, y + 0.5f)) < 12f)) close.Add($"{seed}/{size}");
                        }
                    if (!turned || quarter[0] == 0 || quarter.Distinct().Count() > 1) uneven.Add($"{seed}/{size}: {string.Join(" ", quarter)}");
                    maps++;
                }
            Check(uneven.Count == 0 && maps == 63 && tilesTurned > 0, $"the extra surface fields are exactly alike in all four quarters of the map, tile for tile when turned (63 maps: seeds 1-20 and 99 at 128, 160 and 240; {tilesTurned} ore tiles)" + (uneven.Count > 0 ? $"; uneven: {string.Join("; ", uneven)}" : ""));
            Check(close.Count == 0, "and none lies on a base site");
            Check(Ore(Map.Generate(160, 160, 1, 1f, false)) < 70000, "a map built without them (a game saved before they existed) has only the fixed fields");

            // A join adds the new base's own fields and a few neutral ones: about one base's worth of ore each time, however wide the strip.
            var added = new List<double>();
            foreach (int seed in new[] { 3, 7, 1337 })
            {
                var w = new World(2, seed, 80) { Open = true };
                for (int j = 0; j < 6; j++)
                {
                    long before = Ore(w.Map);
                    if (w.AddTeam("llm", "Joiner" + j, out _) == null) break;
                    added.Add(Ore(w.Map) - before);
                }
            }
            Check(added.Count == 18 && added.All(a => a > 12000 && a < 40000), $"a join adds about a base's worth of surface ore, one strip or a wide one (18 joins on 3 maps, richness 1: {added.Min() / 1000:0.0}k to {added.Max() / 1000:0.0}k, {added.Average() / 1000:0.0}k on average)");
        }

        /// <summary>A base site's deep deposits within `within` tiles as a string: ore, starting amount and distance, sorted.</summary>
        static string DeepSignature(Map m, Vec2 site, float within) =>
            string.Join(" ", m.Deep.Where(d => Vec2.Dist(d.Pos, site) <= within).Select(d => $"{Defs.Ores[d.Type]}:{d.Initial:0}@{Vec2.Dist(d.Pos, site):0.0}").OrderBy(x => x, StringComparer.Ordinal));

        /// <summary>The deposit of this ore nearest a site.</summary>
        static DeepDeposit NearestDeep(Map m, Vec2 site, int ore) => m.Deep.Where(d => d.Type == ore).OrderBy(d => Vec2.Dist(d.Pos, site)).FirstOrDefault();

        /// <summary>Deep deposits on a new map are the same from every base site: a kit each, the rest in the middle, every ore there.</summary>
        static void DeepDepositKits()
        {
            var sizes = new[] { 56, 80, 96, 112, 160 }; var seeds = new[] { 1, 7, 99, 1337, 4242 };
            var uneven = new List<string>(); var noKit = new List<string>(); var noOre = new List<string>(); var badAmount = new List<string>(); var noRoom = new List<string>(); var crowded = new List<string>();
            int maps = 0;
            foreach (int size in sizes)
                foreach (int seed in seeds)
                {
                    var m = Map.Generate(size, size, seed); maps++;
                    string tag = $"{seed}/{size}";
                    var sites = m.Spawns.Take(4).ToList();
                    // Same ore, amount and distance at all four corners, near and far.
                    foreach (float within in new[] { Map.DeepKitReach, 30f, 40f })
                        if (sites.Select(s => DeepSignature(m, s, within)).Distinct().Count() > 1) uneven.Add($"{tag} within {within}");
                    // Each base has a deep iron and a deep copper deposit of its own in reach (and, where the bases are far enough apart
                    // not to share reach, exactly one of each).
                    if (!sites.All(s => Vec2.Dist(NearestDeep(m, s, Map.Iron).Pos, s) <= Map.DeepKitReach && Vec2.Dist(NearestDeep(m, s, Map.Copper).Pos, s) <= Map.DeepKitReach &&
                                        (size < 80 || (m.Deep.Count(d => d.Type == Map.Iron && Vec2.Dist(d.Pos, s) <= Map.DeepKitReach) == 1 && m.Deep.Count(d => d.Type == Map.Copper && Vec2.Dist(d.Pos, s) <= Map.DeepKitReach) == 1)))) noKit.Add(tag);
                    // Crystal and uranium are in the middle, not in a base's kit area (on maps big enough to have the room).
                    if (size >= 80 && m.Deep.Any(d => d.Type >= Map.Crystal && sites.Any(s => Vec2.Dist(d.Pos, s) <= Map.DeepKitReach))) crowded.Add(tag);
                    // Every ore exists deep, in the usual amounts.
                    if (Enumerable.Range(0, 4).Any(t => m.Deep.All(d => d.Type != t))) noOre.Add(tag);
                    if (m.Deep.Any(d => d.Type <= Map.Copper ? d.Initial < 4000 || d.Initial >= 8000 : d.Type == Map.Crystal ? d.Initial < 2000 || d.Initial >= 4000 : d.Initial < 1500 || d.Initial >= 3000)) badAmount.Add(tag);
                    // Open ground a deep mine can stand on, and a way there from the sites.
                    var reach = new bool[m.W * m.H]; var q = new Queue<Int2>(); var start = Int2.Of(m.Spawns[0]);
                    reach[m.Idx(start.X, start.Y)] = true; q.Enqueue(start);
                    while (q.Count > 0)
                    {
                        var c = q.Dequeue();
                        foreach (var n in new[] { new Int2(c.X + 1, c.Y), new Int2(c.X - 1, c.Y), new Int2(c.X, c.Y + 1), new Int2(c.X, c.Y - 1) })
                            if (m.TerrainPassable(n.X, n.Y) && !reach[m.Idx(n.X, n.Y)]) { reach[m.Idx(n.X, n.Y)] = true; q.Enqueue(n); }
                    }
                    if (!m.Deep.All(d => m.Passable((int)d.Pos.X, (int)d.Pos.Y) && reach[m.Idx((int)d.Pos.X, (int)d.Pos.Y)])) noRoom.Add(tag);
                }
            string of = $"{maps} maps: seeds {string.Join(",", seeds)} at sizes {string.Join(",", sizes)}";
            Check(uneven.Count == 0, $"every base site sees the same deep deposits (ore, amount, distance) within {Map.DeepKitReach}, 30 and 40 tiles, on {of}" + (uneven.Count > 0 ? $"; uneven: {string.Join(", ", uneven)}" : ""));
            Check(noKit.Count == 0, "each base has its own deep iron and deep copper deposit in reach (exactly one of each where bases don't share reach)" + (noKit.Count > 0 ? $"; not on {string.Join(", ", noKit)}" : ""));
            Check(crowded.Count == 0, "crystal and uranium deposits keep out of the bases' kit areas (80 and up)" + (crowded.Count > 0 ? $"; inside on {string.Join(", ", crowded)}" : ""));
            Check(noOre.Count == 0, "every ore, uranium and crystal too, has a deep deposit on every map" + (noOre.Count > 0 ? $"; missing on {string.Join(", ", noOre)}" : ""));
            Check(badAmount.Count == 0, "deposit amounts stay in the old ranges (iron and copper 4000-8000, crystal 2000-4000, uranium 1500-3000)" + (badAmount.Count > 0 ? $"; out of range on {string.Join(", ", badAmount)}" : ""));
            Check(noRoom.Count == 0, "every deposit is on open, reachable ground" + (noRoom.Count > 0 ? $"; not on {string.Join(", ", noRoom)}" : ""));

            // The case the issue measured: two players at opposite corners get equal deposits on their own sides, whatever the seed.
            var lopsided = new List<string>();
            foreach (var (seed, size) in new[] { (1, 80), (99, 80), (7, 80), (7, 96), (1337, 80) })
            {
                var m = Map.Generate(size, size, seed);
                var a = m.Spawns[0]; var b = m.Spawns[1];
                var mine = m.Deep.Where(d => Vec2.Dist(d.Pos, a) < Vec2.Dist(d.Pos, b) - 0.01f).ToList();
                var theirs = m.Deep.Where(d => Vec2.Dist(d.Pos, b) < Vec2.Dist(d.Pos, a) - 0.01f).ToList();
                if (mine.Count != theirs.Count || mine.Sum(d => d.Initial) != theirs.Sum(d => d.Initial)) lopsided.Add($"{seed}/{size}: {mine.Count} deposits {mine.Sum(d => d.Initial):0} ore vs {theirs.Count} deposits {theirs.Sum(d => d.Initial):0}");
            }
            Check(lopsided.Count == 0, "two players get the same number of deposits and the same ore nearer their own base than the other's (the seeds that were lopsided before)" + (lopsided.Count > 0 ? $"; {string.Join("; ", lopsided)}" : ""));

            // New maps are still made from the seed alone, and different seeds differ.
            var one = Map.Generate(80, 80, 7); var again = Map.Generate(80, 80, 7); var other = Map.Generate(80, 80, 8);
            string Dump(Map m) => string.Join("|", m.Deep.Select(d => $"{d.Id} {d.Type} {d.Pos.X},{d.Pos.Y} {d.Initial}"));
            Check(Dump(one) == Dump(again) && Dump(one) != Dump(other), "deep deposits come from the seed: the same seed makes the same ones, another seed other ones");

            // A mine can be built on each deposit of a real game, and a saved game keeps the deposits it has (only new maps change).
            var w = new World(2, 7, 80);
            Check(w.Map.Deep.All(d => w.DeepMineSpot(d, 0, out _) != null), $"a deep mine fits on every one of a game's {w.Map.Deep.Count} deposits");
            var game = new Game(new GameConfig { Seed = 99, MapSize = 80, Controllers = new[] { "llm", "llm" } });
            string Deposits(Game g) => string.Join("|", g.World.Map.Deep.Select(d => $"{d.Id} {d.Type} {d.Pos.X},{d.Pos.Y} {d.Amount} {d.Initial} {d.MineId}"));
            game.World.Map.Deep[3].Amount -= 250; // part mined out
            var back = Resume(Snap(game));
            Check(Deposits(back) == Deposits(game) && back.World.Map.Deep.Count > 0, "a saved game resumes with exactly the deep deposits it had");
        }

        /// <summary>A base added by a join gets the same kit as the others; the strip's random extras stay in neutral ground.</summary>
        static void DeepKitForJoiners()
        {
            var bad = new List<string>(); int joins = 0;
            foreach (int seed in new[] { 3, 7, 1337 })
            {
                var w = new World(2, seed, 80) { Open = true };
                var home = w.Map.Spawns[0];
                var iron = NearestDeep(w.Map, home, Map.Iron); var copper = NearestDeep(w.Map, home, Map.Copper);
                iron.Amount = 12; // base one has mined its deep iron nearly out: a newcomer's kit is still the full one
                for (int j = 0; j < 3; j++, joins++)
                {
                    var oldDeposits = w.Map.Deep.Select(d => $"{d.Id} {d.Type} {d.Pos.X},{d.Pos.Y} {d.Initial}").ToList();
                    int lastId = w.Map.Deep.Max(d => d.Id);
                    var team = w.AddTeam("llm", $"Joiner{j}", out var err);
                    string tag = $"seed {seed} join {j + 1}";
                    if (team == null) { bad.Add($"{tag}: {err}"); continue; }
                    var site = team.StartPos;
                    var fresh = w.Map.Deep.Where(d => d.Id > lastId).ToList();
                    var kit = w.Map.Deep.Where(d => Vec2.Dist(d.Pos, site) <= Map.DeepKitReach).ToList();
                    var ni = kit.Where(d => d.Type == Map.Iron).ToList(); var nc = kit.Where(d => d.Type == Map.Copper).ToList();
                    if (kit.Count != 2 || ni.Count != 1 || nc.Count != 1) { bad.Add($"{tag}: {kit.Count} deposits in reach of the new base ({string.Join(", ", kit.Select(d => Defs.Ores[d.Type]))})"); continue; }
                    if (ni[0].Initial != iron.Initial || nc[0].Initial != copper.Initial || ni[0].Amount != ni[0].Initial) bad.Add($"{tag}: kit amounts {ni[0].Initial:0}/{nc[0].Initial:0}, wanted {iron.Initial:0}/{copper.Initial:0}");
                    if (MathF.Abs(Vec2.Dist(ni[0].Pos, site) - Vec2.Dist(iron.Pos, home)) > 1.5f || MathF.Abs(Vec2.Dist(nc[0].Pos, site) - Vec2.Dist(copper.Pos, home)) > 1.5f)
                        bad.Add($"{tag}: kit distances {Vec2.Dist(ni[0].Pos, site):0.0}/{Vec2.Dist(nc[0].Pos, site):0.0}, wanted {Vec2.Dist(iron.Pos, home):0.0}/{Vec2.Dist(copper.Pos, home):0.0}");
                    if (!kit.All(d => fresh.Contains(d))) bad.Add($"{tag}: the kit isn't new deposits");
                    // The strip's other deposits are neutral: out of every base's reach.
                    var extras = fresh.Where(d => !kit.Contains(d)).ToList();
                    if (extras.Any(d => w.Map.Spawns.Any(s => Vec2.Dist(d.Pos, s) <= Map.DeepKitReach))) bad.Add($"{tag}: a random extra inside a base's reach");
                    // Everything that was already there is untouched.
                    if (!oldDeposits.All(o => w.Map.Deep.Select(d => $"{d.Id} {d.Type} {d.Pos.X},{d.Pos.Y} {d.Initial}").Contains(o))) bad.Add($"{tag}: an old deposit changed");
                    if (!w.Map.Deep.All(d => w.DeepMineSpot(d, team.Id, out _) != null || w.Entities.Any(e => !e.Dead && e.DepositId == d.Id))) bad.Add($"{tag}: a deposit with no room for a mine");
                }
            }
            Check(bad.Count == 0, $"each of {joins} joiners gets the first base's kit (same ores and amounts at the same distances, whatever it has mined), and the strip's extras stay in neutral ground" + (bad.Count > 0 ? $"; {string.Join("; ", bad)}" : ""));

            // A game from before kits (nothing of the sort near its first base) still gives a newcomer a kit, with fresh amounts.
            var old = new World(2, 7, 80) { Open = true };
            old.Map.Deep.Clear();
            old.Map.Deep.Add(new DeepDeposit { Id = 1, Pos = new Vec2(40.5f, 40.5f), Type = Map.Uranium, Amount = 2000, Initial = 2000 });
            var late = old.AddTeam("llm", "Late", out _);
            var lateKit = old.Map.Deep.Where(d => Vec2.Dist(d.Pos, late.StartPos) <= Map.DeepKitReach).ToList();
            Check(lateKit.Count == 2 && lateKit.Any(d => d.Type == Map.Iron && d.Initial >= 4000 && d.Initial < 8000) && lateKit.Any(d => d.Type == Map.Copper && d.Initial >= 4000 && d.Initial < 8000) && old.Map.Deep.Count(d => d.Id == 1) == 1,
                  $"a map with no kits still gives a joiner one: {string.Join(", ", lateKit.Select(d => $"{Defs.Ores[d.Type]} {d.Initial:0}"))}");

            // A copy lands on a tile centre, which can be up to 0.7 tiles from where it was aimed: with the first base's copper right at the
            // edge of its reach (21.9 tiles, and the 11.5 ahead, 18.5 aside spot), no copy may end up past the reach and so out of the kit.
            var past = new List<string>(); int tried = 0;
            foreach (int seed in new[] { 3, 7, 1337 })
                foreach (var (ahead, aside) in new[] { (21.9f, 0f), (20.8f, -6.8f), (20.8f, 6.8f), (17.5f, -13.2f), (17.5f, 13.2f), (15.1f, 15.9f), (11.5f, 18.5f), (11.5f, -18.5f) })
                {
                    var w = new World(2, seed, 80) { Open = true };
                    var home = w.Map.Spawns[0];
                    var dir = (new Vec2(w.Map.W / 2f, w.Map.H / 2f) - home).Normalized; var side = new Vec2(-dir.Y, dir.X);
                    var copper = NearestDeep(w.Map, home, Map.Copper);
                    copper.Pos = home + dir * ahead + side * aside;
                    int lastId = w.Map.Deep.Max(d => d.Id);
                    var joiner = w.AddTeam("llm", "Edge", out _); tried++;
                    if (!w.Map.Deep.Any(d => d.Id > lastId && d.Type == Map.Copper && d.Initial == copper.Initial && Vec2.Dist(d.Pos, joiner.StartPos) <= Map.DeepKitReach)) past.Add($"{seed} ({ahead}, {aside})");
                }
            Check(past.Count == 0, $"a joiner's copy of a kit deposit always lands inside the kit's reach, even when the first base's is at its edge ({tried} cases)" + (past.Count > 0 ? $"; copper missing for {string.Join(", ", past)}" : ""));
        }

        /// <summary>Selling can't dodge a loss: unfinished buildings refund by health, a building under fire can't be sold, and derricks can't be sold at all.</summary>
        static void SellRules()
        {
            var w = new World(2, 7, 96);
            var t = w.Teams[1];
            foreach (var u in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(u);
            int Steel() => (int)t.Amount("steel");

            // Unfinished, healthy (the health it should have at that point of the build): the unbuilt share back, as before.
            var healthy = w.SpawnStructure(1, "factory", w.FindPlacement(1, "factory").Value, 0.5f);
            healthy.Hp = healthy.Def.MaxHp * (0.1f + 0.9f * 0.5f);
            int s0 = Steel();
            var ok = Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", healthy.Id, "confirm", true));
            Check(Ok(ok) && healthy.Dead && Steel() - s0 == 225, $"a healthy half-built factory still refunds 75% ({Steel() - s0} steel): {Said(ok)}");
            // Unfinished and almost destroyed: it refunds what's left of it, not the whole unbuilt share.
            var wreck = w.SpawnStructure(1, "factory", w.FindPlacement(1, "factory").Value, 0.5f);
            wreck.Hp = 1;
            s0 = Steel();
            var bad = Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", wreck.Id, "confirm", true));
            Check(Ok(bad) && wreck.Dead && Steel() - s0 <= 1, $"a half-built factory at 1 HP refunds almost nothing ({Steel() - s0} steel, was 225)");
            // A fresh foundation at its starting health (10%) is not "damaged": it refunds in full.
            var fresh = w.SpawnStructure(1, "factory", w.FindPlacement(1, "factory").Value, 0f);
            s0 = Steel();
            Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", fresh.Id, "confirm", true));
            Check(fresh.Dead && Steel() - s0 == 300, $"a new foundation at its starting health refunds in full ({Steel() - s0} steel)");
            // A foundation at half its starting health refunds half.
            var half = w.SpawnStructure(1, "factory", w.FindPlacement(1, "factory").Value, 0f);
            half.Hp = half.Def.MaxHp * 0.05f;
            s0 = Steel();
            Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", half.Id, "confirm", true));
            Check(half.Dead && Steel() - s0 == 150, $"a foundation at half its health refunds half ({Steel() - s0} steel)");

            // Under fire: refused for 10 s after the last hit, with when it can be sold; then it sells.
            var pp = w.SpawnStructure(1, "power_plant", w.FindPlacement(1, "power_plant").Value, 1f);
            w.Hurt(pp, 100, null, 0);
            var no = Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", pp.Id));
            Check(!Ok(no) && !pp.Dead && Said(no).Contains("under fire") && Said(no).Contains("10 s"), $"a building an enemy just hit can't be sold: {Said(no)}");
            Run(w, 6);
            no = Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", pp.Id));
            Check(!Ok(no) && !pp.Dead && Said(no).Contains("in 4 s"), $"and the error counts down: {Said(no)}");
            Run(w, 4.5f);
            var yes = Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", pp.Id, "confirm", true));
            Check(Ok(yes) && pp.Dead, $"10 s after the last hit it can be sold: {Said(yes)}");
            // A burning building is dying to whoever set the fire: no selling it until it's repaired above the burn line.
            var blaze = w.SpawnStructure(1, "power_plant", w.FindPlacement(1, "power_plant").Value, 1f);
            blaze.Hp = blaze.Def.MaxHp * 0.2f;
            Run(w, 1);
            var nb = Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", blaze.Id, "confirm", true));
            Check(blaze.Burning && !Ok(nb) && !blaze.Dead && Said(nb).Contains("on fire") && Said(nb).Contains("30%"), $"a burning building can't be sold: {Said(nb)}");
            blaze.Hp = blaze.Def.MaxHp * 0.5f;
            Run(w, 1);
            var nb2 = Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", blaze.Id, "confirm", true));
            Check(!blaze.Burning && Ok(nb2) && blaze.Dead, $"repaired above the burn line, the fire is out and it sells: {Said(nb2)}");
            // Unfinished buildings are covered too.
            var site = w.SpawnStructure(1, "factory", w.FindPlacement(1, "factory").Value, 0.3f);
            w.Hurt(site, 10, null, 0);
            Check(!Ok(Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", site.Id))) && !site.Dead, "an unfinished building that was just hit can't be sold either");

            // Derricks can't be sold, whoever holds them; if one is ever removed another way, its site still comes back.
            var d = w.Derricks.First();
            d.Team = 1;
            var nd = Commands.Execute(w, 1, Cmd("type", "sell", "structure_id", d.Id));
            Check(!Ok(nd) && !d.Dead && d.Team == 1 && Said(nd).Contains("can't be sold") && Said(nd).Contains("steel/s"), $"a held derrick can't be sold: {Said(nd)}");
            int before = w.Derricks.Count();
            w.Remove(d);
            Check(w.DerrickRespawns.Count == 1 && w.Derricks.Count() == before - 1, "removed any other way, a derrick still queues its respawn");
            Run(w, World.DerrickRespawn + 2);
            var back = w.Derricks.FirstOrDefault(x => x.Origin.Equals(d.Origin));
            Check(back != null && back.Team == -1 && w.DerrickRespawns.Count == 0 && w.Derricks.Count() == before, "and a fresh neutral derrick rises on the site");
        }

        static void DefenceUpkeep()
        {
            var w = new World(2, 7, 96);
            var t = w.Teams[0];
            foreach (var u in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(u);
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var turrets = new List<Entity>();
            int baseUsed = t.PowerUsed;
            for (int i = 0; i < 11; i++) turrets.Add(w.SpawnStructure(0, "gun_turret", w.FindPlacement(0, "gun_turret").Value, 1f));
            t.Stock["steel"] = 1000;
            Run(w, 60.05f);
            float paid = 1000 - t.Stock["steel"];
            Check(Math.Abs(paid - 42f) < 1.2f && turrets.All(x => !x.Offline), $"an 11-turret base pays 4% of the 150 steel price (6 steel/min) for each turret past the first 4: {paid:0.0} steel a minute");
            var st = D(StateData.Team(w, 0));
            var up = (Dictionary<string, object>)st["upkeep"];
            Check((double)up["defences"] == 11 && (double)up["paying"] == 7 && (double)up["steel_per_min"] == 42 && ((List<object>)up["offline"]).Count == 0, "state shows the upkeep: 11 defences, 7 paying, 42 steel/min");
            var rulesText = Json.Write(StateView.Rules());
            Check(Defs.Get("laser_tower").Description.Contains("4% of its steel price") && rulesText.Contains("4% of its steel price") && rulesText.Contains("a wall of 11 gun turrets costs 42 steel/min, 11 laser towers 70"),
                  "the defence descriptions and the rules state the upkeep as a share of the steel price, with the 11-tower numbers");

            // Out of steel: the newest go offline (the first 4 never do), and hold fire.
            t.Stock["steel"] = 0.25f; // enough for 2 of the 7 for one second
            Run(w, 1);
            var off = turrets.Where(x => x.Offline).ToList();
            Check(off.Count == 5 && off.All(x => x.Id > turrets[5].Id) && turrets.Take(4).All(x => !x.Offline),
                  $"short of steel, the newest defences past the free ones go offline ({off.Count} offline: #{string.Join(", #", off.Select(x => x.Id))})");
            Check(t.PowerUsed == baseUsed + 6 * 15, $"and only the 6 still online draw power ({t.PowerUsed} used)");
            t.Stock["steel"] = 0;
            Run(w, 1);
            Check(turrets.Count(x => x.Offline) == 7 && w.Alerts.Active(w, 0).Any(a => a.Kind == "defences_offline" && a.Priority == Priority.High),
                  "with no steel at all, all 7 paid defences are offline and a DEFENCES OFFLINE alert says so");
            Check(((List<string>)StateView.TeamState(w, 0)["my_structures"]).Count(l => l.Contains("OFFLINE")) == 7, "my_structures marks them OFFLINE");
            // Offline defences draw no power: 11 turrets draw 15 each, but with 7 offline only the 4 free ones do.
            Check(t.PowerUsed == baseUsed + 4 * 15, $"7 offline turrets draw no power: {t.PowerUsed} used, of which the 4 free turrets draw {4 * 15} (the rest of the base {baseUsed})");
            // An enemy tank next to an offline turret and an online one: only the online one fires.
            var enemy = w.SpawnUnit(1, "heavy_tank", w.Owned(1).First(e => e.IsStructure));
            var offT = turrets.Last(); var onT = turrets[0];
            enemy.Pos = enemy.PrevPos = enemy.GuardPos = offT.Center + new Vec2(3, 0);
            w.SetOrder(enemy, Order.Idle, enemy.Pos);
            w.UpdateVisibility();
            float offFired = offT.LastFiredAt;
            Run(w, 3);
            Check(offT.LastFiredAt == offFired, "an offline defence holds its fire");
            t.Stock["steel"] = 500;
            Run(w, 1.05f);
            Check(turrets.All(x => !x.Offline) && w.Events.Any(e => e.Type == "defences_online"), "paid again, they come back online");
            Check(t.PowerUsed == baseUsed + 11 * 15, $"and draw power again ({t.PowerUsed} used)");
            // A base of 4 or fewer never pays anything.
            var w2 = new World(2, 7, 96);
            var t2 = w2.Teams[0];
            for (int i = 0; i < 4; i++) w2.SpawnStructure(0, "gun_turret", w2.FindPlacement(0, "gun_turret").Value, 1f);
            t2.Stock["steel"] = 0;
            Run(w2, 30);
            Check(w2.Defences(0).All(x => !x.Offline) && t2.Stats.UpkeepPaid == 0, "a base can always defend itself: the first 4 defences are free and never go offline");

            // Upkeep follows the steel price: past the first 4 a gun turret costs 6 steel/min, a SAM site 8, a laser tower 10.
            var w3 = new World(2, 7, 96);
            var t3 = w3.Teams[0];
            for (int i = 0; i < 4; i++) w3.SpawnStructure(0, "gun_turret", w3.FindPlacement(0, "gun_turret").Value, 1f);
            var laser = w3.SpawnStructure(0, "laser_tower", w3.FindPlacement(0, "laser_tower").Value, 1f);
            var sam = w3.SpawnStructure(0, "sam_site", w3.FindPlacement(0, "sam_site").Value, 1f);
            var gun = w3.SpawnStructure(0, "gun_turret", w3.FindPlacement(0, "gun_turret").Value, 1f);
            t3.Stock["steel"] = 1000;
            Run(w3, 60.05f);
            float paid3 = 1000 - t3.Stock["steel"];
            Check(Math.Abs(paid3 - 24f) < 0.8f, $"a laser tower, a SAM site and a gun turret past the free four pay 10 + 8 + 6 steel/min: {paid3:0.0} steel a minute");
            // They are paid oldest first: steel for the laser tower alone (10/min) keeps it online and drops the two newer ones.
            t3.Stock["steel"] = 10f / 60f + 0.001f;
            Run(w3, 1);
            Check(!laser.Offline && sam.Offline && gun.Offline, $"short of steel, the older defence stays paid and the newer ones go offline (laser {(laser.Offline ? "offline" : "online")}, sam {(sam.Offline ? "offline" : "online")}, gun {(gun.Offline ? "offline" : "online")})");
        }

        /// <summary>
        /// The fusion reactor: +500 power for the plasma it burns, scaled to the share of its burn the team can pay (so a
        /// trickle of plasma gives a trickle of power, not none), leaving plasma held back by reserve or save_for alone.
        /// </summary>
        static void FusionReactor()
        {
            var fr = Defs.Get("fusion_reactor"); var plant = Defs.Get("power_plant");
            // In raw ore (steel and copper 1, circuits 2 copper + 1 steel, plasma 2 uranium) it costs less per power than power plants,
            // takes less room, and its fuel for an hour still leaves it ahead.
            int Ore(Dictionary<string, int> c) => c.Sum(kv => kv.Value * (kv.Key switch { "circuits" => 3, "plasma" => 2, _ => 1 }));
            float perHour = World.FusionBurn * 3600 * 2; // uranium
            Check((float)Ore(fr.Cost) / fr.Power < (float)Ore(plant.Cost) / plant.Power && (float)fr.Power / (fr.SizeX * fr.SizeY) > (float)plant.Power / (plant.SizeX * plant.SizeY)
                  && Ore(fr.Cost) + perHour < 5 * Ore(plant.Cost),
                  $"a fusion reactor beats power plants per ore and per tile: {Ore(fr.Cost)} ore for +{fr.Power} ({(float)Ore(fr.Cost) / fr.Power:0.00} each) in {fr.SizeX * fr.SizeY} tiles, against {5 * Ore(plant.Cost)} for five plants in {5 * plant.SizeX * plant.SizeY}; an hour of its fuel is {perHour:0} uranium");

            int baseMade = 0;
            (World w, Team t, Entity r) Reactor(float plasma = 0)
            {
                var w = new World(2, 7, 96);
                var t = w.Teams[0];
                foreach (var u in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(u);
                baseMade = t.PowerProduced;
                t.Stock["plasma"] = plasma;
                var r = w.SpawnStructure(0, "fusion_reactor", w.FindPlacement(0, "fusion_reactor").Value, 1f);
                return (w, t, r);
            }
            // Reactors burn plasma into the team's PlasmaOwed and the stockpile pays it once a second: plasma left is stock minus owed.
            float Left(Team t) => t.Stock["plasma"] - t.PlasmaOwed;
            string ConverterLine(World w, Entity r) => ((List<string>)StateView.TeamState(w, 0)["converters"]).First(l => l.StartsWith($"#{r.Id} "));

            // Fuelled: it makes its full +500 and burns FusionBurn plasma a second.
            var (w1, t1, r1) = Reactor(100);
            Run(w1, 60);
            float burned = 100 - Left(t1);
            Check(t1.PowerProduced == baseMade + 500 && r1.Working && Math.Abs(burned - World.FusionBurn * 60) < 0.05f && ConverterLine(w1, r1).EndsWith("working"),
                  $"a fuelled reactor makes +500 and burns {World.FusionBurn} plasma/s ({burned / 60:0.000}/s measured; power {t1.PowerProduced - baseMade})");

            // Half the plasma it needs arriving: half the power, not none, and nothing stockpiled.
            var (w2, t2, r2) = Reactor();
            t2.Stock["plasma"] = 0;
            for (int i = 0; i < 20 * World.TickRate; i++) { t2.Add("plasma", 0.5f * World.FusionBurn * World.Dt); w2.Step(); }
            Check(Math.Abs(t2.PowerProduced - baseMade - 250) <= 2 && r2.Working && Left(t2) < 0.01f && ConverterLine(w2, r2).Contains("SHORT OF PLASMA") && ConverterLine(w2, r2).Contains("50%"),
                  $"with half the plasma it needs it makes half the power ({t2.PowerProduced - baseMade} of 500): {ConverterLine(w2, r2)}");
            // And a tenth of what it needs: a tenth of the power.
            for (int i = 0; i < 20 * World.TickRate; i++) { t2.Add("plasma", 0.1f * World.FusionBurn * World.Dt); w2.Step(); }
            Check(Math.Abs(t2.PowerProduced - baseMade - 50) <= 2, $"and a tenth of it, a tenth of the power ({t2.PowerProduced - baseMade} of 500)");

            // No plasma at all: no power from it, and it says so.
            var (w3, t3, r3) = Reactor();
            t3.Stock["plasma"] = 0;
            Run(w3, 5);
            Check(t3.PowerProduced == baseMade && !r3.Working && ConverterLine(w3, r3).Contains("IDLE (no plasma"), $"with no plasma it makes nothing ({t3.PowerProduced - baseMade}): {ConverterLine(w3, r3)}");
            // Short of power because of it, the low-power alert says why and what to do.
            for (int i = 0; i < 2; i++) w3.SpawnStructure(0, "gun_turret", w3.FindPlacement(0, "gun_turret").Value, 1f);
            Run(w3, 1);
            Check(t3.LowPower && w3.Alerts.All.Any(a => a.Team == 0 && a.Kind == "low_power" && a.Lost.Any(l => l.Contains($"fusion_reactor #{r3.Id} is short of plasma and makes only 0% of its 500 power"))),
                  "and the low-power alert names the starved reactor");

            // Reserve and save_for hold plasma back from it, as from converters.
            var (w4, t4, r4) = Reactor();
            t4.Stock["plasma"] = 50;
            t4.Reserve["plasma"] = 50;
            Run(w4, 10);
            Check(t4.Stock["plasma"] == 50 && t4.PowerProduced == baseMade && !r4.Working, $"plasma held back by 'reserve' is not burned ({t4.Stock["plasma"]:0.###} left, power {t4.PowerProduced - baseMade})");
            t4.Reserve["plasma"] = 40;
            Run(w4, 10);
            Check(Left(t4) < 50 && Left(t4) >= 40 && t4.PowerProduced == baseMade + 500, $"it burns what's above the reserve ({Left(t4):0.###} of 50, reserve 40)");
            t4.Reserve.Clear();
            t4.Stock["plasma"] = 40;
            t4.SaveFor = "laser_tank";
            t4.SaveForCost["plasma"] = 40;
            Run(w4, 10);
            Check(t4.Stock["plasma"] == 40 && t4.PowerProduced == baseMade, $"plasma saved for a purchase ('save_for') is not burned either ({t4.Stock["plasma"]:0.###} left)");

            // A big stockpile: a tick's burn (0.0015) is below what a float stock of 100,000 can add or take away, so the
            // burn is owed and paid once a second (0.03, which a float of 100,000 holds to within 0.004). 200 s at full output is 6 plasma.
            var (w5, t5, r5) = Reactor(100000);
            Run(w5, 200);
            float burned5 = 100000 - Left(t5);
            Check(burned5 > 5.8f && burned5 < 6.4f && t5.Stock["plasma"] <= 99995 && t5.PowerProduced == baseMade + 500,
                  $"a reactor burns its fuel off a stockpile of 100,000 too ({burned5:0.000} of {World.FusionBurn * 200} plasma in 200 s, {100000 - t5.Stock["plasma"]:0.00} taken off the stock)");

            // Two reactors on too little plasma share it: they can't burn more than the stockpile holds (each used to count only
            // its own debt, so two reactors on 0.5 plasma burned 1.0), and the second one's power drops with the first's.
            var (w10, t10, r10) = Reactor(0.5f);
            var r10b = w10.SpawnStructure(0, "fusion_reactor", w10.FindPlacement(0, "fusion_reactor").Value, 1f);
            float lowest10 = float.MaxValue;
            for (int i = 0; i < 60 * World.TickRate; i++) { w10.Step(); lowest10 = MathF.Min(lowest10, t10.Stock["plasma"]); }
            Check(lowest10 >= 0f && Left(t10) > -0.001f && Left(t10) < 0.0016f && r10.Output < 0.001f && r10b.Output < 0.001f && t10.PowerProduced == baseMade,
                  $"two reactors on 0.5 plasma burn exactly that and stop (left {Left(t10):0.0000}, stock never below {lowest10:0.0000}, power {t10.PowerProduced - baseMade})");
            var (w11, t11, r11) = Reactor(0.5f);
            var r11b = w11.SpawnStructure(0, "fusion_reactor", w11.FindPlacement(0, "fusion_reactor").Value, 1f);
            Run(w11, 3);
            Check(t11.PowerProduced == baseMade + 1000 && Left(t11) < 0.5f - 0.05f, $"while it lasts both make full power ({t11.PowerProduced - baseMade}), burning together ({0.5f - Left(t11):0.000} of 0.5 plasma gone after 3 s)");

            // Burned plasma can't be spent: Team.Amount (and so Missing, which every purchase checks) leaves out what is owed.
            var (w12, t12, r12) = Reactor(40);
            Run(w12, 0.5f);
            var plasma40 = new Dictionary<string, int> { { "plasma", 40 } };
            Check(t12.Amount("plasma") == 39 && t12.Missing(plasma40) != null && t12.PlasmaOwed > 0,
                  $"a stockpile of 40 plasma that a reactor has burned into can't buy a 40-plasma unit ({t12.Amount("plasma")} available; {t12.Missing(plasma40) ?? "affordable"})");
            // A team that leaves takes its stockpile with it as salvage, minus the plasma already burned, and owes nothing after.
            float owed12 = t12.PlasmaOwed;
            w12.Leave(0);
            Check(owed12 > 0 && t12.PlasmaOwed == 0 && t12.Stock.Count == 0, $"a team that leaves leaves no burned plasma owed behind ({owed12:0.0000} owed before)");

            // A reactor starts at the share its team can pay: finished with plasma in the stockpile it counts at once, without a tick.
            var (w6, t6, r6) = Reactor(100);
            Check(r6.Output == 1f && t6.PowerProduced == baseMade + 500, $"a newly finished reactor with plasma counts its full power in the first sum ({t6.PowerProduced - baseMade})");
            var (w7, t7, r7) = Reactor(0);
            Check(r7.Output == 0f && t7.PowerProduced == baseMade, $"and with none it counts nothing ({t7.PowerProduced - baseMade})");

            // Captured, it is the new owner's to fuel: a team with no plasma gets none of the old owner's output, and doesn't
            // flip into power and back out of it for a tick (no power_restored, no second low_power).
            var w8 = new World(2, 7, 96);
            foreach (var u in w8.Owned(0).Concat(w8.Owned(1)).Where(e => !e.IsStructure).ToList()) w8.Remove(u);
            var t8a = w8.Teams[0]; var t8b = w8.Teams[1];
            t8b.Stock["plasma"] = 100;
            var r8 = w8.SpawnStructure(1, "fusion_reactor", w8.FindPlacement(1, "fusion_reactor").Value, 1f);
            for (int i = 0; i < 2; i++) w8.SpawnStructure(0, "gun_turret", w8.FindPlacement(0, "gun_turret").Value, 1f); // 30 power drawn against the command center's 20
            r8.Hp = r8.Def.MaxHp * 0.4f;
            Run(w8, 2);
            var eng8 = w8.SpawnUnit(0, "engineer", w8.Owned(0).First(e => e.IsStructure));
            eng8.Pos = eng8.PrevPos = r8.Center + new Vec2(1.6f, 0);
            w8.SetOrder(eng8, Order.Capture, r8.Center, r8.Id);
            int power0 = t8a.PowerProduced, restored0 = w8.Events.Count(e => e.Type == "power_restored" && e.Team == 0), low0 = w8.Events.Count(e => e.Type == "low_power" && e.Team == 0);
            Run(w8, 3);
            Check(r8.Team == 0 && r8.Output == 0f && t8a.PowerProduced == power0 && t8a.LowPower
                  && w8.Events.Count(e => e.Type == "power_restored" && e.Team == 0) == restored0 && w8.Events.Count(e => e.Type == "low_power" && e.Team == 0) == low0,
                  $"a captured reactor makes only what its new team's plasma pays for ({r8.Output:0.00}), without the old owner's output flipping the team out of low power for a tick");

            // Saved and resumed, a half-fed reactor keeps its output.
            var game = new Game(new GameConfig { Seed = 5, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            var gw = game.World;
            foreach (var u in gw.Entities.Where(e => !e.IsStructure).ToList()) gw.Remove(u);
            var gr = gw.SpawnStructure(0, "fusion_reactor", gw.FindPlacement(0, "fusion_reactor").Value, 1f);
            for (int i = 0; i < 5 * World.TickRate + 7; i++) { gw.Teams[0].Add("plasma", 0.5f * World.FusionBurn * World.Dt); gw.Step(); } // ends mid-second: some burn is owed
            var json = Snap(game);
            var back = Resume(json);
            var br = back.World.Get(gr.Id);
            var again = Snap(back);
            Check(gr.Output > 0.4f && gr.Output < 0.6f && br != null && Math.Abs(br.Output - gr.Output) < 1e-4f && br.Working && back.World.Teams[0].PowerProduced == gw.Teams[0].PowerProduced && again == json,
                  $"a half-fed reactor survives a save and resume ({gr.Output:0.00} -> {br?.Output:0.00}, power {back.World.Teams[0].PowerProduced} of {gw.Teams[0].PowerProduced}; identical {again == json}: {FirstDiff(json, again)})");
            Check(gw.Teams[0].PlasmaOwed > 0 && Math.Abs(back.World.Teams[0].PlasmaOwed - gw.Teams[0].PlasmaOwed) < 1e-5f, $"and so does the plasma the team's reactors owe its stockpile ({gw.Teams[0].PlasmaOwed:0.0000})");

            // A save from before reactors had partial output (no "output" saved) loads sensibly: a working one at full, an idle one at none.
            var fuelled = gw.SpawnStructure(0, "fusion_reactor", gw.FindPlacement(0, "fusion_reactor").Value, 1f);
            gw.Teams[0].Stock["plasma"] = 1000;
            for (int i = 0; i < 3 * World.TickRate; i++) gw.Step();
            var root = (Dictionary<string, object>)Json.Parse(Snap(game));
            int stripped = 0;
            foreach (var en in (List<object>)((Dictionary<string, object>)root["world"])["entities"])
                if (en is Dictionary<string, object> ed && (string)ed["def"] == "fusion_reactor") { ed.Remove("output"); stripped++; }
            var old = Resume(Json.Write(root));
            var ow = old.World;
            Check(stripped == 2 && ow.Get(fuelled.Id).Output == 1f && ow.Get(fuelled.Id).Working && ow.Teams[0].PowerProduced == gw.Teams[0].PowerProduced,
                  $"a save from before partial output loads a working reactor at full output ({ow.Get(fuelled.Id).Output:0.00}, power {ow.Teams[0].PowerProduced} of {gw.Teams[0].PowerProduced})");
            gw.Teams[0].Stock["plasma"] = 0;
            gw.Teams[0].PlasmaOwed = 0;
            for (int i = 0; i < 2 * World.TickRate; i++) gw.Step();
            root = (Dictionary<string, object>)Json.Parse(Snap(game));
            foreach (var en in (List<object>)((Dictionary<string, object>)root["world"])["entities"])
                if (en is Dictionary<string, object> ed && (string)ed["def"] == "fusion_reactor") ed.Remove("output");
            old = Resume(Json.Write(root));
            Check(old.World.Get(fuelled.Id).Output == 0f && !old.World.Get(fuelled.Id).Working && old.World.Teams[0].PowerProduced == gw.Teams[0].PowerProduced,
                  $"and an idle one at none (power {old.World.Teams[0].PowerProduced} of {gw.Teams[0].PowerProduced})");

            // A save from before offline defences stopped drawing power still holds the old power_used. Loading adds power
            // up again, so a team that has enough under the new rules isn't left at half speed. Steel at zero means the
            // defences' Offline flags never change, so nothing else would correct it.
            var g9 = new Game(new GameConfig { Seed = 5, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            var w9 = g9.World;
            foreach (var u in w9.Entities.Where(e => !e.IsStructure).ToList()) w9.Remove(u);
            var t9 = w9.Teams[0];
            w9.SpawnStructure(0, "power_plant", w9.FindPlacement(0, "power_plant").Value, 1f);
            for (int i = 0; i < 9; i++) w9.SpawnStructure(0, "gun_turret", w9.FindPlacement(0, "gun_turret").Value, 1f);
            t9.Stock["steel"] = 0;
            for (int i = 0; i < 3 * World.TickRate; i++) w9.Step();
            int offline9 = w9.Defences(0).Count(d => d.Offline), used9 = t9.PowerUsed;
            var root9 = (Dictionary<string, object>)Json.Parse(Snap(g9));
            var team9 = (Dictionary<string, object>)((List<object>)((Dictionary<string, object>)root9["world"])["teams"])[0];
            team9["power_used"] = (double)(used9 + offline9 * 15); // what the old rules counted: the offline turrets too
            var back9 = Resume(Json.Write(root9));
            var bt9 = back9.World.Teams[0];
            Check(offline9 == 5 && bt9.PowerUsed == used9 && !bt9.LowPower && back9.World.Events.Any(e => e.Type == "power_restored" && e.Team == 0),
                  $"loading an older save adds power up again: {bt9.PowerUsed} used, not {used9 + offline9 * 15} ({offline9} offline turrets draw nothing), low power {bt9.LowPower}");
        }

        /// <summary>attack "until":"capturable": units hold fire at 50% so a commander with slow replies can still land the capture.</summary>
        static void AttackUntilCapturable()
        {
            var w = new World(2, 7, 96);
            foreach (var u in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(u);
            var d = w.Derricks.First();
            d.Team = 1;
            var hq0 = w.Owned(0).First(e => e.Def.Key == "command_center");
            var tanks = Enumerable.Range(0, 3).Select(i => At(w.SpawnUnit(0, "heavy_tank", hq0), d.Center + new Vec2(4, i - 1f))).ToList();
            var ids = tanks.Select(t => t.Id).ToArray();
            w.UpdateVisibility();
            var r = Commands.Execute(w, 0, Cmd("type", "attack", "units", ids, "target", d.Id, "until", "capturable"));
            Run(w, 40);
            float held = d.Hp;
            Run(w, 5);
            Check(Ok(r) && d.Hp <= d.Def.MaxHp * World.CaptureThreshold && d.Hp >= d.Def.MaxHp * World.BurnBelow && d.Hp == held && tanks.All(t => t.Order == Order.Attack && t.UntilCapturable),
                  $"attack until capturable: heavy tanks stop at 50%, short of the fire, and stand by ({Said(r)}; hp {d.Hp:0}/{d.Def.MaxHp})");
            d.Hp = d.Def.MaxHp * 0.6f;
            Run(w, 10);
            Check(d.Hp <= d.Def.MaxHp * World.CaptureThreshold && d.Hp >= d.Def.MaxHp * World.BurnBelow, $"repaired above 50%, they open up again and stop again ({d.Hp:0}/{d.Def.MaxHp})");

            var foe = At(w.SpawnUnit(1, "light_tank", w.Owned(1).First(e => e.IsStructure)), d.Center + new Vec2(4, 2.5f));
            w.UpdateVisibility();
            var again = Commands.Execute(w, 0, Cmd("type", "attack", "units", ids, "target", d.Id, "until", "capturable"));
            var unit = Commands.Execute(w, 0, Cmd("type", "attack", "units", ids, "target", foe.Id, "until", "capturable"));
            var bad = Commands.Execute(w, 0, Cmd("type", "attack", "units", ids, "target", d.Id, "until", "dead"));
            w.Remove(foe);
            Check(!Ok(again) && Said(again).Contains("capture it now") && !Ok(unit) && Said(unit).Contains("only structures") && !Ok(bad) && tanks.All(t => t.Order == Order.Attack && t.TargetId == d.Id),
                  $"refused, orders unchanged: a target already capturable ({Said(again)}), a unit ({Said(unit)}), an unknown until ({Said(bad)})");

            var eng = At(w.SpawnUnit(0, "engineer", hq0), d.Center + new Vec2(1.6f, 0.4f));
            var rc = Commands.Execute(w, 0, Cmd("type", "capture", "units", new[] { eng.Id }, "target", d.Id));
            Run(w, 5);
            Check(Ok(rc) && d.Team == 0 && d.Hp == d.Def.MaxHp * World.CaptureThreshold && tanks.All(t => t.Order == Order.Idle && !t.UntilCapturable),
                  $"the engineer takes it at 50% and the tanks stand down ({Said(rc)})");
        }

        /// <summary>attack_move leaves held derricks alone unless told "derricks":true (never neutral ones), and says so when units stop by one.</summary>
        static void AttackMoveDerricks()
        {
            var w = new World(2, 7, 96);
            foreach (var u in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(u);
            var ds = w.Derricks.ToList();
            var d = ds[0];
            d.Team = 1;
            var hq0 = w.Owned(0).First(e => e.Def.Key == "command_center");
            var tanks = Enumerable.Range(0, 2).Select(i => At(w.SpawnUnit(0, "light_tank", hq0), d.Center + new Vec2(-12, i))).ToList();
            var ids = tanks.Select(t => t.Id).ToArray();
            var r = Commands.Execute(w, 0, Cmd("type", "attack_move", "units", ids, "x", d.Center.X - 3, "y", d.Center.Y));
            Run(w, 15);
            var hints = w.Events.Where(e => e.Type == "derrick_skipped").ToList();
            Check(Ok(r) && d.Hp == d.Def.MaxHp && tanks.All(t => t.Order == Order.Idle) && hints.Count == 1 && hints[0].B == d.Id && hints[0].Team == 0 &&
                  StateView.EventsFor(w, 0, 0, 25).Any(l => l.Contains("\"derricks\":true")),
                  $"by default attack_move passes a held derrick, and the units stopping by it say why, once ({hints.FirstOrDefault()?.Text})");
            Commands.Execute(w, 0, Cmd("type", "attack_move", "units", ids, "x", d.Center.X - 3, "y", d.Center.Y + 2));
            Run(w, 5);
            Check(w.Events.Count(e => e.Type == "derrick_skipped") == 1 && d.Hp == d.Def.MaxHp, "and not again for the same derrick soon after");

            var rm = Commands.Execute(w, 0, Cmd("type", "move", "units", ids, "x", d.Center.X - 3, "y", d.Center.Y, "derricks", true));
            var r2 = Commands.Execute(w, 0, Cmd("type", "attack_move", "units", ids, "x", d.Center.X - 3, "y", d.Center.Y - 2, "derricks", true));
            Run(w, 8);
            Check(!Ok(rm) && Ok(r2) && d.Hp < d.Def.MaxHp && tanks.All(t => t.Order == Order.AttackMove && t.EngageDerricks),
                  $"with \"derricks\":true they shoot it ({Said(r2)}; hp {d.Hp:0}/{d.Def.MaxHp}); move refuses the flag ({Said(rm)})");
            int kv = w.Teams[0].Stats.KillValue;
            w.Hurt(d, 1e5f, tanks[0]);
            Check(d.Dead && w.Teams[0].Stats.KillValue - kv == World.DerrickWorth && World.HowScored.Contains($"about {World.DerrickWorth}"),
                  $"a destroyed derrick is worth {w.Teams[0].Stats.KillValue - kv} kill value, as the points text says");

            var neutral = ds[1];
            foreach (var t in tanks) At(t, neutral.Center + new Vec2(-8, tanks.IndexOf(t)));
            float fired = tanks.Max(t => t.LastFiredAt);
            Commands.Execute(w, 0, Cmd("type", "attack_move", "units", ids, "x", neutral.Center.X - 3, "y", neutral.Center.Y, "derricks", true));
            Run(w, 10);
            Check(neutral.Team < 0 && neutral.Hp == neutral.Def.MaxHp && tanks.Max(t => t.LastFiredAt) == fired && tanks.All(t => t.Order == Order.Idle),
                  "never a neutral derrick: it can't be hurt, so nobody shoots at it");
        }

        static void Derricks()
        {
            var w = new World(2, 7, 96);
            var ds = w.Derricks.ToList();
            var mid = new Vec2(48, 48);
            float d0 = Vec2.Dist(ds[0].Center, w.Teams[0].StartPos), d1 = Vec2.Dist(ds[0].Center, w.Teams[1].StartPos);
            Check(ds.Count == 2 && ds.All(d => d.Team == -1 && Vec2.Dist(d.Center, mid) < 18) && Math.Abs(d0 - d1) < 6,
                  $"a 96-tile map has two neutral derricks near the middle, about as far from each base ({d0:0} vs {d1:0} tiles)");
            Check(World.DerrickCount(208, 208) == 5 && World.DerrickCount(320, 320) == 8, "bigger maps get more (5 at 208, 8 at 320)");
            var d = ds[0];
            var hq0 = w.Owned(0).First(e => e.Def.Key == "command_center");
            var tank = At(w.SpawnUnit(0, "heavy_tank", hq0), d.Center + new Vec2(3, 0));
            w.Hurt(d, 5000, tank);
            var ra = Commands.Execute(w, 0, Cmd("type", "attack", "units", new[] { tank.Id }, "target", d.Id));
            Run(w, 5);
            Check(d.Hp == d.Def.MaxHp && !Ok(ra) && tank.LastFiredAt < 0, $"a neutral derrick can't be hurt, and nobody shoots at it ({Said(ra)})");

            // Captured by any engineer at full health, even out of sight; then it pays its holder.
            tank.Pos = tank.PrevPos = tank.GuardPos = hq0.Center + new Vec2(0, -4); // out of the way (it would shoot the engineers)
            foreach (var u in w.Owned(0).Concat(w.Owned(1)).Where(u => !u.IsStructure && u != tank).ToList()) w.Remove(u);
            var t1 = w.Teams[1];
            var eng = At(w.SpawnUnit(1, "engineer", w.Owned(1).First(e => e.IsStructure)), d.Center + new Vec2(1.6f, 0.4f));
            var rc = Commands.Execute(w, 1, Cmd("type", "capture", "units", new[] { eng.Id }, "target", d.Id));
            Run(w, 10);
            float steel0 = t1.Amount("steel");
            Run(w, 10);
            float paid = t1.Amount("steel") - steel0;
            Check(Ok(rc) && d.Team == 1 && eng.Dead && t1.Stats.DerricksCaptured == 1 && w.Events.Any(e => e.Type == "chat" && e.Text.Contains("derrick")),
                  $"an engineer captures a neutral derrick at full health ({Said(rc)})");
            Check(paid >= 14.5f && paid <= 15.5f + 10f /* the command center's trickle is iron, not steel */, $"it pays its holder 1.5 steel/s ({paid:0.0} steel in 10 s)");
            // Its new owner's enemies: not above 50%, yes below it; destroyed it leaves salvage and comes back neutral.
            var eng0 = At(w.SpawnUnit(0, "engineer", hq0), d.Center + new Vec2(-1.6f, 0));
            w.UpdateVisibility();
            var r1 = Commands.Execute(w, 0, Cmd("type", "capture", "units", new[] { eng0.Id }, "target", d.Id));
            w.Hurt(d, d.Def.MaxHp * 0.6f, tank);
            var r2 = Commands.Execute(w, 0, Cmd("type", "capture", "units", new[] { eng0.Id }, "target", d.Id));
            Run(w, 10);
            Check(!Ok(r1) && Ok(r2) && d.Team == 0, $"an owned derrick is taken back like any building: an engineer once it's below 50% ({Said(r1)})");
            int salvage0 = w.Teams[1].Stats.SalvageLeft;
            var raider = At(w.SpawnUnit(1, "heavy_tank", w.Owned(1).First(e => e.IsStructure)), d.Center + new Vec2(3, 3));
            w.Hurt(d, 1e5f, raider);
            Check(d.Dead && w.Teams[1].Stats.SalvageLeft - salvage0 == 150 && w.DerrickRespawns.Count == 1, $"destroyed, it leaves salvage ({w.Teams[1].Stats.SalvageLeft - salvage0} iron ore)");
            Run(w, 119);
            int during = w.Derricks.Count();
            Run(w, 2);
            var back = w.Derricks.FirstOrDefault(x => x.Origin.Equals(d.Origin));
            Check(during == 1 && back != null && back.Team == -1 && back.Hp == back.Def.MaxHp, "and a fresh neutral derrick rises on its site two minutes later");

            // A derrick doesn't keep a team in; a team that leaves or is out gives its derricks back.
            var w2 = new World(2, 7, 96) { Open = true };
            var dd = w2.Derricks.First();
            foreach (var u in w2.Owned(0).Where(u => !u.IsStructure).ToList()) w2.Remove(u);
            var e2 = At(w2.SpawnUnit(1, "engineer", w2.Owned(1).First(e => e.IsStructure)), dd.Center + new Vec2(1.6f, 0));
            w2.SetOrder(e2, Order.Capture, dd.Center, dd.Id);
            Run(w2, 8);
            bool held = dd.Team == 1;
            foreach (var e in w2.Owned(1).Where(e => e != dd).ToList()) w2.Remove(e);
            Run(w2, 2);
            Check(held && w2.Teams[1].Defeated && dd.Team == -1 && !dd.Dead, "a team left with nothing but a derrick is out, and its derrick goes back to neutral");
            var t3 = w2.AddTeam("llm", "Joiner", out _);
            int after = w2.Derricks.Count();
            Check(w2.Map.W > 96 && after == World.DerrickCount(w2.Map.W, w2.Map.H), $"the map grows and gains derricks around its new middle ({w2.Map.W}x{w2.Map.H}: {after})");

            // What players see: every derrick, whose it is, what it pays; neutral ones aren't listed as enemies.
            var st = D(StateData.Team(w, 0));
            var list = (List<object>)st["derricks"];
            var enemies = (List<object>)st["enemies"];
            Check(list.Count == 2 && list.Cast<Dictionary<string, object>>().All(x => x.ContainsKey("income_steel_per_s") && x.ContainsKey("owner_team")) &&
                  !enemies.Cast<Dictionary<string, object>>().Any(x => (string)x["type"] == "derrick" && (double)x["team"] < 0),
                  "state lists every derrick (where, whose, its pay) and never a neutral one as an enemy");
            var text = StateView.TeamState(w, 0);
            Check(((List<string>)text["derricks"]).Any(l => l.Contains("neutral") && l.Contains("capture")), $"the text state says how to take one ({((List<string>)text["derricks"]).FirstOrDefault()})");

            // A game saved before derricks gets them when it resumes.
            var g = new Game(new GameConfig { Seed = 8, MapSize = 96, Controllers = new[] { "llm", "llm" } });
            var root = (Dictionary<string, object>)Json.Parse(Snap(g));
            var ents = (List<object>)((Dictionary<string, object>)root["world"])["entities"];
            ents.RemoveAll(x => (string)((Dictionary<string, object>)x)["def"] == "derrick");
            var old = Resume(Json.Write(root));
            Check(!old.World.Derricks.Any(), "(an old save has none)");
            Step(old, 1);
            Check(old.World.Derricks.Count() == 2 && old.World.Errors == 0, "a game from before derricks gets its set on resume");

            // The house AI sends engineers for them.
            var ai = new Game(new GameConfig { Seed = 9, MapSize = 96, Open = true, Controllers = new[] { "ai", "ai" } });
            Step(ai, 12 * 60);
            Check(ai.World.Derricks.Count(x => x.Team >= 0) >= 1 && ai.World.Teams.Sum(t => t.Stats.DerricksCaptured) >= 1,
                  $"house AIs capture derricks with engineers ({ai.World.Derricks.Count(x => x.Team >= 0)} held after 12 minutes)");
        }

        static Dictionary<string, object> D(JObj o) => (Dictionary<string, object>)Json.Parse(Json.Write(o));

        /// <summary>
        /// A flat 80x80 map with each base's iron and copper laid out alike: seat 1's half is seat 0's turned half a circle about
        /// the middle of the two bases (tile x,y becomes 80-x,80-y), so any difference between the seats is the rules' doing.
        /// </summary>
        static World MirroredBases()
        {
            var w = new World(2, 1, 80);
            var m = w.Map;
            Array.Clear(m.Tiles, 0, m.Tiles.Length); Array.Clear(m.Ore, 0, m.Ore.Length); Array.Clear(m.OreBase, 0, m.OreBase.Length);
            m.Deep.Clear();
            void Field(int cx, int cy, float r, byte type)
            {
                for (int y = cy - 5; y <= cy + 5; y++)
                    for (int x = cx - 5; x <= cx + 5; x++)
                    {
                        if (MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) > r) continue;
                        foreach (var (tx, ty) in new[] { (x, y), (79 - x, 79 - y) })
                        {
                            int i = m.Idx(tx, ty);
                            m.Ore[i] = m.OreBase[i] = 300; m.OreType[i] = m.OreBaseType[i] = type;
                        }
                    }
            }
            Field(15, 15, 3.4f, Map.Iron);
            Field(7, 17, 2.4f, Map.Copper);
            m.FieldsChanged(); // the map's field index was built from the ore it had before
            w.ChooseBays(); // each command center picked its bay side from the ore the map had before
            return w;
        }

        /// <summary>How far a point is from the iron field next to a team's base.</summary>
        static float ToHomeIron(World w, int team, Vec2 p)
        {
            var s = w.Teams[team].StartPos;
            float best = float.MaxValue;
            for (int y = 0; y < w.Map.H; y++)
                for (int x = 0; x < w.Map.W; x++)
                {
                    int i = w.Map.Idx(x, y);
                    var c = new Vec2(x + 0.5f, y + 0.5f);
                    if (w.Map.OreBase[i] > 0 && w.Map.OreBaseType[i] == Map.Iron && Vec2.Dist(c, s) < 14) best = MathF.Min(best, Vec2.Dist(c, p));
                }
            return best;
        }

        /// <summary>A drop-off's truck bay opens on the side that faces its ore, the side is saved, and an old save has south bays (#64).</summary>
        static void BaysFaceTheOre()
        {
            // Mirrored bases: the bottom-left base's bay faces its ore (it opened south, away from it), and the top-right base's faces
            // its own, the same distance and the opposite way round.
            var w = MirroredBases();
            var hq = new[] { 0, 1 }.Select(t => w.Owned(t).First(e => e.Def.Key == "command_center")).ToArray();
            float near0 = ToHomeIron(w, 0, w.Bay(hq[0]).head), near1 = ToHomeIron(w, 1, w.Bay(hq[1]).head);
            float south0 = ToHomeIron(w, 0, w.Bay(hq[0].Origin, hq[0].Def, 0).head);
            Check(hq[0].BaySide != 0 && near0 < south0 - 2f, $"a bottom-corner command center's bay faces its ore (side {hq[0].BaySide}, {near0:0.0} tiles from the iron where south would be {south0:0.0})");
            Check(MathF.Abs(near0 - near1) < 0.01f && MathF.Cos(w.Bay(hq[0]).facing + MathF.PI - w.Bay(hq[1]).facing) > 0.99f,
                  $"and the opposite corner's mirrors it (sides {hq[0].BaySide} and {hq[1].BaySide}, {near0:0.0} and {near1:0.0} tiles)");

            // Generated maps: no command center opens further from its iron than south would, and the bottom-left one (south was always
            // away from its ore) is well nearer.
            var bad = new List<string>();
            foreach (var (seed, size, ore) in new[] { (1, 80, 1f), (2, 80, 1f), (3, 96, 1f), (4, 128, 0.3f), (5, 112, 1f), (6, 80, 0.3f) })
            {
                var g = new World(2, seed, size, ore);
                for (int t = 0; t < 2; t++)
                {
                    var h = g.Owned(t).First(e => e.Def.Key == "command_center");
                    float chosen = ToHomeIron(g, t, g.Bay(h).head), south = ToHomeIron(g, t, g.Bay(h.Origin, h.Def, 0).head);
                    if (chosen > south + 0.5f || (t == 0 && chosen > south - 3f)) bad.Add($"seed {seed} size {size} seat {t}: {chosen:0.0} against {south:0.0} for south");
                }
            }
            Check(bad.Count == 0, "on generated maps every command center's bay faces its ore" + (bad.Count > 0 ? $" (not: {string.Join("; ", bad)})" : ""));

            // The side is saved with the game; a save from before bays turned has none, and its bays are south as they were.
            var game = new Game(new GameConfig { Seed = 2, MapSize = 80, Controllers = new[] { "llm", "llm" } });
            var gw = game.World;
            for (int t = 0; t < 2; t++)
            {
                gw.SpawnStructure(t, "power_plant", gw.FindPlacement(t, "power_plant").Value, 1f);
                gw.SpawnStructure(t, "mining_refinery", gw.FindPlacement(t, "mining_refinery").Value, 1f);
            }
            var sides = gw.Entities.Where(e => !e.Dead && e.Def.DropOff).ToDictionary(e => e.Id, e => e.BaySide);
            string saved = Snap(game);
            var back = Resume(saved).World;
            Check(sides.Count == 4 && sides.Values.Any(s => s != 0) && sides.All(kv => back.Get(kv.Key)?.BaySide == kv.Value),
                  $"a snapshot round-trips every drop-off's bay side (sides {string.Join(", ", sides.Values)})");
            var old = (Dictionary<string, object>)Json.Parse(saved);
            foreach (Dictionary<string, object> e in (List<object>)((Dictionary<string, object>)old["world"])["entities"]) e.Remove("bay");
            var oldGame = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm", "llm" } });
            oldGame.Restore(old);
            var oldDropOffs = oldGame.World.Entities.Where(e => !e.Dead && e.Def.DropOff).ToList();
            Step(oldGame, 10);
            Check(oldDropOffs.Count == 4 && oldDropOffs.All(e => e.BaySide == 0) && oldGame.World.Errors == 0,
                  $"a snapshot from before bays turned loads with every bay south, and plays on ({oldDropOffs.Count} drop-offs, sides {string.Join(", ", oldDropOffs.Select(e => e.BaySide))}, {oldGame.World.Errors} errors: {oldGame.World.LastError})");
        }

        /// <summary>
        /// Two seats on opposite corners get the same of everything (#69): base sites (and so how far each is from its ore and from the
        /// derricks), the tie-breaks that used to go to the first thing the scan met (the lowest y, then x: the south-west corner's side),
        /// and who strikes first in a fight (units took turns in a fixed order, the first seat's units first). Each is told apart from
        /// luck by turning a position half a circle about the middle of the map and seeing the other seat do the same.
        /// </summary>
        static void SeatSymmetry()
        {
            // Base sites on tile centres: each base the half-turned copy of the next, its command center included.
            var off = new List<string>();
            foreach (var size in new[] { 56, 80, 96, 128 })
            {
                var w = new World(4, 3, size);
                var s = w.Map.Spawns;
                bool ok = s.Take(4).All(p => p.X - MathF.Floor(p.X) == 0.5f && p.Y - MathF.Floor(p.Y) == 0.5f)
                       && Vec2.Dist(s[1], new Vec2(size - s[0].X, size - s[0].Y)) < 1e-3f && Vec2.Dist(s[2], new Vec2(size - s[0].Y, s[0].X)) < 1e-3f && Vec2.Dist(s[3], new Vec2(s[0].Y, size - s[0].X)) < 1e-3f;
                var hq = Enumerable.Range(0, 4).Select(t => w.Owned(t).First(e => e.Def.Key == "command_center")).ToArray();
                ok &= hq[1].Origin.Equals(new Int2(size - 3 - hq[0].Origin.X, size - 3 - hq[0].Origin.Y));
                if (!ok) off.Add($"{size}x{size}");
            }
            Check(off.Count == 0, "base sites are on tile centres, each the half-turned copy of the next, command centers too" + (off.Count > 0 ? $" (not on {string.Join(", ", off)})" : ""));
            double toIron = 0, toDerrick = 0; int maps = 0, derrickMaps = 0;
            for (int seed = 1; seed <= 40; seed++)
            {
                var w = new World(2, seed, 80);
                var hq = new[] { 0, 1 }.Select(t => w.Owned(t).First(e => e.Def.Key == "command_center")).ToArray();
                toIron += ToHomeIron(w, 1, hq[1].Center) - ToHomeIron(w, 0, hq[0].Center); maps++;
                var ds = w.Derricks.ToList();
                if (ds.Count >= 2) { toDerrick += ds.Min(d => Vec2.Dist(d.Center, w.Teams[1].StartPos)) - ds.Min(d => Vec2.Dist(d.Center, w.Teams[0].StartPos)); derrickMaps++; }
            }
            Check(MathF.Abs((float)(toIron / maps)) < 0.5f, $"over 40 maps each seat's command center is as near its iron (seat 1 {toIron / maps:+0.00;-0.00} tiles on average; it was +1.4)");
            Check(derrickMaps >= 30 && MathF.Abs((float)(toDerrick / derrickMaps)) < 0.4f, $"and each seat's nearest derrick is as far (seat 1 {toDerrick / Math.Max(1, derrickMaps):+0.00;-0.00} tiles on average over {derrickMaps} maps; it was -0.8)");

            // Equal choices go to mirrored picks: the first ore tile a truck turns to, the nearest open tile, a wave's corner, a derrick.
            var map = new Map(80, 80);
            foreach (var (x, y) in new[] { (10, 10), (11, 10), (12, 10), (10, 11), (12, 11), (10, 12), (11, 12), (12, 12), (67, 67), (68, 67), (69, 67), (67, 68), (69, 68), (67, 69), (68, 69), (69, 69) })
            { map.Ore[map.Idx(x, y)] = 100; map.OreBase[map.Idx(x, y)] = 100; }
            var seat0Tile = map.NearestOre(new Vec2(11.5f, 11.5f), 10, null, -1, new Vec2(9.5f, 9.5f));
            var seat1Tile = map.NearestOre(new Vec2(68.5f, 68.5f), 10, null, -1, new Vec2(70.5f, 70.5f));
            Check(seat0Tile.HasValue && seat1Tile.HasValue && seat1Tile.Value.X == 79 - seat0Tile.Value.X && seat1Tile.Value.Y == 79 - seat0Tile.Value.Y,
                  $"a truck on a mined-out tile turns to the equally near tile the mirrored truck does ({seat0Tile} and {seat1Tile}, not the lowest-numbered one)");
            var open = new World(2, 1, 80);
            Array.Clear(open.Map.Tiles, 0, open.Map.Tiles.Length);
            foreach (var (x, y) in new[] { (20, 20), (59, 59) }) open.Map.Tiles[open.Map.Idx(x, y)] = Terrain.Rock;
            var p0 = open.Paths.NearestPassable(new Int2(20, 20)); var p1 = open.Paths.NearestPassable(new Int2(59, 59));
            Check(p1.X == 79 - p0.X && p1.Y == 79 - p0.Y, $"the nearest open tile to a blocked spot is mirrored too ({p0} and {p1})");

            // A route between two points and the route between their half-turned images: of equally short ways round the rock, one
            // was taken by scan order (the first found), so the seat at one end of the map went round by the left and the other by the
            // right, and the two arrived ticks apart. They are searched in an order that turns with the problem now.
            var rocky = new World(2, 1, 80);
            Array.Clear(rocky.Map.Tiles, 0, rocky.Map.Tiles.Length);
            var layout = new Random(5);
            for (int k = 0; k < 14; k++)
            {
                int rx = layout.Next(12, 60), ry = layout.Next(12, 60), len = layout.Next(2, 9); bool across = layout.Next(2) == 0;
                for (int i = 0; i < len; i++)
                {
                    int tx = across ? rx + i : rx, ty = across ? ry : ry + i;
                    rocky.Map.Tiles[rocky.Map.Idx(tx, ty)] = Terrain.Rock; rocky.Map.Tiles[rocky.Map.Idx(79 - tx, 79 - ty)] = Terrain.Rock;
                }
            }
            int routes = 0, parted = 0;
            for (int k = 0; k < 200; k++)
            {
                var from = new Vec2(layout.Next(4, 76) + 0.5f, layout.Next(4, 76) + 0.5f); var to = new Vec2(layout.Next(4, 76) + 0.5f, layout.Next(4, 76) + 0.5f);
                Vec2 Turn(Vec2 v) => new Vec2(80f - v.X, 80f - v.Y);
                if (!rocky.Map.Passable((int)from.X, (int)from.Y) || !rocky.Map.Passable((int)to.X, (int)to.Y)) continue;
                var route = rocky.Paths.Find(from, to); var twin = rocky.Paths.Find(Turn(from), Turn(to));
                if (route == null || twin == null) continue;
                routes++;
                if (route.Count != twin.Count || route.Zip(twin, (a, b) => Vec2.Dist(Turn(a), b)).Any(d => d > 1e-3f)) parted++;
            }
            Check(routes >= 150 && parted * 100 <= routes * 15, $"a route and its half-turned twin go the same way round the rock ({parted} of {routes} differ; it was a third)");

            var (mw, _) = (MirroredBases(), 0);
            foreach (var d in mw.Derricks.ToList()) mw.Remove(d);
            var da = mw.SpawnStructure(-1, "derrick", new Int2(32, 46), 1f); var db = mw.SpawnStructure(-1, "derrick", new Int2(46, 32), 1f); // equally far from both seats
            var goals = new Vec2[2]; var captures = new Vec2[2];
            for (int t = 0; t < 2; t++)
            {
                mw.SpawnStructure(t, "barracks", mw.FindPlacement(t, "barracks").Value, 1f);
                var eng = mw.SpawnUnit(t, "engineer", mw.Owned(t).First(e => e.Def.Key == "command_center")); eng.Order = Order.Idle;
                var army = Enumerable.Range(0, 6).Select(_ => mw.SpawnUnit(t, "rifleman", mw.Owned(t).First(e => e.Def.Key == "command_center"))).ToList();
                foreach (var u in army) { u.Order = Order.Idle; }
                new SimpleAI(t).Update(mw);
                var target = mw.Get(eng.TargetId);
                captures[t] = eng.Order == Order.Capture && target != null ? target.Center : new Vec2(-1, -1);
                goals[t] = army[0].Order == Order.AttackMove ? army[0].OrderPos : new Vec2(-1, -1);
            }
            Check(Vec2.Dist(captures[1], new Vec2(80f - captures[0].X, 80f - captures[0].Y)) < 0.5f && captures[0].X > 0 && Vec2.Dist(captures[0], captures[1]) > 5f,
                  $"the two scripted AIs send their engineers to different, mirrored derricks ({captures[0]} and {captures[1]})");
            Check(Vec2.Dist(goals[1], new Vec2(80f - goals[0].X, 80f - goals[0].Y)) < 0.5f && goals[0].X > 0 && Vec2.Dist(goals[0], goals[1]) > 5f,
                  $"and their first waves to different, mirrored corners ({goals[0]} and {goals[1]})");

            // A friendly in a moving vehicle's way steps aside to the side it is on, not to the side its id's parity picks.
            var (stepped, steppedTurned, stood) = SteppingAside();
            Check(Vec2.Dist(stepped, stood) > 0.8f && stepped.Y > stood.Y - 0.01f && Vec2.Dist(stepped, steppedTurned) < 0.3f,
                  $"a rifleman in a friendly tank's way steps to the side he is on, and so does his half-turned twin (to {stepped}, and {steppedTurned} turned back; he stood at {stood})");

            // Fights. Two equal laser troopers in range of each other trade volleys every 1.4 s, and the one that acts first in the tick
            // lands the killing shot before the other's turn, so which seat wins is whose turn comes first. The turns alternate with the
            // tick: start the same fight a tick apart and the other seat must win.
            int a0 = Stationary(8), a1 = Stationary(9);
            Check(a0 >= 0 && a1 >= 0 && a0 != a1, $"two equal troopers in a stand-off: seat {a0} wins when the first volley falls on tick 8 and seat {a1} on tick 9, not the same seat's every time");

            // New units stand a little off the middle of their doorway, by a scatter. It was drawn from one stream shared by the teams
            // that began from the same place every game, so seat 0 always got the same luck (its truck on the near side of the
            // ore, say) and seat 1 the next, and later units went to whichever team's came first in the tick; now each team draws
            // from its own stream, measured along the door and across it, so its n-th unit is scattered as every other team's n-th is.
            int apartGames = 0, scattered = 0; float widest = 0f;
            for (int seed = 1; seed <= 20; seed++)
            {
                var w = new World(2, seed, 80);
                var hqs = new[] { 0, 1 }.Select(t => w.Owned(t).First(e => e.Def.Key == "command_center")).ToArray();
                // How far off the middle of its tile each unit stands, along its door and across it.
                List<float> Offsets(int t, IEnumerable<Entity> units)
                {
                    var door = new Vec2(MathF.Round(MathF.Cos(w.DoorFacing(hqs[t]))), MathF.Round(MathF.Sin(w.DoorFacing(hqs[t]))));
                    var list = new List<float>();
                    foreach (var u in units)
                    {
                        var v = u.Pos - hqs[t].Center;
                        float along = v.X * door.X + v.Y * door.Y, abeam = v.X * -door.Y + v.Y * door.X;
                        list.Add(along - MathF.Round(along)); list.Add(abeam - MathF.Round(abeam));
                    }
                    return list;
                }
                var seen = new List<float>[2] { Offsets(0, w.Owned(0).Where(e => !e.IsStructure).ToList()), Offsets(1, w.Owned(1).Where(e => !e.IsStructure).ToList()) };
                // ...and the units that come later, seat 1's first each round: each seat's n-th is scattered like the other's n-th.
                var later = new List<Entity>[2] { new List<Entity>(), new List<Entity>() };
                for (int round = 0; round < 3; round++)
                    for (int t = 1; t >= 0; t--) later[t].Add(w.SpawnUnit(t, "rifleman", hqs[t]));
                seen[0].AddRange(Offsets(0, later[0])); seen[1].AddRange(Offsets(1, later[1]));
                float apart = seen[0].Zip(seen[1], (a, b) => MathF.Abs(a - b)).Max();
                widest = MathF.Max(widest, apart);
                if (apart > 1e-3f) apartGames++;
                if (seen[0].Any(x => MathF.Abs(x) > 0.02f)) scattered++;
            }
            Check(apartGames == 0 && scattered >= 19, $"both seats' units, the opening ones and later ones, are scattered alike about their door ({apartGames} of 20 games differ, by up to {widest:0.000} tiles; {scattered} of 20 are scattered at all)");
        }

        /// <summary>
        /// A tank driving through a friendly rifleman standing a little to one side of its line, and the same thing turned half a circle
        /// (the rifleman's id one apart in parity, which used to choose the side it stepped to): where each rifleman ends up.
        /// </summary>
        static (Vec2 made, Vec2 turned, Vec2 start) SteppingAside()
        {
            var w = new World(2, 1, 80);
            var m = w.Map;
            Array.Clear(m.Tiles, 0, m.Tiles.Length); Array.Clear(m.Ore, 0, m.Ore.Length); Array.Clear(m.OreBase, 0, m.OreBase.Length);
            m.Deep.Clear(); m.FieldsChanged();
            foreach (var u in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(u);
            var hq0 = w.Owned(0).First(e => e.IsStructure); var hq1 = w.Owned(1).First(e => e.IsStructure);
            Entity At(Entity e, Vec2 p) { e.Pos = e.PrevPos = e.GuardPos = p; return e; }
            var tank0 = At(w.SpawnUnit(0, "heavy_tank", hq0), new Vec2(30f, 40f));
            var foot0 = At(w.SpawnUnit(0, "rifleman", hq0), new Vec2(34f, 40.25f));
            w.Remove(w.SpawnUnit(0, "rifleman", hq0)); // (so the two riflemen's ids differ in parity)
            var tank1 = At(w.SpawnUnit(1, "heavy_tank", hq1), new Vec2(50f, 40f));
            var foot1 = At(w.SpawnUnit(1, "rifleman", hq1), new Vec2(46f, 39.75f));
            w.SetOrder(tank0, Order.Move, new Vec2(50f, 40f)); w.SetOrder(tank1, Order.Move, new Vec2(30f, 40f));
            w.SetOrder(foot0, Order.Idle, foot0.Pos); w.SetOrder(foot1, Order.Idle, foot1.Pos);
            for (int i = 0; i < 6 * World.TickRate; i++) w.Step();
            return (foot0.Pos, new Vec2(80f - foot1.Pos.X, 80f - foot1.Pos.Y), new Vec2(34f, 40.25f));
        }

        /// <summary>
        /// The survivor (0 or 1; -1 if neither) of two equal laser troopers standing 4 tiles apart on open ground, both ready to fire at once
        /// (`delayTicks` later, to put the first volley on an odd or an even tick). Their volleys come at the same ticks every time round,
        /// so whoever acts first in the tick has the last word.
        /// </summary>
        static int Stationary(int delayTicks)
        {
            var w = new World(2, 1, 80);
            var m = w.Map;
            Array.Clear(m.Tiles, 0, m.Tiles.Length); Array.Clear(m.Ore, 0, m.Ore.Length); Array.Clear(m.OreBase, 0, m.OreBase.Length);
            m.Deep.Clear(); m.FieldsChanged();
            foreach (var u in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(u);
            var a = w.SpawnUnit(0, "laser_trooper", w.Owned(0).First(e => e.IsStructure));
            var b = w.SpawnUnit(1, "laser_trooper", w.Owned(1).First(e => e.IsStructure));
            a.Pos = a.PrevPos = a.GuardPos = new Vec2(38f, 40f); a.Facing = a.TurretFacing = 0;
            b.Pos = b.PrevPos = b.GuardPos = new Vec2(42f, 40f); b.Facing = b.TurretFacing = MathF.PI;
            w.SetOrder(a, Order.Idle, a.Pos); w.SetOrder(b, Order.Idle, b.Pos);
            a.Cooldown = b.Cooldown = delayTicks * World.Dt;
            for (int i = 0; i < 30 * World.TickRate && !a.Dead && !b.Dead; i++) w.Step();
            return b.Dead && !a.Dead ? 0 : a.Dead && !b.Dead ? 1 : -1;
        }

        /// <summary>A world with open ground round seat 0's command center, so every side of it has a clear lane.</summary>
        static (World w, Entity hq) OpenBase()
        {
            var w = new World(2, 7, 80);
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            for (int y = hq.Origin.Y - 8; y <= hq.Origin.Y + 11; y++)
                for (int x = hq.Origin.X - 8; x <= hq.Origin.X + 11; x++)
                    if (w.Map.InBounds(x, y) && w.Map.Occupant[w.Map.Idx(x, y)] == 0) { w.Map.Tiles[w.Map.Idx(x, y)] = Terrain.Grass; w.Map.Ore[w.Map.Idx(x, y)] = 0; }
            return (w, hq);
        }

        /// <summary>
        /// Only friendly buildings are kept off a truck lane, so an enemy can build on one; the sim then moves that bay to the next clear
        /// side (as it always has for blocked lanes). Everything that shows the door follows it: where units appear, the spectator feed's
        /// facing for the building (the browser viewers turn the model by it every frame), and back again when the blocker goes.
        /// </summary>
        static void BayFollowsABlockedLane()
        {
            var (w, hq) = OpenBase();
            hq.BaySide = 3; // north
            float north = MathF.PI / 2;
            float FeedFacing() { var row = ((List<object>)D(ApiServer.ViewFrame(w, -1))["entities"]).Cast<List<object>>().First(r => Convert.ToInt32(r[0]) == hq.Id); return Convert.ToSingle(row[6]); }
            Check(MathF.Abs(w.Bay(hq).facing - north) < 0.001f && MathF.Abs(FeedFacing() - north) < 0.01f, $"a north bay opens north and the spectator feed says so ({FeedFacing():0.00})");

            var (bay, head, _, _) = w.Bay(hq);
            var onLane = Int2.Of((bay + head) * 0.5f);
            var blocker = w.SpawnStructure(1, "power_plant", onLane, 1f); // an enemy building up on the lane
            var (_, _, now, ok) = w.Bay(hq);
            var truck = w.SpawnUnit(0, "mining_truck", hq);
            var off = truck.Pos - hq.Center;
            Check(ok && MathF.Cos(now - north) < 0.99f && MathF.Abs(w.DoorFacing(hq) - now) < 0.001f && MathF.Abs(FeedFacing() - now) < 0.01f,
                  $"with an enemy building on its lane the bay moves to a clear side ({now * 180 / MathF.PI:0} degrees) and the feed follows ({FeedFacing() * 180 / MathF.PI:0})");
            var dir = new Vec2(MathF.Round(MathF.Cos(now)), MathF.Round(MathF.Sin(now)));
            Check(off.X * dir.X + off.Y * dir.Y > 1.5f && MathF.Cos(truck.Facing - now) > 0.99f, $"and new units appear on that side, facing out (a truck {off} from the centre)");

            w.Remove(blocker);
            Check(MathF.Abs(w.Bay(hq).facing - north) < 0.001f && MathF.Abs(FeedFacing() - north) < 0.01f, "when the blocker is gone the bay is back on its own side, and the feed with it");
        }

        /// <summary>New ground units appear at their producer's door: a drop-off's is on its bay side (the view turns the model to match), and they head out from it.</summary>
        static void UnitsLeaveByTheDoor()
        {
            var (w, hq) = OpenBase();
            var sides = new[] { (side: 0, name: "south", dx: 0, dy: -1), (side: 1, name: "east", dx: 1, dy: 0), (side: 2, name: "west", dx: -1, dy: 0), (side: 3, name: "north", dx: 0, dy: 1) };
            var wrong = new List<string>();
            foreach (var s in sides)
            {
                hq.BaySide = s.side;
                var truck = w.SpawnUnit(0, "mining_truck", hq);
                var rifle = w.SpawnUnit(0, "rifleman", hq);
                foreach (var u in new[] { truck, rifle })
                {
                    var off = u.Pos - hq.Center;
                    float along = off.X * s.dx + off.Y * s.dy, sideways = Math.Abs(off.X * s.dy) + Math.Abs(off.Y * s.dx);
                    // Just outside the wall on that side (the footprint is 3x3: 1.5 to the wall), near the middle of it, facing out.
                    bool placed = along > 1.5f && along < 3.2f && sideways < 1f && MathF.Cos(u.Facing - new Vec2(s.dx, s.dy).Angle) > 0.99f;
                    if (!placed) wrong.Add($"{u.Def.Key} from the {s.name} bay at {off} facing {u.Facing:0.00}");
                }
                // A unit with no rally point rolls a few tiles on, away from the door (its first move, which was always south).
                var rolled = rifle.OrderPos - rifle.Pos;
                if (rifle.Order != Order.Move || rolled.X * s.dx + rolled.Y * s.dy < 1.5f) wrong.Add($"rifleman from the {s.name} bay rolls {rolled} ({rifle.OrderName})");
            }
            Check(wrong.Count == 0, "a command center's new units appear on its bay side, facing out, and roll on away from the door" + (wrong.Count > 0 ? $" (not: {string.Join("; ", wrong)})" : ""));

            // A barracks' door faces the middle of the map (the axis it is farthest from the middle on), and its recruits come out there.
            var rax = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
            var recruit = w.SpawnUnit(0, "rifleman", rax);
            var toMiddle = new Vec2(w.Map.W / 2f, w.Map.H / 2f) - rax.Center;
            var wantDir = MathF.Abs(toMiddle.X) > MathF.Abs(toMiddle.Y) ? new Vec2(MathF.Sign(toMiddle.X), 0) : new Vec2(0, MathF.Sign(toMiddle.Y));
            var out_ = recruit.Pos - rax.Center;
            Check(MathF.Cos(w.DoorFacing(rax) - wantDir.Angle) > 0.99f && out_.X * wantDir.X + out_.Y * wantDir.Y > 1f && MathF.Cos(recruit.Facing - wantDir.Angle) > 0.99f,
                  $"a barracks' door faces the middle of the map and its recruits come out there (a recruit at {out_}, door {w.DoorFacing(rax) * 180 / MathF.PI:0} degrees)");
        }

        static void SymmetricPlacement()
        {
            var w = MirroredBases();
            var keys = new[] { "power_plant", "mining_refinery", "barracks", "factory" };
            var placed = new Dictionary<string, Entity>[2] { new Dictionary<string, Entity>(), new Dictionary<string, Entity>() };
            foreach (var key in keys)
                for (int t = 0; t < 2; t++)
                {
                    var spot = w.FindPlacement(t, key);
                    if (spot.HasValue) placed[t][key] = w.SpawnStructure(t, key, spot.Value, 1f);
                }
            Check(keys.All(k => placed[0].ContainsKey(k) && placed[1].ContainsKey(k)), "both seats find a spot for a power plant, refinery, barracks and factory");
            if (!keys.All(k => placed[0].ContainsKey(k) && placed[1].ContainsKey(k))) return;
            var line = new List<string>();
            foreach (var key in keys)
            {
                var a = placed[0][key]; var b = placed[1][key];
                var turned = new Vec2(80 - a.Center.X, 80 - a.Center.Y); // seat 0's spot turned about the middle of the two bases
                bool mirrored = Vec2.Dist(turned, b.Center) < 0.01f;
                // A door mirrors too (a drop-off's bay, a barracks' or factory's door): it faces the opposite way.
                bool bayMirrored = !World.HasDoorSide(a.Def) || MathF.Cos(w.DoorFacing(a) + MathF.PI - w.DoorFacing(b)) > 0.99f;
                line.Add($"{key} {a.Origin}/{b.Origin}");
                Check(mirrored && bayMirrored, $"on a mirrored map both seats' {key} are mirror images (seat 0 at {a.Origin}, seat 1 at {b.Origin}{(World.HasDoorSide(a.Def) ? $", doors {a.BaySide} and {b.BaySide}" : "")})");
            }
            Console.WriteLine($"      seat 0 / seat 1 origins: {string.Join(", ", line)}");
            Check(new[] { 0, 1 }.All(t => ToHomeIron(w, t, w.Bay(placed[t]["mining_refinery"]).head) <= 1f),
                  $"each refinery's lane opens right at the iron field, for trucks' short trips ({ToHomeIron(w, 0, w.Bay(placed[0]["mining_refinery"]).head):0.0} and {ToHomeIron(w, 1, w.Bay(placed[1]["mining_refinery"]).head):0.0} tiles)");

            // Deterministic (a resumed game and the replay depend on it): asking again, or in an identical world, gives the same spot.
            var w1 = MirroredBases(); var w2 = MirroredBases();
            var every = new[] { "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant", "gun_turret" };
            Check(new[] { 0, 1 }.All(t => every.All(k => Equals(w1.FindPlacement(t, k), w2.FindPlacement(t, k)) && Equals(w1.FindPlacement(t, k), w1.FindPlacement(t, k)))),
                  "the same world always gives the same spots");

            // A commander who leaves out x and y gets the same placement as the scripted AI.
            var w3 = MirroredBases();
            var heads = new float[2];
            for (int t = 0; t < 2; t++)
            {
                w3.SpawnStructure(t, "power_plant", w3.FindPlacement(t, "power_plant").Value, 1f);
                w3.Teams[t].Add("iron_ore", 500); w3.Teams[t].Add("copper_ore", 200);
                var r = Commands.Execute(w3, t, Cmd("type", "build", "structure", "mining_refinery"));
                var made = Ok(r) ? w3.Get(Convert.ToInt32(r["id"])) : null;
                heads[t] = made != null ? ToHomeIron(w3, t, w3.Bay(made).head) : 99f;
            }
            Check(heads[0] <= 1f && heads[1] <= 1f, $"a build command with no x,y puts the refinery's lane at the iron too ({heads[0]:0.0} and {heads[1]:0.0} tiles)");

            // The scripted AI's build order up to its second refinery. A spot beside the ore can lie a few rings beyond the first
            // ring with any legal spot (seat 0's lane opened 6 tiles from the iron, when one at the iron was legal 10 tiles out), so
            // a drop-off's search runs on until nothing further out can score better.
            var w4 = MirroredBases();
            var second = new float[2];
            for (int t = 0; t < 2; t++)
                foreach (var key in new[] { "power_plant", "mining_refinery", "barracks", "factory", "power_plant", "electronics_plant", "gun_turret", "mining_refinery" })
                {
                    var spot = w4.FindPlacement(t, key);
                    if (!spot.HasValue) { second[t] = 99f; break; }
                    var built = w4.SpawnStructure(t, key, spot.Value, 1f);
                    if (key == "mining_refinery") second[t] = ToHomeIron(w4, t, w4.Bay(built).head);
                }
            Check(second[0] <= 4f && second[1] <= 4f, $"after the build order up to its second refinery, each seat's second lane still opens close to the iron ({second[0]:0.0} and {second[1]:0.0} tiles)");

            // Real maps are not mirrored, but every seat's first refinery still opens close to its iron (it was 12.5 tiles for seat 0 on every map).
            var far = new List<string>();
            foreach (var (seed, size) in new[] { (1, 80), (2, 80), (3, 80), (4, 96), (5, 128), (6, 112) })
            {
                var g = new World(2, seed, size);
                for (int t = 0; t < 2; t++)
                {
                    g.SpawnStructure(t, "power_plant", g.FindPlacement(t, "power_plant").Value, 1f);
                    var spot = g.FindPlacement(t, "mining_refinery");
                    float d = spot.HasValue ? ToHomeIron(g, t, g.Bay(g.SpawnStructure(t, "mining_refinery", spot.Value, 1f)).head) : 99f;
                    if (d > 2f) far.Add($"seed {seed} size {size} seat {t}: {d:0.0}");
                }
            }
            Check(far.Count == 0, "on generated maps each seat's first refinery opens within 2 tiles of its iron" + (far.Count > 0 ? $" (not: {string.Join("; ", far)})" : ""));
        }

        static void MatchClock()
        {
            // A short match: sudden death at 15 minutes, decay at 45, the end at 75.
            var g = new Game(new GameConfig { Seed = 3, MapSize = 80, Open = true, MatchHours = 0.25f, Controllers = new[] { "llm", "llm" } });
            var w = g.World;
            // Both seats have agents at the controls: a seat nobody plays is resigned after 20 minutes idle (World.CheckIdle).
            void Play(float seconds) { for (float s = 0; s < seconds; s += 60) { Step(g, Math.Min(60, seconds - s)); foreach (var t in g.World.Teams) t.LastActiveAt = g.World.Time; } }
            Check(w.SuddenDeathAt == 900 && w.Phase == MatchPhase.Normal && D(StateView.MatchJson(w)).Str("phase") == "normal" && (int)D(StateView.MatchJson(w)).Num("ends_in_s") == 4500,
                  $"a new game has a match clock: sudden death at hour N (here 0.25 h), the end an hour later ({StateView.MatchText(w)})");
            var t0 = w.Teams[0];
            t0.Stats.KillValue = 500; t0.Stats.OreMined = 900; w.Teams[1].Stats.OreMined = 100;
            Play(301);
            int warned = w.Events.Count(e => e.Type == "chat" && e.Text.Contains("Sudden death in 10 minutes"));
            bool alerted = w.Alerts.Active(w, 1).Any(a => a.Kind == "match_clock" && a.Priority == Priority.High);
            Play(540);
            warned += w.Events.Count(e => e.Type == "chat" && e.Text.Contains("Sudden death in 1 minute"));
            Check(warned == 2 && alerted, $"sudden death is announced 10 minutes and 1 minute ahead, in the chat and as a MATCH CLOCK alert ({warned} warnings)");
            Play(60);
            int regrownBefore = w.Regrown;
            for (int i = 0; i < w.Map.Ore.Length; i++) if (w.Map.OreBase[i] > 0) w.Map.Ore[i] = 0;
            Play(60);
            Check(w.Phase == MatchPhase.SuddenDeath && w.Regrown == regrownBefore && w.Events.Any(e => e.Type == "chat" && e.Text.Contains("SUDDEN DEATH")),
                  $"at sudden death ore stops regrowing ({w.Regrown - regrownBefore} regrown after it)");
            var hq = w.Owned(1).First(e => e.Def.Key == "command_center");
            Play(1800);
            float hp = hq.Hp;
            Play(600);
            Check(w.Phase == MatchPhase.Decay && hq.Hp < hp - hq.Def.MaxHp * 0.17f && hq.Hp > hp - hq.Def.MaxHp * 0.19f && w.Events.Any(e => e.Text != null && e.Text.Contains("DECAY")),
                  $"30 minutes on, structures decay: 0.03% of max health a second ({hp:0} -> {hq.Hp:0} in 10 minutes)");
            var state = D(StateData.Team(w, 0));
            Check(state.Obj("match")?.Str("phase") == "decay" && state.Obj("match").Num("next_phase_in_s") > 0, "state shows the phase and how long until the next one");
            Play(1200);
            Check(w.GameOver && w.MatchOver && w.Winner == 0 && w.Events.Any(e => e.Type == "game_over" && e.Text.Contains("wins the match on points")),
                  $"an hour after sudden death the match ends with a score victory ({w.Events.LastOrDefault(e => e.Type == "game_over")?.Text})");
            var m = D(StateView.MatchJson(w));
            var scores = (List<object>)m["scores"];
            Check(m.Str("phase") == "ended" && m.Str("winner") == t0.Name && scores.Count == 2 && m["how_scored"] != null && m.Num("new_match_in_s") > 100,
                  $"the results stay in state: winner, scores with their components, and when the next match starts ({StateView.MatchText(w)})");
            var r = Commands.Execute(w, 0, Cmd("type", "say", "text", "gg"));
            Check(!Ok(r), "nobody can act once the match is over");
            // The results stay up for 3 minutes (saved with the game), then a new match starts by itself.
            Step(g, 60);
            var mid = Resume(Snap(g));
            Check(mid.World.MatchOver && Math.Abs(mid.RestartIn - g.RestartIn) < 0.01f, $"the results countdown survives a restart ({mid.RestartIn:0}s left)");
            var oldWorld = w;
            int seed = g.Config.Seed;
            bool restarted = false;
            g.Restarted = () => restarted = true;
            Step(g, 125);
            Check(restarted && g.World != oldWorld && !g.World.GameOver && g.Config.Seed != seed && g.World.SuddenDeathAt == 900 && g.World.Phase == MatchPhase.Normal &&
                  g.World.Events.Any(e => e.Type == "chat" && e.Text.Contains("new match")),
                  "three minutes after the results, the open arena starts a fresh match on a new map, without anyone stepping in");
            bool savedLlm = mid.Config.Controllers.All(x => x == "llm");
            mid.NextMatch();
            Check(savedLlm && mid.World.Teams.Count == 1 && mid.World.Teams[0].Controller == "ai" && mid.World.Teams[0].House,
                  $"a game resumed from a save with \"llm\" seats starts its next match with only the house AI ({string.Join(",", mid.World.Teams.Select(t => t.Controller))})");

            // A closed match ends on points too, and simply stays over.
            var c = new Game(new GameConfig { Seed = 4, MapSize = 64, MatchHours = 0.05f, Controllers = new[] { "llm", "llm" } });
            Step(c, 3 * 60 + 3605);
            Step(c, 300);
            Check(c.World.GameOver && c.World.MatchOver && c.RestartIn < 0, "a closed match ends on points and stays over (no automatic next match)");

            // A game that's already past hour N when the clock arrives (room 1 after 8 hours): sudden death 30 minutes from
            // the upgrade, not instantly, with the warnings.
            var live = new Game(new GameConfig { Seed = 6, MapSize = 80, Open = true, Controllers = new[] { "llm", "llm" } });
            Step(live, 5);
            var root = (Dictionary<string, object>)Json.Parse(Snap(live));
            var wd = (Dictionary<string, object>)root["world"];
            wd.Remove("sudden_death_at");
            wd["tick"] = 8.0 * 3600 * World.TickRate;
            ((Dictionary<string, object>)root["config"]).Remove("match_hours");
            var up = Resume(Json.Write(root));
            var uw = up.World;
            Check(Math.Abs(uw.SuddenDeathAt - (uw.Time + 1800)) < 0.01f && uw.Phase == MatchPhase.Normal && up.Config.MatchHours == 4,
                  $"a game resumed past hour N from before the clock gets sudden death 30 minutes later, not at once ({StateView.MatchText(uw)})");
            Step(up, 1201);
            bool early = uw.Events.Any(e => e.Type == "chat" && e.Text.Contains("Sudden death in 10 minutes"));
            Step(up, 600);
            Check(early && uw.Phase == MatchPhase.SuddenDeath && !uw.GameOver, "and its players get the 10-minute warning before it begins");
            var fresh = new Game(new GameConfig { Seed = 6, MapSize = 80, Open = true, Controllers = new[] { "llm", "llm" } });
            Step(fresh, 5);
            var root2 = (Dictionary<string, object>)Json.Parse(Snap(fresh));
            ((Dictionary<string, object>)root2["world"]).Remove("sudden_death_at");
            var early2 = Resume(Json.Write(root2));
            Check(early2.World.SuddenDeathAt == 4 * 3600, "a young game from before the clock gets the usual hour N");
        }

        /// <summary>A fresh world with `buildings` of one producer (and power to run them, or a load that puts it in low power), `unit` kept queued 10+ deep for `seconds`: how many came out.</summary>
        static int TrainedWith(string producer, int buildings, string unit, float seconds, bool power, out bool lowPower)
        {
            var w = new World(2, 7, 96);
            var t = w.Teams[0];
            for (int i = 0; i < 3; i++) w.SpawnStructure(0, power ? "power_plant" : "electronics_plant", w.FindPlacement(0, power ? "power_plant" : "electronics_plant").Value, 1f);
            // The team's own command center is the first; a second one goes on open ground.
            for (int i = producer == "command_center" ? 1 : 0; i < buildings; i++)
                w.SpawnStructure(0, producer, producer == "command_center" ? Int2.Of(OpenGround(w)) : w.FindPlacement(0, producer).Value, 1f);
            w.Step();
            var queue = t.UnitQueues[Defs.Get(unit).BuiltBy];
            for (int s = 0; s < seconds; s++)
            {
                foreach (var item in new[] { "steel", "copper", "circuits", "plasma", "iron_ore" }) t.Stock[item] = 1e6f;
                if (queue.Count < 10) Commands.Execute(w, 0, Cmd("type", "train", "unit", unit, "count", 10));
                Run(w, 1);
            }
            lowPower = t.LowPower;
            return t.Stats.Built.TryGetValue(unit, out var n) ? n : 0;
        }

        /// <summary>Every completed barracks, factory, airfield and command center works its own unit of the shared queue.</summary>
        static void ParallelProduction()
        {
            foreach (var (producer, unit) in new[] { ("barracks", "rifleman"), ("factory", "light_tank"), ("airfield", "transport_chopper"), ("command_center", "mining_truck") })
            {
                int one = TrainedWith(producer, 1, unit, 60, true, out _);
                int two = TrainedWith(producer, 2, unit, 60, true, out _);
                int lowTwo = TrainedWith(producer, 2, unit, 60, false, out var low);
                Check(one >= 4 && Math.Abs(two - 2 * one) <= 1, $"two {producer} buildings train {unit} twice as fast as one ({two} against {one} in a minute)");
                Check(low && Math.Abs(lowTwo - one) <= 1, $"low power halves each of two {producer} buildings ({lowTwo} {unit} in a minute, the same as one at full power: {one})");
            }
            {
                int three = TrainedWith("barracks", 3, "rifleman", 60, true, out _), one = TrainedWith("barracks", 1, "rifleman", 60, true, out _);
                Check(Math.Abs(three - 3 * one) <= 1, $"and 3 barracks are three times as fast ({three} against {one})");
            }

            // A queued item with a structure_id is built at that building only, and waits for it while the other one sits idle.
            {
                var w = new World(2, 7, 80);
                var t = w.Teams[0];
                for (int i = 0; i < 3; i++) w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
                var b1 = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
                var b2 = w.SpawnStructure(0, "barracks", new Int2(40, 40), 1f);
                t.Add("steel", 5000);
                var q = t.UnitQueues[Producer.Barracks];
                var r = Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "count", 3, "structure_id", b2.Id));
                bool strayed = false;
                for (int i = 0; i < 12.5f * World.TickRate; i++) { w.Step(); strayed |= q.Any(p => p.WorkerId == b1.Id); }
                var trained = w.Events.Where(e => e.Type == "trained").ToList();
                Check(Ok(r) && !strayed && trained.Count == 3 && trained.All(e => Vec2.Dist(e.Pos, b2.Center) < Vec2.Dist(e.Pos, b1.Center)),
                      $"units asked for barracks #{b2.Id} come out of it one at a time while #{b1.Id} sits idle ({trained.Count} in 12.5 s; {Said(r)})");
                Check(Said(r).Contains("2 barracks buildings") && Said(r).Contains("one at a time"), $"the train reply says how many buildings share the queue: {Said(r)}");

                // Mixed with unpinned units the other barracks works through those meanwhile: six units take 12 s, not 24.
                w = new World(2, 7, 80);
                t = w.Teams[0];
                for (int i = 0; i < 3; i++) w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
                b1 = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
                b2 = w.SpawnStructure(0, "barracks", new Int2(40, 40), 1f);
                t.Add("steel", 5000);
                q = t.UnitQueues[Producer.Barracks];
                Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "count", 3));
                Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "count", 3, "structure_id", b2.Id));
                strayed = false;
                for (int i = 0; i < 12.5f * World.TickRate; i++) { w.Step(); strayed |= q.Any(p => p.StructureId == b2.Id && p.WorkerId == b1.Id); }
                trained = w.Events.Where(e => e.Type == "trained").ToList();
                int at1 = trained.Count(e => Vec2.Dist(e.Pos, b1.Center) < Vec2.Dist(e.Pos, b2.Center));
                Check(!strayed && trained.Count == 6 && at1 == 3, $"unpinned units fill the other building: 3 out of each barracks in 12 s ({trained.Count} trained, {at1} at #{b1.Id}, never a pinned one there: {!strayed})");
            }

            // Cancel still refunds what was paid, working or waiting, and a freed building goes to the next unit waiting.
            {
                var w = new World(2, 7, 80);
                var t = w.Teams[0];
                for (int i = 0; i < 3; i++) w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
                var b1 = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
                var b2 = w.SpawnStructure(0, "barracks", new Int2(40, 40), 1f);
                t.Stock["steel"] = 1000; t.Stock["copper"] = 1000;
                var q = t.UnitQueues[Producer.Barracks];
                Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "count", 1));
                Commands.Execute(w, 0, Cmd("type", "train", "unit", "rocket_soldier", "count", 1));
                Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "count", 1));
                Run(w, 1);
                Check(q.Count == 3 && q[0].WorkerId != 0 && q[1].WorkerId != 0 && q[0].WorkerId != q[1].WorkerId && q[2].WorkerId == 0 && q[1].Progress > 0 && q[2].Progress == 0,
                      $"two units are in progress at once and the third waits ({string.Join(", ", q.Select(p => $"{p.Key} {p.Progress:0.0}s at #{p.WorkerId}"))})");
                int busy = q[1].WorkerId;
                var gone = Commands.Execute(w, 0, Cmd("type", "cancel", "unit", "rocket_soldier"));
                Run(w, 0.1f);
                Check(Ok(gone) && t.Amount("steel") == 1000 - 40 - 80 - 40 + 80 && t.Amount("copper") == 1000 && q.Count == 2 && q[1].WorkerId == busy,
                      $"cancelling a unit that was being built refunds its full price and its building goes to the one waiting (steel {t.Amount("steel")}, copper {t.Amount("copper")}; {Said(gone)})");
                Commands.Execute(w, 0, Cmd("type", "cancel", "unit", "rifleman"));
                Commands.Execute(w, 0, Cmd("type", "cancel", "unit", "rifleman"));
                Check(q.Count == 0 && t.Amount("steel") == 1000 && t.Amount("copper") == 1000, $"cancelling the rest refunds everything paid (steel {t.Amount("steel")}, copper {t.Amount("copper")})");
            }

            // State shows which building is working on what; a building lost mid-unit hands its unit on with the progress it had.
            {
                var w = new World(2, 7, 80);
                var t = w.Teams[0];
                for (int i = 0; i < 3; i++) w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
                var b1 = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
                var b2 = w.SpawnStructure(0, "barracks", new Int2(40, 40), 1f);
                t.Add("steel", 5000);
                var r = Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "count", 3));
                Run(w, 2);
                var prod = (JObj)StateView.TeamState(w, 0)["production"];
                var barracks = (List<string>)prod["barracks"];
                Check(barracks.Count == 3 && barracks.Count(l => l.Contains("50% at barracks #")) == 2 && barracks.Any(l => l.Contains($"at barracks #{b1.Id}")) && barracks.Any(l => l.Contains($"at barracks #{b2.Id}")) &&
                      barracks[2].Contains("waiting for a free barracks"), $"state lists each unit with the building working on it: {string.Join(" | ", barracks)}");
                var json = D(StateData.Team(w, 0));
                var items = ((Dictionary<string, object>)json["production"])["barracks"] as List<object>;
                var working = items.Cast<Dictionary<string, object>>().Select(i => i["working_at"]).ToList();
                Check(working.Count == 3 && working[0] is double && working[1] is double && (double)working[0] != (double)working[1] && working[2] == null, "and so does the JSON state (working_at)");

                var viaB1 = t.UnitQueues[Producer.Barracks].First(p => p.WorkerId == b1.Id);
                w.Remove(b1);
                Run(w, 0.1f);
                float kept = viaB1.Progress;
                Check(viaB1.WorkerId == b2.Id || viaB1.WorkerId == 0, "a destroyed barracks lets go of its unit");
                Run(w, 12);
                int built = t.Stats.Built.TryGetValue("rifleman", out var nb) ? nb : 0;
                Check(kept > 1.5f && built == 3 && t.UnitQueues[Producer.Barracks].Count == 0,
                      $"the survivor finishes all three, the unit from the lost barracks keeping its {kept:0.0}s of progress ({built} trained in 14 s)");
            }

            // The reply names the lone building too.
            {
                var w = new World(2, 7, 80);
                w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
                w.Teams[0].Add("steel", 500);
                var r = Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "count", 2));
                Check(Ok(r) && Said(r).Contains("your only barracks") && Said(r).Contains("second barracks"), $"with one building the reply says so: {Said(r)}");
            }
        }

        static void ConstructionTruck()
        {
            var w = new World(2, 7, 96);
            var t0 = w.Teams[0];
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var r0 = Commands.Execute(w, 0, Cmd("type", "train", "unit", "construction_truck"));
            Check(!Ok(r0) && Said(r0).Contains("factory"), $"a construction truck comes from a factory ({Said(r0)})");
            var factory = w.SpawnStructure(0, "factory", w.FindPlacement(0, "factory").Value, 1f);
            w.SpawnStructure(0, "electronics_plant", w.FindPlacement(0, "electronics_plant").Value, 1f);
            w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
            t0.Add("steel", 1000); t0.Add("circuits", 300);
            var r1 = Commands.Execute(w, 0, Cmd("type", "train", "unit", "construction_truck"));
            Check(!Ok(r1) && Said(r1).Contains("steel"), $"and costs real money ({Said(r1)})");
            t0.Add("steel", 1000);
            var r2 = Commands.Execute(w, 0, Cmd("type", "train", "unit", "construction_truck"));
            Run(w, 31);
            var truck = w.Owned(0).FirstOrDefault(e => e.Def.Key == "construction_truck");
            Check(Ok(r2) && truck != null, $"1500 steel + 200 circuits and 30 s at the factory buys one ({Said(r2)})");

            // Deploying: same placement rules as an outpost (open ground, no ore, the truck lanes).
            var open = OpenGround(w);
            At(truck, open);
            var r3 = Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { truck.Id }));
            var cc2 = w.Owned(0).Where(e => e.Def.Key == "command_center" && e != hq).FirstOrDefault();
            Check(Ok(r3) && cc2 != null && cc2.IsComplete && truck.Dead && Said(r3).Contains("command_center"), $"it deploys into a working command center where it stands ({Said(r3)})");
            var lane = w.Bay(cc2).head;
            var truck2 = At(w.SpawnUnit(0, "construction_truck", factory), lane + new Vec2(0, -1f));
            var r4 = Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { truck2.Id }));
            Check(!Ok(r4) && Said(r4).Contains("lane"), $"but never on another drop-off's truck lane ({Said(r4)})");
            var ore = w.Map.NearestOre(hq.Center, 40).Value;
            At(truck2, ore.Center);
            var r5 = Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { truck2.Id }));
            Check(!Ok(r5) && Said(r5).Contains("ore"), $"or on ore ({Said(r5)})");

            // Alive while it has one: losing every structure with a construction truck left is not the end.
            foreach (var open2 in new[] { false, true })
            {
                var w2 = new World(2, 7, 96) { Open = open2 };
                var spare = At(w2.SpawnUnit(1, "construction_truck", w2.Owned(1).First(e => e.IsStructure)), OpenGround(w2));
                var hunter = w2.SpawnUnit(0, "heavy_tank", w2.Owned(0).First(e => e.IsStructure));
                foreach (var s in w2.Owned(1).Where(e => e.IsStructure).ToList()) w2.Hurt(s, 1e6f, hunter);
                foreach (var u in w2.Owned(1).Where(e => !e.IsStructure && e != spare).ToList()) w2.Remove(u);
                Run(w2, 2);
                bool alive = !w2.Teams[1].Defeated && !w2.GameOver && w2.Stalled(w2.Teams[1]) == null;
                var r6 = Commands.Execute(w2, 1, Cmd("type", "deploy", "units", new[] { spare.Id }));
                Run(w2, 1);
                Check(alive && Ok(r6) && !w2.Teams[1].Defeated && w2.Owned(1).Any(e => e.Def.Key == "command_center"),
                      $"{(open2 ? "open arena" : "match")}: a team whose last structure falls but has a construction truck is still in (not stalled either), and rebuilds ({Said(r6)})");
                // Without one, the same loss is the end.
                foreach (var s in w2.Owned(1).ToList()) w2.Hurt(s, 1e6f, hunter);
                Run(w2, 2);
                Check(w2.Teams[1].Defeated, $"{(open2 ? "open arena" : "match")}: with no structure and no construction truck the team is out");
            }
            // A stranded construction truck and nothing else can't make progress: the stall rule still resigns it.
            var w3 = new World(2, 7, 96);
            var stuck = At(w3.SpawnUnit(1, "construction_truck", w3.Owned(1).First(e => e.IsStructure)), OpenGround(w3));
            foreach (var e in w3.Owned(1).Where(e => e != stuck).ToList()) w3.Remove(e);
            w3.Teams[1].Stock.Clear();
            stuck.Fuel = 0; stuck.Stranded = true;
            Check(w3.Stalled(w3.Teams[1]) != null, "a stranded construction truck alone can't make progress (stall rule applies)");

            // The house AI rebuilds a lost HQ when it can afford a truck.
            var g = new Game(new GameConfig { Seed = 5, MapSize = 96, Controllers = new[] { "ai", "ai" } });
            var wg = g.World;
            wg.Teams[0].ProtectedUntil = 1e9f; wg.Teams[1].ProtectedUntil = 1e9f; // just the AI's own logic, no war
            Step(g, 8 * 60);
            var ai = wg.Teams[0];
            if (!wg.HasComplete(0, "factory")) wg.SpawnStructure(0, "factory", wg.FindPlacement(0, "factory").Value, 1f);
            if (!wg.HasComplete(0, "electronics_plant")) wg.SpawnStructure(0, "electronics_plant", wg.FindPlacement(0, "electronics_plant").Value, 1f);
            var lost = wg.Owned(0).First(e => e.Def.Key == "command_center");
            wg.Remove(lost);
            ai.Add("steel", 2500); ai.Add("circuits", 400);
            Step(g, 120);
            int built = wg.Teams[0].Stats.Built.TryGetValue("construction_truck", out var nb) ? nb : 0;
            Check(wg.Owned(0).Count(e => e.Def.Key == "command_center") >= 1 && built >= 1,
                  $"the house AI that loses its HQ builds a construction truck and deploys a new one ({wg.Owned(0).Count(e => e.Def.Key == "command_center")} command centers, {built} trucks built)");
        }

        /// <summary>Mine a fresh map bare, let it regrow: how much comes back in 10 and 20 minutes, where, and of which ore (iron, copper, crystal, uranium).</summary>
        static (int total, int middle, int edges, int fieldOre, int[] byOre) RegrowBare(int size, float oreScale, float minutes, int seed = 1)
        {
            var w = new World(2, seed, size, oreScale);
            foreach (var e in w.Entities.Where(e => e.IsHarvester).ToList()) w.Remove(e);
            int field = w.Map.OreBase.Sum();
            Array.Clear(w.Map.Ore, 0, w.Map.Ore.Length);
            Run(w, minutes * 60);
            int mid = 0, edge = 0;
            var byOre = new int[4];
            float half = 0.5f * MathF.Sqrt(2) * size;
            for (int i = 0; i < w.Map.Ore.Length; i++)
            {
                float dx = i % size + 0.5f - size / 2f, dy = i / size + 0.5f - size / 2f;
                if (MathF.Sqrt(dx * dx + dy * dy) < half * 0.4f) mid += w.Map.Ore[i]; else edge += w.Map.Ore[i];
                if (w.Map.OreBase[i] > 0) byOre[w.Map.OreBaseType[i]] += w.Map.Ore[i];
            }
            return (w.Map.Ore.Sum(), mid, edge, field, byOre);
        }

        static void OreRegrowth()
        {
            foreach (var (size, scale) in new[] { (96, 1f), (96, 0.3f), (208, 0.3f) })
            {
                var r = RegrowBare(size, scale, 20);
                Console.WriteLine($"      regrowth {size}x{size} ore x{scale}, 20 min after mined bare: {r.total} of {r.fieldOre} ({r.total / 1200f:0.0} ore/s), middle {r.middle}, edges {r.edges}; iron {r.byOre[0] / 1200f:0.0}, copper {r.byOre[1] / 1200f:0.0}, crystal {r.byOre[2] / 1200f:0.0}, uranium {r.byOre[3] / 1200f:0.0} ore/s");
                Check(r.total / 1200f > 1.5f && r.total / 1200f < 10f,
                      $"a mined-bare {size}x{size} map (ore x{scale}) earns back meaningful income in 20 minutes ({r.total / 1200f:0.0} ore/s, a deep mine pumps 4)");
                int basic = r.byOre[0] + r.byOre[1];
                Check(basic > r.total * 0.4f && r.byOre[2] < r.total * 0.5f,
                      $"and iron and copper are a real share of it, not crystal ({basic / 1200f:0.0} ore/s iron + copper, {r.byOre[2] / 1200f:0.0} crystal)");
            }

            // Never past the original amount, from a field's root when it's mined to nothing, never under a structure or
            // on a tile a truck is mining.
            var w = new World(2, 7, 80);
            foreach (var e in w.Entities.Where(e => e.IsHarvester).ToList()) w.Remove(e);
            var m = w.Map;
            m.FieldIndex(out var tiles, out var roots);
            Array.Clear(m.Ore, 0, m.Ore.Length);
            // The crystal field nearest the centre: its root, a tile next to the root, and a tile far from it.
            var crystal = tiles.Where(i => m.OreBaseType[i] == Map.Crystal).OrderBy(i => Vec2.Dist(new Vec2(i % m.W, i / m.W), new Vec2(40, 40))).ToList();
            int root = crystal.First(i => roots[i]);
            int far = crystal.Where(i => !roots[i]).OrderByDescending(i => Vec2.Dist(new Vec2(i % m.W, i / m.W), new Vec2(root % m.W, root / m.W))).First();
            Run(w, 2.1f);
            bool rootsOnly = tiles.Where(i => m.Ore[i] > 0).All(i => roots[i]);
            for (int k = 0; k < 20 * 300 && m.Ore[root] == 0; k++) w.Step(); // crystal regrows at half the base rate: a minute or so
            Check(rootsOnly && m.Ore[root] > 0 && m.Ore[far] == 0,
                  $"a field mined to nothing starts growing back from its root (the tile that held the most), not everywhere at once (first pass: only roots {rootsOnly}; root {m.Ore[root]}, far tile {m.Ore[far]})");
            Run(w, 300);
            Check(m.Ore[far] > 0, "and spreads from there into the rest of the field");
            // A building on a mined tile, and a truck parked on another: neither regrows.
            var mid = new Vec2(m.W / 2f, m.H / 2f);
            int Spot(Func<int, bool> ok) => tiles.Where(i => !roots[i] && ok(i)).OrderBy(i => Vec2.Dist(new Vec2(i % m.W, i / m.W), mid)).First();
            int built = Spot(i => true);
            m.Ore[built] = 0;
            var turret = w.SpawnStructure(0, "gun_turret", new Int2(built % m.W, built / m.W), 1f);
            int mined = Spot(i => i != built && i != built + 1 && i != built - 1);
            m.Ore[mined] = 1; m.OreType[mined] = m.OreBaseType[mined];
            var truck = w.SpawnUnit(0, "mining_truck", w.Owned(0).First(e => e.Def.Key == "command_center"));
            w.SetOrder(truck, Order.Harvest, new Vec2(mined % m.W + 0.5f, mined / m.W + 0.5f)); // assigned to that tile
            truck.Stranded = true; truck.Fuel = 0; // and held off it, so it doesn't mine it out
            var hq1 = w.Owned(1).First(e => e.Def.Key == "command_center");
            int minedBefore = m.Ore[mined];
            Run(w, 30);
            Check(m.Ore[built] == 0 && !turret.Dead, $"nothing regrows under a structure ({m.Ore[built]})");
            Check(m.Ore[mined] <= minedBefore, $"nor on a tile a truck is mining ({minedBefore} -> {m.Ore[mined]})");
            // A long time later, nothing is past what the map started with, and the middle is full again.
            w.Remove(turret); w.Remove(truck);
            for (int k = 0; k < 6; k++) Run(w, 600);
            int over = tiles.Count(i => m.Ore[i] > m.OreBase[i]);
            int midFull = crystal.Count(i => m.Ore[i] >= m.OreBase[i]);
            Check(over == 0 && m.Ore.Max() <= Map.MaxOrePerTile && midFull > crystal.Count / 2,
                  $"an hour later no tile is past its original amount ({over} over), and the middle field is back ({midFull} of {crystal.Count} tiles full)");

            // Salvage never lands on a field of another ore (it would block that field's regrowth), and a field tile under
            // salvage of another ore waits for it to be mined.
            int ironTile = tiles.First(i => m.OreBaseType[i] == Map.Iron);
            m.Ore[ironTile] = 0;
            Check(!m.SalvageMayLand(ironTile, Map.Copper) && m.SalvageMayLand(ironTile, Map.Iron), "salvage only lands on a field tile of its own ore");
            m.Ore[ironTile] = 50; m.OreType[ironTile] = Map.Copper; // a copper pile from before this rule
            Run(w, 60);
            Check(m.Ore[ironTile] == 50 && m.OreType[ironTile] == Map.Copper, "a field tile under another ore's pile doesn't regrow until the pile is mined");

            // Saved games: the original amounts are saved; a game from before regrowth rebuilds them from its seed.
            var g = new Game(new GameConfig { Seed = 31, MapSize = 80, OreScale = 0.3f, Controllers = new[] { "ai", "ai" } });
            Step(g, 60);
            var root0 = (Dictionary<string, object>)Json.Parse(Json.Write(Snapshot.Write(g)));
            var back = Resume(Json.Write(root0));
            Check(back.World.Map.OreBase.SequenceEqual(g.World.Map.OreBase) && back.World.Map.OreBaseType.SequenceEqual(g.World.Map.OreBaseType), "the fields' original amounts are saved");
            var mapD = (Dictionary<string, object>)((Dictionary<string, object>)root0["world"])["map"];
            mapD.Remove("ore_base"); mapD.Remove("ore_base_type");
            var old = Resume(Json.Write(root0));
            Check(old.World.Map.OreBase.SequenceEqual(g.World.Map.OreBase) && !old.World.Map.BaseMissing,
                  $"a game saved before regrowth gets its fields' original amounts back from its seed ({old.World.Map.OreBase.Count(x => x > 0)} field tiles, {g.World.Map.OreBase.Count(x => x > 0)} originally)");
            Step(old, 10);
            Check(old.World.Errors == 0, "and plays on");

            // ...and a game saved when the base sites sat on tile corners (9 tiles in) is rebuilt with the generator it was made with, not
            // the one that puts the sites on tile centres: what such a save restores is the terrain, the ore on the ground and the
            // spawns, and the rebuilt fields must be the ones it started with, tile for tile.
            foreach (var (size, seed, scale) in new[] { (80, 31, 0.3f), (80, 7, 1f), (128, 5, 1f) })
            {
                var madeThen = Map.Generate(size, size, seed, scale, extraFields: false, inset: Map.LegacySiteInset);
                var restored = new Map(size, size) { OreScale = scale };
                Array.Copy(madeThen.Tiles, restored.Tiles, madeThen.Tiles.Length);
                Array.Copy(madeThen.Ore, restored.Ore, madeThen.Ore.Length);
                Array.Copy(madeThen.OreType, restored.OreType, madeThen.OreType.Length);
                restored.Spawns.AddRange(madeThen.Spawns);
                restored.BaseMissing = true; // (no ore_base in the save)
                restored.RebuildBase(seed, size, scale);
                bool exact = restored.OreBase.SequenceEqual(madeThen.OreBase) && restored.OreBaseType.SequenceEqual(madeThen.OreBaseType);
                var newer = Map.Generate(size, size, seed, scale, extraFields: false);
                Check(madeThen.Spawns[0].X == 9f && newer.Spawns[0].X == 9.5f && exact && !newer.OreBase.SequenceEqual(madeThen.OreBase),
                      $"a {size}x{size} game saved with its base sites on tile corners rebuilds exactly its original fields ({restored.OreBase.Count(x => x > 0)} field tiles; today's generator, with the sites on tile centres, would give a different map)");
            }
        }

        /// <summary>Ore of each type within r tiles of p.</summary>
        static int[] OreNear(World w, Vec2 p, int r)
        {
            var n = new int[4];
            for (int y = (int)p.Y - r; y <= (int)p.Y + r; y++)
                for (int x = (int)p.X - r; x <= (int)p.X + r; x++)
                    if (w.Map.InBounds(x, y)) n[w.Map.OreType[w.Map.Idx(x, y)]] += w.Map.Ore[w.Map.Idx(x, y)];
            return n;
        }

        /// <summary>An empty patch of open ground in the middle of a fresh 2-team map (no ore within 6 tiles).</summary>
        static Vec2 OpenGround(World w)
        {
            for (int r = 0; r < 30; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var p = new Vec2(w.Map.W / 2 + dx + 0.5f, w.Map.H / 2 - 10 + dy + 0.5f);
                        bool clear = true;
                        for (int y = (int)p.Y - 6; y <= (int)p.Y + 6 && clear; y++)
                            for (int x = (int)p.X - 6; x <= (int)p.X + 6 && clear; x++)
                                if (!w.Map.InBounds(x, y) || !w.Map.Passable(x, y) || w.Map.Ore[w.Map.Idx(x, y)] > 0) clear = false;
                        if (clear) return p;
                    }
            throw new Exception("no open ground");
        }

        // ------------------------------------------------------------------ the scripted AI's economy

        /// <summary>A fresh world with the scripted AI on team 0 (passive, like the house AI) and nobody on team 1.</summary>
        static (World w, SimpleAI ai) AiWorld(int seed = 3) => (new World(2, seed, 80), new SimpleAI(0, passive: true));

        /// <summary>Plays the AI for `seconds`; `each` runs first every tick (a hand-fed income, say).</summary>
        static void AiRun(World w, SimpleAI ai, float seconds, Action each = null)
        {
            for (int i = 0; i < seconds * World.TickRate; i++) { each?.Invoke(); ai.Update(w); w.Step(); }
        }

        /// <summary>Finished structures for team 0, as if the AI had built them.</summary>
        static void AiBase(World w, params string[] keys)
        {
            foreach (var k in keys) w.SpawnStructure(0, k, w.FindPlacement(0, k).Value, 1f);
        }

        static int Have(World w, string key) => w.Owned(0).Count(e => e.IsStructure && e.Def.Key == key);

        static void AiEconomy()
        {
            Console.WriteLine("\n-- scripted AI economy");
            bool regrowth = World.RegrowthDisabled;
            World.RegrowthDisabled = true; // ore the scenarios clear away stays gone
            try { AiRefineries(); AiDeepMining(); AiDrillChoices(); AiDeepRecovery(); AiEconomyRecovery(); AiHeadquartersRecovery(); AiRigDestinations(); AiMinePlan(); AiBuildOverrides(); AiPlanEdges(); AiExpansion(); }
            finally { World.RegrowthDisabled = regrowth; }
        }

        static void AiRefineries()
        {

            // No trucks, no power plants, and only the command center's trickle of iron ore coming in. The refineries used to
            // eat the ore as it arrived, so neither the 200 a truck costs nor the 250 a plant costs ever sat in the stockpile.
            var (w, ai) = AiWorld();
            var t = w.Teams[0];
            AiRun(w, ai, 4 * 60);
            foreach (var e in w.Owned(0).Where(e => e.IsHarvester || e.Def.Key == "power_plant").ToList()) w.Remove(e);
            t.Add("iron_ore", -t.Amount("iron_ore"));
            AiRun(w, ai, 3);
            Check(Have(w, "mining_refinery") >= 1 && t.LowPower && t.Reserve.TryGetValue("iron_ore", out var held) && held == 250,
                  $"with its power gone the refineries leave alone the 250 iron ore the next power plant costs (reserve {(t.Reserve.TryGetValue("iron_ore", out var h2) ? h2 : 0)})");
            AiRun(w, ai, 14 * 60);
            Check(w.Owned(0).Any(e => e.IsHarvester) && Have(w, "power_plant") >= 1, $"and it works its way out: {w.Owned(0).Count(e => e.IsHarvester)} trucks, {Have(w, "power_plant")} power plants");

            // The second refinery costs 100 copper ore, which the refineries turn into copper as it comes in: they leave it alone too.
            var (w2, ai2) = AiWorld();
            var t2 = w2.Teams[0];
            AiBase(w2, "power_plant", "mining_refinery", "barracks", "factory", "power_plant", "electronics_plant", "gun_turret");
            Array.Fill(t2.Explored, true); // the whole map's surface ore is in view: not an economy that has to go deep
            t2.Add("iron_ore", 700); t2.Add("steel", 1000); t2.Add("copper", 500); t2.Add("circuits", 200);
            t2.Add("copper_ore", -t2.Amount("copper_ore"));
            AiRun(w2, ai2, 5, () => t2.Add("copper_ore", 3 * World.Dt));
            Check(t2.Reserve.TryGetValue("copper_ore", out var cu) && cu == 100, $"it holds back the 100 copper ore its second refinery costs (reserve {(t2.Reserve.TryGetValue("copper_ore", out var c2) ? c2 : 0)})");
            AiRun(w2, ai2, 115, () => t2.Add("copper_ore", 3 * World.Dt));
            Check(Have(w2, "mining_refinery") >= 2, $"and with copper ore trickling in at 3 a second the refinery goes up ({Have(w2, "mining_refinery")} refineries, copper ore {t2.Amount("copper_ore")})");

            // A gun turret short of 30 copper used to stall the whole tech tree. The AI waits a minute for it, then goes on with
            // what it can pay for (leaving the copper alone), and comes back to the turret once there is copper.
            var (w3, ai3) = AiWorld();
            var t3 = w3.Teams[0];
            AiBase(w3, "power_plant", "mining_refinery", "barracks", "factory", "power_plant", "electronics_plant");
            Array.Fill(t3.Explored, true);
            foreach (var d in w3.Map.Deep) d.Amount = 0; // nothing to drill: this is about the build order
            t3.Add("iron_ore", 1400); t3.Add("steel", 20000); t3.Add("circuits", 5000); // plenty: the army can't spend it all
            Action noCopper = () => { t3.Add("copper_ore", -t3.Amount("copper_ore")); t3.Add("copper", -t3.Amount("copper")); }; // spent as fast as it comes in
            AiRun(w3, ai3, 45, noCopper);
            Check(Have(w3, "gun_turret") == 0 && Have(w3, "optics_lab") == 0, "short of copper for the turret, it waits a while before going on");
            var saved = Json.Write(ai3.SaveState());
            var back = SimpleAI.LoadState((Dictionary<string, object>)Json.Parse(saved));
            Check(saved.Contains("\"stuck_since\":[[") && Json.Write(back.SaveState()) == saved, "what it is waiting on is part of the saved game");
            AiRun(w3, ai3, 45, noCopper);
            Check(Have(w3, "gun_turret") == 0 && Have(w3, "optics_lab") == 1, $"then it builds the next step it can pay for ({Have(w3, "optics_lab")} optics lab)");
            t3.Add("copper", 1000);
            AiRun(w3, ai3, 40);
            Check(Have(w3, "gun_turret") == 1, "and comes back to the turret as soon as the copper is there");

            // Short of circuits, a fusion reactor can't be paid for: plain power plants keep the lights on instead of waiting on it.
            var (w4, ai4) = AiWorld();
            var t4 = w4.Teams[0];
            AiBase(w4, "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant", "enrichment_plant");
            t4.Add("plasma", 200); t4.Add("iron_ore", 800); t4.Add("steel", 2000);
            AiRun(w4, ai4, 3);
            Check(t4.LowPower && Have(w4, "power_plant") == 2 && Have(w4, "fusion_reactor") == 0, $"low on power with no circuits for a reactor, it builds a power plant ({Have(w4, "power_plant")} plants)");

            // Crystal and uranium trucks only while it is short of them and has something to refine them.
            var (w6, ai6) = AiWorld();
            var t6 = w6.Teams[0];
            AiBase(w6, "power_plant", "mining_refinery", "barracks", "factory", "power_plant", "electronics_plant", "optics_lab");
            for (int i = 0; i < 6; i++) w6.SpawnUnit(0, "mining_truck", w6.Owned(0).First(e => e.Def.Key == "command_center"));
            t6.Add("crystal", 3000);
            AiRun(w6, ai6, 3);
            var trucks = w6.Owned(0).Where(e => e.IsHarvester).ToList();
            Check(trucks.Count >= 7 && trucks.All(e => e.HarvestType != Map.Crystal), $"with 3000 crystal in the stockpile no truck goes for more ({trucks.Count} trucks)");
            t6.Add("crystal", -t6.Amount("crystal"));
            foreach (var e in trucks) e.Cargo = 0; // (a truck with a load keeps its ore type until it has unloaded)
            AiRun(w6, ai6, 3);
            Check(trucks.Any(e => e.HarvestType == Map.Crystal) && trucks.Count(e => e.HarvestType == Map.Crystal) <= 2, $"out of it, one does ({trucks.Count(e => e.HarvestType == Map.Crystal)} on crystal)");

            // The attacking AI's waves aim at the enemy's base, not at the nearest derrick it holds: attack-move leaves a held
            // derrick alone, so every wave used to stop beside it and the enemy base was never touched.
            var w7 = new World(2, 3, 80);
            var raider = new SimpleAI(0); // not passive
            var foe = w7.Owned(1).First(e => e.Def.Key == "command_center");
            var derrick = w7.Derricks.OrderBy(d => Vec2.Dist(d.Center, w7.Teams[0].StartPos)).First();
            derrick.Team = 1; // the enemy holds it, and it is nearer than their base
            w7.Teams[0].KnownEnemyStructures[derrick.Id] = ("derrick", derrick.Origin, 1);
            w7.Teams[0].KnownEnemyStructures[foe.Id] = ("command_center", foe.Origin, 1);
            var tanks = Enumerable.Range(0, 8).Select(_ => w7.SpawnUnit(0, "light_tank", w7.Owned(0).First(e => e.Def.Key == "command_center"))).ToList();
            foreach (var u in w7.Owned(0).Where(e => !e.IsStructure)) w7.SetOrder(u, Order.Idle, u.Pos); // standing about, as a finished army does
            raider.Update(w7);
            Check(Vec2.Dist(derrick.Center, w7.Teams[0].StartPos) < Vec2.Dist(foe.Center, w7.Teams[0].StartPos) && tanks.All(u => u.Order == Order.AttackMove && Vec2.Dist(u.OrderPos, foe.Center) < 3),
                  $"a wave goes for the enemy command center, not the derrick beside it (orders to {tanks[0].OrderPos}, base at {foe.Center})");
        }

        static void AiDeepMining()
        {
            var (w, ai) = AiWorld();
            var t = w.Teams[0];
            AiBase(w, "power_plant", "power_plant", "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant", "gun_turret");
            for (int i = 0; i < w.Map.Ore.Length; i++) w.Map.Ore[i] = 0;
            for (int s = 1; s < w.Map.Spawns.Count; s++) t.KnownEnemyStructures[900 + s] = ("command_center", Int2.Of(w.Map.Spawns[s]), 1); // (nowhere to expand to: this is about the rig)
            var deps = w.Map.Deep.OrderBy(d => Vec2.Dist(d.Pos, t.StartPos)).Take(3).ToList();
            deps[0].Type = Map.Iron; deps[1].Type = Map.Iron; deps[2].Type = Map.Copper; // the copper one is the farthest
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            foreach (var d in deps) { t.Surveyed.Add(d.Id); t.Zones[d.Id] = new ZoneFlag { ZoneId = d.Id, FlaggedBy = 0, FlaggedAt = w.Time }; }
            var rig = At(w.SpawnUnit(0, "drill_rig", hq), deps[0].Pos + new Vec2(1, 0));
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { rig.Id }))), "(a mine on the first deposit)");
            var mine = w.Owned(0).First(e => e.Def.Key == "deep_mine");
            deps[0].Amount = 0; // and it has run dry
            t.Add("iron_ore", 1000); t.Add("circuits", 300);
            t.Add("copper_ore", -t.Amount("copper_ore")); // short of copper
            mine.LastHitTime = w.Time; // just hit: a building under fire can't be sold for 10 s
            AiRun(w, ai, 5);
            Check(!mine.Dead, "a spent mine that was just hit can't be sold yet");
            AiRun(w, ai, 8);
            Check(mine.Dead && !w.Owned(0).Any(e => e.Def.Key == "deep_mine"), "it sells a mine whose deposit is used up once it can (it draws 50 power for nothing)");
            // Steel only trickles in (2 a second): the army used to spend every bit of it, so the 800-steel rig was never affordable.
            AiRun(w, ai, 3 * 60, () => t.Add("steel", 2 * World.Dt));
            Check(t.SaveFor == "drill_rig" && t.Reserved("steel") >= 800 && w.Owned(0).Count(e => !e.IsStructure && e.IsArmed) <= 2,
                  $"with a dry mine and zones waiting it saves for a rig instead of building an army (saving for {t.SaveFor}, {t.Amount("steel")} steel)");
            AiRun(w, ai, 12 * 60, () => t.Add("steel", 2 * World.Dt));
            var next = w.Owned(0).FirstOrDefault(e => e.Def.Key == "deep_mine" && e.DepositId != deps[0].Id);
            Check(next != null && next.DepositId == deps[2].Id, $"and drills the next deposit: the copper one it is short of, not just the nearest ({(next == null ? "no mine" : Defs.Ores[w.Map.DepositById(next.DepositId).Type])})");
            Check(w.Errors == 0, $"no sim errors ({w.LastError})");
        }

        /// <summary>One price list: kill value, kill salvage, a leaving team's base and a stockpile spill value things the same way.</summary>
        static void OneValuation()
        {
            Console.WriteLine("\n-- one valuation: kill value, salvage, leaves and spills");
            long[] OreByType(World x) { var n = new long[4]; for (int i = 0; i < x.Map.Ore.Length; i++) if (x.Map.Ore[i] > 0) n[x.Map.OreType[i]] += x.Map.Ore[i]; return n; }

            // The table every conversion reads is what the converters really do: a material's ores are its recipe's inputs' ores.
            var bad = new List<string>();
            foreach (var item in Defs.Items) if (!World.OreMix.ContainsKey(item)) bad.Add($"{item} has no ore mix");
            foreach (var r in Defs.All.Values.SelectMany(d => d.Recipes).Where(r => r.Outputs.Count == 1 && r.Inputs.Count > 0))
            {
                var made = r.Outputs.First();
                var sum = new float[4];
                foreach (var inp in r.Inputs) for (int k = 0; k < 4; k++) sum[k] += inp.Value * World.OreMix[inp.Key][k];
                for (int k = 0; k < 4; k++) if (Math.Abs(sum[k] / made.Value - World.OreMix[made.Key][k]) > 1e-4f) bad.Add($"{made.Key} is not {string.Join("+", sum)} per unit");
            }
            Check(bad.Count == 0, $"every item's ore mix matches its converter recipe ({string.Join("; ", bad)})");

            // The same value in a kill and in a leave, for every kind of deployed structure.
            void Bare(World x) // team 1 with nothing: no stockpile, no buildings, no units
            {
                x.Teams[0].ProtectedUntil = 0; x.Teams[1].ProtectedUntil = 0;
                x.Teams[1].Stock.Clear();
                foreach (var e in x.Owned(1).ToList()) x.Remove(e);
            }
            Entity Setup(World x, string key)
            {
                Bare(x);
                if (key == "derrick") { var d = x.Derricks.First(); d.Team = 1; return d; }
                return x.SpawnStructure(1, key, Int2.Of(OpenGround(x)), 1f);
            }
            // What each is worth, from the cost of what deploys into it: construction truck 1500 steel + 200 circuits (a circuit is
            // 2 copper ore + 1 iron ore), outpost truck 400 steel + 100 copper + 50 circuits, drill rig 800 steel; a derrick is 600.
            foreach (var (key, worth) in new[] { ("command_center", 2100), ("outpost", 650), ("deep_mine", 800), ("derrick", 600) })
            {
                var wk = new World(2, 7, 96);
                var victim = Setup(wk, key);
                var shooter = wk.SpawnUnit(0, "heavy_tank", wk.Owned(0).First(e => e.IsStructure));
                int kv = wk.Teams[0].Stats.KillValue;
                wk.Hurt(victim, 1e6f, shooter);
                int killValue = wk.Teams[0].Stats.KillValue - kv;
                var wl = new World(2, 7, 96);
                var mine = Setup(wl, key);
                long before = OreByType(wl).Sum();
                wl.Leave(1);
                long left = OreByType(wl).Sum() - before;
                if (key == "derrick") Check(killValue == worth && mine.Team == -1 && left == 0, $"a derrick is worth {killValue} when killed; leaving hands it back neutral, whole, not as salvage ({left} ore)");
                else Check(killValue == worth && left >= 0.6f * worth - 8 && left <= 0.6f * worth, $"a {key} is worth {killValue} when killed and a leave dismantles 60% of the same ({left} of {worth})");
            }

            // A building nobody finished counts at the price its owner paid: all of it.
            var wu = new World(2, 7, 96);
            Bare(wu);
            var unfinished = wu.SpawnStructure(1, "power_plant", Int2.Of(OpenGround(wu)), 0.08f);
            var killer = wu.SpawnUnit(0, "heavy_tank", wu.Owned(0).First(e => e.IsStructure));
            int kv0 = wu.Teams[0].Stats.KillValue, sl0 = wu.Teams[0].Stats.SalvageLeft;
            wu.Hurt(unfinished, 1e6f, killer);
            Check(unfinished.Dead && wu.Teams[0].Stats.KillValue - kv0 == 250 && wu.Teams[0].Stats.SalvageLeft - sl0 == 62,
                  $"killing a power plant 8% built credits its full 250 as kill value and a quarter as salvage ({wu.Teams[0].Stats.KillValue - kv0}, {wu.Teams[0].Stats.SalvageLeft - sl0})");
            var wv = new World(2, 7, 96);
            Bare(wv);
            wv.SpawnStructure(1, "power_plant", Int2.Of(OpenGround(wv)), 0.08f);
            long beforeV = OreByType(wv).Sum();
            wv.Leave(1);
            long leftV = OreByType(wv).Sum() - beforeV;
            Check(leftV >= 146 && leftV <= 150, $"and a leave counts it the same way: 60% of the 250 ({leftV})");

            // Killing a heavy tank (400 steel + 80 circuits): the circuits come back as their copper AND the iron in them.
            var wh = new World(2, 7, 96);
            var spot = OpenGround(wh);
            var tank = At(wh.SpawnUnit(1, "heavy_tank", wh.Owned(1).First(e => e.Def.Key == "command_center")), spot);
            var gun = At(wh.SpawnUnit(0, "heavy_tank", wh.Owned(0).First(e => e.Def.Key == "command_center")), spot + new Vec2(-4, 0));
            wh.Hurt(tank, 1e6f, gun);
            var ore = OreNear(wh, spot, 4);
            Check(wh.Teams[0].Stats.KillValue == 640 && ore[Map.Iron] == 120 && ore[Map.Copper] == 40 && ore[Map.Crystal] == 0 && ore[Map.Uranium] == 0,
                  $"a heavy tank is worth 640 ore and leaves a quarter: 120 iron ore, 40 copper ore ({ore[0]} iron, {ore[1]} copper; kill value {wh.Teams[0].Stats.KillValue})");

            // A stockpile of every material spills as the raw ores they're made from.
            var ws = new World(2, 7, 80);
            ws.Teams[1].ProtectedUntil = 0;
            ws.Teams[1].Stock.Clear();
            foreach (var item in new[] { ("circuits", 100), ("composite", 100), ("lenses", 50), ("plasma", 10), ("copper", 30), ("steel", 20) }) ws.Teams[1].Add(item.Item1, item.Item2);
            ws.SpawnStructure(1, "power_plant", ws.FindPlacement(1, "power_plant").Value, 1f);
            var beforeS = OreByType(ws);
            Commands.Execute(ws, 1, Cmd("type", "sell", "structure_id", ws.Owned(1).First(e => e.Def.Key == "command_center").Id, "confirm", true));
            var spill = OreByType(ws).Select((v, i) => v - beforeS[i]).ToArray();
            // 100 circuits = 100 iron + 200 copper; 100 composite = 200 iron + 100 crystal; 50 lenses = 100 crystal; 10 plasma = 20 uranium; plus 30 copper, 20 steel.
            Check(spill[Map.Iron] >= 316 && spill[Map.Iron] <= 320 && spill[Map.Copper] >= 226 && spill[Map.Copper] <= 230 && spill[Map.Crystal] >= 198 && spill[Map.Crystal] <= 200 && spill[Map.Uranium] == 20,
                  $"a stockpile spills as its raw ores: iron {spill[0]}/320, copper {spill[1]}/230, crystal {spill[2]}/200, uranium {spill[3]}/20");
            Check(wh.Errors == 0 && wu.Errors == 0 && wv.Errors == 0 && ws.Errors == 0, "no sim errors");
        }

        /// <summary>Two ways deep mining used to get stuck for good: the factory gone with the last mine, and the mine cap filled with basic ores.</summary>
        static void AiDeepRecovery()
        {
            // The factory is destroyed and the last deep mine runs dry while another zone is flagged: "economy first" used to hold
            // the build order back for a rig that, with no factory, could never be built. It rebuilds the factory, then drills.
            var (w, ai) = AiWorld();
            var t = w.Teams[0];
            AiBase(w, "power_plant", "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant", "gun_turret");
            for (int i = 0; i < w.Map.Ore.Length; i++) w.Map.Ore[i] = 0;
            var deps = w.Map.Deep.OrderBy(d => Vec2.Dist(d.Pos, t.StartPos)).Take(3).ToList();
            foreach (var d in deps) { d.Type = Map.Iron; t.Surveyed.Add(d.Id); t.Zones[d.Id] = new ZoneFlag { ZoneId = d.Id, FlaggedBy = 0, FlaggedAt = w.Time }; }
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var rig = At(w.SpawnUnit(0, "drill_rig", hq), deps[0].Pos + new Vec2(1, 0));
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { rig.Id }))), "(a mine on the first deposit)");
            deps[0].Amount = 0; // run dry
            w.Remove(w.Owned(0).First(e => e.Def.Key == "factory")); // and the factory is gone
            t.Add("steel", 3000); t.Add("copper", 800); t.Add("iron_ore", 1500); t.Add("circuits", 500);
            AiRun(w, ai, 4 * 60);
            Check(Have(w, "factory") == 1, $"with the factory gone it rebuilds production instead of waiting for a rig it can't build ({Have(w, "factory")} factories)");
            AiRun(w, ai, 5 * 60);
            Check(w.Owned(0).Any(e => e.Def.Key == "deep_mine" && e.DepositId != deps[0].Id), "and then drills the next zone");

            // Four iron / copper mines pump, crystal and uranium sit at nothing with their plants built and a zone flagged for each:
            // the mine cap used to be filled by basic ores alone. Crystal and uranium have a slot of their own.
            var (w2, ai2) = AiWorld();
            var t2 = w2.Teams[0];
            AiBase(w2, "power_plant", "power_plant", "power_plant", "power_plant", "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant", "optics_lab", "enrichment_plant");
            for (int i = 0; i < w2.Map.Ore.Length; i++) w2.Map.Ore[i] = 0;
            var deps2 = w2.Map.Deep.OrderBy(d => Vec2.Dist(d.Pos, t2.StartPos)).Take(6).ToList();
            var types = new[] { Map.Iron, Map.Copper, Map.Iron, Map.Copper, Map.Crystal, Map.Uranium };
            for (int i = 0; i < deps2.Count; i++) { deps2[i].Type = types[i]; deps2[i].Amount = deps2[i].Initial = 6000; t2.Surveyed.Add(deps2[i].Id); t2.Zones[deps2[i].Id] = new ZoneFlag { ZoneId = deps2[i].Id, FlaggedBy = 0, FlaggedAt = w2.Time }; }
            var hq2 = w2.Owned(0).First(e => e.Def.Key == "command_center");
            for (int i = 0; i < 4; i++)
            {
                var r = At(w2.SpawnUnit(0, "drill_rig", hq2), deps2[i].Pos + new Vec2(1, 0));
                Check(Ok(Commands.Execute(w2, 0, Cmd("type", "deploy", "units", new[] { r.Id }))), $"(mine {i + 1} of 4 on a basic ore)");
            }
            t2.Add("steel", 4000); t2.Add("iron_ore", 1500); t2.Add("copper", 500); t2.Add("circuits", 500);
            AiRun(w2, ai2, 3 * 60);
            bool Has(DeepDeposit d) => w2.Owned(0).Any(e => (e.Def.Key == "drill_rig" && e.ZoneId == d.Id) || (e.Def.Key == "deep_mine" && e.DepositId == d.Id));
            Check(Has(deps2[4]) && Has(deps2[5]) && w2.Owned(0).Count(e => e.Def.Key == "deep_mine") <= 6, $"with four basic mines up, the crystal and uranium zones still get theirs ({w2.Owned(0).Count(e => e.Def.Key == "deep_mine")} mines)");
        }

        /// <summary>
        /// "Economy first" (no army, no tech, until a rig is on its way) must never hold back the buildings the economy stands on.
        /// Each of them is destroyed in turn as the last deep mine runs dry, with another zone flagged and the resources to put
        /// it back: the AI has to rebuild it and then drill the next zone. (The command center is not in the list: losing the
        /// last one spills the stockpile, and a new one comes from a construction truck, which has its own tests.)
        /// </summary>
        static void AiEconomyRecovery()
        {
            foreach (var lost in new[] { "power_plant", "mining_refinery", "factory", "electronics_plant" })
            {
                var (w, ai) = AiWorld();
                var t = w.Teams[0];
                AiBase(w, "power_plant", "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant", "gun_turret");
                for (int i = 0; i < w.Map.Ore.Length; i++) w.Map.Ore[i] = 0;
                var deps = w.Map.Deep.OrderBy(d => Vec2.Dist(d.Pos, t.StartPos)).Take(3).ToList();
                foreach (var d in deps) { d.Type = Map.Iron; t.Surveyed.Add(d.Id); t.Zones[d.Id] = new ZoneFlag { ZoneId = d.Id, FlaggedBy = 0, FlaggedAt = w.Time }; }
                var rig = At(w.SpawnUnit(0, "drill_rig", w.Owned(0).First(e => e.Def.Key == "command_center")), deps[0].Pos + new Vec2(1, 0));
                Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { rig.Id }));
                deps[0].Amount = 0; // the last mine has run dry
                foreach (var e in w.Owned(0).Where(e => e.Def.Key == lost).ToList()) w.Remove(e);
                t.Add("iron_ore", 1500); t.Add("copper_ore", 300); t.Add("copper", 800); t.Add("circuits", 500); t.Add("steel", 400); // under the 800 a rig costs
                AiRun(w, ai, 12 * 60);
                Check(Have(w, lost) >= 1 && w.Owned(0).Any(e => e.Def.Key == "deep_mine" && e.DepositId != deps[0].Id),
                      $"{lost} destroyed as the last mine runs dry: it is rebuilt ({Have(w, lost)}) and the next zone drilled ({w.Owned(0).Count(e => e.Def.Key == "deep_mine")} mines)");
            }
        }

        /// <summary>
        /// The command center is lost with the rest of the base standing: a construction truck (1500 steel + 200 circuits) is the
        /// way back, and the army must not spend the steel as it comes in or the 1500 is never there.
        /// </summary>
        static void AiHeadquartersRecovery()
        {
            var (w, ai) = AiWorld();
            var t = w.Teams[0];
            AiBase(w, "power_plant", "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant", "gun_turret");
            w.Remove(w.Owned(0).First(e => e.Def.Key == "command_center"));
            t.Add("circuits", 300); t.Add("copper", 500);
            AiRun(w, ai, 25 * 60, () => t.Add("steel", 3 * World.Dt)); // steel trickles in at 3 a second (from the trucks' ore, as the refineries make it)
            Check(Have(w, "command_center") >= 1, $"with its command center gone it saves for a construction truck instead of spending the steel on an army ({Have(w, "command_center")} command centers, steel {t.Amount("steel")})");
        }

        /// <summary>A base with the surface mined out, the usual buildings, and the nearest `n` deep zones flagged with the given ores (6000 each).</summary>
        static (World w, SimpleAI ai, Team t, List<DeepDeposit> deps) AiDeepBase(params int[] ores)
        {
            var (w, ai) = AiWorld();
            var t = w.Teams[0];
            AiBase(w, "power_plant", "power_plant", "power_plant", "power_plant", "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant");
            for (int i = 0; i < w.Map.Ore.Length; i++) w.Map.Ore[i] = 0;
            var deps = w.Map.Deep.OrderBy(d => Vec2.Dist(d.Pos, t.StartPos)).Take(ores.Length).ToList();
            for (int i = 0; i < deps.Count; i++)
            {
                deps[i].Type = ores[i]; deps[i].Amount = deps[i].Initial = 6000;
                t.Surveyed.Add(deps[i].Id); t.Zones[deps[i].Id] = new ZoneFlag { ZoneId = deps[i].Id, FlaggedBy = 0, FlaggedAt = w.Time };
            }
            return (w, ai, t, deps);
        }

        static void AiPump(World w, DeepDeposit d)
        {
            var r = At(w.SpawnUnit(0, "drill_rig", w.Owned(0).First(e => e.Def.Key == "command_center")), d.Pos + new Vec2(1, 0));
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { r.Id }))), "(a mine up)");
        }

        /// <summary>
        /// One plan for the deep mines that buying rigs and sending them both read: slots per ore (iron and copper share a cap of 4, a
        /// basic ore it needs and has none of always gets one, crystal and uranium one each), what is taken, and which zones are open.
        /// </summary>
        static void AiMinePlan()
        {
            // Four iron mines pump, the surface copper is gone and there is no copper at all, with a step of the build order waiting on it,
            // and copper zones are flagged: the cap used to filter them out, so copper starved until an iron deposit emptied. The AI retires
            // the iron mine with the most left to make room, and drills copper.
            var (w, ai, t, deps) = AiDeepBase(Map.Iron, Map.Iron, Map.Iron, Map.Iron, Map.Copper, Map.Copper);
            for (int i = 0; i < 4; i++) AiPump(w, deps[i]);
            deps[2].Amount = 5000; // the iron mine with the most left is deps[0], [1] or [3]
            t.Add("copper_ore", -t.Amount("copper_ore")); t.Add("steel", 3000); t.Add("iron_ore", 1000); t.Add("circuits", 300); // (a gun turret, 30 copper, is the next step)
            AiRun(w, ai, 8 * 60);
            int ironMines = w.Owned(0).Count(e => e.Def.Key == "deep_mine" && w.Map.DepositById(e.DepositId).Type == Map.Iron);
            Check(w.Owned(0).Any(e => e.Def.Key == "deep_mine" && w.Map.DepositById(e.DepositId).Type == Map.Copper) && ironMines >= 3,
                  $"four iron mines, no copper and a step waiting on it: an iron mine goes and a copper one comes ({ironMines} iron mines, {w.Owned(0).Count(e => e.Def.Key == "deep_mine" && w.Map.DepositById(e.DepositId).Type == Map.Copper)} copper)");

            // Four basic mines pump and two crystal zones are flagged, crystal at nothing with its plant built: one mine on crystal means one
            // rig is worth buying (the second used to be bought and left idle, 800 steel for nothing).
            var (w2, ai2, t2, d2) = AiDeepBase(Map.Iron, Map.Copper, Map.Iron, Map.Copper, Map.Crystal, Map.Crystal);
            AiBase(w2, "optics_lab");
            for (int i = 0; i < 4; i++) AiPump(w2, d2[i]);
            t2.Add("steel", 4000); t2.Add("iron_ore", 1000); t2.Add("copper", 500); t2.Add("circuits", 300);
            AiRun(w2, ai2, 3 * 60);
            int bought = w2.Teams[0].Stats.Built.TryGetValue("drill_rig", out var nb) ? nb : 0;
            Check(bought == 1 && !w2.Owned(0).Any(e => e.Def.Key == "drill_rig" && e.Order == Order.Idle) && w2.Owned(0).Count(e => e.Def.Key == "deep_mine") == 5,
                  $"two crystal zones, one mine on crystal: it buys one rig, not two ({bought} rigs bought, {w2.Owned(0).Count(e => e.Def.Key == "deep_mine")} mines)");
        }

        /// <summary>
        /// With the ore within reach of its drop-offs thin, the AI expands: an outpost truck to the nearest field or base site nobody holds,
        /// its idle trucks after it, a scout when there is nowhere known to go; and it doesn't hold ore back for an optional refinery
        /// while a building the economy stands on is missing.
        /// </summary>
        static void AiExpansion()
        {
            // The ground round its base is mined out (the fields are still there to regrow, but there is nothing in them): the AI saves up for an
            // outpost truck, sends it to a base site nobody holds (not the enemy's corner), and its idle trucks follow once the outpost stands.
            bool openArena = false;
            (World w, SimpleAI ai, Team t, Entity hq) Thin(params int[] hostileSites)
            {
                var w0 = new World(2, 3, 80) { Open = openArena };
                var ai0 = new SimpleAI(0, passive: true);
                var t0 = w0.Teams[0];
                AiBase(w0, "power_plant", "mining_refinery", "barracks", "factory", "power_plant", "electronics_plant");
                var hq0 = w0.Owned(0).First(e => e.Def.Key == "command_center");
                for (int i = 0; i < w0.Map.Ore.Length; i++)
                    if (Vec2.Dist(new Vec2(i % w0.Map.W + 0.5f, i / w0.Map.W + 0.5f), t0.StartPos) < 45f) w0.Map.Ore[i] = 0;
                t0.Add("steel", 1500); t0.Add("copper", 500); t0.Add("circuits", 300);
                foreach (var s in hostileSites) // enemy structures it has seen by those corners
                    t0.KnownEnemyStructures[900 + s] = ("command_center", Int2.Of(w0.Map.Spawns[s]), 1);
                for (int i = 0; i < 6; i++) w0.SpawnUnit(0, "mining_truck", hq0);
                return (w0, ai0, t0, hq0);
            }
            var (w, ai, t, hq) = Thin(1); // (corner 1 is the enemy's, which it has seen)
            AiRun(w, ai, 40);
            var truck = w.Owned(0).FirstOrDefault(e => e.Def.Key == "outpost_truck");
            Check(truck != null, $"with the ore round its base gone and an electronics plant standing, it trains an outpost truck (saving for {t.SaveFor ?? "nothing"}, {w.Owned(0).Count(e => e.Def.Key == "outpost_truck")} trucks)");
            AiRun(w, ai, 4 * 60);
            var post = w.Owned(0).FirstOrDefault(e => e.Def.Key == "outpost");
            var sites = new[] { 2, 3 }.Select(s => w.Map.Spawns[s]).ToList();
            Check(post != null && sites.Any(s => Vec2.Dist(post.Center, s) < 24f) && Vec2.Dist(post.Center, w.Map.Spawns[1]) > 40f,
                  $"and puts an outpost beside a base site nobody holds, not the enemy's ({(post != null ? post.Center.ToString() : "none")}; free sites at {sites[0]} and {sites[1]})");
            AiRun(w, ai, 2 * 60);
            var trucks = w.Owned(0).Where(e => e.IsHarvester).ToList();
            Check(post != null && trucks.Count(tr => tr.HarvestTile.HasValue && Vec2.Dist(tr.HarvestTile.Value.Center, post.Center) < 20f) >= 3 && t.Stats.OreMined > 0,
                  $"its idle trucks go to the field by the outpost and mine it ({trucks.Count(tr => tr.HarvestTile.HasValue && post != null && Vec2.Dist(tr.HarvestTile.Value.Center, post.Center) < 20f)} of {trucks.Count}, {t.Stats.OreMined} ore mined)");

            // Enemy structures seen at the nearer free corner: it goes to the other one.
            var (w2, ai2, t2, hq2) = Thin(1, 2);
            AiRun(w2, ai2, 5 * 60);
            var post2 = w2.Owned(0).FirstOrDefault(e => e.Def.Key == "outpost");
            Check(post2 != null && Vec2.Dist(post2.Center, w2.Map.Spawns[3]) < 24f, $"with enemy structures seen at one free corner it goes to the other ({(post2 != null ? post2.Center.ToString() : "no outpost")}, free site at {w2.Map.Spawns[3]})");

            // Nowhere known to go (every other corner has an enemy by it) and the ground round its base thin: it sends a recon drone to look.
            var (w3, ai3, t3, hq3) = Thin(1, 2, 3);
            Array.Fill(t3.Explored, true); // it has seen the whole map but a patch beside its base
            for (int y = 8; y <= 16; y++) for (int x = 32; x <= 40; x++) t3.Explored[w3.Map.Idx(x, y)] = false;
            Entity drone = null;
            for (int s = 0; s < 90 && (drone == null || drone.Order != Order.Move); s++) { AiRun(w3, ai3, 1); drone = w3.Owned(0).FirstOrDefault(e => e.Def.Key == "recon_drone"); }
            Check(drone != null && !w3.Owned(0).Any(e => e.Def.Key == "outpost_truck") && drone.Order == Order.Move && Vec2.Dist(drone.OrderPos, new Vec2(36, 12)) < 3f,
                  $"with nowhere known to expand to it trains a recon drone and sends it to the ground it hasn't seen ({(drone != null ? drone.OrderName + " to " + drone.OrderPos : "no drone")})");

            // A truck under way learns of enemy structures at the only places it could go: it turns back, instead of driving on at them, and
            // having nowhere to go doesn't stop the scouting that would find somewhere.
            var (w8, ai8, t8, hq8) = Thin(1);
            Entity truck8 = null;
            for (int s = 0; s < 120 && (truck8 == null || truck8.Order != Order.Move || Vec2.Dist(truck8.OrderPos, t8.StartPos) < 30f); s++) { AiRun(w8, ai8, 1); truck8 = w8.Owned(0).FirstOrDefault(e => e.Def.Key == "outpost_truck"); }
            Check(truck8 != null && truck8.Order == Order.Move && Vec2.Dist(truck8.OrderPos, t8.StartPos) >= 30f, $"(set up: an outpost truck is on its way to a free corner: {(truck8 != null ? truck8.OrderPos.ToString() : "none")})");
            foreach (var c in new[] { 2, 3 }) t8.KnownEnemyStructures[950 + c] = ("command_center", Int2.Of(w8.Map.Spawns[c]), 1);
            Array.Fill(t8.Explored, true); // (the patch it hasn't seen is well off the truck's way home)
            for (int y = 26; y <= 34; y++) for (int x = 42; x <= 50; x++) t8.Explored[w8.Map.Idx(x, y)] = false;
            AiRun(w8, ai8, 3);
            var homes8 = w8.Owned(0).Where(e => e.IsStructure && e.Def.DropOff).Select(e => e.Center).ToList();
            Check(truck8 != null && (truck8.Order == Order.Idle || (truck8.Order == Order.Move && homes8.Any(h => Vec2.Dist(h, truck8.OrderPos) < 6f))),
                  $"it turns back towards its own drop-offs once its destination is by an enemy ({(truck8 != null ? truck8.OrderName + " to " + truck8.OrderPos : "no truck")})");
            Entity drone8 = null;
            for (int s = 0; s < 90 && (drone8 == null || drone8.Order != Order.Move); s++) { AiRun(w8, ai8, 1); drone8 = w8.Owned(0).FirstOrDefault(e => e.Def.Key == "recon_drone"); }
            Check(drone8 != null && drone8.Order == Order.Move && Vec2.Dist(drone8.OrderPos, new Vec2(46, 30)) < 8f,
                  $"and with that truck standing about at home, the scouting goes ahead ({(drone8 != null ? drone8.OrderName + " to " + drone8.OrderPos : "no drone")})");

            // Unseen ground that falls between the scouting samples (24 tiles apart) is found too.
            var (w9, ai9, t9, hq9) = Thin(1, 2, 3);
            Array.Fill(t9.Explored, true);
            for (int y = 26; y <= 34; y++) for (int x = 42; x <= 50; x++) t9.Explored[w9.Map.Idx(x, y)] = false; // no sample point (12, 36, 60) lies in it
            Entity drone9 = null;
            for (int s = 0; s < 90 && (drone9 == null || drone9.Order != Order.Move); s++) { AiRun(w9, ai9, 1); drone9 = w9.Owned(0).FirstOrDefault(e => e.Def.Key == "recon_drone"); }
            Check(drone9 != null && drone9.Order == Order.Move && Vec2.Dist(drone9.OrderPos, new Vec2(46, 30)) < 8f,
                  $"an unseen patch between the sample points is found: the drone is sent to {(drone9 != null ? drone9.OrderPos.ToString() : "nowhere")}, the patch is around (46,30)");

            // Idle trucks with none of their ore within 80 tiles go to a field their outposts reach, but not one by an enemy base they know of:
            // a truck choosing for itself keeps clear of those, and a coordinate harvest skips that unless the AI does it.
            foreach (bool foeThere in new[] { false, true })
            {
                var (wa, aia, ta, hqa) = Thin();
                for (int i = 0; i < wa.Map.Ore.Length; i++) wa.Map.Ore[i] = 0; // no other ore on the map
                for (int y = 56; y <= 59; y++)
                    for (int x = 72; x <= 75; x++)
                    {
                        int i = wa.Map.Idx(x, y);
                        wa.Map.Tiles[i] = Terrain.Dirt; wa.Map.Ore[i] = wa.Map.OreBase[i] = 200; wa.Map.OreType[i] = wa.Map.OreBaseType[i] = Map.Iron;
                    }
                wa.Map.FieldsChanged();
                wa.SpawnStructure(0, "outpost", new Int2(66, 56), 1f); // its own outpost beside the field
                foreach (var tr in wa.Owned(0).Where(e => e.IsHarvester)) tr.Pos = tr.PrevPos = tr.GuardPos = new Vec2(6, 6); // 83 tiles from it
                for (int y = 48; y <= 68; y++) for (int x = 58; x <= 79; x++) ta.Explored[wa.Map.Idx(x, y)] = true;
                if (foeThere) ta.KnownEnemyStructures[960] = ("command_center", new Int2(74, 48), 1); // (out of sight of the outpost, which would otherwise see it is not there)
                var sentIds = new HashSet<int>();
                AiRun(wa, aia, 90, () => { foreach (var e in wa.Owned(0).Where(e => e.IsHarvester && e.Order == Order.Harvest && Vec2.Dist(e.OrderPos, new Vec2(73, 57)) < 12f)) sentIds.Add(e.Id); });
                int sent = sentIds.Count;
                Check(foeThere ? sent == 0 : sent >= 3,
                      foeThere ? $"idle trucks aren't sent to a field beside an enemy structure they know of ({sent} sent)" : $"(control: with no enemy there, idle trucks are sent to the field by their outpost: {sent} sent)");
            }

            // Unseen ground the sample grids miss entirely (a 3x3 patch between the 4-tile samples) is still found, tile by tile.
            var (w10, ai10, t10, hq10) = Thin(1, 2, 3);
            Array.Fill(t10.Explored, true);
            for (int y = 29; y <= 31; y++) for (int x = 47; x <= 49; x++) t10.Explored[w10.Map.Idx(x, y)] = false; // no point of the 24- or 4-tile grids lies in it
            Entity drone10 = null;
            for (int s = 0; s < 90 && (drone10 == null || drone10.Order != Order.Move); s++) { AiRun(w10, ai10, 1); drone10 = w10.Owned(0).FirstOrDefault(e => e.Def.Key == "recon_drone"); }
            Check(drone10 != null && drone10.Order == Order.Move && Vec2.Dist(drone10.OrderPos, new Vec2(48, 30)) < 3f,
                  $"a 3x3 unseen patch that no grid point lies in is found by the tile-by-tile fallback: the drone is sent to {(drone10 != null ? drone10.OrderPos.ToString() : "nowhere")}, the patch is around (48,30)");

            // A coordinate harvest takes the nearest live ore within 12 tiles of the point, which need not be at the field's root: here the root and
            // the rows by it are mined out, the root is 15 tiles from an enemy structure it knows of, and the ore that is left lies on the side
            // towards it, 13 tiles away. Checking the root would send the trucks there; the ore tile itself is what is checked.
            foreach (bool safeTile in new[] { false, true })
            {
                var (wb, aib, tb, hqb) = Thin();
                for (int i = 0; i < wb.Map.Ore.Length; i++) wb.Map.Ore[i] = 0;
                for (int y = 56; y <= 59; y++)
                    for (int x = 72; x <= 75; x++)
                    {
                        int i = wb.Map.Idx(x, y);
                        wb.Map.Tiles[i] = Terrain.Dirt; wb.Map.OreBase[i] = 200; wb.Map.OreType[i] = wb.Map.OreBaseType[i] = Map.Iron;
                        wb.Map.Ore[i] = y >= 58 ? 200 : 0; // the near rows are mined out
                    }
                if (safeTile) wb.Map.Ore[wb.Map.Idx(75, 56)] = 200; // but one live tile on the far side of the field
                wb.Map.FieldsChanged();
                wb.SpawnStructure(0, "outpost", new Int2(66, 56), 1f);
                foreach (var tr in wb.Owned(0).Where(e => e.IsHarvester)) tr.Pos = tr.PrevPos = tr.GuardPos = new Vec2(6, 6);
                for (int y = 48; y <= 68; y++) for (int x = 58; x <= 79; x++) tb.Explored[wb.Map.Idx(x, y)] = true;
                tb.KnownEnemyStructures[960] = ("command_center", new Int2(72, 71), 1); // 15 tiles from the root, out of sight
                var firstTarget = new Dictionary<int, Vec2>();
                AiRun(wb, aib, 90, () => { foreach (var e in wb.Owned(0).Where(e => e.IsHarvester && e.Order == Order.Harvest && Vec2.Dist(e.OrderPos, new Vec2(73, 57)) < 12f)) if (!firstTarget.ContainsKey(e.Id)) firstTarget[e.Id] = e.OrderPos; });
                bool ok = safeTile ? firstTarget.Count >= 3 && firstTarget.Values.All(v => Vec2.Dist(v, new Vec2(75.5f, 56.5f)) < 1f) : firstTarget.Count == 0;
                Check(ok, safeTile ? $"with a live tile on the safe side of the field the trucks are sent to that tile ({firstTarget.Count} sent, to {string.Join(" ", firstTarget.Values.Distinct())})"
                                   : $"with all the ore that is left within reach of an enemy structure it knows of, no truck is sent, though the field's root is 15 tiles from it ({firstTarget.Count} sent, to {string.Join(" ", firstTarget.Values.Distinct())})");
            }

            // The electronics plant is needed to buy an outpost truck, not to send one it has already paid for (Deploy doesn't need it): with
            // the plant gone the truck it has still gets its destination, and no second one is bought.
            var (wc, aic, tc, hqc) = Thin();
            var paid = wc.SpawnUnit(0, "outpost_truck", hqc);
            foreach (var plant in wc.Owned(0).Where(e => e.Def.Key == "electronics_plant").ToList()) wc.Remove(plant);
            AiRun(wc, aic, 3); // (before the AI has built the plant again)
            Check(!wc.HasComplete(0, "electronics_plant") && paid.Order == Order.Move && Vec2.Dist(paid.OrderPos, tc.StartPos) > 30f && wc.Owned(0).Count(e => e.Def.Key == "outpost_truck") == 1 && !tc.UnitQueues[Producer.Factory].Any(q => q.Key == "outpost_truck"),
                  $"an outpost truck it has paid for gets a destination although the electronics plant is gone, and no second is bought ({paid.OrderName} to {paid.OrderPos}; plant standing: {wc.HasComplete(0, "electronics_plant")})");

            // A field seen only at its edges is a field: the two columns of it that are explored show iron, though the root (the tile that held
            // the most) is still in the fog. The AI goes for it rather than the free corner.
            var (wd, aid, td, hqd) = Thin();
            for (int y = 32; y <= 35; y++)
                for (int x = 55; x <= 58; x++)
                {
                    int i = wd.Map.Idx(x, y);
                    wd.Map.Tiles[i] = Terrain.Dirt; wd.Map.Ore[i] = wd.Map.OreBase[i] = 200; wd.Map.OreType[i] = wd.Map.OreBaseType[i] = Map.Iron;
                }
            wd.Map.FieldsChanged();
            for (int y = 28; y <= 38; y++) for (int x = 57; x <= 62; x++) td.Explored[wd.Map.Idx(x, y)] = true; // x 55 and 56 stay unseen, root (55,32) with them
            bool rootFogged = !td.Explored[wd.Map.Idx(55, 32)]; // (the truck's own sight clears it on arrival)
            Vec2? firstGoalD = null;
            AiRun(wd, aid, 2 * 60, () =>
            {
                var tk = wd.Owned(0).FirstOrDefault(e => e.Def.Key == "outpost_truck");
                if (tk != null && firstGoalD == null && tk.Order == Order.Move && Vec2.Dist(tk.OrderPos, td.StartPos) > 30f) firstGoalD = tk.OrderPos;
            });
            Check(rootFogged && firstGoalD.HasValue && Vec2.Dist(firstGoalD.Value, new Vec2(57, 34)) < 12f,
                  $"a field whose root is unseen but whose edge shows iron is recognised: the truck's first destination is {(firstGoalD.HasValue ? firstGoalD.Value.ToString() : "none")}, the field at (57,34)");

            // In an open arena the free base sites are kept for the players who join later (a newcomer takes a free site, and an outpost by it
            // would pin them or use up the seat; the ore there is theirs while they are protected). The house AI (passive, as here) still
            // expands, but only onto neutral fields, 18 tiles or more from every site that isn't its own. With the same ground a closed
            // match has it go to the free corner (above); here the nearest neutral field is the crystal ring's, in the middle.
            openArena = true;
            var (w5, ai5, t5, hq5) = Thin(1); // corner 1 is an enemy's, corners 2 and 3 are free sites
            var free5 = new[] { w5.Map.Spawns[2], w5.Map.Spawns[3] };
            float nearest5 = float.MaxValue; // the closest any outpost or any outpost truck's destination comes to a free site
            AiRun(w5, ai5, 6 * 60, () =>
            {
                foreach (var e in w5.Owned(0).Where(e => e.Def.Key == "outpost" || e.Def.Key == "outpost_truck"))
                    foreach (var s in free5) nearest5 = Math.Min(nearest5, Vec2.Dist(e.Def.Key == "outpost" ? e.Center : e.OrderPos, s));
            });
            var post5 = w5.Owned(0).FirstOrDefault(e => e.Def.Key == "outpost");
            Check(post5 != null && nearest5 >= 18f, $"in an open arena the house AI's outpost goes to a neutral field and never within 18 tiles of a free base site ({(post5 != null ? post5.Center.ToString() : "no outpost")}; closest approach {nearest5:0} tiles; free sites {free5[0]} and {free5[1]})");
            var (w6, ai6, t6, hq6) = Thin(1);
            var fieldAt = new Vec2(57, 34); // out of reach of its drop-offs, 29 tiles from the free site at 71,9
            for (int y = 32; y <= 35; y++)
                for (int x = 55; x <= 58; x++)
                {
                    int i = w6.Map.Idx(x, y);
                    w6.Map.Tiles[i] = Terrain.Dirt; w6.Map.Ore[i] = w6.Map.OreBase[i] = 200; w6.Map.OreType[i] = w6.Map.OreBaseType[i] = Map.Iron;
                }
            w6.Map.FieldsChanged();
            for (int y = 24; y <= 44; y++) for (int x = 47; x <= 67; x++) t6.Explored[w6.Map.Idx(x, y)] = true;
            float nearest6 = float.MaxValue;
            AiRun(w6, ai6, 6 * 60, () =>
            {
                foreach (var e in w6.Owned(0).Where(e => e.Def.Key == "outpost" || e.Def.Key == "outpost_truck"))
                    foreach (var s in free5) nearest6 = Math.Min(nearest6, Vec2.Dist(e.Def.Key == "outpost" ? e.Center : e.OrderPos, s));
            });
            var post6 = w6.Owned(0).FirstOrDefault(e => e.Def.Key == "outpost");
            Check(post6 != null && Vec2.Dist(post6.Center, fieldAt) < 12f && nearest6 >= 18f,
                  $"and given a neutral iron field out in the open it puts its outpost there ({(post6 != null ? post6.Center.ToString() : "no outpost")}, field at {fieldAt}; closest approach to a free site {nearest6:0} tiles)");

            // The 24-tile rule is about the field, but the outpost stands wherever placement finds open ground near it. A field 24.5 tiles from a
            // free site, ringed by rock but for a clearing on the side towards the site, would put the outpost 14 tiles from it: the spot it will
            // really stand on is what is checked, so that field is passed over for another.
            var (w7, ai7, t7, hq7) = Thin(1);
            var root7 = new Vec2(57.5f, 29.5f); var clearing7 = new Vec2(63.3f, 20.7f);
            for (int y = 14; y <= 46; y++)
                for (int x = 44; x <= 72; x++)
                {
                    var c = new Vec2(x + 0.5f, y + 0.5f); int i = w7.Map.Idx(x, y);
                    if (Vec2.Dist(c, root7) <= 13.5f && Vec2.Dist(c, clearing7) > 3.5f && w7.Map.Occupant[i] == 0) w7.Map.Tiles[i] = Terrain.Rock;
                }
            for (int y = 29; y <= 32; y++)
                for (int x = 57; x <= 60; x++)
                {
                    int i = w7.Map.Idx(x, y);
                    w7.Map.Tiles[i] = Terrain.Dirt; w7.Map.Ore[i] = w7.Map.OreBase[i] = 200; w7.Map.OreType[i] = w7.Map.OreBaseType[i] = Map.Iron;
                }
            var other7 = new Vec2(30, 55); // and a field with clear ground round it, 26 tiles from the free site at 9,71
            for (int y = 54; y <= 57; y++)
                for (int x = 28; x <= 31; x++)
                {
                    int i = w7.Map.Idx(x, y);
                    w7.Map.Tiles[i] = Terrain.Dirt; w7.Map.Ore[i] = w7.Map.OreBase[i] = 200; w7.Map.OreType[i] = w7.Map.OreBaseType[i] = Map.Iron;
                }
            w7.Map.FieldsChanged();
            for (int y = 14; y <= 46; y++) for (int x = 44; x <= 72; x++) t7.Explored[w7.Map.Idx(x, y)] = true;
            for (int y = 45; y <= 65; y++) for (int x = 20; x <= 40; x++) t7.Explored[w7.Map.Idx(x, y)] = true;
            float nearest7 = float.MaxValue; Vec2? firstGoal7 = null;
            AiRun(w7, ai7, 6 * 60, () =>
            {
                foreach (var e in w7.Owned(0).Where(e => e.Def.Key == "outpost" || e.Def.Key == "outpost_truck"))
                {
                    var at = e.Def.Key == "outpost" ? e.Center : e.OrderPos;
                    if (e.Def.Key == "outpost_truck" && e.Order == Order.Move && Vec2.Dist(at, t7.StartPos) > 20f && firstGoal7 == null) firstGoal7 = at; // (its first Move is the factory's rally)
                    foreach (var s in free5) nearest7 = Math.Min(nearest7, Vec2.Dist(at, s));
                }
            });
            Check(firstGoal7.HasValue && Vec2.Dist(firstGoal7.Value, other7) < 12f && nearest7 >= 18f,
                  $"a field that passes the 24-tile rule but whose only open ground is 14 tiles from a free site is passed over for the next: the truck's first destination is {(firstGoal7.HasValue ? firstGoal7.Value.ToString() : "none")}, the clear field at {other7} (the ringed one at {root7}); closest approach to a free site {nearest7:0} tiles");

            // A truck deploys its outpost 2x2 on its own tile, so the outpost's centre is not where the truck stands: with the truck 12.8 tiles
            // west and 13.2 south of the free site (70.5,70.5) it is 18.4 tiles from it, but the outpost would be centred at 58,58, 17.7 away.
            // It is the centre that is checked.
            var (w11, ai11, t11, hq11) = Thin(); // (no enemy structure known, so the arena rule is what refuses)
            var rolled = w11.SpawnUnit(0, "outpost_truck", hq11);
            var site11 = w11.Map.Spawns[1];
            var stands = new Vec2(site11.X - 12.8f, site11.Y - 13.2f);
            rolled.Pos = rolled.PrevPos = rolled.GuardPos = stands; w11.SetOrder(rolled, Order.Idle, rolled.Pos);
            var saved11 = (Dictionary<string, object>)Json.Parse(Json.Write(ai11.SaveState()));
            saved11["outpost_targets"] = new List<object> { new List<object> { (double)rolled.Id, (double)stands.X, (double)stands.Y } }; // it has just arrived where it was sent
            var ai11b = SimpleAI.LoadState(saved11);
            Check(Vec2.Dist(stands, site11) >= 18f && Vec2.Dist(new Vec2(MathF.Floor(stands.X) + 1f, MathF.Floor(stands.Y) + 1f), site11) < 18f, "(set up: the truck is 18 tiles or more from the site, the outpost would be less)");
            AiRun(w11, ai11b, 3);
            Check(!w11.Owned(0).Any(e => e.Def.Key == "outpost") && !rolled.Dead && rolled.Def.Key == "outpost_truck",
                  $"it doesn't deploy where the outpost's centre would be within 18 tiles of a site that isn't its own ({w11.Owned(0).Count(e => e.Def.Key == "outpost")} outposts)");
            openArena = false;

            // The factory is missing and iron ore piles up past 1500: an extra refinery is wanted, but holding back the 100 copper ore it
            // costs would keep the copper (100 for the factory) from ever being refined from what little copper ore there is.
            var (w4, ai4) = AiWorld();
            var t4 = w4.Teams[0];
            AiBase(w4, "power_plant", "mining_refinery", "barracks");
            foreach (var e in w4.Owned(0).Where(e => e.IsHarvester).ToList()) w4.Remove(e);
            t4.Add("copper_ore", 100 - t4.Amount("copper_ore")); t4.Add("copper", 20 - t4.Amount("copper")); t4.Add("steel", 600);
            AiRun(w4, ai4, 5, () => t4.Add("iron_ore", 1800 - t4.Amount("iron_ore")));
            int holdCu = t4.Reserve.TryGetValue("copper_ore", out var h4) ? h4 : 0;
            Check(Have(w4, "factory") == 0 && holdCu < 100, $"with the factory missing it doesn't hold back copper ore for an optional second refinery (reserve {holdCu})");
            AiRun(w4, ai4, 150, () => t4.Add("iron_ore", 1800 - t4.Amount("iron_ore")));
            Check(Have(w4, "factory") == 1, $"so the 80 copper ore is refined into the copper the factory needs, and the factory goes up ({t4.Amount("copper")} copper, {Have(w4, "factory")} factories after 2.5 minutes)");
        }

        /// <summary>Selling a mine must never cost the AI the game, a retired mine needs its paid replacement first, and a raw-ore shortage earns a slot like an empty stockpile.</summary>
        static void AiPlanEdges()
        {
            // The dry mine is the last structure standing and a rig is on its way to a fresh deposit: selling it would eliminate the team
            // (a drill rig doesn't keep a team in the game), so it stays until the new mine is up.
            var (w, ai) = AiWorld();
            var t = w.Teams[0];
            for (int i = 0; i < w.Map.Ore.Length; i++) w.Map.Ore[i] = 0;
            var deps = w.Map.Deep.OrderBy(d => Vec2.Dist(d.Pos, t.StartPos)).Take(2).ToList();
            foreach (var d in deps) { d.Type = Map.Iron; d.Amount = d.Initial = 6000; t.Surveyed.Add(d.Id); t.Zones[d.Id] = new ZoneFlag { ZoneId = d.Id, FlaggedBy = 0, FlaggedAt = w.Time }; }
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var first = At(w.SpawnUnit(0, "drill_rig", hq), deps[0].Pos + new Vec2(1, 0));
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { first.Id }))), "(a mine on the first deposit)");
            var spare = At(w.SpawnUnit(0, "drill_rig", hq), t.StartPos + new Vec2(4, 4));
            w.SetOrder(spare, Order.Idle, spare.Pos);
            w.Remove(hq);
            foreach (var e in w.Owned(0).Where(e => e.IsStructure && e.Def.Key != "deep_mine").ToList()) w.Remove(e);
            deps[0].Amount = 0; // the mine has run dry: it is the only structure left
            AiRun(w, ai, 60);
            Check(!t.Defeated && w.Owned(0).Any(e => e.Def.Key == "deep_mine" && e.DepositId == deps[1].Id), $"a dry mine that is the last structure is kept until the new one is up ({(t.Defeated ? "the team was eliminated" : "still in the game")}, {w.Owned(0).Count(e => e.Def.Key == "deep_mine")} mines)");

            // Four iron mines, no copper, 800 steel and a step waiting on copper: the iron mine is only retired once the rig that replaces
            // it is bought (the budget used to be checked, then spent on something else, and the mine was gone for nothing).
            var (w2, ai2, t2, d2) = AiDeepBase(Map.Iron, Map.Iron, Map.Iron, Map.Iron, Map.Copper);
            for (int i = 0; i < 4; i++) AiPump(w2, d2[i]);
            t2.Add("copper_ore", -t2.Amount("copper_ore")); t2.Add("steel", 800); t2.Add("circuits", 300);
            AiRun(w2, ai2, 1); // its first think
            int ironNow = w2.Owned(0).Count(e => e.Def.Key == "deep_mine");
            bool paid = w2.Teams[0].UnitQueues[Producer.Factory].Any(q => q.Key == "drill_rig") || w2.Owned(0).Any(e => e.Def.Key == "drill_rig");
            Check(ironNow == 4 && paid, $"it buys the replacement rig first and keeps all four mines meanwhile ({ironNow} mines, rig bought {paid})");
            AiRun(w2, ai2, 5 * 60);
            Check(w2.Owned(0).Any(e => e.Def.Key == "deep_mine" && w2.Map.DepositById(e.DepositId).Type == Map.Copper) && w2.Owned(0).Count(e => e.Def.Key == "deep_mine" && w2.Map.DepositById(e.DepositId).Type == Map.Iron) >= 3,
                  $"and then swaps one iron mine for a copper one ({w2.Owned(0).Count(e => e.Def.Key == "deep_mine")} mines)");

            // Four iron mines pump, a rig stands paid for and idle, there is plenty of refined copper but no copper ore, and a refinery is
            // waiting on 100 copper ore: that raw shortage earns the copper zone a slot as an empty stockpile would.
            var (w3, ai3, t3, d3) = AiDeepBase(Map.Iron, Map.Iron, Map.Iron, Map.Iron, Map.Copper);
            AiBase(w3, "gun_turret");
            for (int i = 0; i < 4; i++) AiPump(w3, d3[i]);
            var idle = At(w3.SpawnUnit(0, "drill_rig", w3.Owned(0).First(e => e.Def.Key == "command_center")), t3.StartPos + new Vec2(4, 4));
            w3.SetOrder(idle, Order.Idle, idle.Pos);
            t3.Add("copper", 3000); t3.Add("copper_ore", -t3.Amount("copper_ore")); t3.Add("steel", 500); t3.Add("iron_ore", 1000); t3.Add("circuits", 300);
            AiRun(w3, ai3, 5);
            Check(idle.Order == Order.Drill && idle.ZoneId == d3[4].Id, $"3000 copper but none of the copper ore a refinery is waiting on, four iron mines up: the idle rig goes to the copper zone ({idle.OrderName}, zone {idle.ZoneId})");
        }

        /// <summary>The structure it builds is PickStep's step unless something urgent goes first, and that must be buildable: a blocked override never hides the step.</summary>
        static void AiBuildOverrides()
        {
            // The last power plant dies while a fueled fusion reactor covers the demand; iron ore piles up past 1500 and there is copper ore
            // for a refinery. The "another refinery" override used to win and then be refused (a refinery needs a power plant), every second,
            // so the replacement plant was never built while the iron lasted.
            var (w, ai) = AiWorld();
            var t = w.Teams[0];
            AiBase(w, "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant", "enrichment_plant", "fusion_reactor");
            t.Add("plasma", 500); t.Add("iron_ore", 6000); t.Add("copper_ore", 3000); t.Add("steel", 1000); t.Add("circuits", 300);
            Array.Fill(t.Explored, true);
            Run(w, 1); // the reactor lights up
            w.Remove(w.Owned(0).First(e => e.Def.Key == "power_plant"));
            AiRun(w, ai, 60);
            Check(Have(w, "power_plant") >= 1 && !t.LowPower, $"with the iron ore piling up and the reactor holding the power, the lost power plant is still rebuilt ({Have(w, "power_plant")} plants, {t.Amount("iron_ore")} iron ore)");
        }

        /// <summary>A rig that exists always gets a free zone while the mines and rigs already on their way are under the cap, however many rigs are counted against it.</summary>
        static void AiRigDestinations()
        {
            (World w, SimpleAI ai, Team t, List<DeepDeposit> deps) Base(int zones)
            {
                var (w, ai) = AiWorld();
                var t = w.Teams[0];
                AiBase(w, "power_plant", "power_plant", "power_plant", "mining_refinery", "barracks", "factory", "electronics_plant");
                for (int i = 0; i < w.Map.Ore.Length; i++) w.Map.Ore[i] = 0;
                var deps = w.Map.Deep.OrderBy(d => Vec2.Dist(d.Pos, t.StartPos)).Take(zones).ToList();
                for (int i = 0; i < deps.Count; i++) { deps[i].Type = i % 2 == 0 ? Map.Iron : Map.Copper; deps[i].Amount = deps[i].Initial = 6000; t.Surveyed.Add(deps[i].Id); t.Zones[deps[i].Id] = new ZoneFlag { ZoneId = deps[i].Id, FlaggedBy = 0, FlaggedAt = w.Time }; }
                t.Add("iron_ore", 1500); t.Add("copper", 500); t.Add("circuits", 300);
                return (w, ai, t, deps);
            }
            void Mine(World w, DeepDeposit d)
            {
                var r = At(w.SpawnUnit(0, "drill_rig", w.Owned(0).First(e => e.Def.Key == "command_center")), d.Pos + new Vec2(1, 0));
                Check(Ok(Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { r.Id }))), "(a mine up)");
            }

            // Three iron / copper mines pump and a fourth rig stands idle: with the rig counted that is the cap, but it must still be sent.
            var (w1, ai1, t1, d1) = Base(5);
            for (int i = 0; i < 3; i++) Mine(w1, d1[i]);
            var idle = At(w1.SpawnUnit(0, "drill_rig", w1.Owned(0).First(e => e.Def.Key == "command_center")), w1.Teams[0].StartPos + new Vec2(4, 4));
            w1.SetOrder(idle, Order.Idle, idle.Pos);
            AiRun(w1, ai1, 5);
            Check(idle.Order == Order.Drill && (idle.ZoneId == d1[3].Id || idle.ZoneId == d1[4].Id), $"3 mines and an idle rig: the rig is sent to a free zone ({idle.OrderName}, zone {idle.ZoneId})");

            // Two mines pump and two more rigs are on order (800 steel each): when they come out they must be sent too.
            var (w2, ai2, t2, d2) = Base(5);
            for (int i = 0; i < 2; i++) Mine(w2, d2[i]);
            t2.Add("steel", 1700);
            Check(Ok(Commands.Execute(w2, 0, Cmd("type", "train", "unit", "drill_rig", "count", 2))), "(two rigs queued)");
            AiRun(w2, ai2, 3 * 60);
            Check(w2.Owned(0).Count(e => e.Def.Key == "deep_mine") == 4, $"2 mines and 2 rigs on order: all four end up pumping ({w2.Owned(0).Count(e => e.Def.Key == "deep_mine")} mines, {w2.Owned(0).Count(e => e.Def.Key == "drill_rig")} rigs left)");

            // The factory is gone, but a rig that already exists can still be sent.
            var (w3, ai3, t3, d3) = Base(1);
            var rig3 = At(w3.SpawnUnit(0, "drill_rig", w3.Owned(0).First(e => e.Def.Key == "command_center")), w3.Teams[0].StartPos + new Vec2(4, 4));
            w3.SetOrder(rig3, Order.Idle, rig3.Pos);
            w3.Remove(w3.Owned(0).First(e => e.Def.Key == "factory"));
            AiRun(w3, ai3, 5);
            Check(rig3.Order == Order.Drill && rig3.ZoneId == d3[0].Id, $"with the factory destroyed an existing rig is still sent to its zone ({rig3.OrderName})");
        }

        /// <summary>Which zones the AI's drill rigs go to: a raw-ore shortage counts, a stopped rig doesn't hold an ore, and one mine per crystal / uranium holds within a tick.</summary>
        static void AiDrillChoices()
        {
            // A base with the surface mined out and two deep zones flagged; `refineries` more refineries built (the build order's
            // refinery step is done, so nothing waits on copper ore).
            (World w, SimpleAI ai, Team t, List<DeepDeposit> deps) Dry(int refineries)
            {
                var (w, ai) = AiWorld();
                var t = w.Teams[0];
                AiBase(w, "power_plant", "mining_refinery", "barracks", "factory", "power_plant", "electronics_plant", "gun_turret");
                if (refineries > 0) AiBase(w, "mining_refinery", "power_plant");
                for (int i = 0; i < w.Map.Ore.Length; i++) w.Map.Ore[i] = 0;
                var deps = w.Map.Deep.OrderBy(d => Vec2.Dist(d.Pos, t.StartPos)).Take(2).ToList();
                foreach (var d in deps) { t.Surveyed.Add(d.Id); t.Zones[d.Id] = new ZoneFlag { ZoneId = d.Id, FlaggedBy = 0, FlaggedAt = w.Time }; }
                return (w, ai, t, deps);
            }
            bool GoesTo(World x, DeepDeposit d) => x.Owned(0).Any(e => (e.Def.Key == "drill_rig" && e.ZoneId == d.Id) || (e.Def.Key == "deep_mine" && e.DepositId == d.Id));

            // A refinery waiting on 100 copper ore, with plenty of refined copper in hand and no surface ore left: copper deposits are
            // still worth a rig (refined copper can't pay a refinery's copper ore).
            var (w1, ai1, t1, d1) = Dry(0);
            d1[0].Type = Map.Copper; d1[1].Type = Map.Copper;
            t1.Add("copper", 3000); t1.Add("iron_ore", 1000); t1.Add("steel", 1000); t1.Add("copper_ore", -t1.Amount("copper_ore"));
            AiRun(w1, ai1, 90);
            Check(GoesTo(w1, d1[0]), $"with 3000 copper but no copper ore for its second refinery and no surface ore left, a rig goes to the copper zone ({w1.Owned(0).Count(e => e.Def.Key == "drill_rig")} rigs, {w1.Owned(0).Count(e => e.Def.Key == "deep_mine")} mines)");
            var (w1b, ai1b, t1b, d1b) = Dry(1);
            d1b[0].Type = Map.Copper; d1b[1].Type = Map.Copper;
            t1b.Add("copper", 3000); t1b.Add("iron_ore", 1000); t1b.Add("steel", 1000); t1b.Add("copper_ore", -t1b.Amount("copper_ore"));
            AiRun(w1b, ai1b, 90);
            Check(!GoesTo(w1b, d1b[0]) && !GoesTo(w1b, d1b[1]), "but with no refinery waiting on copper ore, copper zones are not worth a rig while copper is plentiful");

            // A crystal rig that was stopped keeps its old zone but isn't drilling: its ore is free again, and the rig gets a zone.
            var (w2, ai2, t2, d2) = Dry(0);
            AiBase(w2, "optics_lab");
            d2[0].Type = Map.Crystal; t2.Surveyed.Remove(d2[1].Id); // the crystal zone is the only one it knows
            t2.Add("steel", 1000); t2.Add("iron_ore", 1000);
            var hq = w2.Owned(0).First(e => e.Def.Key == "command_center");
            var stopped = At(w2.SpawnUnit(0, "drill_rig", hq), hq.Center + new Vec2(4, 4));
            w2.SetOrder(stopped, Order.Idle, stopped.Pos);
            stopped.ZoneId = d2[0].Id; // told to drill there, then stopped
            AiRun(w2, ai2, 3);
            Check(stopped.Order == Order.Drill && stopped.ZoneId == d2[0].Id, $"a stopped rig doesn't keep its crystal reserved against itself: it is sent to that zone ({stopped.OrderName}, zone {stopped.ZoneId})");

            // Two idle rigs and two crystal zones in the same tick: one mine on crystal, so only one rig goes for it.
            var (w3, ai3, t3, d3) = Dry(0);
            AiBase(w3, "optics_lab");
            d3[0].Type = Map.Crystal; d3[1].Type = Map.Crystal;
            t3.Add("steel", 1000); t3.Add("iron_ore", 1000);
            var hq3 = w3.Owned(0).First(e => e.Def.Key == "command_center");
            var rigs3 = Enumerable.Range(0, 2).Select(i => At(w3.SpawnUnit(0, "drill_rig", hq3), hq3.Center + new Vec2(4 + i, 4))).ToList();
            foreach (var r in rigs3) w3.SetOrder(r, Order.Idle, r.Pos);
            ai3.Update(w3);
            Check(rigs3.Count(r => r.Order == Order.Drill) == 1 && rigs3.Count(r => r.Order == Order.Idle) == 1,
                  $"one mine on crystal at a time, even for two rigs sent in the same tick ({string.Join(", ", rigs3.Select(r => r.OrderName))})");
        }

        /// <summary>What the arena gives a team (starting stock and base, a late joiner's kit) can't be cashed out or farmed for score.</summary>
        static void GrantedValue()
        {
            Console.WriteLine("\n-- granted value: starting and catch-up gifts are worth nothing as salvage, kill value or score");
            long[] OreByType(World x) { var n = new long[4]; for (int i = 0; i < x.Map.Ore.Length; i++) if (x.Map.Ore[i] > 0) n[x.Map.OreType[i]] += x.Map.Ore[i]; return n; }
            float OreWorth(string item) => World.OreMix[item].Sum();
            float OwnWorth(Team t) => t.Stock.Keys.Sum(k => t.Own(k) * OreWorth(k));

            // Join and leave straight away, at arena ages 0, 5 and 30 minutes: nothing from the grant is left as salvage
            // (it was 1,990, 3,103 and 6,255 ore before: everything the newcomer was given).
            foreach (int minutes in new[] { 0, 5, 30 })
            {
                var w = new World(2, 7, 96) { Open = true };
                w.Tick = minutes * 60 * World.TickRate;
                var alt = w.AddTeam("llm", "Alt", out var err);
                int granted = w.Owned(alt.Id).Count(e => e.GrantedShare >= 1f), all = w.Owned(alt.Id).Count();
                float given = alt.Stock.Keys.Sum(k => alt.Stock[k] * OreWorth(k));
                long before = OreByType(w).Sum();
                w.Leave(alt.Id);
                long left = OreByType(w).Sum() - before;
                Check(alt != null && granted == all && all >= 4 && left == 0, $"joining at {minutes} min and leaving at once leaves no salvage though the newcomer was given {(int)given} ore of stock and {all} gifted things ({left} ore)");
            }

            // Only what the team made or mined itself comes out of its base: 500 steel it mined, and a tank it built.
            var w2 = new World(2, 7, 96) { Open = true };
            w2.Tick = 30 * 60 * World.TickRate;
            var alt2 = w2.AddTeam("llm", "Alt", out _);
            alt2.Add("steel", 500);
            w2.SpawnUnit(alt2.Id, "light_tank", w2.Owned(alt2.Id).First(e => e.Def.Key == "command_center"));
            long before2 = OreByType(w2).Sum();
            w2.Leave(alt2.Id);
            long left2 = OreByType(w2).Sum() - before2;
            Check(left2 >= 600 && left2 <= 620, $"a leaver's own 500 steel and a light tank it built (half of 240) are salvage, its gift is not ({left2} ore of 620)");

            // Losing the last command center: the same rule for the spill, and a team holding only its gift is told so.
            var w3 = new World(2, 7, 96) { Open = true };
            w3.Tick = 5 * 60 * World.TickRate;
            var alt3 = w3.AddTeam("llm", "Alt", out _);
            alt3.ProtectedUntil = 0;
            w3.SpawnStructure(alt3.Id, "barracks", w3.FindPlacement(alt3.Id, "barracks").Value, 1f); // something else to stand on
            var cc3 = w3.Owned(alt3.Id).First(e => e.Def.Key == "command_center");
            long before3 = OreByType(w3).Sum();
            Commands.Execute(w3, alt3.Id, Cmd("type", "sell", "structure_id", cc3.Id, "confirm", true));
            Check(OreByType(w3).Sum() == before3 && alt3.Stock.Count == 0 && w3.Alerts.Active(w3, alt3.Id).Any(a => a.Kind == "stockpile_lost" && a.Lost.Any(l => l.Contains("only what you were given"))),
                  "selling the last command center with only the gift in the stockpile spills nothing, and says the stockpile is gone");
            var w4 = new World(2, 7, 96) { Open = true };
            w4.Tick = 5 * 60 * World.TickRate;
            var alt4 = w4.AddTeam("llm", "Alt", out _);
            alt4.ProtectedUntil = 0;
            w4.SpawnStructure(alt4.Id, "barracks", w4.FindPlacement(alt4.Id, "barracks").Value, 1f);
            alt4.Add("iron_ore", 300); // mined on top of the gift
            long before4 = OreByType(w4).Sum();
            Commands.Execute(w4, alt4.Id, Cmd("type", "sell", "structure_id", w4.Owned(alt4.Id).First(e => e.Def.Key == "command_center").Id, "confirm", true));
            long spilled4 = OreByType(w4).Sum() - before4;
            Check(spilled4 >= 296 && spilled4 <= 300, $"and with 300 ore mined on top of the gift, those 300 spill and the gift doesn't ({spilled4})");

            // Selling a gifted building refunds the gift as gift: more to spend, nothing that could spill.
            var w5 = new World(2, 7, 96) { Open = true };
            w5.Tick = 5 * 60 * World.TickRate;
            var alt5 = w5.AddTeam("llm", "Alt", out _);
            alt5.ProtectedUntil = 0;
            var gift5 = w5.Owned(alt5.Id).First(e => e.Def.Key == "power_plant" && e.GrantedShare >= 1f);
            int iron5 = alt5.Amount("iron_ore"); float giftIron5 = alt5.Gift("iron_ore"), ownIron5 = alt5.Own("iron_ore");
            var sold5 = Commands.Execute(w5, alt5.Id, Cmd("type", "sell", "structure_id", gift5.Id, "confirm", true));
            Check(Ok(sold5) && alt5.Amount("iron_ore") == iron5 + 125 && alt5.Gift("iron_ore") == giftIron5 + 125 && alt5.Own("iron_ore") == ownIron5,
                  $"selling the gifted power plant refunds 125 iron ore, all of it as gift, none as the team's own: {Said(sold5)}");

            // Killing a fresh team's starting units earns nothing: no kill value, no salvage. A unit it built itself still does.
            var w6 = new World(2, 7, 96);
            w6.Teams[0].ProtectedUntil = 0; w6.Teams[1].ProtectedUntil = 0;
            var shooter = w6.SpawnUnit(0, "heavy_tank", w6.Owned(0).First(e => e.Def.Key == "command_center"));
            long ore6 = OreByType(w6).Sum();
            foreach (var victim in w6.Owned(1).Where(e => e.GrantedShare >= 1f).ToList()) w6.Hurt(victim, 1e6f, shooter);
            Check(!w6.Owned(1).Any() && w6.Teams[0].Stats.KillValue == 0 && w6.Teams[0].Stats.SalvageLeft == 0 && OreByType(w6).Sum() == ore6,
                  $"destroying a fresh team's starting command center, truck and riflemen gives no kill value and no salvage ({w6.Teams[0].Stats.KillValue}, {OreByType(w6).Sum() - ore6} ore)");
            var built6 = w6.SpawnUnit(1, "light_tank", w6.SpawnStructure(1, "barracks", Int2.Of(OpenGround(w6)), 1f));
            w6.Hurt(built6, 1e6f, shooter);
            Check(w6.Teams[0].Stats.KillValue == 240, $"but a tank it trained is worth its 240 ({w6.Teams[0].Stats.KillValue})");

            // A gifted structure stays a gift in an enemy's hands.
            var w7 = new World(2, 7, 80);
            w7.Teams[0].ProtectedUntil = 0; w7.Teams[1].ProtectedUntil = 0;
            var hq7 = w7.Owned(1).First(e => e.Def.Key == "command_center");
            w7.SpawnStructure(1, "power_plant", w7.FindPlacement(1, "power_plant").Value, 1f);
            foreach (var u in w7.Entities.Where(e => !e.IsStructure).ToList()) w7.Remove(u);
            hq7.Hp = hq7.Def.MaxHp * 0.3f;
            var eng7 = At(w7.SpawnUnit(0, "engineer", w7.Owned(0).First(e => e.Def.Key == "command_center")), hq7.Center + new Vec2(2.5f, 0));
            w7.UpdateVisibility();
            Commands.Execute(w7, 0, Cmd("type", "capture", "units", new[] { eng7.Id }, "target", hq7.Id));
            Run(w7, 20);
            var raider7 = w7.SpawnUnit(1, "heavy_tank", w7.Owned(1).First(e => e.IsStructure));
            w7.Hurt(hq7, 1e6f, raider7);
            Check(hq7.Team == 0 && hq7.GrantedShare == 1f && hq7.Dead && w7.Teams[1].Stats.KillValue == 0, $"a captured gifted command center is still worth nothing when its old owner destroys it ({w7.Teams[1].Stats.KillValue})");

            // Refining the gift doesn't make it the team's own: converters pass the granted share on to what they make.
            var w8 = new World(2, 7, 96);
            w8.Teams[0].ProtectedUntil = 0; w8.Teams[1].ProtectedUntil = 0;
            foreach (var u in w8.Entities.Where(e => !e.IsStructure).ToList()) w8.Remove(u); // no trucks mining
            w8.SpawnStructure(1, "power_plant", w8.FindPlacement(1, "power_plant").Value, 1f);
            var refinery = w8.SpawnStructure(1, "mining_refinery", w8.FindPlacement(1, "mining_refinery").Value, 1f);
            foreach (var u in w8.Entities.Where(e => !e.IsStructure).ToList()) w8.Remove(u); // and not the refinery's free one
            var t8 = w8.Teams[1];
            Run(w8, 60);
            // Only the command center's trickle (1 iron ore a second) is the team's own; the rest is the starting 500 iron and 150 copper, however refined.
            Check(t8.Amount("steel") > 100 && OwnWorth(t8) <= 70f, $"after refining {t8.Amount("steel")} steel from the starting ore, only the trickle ({OwnWorth(t8):0} ore) counts as the team's own");
            long before8 = OreByType(w8).Sum();
            w8.Leave(1);
            long left8 = OreByType(w8).Sum() - before8;
            Check(left8 >= 430 && left8 <= 460, $"so leaving dismantles only that trickle (60) and the power plant and refinery it built (60% of 250 + 400: about 450; {left8} ore)");

            // Spending takes from the gift first: two power plants paid with the whole starting 500 iron ore use it up, and the
            // next 500 the team mines is its own (it spills, and isn't taken for gift when refined).
            var w10 = new World(2, 7, 96);
            w10.Teams[0].ProtectedUntil = 0; w10.Teams[1].ProtectedUntil = 0;
            var t10 = w10.Teams[1];
            var p1 = Commands.Execute(w10, 1, Cmd("type", "build", "structure", "power_plant"));
            var p2 = Commands.Execute(w10, 1, Cmd("type", "build", "structure", "power_plant"));
            var plants10 = w10.Owned(1).Where(e => e.Def.Key == "power_plant").ToList();
            Check(Ok(p1) && Ok(p2) && plants10.Count == 2 && plants10.All(e => e.GrantedShare == 1f) && t10.Gift("iron_ore") == 0 && t10.Amount("iron_ore") == 0,
                  $"two power plants paid wholly from the starting iron ore use the gift up and carry its whole share ({string.Join(", ", plants10.Select(e => e.GrantedShare))}; gift left {t10.Gift("iron_ore")})");
            t10.Add("iron_ore", 500); // mined
            Check(t10.Own("iron_ore") == 500 && OwnWorth(t10) >= 500f, $"the 500 iron ore it mines next is its own, not taken for the spent gift ({t10.Own("iron_ore")})");
            long before10 = OreByType(w10).Sum();
            foreach (var e in w10.Owned(1).Where(e => e.Def.Key == "power_plant").ToList()) w10.Hurt(e, 1e6f, w10.SpawnUnit(0, "heavy_tank", w10.Owned(0).First(x => x.Def.Key == "command_center")));
            Check(w10.Teams[0].Stats.KillValue == 0, $"the plants it paid for with the gift are worth nothing as kills ({w10.Teams[0].Stats.KillValue})");
            w10.Leave(1);
            long left10 = OreByType(w10).Sum() - before10;
            Check(left10 >= 490 && left10 <= 500, $"and a leave then spills just that mined 500 ({left10})");

            // A purchase part-paid from the gift carries that share: 100 of the 250 iron ore for a power plant.
            var w11 = new World(2, 7, 96);
            w11.Teams[0].ProtectedUntil = 0; w11.Teams[1].ProtectedUntil = 0;
            var t11 = w11.Teams[1];
            t11.Granted["iron_ore"] = 100; // (stock is the starting 500)
            Commands.Execute(w11, 1, Cmd("type", "build", "structure", "power_plant"));
            var plant11 = w11.Owned(1).First(e => e.Def.Key == "power_plant");
            w11.Hurt(plant11, 1e6f, w11.SpawnUnit(0, "heavy_tank", w11.Owned(0).First(x => x.Def.Key == "command_center")));
            Check(Math.Abs(plant11.GrantedShare - 0.4f) < 0.001f && t11.Gift("iron_ore") == 0 && w11.Teams[0].Stats.KillValue >= 149 && w11.Teams[0].Stats.KillValue <= 150,
                  $"a 250-ore plant paid 100 from the gift carries 0.4 of it and is worth the other 150 as a kill ({plant11.GrantedShare}; {w11.Teams[0].Stats.KillValue})");

            // Refunds give the gift back as gift; a unit trained wholly from catch-up steel gives no kill value.
            var w12 = new World(2, 7, 96) { Open = true };
            w12.Tick = 30 * 60 * World.TickRate;
            var alt12 = w12.AddTeam("llm", "Alt", out _);
            alt12.ProtectedUntil = 0; w12.Teams[0].ProtectedUntil = 0;
            w12.SpawnStructure(alt12.Id, "barracks", w12.FindPlacement(alt12.Id, "barracks").Value, 1f);
            float giftSteel = alt12.Gift("steel"), ownSteel = alt12.Own("steel");
            Commands.Execute(w12, alt12.Id, Cmd("type", "train", "unit", "rifleman"));
            var queued12 = alt12.UnitQueues[Producer.Barracks].FirstOrDefault();
            Check(queued12 != null && queued12.GrantedShare == 1f && alt12.Gift("steel") == giftSteel - 40, $"a rifleman paid from the catch-up steel is queued with its whole share ({queued12?.GrantedShare}) and takes 40 from the gift");
            Commands.Execute(w12, alt12.Id, Cmd("type", "cancel", "unit", "rifleman"));
            Check(alt12.Gift("steel") == giftSteel && alt12.Own("steel") == ownSteel, $"cancelling it gives the 40 steel back as gift, not as the team's own ({alt12.Gift("steel")} of {giftSteel}; own {alt12.Own("steel")})");
            float giftIron12 = alt12.Gift("iron_ore"), ownIron12 = alt12.Own("iron_ore");
            Commands.Execute(w12, alt12.Id, Cmd("type", "build", "structure", "power_plant"));
            var bought12 = w12.Owned(alt12.Id).Where(e => e.Def.Key == "power_plant").OrderByDescending(e => e.Id).First();
            Commands.Execute(w12, alt12.Id, Cmd("type", "sell", "structure_id", bought12.Id, "confirm", true));
            Check(bought12.GrantedShare == 1f && alt12.Gift("iron_ore") == giftIron12 && alt12.Own("iron_ore") == ownIron12, $"selling a plant bought from the gift gives its iron ore back as gift ({alt12.Gift("iron_ore")} of {giftIron12}; own {alt12.Own("iron_ore")})");
            Commands.Execute(w12, alt12.Id, Cmd("type", "train", "unit", "rifleman"));
            Run(w12, 20);
            var trained12 = w12.Owned(alt12.Id).Where(e => e.Def.Key == "rifleman").OrderByDescending(e => e.Id).FirstOrDefault(); // (the newest: the starting two are older)
            var killer12 = At(w12.SpawnUnit(0, "heavy_tank", w12.Owned(0).First(x => x.Def.Key == "command_center")), alt12.StartPos + new Vec2(6, 0));
            if (trained12 != null) w12.Hurt(trained12, 1e6f, killer12);
            Check(trained12 != null && trained12.GrantedShare == 1f && w12.Teams[0].Stats.KillValue == 0 && w12.Teams[0].Stats.SalvageLeft == 0,
                  $"a unit trained wholly from catch-up steel gives its killer no kill value and no salvage, like the starting units ({trained12?.GrantedShare}; {w12.Teams[0].Stats.KillValue})");

            // Salvage picked up is real ore, delivered or not: it counts against the economy score only once it's delivered.
            var w9 = new World(2, 7, 96);
            var spot = OpenGround(w9);
            var tile = Int2.Of(spot);
            int ti = w9.Map.Idx(tile.X, tile.Y);
            w9.Map.Ore[ti] = 300; w9.Map.OreType[ti] = Map.Iron; w9.Map.Salvage[ti] = 300; // a pile on ground that was never a field
            var truck9 = w9.Owned(0).First(e => e.IsHarvester);
            w9.SetOrder(truck9, Order.Harvest, tile.Center);
            Run(w9, 150);
            var s9 = w9.Teams[0].Stats;
            var score9 = w9.Scores().First(s => s.Team == 0);
            int picked9 = 300 - w9.Map.SalvageOn(ti);
            Check(s9.SalvageMined >= 100 && s9.SalvageMined == picked9 - truck9.CargoSalvage && score9.OreMined == Math.Max(0, s9.OreMined - s9.SalvageMined) && score9.OreMined < s9.OreMined,
                  $"a truck picked up {picked9} ore of salvage and delivered {s9.SalvageMined} (the rest is still in its load): ore_mined {s9.OreMined} includes it, the economy score counts {score9.OreMined}");
            Check(World.HowScored.Contains("salvage you pick up doesn't count"), "and the points text says so");

            // A truck lost with salvage aboard never delivered it: the score loses nothing for it.
            var w13 = new World(2, 7, 96);
            var spot13 = OpenGround(w13);
            var tile13 = Int2.Of(spot13);
            int ti13 = w13.Map.Idx(tile13.X, tile13.Y);
            w13.Map.Ore[ti13] = 300; w13.Map.OreType[ti13] = Map.Iron; w13.Map.Salvage[ti13] = 300;
            w13.Teams[1].ProtectedUntil = 0; w13.Teams[0].ProtectedUntil = 0;
            var truck13 = w13.Owned(0).First(e => e.IsHarvester);
            w13.SetOrder(truck13, Order.Harvest, tile13.Center);
            for (int i = 0; i < 120 * World.TickRate && truck13.CargoSalvage < 100; i++) w13.Step();
            w13.Teams[0].Stats.OreMined = 1000; // delivered field ore
            int carried13 = truck13.CargoSalvage;
            w13.Hurt(truck13, 1e6f, w13.SpawnUnit(1, "heavy_tank", w13.Owned(1).First(e => e.Def.Key == "command_center")));
            var score13 = w13.Scores().First(s => s.Team == 0);
            Check(carried13 >= 100 && truck13.Dead && w13.Teams[0].Stats.SalvageMined == 0 && score13.OreMined == 1000,
                  $"1000 delivered field ore and a truck lost with {carried13} salvage aboard still score 1000 ({score13.OreMined})");

            // Salvage on a field tile is salvage: taken first, counted when delivered, and the field still grows back around it.
            var w14 = new World(2, 7, 96);
            foreach (var u in w14.Entities.Where(e => e.IsHarvester).ToList()) w14.Remove(u); // no trucks mining the tiles under test
            w14.Map.FieldIndex(out var fieldTiles, out _);
            var centre = new Vec2(w14.Map.W / 2f, w14.Map.H / 2f);
            int ft = fieldTiles.Where(i => w14.Map.OreBase[i] >= 150 && w14.Map.OreBase[i] <= 600).OrderBy(i => Vec2.Dist(new Int2(i % w14.Map.W, i / w14.Map.W).Center, centre)).First();
            int baseOre = w14.Map.OreBase[ft];
            w14.Map.OreType[ft] = w14.Map.OreBaseType[ft];
            w14.Map.Ore[ft] = 100 + baseOre; w14.Map.Salvage[ft] = baseOre; // the field mined down to 100, with a pile of salvage as big as the field on top
            var wasField = w14.Map.FieldOre(ft);
            Run(w14, 900);
            Check(wasField == 100 && w14.Map.FieldOre(ft) > 100 && w14.Map.FieldOre(ft) <= baseOre && w14.Map.SalvageOn(ft) == baseOre,
                  $"salvage on a field tile doesn't stop it growing back, and the field never passes its original {baseOre} because of it (field ore {wasField} -> {w14.Map.FieldOre(ft)}, salvage {w14.Map.SalvageOn(ft)})");
            int ft2 = fieldTiles.First(i => i != ft && w14.Map.OreBase[i] > 0 && w14.Map.OreBase[i] <= 700);
            w14.Map.OreType[ft2] = w14.Map.OreBaseType[ft2]; w14.Map.Ore[ft2] = w14.Map.OreBase[ft2] + 200; w14.Map.Salvage[ft2] = 200; // a full field with salvage on it
            Run(w14, 300);
            Check(w14.Map.FieldOre(ft2) == w14.Map.OreBase[ft2] && w14.Map.Ore[ft2] == w14.Map.OreBase[ft2] + 200, $"a full field with salvage on it isn't topped up past its original ({w14.Map.FieldOre(ft2)} of {w14.Map.OreBase[ft2]}, {w14.Map.Ore[ft2]} in all)");
            var w15 = new World(2, 7, 96);
            var tile15 = Int2.Of(OpenGround(w15));
            int ti15 = w15.Map.Idx(tile15.X, tile15.Y);
            w15.Map.OreBase[ti15] = 400; w15.Map.OreBaseType[ti15] = Map.Iron; w15.Map.OreType[ti15] = Map.Iron; w15.Map.Ore[ti15] = 460; w15.Map.Salvage[ti15] = 60; // a field tile, 400 of its own and 60 salvage
            var truck15 = w15.Owned(0).First(e => e.IsHarvester);
            w15.SetOrder(truck15, Order.Harvest, tile15.Center);
            for (int i = 0; i < 200 * World.TickRate && truck15.Cargo < 150; i++) w15.Step();
            Check(truck15.Cargo == 150 && truck15.CargoSalvage == 60 && w15.Map.Salvage[ti15] == 0 && w15.Map.Ore[ti15] == 310,
                  $"a truck takes the salvage on a field tile first: of its load of {truck15.Cargo}, {truck15.CargoSalvage} is salvage ({w15.Map.Ore[ti15]} ore left on the tile)");
            // Plasma the reactors owe is charged to the gift first, like any spending: stock 30 with 27.5 of it gift and 0.03 owed
            // leaves 2.5 of the team's own, not 2.47.
            var tp = new Team();
            tp.Stock["plasma"] = 30f; tp.Granted["plasma"] = 27.5f; tp.PlasmaOwed = 0.03f;
            Check(Math.Abs(tp.Own("plasma") - 2.5f) < 0.001f, $"owed plasma comes off the gift first: of 30 plasma, 27.5 gift and 0.03 owed, {tp.Own("plasma"):0.000} is the team's own");
            var tq = new Team();
            tq.Stock["plasma"] = 30f; tq.Granted["plasma"] = 5f; tq.PlasmaOwed = 4f; // only 1 of the gift is free of what's owed
            float own0 = tq.Own("plasma");
            float shareQ = tq.Pay(new Dictionary<string, int> { { "plasma", 10 } });
            Check(Math.Abs(shareQ - 0.1f) < 0.001f && tq.Gift("plasma") == 4f, $"a purchase can only spend the gift that isn't already owed: 1 of its 10 plasma ({shareQ:0.00}), leaving the 4 that's owed");
            float ownBefore = tq.Own("plasma");
            tq.SettleOwed(4f);
            Check(Math.Abs(tq.Own("plasma") - ownBefore) < 0.001f && tq.PlasmaOwed == 0f && tq.Gift("plasma") == 0f && Math.Abs(own0 - 25f) < 0.001f && Math.Abs(ownBefore - 16f) < 0.001f,
                  $"and settling what's owed comes off the gift too, so the team's own plasma doesn't move ({ownBefore:0.0} -> {tq.Own("plasma"):0.0})");

            // Structured state (format=json) has salvage_mined too.
            w9.Teams[0].Stats.SalvageMined = 123;
            var stats9 = (Dictionary<string, object>)D(StateData.Team(w9, 0))["stats"];
            Check(stats9.ContainsKey("salvage_mined") && (double)stats9["salvage_mined"] == 123 && stats9.ContainsKey("ore_mined"), "format=json state carries salvage_mined next to ore_mined");
            Check(w6.Errors == 0 && w7.Errors == 0 && w8.Errors == 0 && w9.Errors == 0 && w10.Errors == 0 && w11.Errors == 0 && w12.Errors == 0 && w13.Errors == 0 && w14.Errors == 0 && w15.Errors == 0, "no sim errors");
        }

        static void SalvageFromKills()
        {
            var w = new World(2, 7, 96);
            var spot = OpenGround(w);
            var hq0 = w.Owned(0).First(e => e.Def.Key == "command_center");
            var hq1 = w.Owned(1).First(e => e.Def.Key == "command_center");
            var shooter = At(w.SpawnUnit(0, "heavy_tank", hq0), spot + new Vec2(-4, 0));

            // An enemy kill: about a quarter of the light tank's 200 steel + 40 copper, as iron and copper ore.
            var tank = At(w.SpawnUnit(1, "light_tank", hq1), spot);
            int killsBefore = w.Teams[0].Stats.Kills;
            w.Hurt(tank, 10000, shooter);
            var ore = OreNear(w, spot, 4);
            Check(tank.Dead && ore[Map.Iron] == 50 && ore[Map.Copper] == 10 && w.Teams[0].Stats.Kills == killsBefore + 1,
                  $"a tank killed by an enemy leaves a quarter of its cost as ore around the spot ({ore[0]} iron ore, {ore[1]} copper ore)");
            Check(w.Teams[0].Stats.KillValue == 240 && w.Teams[0].Stats.SalvageLeft == 60, $"the killer's stats count what it destroyed and the salvage left ({w.Teams[0].Stats.KillValue}, {w.Teams[0].Stats.SalvageLeft})");
            var a0 = w.Alerts.Active(w, 0).FirstOrDefault(a => a.Kind == "salvage_dropped");
            var a1 = w.Alerts.Active(w, 1).FirstOrDefault(a => a.Kind == "salvage_dropped");
            Check(a0 != null && a1 != null && a0.Amount == 60 && AlertLog.Describe(w, a0).Contains("60 ore of salvage"), "both sides are told where the salvage lies (SALVAGE ON THE FIELD)");

            // A squad dying in the same place: one alert per side with the running total, and the piles merge.
            long seqBefore = w.Alerts.LastSeq;
            int tilesBefore = Enumerable.Range(0, w.Map.Ore.Length).Count(i => w.Map.Ore[i] > 0);
            for (int i = 0; i < 10; i++) w.Hurt(At(w.SpawnUnit(1, "rifleman", hq1), spot + new Vec2(0.3f * (i % 3), 0.3f * (i / 3))), 1000, shooter);
            int tilesAfter = Enumerable.Range(0, w.Map.Ore.Length).Count(i => w.Map.Ore[i] > 0);
            Check(w.Alerts.LastSeq == seqBefore && a0.Amount == 100 && a0.Count == 11 && tilesAfter == tilesBefore,
                  $"ten riflemen killed together merge into the same alert ({a0.Count} wrecks, {a0.Amount} ore) and the same pile (no new ore tiles: {tilesAfter - tilesBefore})");
            Check(OreNear(w, spot, 4)[Map.Iron] == 90, $"infantry leave very little: 4 iron ore a rifleman ({OreNear(w, spot, 4)[Map.Iron] - 50} for ten)");

            // Nothing for deaths with no enemy cause, a team's own fire, selling, or running out of fuel.
            var w2 = new World(2, 7, 96);
            var s2 = OpenGround(w2);
            var h2 = w2.Owned(1).First(e => e.Def.Key == "command_center");
            int before = OreNear(w2, s2, 6).Sum();
            w2.Hurt(At(w2.SpawnUnit(1, "heavy_tank", h2), s2), 10000, null);
            var own = At(w2.SpawnUnit(1, "artillery", h2), s2 + new Vec2(2, 0));
            w2.Hurt(At(w2.SpawnUnit(1, "heavy_tank", h2), s2), 10000, own);
            var gunship = At(w2.SpawnUnit(1, "gunship", h2), s2 + new Vec2(0, 3));
            gunship.Fuel = 0.01f; gunship.Order = Order.Move; gunship.OrderPos = s2 + new Vec2(20, 3);
            w2.Step(); w2.Step();
            var spot2 = w2.FindPlacement(1, "barracks").Value;
            var barracks = w2.SpawnStructure(1, "barracks", spot2, 1f);
            Commands.Execute(w2, 1, Cmd("type", "sell", "structure_id", barracks.Id, "confirm", true));
            int barracksOre = 0; for (int y = -1; y <= 2; y++) for (int x = -1; x <= 2; x++) barracksOre += w2.Map.OreAt(spot2.X + x, spot2.Y + y);
            Check(OreNear(w2, s2, 6).Sum() == before && barracksOre == 0 && gunship.Dead, $"no salvage for a death with no enemy cause, your own side's fire, a crash or selling ({OreNear(w2, s2, 6).Sum() - before} ore appeared)");

            // A fire an enemy set: its kill and salvage even after the arsonist is dead.
            var w3 = new World(2, 7, 96);
            var h3 = w3.Owned(1).First(e => e.Def.Key == "command_center");
            var raider = At(w3.SpawnUnit(0, "light_tank", w3.Owned(0).First(e => e.IsStructure)), new Vec2(48, 48));
            var depot = w3.SpawnStructure(1, "power_plant", w3.FindPlacement(1, "power_plant").Value, 1f);
            w3.Hurt(depot, depot.Def.MaxHp * 0.8f, raider);
            w3.Remove(raider); // the raider is gone; the fire burns on
            int k0 = w3.Teams[0].Stats.Kills, salv0 = w3.Teams[0].Stats.SalvageLeft;
            Run(w3, 90);
            Check(depot.Dead && w3.Teams[0].Stats.Kills == k0 + 1 && w3.Teams[0].Stats.SalvageLeft - salv0 == 62,
                  $"a building burned down by a fire an enemy set is that enemy's kill, and leaves salvage, even after the attacker died ({w3.Teams[0].Stats.SalvageLeft - salv0} iron ore)");

            // A burning building captured: the engineer puts the fire out and patches it up to 50%, and whoever hit it before
            // (here a third team's tank, still alive) has no claim on it: no fire burns on to hand them the kill.
            var w4 = new World(3, 7, 96);
            foreach (var u in w4.Owned(1).Where(e => !e.IsStructure).ToList()) w4.Remove(u); // nobody of the old owner's shoots at it
            var b4 = w4.SpawnStructure(1, "power_plant", w4.FindPlacement(1, "power_plant").Value, 1f);
            var t4 = At(w4.SpawnUnit(2, "light_tank", w4.Owned(2).First(e => e.IsStructure)), b4.Center + new Vec2(5, 0));
            w4.Hurt(b4, b4.Def.MaxHp * 0.8f, t4);
            At(t4, w4.Teams[2].StartPos); // gone home, alive
            Run(w4, 1);
            bool burning4 = b4.Burning;
            var eng = At(w4.SpawnUnit(0, "engineer", w4.Owned(0).First(e => e.IsStructure)), b4.Center + new Vec2(1.6f, 0));
            w4.SetOrder(eng, Order.Capture, b4.Center, b4.Id);
            Run(w4, 3);
            Check(burning4 && b4.Team == 0 && !b4.Burning && b4.Hp == b4.Def.MaxHp * World.CaptureThreshold && b4.LastAttackerId == 0 && b4.LastAttackerTeam == -1,
                  $"capturing a burning building puts the fire out, lifts it to 50% and forgets who hit it (team {b4.Team}, burning {b4.Burning}, hp {b4.Hp}/{b4.Def.MaxHp}, last attacker #{b4.LastAttackerId})");
            int kills4 = w4.Teams[2].Stats.Kills;
            Run(w4, 90);
            Check(!b4.Dead && b4.Team == 0 && w4.Teams[2].Stats.Kills == kills4 && !t4.Dead, "and it stands: no fire burns on to give a third team the kill");

            // A mining truck spills its load; salvage never lands on a tile of another ore, and never past the cap.
            var w5 = new World(2, 7, 96);
            var s5 = OpenGround(w5);
            var h5 = w5.Owned(1).First(e => e.Def.Key == "command_center");
            int ci = w5.Map.Idx((int)s5.X, (int)s5.Y), ii = w5.Map.Idx((int)s5.X + 1, (int)s5.Y);
            w5.Map.Ore[ci] = 500; w5.Map.OreType[ci] = Map.Copper;
            w5.Map.Ore[ii] = 990; w5.Map.OreType[ii] = Map.Iron;
            var truck = At(w5.SpawnUnit(1, "mining_truck", h5), s5);
            w5.SetOrder(truck, Order.Idle, truck.Pos);
            truck.Cargo = 120; truck.CargoType = Map.Iron;
            var hunter = At(w5.SpawnUnit(0, "light_tank", w5.Owned(0).First(e => e.IsStructure)), s5 + new Vec2(-4, 0));
            int ironBefore = OreNear(w5, s5, 4)[Map.Iron];
            w5.Hurt(truck, 10000, hunter);
            int ironAfter = OreNear(w5, s5, 4)[Map.Iron];
            Check(ironAfter - ironBefore == 170 && w5.Map.Ore[ci] == 500 && w5.Map.OreType[ci] == Map.Copper && w5.Map.Ore[ii] == 1000 && w5.Map.Ore.Max() <= Map.MaxOrePerTile,
                  $"a mining truck spills its load plus a quarter of its cost ({ironAfter - ironBefore} iron ore), leaving the copper tile alone and the nearly full tile at the cap ({w5.Map.Ore[ii]})");

            // Farming: what a kill leaves is always far less than the victim cost, so feeding an opponent kills never pays.
            var w6 = new World(2, 7, 96);
            var s6 = OpenGround(w6);
            var h6 = w6.Owned(1).First(e => e.Def.Key == "command_center");
            var k6 = At(w6.SpawnUnit(0, "light_tank", w6.Owned(0).First(e => e.IsStructure)), s6 + new Vec2(-4, 0));
            bool cheap = true; string worst = "";
            foreach (var key in new[] { "rifleman", "light_tank", "heavy_tank", "mammoth_tank", "gunship", "outpost_truck", "engineer" })
            {
                int o0 = w6.Map.Ore.Sum();
                var v = At(w6.SpawnUnit(1, key, h6), s6);
                w6.Hurt(v, 100000, k6);
                float paid = Defs.Get(key).Cost.Sum(kv => kv.Value * World.OreMix[kv.Key].Sum()); // in raw ore, as salvage counts it
                float got = w6.Map.Ore.Sum() - o0;
                if (got > paid * 0.26f) { cheap = false; worst = $"{key}: {got}/{paid}"; }
            }
            Check(cheap, $"no kill leaves more than about a quarter of what the victim cost {worst}");
        }
    }
}
