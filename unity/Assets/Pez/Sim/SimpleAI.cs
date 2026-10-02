using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>A scripted opponent. Uses only Commands.Execute, same as humans and LLMs.</summary>
    public class SimpleAI
    {
        readonly int team;
        float nextThink;
        int waveSize = 6;

        static readonly string[] BuildOrder =
        {
            "power_plant", "refinery", "barracks", "power_plant", "war_factory", "gun_turret", "refinery", "power_plant", "gun_turret", "power_plant", "barracks", "war_factory",
        };

        public SimpleAI(int team) { this.team = team; }

        static Dictionary<string, object> Cmd(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1] is int n ? (double)n : kv[i + 1] is float f ? (double)f : kv[i + 1];
            return d;
        }

        public void Update(World w)
        {
            if (w.Time < nextThink || w.GameOver) return;
            nextThink = w.Time + 1f;
            var t = w.Teams[team];
            if (t.Defeated) return;
            var mine = w.Owned(team).ToList();
            var structures = mine.Where(e => e.IsStructure).ToList();

            // Economy / base building.
            if (t.StructureQueue.Count == 0)
            {
                string next = null;
                if (t.PowerProduced - t.PowerUsed < 20 && structures.Count > 1) next = "power_plant";
                else
                {
                    var have = structures.GroupBy(s => s.Def.Key).ToDictionary(g => g.Key, g => g.Count());
                    var need = new Dictionary<string, int>();
                    foreach (var k in BuildOrder)
                    {
                        need[k] = need.TryGetValue(k, out var c) ? c + 1 : 1;
                        if ((have.TryGetValue(k, out var h) ? h : 0) < need[k]) { next = k; break; }
                    }
                }
                if (next != null && t.Credits >= Defs.Get(next).Cost) Commands.Execute(w, team, Cmd("type", "build", "structure", next));
            }

            int refineries = structures.Count(s => s.Def.Key == "refinery" && s.IsComplete);
            int harvesters = mine.Count(e => e.IsHarvester);
            var factoryQ = t.UnitQueues[Producer.WarFactory];
            var barracksQ = t.UnitQueues[Producer.Barracks];
            bool hasFactory = w.HasComplete(team, "war_factory");
            if (hasFactory && harvesters < refineries * 2 && !factoryQ.Any(p => p.Key == "harvester") && t.Credits >= 1000)
                Commands.Execute(w, team, Cmd("type", "train", "unit", "harvester"));

            // Army, keeping a reserve for buildings early on.
            int reserve = structures.Count < 6 ? 600 : 0;
            if (hasFactory && factoryQ.Count < 2 && t.Credits - reserve >= 1200)
                Commands.Execute(w, team, Cmd("type", "train", "unit", w.Tick % 3 == 0 ? "heavy_tank" : "light_tank"));
            if (w.HasComplete(team, "barracks") && barracksQ.Count < 2 && t.Credits - reserve >= 300)
                Commands.Execute(w, team, Cmd("type", "train", "unit", w.Tick % 2 == 0 ? "rocket_soldier" : "rifleman", "count", 2));

            // Defense: anything hostile near base gets everyone.
            var army = mine.Where(e => !e.IsStructure && e.IsArmed).ToList();
            var threat = w.Entities.FirstOrDefault(e => !e.Dead && e.Team != team && !e.IsStructure && w.IsVisibleTo(team, e) && Vec2.Dist(e.Pos, t.StartPos) < 14);
            if (threat != null)
            {
                foreach (var u in army.Where(u => u.Order == Order.Idle || u.Order == Order.Move))
                    w.SetOrder(u, Order.AttackMove, threat.Pos);
                return;
            }

            // Offense: send a wave when big enough, at known structures or the enemy start.
            var idle = army.Where(u => u.Order == Order.Idle).ToList();
            if (idle.Count >= waveSize)
            {
                var enemy = w.Teams.Where(x => x.Id != team && !x.Defeated).OrderBy(x => Vec2.Dist(x.StartPos, t.StartPos)).FirstOrDefault();
                if (enemy == null) return;
                Vec2 goal = enemy.StartPos;
                var known = t.KnownEnemyStructures.Values.Where(k => k.team == enemy.Id).ToList();
                if (known.Count > 0) { var k = known[0]; goal = new Vec2(k.origin.X + 1, k.origin.Y + 1); }
                foreach (var u in idle) w.SetOrder(u, Order.AttackMove, goal);
                waveSize = System.Math.Min(14, waveSize + 2);
                Commands.Execute(w, team, Cmd("type", "say", "text", $"Wave of {idle.Count} inbound."));
            }
        }
    }
}
