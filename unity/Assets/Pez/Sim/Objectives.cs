using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public class Objective
    {
        public string Id, Squad, Verb, PauseReason, Packet;
        public Vec2 At, AssemblyAt, Anchor, Centre;
        public float Radius, PhaseAt, LastAnchorAt = -999;
        public int Version = 1, AssemblyMin, TargetId, PatrolIndex, CommandTick;
        public string TargetSquad;
        public bool HasAssembly, Assembled, Paused, Done, HasHadMembers;
        public readonly List<Vec2> Waypoints = new List<Vec2>();
        public readonly List<string> Wake = new List<string>();
        public readonly Dictionary<int, ObjectiveMember> Members = new Dictionary<int, ObjectiveMember>();
        public readonly HashSet<int> Detached = new HashSet<int>();
        public readonly Dictionary<int, float> BaselineHp = new Dictionary<int, float>();
        public readonly HashSet<int> BaselineMembers = new HashSet<int>();
        public readonly Dictionary<string, int> BaselineSpent = new Dictionary<string, int>();
        public readonly Dictionary<int, ObjectiveThreat> Threats = new Dictionary<int, ObjectiveThreat>();
        public ReinforcementPlan Reinforce;
        public string Phase => Paused ? "paused" : HasAssembly && !Assembled ? "assembling" : Verb == "go" && Done ? "holding" : Verb;

        public JObj View() => new JObj().Set("id", Id).Set("squad", Squad).Set("verb", Verb).Set("version", Version)
            .Set("at", SnapIO.V(At)).Set("radius", Radius).Set("phase", Phase).Set("paused", Paused).Set("reason", PauseReason)
            .Set("assemble", HasAssembly ? (object)new JObj().Set("at", SnapIO.V(AssemblyAt)).Set("min", AssemblyMin).Set("released", Assembled) : null)
            .Set("target_id", TargetId != 0 ? (object)TargetId : null).Set("target_squad", TargetSquad)
            .Set("waypoints", Waypoints.Select(p => (object)SnapIO.V(p)).ToList()).Set("patrol_index", PatrolIndex)
            .Set("anchor", SnapIO.V(Anchor)).Set("centre", SnapIO.V(Centre)).Set("wake", Wake.Cast<object>().ToList()).Set("done", Done)
            .Set("members", Members.Keys.OrderBy(k => k).Cast<object>().ToList()).Set("detached", Detached.OrderBy(k => k).Cast<object>().ToList())
            .Set("reinforce", Reinforce?.View()).Set("decision", Packet == null ? null : Json.Parse(Packet));

        internal JObj Save() => View().Set("at", SnapIO.V(At)).Set("anchor", SnapIO.V(Anchor)).Set("radius", SnapIO.X(Radius))
            .Set("has_assembly", HasAssembly).Set("assembly_at", SnapIO.V(AssemblyAt)).Set("assembly_min", AssemblyMin).Set("assembled", Assembled)
            .Set("phase_at", SnapIO.X(PhaseAt)).Set("last_anchor_at", SnapIO.X(LastAnchorAt)).Set("command_tick", CommandTick)
            .Set("has_had_members", HasHadMembers).Set("packet", Packet).Set("reinforce", Reinforce?.Save())
            .Set("members", Members.OrderBy(kv => kv.Key).Select(kv => (object)kv.Value.Save().Set("id", kv.Key)).ToList())
            .Set("baseline_hp", BaselineHp.OrderBy(kv => kv.Key).Select(kv => (object)new List<object> { kv.Key, SnapIO.X(kv.Value) }).ToList())
            .Set("baseline_members", BaselineMembers.OrderBy(k => k).Cast<object>().ToList()).Set("baseline_spent", SnapIO.Ints(BaselineSpent))
            .Set("threats", Threats.Values.OrderBy(t => t.Id).Select(t => (object)t.Save()).ToList());

        internal static Objective Load(Dictionary<string, object> d)
        {
            var o = new Objective();
            d.Load("id", ref o.Id); d.Load("squad", ref o.Squad); d.Load("verb", ref o.Verb); d.Load("reason", ref o.PauseReason);
            d.Load("version", ref o.Version); d.Load("at", ref o.At); d.Load("anchor", ref o.Anchor); d.Load("centre", ref o.Centre); d.Load("radius", ref o.Radius);
            d.Load("has_assembly", ref o.HasAssembly); d.Load("assembly_at", ref o.AssemblyAt); d.Load("assembly_min", ref o.AssemblyMin); d.Load("assembled", ref o.Assembled);
            d.Load("target_id", ref o.TargetId); d.Load("target_squad", ref o.TargetSquad); d.Load("patrol_index", ref o.PatrolIndex);
            d.Load("paused", ref o.Paused); d.Load("done", ref o.Done); d.Load("has_had_members", ref o.HasHadMembers);
            d.Load("phase_at", ref o.PhaseAt); d.Load("last_anchor_at", ref o.LastAnchorAt); d.Load("command_tick", ref o.CommandTick); d.Load("packet", ref o.Packet);
            foreach (var p in d.Arr("waypoints").OfType<List<object>>()) if (p.Count == 2) o.Waypoints.Add(new Vec2(SnapIO.ToF(p[0]), SnapIO.ToF(p[1])));
            o.Wake.AddRange(d.Arr("wake").OfType<string>());
            foreach (var m in d.Objs("members")) o.Members[m.In("id")] = ObjectiveMember.Load(m);
            foreach (var id in d.Arr("detached")) o.Detached.Add(SnapIO.ToI(id));
            foreach (var pair in d.Arr("baseline_hp").OfType<List<object>>()) if (pair.Count == 2) o.BaselineHp[SnapIO.ToI(pair[0])] = SnapIO.ToF(pair[1]);
            foreach (var id in d.Arr("baseline_members")) o.BaselineMembers.Add(SnapIO.ToI(id));
            SnapIO.LoadInts(d.Obj("baseline_spent"), o.BaselineSpent);
            foreach (var t in d.Objs("threats")) { var threat = ObjectiveThreat.Load(t); o.Threats[threat.Id] = threat; }
            if (d.Obj("reinforce") is Dictionary<string, object> r) o.Reinforce = ReinforcementPlan.Load(r);
            return o;
        }
    }

    public class ObjectiveMember
    {
        public Vec2 Goal;
        public bool Assigned, Holding, Arrived, Returning;
        public float GoalAt = -999;
        public int IntentRevision;
        public GuardContinuation Intent;
        internal JObj Save() => new JObj().Set("goal", SnapIO.V(Goal)).Set("assigned", Assigned).Set("holding", Holding).Set("arrived", Arrived)
            .Set("returning", Returning).Set("goal_at", SnapIO.X(GoalAt)).Set("intent_revision", IntentRevision).Set("intent", Intent?.Save());
        internal static ObjectiveMember Load(Dictionary<string, object> d)
        {
            var m = new ObjectiveMember(); d.Load("goal", ref m.Goal); d.Load("assigned", ref m.Assigned); d.Load("holding", ref m.Holding); d.Load("arrived", ref m.Arrived);
            d.Load("returning", ref m.Returning);
            d.Load("goal_at", ref m.GoalAt); d.Load("intent_revision", ref m.IntentRevision); if (d.Obj("intent") is Dictionary<string, object> c) m.Intent = GuardContinuation.Load(c); return m;
        }
    }

    public class ObjectiveThreat
    {
        public int Id, Team;
        public string Key;
        public Vec2 Position;
        public float Hp, LastSeen;
        internal JObj Save() => new JObj().Set("id", Id).Set("team", Team).Set("key", Key).Set("pos", SnapIO.V(Position))
            .Set("hp", SnapIO.X(Hp)).Set("last_seen_t", SnapIO.X(LastSeen));
        internal static ObjectiveThreat Load(Dictionary<string, object> d)
        {
            var t = new ObjectiveThreat(); d.Load("id", ref t.Id); d.Load("team", ref t.Team); d.Load("key", ref t.Key);
            d.Load("pos", ref t.Position); d.Load("hp", ref t.Hp); d.Load("last_seen_t", ref t.LastSeen); return t;
        }
    }

    public static class Objectives
    {
        public const float AnchorCadence = 1f, GoalBand = 1f;
        public static List<object> View(World w, int team) => w.Teams[team].Objectives.Values.Select(o => (object)o.View()).ToList();
        public static Objective ForSquad(World w, int team, string name) => name != null && team >= 0 && team < w.Teams.Count
            ? w.Teams[team].Objectives.Values.FirstOrDefault(o => o.Squad == name) : null;
        public static Objective ForUnit(World w, Entity unit) => unit.ObjectiveId != null && unit.Team >= 0 && unit.Team < w.Teams.Count &&
            w.Teams[unit.Team].Objectives.TryGetValue(unit.ObjectiveId, out var o) && !o.Detached.Contains(unit.Id) ? o : null;
        static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
        static bool Name(string id) => id != null && id.Length >= 1 && id.Length <= 64 && Text.Clean(id, 64) == id;
        static JObj Fail(string why) => new JObj().Set("ok", false).Set("error", why);
        static bool Point(World w, Dictionary<string, object> c, string field, out Vec2 point)
        {
            var p = c.Arr(field); point = default;
            if (p.Count != 2 || !(p[0] is double x) || !(p[1] is double y) || !Finite((float)x) || !Finite((float)y) || x < 0 || y < 0 || x >= w.Map.W || y >= w.Map.H) return false;
            point = new Vec2((float)x, (float)y); return true;
        }

        public static JObj Configure(World w, int team, Dictionary<string, object> c)
        {
            string id = c.Str("id"); if (!Name(id)) return Fail("objective id must be 1-64 characters without surrounding whitespace or control characters");
            var controls = new[] { "pause", "resume", "cancel" }.Where(c.ContainsKey).ToList();
            if (controls.Count > 0)
            {
                if (controls.Count != 1 || !(c[controls[0]] is bool yes) || !yes) return Fail("exactly one objective control must be true: pause, resume or cancel");
                if (c.Keys.Any(k => k != "type" && k != "id" && k != controls[0] && k != "request_id" && k != "as_of_tick" && k != "plan_version")) return Fail("objective controls take only id; full configuration replaces an objective");
                if (!w.Teams[team].Objectives.TryGetValue(id, out var existing)) return Fail("unknown objective '" + id + "'");
                if (controls[0] == "cancel")
                {
                    Release(w, team, existing); w.Teams[team].Objectives.Remove(id);
                    Note(w, team, existing, "objective_cancelled", new JObj().Set("reason", "cancelled_by_command"));
                    return new JObj().Set("ok", true).Set("result", "objective cancelled; existing orders and paid production remain");
                }
                if (controls[0] == "pause") Pause(w, team, existing, "paused_by_command");
                else
                {
                    if (!w.Teams[team].Squads.TryGetValue(existing.Squad, out var squad)) return Fail("objective squad no longer exists");
                    existing.Paused = false; existing.PauseReason = null; existing.Detached.Clear(); existing.PhaseAt = w.Time;
                    foreach (var m in existing.Members.Values) { m.Assigned = false; m.Intent = null; }
                    Bind(w, team, existing, squad.Live(w, team), explicitIntent: true);
                    Note(w, team, existing, "objective_resumed", new JObj().Set("reason", "resumed_by_command"));
                }
                return new JObj().Set("ok", true).Set("objective", existing.View());
            }
            string squadName = c.Str("squad"), verb = c.Str("verb");
            if (squadName == null || !w.Teams[team].Squads.TryGetValue(squadName, out var selected)) return Fail("objective squad must name one of your squads");
            if (verb != "go" && verb != "hold" && verb != "escort" && verb != "patrol") return Fail("objective verb must be go, hold, escort or patrol");
            float radius = c.Num("radius"); if (!Finite(radius) || radius <= 0) return Fail("objective radius must be a finite positive distance");
            var o = new Objective { Id = id, Squad = squadName, Verb = verb, Radius = radius, PhaseAt = w.Time };
            if ((verb == "go" || verb == "hold") && !Point(w, c, "at", out o.At)) return Fail("go and hold require at:[x,y] inside the map");
            if (verb == "escort")
            {
                if (c.ContainsKey("target") && c.ContainsKey("target_id")) return Fail("escort target and target_id are aliases; supply only one");
                float target = c.ContainsKey("target_id") ? c.Num("target_id") : c.Num("target");
                o.TargetSquad = c.Str("target_squad");
                bool hasTarget = c.ContainsKey("target_id") || c.ContainsKey("target");
                if (hasTarget == (o.TargetSquad != null)) return Fail("escort requires exactly one own target_id or target_squad");
                if (hasTarget)
                {
                    if (!Finite(target) || target != MathF.Floor(target) || target <= 0 || target > int.MaxValue) return Fail("escort target_id must be a live own mobile unit");
                    var unit = w.Get((int)target);
                    if (unit == null || unit.Dead || unit.Team != team || unit.IsStructure || unit.IsMine || selected.Members.Contains(unit.Id)) return Fail("escort target must be a live own mobile unit outside the escorting squad");
                    o.TargetId = unit.Id;
                }
                else if (o.TargetSquad == squadName || !w.Teams[team].Squads.ContainsKey(o.TargetSquad)) return Fail("escort target_squad must be another own squad");
            }
            if (verb == "patrol")
            {
                foreach (var raw in c.Arr("waypoints"))
                {
                    var one = new Dictionary<string, object> { ["point"] = raw };
                    if (!Point(w, one, "point", out var at)) return Fail("patrol waypoints must be points inside the map");
                    o.Waypoints.Add(at);
                }
                if (o.Waypoints.Count < 2 || World.TightLoop(o.Waypoints[0], o.Waypoints.Skip(1).ToList())) return Fail("patrol needs at least two explicit waypoints spanning at least 4 tiles");
                o.At = o.Waypoints[0];
                if (c.ContainsKey("at") && (!Point(w, c, "at", out var firstAt) || Vec2.DistSq(firstAt, o.At) > 0.01f)) return Fail("patrol at must match its first waypoint; omit at otherwise");
            }
            if (c.ContainsKey("assemble"))
            {
                var assemble = c.Obj("assemble"); float min = assemble == null ? float.NaN : assemble.Num("min");
                if (assemble == null || !Point(w, assemble, "at", out o.AssemblyAt) || !Finite(min) || min < 1 || min != MathF.Floor(min) || min > int.MaxValue) return Fail("assemble requires at:[x,y] and a positive integer min");
                o.HasAssembly = true; o.AssemblyMin = (int)min;
            }
            if (c.ContainsKey("reinforce"))
            {
                if (!ObjectiveReinforcements.Parse(w, team, c.Obj("reinforce"), out var reinforce, out string error)) return Fail(error);
                o.Reinforce = reinforce;
            }
            if (c.ContainsKey("wake"))
            {
                if (!(c["wake"] is List<object> wakes)) return Fail("wake must be a list of literal conditions");
                foreach (var raw in wakes)
                {
                    if (!(raw is string wake) || (wake != "objective_done" && wake != "enemy_strength_seen>squad" && !Below(wake, out _))) return Fail("unknown objective wake condition");
                    if (!o.Wake.Contains(wake)) o.Wake.Add(wake);
                }
            }
            if (w.Teams[team].Objectives.TryGetValue(id, out var previous)) o.Version = previous.Version + 1;
            foreach (var old in w.Teams[team].Objectives.Values.Where(old => old.Squad == squadName || old.Id == id).ToList())
            { Release(w, team, old); w.Teams[team].Objectives.Remove(old.Id); }
            w.Teams[team].Objectives[id] = o;
            o.Anchor = o.HasAssembly ? o.AssemblyAt : o.At;
            o.Centre = o.Anchor;
            if (verb == "escort" && EscortAnchor(w, team, o, out var anchor)) o.Anchor = anchor;
            Bind(w, team, o, selected.Live(w, team), explicitIntent: true);
            Note(w, team, o, "objective_started", new JObj().Set("phase", o.Phase).Set("radius", radius).Set("at", SnapIO.V(o.Anchor)));
            return new JObj().Set("ok", true).Set("objective", o.View());
        }

        static bool Below(string wake, out int value)
        {
            value = 0; return wake.StartsWith("squad_below_", StringComparison.Ordinal) && int.TryParse(wake.Substring(12), out value) && value >= 1 && value <= 100;
        }
        static void Bind(World w, int team, Objective o, List<Entity> live, bool explicitIntent = false)
        {
            foreach (var unit in live)
            {
                if (o.Detached.Contains(unit.Id)) continue;
                unit.ObjectiveId = o.Id;
                if (!o.Members.ContainsKey(unit.Id)) o.Members[unit.Id] = new ObjectiveMember();
                if (explicitIntent && unit.Order != Order.Refuel && !SquadGuards.Controls(w, unit)) unit.Retreating = false;
            }
            var attached = live.Where(e => !o.Detached.Contains(e.Id)).ToList();
            if (live.Count > 0) o.HasHadMembers = true;
            if (attached.Count > 0) o.Centre = new Vec2(attached.Average(e => e.Pos.X), attached.Average(e => e.Pos.Y));
        }
        static void Release(World w, int team, Objective o)
        {
            foreach (var id in o.Members.Keys)
            {
                o.Members[id].Assigned = false;
                var unit = w.Get(id); if (unit == null || unit.Team != team || unit.ObjectiveId != o.Id) continue;
                unit.ObjectiveId = null;
                if (w.Teams[team].Squads.TryGetValue(o.Squad, out var squad)) SquadGuards.ClearBase(squad, unit);
            }
        }
        public static void DirectOrder(World w, Entity unit)
        {
            var o = ForUnit(w, unit); if (o == null) return;
            o.Detached.Add(unit.Id); o.Members.Remove(unit.Id); unit.ObjectiveId = null;
            if (w.Teams[unit.Team].Squads.TryGetValue(o.Squad, out var squad)) SquadGuards.ClearBase(squad, unit);
            Note(w, unit.Team, o, "detached_by_order", new JObj().Set("unit", unit.Id).Set("source", "objective").Set("reason", "direct_order"));
        }
        public static void MemberRemoved(World w, Entity unit, string squad)
        {
            if (unit == null || unit.Team < 0) return;
            var o = ForSquad(w, unit.Team, squad); if (o == null) return;
            DirectOrder(w, unit); Pause(w, unit.Team, o, "squad_membership_changed", new JObj().Set("unit", unit.Id));
        }
        public static void SquadRemoved(World w, int team, string squad)
        {
            var o = ForSquad(w, team, squad); if (o == null) return;
            Pause(w, team, o, "squad_disbanded");
            o.PauseReason = "squad_disbanded";
            SquadGuards.FlushBase(w, team, w.Teams[team].Squads[squad]);
            Release(w, team, o);
        }
        public static void InvalidateBase(World w, int team, string squad)
        {
            var o = ForSquad(w, team, squad); if (o == null) return;
            foreach (var pair in o.Members)
            {
                pair.Value.Intent = null;
                var unit = w.Get(pair.Key);
                if (o.Paused && unit != null && !unit.Dead) pair.Value.Goal = unit.Pos;
            }
        }
        public static void OwnRemoved(World w, Entity unit)
        {
            var o = ForUnit(w, unit); if (o != null) { if (o.Members.Count == 1) o.Centre = unit.Pos; o.Members.Remove(unit.Id); }
            unit.ObjectiveId = null;
        }
        public static bool EnrollSpawn(World w, Entity unit, string squadName)
        {
            var o = ForSquad(w, unit.Team, squadName); if (o == null || o.Paused && o.PauseReason == "squad_disbanded") return false;
            Bind(w, unit.Team, o, new List<Entity> { unit }); return true;
        }
        public static void WitnessGone(World w, Entity gone)
        {
            foreach (var team in w.Teams)
                if (gone.Team != team.Id && team.Objectives.Count > 0 && w.IsVisibleTo(team.Id, gone))
                    foreach (var o in team.Objectives.Values) o.Threats.Remove(gone.Id);
        }
        public static void CommandSucceeded(World w, int team)
        {
            if (team < 0 || team >= w.Teams.Count) return;
            foreach (var o in w.Teams[team].Objectives.Values)
            {
                o.CommandTick = w.Tick; o.BaselineHp.Clear(); o.BaselineMembers.Clear(); o.BaselineSpent.Clear();
                foreach (var unit in w.Owned(team).Where(e => !e.IsStructure && !e.IsMine)) o.BaselineHp[unit.Id] = unit.Hp;
                if (w.Teams[team].Squads.TryGetValue(o.Squad, out var squad)) foreach (int id in squad.Members) o.BaselineMembers.Add(id);
                if (o.Reinforce != null) foreach (var pair in o.Reinforce.Spent) o.BaselineSpent[pair.Key] = pair.Value;
            }
        }

        static void Note(World w, int team, Objective o, string kind, JObj facts, Sev severity = Sev.Notice)
        {
            var body = new JObj().Set("objective_id", o.Id).Set("squad", o.Squad).Set("verb", o.Verb).Set("phase", o.Phase);
            foreach (System.Collections.DictionaryEntry pair in facts) body.Set((string)pair.Key, pair.Value);
            w.Emit(kind, team, pos: o.Anchor, text: $"{o.Id}: {kind}");
            if (!w.Mirror) w.Observe.Note(team, "orders", kind, severity, "own", body, $"{o.Id}: {kind}");
        }
        static JObj Commitments(World w, int team, Objective o) => new JObj()
            .Set("other_objectives", w.Teams[team].Objectives.Values.Where(other => other != o).Select(other => (object)new JObj().Set("id", other.Id).Set("squad", other.Squad).Set("phase", other.Phase).Set("reinforce", other.Reinforce?.View())).ToList())
            .Set("production", w.Teams[team].UnitQueues.Values.SelectMany(q => q).Select(p => (object)new JObj().Set("unit", p.Key).Set("progress_s", p.Progress)
                .Set("structure_id", p.StructureId).Set("worker_id", p.WorkerId).Set("objective_id", p.ObjectiveId).Set("reinforcement_squad", p.ReinforcementSquad)).ToList())
            .Set("construction", w.Teams[team].StructureQueue.Select(p => (object)new JObj().Set("key", p.Key).Set("structure_id", p.StructureId)).ToList())
            .Set("reserves", SnapIO.Ints(w.Teams[team].Reserve)).Set("saving_for", w.Teams[team].SaveFor);

        public static void Pause(World w, int team, Objective o, string reason, JObj facts = null)
        {
            if (o.Paused) return;
            o.Paused = true; o.PauseReason = reason; o.PhaseAt = w.Time;
            foreach (var pair in o.Members)
            {
                var unit = w.Get(pair.Key); if (unit == null || unit.Dead) continue;
                pair.Value.Goal = unit.Pos; pair.Value.Assigned = true; pair.Value.Holding = true;
                Queue(w, team, o, unit, pair.Value, Order.Idle);
            }
            var own = w.Owned(team).Where(e => !e.IsStructure && !e.IsMine).ToDictionary(e => e.Id, e => e);
            var changed = new JObj().Set("from_tick", o.CommandTick)
                .Set("lost_units", o.BaselineHp.Keys.Where(id => !own.ContainsKey(id)).OrderBy(id => id).Cast<object>().ToList())
                .Set("new_units", own.Keys.Where(id => !o.BaselineHp.ContainsKey(id)).OrderBy(id => id).Cast<object>().ToList())
                .Set("hp", own.Values.Where(e => o.BaselineHp.TryGetValue(e.Id, out var hp) && MathF.Abs(hp - e.Hp) >= 0.5f)
                    .Select(e => (object)new JObj().Set("unit", e.Id).Set("before", o.BaselineHp[e.Id]).Set("after", e.Hp)).ToList())
                .Set("members_removed", o.BaselineMembers.Where(id => !o.Members.ContainsKey(id)).OrderBy(id => id).Cast<object>().ToList())
                .Set("orders", own.Values.Where(e => e.LastOrderTick > o.CommandTick).Select(e => (object)new JObj().Set("unit", e.Id).Set("tick", e.LastOrderTick).Set("by", e.LastOrderBy).Set("order", e.OrderName)).ToList())
                .Set("retreats", own.Values.Where(e => e.LastRetreatTick > o.CommandTick).Select(e => (object)new JObj().Set("unit", e.Id).Set("tick", e.LastRetreatTick)).ToList())
                .Set("spent", o.Reinforce == null ? null : o.Reinforce.Spent.Aggregate(new JObj(), (j, pair) => j.Set(pair.Key, pair.Value - (o.BaselineSpent.TryGetValue(pair.Key, out int before) ? before : 0))));
            var visible = w.Entities.Where(e => !e.Dead && e.Team >= 0 && e.Team != team && e.IsArmed && w.IsVisibleTo(team, e) && Vec2.Dist(e.Center, o.Centre) <= o.Radius).ToList();
            var threats = new JObj().Set("around", SnapIO.V(o.Centre)).Set("radius", o.Radius)
                .Set("visible", visible.Select(e => (object)new JObj().Set("id", e.Id).Set("key", e.Def.Key).Set("team", e.Team).Set("pos", SnapIO.V(e.Center)).Set("hp", e.Hp).Set("src", "sight")).ToList())
                .Set("remembered", o.Threats.Values.Where(t => !visible.Any(e => e.Id == t.Id) && Vec2.Dist(t.Position, o.Centre) <= o.Radius)
                    .Select(t => (object)t.Save().Set("src", "memory")).ToList());
            var packet = new JObj().Set("objective", new JObj().Set("id", o.Id).Set("squad", o.Squad).Set("verb", o.Verb).Set("version", o.Version).Set("phase", o.Phase))
                .Set("reason", reason).Set("facts", facts ?? new JObj()).Set("changed_since_command", changed).Set("threats", threats)
                .Set("commitments", Commitments(w, team, o)).Set("triggered_at_s", w.Time).Set("trigger_basis", "condition_observed").Set("action", "pause");
            o.Packet = Json.Write(packet);
            Note(w, team, o, "objective_paused", new JObj().Set("reason", reason));
            Note(w, team, o, "decision_needed", packet, Sev.Critical);
        }

        public static bool GuardDecision(World w, int team, Squad squad, JObj facts)
        {
            var o = ForSquad(w, team, squad.Name); if (o == null) return false;
            Pause(w, team, o, "guard_withdraw_below_strength_pct", facts); return true;
        }

        static bool EscortAnchor(World w, int team, Objective o, out Vec2 anchor)
        {
            anchor = default;
            if (o.TargetId != 0)
            {
                var target = w.Get(o.TargetId);
                if (target == null || target.Dead || target.Team != team || (w.Teams[team].Squads.TryGetValue(o.Squad, out var s) && s.Members.Contains(target.Id))) return false;
                anchor = target.Pos; return true;
            }
            if (!w.Teams[team].Squads.TryGetValue(o.TargetSquad ?? "", out var squad)) return false;
            var live = squad.Live(w, team); if (live.Count == 0) return false;
            anchor = new Vec2(live.Average(e => e.Pos.X), live.Average(e => e.Pos.Y)); return true;
        }

        public static void Step(World w, int team)
        {
            if (w.Tick % 2 != 0 || w.Teams[team].Defeated || w.Teams[team].Left) return;
            foreach (var o in w.Teams[team].Objectives.Values)
            {
                // Disband released the old squad's authority; a later squad with the same name is not a handback.
                if (o.Paused && o.PauseReason == "squad_disbanded") continue;
                if (!w.Teams[team].Squads.TryGetValue(o.Squad, out var squad)) { Pause(w, team, o, "squad_gone"); continue; }
                var live = squad.Live(w, team); Bind(w, team, o, live);
                foreach (int id in o.Members.Keys.Where(id => !squad.Members.Contains(id) || w.Get(id) == null).ToList()) o.Members.Remove(id);
                var attached = live.Where(e => !o.Detached.Contains(e.Id)).ToList();
                if (attached.Count == 0 && (o.HasHadMembers || !o.HasAssembly || o.Reinforce == null || !o.Reinforce.Keep.Any(k => k.Value > 0)))
                { Pause(w, team, o, live.Count == 0 ? "squad_gone" : "all_members_detached"); continue; }
                var seen = w.Entities.Where(e => !e.Dead && e.Team >= 0 && e.Team != team && e.IsArmed && w.IsVisibleTo(team, e)).ToList();
                foreach (var enemy in seen) o.Threats[enemy.Id] = new ObjectiveThreat { Id = enemy.Id, Team = enemy.Team, Key = enemy.Def.Key, Position = enemy.Center, Hp = enemy.Hp, LastSeen = w.Time };
                foreach (int id in o.Threats.Keys.Where(id => w.Time - o.Threats[id].LastSeen > 600).ToList()) o.Threats.Remove(id);
                if (o.Paused)
                {
                    foreach (var unit in attached)
                    {
                        var m = o.Members[unit.Id];
                        if (!m.Assigned || m.Intent == null)
                        { m.Goal = unit.Pos; m.Assigned = true; m.Holding = true; Queue(w, team, o, unit, m, Order.Idle); }
                    }
                    continue;
                }
                var retreating = attached.FirstOrDefault(e => e.Retreating && !SquadGuards.SupersedesNativeRetreat(w, e));
                if (retreating != null) { Pause(w, team, o, "native_retreat", new JObj().Set("unit", retreating.Id)); continue; }
                var g = squad.Guards;
                if (g.StrengthWithdrawn && attached.Any(e => g.Units.TryGetValue(e.Id, out var state) && !state.StrengthExempt))
                {
                    Pause(w, team, o, "guard_withdraw_below_strength_pct", new JObj().Set("rule", "withdraw_below_strength_pct")
                        .Set("latched", true).Set("to", SnapIO.V(g.To))); continue;
                }
                if (attached.Count > 0 && attached.All(e => g.Units.TryGetValue(e.Id, out var state) && state.HpRetreated))
                {
                    Pause(w, team, o, "guard_retreat", new JObj().Set("rule", "retreat_below_pct")
                        .Set("units", attached.Select(e => (object)e.Id).ToList()).Set("to", SnapIO.V(g.To))); continue;
                }
                if (o.Verb == "escort" && !EscortAnchor(w, team, o, out _)) { Pause(w, team, o, "escort_target_gone"); continue; }
                float hp = live.Sum(e => e.Hp), strength = squad.FormationHp > 0 ? hp / squad.FormationHp * 100 : 0;
                var centre = attached.Count > 0 ? new Vec2(attached.Average(e => e.Pos.X), attached.Average(e => e.Pos.Y)) : o.Anchor;
                foreach (string wake in o.Wake)
                {
                    if (Below(wake, out int threshold) && strength < threshold) Pause(w, team, o, wake, new JObj().Set("strength_pct", strength).Set("threshold_pct", threshold));
                    else if (wake == "enemy_strength_seen>squad")
                    {
                        var nearby = seen.Where(e => !e.IsStructure && Vec2.Dist(e.Pos, centre) <= o.Radius).ToList(); float enemyHp = nearby.Sum(e => e.Hp);
                        if (enemyHp > hp) Pause(w, team, o, wake, new JObj().Set("enemy_hp_seen", enemyHp).Set("squad_hp", hp).Set("radius", o.Radius).Set("centre", SnapIO.V(centre)).Set("enemies", nearby.Select(e => (object)e.Id).ToList()));
                    }
                    if (o.Paused) break;
                }
                if (o.Paused) continue;
                ObjectiveReinforcements.Tick(w, team, o, squad, live); if (o.Paused) continue;
                if (o.HasAssembly && !o.Assembled && attached.Count(e => Vec2.Dist(e.Pos, o.AssemblyAt) <= o.Radius) >= o.AssemblyMin)
                { o.Assembled = true; o.PhaseAt = w.Time; Invalidate(o); Note(w, team, o, "objective_phase", new JObj().Set("phase", o.Phase).Set("reason", "assembled")); }
                Vec2 anchor = o.HasAssembly && !o.Assembled ? o.AssemblyAt : o.Verb == "patrol" ? o.Waypoints[o.PatrolIndex] : o.At;
                if (o.Verb == "escort" && !(o.HasAssembly && !o.Assembled)) EscortAnchor(w, team, o, out anchor);
                bool moved = Vec2.Dist(o.Anchor, anchor) > GoalBand;
                if (o.Verb == "escort" && moved && w.Time - o.LastAnchorAt < AnchorCadence) anchor = o.Anchor;
                if (Vec2.Dist(o.Anchor, anchor) > GoalBand || attached.Any(e => !o.Members[e.Id].Assigned))
                {
                    o.Anchor = anchor; o.LastAnchorAt = w.Time;
                    if (!Assign(w, team, o, attached)) { Pause(w, team, o, "no_open_slots", new JObj().Set("at", SnapIO.V(anchor)).Set("radius", o.Radius)); continue; }
                }
                foreach (var unit in attached)
                {
                    var m = o.Members[unit.Id];
                    m.Arrived |= Vec2.Dist(unit.Pos, m.Goal) <= MathF.Max(0.75f, World.TakenReach(unit));
                    bool holdingPhase = o.HasAssembly && !o.Assembled || o.Verb == "hold" || o.Verb == "escort" || o.Verb == "go" && (o.Done || m.Arrived) || o.Verb == "patrol" && m.Arrived;
                    if (holdingPhase && Vec2.Dist(unit.Pos, o.Anchor) <= o.Radius) m.Holding = true;
                    if (m.Holding && Vec2.Dist(unit.Pos, o.Anchor) > o.Radius + 0.5f) m.Returning = true;
                    if (m.Returning && Vec2.Dist(unit.Pos, o.Anchor) < o.Radius * 0.7f) m.Returning = false;
                    Order order = o.Verb == "go" && !o.Done && !(o.HasAssembly && !o.Assembled) ? Order.AttackMove : Order.Move;
                    if (holdingPhase && m.Holding && !m.Returning) order = Order.Idle;
                    Queue(w, team, o, unit, m, order);
                }
                bool allThere = attached.Count > 0 && attached.All(e => o.Members[e.Id].Arrived);
                if (o.HasAssembly && !o.Assembled) continue;
                if (o.Verb == "go" && !o.Done && allThere)
                {
                    o.Done = true; o.PhaseAt = w.Time;
                    if (o.Wake.Contains("objective_done")) Pause(w, team, o, "objective_done");
                    else Note(w, team, o, "objective_completed", new JObj().Set("reason", "arrived"));
                }
                else if (o.Verb == "patrol" && allThere && w.Time - o.PhaseAt >= AnchorCadence)
                {
                    o.PatrolIndex = (o.PatrolIndex + 1) % o.Waypoints.Count; o.PhaseAt = w.Time; Invalidate(o);
                    Note(w, team, o, "objective_phase", new JObj().Set("patrol_index", o.PatrolIndex).Set("reason", "waypoint_reached"));
                }
            }
        }

        static void Invalidate(Objective o) { foreach (var m in o.Members.Values) { m.Assigned = false; m.Arrived = false; m.Holding = false; m.Returning = false; m.Intent = null; } }
        static bool Assign(World w, int team, Objective o, List<Entity> units)
        {
            if (units.Count == 0) return true;
            var ids = new HashSet<int>(units.Select(e => e.Id));
            var reserved = w.Teams[team].Objectives.Values.Where(other => other != o).SelectMany(other => other.Members)
                .Where(pair => !ids.Contains(pair.Key) && pair.Value.Assigned).Select(pair => pair.Value.Goal).ToList();
            float gap = MathF.Max(1.2f, units.Max(e => e.Def.Radius * 2f + 0.1f));
            var goals = Commands.FormationSlots(w, units, o.Anchor, gap, reserved, p => Vec2.Dist(p, o.Anchor) <= o.Radius);
            if (goals.Values.Any(p => !w.Map.Passable((int)p.X, (int)p.Y) || Vec2.Dist(p, o.Anchor) > o.Radius + 0.01f)) return false;
            var taken = new List<Vec2>(reserved);
            foreach (var goal in goals.Values)
            {
                if (taken.Any(other => Vec2.DistSq(other, goal) < gap * gap * 0.81f)) return false;
                taken.Add(goal);
            }
            foreach (var unit in units) { var m = o.Members[unit.Id]; m.Goal = goals[unit]; m.GoalAt = w.Time; m.Assigned = true; m.Arrived = false; m.Holding = false; m.Returning = false; }
            return true;
        }
        static void Queue(World w, int team, Objective o, Entity unit, ObjectiveMember m, Order order)
        {
            if (!w.Teams[team].Squads.TryGetValue(o.Squad, out var squad)) return;
            if (m.Intent != null && m.Intent.Order == order && Vec2.DistSq(m.Intent.Pos, m.Goal) < 0.01f) return;
            m.IntentRevision++;
            m.Intent = new GuardContinuation { Order = order, Pos = m.Goal, GuardPos = m.Goal, RouteOrigin = unit.Pos };
            SquadGuards.SetBaseIntent(w, squad, unit, m.Intent, o.Id, m.IntentRevision);
        }

        public static void BaseCommitted(World w, Entity unit)
        {
            var o = ForUnit(w, unit); if (o == null) return;
            Note(w, unit.Team, o, "objective_order", new JObj().Set("unit", unit.Id).Set("order", unit.OrderName).Set("goal", SnapIO.V(unit.OrderPos)));
        }
        public static bool Bounded(World w, Entity unit)
        {
            var o = ForUnit(w, unit);
            return o != null && o.Members.TryGetValue(unit.Id, out var m) && m.Holding && !unit.IsCarried && !unit.Stranded && unit.Order != Order.Refuel &&
                !unit.Retreating && !MinerPolicies.Controls(w, unit) && !SquadGuards.Controls(w, unit);
        }
        static Vec2 BoundCentre(Objective o, ObjectiveMember m) => o.Paused ? m.Goal : o.Anchor;
        public static Vec2 HoldPoint(World w, Entity unit, Vec2 fallback)
        { var o = ForUnit(w, unit); return o != null && Bounded(w, unit) ? o.Members[unit.Id].Goal : fallback; }
        public static bool TargetAllowed(World w, Entity unit, Entity target)
        {
            if (!Bounded(w, unit)) return true;
            var o = ForUnit(w, unit); return Vec2.Dist(target.Center, BoundCentre(o, o.Members[unit.Id])) <= o.Radius;
        }
        public static bool MotionAllowed(World w, Entity unit, Vec2 next)
        {
            if (!Bounded(w, unit)) return true;
            var o = ForUnit(w, unit); var centre = BoundCentre(o, o.Members[unit.Id]);
            return Vec2.Dist(next, centre) <= o.Radius + 0.01f || Vec2.Dist(next, centre) < Vec2.Dist(unit.Pos, centre) - 0.001f;
        }
    }
}
