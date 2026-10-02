using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>
    /// The same team state as StateView.TeamState, as plain data for programs: numbers for coordinates, HP, fuel and
    /// amounts, ids as integers, alerts and events as objects. Asked for with format=json.
    /// </summary>
    public static class StateData
    {
        static float R1(float v) => (float)Math.Round(v, 1);

        public static JObj Team(World w, int team, long sinceSeq = 0)
        {
            var t = w.Teams[team];
            var mine = w.Owned(team).ToList();
            var o = new JObj()
                .Set("format", "json")
                .Set("tick", w.Tick)
                .Set("time_s", R1(w.Time))
                .Set("game_over", w.GameOver)
                .Set("winner_team", w.Winner >= 0 ? (object)w.Winner : null)
                .Set("you", new JObj()
                    .Set("team", team).Set("name", t.Name).Set("player", t.PlayerName)
                    .Set("status", t.Resigned ? "resigned" : t.Left ? "left" : t.Defeated ? "eliminated" : "playing")
                    .Set("start", Point(t.StartPos))
                    .Set("protected_for_s", w.IsProtected(team) ? (int)(t.ProtectedUntil - w.Time) : 0)
                    .Set("stalled_for_s", t.StalledSince >= 0 ? (int)(w.Time - t.StalledSince) : 0)
                    .Set("stockpile", Stock(t))
                    .Set("power", new JObj().Set("produced", t.PowerProduced).Set("used", t.PowerUsed).Set("low", t.LowPower)))
                .Set("map", new JObj().Set("w", w.Map.W).Set("h", w.Map.H).Set("sector_tiles", w.Map.W / 8));

            o.Set("alerts", w.Alerts.Active(w, team).Select(a => (object)Alert(w, a)).ToList());
            o.Set("last_alert_seq", w.Alerts.LastSeq);
            o.Set("standing_orders", t.StandingOrders.Length == 0 ? null : t.StandingOrders);
            o.Set("orders_version", t.OrdersVersion);
            o.Set("opponents", w.Teams.Where(x => x.Id != team && !x.Left).Select(x => (object)new JObj()
                .Set("team", x.Id).Set("name", x.Name).Set("player", x.PlayerName ?? x.Controller)
                .Set("protected_for_s", w.IsProtected(x.Id) ? (int)(x.ProtectedUntil - w.Time) : 0)
                .Set("defeated", x.Defeated)).ToList());
            o.Set("explored_pct", t.Explored.Count(b => b) * 100 / t.Explored.Length);

            o.Set("structures", mine.Where(e => e.IsStructure).Select(e => (object)new JObj()
                .Set("id", e.Id).Set("type", e.Def.Key).Set("x", e.Origin.X).Set("y", e.Origin.Y).Set("w", e.Def.SizeX).Set("h", e.Def.SizeY)
                .Set("hp", (int)e.Hp).Set("max_hp", e.Def.MaxHp).Set("complete", e.IsComplete).Set("progress_pct", (int)(e.BuildProgress * 100))
                .Set("working", e.Def.Recipes.Length > 0 || e.Def.Key == "fusion_reactor" ? (object)e.Working : null)
                .Set("under_attack", w.Time - e.LastHitTime < 5)).ToList());

            o.Set("units", mine.Where(e => !e.IsStructure && !e.IsMine).Select(e =>
            {
                var u = new JObj().Set("id", e.Id).Set("type", e.Def.Key).Set("x", R1(e.Pos.X)).Set("y", R1(e.Pos.Y))
                    .Set("hp", (int)e.Hp).Set("max_hp", e.Def.MaxHp).Set("order", e.OrderName)
                    .Set("target_id", e.TargetId != 0 ? (object)e.TargetId : null).Set("air", e.IsAir)
                    .Set("speed", e.Def.Speed);
                if (e.OrderName != "idle") u.Set("order_x", R1(e.OrderPos.X)).Set("order_y", R1(e.OrderPos.Y));
                if (e.Waypoints.Count > 0) u.Set("waypoints", e.Waypoints.Select(p => (object)Point(p)).ToList());
                if (e.RetreatBelow > 0) u.Set("retreat_below_pct", (int)(e.RetreatBelow * 100));
                if (e.IsCarried) u.Set("carried_by", e.CarrierId);
                if (e.Def.Capacity > 0) u.Set("passengers", e.Passengers.Cast<object>().ToList()).Set("capacity", e.Def.Capacity);
                if (e.IsHarvester) u.Set("cargo", e.Cargo).Set("cargo_type", e.CargoType >= 0 ? Defs.Ores[e.CargoType] : null)
                                    .Set("assigned_ore", e.HarvestType >= 0 ? Defs.Ores[e.HarvestType] : null);
                if (e.Def.UsesFuel) u.Set("fuel_pct", (int)(e.FuelFraction * 100)).Set("stranded", e.Stranded).Set("landed", e.Landed);
                if (e.Def.LaysMines) u.Set("mines_queued", e.MineQueue.Count);
                return (object)u;
            }).ToList());

            o.Set("mines", mine.Where(e => e.IsMine).Select(m => (object)Point(m.Pos)).ToList());

            o.Set("enemies", w.Entities.Where(e => !e.Dead && e.Team != team && w.IsVisibleTo(team, e)).Select(e =>
            {
                var x = new JObj().Set("id", e.Id).Set("team", e.Team).Set("type", e.Def.Key).Set("hp", (int)e.Hp).Set("max_hp", e.Def.MaxHp)
                    .Set("structure", e.IsStructure).Set("air", e.IsAir);
                if (e.IsStructure) x.Set("x", e.Origin.X).Set("y", e.Origin.Y).Set("w", e.Def.SizeX).Set("h", e.Def.SizeY);
                else x.Set("x", R1(e.Pos.X)).Set("y", R1(e.Pos.Y));
                return (object)x;
            }).ToList());
            o.Set("radar_contacts", StateView.RadarContacts(w, team).Select(e => (object)new JObj()
                .Set("team", e.Team).Set("x", (int)e.Pos.X).Set("y", (int)e.Pos.Y).Set("kind", "aircraft")).ToList());

            o.Set("remembered_enemy_structures", t.KnownEnemyStructures
                .Where(kv => { var e = w.Get(kv.Key); return e == null || !w.IsVisibleTo(team, e); })
                .Select(kv => (object)new JObj().Set("id", kv.Key).Set("team", kv.Value.team).Set("type", kv.Value.key)
                    .Set("x", kv.Value.origin.X).Set("y", kv.Value.origin.Y)).ToList());

            o.Set("ore_fields", StateView.OreFields(w, t.Explored).Select(f => (object)new JObj()
                .Set("type", f.type).Set("x", f.cx).Set("y", f.cy).Set("tiles", f.tiles).Set("amount", f.total)).ToList());

            var prod = new JObj();
            prod.Set("structures", t.StructureQueue.Select(p => { var s = w.Get(p.StructureId); return (object)new JObj().Set("type", p.Key).Set("id", p.StructureId).Set("progress_pct", (int)((s?.BuildProgress ?? 0) * 100)); }).ToList());
            foreach (var kv in t.UnitQueues)
                prod.Set(Defs.ProducerKey(kv.Key), kv.Value.Select((p, i) => (object)new JObj().Set("type", p.Key)
                    .Set("progress_pct", i == 0 ? (int)(p.Progress / Defs.Get(p.Key).BuildTime * 100) : 0)).ToList());
            o.Set("production", prod);
            o.Set("build_options", BuildOptions(w, team));

            o.Set("stats", new JObj().Set("kills", t.Stats.Kills).Set("units_lost", t.Stats.UnitsLost)
                .Set("structures_lost", t.Stats.StructuresLost).Set("ore_mined", t.Stats.OreMined));
            o.Set("events", StateView.EventList(w, team, sinceSeq, 25).Select(e => (object)new JObj()
                .Set("seq", e.seq).Set("t", e.t).Set("type", e.type).Set("text", e.text)).ToList());
            o.Set("last_event_seq", w.Events.Count > 0 ? w.Events[w.Events.Count - 1].Seq : 0);
            return o;
        }

        static JObj Point(Vec2 p) => new JObj().Set("x", R1(p.X)).Set("y", R1(p.Y));

        static JObj Stock(Team t)
        {
            var o = new JObj();
            foreach (var item in Defs.Items)
                o.Set(item, new JObj().Set("amount", t.Amount(item)).Set("rate_per_s", R1(t.Rates.TryGetValue(item, out var r) ? r : 0)));
            return o;
        }

        public static JObj Alert(World w, Alert a) => new JObj()
            .Set("seq", a.Seq).Set("priority", a.Priority.ToString().ToLowerInvariant()).Set("kind", a.Kind).Set("label", AlertLog.Label(a.Kind))
            .Set("x", (int)a.Pos.X).Set("y", (int)a.Pos.Y).Set("sector", StateView.Sector(w.Map, a.Pos))
            .Set("age_s", R1((w.Tick - a.LastTick) * World.Dt)).Set("hits", a.Count)
            .Set("victims", a.Victims.Where(id => w.Get(id) != null).Cast<object>().ToList())
            .Set("attackers", a.Attackers.Where(id => w.Get(id) != null).Cast<object>().ToList())
            .Set("notes", a.Lost.Cast<object>().ToList())
            .Set("text", AlertLog.Describe(w, a));

        /// <summary>What can be built or trained right now, and for everything else exactly what's missing.</summary>
        public static JObj BuildOptions(World w, int team)
        {
            var t = w.Teams[team];
            var now = new List<object>();
            var blocked = new List<object>();
            foreach (var d in Defs.All.Values)
            {
                if (!d.Buildable || d.BuiltBy == Producer.None) continue;
                var needs = new List<string>();
                var producer = Defs.ProducerKey(d.BuiltBy);
                if (producer != null && !w.HasComplete(team, producer)) needs.Add(producer);
                foreach (var r in d.Requires) if (!w.HasComplete(team, r) && !needs.Contains(r)) needs.Add(r);
                var cost = new JObj();
                foreach (var kv in d.Cost) cost.Set(kv.Key, kv.Value);
                var shortBy = new JObj();
                foreach (var kv in d.Cost) if (t.Amount(kv.Key) < kv.Value) shortBy.Set(kv.Key, kv.Value - t.Amount(kv.Key));
                var item = new JObj().Set("type", d.Key).Set("kind", d.IsStructure ? "structure" : "unit").Set("built_by", producer).Set("cost", cost);
                if (needs.Count == 0 && shortBy.Count == 0) now.Add(item);
                else
                {
                    if (needs.Count > 0) item.Set("needs_buildings", needs.Cast<object>().ToList());
                    if (shortBy.Count > 0) item.Set("short_of", shortBy);
                    item.Set("why", needs.Count > 0
                        ? $"needs a completed {string.Join(" and ", needs)}" + (shortBy.Count > 0 ? " (and more resources)" : "")
                        : "can't afford yet: " + string.Join(", ", d.Cost.Where(kv => t.Amount(kv.Key) < kv.Value).Select(kv => $"{kv.Key} {t.Amount(kv.Key)}/{kv.Value}")));
                    blocked.Add(item);
                }
            }
            return new JObj().Set("now", now).Set("blocked", blocked);
        }
    }
}
