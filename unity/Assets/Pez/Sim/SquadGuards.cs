using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public enum GuardAction { None, Support, Screen, HpRetreat, Strength }

    /// <summary>Every field is off unless explicitly supplied. Runtime is separate from native refuel continuations.</summary>
    public class SquadGuard
    {
        public float RetreatBelowPct, SupportWithin, ScreenBehind, WithdrawBelowStrengthPct;
        public readonly List<string> ScreenRoles = new List<string>();
        public bool HasTo, StrengthWithdrawn;
        public Vec2 To;
        public float StrengthAt = -999;
        public readonly Dictionary<int, GuardUnit> Units = new Dictionary<int, GuardUnit>();
        public readonly List<string> Notes = new List<string>();
        public bool Enabled => RetreatBelowPct > 0 || SupportWithin > 0 || ScreenRoles.Count > 0 || WithdrawBelowStrengthPct > 0;

        public JObj View() => new JObj().Set("retreat_below_pct", RetreatBelowPct > 0 ? (object)RetreatBelowPct : null)
            .Set("hold_for_support", SupportWithin > 0 ? (object)new JObj().Set("within", SupportWithin) : null)
            .Set("screen", ScreenRoles.Count > 0 ? (object)new JObj().Set("roles", ScreenRoles.Cast<object>().ToList()).Set("behind", ScreenBehind) : null)
            .Set("withdraw_below_strength_pct", WithdrawBelowStrengthPct > 0 ? (object)WithdrawBelowStrengthPct : null)
            .Set("to", HasTo ? (object)SnapIO.V(To) : null).Set("withdrawn", StrengthWithdrawn)
            .Set("controlled", Units.Where(kv => kv.Value.Action != GuardAction.None).Select(kv => (object)new JObj().Set("unit", kv.Key)
                .Set("action", kv.Value.Action.ToString().ToLowerInvariant())).ToList())
            .Set("interventions", Notes.Select(n => Json.Parse(n)).ToList());

        internal JObj Save() => new JObj().Set("retreat_below_pct", SnapIO.X(RetreatBelowPct)).Set("support_within", SnapIO.X(SupportWithin))
            .Set("screen_roles", ScreenRoles.Cast<object>().ToList()).Set("screen_behind", SnapIO.X(ScreenBehind))
            .Set("withdraw_below_strength_pct", SnapIO.X(WithdrawBelowStrengthPct)).Set("has_to", HasTo).Set("to", SnapIO.V(To))
            .Set("strength_withdrawn", StrengthWithdrawn).Set("strength_at", SnapIO.X(StrengthAt))
            .Set("units", Units.OrderBy(kv => kv.Key).Select(kv => (object)kv.Value.Save().Set("id", kv.Key)).ToList())
            .Set("notes", Notes.Cast<object>().ToList());

        internal static SquadGuard Load(Dictionary<string, object> d)
        {
            var g = new SquadGuard(); if (d == null) return g;
            d.Load("retreat_below_pct", ref g.RetreatBelowPct); d.Load("support_within", ref g.SupportWithin);
            d.Load("screen_behind", ref g.ScreenBehind); d.Load("withdraw_below_strength_pct", ref g.WithdrawBelowStrengthPct);
            d.Load("has_to", ref g.HasTo); d.Load("to", ref g.To); d.Load("strength_withdrawn", ref g.StrengthWithdrawn); d.Load("strength_at", ref g.StrengthAt);
            g.ScreenRoles.AddRange(d.Arr("screen_roles").OfType<string>());
            foreach (var u in d.Objs("units")) g.Units[u.In("id")] = GuardUnit.Load(u);
            g.Notes.AddRange(d.Arr("notes").OfType<string>().TakeLast(SquadGuards.MaxNotes));
            return g;
        }
    }

    public class GuardUnit
    {
        public bool HpRetreated, HpSpent, SupportHeld, ScreenActive, StrengthExempt, StrengthAssigned, PaceApplied;
        public int PatrolLap, LastLoopPhase = -1;
        public float HpAt = -999, SupportAt = -999, ScreenAt = -999, ScreenGoalAt = -999, LastDirectAt = -999;
        public Vec2 HpGoal, ScreenGoal, StrengthGoal;
        public GuardAction Action;
        public GuardContinuation Continuation;

        internal JObj Save() => new JObj().Set("hp_retreated", HpRetreated).Set("hp_spent", HpSpent).Set("hp_at", SnapIO.X(HpAt))
            .Set("hp_goal", SnapIO.V(HpGoal)).Set("support_held", SupportHeld).Set("support_at", SnapIO.X(SupportAt))
            .Set("screen_active", ScreenActive).Set("screen_at", SnapIO.X(ScreenAt)).Set("screen_goal_at", SnapIO.X(ScreenGoalAt))
            .Set("screen_goal", SnapIO.V(ScreenGoal)).Set("strength_exempt", StrengthExempt).Set("strength_assigned", StrengthAssigned)
            .Set("strength_goal", SnapIO.V(StrengthGoal)).Set("last_direct_at", SnapIO.X(LastDirectAt)).Set("action", Action.ToString())
            .Set("pace_applied", PaceApplied).Set("patrol_lap", PatrolLap).Set("last_loop_phase", LastLoopPhase)
            .Set("continuation", Continuation?.Save());

        internal static GuardUnit Load(Dictionary<string, object> d)
        {
            var u = new GuardUnit();
            d.Load("hp_retreated", ref u.HpRetreated); d.Load("hp_spent", ref u.HpSpent); d.Load("hp_at", ref u.HpAt); d.Load("hp_goal", ref u.HpGoal);
            d.Load("support_held", ref u.SupportHeld); d.Load("support_at", ref u.SupportAt);
            d.Load("screen_active", ref u.ScreenActive); d.Load("screen_at", ref u.ScreenAt); d.Load("screen_goal_at", ref u.ScreenGoalAt); d.Load("screen_goal", ref u.ScreenGoal);
            d.Load("strength_exempt", ref u.StrengthExempt); d.Load("strength_assigned", ref u.StrengthAssigned); d.Load("strength_goal", ref u.StrengthGoal);
            d.Load("last_direct_at", ref u.LastDirectAt); d.LoadEnum("action", ref u.Action);
            d.Load("pace_applied", ref u.PaceApplied); d.Load("patrol_lap", ref u.PatrolLap); d.Load("last_loop_phase", ref u.LastLoopPhase);
            if (d.Obj("continuation") is Dictionary<string, object> c) u.Continuation = GuardContinuation.Load(c);
            return u;
        }
    }

    public class GuardContinuation
    {
        public Order Order;
        public Vec2 Pos, GuardPos, HomePos, RouteOrigin;
        public int Target, Group;
        public float SpeedCap;
        public bool Loop, Derricks, UntilCapturable, Responding;
        public readonly List<Vec2> Waypoints = new List<Vec2>();

        internal static GuardContinuation Capture(Entity e)
        {
            var c = new GuardContinuation { Order = e.Order, Pos = e.OrderPos, Target = e.TargetId, GuardPos = e.GuardPos, RouteOrigin = e.Pos,
                HomePos = e.HomePos, Group = e.Group, SpeedCap = e.SpeedCap, Loop = e.WaypointLoop,
                Derricks = e.EngageDerricks, UntilCapturable = e.UntilCapturable, Responding = e.Responding };
            c.Waypoints.AddRange(e.Waypoints); return c;
        }

        internal void Restore(World w, Entity e)
        {
            w.SetOrder(e, Order, Pos, Target); e.GuardPos = GuardPos; e.HomePos = HomePos; e.Group = Group; e.SpeedCap = SpeedCap;
            e.Waypoints.AddRange(Waypoints); e.WaypointLoop = Loop; e.EngageDerricks = Derricks;
            e.UntilCapturable = UntilCapturable; e.Responding = Responding; e.LastOrderBy = "squad_guard";
        }

        internal void ResumeAfterRefuel(Entity e)
        {
            e.ResumeOrder = Order; e.ResumePos = Pos; e.ResumeTarget = Target; e.ResumeGuard = GuardPos; e.ResumeSpeedCap = SpeedCap;
            e.ResumeWaypoints.Clear(); e.ResumeWaypoints.AddRange(Waypoints);
            if (Loop) e.ResumeWaypoints.Add(new Vec2(float.NaN, 0));
        }

        internal JObj Save() => new JObj().Set("order", Order.ToString()).Set("pos", SnapIO.V(Pos)).Set("target", Target)
            .Set("guard_pos", SnapIO.V(GuardPos)).Set("home_pos", SnapIO.V(HomePos)).Set("route_origin", SnapIO.V(RouteOrigin)).Set("group", Group).Set("speed_cap", SnapIO.X(SpeedCap))
            .Set("loop", Loop).Set("derricks", Derricks).Set("until_capturable", UntilCapturable).Set("responding", Responding)
            .Set("waypoints", SnapIO.Vs(Waypoints));

        internal static GuardContinuation Load(Dictionary<string, object> d)
        {
            var c = new GuardContinuation(); d.LoadEnum("order", ref c.Order); d.Load("pos", ref c.Pos); d.Load("target", ref c.Target);
            d.Load("guard_pos", ref c.GuardPos); d.Load("home_pos", ref c.HomePos); d.Load("group", ref c.Group); d.Load("speed_cap", ref c.SpeedCap);
            c.RouteOrigin = c.GuardPos; d.Load("route_origin", ref c.RouteOrigin);
            d.Load("loop", ref c.Loop); d.Load("derricks", ref c.Derricks); d.Load("until_capturable", ref c.UntilCapturable); d.Load("responding", ref c.Responding);
            c.Waypoints.AddRange(SnapIO.ToVecs(d.Arr("waypoints"))); return c;
        }
    }

    public static partial class SquadGuards
    {
        public const int MaxNotes = 24;
        public const float Cooldown = 5f;

        public static JObj Configure(World w, int team, Dictionary<string, object> c)
        {
            string name = c.Str("squad");
            if (name == null || !w.Teams[team].Squads.TryGetValue(name, out var squad)) return Fail("guard squad must name one of your squads");
            var g = new SquadGuard();
            if (!Percentage(c, "retreat_below_pct", out g.RetreatBelowPct) || !Percentage(c, "withdraw_below_strength_pct", out g.WithdrawBelowStrengthPct))
                return Fail("guard percentages must be finite numbers from 0 to 95");
            if (c.ContainsKey("hold_for_support"))
            {
                var support = c.Obj("hold_for_support");
                float within = support == null ? float.NaN : support.Num("within");
                if (!Finite(within) || within < 1 || within > w.Map.W + w.Map.H) return Fail("hold_for_support.within must be a finite distance of at least 1 tile");
                g.SupportWithin = within;
            }
            if (c.ContainsKey("screen"))
            {
                var screen = c.Obj("screen");
                float behind = screen == null ? float.NaN : screen.Num("behind");
                var roles = screen?.Arr("roles");
                if (!Finite(behind) || behind < 0 || roles == null || roles.Count == 0 || roles.Count > 32 || roles.Any(r => !(r is string)))
                    return Fail("screen requires nonempty roles:[exact unit keys] and finite behind >= 0");
                foreach (string role in roles)
                {
                    var def = w.Def(role);
                    if (def == null || def.IsStructure || def.IsMine || (def.OwnerTeam >= 0 && def.OwnerTeam != team)) return Fail("unknown own screen unit key '" + role + "'");
                    if (!g.ScreenRoles.Contains(role)) g.ScreenRoles.Add(role);
                }
                g.ScreenBehind = behind;
            }
            if (c.ContainsKey("to"))
            {
                var to = c.Arr("to");
                float x = to.Count == 2 && to[0] is double dx ? (float)dx : float.NaN;
                float y = to.Count == 2 && to[1] is double dy ? (float)dy : float.NaN;
                if (!Finite(x) || !Finite(y) || x < 0 || y < 0 || x >= w.Map.W || y >= w.Map.H) return Fail("guard to must be [x,y] inside the map");
                g.HasTo = true; g.To = new Vec2(x, y);
            }
            if ((g.RetreatBelowPct > 0 || g.WithdrawBelowStrengthPct > 0) && !g.HasTo) return Fail("retreat guards require an explicit to:[x,y]");
            var old = squad.Guards;
            string actor = w.CommandActor; w.CommandActor = null;
            try
            {
                foreach (var e in squad.Live(w, team))
                {
                    e.GuardMotionPaused = false;
                    if (!old.Units.TryGetValue(e.Id, out var u)) continue;
                    if (u.PaceApplied && u.Continuation != null)
                    {
                        e.SpeedCap = u.Continuation.SpeedCap;
                        if (e.Order == Order.Refuel) e.ResumeSpeedCap = u.Continuation.SpeedCap;
                    }
                    if ((u.Action == GuardAction.Support || u.Action == GuardAction.Screen) && u.Continuation != null)
                    {
                        if (e.Order == Order.Refuel) u.Continuation.ResumeAfterRefuel(e);
                        else if (CanControl(w, e)) u.Continuation.Restore(w, e);
                    }
                }
            }
            finally { w.CommandActor = actor; }
            g.Notes.AddRange(old.Notes.TakeLast(MaxNotes)); squad.Guards = g;
            return new JObj().Set("ok", true).Set("guards", g.View()).Set("result", "guard configuration replaced; omitted guards are off");
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Percentage(Dictionary<string, object> c, string name, out float value)
        {
            value = c.ContainsKey(name) ? c.Num(name) : 0;
            return Finite(value) && value >= 0 && value <= 95;
        }
        static JObj Fail(string reason) => new JObj().Set("ok", false).Set("error", reason);

        static GuardUnit State(SquadGuard g, Entity unit)
        {
            if (!g.Units.TryGetValue(unit.Id, out var state)) g.Units[unit.Id] = state = new GuardUnit();
            return state;
        }

        static bool CanControl(World w, Entity e)
        {
            if (e.Dead || e.IsCarried || e.Stranded || e.Order == Order.Refuel || w.Time < e.SettledUntil || MinerPolicies.Controls(w, e)) return false;
            bool ownRetreat = SupersedesNativeRetreat(w, e);
            if (e.Retreating && !ownRetreat) return false;
            return ownRetreat || !(e.RetreatBelow > 0 && !e.RetreatSpent && e.Hp < e.Def.MaxHp * e.RetreatBelow);
        }

        static Dictionary<Entity, Vec2> Slots(World w, int team, List<Entity> units, Vec2 to, Func<Vec2, bool> allowed = null)
        {
            var ids = new HashSet<int>(units.Select(e => e.Id));
            var reserved = w.Owned(team).Where(e => !e.IsStructure && !e.IsMine && !ids.Contains(e.Id)).Select(e => e.Pos).ToList();
            foreach (var squad in w.Teams[team].Squads.Values)
                foreach (var pair in squad.Guards.Units)
                    if (!ids.Contains(pair.Key))
                    {
                        if (squad.Guards.StrengthWithdrawn && !pair.Value.StrengthExempt && pair.Value.StrengthAssigned) reserved.Add(pair.Value.StrengthGoal);
                        else if (pair.Value.HpRetreated) reserved.Add(pair.Value.HpGoal);
                        else if (pair.Value.ScreenActive) reserved.Add(pair.Value.ScreenGoal);
                    }
            float gap = MathF.Max(1.2f, units.Max(e => e.Def.Radius * 2f + 0.1f));
            return Commands.FormationSlots(w, units, to, gap, reserved, allowed);
        }

        static void Record(World w, int team, Squad squad, string kind, string rule, JObj facts, Sev severity = Sev.Notice)
        {
            string action = kind == "decision_needed" ? "wake" : kind == "guard_released" ? "resume" :
                kind == "detached_by_order" ? "detach" : rule == "hold_for_support" ? "hold" : "move";
            var body = new JObj().Set("squad", squad.Name).Set("rule", rule).Set("kind", kind).Set("tick", w.Tick).Set("t", w.Time)
                .Set("triggered_at_s", w.Time).Set("trigger_basis", "condition_observed").Set("action", action);
            foreach (System.Collections.DictionaryEntry pair in facts) body.Set((string)pair.Key, pair.Value);
            string text = $"{squad.Name}: {rule} ({facts["reason"] ?? kind})";
            body.Set("text", text);
            squad.Guards.Notes.Add(Json.Write(body));
            while (squad.Guards.Notes.Count > MaxNotes) squad.Guards.Notes.RemoveAt(0);
            w.Emit(kind, team, text: text);
            if (!w.Mirror) w.Observe.Note(team, "orders", kind, severity, "own", body, text);
        }

        static bool Find(World w, Entity unit, out Squad squad)
        {
            squad = null;
            if (unit.Team < 0 || unit.Team >= w.Teams.Count) return false;
            squad = w.Teams[unit.Team].Squads.Values.FirstOrDefault(s => s.Guards.Enabled && s.Members.Contains(unit.Id));
            return squad != null;
        }

        public static bool SupersedesNativeRetreat(World w, Entity unit)
        {
            if (!Find(w, unit, out var squad)) return false;
            var g = squad.Guards;
            return g.RetreatBelowPct > 0 || (g.StrengthWithdrawn && g.Units.TryGetValue(unit.Id, out var u) && !u.StrengthExempt);
        }

        public static bool Controls(World w, Entity unit) => Find(w, unit, out var squad) && Controls(w, unit.Team, squad, unit);

        public static bool Controls(World w, int team, Squad squad, Entity unit)
        {
            var g = squad.Guards;
            if (!g.Enabled || unit.Team != team || !g.Units.TryGetValue(unit.Id, out var state)) return false;
            return state.HpRetreated || state.SupportHeld || state.ScreenActive || (g.StrengthWithdrawn && !state.StrengthExempt);
        }

        public static void Release(World w, IEnumerable<Entity> units)
        {
            foreach (var e in units) e.GuardMotionPaused = false;
        }

        public static void DirectOrder(World w, Entity unit)
        {
            unit.GuardMotionPaused = false;
            if (!Find(w, unit, out var squad)) return;
            var g = squad.Guards; var u = State(g, unit);
            bool wasControlled = Controls(w, unit.Team, squad, unit);
            if (wasControlled) ResetMotionWindow(unit);
            u.HpRetreated = false; u.SupportHeld = false; u.ScreenActive = false; u.StrengthExempt = g.StrengthWithdrawn;
            u.Continuation = null; u.Action = GuardAction.None; u.LastDirectAt = w.Time; u.SupportAt = w.Time; u.ScreenAt = w.Time;
            u.PaceApplied = false; u.PatrolLap = 0; u.LastLoopPhase = -1;
            if (g.RetreatBelowPct > 0 && unit.Hp * 100f / unit.Def.MaxHp < g.RetreatBelowPct) { u.HpSpent = true; u.HpAt = w.Time; }
            if (wasControlled) Record(w, unit.Team, squad, "detached_by_order", "direct_order",
                new JObj().Set("reason", "direct_order").Set("unit", unit.Id).Set("source", "squad_guard"));
        }

        /// <summary>P6 stages base intent here; its coordinator must resolve guards before the single order write.</summary>
        public static void SetBaseIntent(World w, Squad squad, Entity unit, GuardContinuation continuation)
        {
            var state = State(squad.Guards, unit); state.Continuation = continuation;
        }

        public static void Step(World w, int team)
        {
            if (w.Tick % 2 != 0 || w.Teams[team].Defeated || w.Teams[team].Left) return;
            foreach (var squad in w.Teams[team].Squads.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                var g = squad.Guards; if (!g.Enabled) continue;
                var live = squad.Live(w, team);
                foreach (int id in g.Units.Keys.Where(id => !squad.Members.Contains(id) || w.Get(id) == null).ToList()) g.Units.Remove(id);
                foreach (var unit in live)
                {
                    var state = State(g, unit);
                    if (CanControl(w, unit) && state.Action == GuardAction.None &&
                        (unit.Order == Order.Move || unit.Order == Order.AttackMove || (unit.Order == Order.Attack && unit.LastOrderBy != "sim")))
                    {
                        var continuation = GuardContinuation.Capture(unit);
                        if (state.Continuation != null)
                        {
                            var previous = state.Continuation;
                            if (state.PaceApplied) continuation.SpeedCap = previous.SpeedCap;
                            if (Vec2.DistSq(previous.Pos, continuation.Pos) < 0.01f || (continuation.Target != 0 && previous.Target == continuation.Target))
                                continuation.RouteOrigin = previous.RouteOrigin;
                            else if (unit.LastOrderBy == "sim" && previous.Waypoints.Count > 0) continuation.RouteOrigin = previous.Pos;
                        }
                        state.Continuation = continuation;
                    }
                }
                EvaluateRetreats(w, team, squad, g, live);
                EvaluateSupport(w, team, squad, g, live);
                EvaluateScreen(w, team, squad, g, live);
                foreach (var unit in live) Apply(w, team, squad, g, unit, State(g, unit));
            }
        }

        static GuardContinuation Base(GuardUnit state, Entity unit) => state.Continuation ?? GuardContinuation.Capture(unit);
        static bool Traveling(GuardContinuation c) => c.Order == Order.Move || c.Order == Order.AttackMove || c.Order == Order.Attack;
        static float RouteLeft(Entity e, GuardContinuation c)
        {
            float route = Vec2.Dist(e.Pos, c.Pos); var previous = c.Pos;
            if (!c.Loop) foreach (var next in c.Waypoints) { route += Vec2.Dist(previous, next); previous = next; }
            return route;
        }

        static float RouteProgress(Entity unit, GuardUnit u, GuardContinuation c)
        {
            if (!c.Loop || c.Waypoints.Count == 0) return -RouteLeft(unit, c);
            // A measurement origin on the EXISTING route, never a new destination. Native patrols rotate their list
            // independently; unwrap laps so a leader entering the next leg cannot look like a distant straggler.
            var points = new List<Vec2> { c.Pos }; points.AddRange(c.Waypoints);
            int n = points.Count;
            int origin = Enumerable.Range(0, n).OrderBy(i => points[i].X).ThenBy(i => points[i].Y).First();
            int phase = (n - origin) % n;
            if (u.LastLoopPhase >= 0 && phase < u.LastLoopPhase) u.PatrolLap++;
            u.LastLoopPhase = phase;
            float cycle = 0, prefix = 0;
            for (int i = 0; i < n; i++)
            {
                float leg = Vec2.Dist(points[(origin + i) % n], points[(origin + i + 1) % n]);
                cycle += leg; if (i < phase) prefix += leg;
            }
            return u.PatrolLap * cycle + prefix - Vec2.Dist(unit.Pos, c.Pos);
        }

        static bool Stronger(SquadGuard g, GuardUnit u) => u.HpRetreated || (g.StrengthWithdrawn && !u.StrengthExempt);

        static void EvaluateSupport(World w, int team, Squad squad, SquadGuard g, List<Entity> live)
        {
            if (g.SupportWithin <= 0) return;
            var going = live.Where(e => !e.IsAir && CanControl(w, e) && !Stronger(g, State(g, e)) && Traveling(Base(State(g, e), e))).ToList();
            if (going.Count < 2) return;
            float pace = going.Min(e => e.Def.Speed);
            foreach (var e in going)
                if (!State(g, e).SupportHeld) { e.SpeedCap = pace; State(g, e).PaceApplied = true; }
            var progress = going.ToDictionary(e => e.Id, e => RouteProgress(e, State(g, e), Base(State(g, e), e)));
            float rear = progress.Values.Min();
            foreach (var unit in going)
            {
                var u = State(g, unit); float lag = progress[unit.Id] - rear;
                if (u.SupportHeld)
                {
                    if (lag < g.SupportWithin * 0.7f && w.Time - u.SupportAt >= Cooldown)
                    {
                        u.SupportHeld = false; u.SupportAt = w.Time;
                        Record(w, team, squad, "guard_released", "hold_for_support", new JObj().Set("reason", "support_caught_up")
                            .Set("unit", unit.Id).Set("lag", lag).Set("enter", g.SupportWithin).Set("exit", g.SupportWithin * 0.7f));
                    }
                }
                else if (lag > g.SupportWithin && w.Time - u.SupportAt >= Cooldown && w.Time - u.LastDirectAt >= Cooldown)
                {
                    u.Continuation ??= GuardContinuation.Capture(unit); u.SupportHeld = true; u.SupportAt = w.Time;
                    Record(w, team, squad, "guard_triggered", "hold_for_support", new JObj().Set("reason", "support_behind")
                        .Set("unit", unit.Id).Set("lag", lag).Set("enter", g.SupportWithin).Set("exit", g.SupportWithin * 0.7f));
                }
            }
        }

        static void EvaluateScreen(World w, int team, Squad squad, SquadGuard g, List<Entity> live)
        {
            if (g.ScreenRoles.Count == 0) return;
            var frontUnits = live.Where(e => !e.IsAir && CanControl(w, e) && !Stronger(g, State(g, e)) && !g.ScreenRoles.Contains(e.Def.Key) &&
                Traveling(Base(State(g, e), e))).ToList();
            if (frontUnits.Count == 0) return;
            var centre = new Vec2(frontUnits.Average(e => e.Pos.X), frontUnits.Average(e => e.Pos.Y));
            var route = new Vec2(frontUnits.Average(e => Base(State(g, e), e).Pos.X - Base(State(g, e), e).RouteOrigin.X),
                frontUnits.Average(e => Base(State(g, e), e).Pos.Y - Base(State(g, e), e).RouteOrigin.Y));
            if (route.LengthSq < 0.01f) return;
            var forward = route.Normalized;
            float projection = frontUnits.Max(e => (e.Pos.X - centre.X) * forward.X + (e.Pos.Y - centre.Y) * forward.Y);
            var target = centre + forward * (projection - g.ScreenBehind);
            var roles = live.Where(e => CanControl(w, e) && !Stronger(g, State(g, e)) && !State(g, e).SupportHeld && g.ScreenRoles.Contains(e.Def.Key)).ToList();
            if (roles.Count == 0) return;
            var updating = roles.Where(e => w.Time - State(g, e).LastDirectAt >= Cooldown &&
                (!State(g, e).ScreenActive || (w.Time - State(g, e).ScreenGoalAt >= Cooldown && Vec2.Dist(State(g, e).ScreenGoal, target) > 1f))).ToList();
            if (updating.Count == 0) return;
            var slots = Slots(w, team, updating, target, p => (p.X - target.X) * forward.X + (p.Y - target.Y) * forward.Y <= 0.01f);
            foreach (var unit in updating)
            {
                var u = State(g, unit); u.Continuation ??= GuardContinuation.Capture(unit);
                u.ScreenActive = true; u.ScreenAt = w.Time; u.ScreenGoalAt = w.Time; u.ScreenGoal = slots[unit];
                Record(w, team, squad, "guard_triggered", "screen", new JObj().Set("reason", "commanded_front")
                    .Set("unit", unit.Id).Set("behind", g.ScreenBehind).Set("goal", SnapIO.V(u.ScreenGoal))
                    .Set("bearing", SnapIO.V(forward)).Set("enter_margin", 1).Set("exit_margin", 0.6f));
            }
        }

        static void Apply(World w, int team, Squad squad, SquadGuard g, Entity unit, GuardUnit u)
        {
            if (!CanControl(w, unit)) return;
            GuardAction action = g.StrengthWithdrawn && !u.StrengthExempt && u.StrengthAssigned ? GuardAction.Strength :
                u.HpRetreated ? GuardAction.HpRetreat : u.SupportHeld ? GuardAction.Support : u.ScreenActive ? GuardAction.Screen : GuardAction.None;
            if (action == GuardAction.None)
            {
                if (u.Action == GuardAction.Support || u.Action == GuardAction.Screen)
                {
                    unit.GuardMotionPaused = false; u.Continuation?.Restore(w, unit);
                }
                u.Action = GuardAction.None; return;
            }
            var target = action == GuardAction.Strength ? u.StrengthGoal : action == GuardAction.HpRetreat ? u.HpGoal : u.ScreenGoal;
            bool issue = action != u.Action || (action == GuardAction.Screen && Vec2.Dist(target, unit.OrderPos) > 0.6f && Vec2.Dist(target, unit.Pos) > 0.6f);
            if (issue)
            {
                if (action != u.Action) ResetMotionWindow(unit);
                if (action == GuardAction.Support) w.SetOrder(unit, Order.Idle, unit.Pos);
                else w.SetOrder(unit, Order.Move, target);
                unit.LastOrderBy = "squad_guard";
                if (action == GuardAction.HpRetreat || action == GuardAction.Strength) unit.LastRetreatTick = w.Tick;
            }
            unit.GuardMotionPaused = true; u.Action = action;
        }

        static void ResetMotionWindow(Entity unit)
        {
            // The previous controller's velocity is not evidence about the new intent. A fast retreat reversed by a
            // direct, together-paced advance otherwise looks like "nowhere" while its old two-second average decays.
            // Repeated direct commands after detachment do not clear it, and the new order is still guarded normally.
            unit.DanceVel = unit.DanceStep = unit.DanceHeading = default;
            unit.DanceSpeed = unit.DanceFor = unit.DanceFlips = 0;
        }
    }
}
