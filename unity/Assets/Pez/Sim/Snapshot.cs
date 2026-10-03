using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Pez.Sim
{
    using D = Dictionary<string, object>;

    /// <summary>
    /// A complete saved game, so a host restart (a new build swapped in, the app or the machine restarting) resumes the
    /// same world, seats and players instead of starting over. Serialization is explicit, field by field, so every saved
    /// field is a decision; when you add state to the sim, add it here too (the round-trip test catches what's missed).
    ///
    /// Compatibility rule. Restores happen across build updates, so a NEWER build must load an OLDER snapshot:
    ///  - Fields are only ever added. A missing field keeps the default a fresh object has (its class initializer, or a
    ///    value rebuilt from the rest of the snapshot), and unknown fields are ignored. So old snapshots load in new
    ///    builds, and new snapshots in old builds (a rollback).
    ///  - Values equal to that default may be left out when writing, which keeps snapshots small.
    ///  - Enums are stored by name and entity types by key. An unknown name falls back to the default; an entity whose
    ///    type no longer exists is dropped.
    ///  - Bump Schema when adding fields (for diagnostics). Bump MinReader only for a change an older reader would
    ///    misread; readers refuse snapshots whose min_reader is newer than their Schema.
    /// </summary>
    public static class Snapshot
    {
        public const string Format = "pezz-snapshot";
        /// <summary>1: first version. 2: mining zones, prospecting surveyors, drill rigs' zones, arena champion. 3: agent inventions.</summary>
        public const int Schema = 3;
        public const int MinReader = 1;
        /// <summary>Events kept: the last two minutes, plus recent chat, at most MaxEvents. Alerts: the last minute.</summary>
        const int MaxEvents = 1500;
        const float EventWindow = 120f, AlertWindow = 60f;

        /// <summary>The whole game as a JSON-ready object. Hosts add their own sections (the API's tokens) before writing.</summary>
        public static JObj Write(Game g, DateTime? now = null) => new JObj()
            .Set("format", Format).Set("schema", Schema).Set("min_reader", MinReader)
            .Set("saved_at", (now ?? DateTime.UtcNow).ToString("o", CultureInfo.InvariantCulture))
            .Set("rules_version", StateView.RulesVersion)
            .Set("config", WriteConfig(g.Config))
            .Set("game", g.SaveState())
            .Set("world", g.World.SaveState());

        /// <summary>Parses and checks a snapshot (throws FormatException if it isn't one this build can read).</summary>
        public static D Parse(string json)
        {
            var root = Json.Parse(json) as D ?? throw new FormatException("a snapshot is a JSON object");
            if (root.Str("format") != Format) throw new FormatException("not a Pezz snapshot");
            int minReader = root.In("min_reader", 1);
            if (minReader > Schema) throw new FormatException($"snapshot needs a newer build (min_reader {minReader}, this build reads schema {Schema})");
            if (root.Obj("world") == null) throw new FormatException("snapshot has no world");
            return root;
        }

        // ------------------------------------------------------------------ config

        static JObj WriteConfig(GameConfig c) => new JObj()
            .Set("seed", c.Seed).Set("map_size", c.MapSize).Set("speed", SnapIO.X(c.Speed))
            .Set("controllers", (c.Controllers ?? new string[0]).Cast<object>().ToList())
            .Set("orders", (c.Orders ?? new string[0]).Select(o => (object)(o ?? "")).ToList())
            .Set("open", c.Open).Set("max_players", c.MaxPlayers).Set("max_map_size", c.MaxMapSize)
            .Set("ore_scale", SnapIO.X(c.OreScale)).Set("house_ais", c.HouseAIs).Set("house_resign_above", c.HouseResignAbove);

        internal static GameConfig ReadConfig(D d)
        {
            var c = new GameConfig();
            if (d == null) return c;
            d.Load("seed", ref c.Seed); d.Load("map_size", ref c.MapSize); d.Load("speed", ref c.Speed);
            if (d.Has("controllers", out _)) c.Controllers = d.Arr("controllers").Select(x => x?.ToString() ?? "llm").ToArray();
            if (d.Has("orders", out _)) c.Orders = d.Arr("orders").Select(x => x?.ToString() ?? "").ToArray();
            d.Load("open", ref c.Open); d.Load("max_players", ref c.MaxPlayers); d.Load("max_map_size", ref c.MaxMapSize);
            d.Load("ore_scale", ref c.OreScale); d.Load("house_ais", ref c.HouseAIs); d.Load("house_resign_above", ref c.HouseResignAbove);
            return c;
        }

        // ------------------------------------------------------------------ teams

        internal static JObj WriteTeam(Team t)
        {
            var o = new JObj().Set("id", t.Id).Set("name", t.Name).Set("controller", t.Controller).Put("player", t.PlayerName)
                .Put("standing_orders", t.StandingOrders).Put("orders_version", t.OrdersVersion)
                .Set("stock", SnapIO.Floats(t.Stock)).Set("rates", SnapIO.Floats(t.Rates))
                .Set("power_produced", t.PowerProduced).Set("power_used", t.PowerUsed)
                .Set("start", SnapIO.V(t.StartPos)).Put("defeated", t.Defeated).Put("left", t.Left).Put("resigned", t.Resigned)
                .Put("stalled_since", t.StalledSince, -1f).Put("house", t.House).Set("seat", t.Seat).Put("protected_until", t.ProtectedUntil)
                .Put("surface_warned_at", t.SurfaceWarnedAt, -999f).Put("last_command_at", t.LastCommandAt, -1f);
            if (t.Visible != null) o.Set("visible", SnapIO.PackBits(t.Visible));
            if (t.Explored != null) o.Set("explored", SnapIO.PackBits(t.Explored));
            if (t.Detected.Count > 0) o.Set("detected", t.Detected.Cast<object>().ToList());
            if (t.Revealed.Count > 0) o.Set("revealed", t.Revealed.Select(kv => (object)new List<object> { kv.Key, SnapIO.X(kv.Value) }).ToList());
            if (t.StructureQueue.Count > 0) o.Set("structure_queue", t.StructureQueue.Select(p => (object)WriteItem(p)).ToList());
            var uq = new JObj();
            foreach (var kv in t.UnitQueues) if (kv.Value.Count > 0) uq.Set(kv.Key.ToString(), kv.Value.Select(p => (object)WriteItem(p)).ToList());
            if (uq.Count > 0) o.Set("unit_queues", uq);
            if (t.KnownEnemyStructures.Count > 0)
                o.Set("known_enemy_structures", t.KnownEnemyStructures.Select(kv => (object)new List<object> { kv.Key, kv.Value.key, kv.Value.origin.X, kv.Value.origin.Y, kv.Value.team }).ToList());
            if (t.Surveyed.Count > 0) o.Set("surveyed", t.Surveyed.Cast<object>().ToList());
            if (t.SurveySites.Count > 0) o.Set("survey_sites", SnapIO.Vs(t.SurveySites));
            if (t.Reserve.Count > 0) o.Set("reserve", t.Reserve.Aggregate(new JObj(), (j, kv) => j.Set(kv.Key, kv.Value)));
            if (t.Zones.Count > 0) o.Set("zones", t.Zones.Values.Select(z => (object)new List<object> { z.ZoneId, z.FlaggedBy, SnapIO.X(z.FlaggedAt) }).ToList());
            var s = t.Stats;
            o.Set("stats", new JObj().Put("units_built", s.UnitsBuilt).Put("structures_built", s.StructuresBuilt).Put("units_lost", s.UnitsLost)
                .Put("structures_lost", s.StructuresLost).Put("kills", s.Kills).Put("ore_mined", s.OreMined)
                .Set("built", s.Built.Aggregate(new JObj(), (j, kv) => j.Set(kv.Key, kv.Value))));
            return o;
        }

        static JObj WriteItem(ProdItem p) => new JObj().Set("key", p.Key).Put("progress", p.Progress).Put("structure_id", p.StructureId);

        static ProdItem ReadItem(D d)
        {
            var p = new ProdItem();
            d.Load("key", ref p.Key); d.Load("progress", ref p.Progress); d.Load("structure_id", ref p.StructureId);
            return Defs.Get(p.Key) == null ? null : p;
        }

        internal static Team ReadTeam(D d, int index, Map map)
        {
            var t = new Team { Id = index };
            d.Load("name", ref t.Name); d.Load("controller", ref t.Controller); d.Load("player", ref t.PlayerName);
            d.Load("standing_orders", ref t.StandingOrders); d.Load("orders_version", ref t.OrdersVersion);
            SnapIO.LoadFloats(d.Obj("stock"), t.Stock); SnapIO.LoadFloats(d.Obj("rates"), t.Rates);
            d.Load("power_produced", ref t.PowerProduced); d.Load("power_used", ref t.PowerUsed);
            d.Load("start", ref t.StartPos); d.Load("defeated", ref t.Defeated); d.Load("left", ref t.Left); d.Load("resigned", ref t.Resigned);
            d.Load("stalled_since", ref t.StalledSince); d.Load("house", ref t.House); d.Load("seat", ref t.Seat);
            d.Load("protected_until", ref t.ProtectedUntil); d.Load("surface_warned_at", ref t.SurfaceWarnedAt); d.Load("last_command_at", ref t.LastCommandAt);
            t.Visible = SnapIO.UnpackBits(d.Str("visible"), map.W * map.H);
            t.Explored = SnapIO.UnpackBits(d.Str("explored"), map.W * map.H);
            foreach (var x in d.Arr("detected")) if (x is double id) t.Detected.Add((int)id);
            foreach (var x in d.Arr("revealed")) if (x is List<object> l && l.Count >= 2 && l[0] is double id) t.Revealed[(int)id] = SnapIO.ToF(l[1]);
            foreach (var x in d.Objs("structure_queue")) { var p = ReadItem(x); if (p != null) t.StructureQueue.Add(p); }
            var uq = d.Obj("unit_queues");
            if (uq != null)
                foreach (var kv in uq)
                {
                    if (!Enum.TryParse<Producer>(kv.Key, out var prod) || !(kv.Value is List<object> items)) continue;
                    if (!t.UnitQueues.TryGetValue(prod, out var q)) t.UnitQueues[prod] = q = new List<ProdItem>();
                    foreach (var x in items.OfType<D>()) { var p = ReadItem(x); if (p != null) q.Add(p); }
                }
            foreach (var x in d.Arr("known_enemy_structures"))
                if (x is List<object> l && l.Count >= 5 && l[0] is double id)
                    t.KnownEnemyStructures[(int)id] = (l[1]?.ToString(), new Int2(SnapIO.ToI(l[2]), SnapIO.ToI(l[3])), SnapIO.ToI(l[4]));
            foreach (var x in d.Arr("surveyed")) if (x is double id) t.Surveyed.Add((int)id);
            t.SurveySites.AddRange(SnapIO.ToVecs(d.Arr("survey_sites")));
            var rv = d.Obj("reserve");
            if (rv != null) foreach (var kv in rv) t.Reserve[kv.Key] = SnapIO.ToI(kv.Value);
            foreach (var x in d.Arr("zones"))
                if (x is List<object> l && l.Count >= 3) t.Zones[SnapIO.ToI(l[0])] = new ZoneFlag { ZoneId = SnapIO.ToI(l[0]), FlaggedBy = SnapIO.ToI(l[1]), FlaggedAt = SnapIO.ToF(l[2]) };
            var s = d.Obj("stats");
            if (s != null)
            {
                var st = t.Stats;
                s.Load("units_built", ref st.UnitsBuilt); s.Load("structures_built", ref st.StructuresBuilt); s.Load("units_lost", ref st.UnitsLost);
                s.Load("structures_lost", ref st.StructuresLost); s.Load("kills", ref st.Kills); s.Load("ore_mined", ref st.OreMined);
                var b = s.Obj("built");
                if (b != null) foreach (var kv in b) if (kv.Value is double n) st.Built[kv.Key] = (int)n;
            }
            return t;
        }

        // ------------------------------------------------------------------ entities

        internal static JObj WriteEntity(Entity e)
        {
            var o = new JObj().Set("id", e.Id).Set("team", e.Team).Set("def", e.Def.Key)
                .Set("pos", SnapIO.V(e.Pos)).Set("prev_pos", SnapIO.V(e.PrevPos))
                .Put("facing", e.Facing).Put("turret_facing", e.TurretFacing).Set("hp", SnapIO.X(e.Hp)).Put("dead", e.Dead);
            if (e.IsStructure) o.Set("origin", new List<object> { e.Origin.X, e.Origin.Y });
            o.Put("build_progress", e.BuildProgress, 1f);
            if (e.Rally.HasValue) o.Set("rally", SnapIO.V(e.Rally.Value));
            o.PutEnum("order", e.Order, Order.Idle).PutV("order_pos", e.OrderPos).PutV("guard_pos", e.GuardPos).Put("target", e.TargetId);
            if (e.Path != null) o.Set("path", SnapIO.Vs(e.Path)).Put("path_idx", e.PathIdx);
            o.Put("cooldown", e.Cooldown).Put("speed_cap", e.SpeedCap)
             .PutV("progress_pos", e.ProgressPos).Put("progress_at", e.ProgressAt).Put("ghost_until", e.GhostUntil).Put("progress_dist", e.ProgressDist);
            if (e.Waypoints.Count > 0) o.Set("waypoints", SnapIO.Vs(e.Waypoints));
            o.Put("waypoint_loop", e.WaypointLoop).Put("retreat_below", e.RetreatBelow).Put("retreating", e.Retreating)
             .Put("repath_timer", e.RepathTimer).Put("moving", e.Moving).Put("last_attacker", e.LastAttackerId)
             .Put("last_hit_time", e.LastHitTime, -999f).Put("last_call_for_help", e.LastCallForHelp, -999f)
             .Put("responding", e.Responding).PutV("home_pos", e.HomePos)
             .Put("cargo", e.Cargo).Put("cargo_type", e.CargoType, -1).Put("harvest_type", e.HarvestType, -1);
            if (e.HarvestTile.HasValue) o.Set("harvest_tile", new List<object> { e.HarvestTile.Value.X, e.HarvestTile.Value.Y });
            o.PutEnum("dock", e.Dock, DockStep.None).Put("dock_at", e.DockAt).Put("burning", e.Burning);
            o.Put("work_timer", e.WorkTimer).Put("deposit", e.DepositId).Put("working", e.Working).Put("carrier", e.CarrierId);
            if (e.Passengers.Count > 0) o.Set("passengers", e.Passengers.Cast<object>().ToList());
            o.Put("fuel", e.Fuel).Put("landed", e.Landed).Put("stranded", e.Stranded).Put("at_depot", e.AtDepot).Put("fuel_warned", e.FuelWarned)
             .Put("no_auto_refuel_until", e.NoAutoRefuelUntil).PutEnum("resume_order", e.ResumeOrder, Order.Idle)
             .PutV("resume_pos", e.ResumePos).PutV("resume_guard", e.ResumeGuard).Put("resume_target", e.ResumeTarget)
             .Put("resume_speed_cap", e.ResumeSpeedCap);
            if (e.ResumeWaypoints.Count > 0) o.Set("resume_waypoints", SnapIO.Vs(e.ResumeWaypoints));
            if (e.MineQueue.Count > 0) o.Set("mine_queue", SnapIO.Vs(e.MineQueue));
            o.Put("last_fired_at", e.LastFiredAt, -999f)
             .Put("prospecting", e.Prospecting).PutV("prospect_center", e.ProspectCenter).Put("prospect_radius", e.ProspectRadius)
             .Put("survey_failure", e.SurveyFailure).Put("survey_failed_at", e.SurveyFailedAt).Put("zone", e.ZoneId);
            if (e.SkipSites.Count > 0) o.Set("skip_sites", SnapIO.Vs(e.SkipSites));
            return o;
        }

        /// <summary>Null if the entity's type no longer exists in this build.</summary>
        internal static Entity ReadEntity(D d)
        {
            var def = Defs.Get(d.Str("def"));
            if (def == null) return null;
            var e = new Entity { Def = def };
            d.Load("id", ref e.Id); d.Load("team", ref e.Team);
            d.Load("pos", ref e.Pos); e.PrevPos = e.Pos; d.Load("prev_pos", ref e.PrevPos);
            d.Load("facing", ref e.Facing); d.Load("turret_facing", ref e.TurretFacing);
            e.Hp = def.MaxHp; d.Load("hp", ref e.Hp); d.Load("dead", ref e.Dead);
            if (d.Has("origin", out var og) && og is List<object> ol && ol.Count >= 2) e.Origin = new Int2(SnapIO.ToI(ol[0]), SnapIO.ToI(ol[1]));
            d.Load("build_progress", ref e.BuildProgress);
            if (d.Has("rally", out _)) { var r = default(Vec2); d.Load("rally", ref r); e.Rally = r; }
            d.LoadEnum("order", ref e.Order); d.Load("order_pos", ref e.OrderPos); d.Load("guard_pos", ref e.GuardPos); d.Load("target", ref e.TargetId);
            if (d.Has("path", out _)) { e.Path = SnapIO.ToVecs(d.Arr("path")); d.Load("path_idx", ref e.PathIdx); }
            d.Load("cooldown", ref e.Cooldown); d.Load("speed_cap", ref e.SpeedCap);
            d.Load("progress_pos", ref e.ProgressPos); d.Load("progress_at", ref e.ProgressAt); d.Load("ghost_until", ref e.GhostUntil); d.Load("progress_dist", ref e.ProgressDist);
            e.Waypoints.AddRange(SnapIO.ToVecs(d.Arr("waypoints")));
            d.Load("waypoint_loop", ref e.WaypointLoop); d.Load("retreat_below", ref e.RetreatBelow); d.Load("retreating", ref e.Retreating);
            d.Load("repath_timer", ref e.RepathTimer); d.Load("moving", ref e.Moving); d.Load("last_attacker", ref e.LastAttackerId);
            d.Load("last_hit_time", ref e.LastHitTime); d.Load("last_call_for_help", ref e.LastCallForHelp);
            d.Load("responding", ref e.Responding); d.Load("home_pos", ref e.HomePos);
            d.Load("cargo", ref e.Cargo); d.Load("cargo_type", ref e.CargoType); d.Load("harvest_type", ref e.HarvestType);
            if (d.Has("harvest_tile", out var ht) && ht is List<object> hl && hl.Count >= 2) e.HarvestTile = new Int2(SnapIO.ToI(hl[0]), SnapIO.ToI(hl[1]));
            d.LoadEnum("dock", ref e.Dock); d.Load("dock_at", ref e.DockAt); d.Load("burning", ref e.Burning);
            d.Load("work_timer", ref e.WorkTimer); d.Load("deposit", ref e.DepositId); d.Load("working", ref e.Working); d.Load("carrier", ref e.CarrierId);
            foreach (var x in d.Arr("passengers")) if (x is double pid) e.Passengers.Add((int)pid);
            // A snapshot from before fuel existed: start full rather than stranded.
            e.Fuel = def.Fuel; d.Load("fuel", ref e.Fuel);
            d.Load("landed", ref e.Landed); d.Load("stranded", ref e.Stranded); d.Load("at_depot", ref e.AtDepot); d.Load("fuel_warned", ref e.FuelWarned);
            d.Load("no_auto_refuel_until", ref e.NoAutoRefuelUntil); d.LoadEnum("resume_order", ref e.ResumeOrder);
            d.Load("resume_pos", ref e.ResumePos); d.Load("resume_guard", ref e.ResumeGuard); d.Load("resume_target", ref e.ResumeTarget);
            d.Load("resume_speed_cap", ref e.ResumeSpeedCap);
            e.ResumeWaypoints.AddRange(SnapIO.ToVecs(d.Arr("resume_waypoints")));
            e.MineQueue.AddRange(SnapIO.ToVecs(d.Arr("mine_queue")));
            d.Load("last_fired_at", ref e.LastFiredAt);
            d.Load("prospecting", ref e.Prospecting); d.Load("prospect_center", ref e.ProspectCenter); d.Load("prospect_radius", ref e.ProspectRadius);
            d.Load("survey_failure", ref e.SurveyFailure); d.Load("survey_failed_at", ref e.SurveyFailedAt); d.Load("zone", ref e.ZoneId);
            e.SkipSites.AddRange(SnapIO.ToVecs(d.Arr("skip_sites")));
            return e;
        }

        // ------------------------------------------------------------------ projectiles, events

        internal static JObj WriteProjectile(World w, Projectile p) => new JObj()
            .Set("id", p.Id).Set("team", p.Team).Put("source", p.SourceId).Put("target", p.TargetId)
            .Set("pos", SnapIO.V(p.Pos)).Set("prev_pos", SnapIO.V(p.PrevPos)).Set("target_pos", SnapIO.V(p.TargetPos))
            .Set("weapon_of", (Defs.All.Values.FirstOrDefault(d => d.Weapon == p.Weapon) ?? w.Inventions.Values.FirstOrDefault(i => i.Def.Weapon == p.Weapon)?.Def)?.Key);

        // ------------------------------------------------------------------ inventions

        /// <summary>
        /// An agent-invented unit (Invention.cs). Saved as the stats and price it was researched with, not re-evaluated on
        /// load: the team paid for this design, so a pricing change in a newer build doesn't alter it mid-game.
        /// </summary>
        internal static JObj WriteInvention(Invention i)
        {
            var d = i.Def;
            return new JObj()
                .Set("key", i.Key).Set("name", i.Name).Set("team", i.Team).Set("chassis", i.Chassis).Put("weapon_from", i.WeaponFrom)
                .Set("hp", d.MaxHp).Set("speed", SnapIO.X(d.Speed)).Set("sight", SnapIO.X(d.Sight)).Put("fuel", d.Fuel)
                .Set("damage", SnapIO.X(d.Weapon.Damage)).Set("range", SnapIO.X(d.Weapon.Range)).Set("cooldown", SnapIO.X(d.Weapon.Cooldown))
                .Set("cost", SnapIO.Ints(d.Cost)).Set("build_time", SnapIO.X(d.BuildTime)).Set("description", d.Description)
                .Set("research_cost", SnapIO.Ints(i.ResearchCost)).Set("research_time", SnapIO.X(i.ResearchTime)).Put("progress", i.Progress)
                .Set("novelty", SnapIO.X(i.Novelty)).Set("price_factor", SnapIO.X(i.PriceFactor)).Set("summary", i.Summary)
                .Put("proposed_at", i.ProposedAt).Put("researched_at", i.ResearchedAt, -1f).Put("built", i.Built).Put("lost", i.Lost).Put("kills", i.Kills);
        }

        /// <summary>Null if this build no longer has the unit it was based on (its units are then dropped like any unknown type).</summary>
        internal static Invention ReadInvention(D d)
        {
            Defs.All.TryGetValue(d.Str("chassis") ?? "", out var chassis);
            var donorKey = d.Str("weapon_from");
            var donor = donorKey == null ? chassis : Defs.All.TryGetValue(donorKey, out var dn) ? dn : null;
            var key = d.Str("key");
            if (!Tech.IsChassis(chassis) || !Tech.IsChassis(donor) || key == null) return null;
            float hp = chassis.MaxHp, speed = chassis.Speed, sight = chassis.Sight, fuel = chassis.Fuel;
            float dmg = donor.Weapon.Damage, range = donor.Weapon.Range, cd = donor.Weapon.Cooldown;
            d.Load("hp", ref hp); d.Load("speed", ref speed); d.Load("sight", ref sight); d.Load("fuel", ref fuel);
            d.Load("damage", ref dmg); d.Load("range", ref range); d.Load("cooldown", ref cd);
            var i = new Invention { Key = key, Name = d.Str("name") ?? key, Chassis = chassis.Key, WeaponFrom = donorKey, ResearchCost = new Dictionary<string, int>() };
            d.Load("team", ref i.Team);
            var def = Tech.MakeDef(chassis, donor, i.Team, key, i.Name, Tech.MakeWeapon(donor.Weapon, dmg, range, cd), hp, speed, sight, fuel);
            def.Cost = new Dictionary<string, int>(chassis.Cost);
            SnapIO.LoadInts(d.Obj("cost"), def.Cost);
            d.Load("build_time", ref def.BuildTime);
            d.Load("description", ref def.Description);
            i.Def = def;
            SnapIO.LoadInts(d.Obj("research_cost"), i.ResearchCost);
            d.Load("research_time", ref i.ResearchTime); d.Load("progress", ref i.Progress);
            d.Load("novelty", ref i.Novelty); d.Load("price_factor", ref i.PriceFactor);
            i.Summary = d.Str("summary") ?? Tech.Spec(def);
            d.Load("proposed_at", ref i.ProposedAt); d.Load("researched_at", ref i.ResearchedAt);
            d.Load("built", ref i.Built); d.Load("lost", ref i.Lost); d.Load("kills", ref i.Kills);
            return i;
        }

        internal static Projectile ReadProjectile(D d)
        {
            var weapon = Defs.Get(d.Str("weapon_of"))?.Weapon;
            if (weapon == null) return null;
            var p = new Projectile { Weapon = weapon };
            d.Load("id", ref p.Id); d.Load("team", ref p.Team); d.Load("source", ref p.SourceId); d.Load("target", ref p.TargetId);
            d.Load("pos", ref p.Pos); p.PrevPos = p.Pos; d.Load("prev_pos", ref p.PrevPos); d.Load("target_pos", ref p.TargetPos);
            return p;
        }

        internal static List<object> WriteEvents(World w)
        {
            int keepFrom = w.Tick - (int)(EventWindow * World.TickRate);
            var keep = w.Events.Where(e => e.Tick >= keepFrom || e.Type == "chat").ToList();
            // Old chat beyond the last 20 lines isn't worth keeping.
            var oldChat = keep.Where(e => e.Tick < keepFrom).ToList();
            if (oldChat.Count > 20) { var drop = new HashSet<GameEvent>(oldChat.Take(oldChat.Count - 20)); keep.RemoveAll(drop.Contains); }
            if (keep.Count > MaxEvents) keep.RemoveRange(0, keep.Count - MaxEvents);
            return keep.Select(e => (object)new JObj().Set("seq", e.Seq).Set("tick", e.Tick).Set("type", e.Type).Put("team", e.Team, -1)
                .Put("a", e.A).Put("b", e.B).PutV("pos", e.Pos).PutV("pos2", e.Pos2).Put("key", e.Key).Put("text", e.Text)).ToList();
        }

        internal static GameEvent ReadEvent(D d)
        {
            var e = new GameEvent();
            d.Load("seq", ref e.Seq); d.Load("tick", ref e.Tick); d.Load("type", ref e.Type); d.Load("team", ref e.Team);
            d.Load("a", ref e.A); d.Load("b", ref e.B); d.Load("pos", ref e.Pos); d.Load("pos2", ref e.Pos2); d.Load("key", ref e.Key); d.Load("text", ref e.Text);
            return e.Type == null ? null : e;
        }

        internal static float AlertKeepSeconds => AlertWindow;
    }

    // ---------------------------------------------------------------------- the partial classes' own state

    public partial class Game
    {
        /// <summary>When the snapshot this game was resumed from was saved (ISO 8601 UTC), or null for a fresh game.</summary>
        public string ResumedFrom;
        /// <summary>When the host last saved this game (ISO 8601 UTC), or null.</summary>
        public string LastSaved;

        internal JObj SaveState() => new JObj()
            .Set("speed", SnapIO.X(Speed)).Put("paused", Paused).Set("accumulator", SnapIO.X(accumulator)).Set("next_house_check", SnapIO.X(nextHouseCheck))
            .Set("ais", ais.Select(a => (object)a.SaveState()).ToList());

        /// <summary>Replaces the running game with a snapshot (from Snapshot.Parse). Like Restart, views notice the new World.</summary>
        public void Restore(D root)
        {
            var world = World.LoadState(root.Obj("world"));
            var cfg = Snapshot.ReadConfig(root.Obj("config"));
            var g = root.Obj("game") ?? new D();
            Config = cfg;
            Speed = cfg.Speed; g.Load("speed", ref Speed);
            Paused = false; g.Load("paused", ref Paused);
            accumulator = 0; g.Load("accumulator", ref accumulator);
            nextHouseCheck = 0; g.Load("next_house_check", ref nextHouseCheck);
            ais.Clear();
            if (g.Has("ais", out _))
                foreach (var a in g.Objs("ais")) { var ai = SimpleAI.LoadState(a); if (ai != null && ai.Team < world.Teams.Count) ais.Add(ai); }
            else
                // An older snapshot without AI state: give every scripted seat still playing a fresh controller.
                foreach (var t in world.Teams.Where(t => t.Controller == "ai" && !t.Left && !t.Defeated)) ais.Add(new SimpleAI(t.Id, passive: cfg.Open));
            ResumedFrom = root.Str("saved_at");
            World = world;
        }
    }

    public partial class SimpleAI
    {
        internal JObj SaveState() => new JObj()
            .Set("team", team).Put("passive", Passive).Set("next_think", SnapIO.X(nextThink)).Set("wave_size", waveSize).Set("known_surface", knownSurface)
            .Set("next_prospect", SnapIO.X(nextProspect)).Set("prospect_radius", prospectRadius)
            .Set("outpost_targets", outpostTargets.Select(kv => (object)new List<object> { kv.Key, SnapIO.X(kv.Value.X), SnapIO.X(kv.Value.Y) }).ToList());

        internal static SimpleAI LoadState(D d)
        {
            if (!d.Has("team", out _)) return null;
            bool passive = false; d.Load("passive", ref passive);
            var ai = new SimpleAI(d.In("team"), passive);
            d.Load("next_think", ref ai.nextThink); d.Load("wave_size", ref ai.waveSize); d.Load("known_surface", ref ai.knownSurface);
            d.Load("next_prospect", ref ai.nextProspect); d.Load("prospect_radius", ref ai.prospectRadius);
            foreach (var x in d.Arr("outpost_targets"))
                if (x is List<object> l && l.Count >= 3) ai.outpostTargets[SnapIO.ToI(l[0])] = new Vec2(SnapIO.ToF(l[1]), SnapIO.ToF(l[2]));
            return ai;
        }
    }

    public partial class AlertLog
    {
        internal JObj SaveState(World w)
        {
            var openSet = new HashSet<Alert>(open);
            var keep = All.Where(a => openSet.Contains(a) || (w.Tick - a.LastTick) * World.Dt <= Snapshot.AlertKeepSeconds).ToList();
            return new JObj().Set("next_seq", nextSeq)
                .Set("alerts", keep.Select(a => (object)new JObj().Set("seq", a.Seq).Set("team", a.Team).Set("kind", a.Kind)
                    .PutEnum("priority", a.Priority, Priority.Medium).Set("pos", SnapIO.V(a.Pos)).Set("start_tick", a.StartTick).Set("last_tick", a.LastTick)
                    .Put("count", a.Count).Put("open", openSet.Contains(a))
                    .Set("victims", a.Victims.Cast<object>().ToList()).Set("attackers", a.Attackers.Cast<object>().ToList())
                    .Set("lost", a.Lost.Cast<object>().ToList())).ToList());
        }

        internal void LoadState(D d)
        {
            All.Clear(); open.Clear();
            if (d == null) return;
            foreach (var x in d.Objs("alerts"))
            {
                var a = new Alert();
                x.Load("seq", ref a.Seq); x.Load("team", ref a.Team); x.Load("kind", ref a.Kind); x.LoadEnum("priority", ref a.Priority);
                x.Load("pos", ref a.Pos); x.Load("start_tick", ref a.StartTick); x.Load("last_tick", ref a.LastTick); x.Load("count", ref a.Count);
                foreach (var v in x.Arr("victims")) if (v is double id) a.Victims.Add((int)id);
                foreach (var v in x.Arr("attackers")) if (v is double id) a.Attackers.Add((int)id);
                foreach (var v in x.Arr("lost")) if (v != null) a.Lost.Add(v.ToString());
                if (a.Kind == null) continue;
                All.Add(a);
                bool isOpen = false; x.Load("open", ref isOpen);
                if (isOpen) open.Add(a);
            }
            nextSeq = All.Count > 0 ? All.Max(a => a.Seq) + 1 : 1;
            d.Load("next_seq", ref nextSeq);
        }
    }

    public partial class Map
    {
        internal JObj SaveState() => new JObj()
            .Set("w", W).Set("h", H).Set("ore_scale", SnapIO.X(OreScale)).Set("next_deposit_id", nextDepositId)
            .Set("tiles", SnapIO.Pack(Tiles.Select(t => (byte)t).ToArray()))
            .Set("ore", SnapIO.PackInts(Ore)).Set("ore_type", SnapIO.Pack(OreType)).Set("occupant", SnapIO.PackInts(Occupant))
            .Set("spawns", SnapIO.Vs(Spawns))
            .Set("deep", Deep.Select(d => (object)new JObj().Set("id", d.Id).Set("pos", SnapIO.V(d.Pos)).Set("type", d.Type)
                .Set("amount", SnapIO.X(d.Amount)).Set("initial", SnapIO.X(d.Initial)).Put("mine", d.MineId)).ToList());

        internal static Map LoadState(D d)
        {
            if (d == null) throw new FormatException("snapshot has no map");
            int w = d.In("w"), h = d.In("h");
            if (w < 8 || h < 8 || w > 4096 || h > 4096) throw new FormatException($"bad map size {w}x{h}");
            var m = new Map(w, h);
            d.Load("ore_scale", ref m.OreScale);
            var tiles = SnapIO.Unpack(d.Str("tiles"));
            for (int i = 0; i < Math.Min(tiles.Length, m.Tiles.Length); i++) m.Tiles[i] = (Terrain)tiles[i];
            SnapIO.UnpackInts(d.Str("ore"), m.Ore);
            var types = SnapIO.Unpack(d.Str("ore_type"));
            Array.Copy(types, m.OreType, Math.Min(types.Length, m.OreType.Length));
            SnapIO.UnpackInts(d.Str("occupant"), m.Occupant);
            m.Spawns.AddRange(SnapIO.ToVecs(d.Arr("spawns")));
            foreach (var x in d.Objs("deep"))
            {
                var dep = new DeepDeposit();
                x.Load("id", ref dep.Id); x.Load("pos", ref dep.Pos); x.Load("type", ref dep.Type);
                x.Load("amount", ref dep.Amount); x.Load("initial", ref dep.Initial); x.Load("mine", ref dep.MineId);
                m.Deep.Add(dep);
            }
            m.nextDepositId = m.Deep.Count > 0 ? m.Deep.Max(x => x.Id) + 1 : 1;
            d.Load("next_deposit_id", ref m.nextDepositId);
            return m;
        }
    }

    public partial class World
    {
        /// <summary>For restoring: an empty world on a given map (no teams, nothing generated).</summary>
        World(Map map) { Map = map; Paths = new Pathfinder(map); }

        internal JObj SaveState()
        {
            var o = new JObj()
                .Set("tick", Tick).Set("game_id", GameId).Put("game_over", GameOver).Put("winner", Winner, -1).Set("map_version", MapVersion)
                .Put("open", Open).Set("max_players", MaxPlayers).Set("max_map_size", MaxMapSize).Set("grow_step", GrowStep)
                .Set("safe_join_distance", SnapIO.X(SafeJoinDistance)).Set("protection_seconds", SnapIO.X(ProtectionSeconds)).Set("stall_grace", SnapIO.X(StallGrace))
                .Set("next_id", nextId).Set("next_seq", nextSeq).Set("seat_counter", seatCounter).Set("rng", rng.State.ToString("x16"))
                .Put("errors", Errors).Put("last_error", LastError).Put("arena_champion", ArenaChampion).Put("contested", contested)
                .Set("event_counts", EventCounts.Aggregate(new JObj(), (j, kv) => j.Set(kv.Key, kv.Value)))
                .Set("rate_snapshot", rateSnapshot.Select(kv => (object)new JObj().Set("team", kv.Key).Set("stock", SnapIO.Floats(kv.Value))).ToList())
                .Set("air_warned", airWarned.Select(kv => (object)new List<object> { kv.Key.team, kv.Key.id, SnapIO.X(kv.Value) }).ToList())
                .Set("map", Map.SaveState())
                .Set("inventions", Inventions.Values.Select(i => (object)Snapshot.WriteInvention(i)).ToList())
                .Set("teams", Teams.Select(t => (object)Snapshot.WriteTeam(t)).ToList())
                .Set("entities", Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList())
                .Set("projectiles", Projectiles.Select(p => (object)Snapshot.WriteProjectile(this, p)).ToList())
                .Set("alerts", Alerts.SaveState(this))
                .Set("events", Snapshot.WriteEvents(this));
            return o;
        }

        internal static World LoadState(D d)
        {
            if (d == null) throw new FormatException("snapshot has no world");
            var w = new World(Map.LoadState(d.Obj("map")));
            // Production queues, entities and projectiles below resolve unit keys with Defs.Get, which sees the current
            // world's inventions: make this world current while it loads, and hand the hook back if the load fails.
            var previous = Defs.Invented;
            w.MakeCurrent();
            try { w.LoadInto(d); }
            catch { Defs.Invented = previous; throw; }
            return w;
        }

        void LoadInto(D d)
        {
            var w = this;
            d.Load("tick", ref w.Tick); d.Load("game_id", ref w.GameId); d.Load("game_over", ref w.GameOver); d.Load("winner", ref w.Winner); d.Load("map_version", ref w.MapVersion);
            d.Load("open", ref w.Open); d.Load("max_players", ref w.MaxPlayers); d.Load("max_map_size", ref w.MaxMapSize); d.Load("grow_step", ref w.GrowStep);
            d.Load("safe_join_distance", ref w.SafeJoinDistance); d.Load("protection_seconds", ref w.ProtectionSeconds); d.Load("stall_grace", ref w.StallGrace);
            d.Load("errors", ref w.Errors); d.Load("last_error", ref w.LastError);
            d.Load("arena_champion", ref w.ArenaChampion); d.Load("contested", ref w.contested);

            foreach (var id in d.Objs("inventions")) { var inv = Snapshot.ReadInvention(id); if (inv != null) w.Inventions[inv.Key] = inv; }

            bool rebuildPower = false, rebuildVision = false;
            int i = 0;
            foreach (var td in d.Objs("teams"))
            {
                var t = Snapshot.ReadTeam(td, i++, w.Map);
                rebuildPower |= !td.ContainsKey("power_produced");
                rebuildVision |= !td.ContainsKey("visible");
                w.Teams.Add(t);
            }
            if (w.Teams.Count == 0) throw new FormatException("snapshot has no teams");
            foreach (var k in w.Inventions.Where(kv => kv.Value.Team < 0 || kv.Value.Team >= w.Teams.Count).Select(kv => kv.Key).ToList()) w.Inventions.Remove(k);
            int dropped = 0;
            foreach (var ed in d.Objs("entities"))
            {
                var e = Snapshot.ReadEntity(ed);
                if (e == null || e.Team < -1 || e.Team >= w.Teams.Count) { dropped++; continue; }
                w.Entities.Add(e);
                w.ById[e.Id] = e;
            }
            if (dropped > 0) w.LastError = $"resumed without {dropped} entities of types this build doesn't have";
            foreach (var pd in d.Objs("projectiles")) { var p = Snapshot.ReadProjectile(pd); if (p != null) w.Projectiles.Add(p); }
            w.Alerts.LoadState(d.Obj("alerts"));
            foreach (var ev in d.Objs("events")) { var e = Snapshot.ReadEvent(ev); if (e != null) w.Events.Add(e); }
            var counts = d.Obj("event_counts");
            if (counts != null) foreach (var kv in counts) if (kv.Value is double n) w.EventCounts[kv.Key] = (int)n;
            foreach (var x in d.Objs("rate_snapshot"))
            {
                var stock = new Dictionary<string, float>();
                SnapIO.LoadFloats(x.Obj("stock"), stock);
                w.rateSnapshot[x.In("team")] = stock;
            }
            foreach (var x in d.Arr("air_warned"))
                if (x is List<object> l && l.Count >= 3) w.airWarned[(SnapIO.ToI(l[0]), SnapIO.ToI(l[1]))] = SnapIO.ToF(l[2]);

            // Counters: from the snapshot, or past everything in it (never reuse an id or a sequence number).
            w.nextId = 1 + Math.Max(w.Entities.Select(e => e.Id).DefaultIfEmpty(0).Max(), w.Projectiles.Select(p => p.Id).DefaultIfEmpty(0).Max());
            d.Load("next_id", ref w.nextId);
            w.nextSeq = w.Events.Count > 0 ? w.Events[w.Events.Count - 1].Seq + 1 : 1;
            d.Load("next_seq", ref w.nextSeq);
            w.seatCounter = w.Teams.Max(t => t.Seat);
            d.Load("seat_counter", ref w.seatCounter);
            if (d.Str("rng") is string r && ulong.TryParse(r, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var state)) w.rng.State = state;

            if (rebuildPower) w.UpdatePower();
            if (rebuildVision) w.UpdateVisibility();
        }
    }

    /// <summary>Serializable random numbers (SplitMix64), so a resumed game draws the same sequence it would have.</summary>
    public class SimRng
    {
        public ulong State;
        public SimRng(ulong seed) { State = seed; }

        public ulong NextULong()
        {
            ulong z = State += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        /// <summary>Uniform in [min, max).</summary>
        public int Next(int min, int max) => max <= min ? min : min + (int)(NextULong() % (ulong)(max - min));
    }

    /// <summary>Reading and writing helpers for snapshots: exact floats, defaults left out, compact packed arrays.</summary>
    static class SnapIO
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>A float written so it reads back bit-for-bit (through Json.Parse's double and a cast).</summary>
        public static object X(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return v.ToString(Inv); // "NaN", "Infinity", "-Infinity" as strings
            var s = v.ToString("R", Inv);
            if ((float)double.Parse(s, Inv) != v) s = ((double)v).ToString("R", Inv);
            return new JExact(s);
        }

        public static float ToF(object v) => v switch
        {
            double x => (float)x,
            string s when float.TryParse(s, NumberStyles.Float, Inv, out var f) => f,
            bool b => b ? 1f : 0f,
            _ => 0f,
        };

        public static int ToI(object v) => v is double x ? (int)x : 0;

        public static List<object> V(Vec2 v) => new List<object> { X(v.X), X(v.Y) };

        /// <summary>Points as one flat list [x0, y0, x1, y1, ...].</summary>
        public static List<object> Vs(IEnumerable<Vec2> vs)
        {
            var l = new List<object>();
            foreach (var v in vs) { l.Add(X(v.X)); l.Add(X(v.Y)); }
            return l;
        }

        public static List<Vec2> ToVecs(List<object> flat)
        {
            var r = new List<Vec2>(flat.Count / 2);
            for (int i = 0; i + 1 < flat.Count; i += 2) r.Add(new Vec2(ToF(flat[i]), ToF(flat[i + 1])));
            return r;
        }

        public static JObj Floats(Dictionary<string, float> d)
        {
            var o = new JObj();
            foreach (var kv in d) o.Set(kv.Key, X(kv.Value));
            return o;
        }

        public static JObj Ints(Dictionary<string, int> d) => d.Aggregate(new JObj(), (j, kv) => j.Set(kv.Key, kv.Value));

        public static void LoadInts(D d, Dictionary<string, int> into)
        {
            if (d == null) return;
            into.Clear();
            foreach (var kv in d) if (kv.Value is double x) into[kv.Key] = (int)x;
        }

        public static void LoadFloats(D d, Dictionary<string, float> into)
        {
            if (d == null) return;
            foreach (var kv in d) if (kv.Value != null) into[kv.Key] = ToF(kv.Value);
        }

        // ---- writing, leaving out defaults

        public static JObj Put(this JObj o, string k, float v, float def = 0f) { if (!v.Equals(def)) o[k] = X(v); return o; }
        public static JObj Put(this JObj o, string k, int v, int def = 0) { if (v != def) o[k] = v; return o; }
        public static JObj Put(this JObj o, string k, bool v) { if (v) o[k] = true; return o; }
        public static JObj Put(this JObj o, string k, string v) { if (v != null) o[k] = v; return o; }
        public static JObj PutV(this JObj o, string k, Vec2 v) { if (v.X != 0 || v.Y != 0) o[k] = V(v); return o; }
        public static JObj PutEnum<T>(this JObj o, string k, T v, T def) where T : struct, Enum { if (!v.Equals(def)) o[k] = v.ToString(); return o; }

        // ---- reading: a missing (or null) field leaves the value as it was

        public static bool Has(this D d, string k, out object v) => d.TryGetValue(k, out v) && v != null;
        public static D Obj(this D d, string k) => d != null && d.TryGetValue(k, out var v) ? v as D : null;
        public static List<object> Arr(this D d, string k) => d != null && d.TryGetValue(k, out var v) && v is List<object> l ? l : new List<object>();
        public static IEnumerable<D> Objs(this D d, string k) => d.Arr(k).OfType<D>();
        public static int In(this D d, string k, int def = 0) => d.Has(k, out var v) && v is double x ? (int)x : def;

        public static void Load(this D d, string k, ref float f) { if (d.Has(k, out var v)) f = ToF(v); }
        public static void Load(this D d, string k, ref int i) { if (d.Has(k, out var v) && v is double x) i = (int)x; }
        public static void Load(this D d, string k, ref long l) { if (d.Has(k, out var v) && v is double x) l = (long)x; }
        public static void Load(this D d, string k, ref bool b) { if (d.Has(k, out var v) && v is bool x) b = x; }
        public static void Load(this D d, string k, ref string s) { if (d.Has(k, out var v)) s = v.ToString(); }
        public static void Load(this D d, string k, ref Vec2 p) { if (d.Has(k, out var v) && v is List<object> l && l.Count >= 2) p = new Vec2(ToF(l[0]), ToF(l[1])); }
        public static void LoadEnum<T>(this D d, string k, ref T e) where T : struct, Enum
        {
            if (d.Has(k, out var v) && Enum.TryParse<T>(v.ToString(), out var x) && Enum.IsDefined(typeof(T), x)) e = x;
        }

        // ---- packed arrays: raw bytes, deflated, base64

        public static string Pack(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
            return Convert.ToBase64String(ms.ToArray());
        }

        public static byte[] Unpack(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[0];
            using var src = new MemoryStream(Convert.FromBase64String(s));
            using var z = new DeflateStream(src, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }

        public static string PackInts(int[] a)
        {
            var b = new byte[a.Length * 4];
            for (int i = 0; i < a.Length; i++) { int v = a[i]; b[i * 4] = (byte)v; b[i * 4 + 1] = (byte)(v >> 8); b[i * 4 + 2] = (byte)(v >> 16); b[i * 4 + 3] = (byte)(v >> 24); }
            return Pack(b);
        }

        public static void UnpackInts(string s, int[] into)
        {
            var b = Unpack(s);
            for (int i = 0; i < into.Length && i * 4 + 3 < b.Length; i++) into[i] = b[i * 4] | b[i * 4 + 1] << 8 | b[i * 4 + 2] << 16 | b[i * 4 + 3] << 24;
        }

        public static string PackBits(bool[] a)
        {
            var b = new byte[(a.Length + 7) / 8];
            for (int i = 0; i < a.Length; i++) if (a[i]) b[i >> 3] |= (byte)(1 << (i & 7));
            return Pack(b);
        }

        public static bool[] UnpackBits(string s, int length)
        {
            var r = new bool[length];
            var b = Unpack(s);
            for (int i = 0; i < length && (i >> 3) < b.Length; i++) r[i] = (b[i >> 3] & (1 << (i & 7))) != 0;
            return r;
        }
    }
}
