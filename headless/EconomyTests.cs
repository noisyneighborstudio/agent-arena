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
