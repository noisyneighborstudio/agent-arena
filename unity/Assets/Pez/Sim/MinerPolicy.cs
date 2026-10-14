using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>Only agent-supplied destinations and delays execute. No policy is enabled in a fresh team.</summary>
    public class MinerPolicy
    {
        public bool WithdrawOnAttack;
        public bool ToBase;
        public Vec2 Destination;
        public float ResumeAfterS;
        public readonly List<MinerField> Fields = new List<MinerField>();
        public readonly HashSet<int> Detached = new HashSet<int>();

        public JObj View() => new JObj().Set("withdraw_on_attack", WithdrawOnAttack)
            .Set("to", ToBase ? (object)"base" : SnapIO.V(Destination)).Set("resume_after_s", ResumeAfterS)
            .Set("withdrawn_fields", Fields.Where(f => f.Withdrawn).Select(f => (object)new JObj()
                .Set("field", SnapIO.V(f.Root.Center)).Set("units", f.Trucks.Keys.Cast<object>().ToList())
                .Set("withdrawn_at_s", f.LastWithdrawal).Set("clear_since_s", f.ClearSince >= 0 ? (object)f.ClearSince : null)).ToList());

        static List<object> Tile(Int2 t) => new List<object> { t.X, t.Y };

        internal JObj Save() => new JObj().Set("withdraw_on_attack", WithdrawOnAttack).Set("to_base", ToBase)
            .Set("destination", SnapIO.V(Destination)).Set("resume_after_s", SnapIO.X(ResumeAfterS))
            .Set("detached", Detached.Cast<object>().ToList())
            .Set("fields", Fields.Select(f => (object)new JObj().Set("root", Tile(f.Root))
                .Set("tiles", f.Tiles.Select(t => (object)Tile(t)).ToList())
                .Set("last_withdrawal", SnapIO.X(f.LastWithdrawal)).Set("clear_since", SnapIO.X(f.ClearSince)).Set("withdrawn", f.Withdrawn)
                .Set("trucks", f.Trucks.Select(kv => (object)new JObj().Set("id", kv.Key).Set("target", SnapIO.V(kv.Value.Target))
                    .Set("withdrawal_target", SnapIO.V(kv.Value.WithdrawalTarget)).Set("harvest_type", kv.Value.HarvestType)
                    .Set("harvest_tile", kv.Value.HarvestTile.HasValue ? (object)Tile(kv.Value.HarvestTile.Value) : null)).ToList())
                .Set("threats", f.Threats.Select(kv => (object)new JObj().Set("id", kv.Key).Set("position", SnapIO.V(kv.Value.Position))
                    .Set("range", SnapIO.X(kv.Value.Range)).Set("stealth", kv.Value.Stealth)).ToList())).ToList());

        internal void Load(Dictionary<string, object> d)
        {
            if (d == null) return;
            d.Load("withdraw_on_attack", ref WithdrawOnAttack); d.Load("to_base", ref ToBase);
            d.Load("destination", ref Destination); d.Load("resume_after_s", ref ResumeAfterS);
            foreach (var id in d.Arr("detached")) Detached.Add(SnapIO.ToI(id));
            foreach (var fd in d.Objs("fields"))
            {
                var f = new MinerField();
                var root = fd.Arr("root"); if (root.Count == 2) f.Root = new Int2(SnapIO.ToI(root[0]), SnapIO.ToI(root[1]));
                fd.Load("last_withdrawal", ref f.LastWithdrawal);
                fd.Load("clear_since", ref f.ClearSince); fd.Load("withdrawn", ref f.Withdrawn);
                foreach (var tile in fd.Arr("tiles").OfType<List<object>>())
                    if (tile.Count == 2) f.Tiles.Add(new Int2(SnapIO.ToI(tile[0]), SnapIO.ToI(tile[1])));
                foreach (var td in fd.Objs("trucks"))
                {
                    var r = new MinerResume(); td.Load("target", ref r.Target); td.Load("withdrawal_target", ref r.WithdrawalTarget); td.Load("harvest_type", ref r.HarvestType);
                    var tile = td.Arr("harvest_tile");
                    if (tile.Count == 2) r.HarvestTile = new Int2(SnapIO.ToI(tile[0]), SnapIO.ToI(tile[1]));
                    f.Trucks[td.In("id")] = r;
                }
                foreach (var td in fd.Objs("threats"))
                {
                    var threat = new MinerThreat(); td.Load("position", ref threat.Position);
                    td.Load("range", ref threat.Range); td.Load("stealth", ref threat.Stealth);
                    f.Threats[td.In("id")] = threat;
                }
                Fields.Add(f);
            }
        }
    }

    public class MinerField
    {
        public Int2 Root;
        public readonly List<Int2> Tiles = new List<Int2>();
        public bool Withdrawn;
        public float LastWithdrawal = -999;
        public float ClearSince = -1;
        public readonly Dictionary<int, MinerResume> Trucks = new Dictionary<int, MinerResume>();
        public readonly Dictionary<int, MinerThreat> Threats = new Dictionary<int, MinerThreat>();
    }

    public class MinerResume
    {
        public Vec2 Target;
        public Vec2 WithdrawalTarget;
        public int HarvestType = -1;
        public Int2? HarvestTile;
    }

    public class MinerThreat
    {
        public Vec2 Position;
        public float Range;
        public bool Stealth;
    }

    public static class MinerPolicies
    {
        public const float WithdrawalCooldown = 20f, ExitMargin = 1f;

        public static JObj Configure(World w, int team, Dictionary<string, object> c)
        {
            var miners = c.Obj("miners");
            if (miners == null || !miners.TryGetValue("withdraw_on_attack", out var raw) || !(raw is bool enabled))
                return new JObj().Set("ok", false).Set("error", "miners.withdraw_on_attack must be true or false");
            var p = w.Teams[team].Miners;
            if (!enabled)
            {
                p.WithdrawOnAttack = false;
                foreach (var f in p.Fields) { f.Withdrawn = false; f.Trucks.Clear(); f.Threats.Clear(); f.ClearSince = -1; }
                p.Detached.Clear();
                return new JObj().Set("ok", true).Set("result", "miner protection disabled; existing orders stay in place");
            }
            bool toBase = miners.Str("to") == "base";
            var to = miners.Arr("to");
            float x = to.Count == 2 && to[0] is double dx ? (float)dx : float.NaN;
            float y = to.Count == 2 && to[1] is double dy ? (float)dy : float.NaN;
            float delay = miners.Num("resume_after_s");
            if ((!toBase && (!Finite(x) || !Finite(y) || x < 0 || y < 0 || x >= w.Map.W || y >= w.Map.H)) || !Finite(delay) || delay < 0)
                return new JObj().Set("ok", false).Set("error", "enabling miner protection requires to:[x,y] inside the map or to:\"base\", and a finite nonnegative resume_after_s");
            p.ToBase = toBase; p.Destination = toBase ? w.Teams[team].StartPos : new Vec2(x, y);
            p.ResumeAfterS = delay; p.WithdrawOnAttack = true;
            return new JObj().Set("ok", true).Set("result", "miner protection enabled for harvesting trucks; explicit unit orders take priority");
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static bool Mining(Entity e) => e.IsHarvester && !e.Dead && !e.IsCarried &&
            (e.Order == Order.Harvest || e.Order == Order.ReturnOre ||
             (e.Order == Order.Refuel && (e.ResumeOrder == Order.Harvest || e.ResumeOrder == Order.ReturnOre)));

        static Int2 Assignment(Entity e) => e.HarvestTile ?? Int2.Of(e.Order == Order.Refuel ? e.ResumePos : e.OrderPos);

        static MinerField Field(World w, Team team, Entity truck)
        {
            var tile = Assignment(truck);
            var root = w.Map.FieldRoot(tile);
            if (!root.HasValue) return null; // salvage is not a persistent mining field
            var f = team.Miners.Fields.FirstOrDefault(field => w.Map.FieldRoot(field.Root).Equals(root));
            // Root is the first own miner's known assignment. The map's canonical root is scratch grouping only:
            // that root may lie in unexplored ground and must never enter public facts or a player's saved copy.
            if (f == null) { f = new MinerField { Root = tile }; team.Miners.Fields.Add(f); }
            // Store only field tiles the team has explored. No unseen geography goes into its policy state or facts.
            w.Map.FieldIndex(out var tiles, out _);
            foreach (int i in tiles)
                if (team.Explored[i])
                {
                    var at = new Int2(i % w.Map.W, i / w.Map.W);
                    if (w.Map.FieldRoot(at).Equals(root) && !f.Tiles.Contains(at)) f.Tiles.Add(at);
                }
            if (!f.Tiles.Contains(tile)) f.Tiles.Add(tile); // its own truck's assignment is known
            return f;
        }

        static List<Entity> Trucks(World w, Team team, MinerField f) => w.Owned(team.Id)
            .Where(e => Mining(e) && !team.Miners.Detached.Contains(e.Id) && w.Map.FieldRoot(Assignment(e)).Equals(w.Map.FieldRoot(f.Root)))
            .OrderBy(e => e.Id).ToList();

        static bool Visible(World w, Team team, Vec2 at)
        {
            var t = Int2.Of(at);
            return w.Map.InBounds(t.X, t.Y) && team.Visible[w.Map.Idx(t.X, t.Y)];
        }

        static bool NearField(MinerField f, Vec2 at, float range) => f.Tiles.Any(t => Vec2.Dist(t.Center, at) <= range + ExitMargin);

        static IEnumerable<Entity> SeenThreats(World w, int team) => w.Entities.Where(e => !e.Dead && e.Team >= 0 &&
            e.Team != team && !e.IsStructure && !e.IsMine && e.IsArmed && e.Def.Weapon.CanHit(Defs.Get("mining_truck")) && w.IsVisibleTo(team, e));

        static void Remember(MinerField field, Entity enemy) => field.Threats[enemy.Id] = new MinerThreat
            { Position = enemy.Pos, Range = enemy.Def.Weapon.Range, Stealth = enemy.Def.Stealth };

        public static void Hit(World w, Entity truck)
        {
            if (truck.Team < 0 || !Mining(truck)) return;
            var team = w.Teams[truck.Team];
            if (!team.Miners.WithdrawOnAttack || team.Miners.Detached.Contains(truck.Id)) return;
            var field = Field(w, team, truck);
            if (field == null) return;
            Withdraw(w, team, field, "hit", truck.Id);
        }

        public static bool Controls(World w, Entity truck) => truck.Team >= 0 && w.Teams[truck.Team].Miners.WithdrawOnAttack &&
            w.Teams[truck.Team].Miners.Fields.Any(f => f.Withdrawn && f.Trucks.ContainsKey(truck.Id));

        /// <summary>Capture a witnessed disappearance immediately, before snapshots discard the feed's transient SeenBy.</summary>
        internal static void WitnessGone(World w, Entity gone)
        {
            foreach (var team in w.Teams)
                if (team.Miners.WithdrawOnAttack && team.Id != gone.Team && w.IsVisibleTo(team.Id, gone))
                    foreach (var field in team.Miners.Fields) field.Threats.Remove(gone.Id);
        }

        public static void DirectOrder(World w, Entity truck, Order order)
        {
            var p = w.Teams[truck.Team].Miners;
            if (!p.WithdrawOnAttack) return;
            bool controlled = p.Fields.Any(f => f.Trucks.ContainsKey(truck.Id));
            foreach (var field in p.Fields) field.Trucks.Remove(truck.Id);
            if (order == Order.Harvest) p.Detached.Remove(truck.Id); else p.Detached.Add(truck.Id);
            if (controlled && !w.Mirror) w.Observe.Note(truck.Team, "orders", "detached_by_order", Sev.Notice, "own",
                new JObj().Set("unit", truck.Id).Set("source", "miner_policy").Set("order", order.ToString().ToLowerInvariant()),
                $"mining truck #{truck.Id} detached from miner protection by a direct order");
        }

        public static void Step(World w, int id)
        {
            var team = w.Teams[id]; var p = team.Miners;
            if (!p.WithdrawOnAttack || team.Defeated || team.Left || w.Tick % 4 != 0) return;
            p.Detached.RemoveWhere(key => w.Get(key) == null || w.Get(key).Dead);
            foreach (var truck in w.Owned(id).Where(e => Mining(e) && !p.Detached.Contains(e.Id)).ToList()) Field(w, team, truck);
            var seen = SeenThreats(w, id).ToList();
            foreach (var field in p.Fields)
            {
                foreach (int dead in field.Trucks.Keys.Where(key => w.Get(key) == null || w.Get(key).Dead).ToList()) field.Trucks.Remove(dead);
                if (!field.Withdrawn)
                {
                    var working = Trucks(w, team, field);
                    var threat = seen.FirstOrDefault(e => working.Any(truck => e.Def.Weapon.CanHit(truck.Def) && Vec2.Dist(e.Pos, truck.Pos) <= e.Def.Weapon.Range));
                    if (threat != null) { Remember(field, threat); Withdraw(w, team, field, "seen_weapon_range", 0); }
                    continue;
                }
                var newcomers = Trucks(w, team, field).Where(truck => !field.Trucks.ContainsKey(truck.Id)).ToList();
                if (newcomers.Count > 0)
                {
                    Enroll(w, team, field, newcomers);
                    Note(w, team, field, "miners_withdrawn", "field_already_withdrawn", newcomers.Select(truck => (object)truck.Id).ToList(), 0);
                }
                // Visible threats have an exit band around the ore field, not the withdrawing truck's changing position.
                foreach (var enemy in seen.Where(e => NearField(field, e.Pos, e.Def.Weapon.Range))) Remember(field, enemy);
                bool threatKnown = false;
                foreach (var pair in field.Threats.ToList())
                {
                    var visibleEnemy = seen.FirstOrDefault(e => e.Id == pair.Key);
                    if (visibleEnemy != null)
                    {
                        if (!NearField(field, visibleEnemy.Pos, visibleEnemy.Def.Weapon.Range)) field.Threats.Remove(pair.Key);
                        else { Remember(field, visibleEnemy); threatKnown = true; }
                    }
                    else
                    {
                        // A missing contact is cleared only when the team sees its last spot empty. A stealth contact
                        // cannot be disproved by terrain visibility; it must be seen leave or seen destroyed.
                        if (!pair.Value.Stealth && Visible(w, team, pair.Value.Position)) field.Threats.Remove(pair.Key);
                        else threatKnown = true;
                    }
                }
                bool clear = !threatKnown && field.Tiles.Count > 0 && field.Tiles.All(tile => Visible(w, team, tile.Center));
                if (!clear) { field.ClearSince = -1; continue; }
                if (field.ClearSince < 0) field.ClearSince = w.Time;
                if (w.Time - field.ClearSince < p.ResumeAfterS || w.Time - field.LastWithdrawal < WithdrawalCooldown) continue;
                var resumed = new List<object>();
                foreach (var pair in field.Trucks)
                {
                    var truck = w.Get(pair.Key);
                    if (truck == null || truck.Dead || p.Detached.Contains(truck.Id)) continue;
                    var r = pair.Value;
                    w.SetOrder(truck, Order.Harvest, r.Target);
                    truck.LastOrderBy = "miner_policy";
                    truck.HarvestType = r.HarvestType; truck.HarvestTile = r.HarvestTile;
                    resumed.Add(truck.Id);
                }
                field.Withdrawn = false; field.Trucks.Clear(); field.Threats.Clear(); field.ClearSince = -1;
                if (resumed.Count > 0) Note(w, team, field, "miners_resumed", "field_visibly_clear", resumed, 0);
            }
        }

        static void Withdraw(World w, Team team, MinerField field, string reason, int hitTruck)
        {
            if (field.Withdrawn || w.Time - field.LastWithdrawal < WithdrawalCooldown) return;
            var trucks = Trucks(w, team, field);
            if (trucks.Count == 0) return;
            foreach (var enemy in SeenThreats(w, team.Id).Where(e => NearField(field, e.Pos, e.Def.Weapon.Range))) Remember(field, enemy);
            field.Withdrawn = true; field.LastWithdrawal = w.Time; field.ClearSince = -1;
            Enroll(w, team, field, trucks);
            Note(w, team, field, "miners_withdrawn", reason, trucks.Select(t => (object)t.Id).ToList(), hitTruck);
        }

        static void Enroll(World w, Team team, MinerField field, List<Entity> trucks)
        {
            var target = team.Miners.ToBase ? team.StartPos : team.Miners.Destination;
            float gap = MathF.Max(1.2f, trucks.Max(e => e.Def.Radius * 2f + 0.1f));
            var reserved = team.Miners.Fields.Where(f => f.Withdrawn).SelectMany(f => f.Trucks.Values).Select(r => r.WithdrawalTarget);
            var slots = Commands.FormationSlots(w, trucks, target, gap, reserved);
            foreach (var truck in trucks)
            {
                field.Trucks[truck.Id] = new MinerResume { Target = truck.HarvestTile?.Center ?? truck.OrderPos,
                    HarvestTile = truck.HarvestTile, HarvestType = truck.HarvestType, WithdrawalTarget = slots[truck] };
                w.SetOrder(truck, Order.Move, slots[truck]);
                truck.LastOrderBy = "miner_policy"; truck.LastRetreatTick = w.Tick;
            }
        }

        static void Note(World w, Team team, MinerField field, string kind, string reason, List<object> trucks, int hitTruck)
        {
            w.Emit(kind, team.Id, pos: field.Root.Center, text: reason);
            if (w.Mirror) return;
            var facts = new JObj().Set("policy", "miners.withdraw_on_attack").Set("reason", reason).Set("field", SnapIO.V(field.Root.Center))
                .Set("units", trucks).Set("triggered_at_s", w.Time).Set("to", team.Miners.ToBase ? (object)"base" : SnapIO.V(team.Miners.Destination))
                .Set("resume_after_s", team.Miners.ResumeAfterS).Set("threats_seen", field.Threats.Keys.Cast<object>().ToList());
            if (hitTruck != 0) facts.Set("hit_truck", hitTruck);
            w.Observe.Note(team.Id, "orders", kind, Sev.Notice, "own", facts, $"{kind}: {trucks.Count} mining truck(s), {reason}");
        }
    }
}
