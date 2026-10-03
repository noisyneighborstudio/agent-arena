using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public enum Priority { Medium = 0, High = 1, Critical = 2 }

    /// <summary>
    /// A situation a commander should react to now ("base under attack at 14,9"). Repeated hits in the
    /// same area merge into one alert, so a long fight raises one alert, not hundreds.
    /// </summary>
    public class Alert
    {
        public long Seq;              // increases only when a genuinely new alert is raised
        public int Team;
        public string Kind;           // base_under_attack, structure_lost, harvester_under_attack, units_ambushed, enemy_near_base, stealth_detected, combat, units_lost
        public Priority Priority;
        public Vec2 Pos;
        public int StartTick, LastTick;
        public int Count;
        public readonly HashSet<int> Victims = new HashSet<int>();
        public readonly HashSet<int> Attackers = new HashSet<int>();
        public readonly List<string> Lost = new List<string>();
        /// <summary>A running total some alerts carry (salvage_dropped: ore of salvage left in the area).</summary>
        public float Amount;
    }

    public partial class AlertLog
    {
        public const float MergeRadius = 9f;
        public const float MergeWindow = 8f;     // seconds of quiet before the same area raises a fresh alert
        public const float ActiveWindow = 20f;   // how long an alert stays in get_state after its last update
        public readonly List<Alert> All = new List<Alert>();
        readonly List<Alert> open = new List<Alert>(); // alerts still inside their merge window
        long nextSeq = 1;

        public long LastSeq => nextSeq - 1;

        public Alert Raise(World w, int team, string kind, Priority p, Vec2 pos, Entity victim = null, Entity attacker = null, bool hit = true)
        {
            open.RemoveAll(x => (w.Tick - x.LastTick) * World.Dt > MergeWindow);
            Alert a = null;
            for (int i = open.Count - 1; i >= 0; i--)
            {
                var x = open[i];
                if (x.Team == team && x.Kind == kind && Vec2.Dist(x.Pos, pos) <= MergeRadius) { a = x; break; }
            }
            if (a == null)
            {
                a = new Alert { Seq = nextSeq++, Team = team, Kind = kind, Priority = p, Pos = pos, StartTick = w.Tick };
                All.Add(a);
                open.Add(a);
                if (All.Count > 500) All.RemoveRange(0, All.Count - 500);
            }
            else if (p > a.Priority) a.Priority = p;
            a.LastTick = w.Tick;
            if (hit) a.Count++;
            if (victim != null) a.Victims.Add(victim.Id);
            if (attacker != null) a.Attackers.Add(attacker.Id);
            return a;
        }

        /// <summary>
        /// A fresh alert in the same place as one the player has already seen (still active, at least as serious) is
        /// the same fight going on, not news: it shouldn't cut a wait short. A fight produces a stream of these.
        /// </summary>
        public bool IsContinuation(World w, Alert a, long seenSeq)
        {
            foreach (var x in All)
                if (x.Team == a.Team && x.Seq <= seenSeq && x.Priority >= a.Priority &&
                    (w.Tick - x.LastTick) * World.Dt < 30f && Vec2.Dist(x.Pos, a.Pos) <= 12f)
                    return true;
            return false;
        }

        public void ClearTeam(int team)
        {
            All.RemoveAll(a => a.Team == team);
            open.RemoveAll(a => a.Team == team);
        }

        public IEnumerable<Alert> Active(World w, int team) =>
            All.Where(a => a.Team == team && (w.Tick - a.LastTick) * World.Dt <= ActiveWindow)
               .OrderByDescending(a => a.Priority).ThenByDescending(a => a.LastTick);

        /// <summary>Seconds after its last update that an alert stops being news (a player back after hours isn't handed hours of history).</summary>
        public const float StaleAfter = 120f;

        public IEnumerable<Alert> Since(World w, int team, long seq, Priority min) =>
            All.Where(a => a.Team == team && a.Seq > seq && a.Priority >= min && (w.Tick - a.LastTick) * World.Dt <= StaleAfter);

        public static string Label(string kind) => kind switch
        {
            "base_under_attack" => "BASE UNDER ATTACK",
            "structure_lost" => "STRUCTURE LOST",
            "harvester_under_attack" => "MINING TRUCKS UNDER ATTACK",
            "units_ambushed" => "UNITS UNDER ATTACK",
            "enemy_near_base" => "ENEMY NEAR BASE",
            "stealth_detected" => "STEALTH BOMBER DETECTED",
            "units_lost" => "UNITS LOST",
            "salvage_available" => "SALVAGE AVAILABLE",
            "protection_ended" => "YOUR NEWCOMER PROTECTION HAS ENDED",
            "units_stranded" => "VEHICLE OUT OF FUEL",
            "aircraft_crashed" => "AIRCRAFT CRASHED (OUT OF FUEL)",
            "low_fuel" => "LOW FUEL, NOWHERE TO REFUEL",
            "air_contact" => "ENEMY AIRCRAFT ON RADAR",
            "stalled" => "NO WAY TO MAKE PROGRESS",
            "surface_ore_exhausted" => "SURFACE ORE RUNNING OUT",
            "arena_cleared" => "ARENA CLEARED: YOU WON THIS ROUND",
            "stockpile_lost" => "STOCKPILE SPILLED: LAST COMMAND CENTER LOST",
            "deep_mine_depleted" => "DEEP DEPOSIT USED UP",
            "building_burning" => "BUILDING ON FIRE",
            "deep_mine_running_low" => "DEEP DEPOSIT RUNNING LOW",
            "salvage_dropped" => "SALVAGE ON THE FIELD",
            "match_clock" => "MATCH CLOCK",
            _ => "COMBAT",
        };

        /// <summary>Human/LLM-readable description, built from the current state so it's never stale.</summary>
        public static string Describe(World w, Alert a)
        {
            var parts = new List<string>();
            float ago = (w.Tick - a.LastTick) * World.Dt;
            parts.Add($"{a.Priority.ToString().ToUpperInvariant()} {Label(a.Kind)} at ({(int)a.Pos.X},{(int)a.Pos.Y}) sector {StateView.Sector(w.Map, a.Pos)}, {(ago < 1 ? "now" : $"{ago:0}s ago")}" +
                      (a.Count > 1 ? $", {a.Count} hits since {a.StartTick * World.Dt:0}s" : ""));

            var victims = a.Victims.Select(w.Get).Where(e => e != null).ToList();
            if (victims.Count > 0)
                parts.Add("yours hit: " + string.Join(", ", victims.Take(6).Select(e => $"{e.Def.Key} #{e.Id} hp {(int)e.Hp}/{e.Def.MaxHp}")) + (victims.Count > 6 ? $" +{victims.Count - 6} more" : ""));
            if (a.Kind == "building_burning")
                parts.Add($"it loses health until it's repaired above {(int)(World.BurnBelow * 100)}% (send a repair truck: repair command), or it burns down within about a minute");
            if (a.Kind == "salvage_dropped")
                parts.Add($"{a.Count} wreck(s) left about {(int)a.Amount} ore of salvage on the ground here; anyone's mining trucks can collect it, so whoever holds the ground gets it");
            if (a.Lost.Count > 0) parts.Add("lost: " + string.Join(", ", a.Lost.Take(6)) + (a.Lost.Count > 6 ? $" +{a.Lost.Count - 6} more" : ""));

            // Enemies now visible around the alert, grouped by type.
            var foes = w.Entities.Where(e => !e.Dead && e.Team != a.Team && e.Team >= 0 && !e.IsStructure && w.IsVisibleTo(a.Team, e) && Vec2.Dist(e.Pos, a.Pos) <= 12f).ToList();
            if (foes.Count > 0)
                parts.Add("enemies there: " + string.Join(", ", foes.GroupBy(e => e.Def.Key).Select(g => $"{g.Count()}x {g.Key} ({string.Join(",", g.Take(4).Select(e => "#" + e.Id))}{(g.Count() > 4 ? ",…" : "")})")));
            else if (a.Kind != "units_lost" && a.Kind != "structure_lost" && a.Kind != "building_burning" && a.Kind != "salvage_dropped" && a.Kind != "match_clock") parts.Add("attackers not currently visible");

            // Who could respond: own combat units within 18 tiles, closest first.
            var near = w.Entities.Where(e => !e.Dead && e.Team == a.Team && !e.IsStructure && e.IsArmed && Vec2.Dist(e.Pos, a.Pos) <= 18f)
                         .OrderBy(e => Vec2.Dist(e.Pos, a.Pos)).ToList();
            if (near.Count > 0)
                parts.Add("your combat units within 18 tiles: " + string.Join(", ", near.Take(8).Select(e => $"#{e.Id} {e.Def.Key} ({e.OrderName}, {Vec2.Dist(e.Pos, a.Pos):0} tiles)")) + (near.Count > 8 ? $" +{near.Count - 8} more" : ""));
            else parts.Add("no combat units of yours within 18 tiles");
            return string.Join("; ", parts);
        }
    }
}
