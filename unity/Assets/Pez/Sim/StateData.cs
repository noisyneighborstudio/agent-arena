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
                    .Set("reserves", t.Reserve.Aggregate(new JObj(), (j, kv) => j.Set(kv.Key, kv.Value)))
                    .Set("power", new JObj().Set("produced", t.PowerProduced).Set("used", t.PowerUsed).Set("low", t.LowPower)))
                .Set("map", new JObj().Set("w", w.Map.W).Set("h", w.Map.H).Set("sector_tiles", w.Map.W / 8))
                .Set("upkeep", StateView.UpkeepJson(w, team))
                .Set("match", StateView.MatchJson(w));

            o.Set("alerts", w.Alerts.Active(w, team).Select(a => (object)Alert(w, a)).ToList());
            o.Set("last_alert_seq", w.Alerts.LastSeq);
            o.Set("standing_orders", t.StandingOrders.Length == 0 ? null : t.StandingOrders);
            o.Set("orders_version", t.OrdersVersion);
            o.Set("opponents", w.Teams.Where(x => x.Id != team && !x.Left).Select(x => (object)new JObj()
                .Set("team", x.Id).Set("name", x.Name).Set("player", x.PlayerName ?? x.Controller)
                .Set("protected_for_s", w.IsProtected(x.Id) ? (int)(x.ProtectedUntil - w.Time) : 0)
                .Set("defeated", x.Defeated)).ToList());
            o.Set("explored_pct", t.Explored.Count(b => b) * 100 / t.Explored.Length);

            o.Set("structures", mine.Where(e => e.IsStructure).Select(e =>
            {
                var s = new JObj()
                    .Set("id", e.Id).Set("type", e.Def.Key).Set("x", e.Origin.X).Set("y", e.Origin.Y).Set("w", e.Def.SizeX).Set("h", e.Def.SizeY)
                    .Set("hp", (int)e.Hp).Set("max_hp", e.Def.MaxHp).Set("complete", e.IsComplete).Set("progress_pct", StateView.Pct(e.BuildProgress))
                    .Set("working", e.Def.Recipes.Length > 0 || e.Def.Key == "fusion_reactor" ? (object)e.Working : null)
                    .Set("under_attack", w.Time - e.LastHitTime < 5);
                if (e.IsArmed) s.Set("offline", e.Offline);
                var d = e.Def.Key == "deep_mine" ? w.Map.DepositById(e.DepositId) : null;
                if (d != null) s.Set("ore", Defs.Ores[d.Type]).Set("deposit_left", (int)d.Amount).Set("runs_dry_in_s", (int)World.DeepMineSecondsLeft(d, t));
                return (object)s;
            }).ToList());

            o.Set("units", mine.Where(e => !e.IsStructure && !e.IsMine).Select(e =>
            {
                var u = new JObj().Set("id", e.Id).Set("type", e.Def.Key).Set("x", R1(e.Pos.X)).Set("y", R1(e.Pos.Y))
                    .Set("hp", (int)e.Hp).Set("max_hp", e.Def.MaxHp).Set("order", e.OrderName)
                    .Set("target_id", e.TargetId != 0 ? (object)e.TargetId : null).Set("air", e.IsAir)
                    .Set("speed", e.Def.Speed).Set("sight", e.Def.Sight);
                if (e.OrderName != "idle") u.Set("order_x", R1(e.OrderPos.X)).Set("order_y", R1(e.OrderPos.Y));
                if (e.DockName != null) u.Set("dock", e.DockName);
                if (e.Waypoints.Count > 0) u.Set("waypoints", e.Waypoints.Select(p => (object)Point(p)).ToList());
                if (e.RetreatBelow > 0) u.Set("retreat_below_pct", StateView.Pct(e.RetreatBelow));
                if (e.IsCarried) u.Set("carried_by", e.CarrierId);
                if (e.Def.Capacity > 0) u.Set("passengers", e.Passengers.Cast<object>().ToList()).Set("capacity", e.Def.Capacity);
                if (e.IsHarvester) u.Set("cargo", e.Cargo).Set("cargo_type", e.CargoType >= 0 ? Defs.Ores[e.CargoType] : null)
                                    .Set("assigned_ore", e.HarvestType >= 0 ? Defs.Ores[e.HarvestType] : null);
                if (e.Def.UsesFuel) u.Set("fuel_pct", StateView.Pct(e.FuelFraction)).Set("stranded", e.Stranded).Set("landed", e.Landed);
                if (e.Def.LaysMines) u.Set("mines_queued", e.MineQueue.Count);
                var survey = StateView.SurveyStatus(w, e);
                if (survey != null) u.Set("survey", survey);
                if (e.Order == Order.Drill) u.Set("zone", e.ZoneId);
                return (object)u;
            }).ToList());

            o.Set("mines", mine.Where(e => e.IsMine).Select(m => (object)Point(m.Pos)).ToList());

            o.Set("derricks", StateView.DerrickInfo(w, team).Select(x => (object)new JObj().Set("id", x.d.Id).Set("x", x.d.Origin.X).Set("y", x.d.Origin.Y).Set("w", 2).Set("h", 2)
                .Set("owner_team", x.owner >= -1 ? (object)x.owner : null).Set("owner", x.owner == -1 ? "neutral" : x.owner >= 0 ? w.Teams[x.owner].Name : null)
                .Set("yours", x.owner == team).Set("in_sight", x.seen).Set("hp", x.seen ? (object)(int)x.d.Hp : null).Set("max_hp", x.d.Def.MaxHp)
                .Set("capturable_now", x.capturable).Set("income_steel_per_s", World.DerrickSteel)).ToList());

            o.Set("enemies", w.Entities.Where(e => !e.Dead && e.Team != team && e.Team >= 0 && w.IsVisibleTo(team, e)).Select(e =>
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
                .Where(kv => kv.Value.team >= 0 && kv.Value.key != "derrick" && (w.Get(kv.Key) is var e && (e == null || !w.IsVisibleTo(team, e))))
                .Select(kv => (object)new JObj().Set("id", kv.Key).Set("team", kv.Value.team).Set("type", kv.Value.key)
                    .Set("x", kv.Value.origin.X).Set("y", kv.Value.origin.Y)).ToList());

            o.Set("ore_fields", StateView.OreFields(w, t.Explored).Select(f => (object)new JObj()
                .Set("type", f.type).Set("x", f.cx).Set("y", f.cy).Set("tiles", f.tiles).Set("amount", f.total)).ToList());

            o.Set("mining_zones", w.Map.Deep.Where(d => t.Surveyed.Contains(d.Id)).Select(d =>
            {
                var status = w.ZoneStatus(d, team, out var dm);
                t.Zones.TryGetValue(d.Id, out var f);
                return (object)new JObj().Set("id", d.Id).Set("ore", Defs.Ores[d.Type]).Set("x", R1(d.Pos.X)).Set("y", R1(d.Pos.Y))
                    .Set("amount", (int)d.Amount).Set("initial", (int)d.Initial).Set("status", status)
                    .Set("mine_id", dm != null && (dm.Team == team || w.IsVisibleTo(team, dm)) ? (object)dm.Id : null)
                    .Set("mine_team", dm != null && (dm.Team == team || w.IsVisibleTo(team, dm)) ? (object)dm.Team : null)
                    .Set("rigs_en_route", w.RigsBound(team, d.Id).Select(r => (object)r.Id).ToList())
                    .Set("flagged_by", f != null ? (object)f.FlaggedBy : null)
                    .Set("flagged_at_s", f != null ? (object)R1(f.FlaggedAt) : null);
            }).ToList());
            o.Set("survey_sites", t.SurveySites.Select(p => (object)Point(p)).ToList());

            var prod = new JObj();
            prod.Set("structures", t.StructureQueue.Select(p => { var s = w.Get(p.StructureId); return (object)new JObj().Set("type", p.Key).Set("id", p.StructureId).Set("progress_pct", StateView.Pct(s?.BuildProgress ?? 0)); }).ToList());
            foreach (var kv in t.UnitQueues)
                prod.Set(Defs.ProducerKey(kv.Key), kv.Value.Select((p, i) => (object)new JObj().Set("type", p.Key)
                    .Set("progress_pct", i == 0 ? StateView.Pct(p.Progress / w.Def(p.Key).BuildTime) : 0)
                    .Set("at", p.StructureId != 0 ? (object)p.StructureId : null)).ToList());
            o.Set("production", prod);
            o.Set("build_options", BuildOptions(w, team));
            o.Set("inventions", Tech.OwnJson(w, team));
            o.Set("enemy_inventions_seen", Tech.SeenJson(w, team));

            o.Set("stats", new JObj().Set("kills", t.Stats.Kills).Set("units_lost", t.Stats.UnitsLost)
                .Set("structures_lost", t.Stats.StructuresLost).Set("ore_mined", t.Stats.OreMined)
                .Set("kill_value", t.Stats.KillValue).Set("salvage_left", t.Stats.SalvageLeft)
                .Set("derricks_captured", t.Stats.DerricksCaptured).Set("derrick_steel", (int)t.Stats.DerrickSteel).Set("upkeep_paid", (int)t.Stats.UpkeepPaid));
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
            foreach (var d in Defs.All.Values.Concat(Tech.Own(w, team).Select(i => i.Def)))
            {
                if (!d.Buildable || d.BuiltBy == Producer.None) continue;
                var needs = new List<string>();
                var researching = d.OwnerTeam >= 0 && w.Inventions.TryGetValue(d.Key, out var inv) && !inv.Done ? inv : null;
                var producer = Defs.ProducerKey(d.BuiltBy);
                if (producer != null && !w.HasComplete(team, producer)) needs.Add(producer);
                foreach (var r in d.Requires) if (!w.HasComplete(team, r) && !needs.Contains(r)) needs.Add(r);
                var cost = new JObj();
                foreach (var kv in d.Cost) cost.Set(kv.Key, kv.Value);
                var shortBy = new JObj();
                foreach (var kv in d.Cost) if (t.Amount(kv.Key) < kv.Value) shortBy.Set(kv.Key, kv.Value - t.Amount(kv.Key));
                var item = new JObj().Set("type", d.Key).Set("kind", d.IsStructure ? "structure" : "unit").Set("built_by", producer).Set("cost", cost);
                if (researching != null) { item.Set("why", $"still being researched ({researching.Pct}%)"); blocked.Add(item); }
                else if (needs.Count == 0 && shortBy.Count == 0) now.Add(item);
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
