using System;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>Fixes for what a full playtest (docs/playtests/2026-10-03-plum.md) found missing or wrong.</summary>
    public static partial class Tests
    {
        static string Said(JObj r) => (r["result"] ?? r["error"])?.ToString() ?? "";

        static void PlaytestFixes()
        {
            // Sight is mutual: an artillery piece 6.8 tiles from a turret's edge sees it, as the turret sees it.
            {
                var w = new World(2, 7, 80);
                var turret = w.SpawnStructure(1, "gun_turret", new Int2(40, 40), 1f);
                var art = At(w.SpawnUnit(0, "artillery", w.Owned(0).First(e => e.Def.Key == "command_center")), new Vec2(40.3f, 33.2f));
                w.UpdateVisibility();
                Check(w.IsVisibleTo(1, art) && w.IsVisibleTo(0, turret), $"equal sight is mutual: turret sees the artillery ({w.IsVisibleTo(1, art)}) and the artillery sees the turret ({w.IsVisibleTo(0, turret)})");
            }

            // A big stockpile spills in full around the fallen command center, and the alert reports what landed.
            {
                var w = new World(2, 7, 80);
                var t1 = w.Teams[1];
                t1.Stock.Clear(); t1.Add("steel", 31000);
                var hq = w.Owned(1).First(e => e.Def.Key == "command_center");
                long before = w.Map.Ore.Sum(x => (long)x);
                var shooter = w.SpawnUnit(0, "heavy_tank", w.Owned(0).First(e => e.IsStructure));
                hq.Hp = 1;
                typeof(World).GetMethod("Damage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, new object[] { hq, 50f, shooter, 0, false });
                long spilled = w.Map.Ore.Sum(x => (long)x) - before;
                var alert = w.Alerts.Active(w, 0).FirstOrDefault(a => a.Kind == "salvage_available");
                Check(spilled >= 30900 && alert != null && alert.Lost.Any(l => l.StartsWith(spilled.ToString())),
                      $"a 31000 stockpile spills in full ({spilled} on the ground; alert: {alert?.Lost.FirstOrDefault()})");
            }

            // Selling something that cost nothing says so instead of "sold ... for ".
            {
                var w = new World(2, 7, 80);
                var outpost = w.SpawnStructure(0, "outpost", w.FindPlacement(0, "power_plant").Value, 1f);
                var r = Commands.Execute(w, 0, Cmd("type", "sell", "structure_id", outpost.Id));
                Check(Ok(r) && Said(r).Contains("no refund"), $"selling a free structure explains there's no refund: {Said(r)}");
            }

            // Alerts that have been quiet for minutes aren't delivered as news.
            {
                var w = new World(2, 7, 80);
                var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
                w.Alerts.Raise(w, 0, "base_under_attack", Priority.Critical, hq.Center);
                bool fresh = w.Alerts.Since(w, 0, 0, Priority.High).Any();
                Run(w, AlertLog.StaleAfter + 5);
                Check(fresh && !w.Alerts.Since(w, 0, 0, Priority.High).Any(), "an alert is news when raised but not once it's been quiet for 2 minutes");
            }

            // Deploy says where the structure went up.
            {
                var w = new World(2, 7, 80);
                var truck = At(w.SpawnUnit(0, "outpost_truck", w.Owned(0).First(e => e.Def.Key == "command_center")), new Vec2(40.5f, 40.5f));
                var r = Commands.Execute(w, 0, Cmd("type", "deploy", "units", new[] { truck.Id }));
                Check(Ok(r) && Said(r).Contains("at ("), $"deploy names the outpost and where it stands: {Said(r)}");
            }

            // Reserve: converters leave the reserved amount alone; a raw-ore cost error suggests it.
            {
                var w = new World(2, 7, 80);
                var t = w.Teams[0];
                w.SpawnStructure(0, "mining_refinery", w.FindPlacement(0, "mining_refinery").Value, 1f);
                foreach (var h in w.Owned(0).Where(e => e.IsHarvester).ToList()) w.Remove(h); // only the converters move iron_ore
                t.Stock["iron_ore"] = 0;
                Run(w, 1);
                var hint = Commands.Execute(w, 0, Cmd("type", "build", "structure", "power_plant"));
                Check(!Ok(hint) && Said(hint).Contains("\"reserve\""), $"a raw-ore cost the refineries eat suggests a reserve: {Said(hint)}");
                var r = Commands.Execute(w, 0, Cmd("type", "reserve", "item", "iron_ore", "amount", 250));
                t.Add("iron_ore", 260);
                Run(w, 10); // the refinery takes 5/s: without the reserve this would be ~210
                int held = t.Amount("iron_ore");
                Check(Ok(r) && held >= 250, $"with a 250 iron_ore reserve the refinery stops at 250 ({held} left): {Said(r)}");
                Commands.Execute(w, 0, Cmd("type", "reserve", "item", "iron_ore", "amount", 0));
                Run(w, 5);
                Check(t.Amount("iron_ore") <= held - 5 && t.Reserve.Count == 0, $"amount 0 clears it and the refinery carries on ({held} -> {t.Amount("iron_ore")})");
            }

            // A deep mine warns two minutes before it runs dry and shows how long it has.
            {
                var w = new World(2, 7, 112);
                var t = w.Teams[0];
                var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
                Entity mine = null;
                foreach (var d in w.Map.Deep)
                {
                    t.Surveyed.Add(d.Id);
                    var rig = At(w.SpawnUnit(0, "drill_rig", hq), d.Pos);
                    if (w.Deploy(rig, d) == null) { mine = w.Get(d.MineId); d.Amount = World.DeepMineWarnAmount + 4; break; }
                    w.Remove(rig);
                }
                t.Add("iron_ore", 2000); // keep power out of it: plenty of plants
                for (int i = 0; i < 3; i++) w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
                Run(w, 3);
                var js = StateData.Team(w, 0);
                bool eta = Json.Write(js).Contains("runs_dry_in_s");
                Check(mine != null && eta && w.Alerts.Active(w, 0).Any(a => a.Kind == "deep_mine_running_low"),
                      $"a deep mine shows runs_dry_in_s ({eta}) and warns DEEP DEPOSIT RUNNING LOW at 2 minutes left");
            }

            // Harvest with no surface ore left says which trucks found none and why.
            {
                var w = new World(2, 7, 80);
                Array.Clear(w.Map.Ore, 0, w.Map.Ore.Length);
                var truck = w.Owned(0).First(e => e.IsHarvester);
                var r = Commands.Execute(w, 0, Cmd("type", "harvest", "units", new[] { truck.Id }, "ore", "any"));
                Check(Said(r).Contains("found none") && Said(r).Contains("survey"), $"harvest explains when there's no ore to find: {Said(r)}");
            }

            // move warns when units can't make the trip on their fuel; a repair truck along covers them.
            {
                var w = new World(2, 7, 80);
                var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
                var tank = At(w.SpawnUnit(0, "light_tank", hq), new Vec2(12, 12));
                tank.Fuel = 30;
                var r = Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { tank.Id }, "x", 72, "y", 72));
                Check(Said(r).Contains("NOT ENOUGH FUEL") && Said(r).Contains("turns back"), $"move warns a tank can't make it: {Said(r)}");
                var tanker = At(w.SpawnUnit(0, "repair_truck", hq), new Vec2(13, 12));
                var r2 = Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { tank.Id, tanker.Id }, "x", 72, "y", 72));
                Check(!Said(r2).Contains("NOT ENOUGH FUEL"), $"no warning with a repair truck in the group: {Said(r2)}");
                var full = At(w.SpawnUnit(0, "light_tank", hq), new Vec2(12, 14));
                var r3 = Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { full.Id }, "x", 40, "y", 40));
                Check(!Said(r3).Contains("NOT ENOUGH FUEL"), $"no warning for a trip it can make: {Said(r3)}");
            }

            // An idle drone hovering near its factory lands instead of burning its tank.
            {
                var w = new World(2, 7, 80);
                var factory = w.SpawnStructure(0, "factory", w.FindPlacement(0, "factory").Value, 1f);
                var drone = At(w.SpawnUnit(0, "recon_drone", factory), factory.Center + new Vec2(6, 0));
                drone.Fuel = drone.Def.Fuel * 0.8f;
                Run(w, 10);
                Check(drone.Landed && drone.Order == Order.Idle, $"an idle drone near a pad lands (landed {drone.Landed}, order {drone.OrderName}, fuel {drone.FuelFraction:P0})");
            }

            // train at a chosen barracks.
            {
                var w = new World(2, 7, 80);
                var t = w.Teams[0];
                t.Add("steel", 500);
                var b1 = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
                var b2 = w.SpawnStructure(0, "barracks", new Int2(40, 40), 1f);
                int before = w.Owned(0).Count(e => e.Def.Key == "rifleman");
                var r = Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "structure_id", b2.Id));
                Run(w, 12); // low power can halve the 4s build
                var rifle = w.Owned(0).Where(e => e.Def.Key == "rifleman").OrderByDescending(e => e.Id).First();
                Check(Ok(r) && w.Owned(0).Count(e => e.Def.Key == "rifleman") == before + 1 && rifle.DistFrom(b2.Center) < rifle.DistFrom(b1.Center),
                      $"train with structure_id brings the unit out of that barracks ({rifle.Pos.X:0},{rifle.Pos.Y:0}; b1 at {b1.Origin.X},{b1.Origin.Y}, b2 at {b2.Origin.X},{b2.Origin.Y}): {Said(r)}");
                var bad = Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "structure_id", w.Owned(0).First(e => e.Def.Key == "command_center").Id));
                Check(!Ok(bad) && Said(bad).Contains($"#{b1.Id}"), $"a building that can't train it is refused, listing those that can: {Said(bad)}");
            }

            // spread opens a formation up.
            {
                var w = new World(2, 7, 80);
                var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
                var ids = Enumerable.Range(0, 4).Select(i => At(w.SpawnUnit(0, "rifleman", hq), new Vec2(20 + i, 20)).Id).ToArray();
                var r = Commands.Execute(w, 0, Cmd("type", "move", "units", ids, "x", 40, "y", 40, "spread", 4));
                var a = w.Get(ids[0]).OrderPos; var b = w.Get(ids[1]).OrderPos;
                Check(Ok(r) && Vec2.Dist(a, b) >= 3.9f, $"spread 4 puts units 4 tiles apart ({Vec2.Dist(a, b):0.0})");
            }

            // Rules list sight, and flag weapons that outrange it; the map labels its columns.
            {
                var rules = Json.Write(StateView.Rules());
                Check(rules.Contains("\"sight\"") && rules.Contains("needs a spotter"), "get_rules lists sight and says artillery needs a spotter");
                var w = new World(2, 7, 80);
                var map = StateView.AsciiMap(w, 0);
                Check(map.Contains("    0         10        20        30"), "the ASCII map labels columns with full x numbers");
            }
        }
    }
}
