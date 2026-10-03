using System;
using System.Collections.Generic;
using System.Linq;
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
            SalvageFromKills();
            OreRegrowth();
            ConstructionTruck();
            MatchClock();
        }

        static Dictionary<string, object> D(JObj o) => (Dictionary<string, object>)Json.Parse(Json.Write(o));

        static void MatchClock()
        {
            // A short match: sudden death at 15 minutes, decay at 45, the end at 75.
            var g = new Game(new GameConfig { Seed = 3, MapSize = 80, Open = true, MatchHours = 0.25f, Controllers = new[] { "llm", "llm" } });
            var w = g.World;
            Check(w.SuddenDeathAt == 900 && w.Phase == MatchPhase.Normal && D(StateView.MatchJson(w)).Str("phase") == "normal" && (int)D(StateView.MatchJson(w)).Num("ends_in_s") == 4500,
                  $"a new game has a match clock: sudden death at hour N (here 0.25 h), the end an hour later ({StateView.MatchText(w)})");
            var t0 = w.Teams[0];
            t0.Stats.KillValue = 500; t0.Stats.OreMined = 900; w.Teams[1].Stats.OreMined = 100;
            Step(g, 301);
            int warned = w.Events.Count(e => e.Type == "chat" && e.Text.Contains("Sudden death in 10 minutes"));
            bool alerted = w.Alerts.Active(w, 1).Any(a => a.Kind == "match_clock" && a.Priority == Priority.High);
            Step(g, 540);
            warned += w.Events.Count(e => e.Type == "chat" && e.Text.Contains("Sudden death in 1 minute"));
            Check(warned == 2 && alerted, $"sudden death is announced 10 minutes and 1 minute ahead, in the chat and as a MATCH CLOCK alert ({warned} warnings)");
            Step(g, 60);
            int regrownBefore = w.Regrown;
            for (int i = 0; i < w.Map.Ore.Length; i++) if (w.Map.OreBase[i] > 0) w.Map.Ore[i] = 0;
            Step(g, 60);
            Check(w.Phase == MatchPhase.SuddenDeath && w.Regrown == regrownBefore && w.Events.Any(e => e.Type == "chat" && e.Text.Contains("SUDDEN DEATH")),
                  $"at sudden death ore stops regrowing ({w.Regrown - regrownBefore} regrown after it)");
            var hq = w.Owned(1).First(e => e.Def.Key == "command_center");
            Step(g, 1800);
            float hp = hq.Hp;
            Step(g, 600);
            Check(w.Phase == MatchPhase.Decay && hq.Hp < hp - hq.Def.MaxHp * 0.17f && hq.Hp > hp - hq.Def.MaxHp * 0.19f && w.Events.Any(e => e.Text != null && e.Text.Contains("DECAY")),
                  $"30 minutes on, structures decay: 0.03% of max health a second ({hp:0} -> {hq.Hp:0} in 10 minutes)");
            var state = D(StateData.Team(w, 0));
            Check(state.Obj("match")?.Str("phase") == "decay" && state.Obj("match").Num("next_phase_in_s") > 0, "state shows the phase and how long until the next one");
            Step(g, 1200);
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

        /// <summary>Mine a fresh map bare, let it regrow: how much comes back in 10 and 20 minutes, and where.</summary>
        static (int total, int middle, int edges, int fieldOre) RegrowBare(int size, float oreScale, float minutes, int seed = 7)
        {
            var w = new World(2, seed, size, oreScale);
            foreach (var e in w.Entities.Where(e => e.IsHarvester).ToList()) w.Remove(e);
            int field = w.Map.OreBase.Sum();
            Array.Clear(w.Map.Ore, 0, w.Map.Ore.Length);
            Run(w, minutes * 60);
            int mid = 0, edge = 0;
            float half = 0.5f * MathF.Sqrt(2) * size;
            for (int i = 0; i < w.Map.Ore.Length; i++)
            {
                float dx = i % size + 0.5f - size / 2f, dy = i / size + 0.5f - size / 2f;
                if (MathF.Sqrt(dx * dx + dy * dy) < half * 0.4f) mid += w.Map.Ore[i]; else edge += w.Map.Ore[i];
            }
            return (w.Map.Ore.Sum(), mid, edge, field);
        }

        static void OreRegrowth()
        {
            foreach (var (size, scale) in new[] { (96, 1f), (96, 0.3f), (208, 0.3f) })
            {
                var r = RegrowBare(size, scale, 20);
                Console.WriteLine($"      regrowth {size}x{size} ore x{scale}, 20 min after mined bare: {r.total} of {r.fieldOre} ({r.total / 1200f:0.0} ore/s), middle {r.middle}, edges {r.edges}");
                Check(r.total / 1200f > 1.5f && r.total / 1200f < 10f && r.middle > r.edges * 1.4f,
                      $"a mined-bare {size}x{size} map (ore x{scale}) earns back meaningful income in 20 minutes ({r.total / 1200f:0.0} ore/s, a deep mine pumps 4), mostly in the middle ({r.middle} vs {r.edges} at the edges)");
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
            for (int k = 0; k < 120 && m.Ore[root] == 0; k++) w.Step();
            Check(rootsOnly && m.Ore[root] > 0 && m.Ore[far] == 0,
                  $"a field mined to nothing starts growing back from its root (the tile that held the most), not everywhere at once (first pass: only roots {rootsOnly}; root {m.Ore[root]}, far tile {m.Ore[far]})");
            Run(w, 60);
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
            Commands.Execute(w2, 1, Cmd("type", "sell", "structure_id", barracks.Id));
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

            // A building the attacker then captured and let burn: its own now, so no kill and no salvage.
            var w4 = new World(2, 7, 96);
            foreach (var u in w4.Owned(1).Where(e => !e.IsStructure).ToList()) w4.Remove(u); // nobody of the old owner's shoots at it
            var b4 = w4.SpawnStructure(1, "power_plant", w4.FindPlacement(1, "power_plant").Value, 1f);
            var t4 = At(w4.SpawnUnit(0, "light_tank", w4.Owned(0).First(e => e.IsStructure)), b4.Center + new Vec2(5, 0));
            w4.Hurt(b4, b4.Def.MaxHp * 0.8f, t4);
            var eng = At(w4.SpawnUnit(0, "engineer", w4.Owned(0).First(e => e.IsStructure)), b4.Center + new Vec2(1.6f, 0));
            w4.SetOrder(eng, Order.Capture, b4.Center, b4.Id);
            Run(w4, 3);
            int ore4 = OreNear(w4, b4.Center, 3).Sum(), kills4 = w4.Teams[0].Stats.Kills;
            Run(w4, 90);
            Check(b4.Team == 0 && b4.Dead && OreNear(w4, b4.Center, 3).Sum() == ore4 && w4.Teams[0].Stats.Kills == kills4,
                  $"capturing a building and letting it burn down is no kill and leaves no salvage (team {b4.Team}, dead {b4.Dead}, {OreNear(w4, b4.Center, 3).Sum() - ore4} ore)");

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
                float paid = Defs.Get(key).Cost.Sum(kv => kv.Value * (kv.Key is "circuits" or "lenses" or "plasma" or "composite" ? 2 : 1));
                float got = w6.Map.Ore.Sum() - o0;
                if (got > paid * 0.26f) { cheap = false; worst = $"{key}: {got}/{paid}"; }
            }
            Check(cheap, $"no kill leaves more than about a quarter of what the victim cost {worst}");
        }
    }
}
