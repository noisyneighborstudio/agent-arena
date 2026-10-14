using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>Saved addressing only. Membership never issues an order; casualties retain their formation HP.</summary>
    public class Squad
    {
        public string Name;
        public readonly HashSet<int> Members = new HashSet<int>();
        public float FormationHp;
        public SquadGuard Guards = new SquadGuard();

        public List<Entity> Live(World w, int team) => Members.OrderBy(id => id).Select(w.Get)
            .Where(e => e != null && !e.Dead && e.Team == team && !e.IsStructure && !e.IsMine).ToList();

        public JObj View(World w, int team)
        {
            var live = Live(w, team);
            float hp = live.Sum(e => e.Hp);
            return new JObj().Set("name", Name).Set("members", live.Select(e => (object)e.Id).ToList())
                .Set("hp", hp).Set("formation_hp", FormationHp).Set("strength_pct", FormationHp > 0 ? StateView.Pct(hp / FormationHp) : 0)
                .Set("centre", live.Count == 0 ? null : (object)SnapIO.V(new Vec2(live.Average(e => e.Pos.X), live.Average(e => e.Pos.Y))))
                .Set("objective", null).Set("guards", Guards.View());
        }

        internal JObj Save()
        {
            var saved = new JObj().Set("name", Name).Set("members", Members.OrderBy(id => id).Cast<object>().ToList())
                .Set("formation_hp", SnapIO.X(FormationHp));
            if (Guards.Enabled || Guards.HasTo || Guards.Units.Count > 0 || Guards.Notes.Count > 0) saved.Set("guards", Guards.Save());
            return saved;
        }

        internal static Squad Load(Dictionary<string, object> d)
        {
            if (!Squads.ValidName(d.Str("name"))) return null;
            var squad = new Squad { Name = d.Str("name") };
            d.Load("formation_hp", ref squad.FormationHp);
            squad.Guards = SquadGuard.Load(d.Obj("guards"));
            foreach (var id in d.Arr("members")) squad.Members.Add(SnapIO.ToI(id));
            return squad;
        }
    }

    public static class Squads
    {
        internal static bool ValidName(string name) => name != null && name.Length >= 1 && name.Length <= 24 && Text.Clean(name, 24) == name;
        public static List<object> View(World w, int team) => w.Teams[team].Squads.Values.OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => (object)s.View(w, team)).ToList();

        /// <summary>Recognizes even malformed/missing named selectors, so they can never fall through to all units.</summary>
        public static bool Selector(Dictionary<string, object> c, out string name)
        {
            name = null;
            if (c.TryGetValue("squad", out var raw))
            {
                if (!c.ContainsKey("units") && raw is string value && ValidName(value)) name = value;
                return true;
            }
            if (c.TryGetValue("units", out raw) && raw is string selector && selector.StartsWith("squad:", StringComparison.Ordinal))
            {
                var value = selector.Substring(6);
                if (ValidName(value)) name = value;
                return true;
            }
            return false;
        }

        public static List<Entity> Resolve(World w, int team, string name) => name != null && w.Teams[team].Squads.TryGetValue(name, out var squad)
            ? squad.Live(w, team).Where(e => !e.IsCarried).ToList() : new List<Entity>();

        static bool ReadIds(World w, int team, Dictionary<string, object> c, string field, out List<int> ids)
        {
            ids = new List<int>();
            if (!c.TryGetValue(field, out var raw)) return true;
            if (!(raw is List<object> values)) return false;
            foreach (var value in values)
            {
                if (!(value is double number) || number != Math.Floor(number) || number <= 0 || number > int.MaxValue) return false;
                int id = (int)number;
                var e = w.Get(id);
                if (e == null || e.Dead || e.Team != team || e.IsStructure || e.IsMine) return false;
                if (!ids.Contains(id)) ids.Add(id);
            }
            return true;
        }

        public static JObj Configure(World w, int team, Dictionary<string, object> c)
        {
            string name = c.Str("name");
            if (!ValidName(name)) return Fail("squad name must be 1-24 characters without surrounding whitespace or control characters");
            if (c.TryGetValue("disband", out var raw) && !(raw is bool)) return Fail("disband must be true or false");
            bool disband = raw is bool yes && yes;
            if (disband && (c.ContainsKey("add") || c.ContainsKey("remove"))) return Fail("disband cannot be combined with add or remove");
            if (!ReadIds(w, team, c, "add", out var add) || !ReadIds(w, team, c, "remove", out var remove))
                return Fail("squad add and remove must be arrays of live mobile unit ids belonging to your team");
            if (add.Intersect(remove).Any()) return Fail("a unit cannot be both added and removed in the same squad command");
            var squads = w.Teams[team].Squads;
            if (disband)
            {
                if (!squads.TryGetValue(name, out var gone)) return Fail("unknown squad '" + name + "'");
                SquadGuards.Release(w, gone.Live(w, team));
                squads.Remove(name);
                foreach (var building in w.Owned(team).Where(e => e.IsStructure && e.RallySquad == name)) building.RallySquad = null;
                return new JObj().Set("ok", true).Set("result", "squad '" + name + "' disbanded; existing orders stay in place");
            }
            if (!squads.TryGetValue(name, out var squad)) squads[name] = squad = new Squad { Name = name };
            var amended = new HashSet<Squad>();
            if (c.ContainsKey("add") || c.ContainsKey("remove")) amended.Add(squad);
            foreach (int id in add)
            {
                foreach (var other in squads.Values)
                    if (other != squad && other.Members.Remove(id))
                    {
                        amended.Add(other); other.Guards.Units.Remove(id); w.Get(id).GuardMotionPaused = false;
                    }
                squad.Members.Add(id);
            }
            foreach (int id in remove)
            {
                squad.Members.Remove(id); squad.Guards.Units.Remove(id); w.Get(id).GuardMotionPaused = false;
            }
            // Explicit amendments establish a new formation; deaths do not. Rally additions extend its saved baseline.
            foreach (var changed in amended) changed.FormationHp = changed.Live(w, team).Sum(e => e.Hp);
            return new JObj().Set("ok", true).Set("squad", squad.View(w, team)).Set("result", "squad '" + name + "' membership set; existing orders stay in place");
        }

        /// <summary>Enrollment for a newly spawned unit only. Existing membership is never transferred by production.</summary>
        internal static void EnrollSpawn(World w, Entity unit, string name)
        {
            if (name == null || unit.Dead || unit.IsStructure || unit.IsMine || unit.Team < 0 || unit.Team >= w.Teams.Count ||
                !w.Teams[unit.Team].Squads.TryGetValue(name, out var squad) || w.Teams[unit.Team].Squads.Values.Any(s => s.Members.Contains(unit.Id))) return;
            if (squad.Members.Add(unit.Id)) squad.FormationHp += unit.Hp;
        }

        public static void Remove(World w, Entity unit)
        {
            if (unit.Team < 0 || unit.Team >= w.Teams.Count) return;
            foreach (var squad in w.Teams[unit.Team].Squads.Values) { squad.Members.Remove(unit.Id); squad.Guards.Units.Remove(unit.Id); }
            unit.GuardMotionPaused = false;
        }

        static JObj Fail(string error) => new JObj().Set("ok", false).Set("error", error);
    }
}
