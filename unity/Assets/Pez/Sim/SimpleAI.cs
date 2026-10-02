using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>
    /// A scripted opponent that climbs the tech tree, balances ore types, expands with outposts and
    /// attacks in waves. Uses only Commands.Execute (plus reading its own fogged view), like everyone else.
    /// </summary>
    public class SimpleAI
    {
        readonly int team;
        float nextThink;
        int waveSize = 6;
        readonly Dictionary<int, Vec2> outpostTargets = new Dictionary<int, Vec2>();

        static readonly string[] BuildOrder =
        {
            "power_plant", "mining_refinery", "barracks", "factory", "power_plant", "electronics_plant", "gun_turret",
            "mining_refinery", "power_plant", "optics_lab", "enrichment_plant", "power_plant", "radar_dome", "sam_site",
            "laser_tower", "power_plant", "composite_foundry", "airfield", "fusion_reactor", "gun_turret", "laser_tower", "sam_site",
        };

        public SimpleAI(int team) { this.team = team; }

        static Dictionary<string, object> Cmd(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2)
            {
                var v = kv[i + 1];
                d[(string)kv[i]] = v is int n ? (double)n : v is float f ? (double)f : v is IEnumerable<int> ids ? ids.Select(x => (object)(double)x).ToList() : v;
            }
            return d;
        }

        JObj Do(World w, params object[] kv) => Commands.Execute(w, team, Cmd(kv));

        public void Update(World w)
        {
            if (w.Time < nextThink || w.GameOver) return;
            nextThink = w.Time + 1f;
            var t = w.Teams[team];
            if (t.Defeated) return;
            var mine = w.Owned(team).ToList();
            var structures = mine.Where(e => e.IsStructure).ToList();
            var have = structures.GroupBy(s => s.Def.Key).ToDictionary(g => g.Key, g => g.Count());
            int Count(string k) => have.TryGetValue(k, out var c) ? c : 0;

            // ---- Base building
            string next = null;
            if (t.StructureQueue.Count == 0)
            {
                if (t.PowerProduced - t.PowerUsed < 25 && structures.Count > 1)
                    next = w.MissingPrereq(team, Defs.Get("fusion_reactor")) == null && t.Amount("plasma") > 80 && Count("fusion_reactor") == 0 ? "fusion_reactor" : "power_plant";
                else
                {
                    var need = new Dictionary<string, int>();
                    foreach (var k in BuildOrder)
                    {
                        need[k] = need.TryGetValue(k, out var c) ? c + 1 : 1;
                        if (Count(k) < need[k]) { next = k; break; }
                    }
                }
                if (next != null && t.Missing(Defs.Get(next).Cost) == null && w.MissingPrereq(team, Defs.Get(next)) == null)
                    Do(w, "type", "build", "structure", next);
            }
            var reserve = next != null ? Defs.Get(next).Cost : new Dictionary<string, int>();
            bool Affordable(string unit)
            {
                var cost = Defs.Get(unit).Cost;
                return cost.All(kv => t.Amount(kv.Key) - (reserve.TryGetValue(kv.Key, out var r) ? r : 0) >= kv.Value);
            }

            // ---- Mining trucks: enough of them, spread across the ore types the tech needs
            var trucks = mine.Where(e => e.IsHarvester).ToList();
            int wantTrucks = System.Math.Min(10, 3 + Count("mining_refinery") * 2 + Count("outpost") * 2);
            if (trucks.Count < wantTrucks && t.UnitQueues[Producer.CommandCenter].Count == 0 && t.Amount("iron_ore") >= 200 + (reserve.TryGetValue("iron_ore", out var ri) ? ri : 0))
                Do(w, "type", "train", "unit", "mining_truck");
            // Iron is the bulk resource: roughly 3 of every 5 trucks; one each on copper, then crystal/uranium as tech needs them.
            var plan = new List<int> { Map.Iron, Map.Iron, Map.Copper, Map.Iron };
            if (Count("optics_lab") > 0 || Count("composite_foundry") > 0) plan.Add(Map.Crystal);
            if (Count("enrichment_plant") > 0) plan.Add(Map.Uranium);
            plan.Add(Map.Iron);
            plan.Add(Map.Copper);
            for (int i = 0; i < trucks.Count; i++)
            {
                int type = i < plan.Count ? plan[i] : Map.Iron;
                var tr = trucks[i];
                if (tr.HarvestType != type && tr.Cargo == 0 && tr.Order != Order.ReturnOre)
                    Do(w, "type", "harvest", "units", new[] { tr.Id }, "ore", Defs.Ores[type]);
            }

            // ---- Expansion: claim remote ore fields with outposts
            if (w.HasComplete(team, "electronics_plant") && Count("outpost") + mine.Count(e => e.Def.Key == "outpost_truck") < 2 &&
                !t.UnitQueues[Producer.Factory].Any(p => p.Key == "outpost_truck") && Affordable("outpost_truck"))
                Do(w, "type", "train", "unit", "outpost_truck");
            foreach (var ot in mine.Where(e => e.Def.Key == "outpost_truck"))
            {
                if (!outpostTargets.TryGetValue(ot.Id, out var goal))
                {
                    var field = StateView.OreFields(w, t.Explored)
                        .Where(f => !structures.Any(s => Vec2.Dist(s.Center, new Vec2(f.cx, f.cy)) < 12))
                        .OrderBy(f => Vec2.Dist(new Vec2(f.cx, f.cy), t.StartPos)).FirstOrDefault();
                    if (field.tiles == 0) continue;
                    // Park beside the field, not on the ore.
                    var spot = w.FindPlacement(team, "outpost", new Vec2(field.cx, field.cy)) ?? new Int2(field.cx + 3, field.cy);
                    goal = new Vec2(spot.X + 1, spot.Y + 1);
                    outpostTargets[ot.Id] = goal;
                    Do(w, "type", "move", "units", new[] { ot.Id }, "x", goal.X, "y", goal.Y);
                }
                else if (ot.Order == Order.Idle)
                {
                    var r = Do(w, "type", "deploy", "units", new[] { ot.Id });
                    if (!(r["ok"] is bool ok && ok))
                    {
                        // Shuffle and retry next think.
                        var jitter = new Vec2(goal.X + (w.Tick % 5) - 2, goal.Y + (w.Tick % 3) - 1);
                        outpostTargets[ot.Id] = jitter;
                        Do(w, "type", "move", "units", new[] { ot.Id }, "x", jitter.X, "y", jitter.Y);
                    }
                }
            }

            // ---- Keep a couple of repair trucks (they auto-repair whatever is damaged near them)
            if (w.HasComplete(team, "factory") && mine.Count(e => e.Def.Key == "repair_truck") < 2 &&
                !t.UnitQueues[Producer.Factory].Any(p => p.Key == "repair_truck") && t.Amount("steel") > 300 && Affordable("repair_truck"))
                Do(w, "type", "train", "unit", "repair_truck");
            if (w.HasComplete(team, "barracks") && mine.Count(e => e.Def.Key == "medic") < 2 &&
                !t.UnitQueues[Producer.Barracks].Any(p => p.Key == "medic") && t.Amount("steel") > 200 && Affordable("medic"))
                Do(w, "type", "train", "unit", "medic");

            // ---- Army
            void Train(Producer p, params string[] options)
            {
                if (!w.HasComplete(team, Defs.ProducerKey(p)) || t.UnitQueues[p].Count >= 2) return;
                foreach (var u in options)
                    if (w.MissingPrereq(team, Defs.Get(u)) == null && Affordable(u)) { Do(w, "type", "train", "unit", u); return; }
            }
            // Expensive tiers first so cheap infantry doesn't eat all the steel; infantry is capped.
            int roll = w.Tick / 20 % 3;
            Train(Producer.Airfield, roll == 0 ? new[] { "stealth_bomber", "gunship" } : new[] { "gunship" });
            // Answer enemy aircraft with flak; otherwise climb toward mammoths.
            bool enemyAir = w.Entities.Any(e => !e.Dead && e.Team != team && e.IsAir && w.IsVisibleTo(team, e));
            if (enemyAir && mine.Count(e => e.Def.Key == "flak_track") < 3) Train(Producer.Factory, "flak_track");
            if (w.HasComplete(team, "electronics_plant") && mine.Count(e => e.Def.Key == "recon_drone") == 0) Train(Producer.Factory, "recon_drone");
            Train(Producer.Factory, roll == 0 ? new[] { "mammoth_tank", "laser_tank", "heavy_tank", "light_tank" } : roll == 1 ? new[] { "artillery", "heavy_tank", "light_tank" } : new[] { "light_tank", "apc", "scout_buggy" });
            int infantry = mine.Count(e => e.Def.Armor == Armor.Infantry);
            int vehicles = mine.Count(e => !e.IsStructure && e.Def.Armor != Armor.Infantry && !e.IsHarvester);
            if (infantry < 6 + vehicles && t.Amount("steel") > 150)
                Train(Producer.Barracks, roll == 0 ? new[] { "laser_trooper", "sniper", "rocket_soldier", "rifleman" } : roll == 1 ? new[] { "rocket_soldier", "rifleman" } : new[] { "rifleman" });

            // ---- Defense: anything hostile near a base gets everyone nearby.
            var army = mine.Where(e => !e.IsStructure && e.IsArmed).ToList();
            var threat = w.Entities.FirstOrDefault(e => !e.Dead && e.Team != team && !e.IsStructure && w.IsVisibleTo(team, e) &&
                                                        structures.Any(s => Vec2.Dist(e.Pos, s.Center) < 10));
            if (threat != null)
            {
                foreach (var u in army.Where(u => (u.Order == Order.Idle || u.Order == Order.Move) && u.Def.Weapon.CanHit(threat.Def)))
                    w.SetOrder(u, Order.AttackMove, threat.Pos);
                return;
            }

            // ---- Offense: waves at known structures, else toward the nearest unexplored corner.
            var idle = army.Where(u => u.Order == Order.Idle).ToList();
            if (idle.Count >= waveSize)
            {
                Vec2 goal;
                var known = t.KnownEnemyStructures.Values.ToList();
                if (known.Count > 0) { var k = known.OrderBy(x => Vec2.Dist(new Vec2(x.origin.X, x.origin.Y), t.StartPos)).First(); goal = new Vec2(k.origin.X + 1, k.origin.Y + 1); }
                else
                {
                    var corners = w.Map.Spawns.Where(s => !t.Explored[w.Map.Idx((int)s.X, (int)s.Y)]).ToList();
                    if (corners.Count == 0) corners = w.Map.Spawns.Where(s => Vec2.Dist(s, t.StartPos) > 5).ToList();
                    goal = corners.OrderBy(s => Vec2.Dist(s, t.StartPos)).First();
                }
                foreach (var u in idle) w.SetOrder(u, Order.AttackMove, goal);
                waveSize = System.Math.Min(10, waveSize + 1);
                Do(w, "type", "say", "text", $"Wave of {idle.Count} inbound.");
            }
        }
    }
}
