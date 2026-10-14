using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>Offline paired matches. The commander sees the public fogged data; only the metric observer sees deaths.</summary>
    public static class SlowAgent
    {
        internal const int TurnTicks = 30 * World.TickRate;
        internal const float AllyRadius = 8;
        internal const int SupportCount = 3;
        static readonly HashSet<string> CombatAlerts = new HashSet<string>
        {
            "harvester_under_attack", "units_ambushed", "base_under_attack", "enemy_near_base", "combat", "units_lost", "structure_lost"
        };

        public static int Run(string[] args)
        {
            string Arg(string key, string def) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
            string layer = Arg("--slow-agent-layer", "baseline");
            if (layer != "baseline" && layer != "miners" && layer != "squads") { Console.Error.WriteLine("--slow-agent-layer takes baseline, miners or squads."); return 2; }
            int minutes = int.Parse(Arg("--slow-agent-minutes", "12"), CultureInfo.InvariantCulture);
            int[] seeds = Arg("--slow-agent-seeds", "1,2,3").Split(',').Select(int.Parse).ToArray();
            if (minutes < 1 || seeds.Length == 0) { Console.Error.WriteLine("Use positive minutes and at least one seed."); return 2; }
            string output = Arg("--slow-agent-out", null);
            var rows = new List<object>();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < seeds.Length; i++)
                foreach (bool habits in i % 2 == 0 ? new[] { false, true } : new[] { true, false })
                    foreach (string opponent in new[] { "simple_ai", "self" })
                        foreach (int seat in opponent == "simple_ai" ? new[] { 0, 1 } : new[] { 0 })
                        {
                            var match = Match(seeds[i], habits, opponent, seat, minutes * 60, layer);
                            rows.Add(match);
                            Console.WriteLine(Json.Write(match));
                        }
            if (output != null)
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(output));
                Directory.CreateDirectory(dir);
                File.WriteAllLines(output, rows.Select(Json.Write));
            }
            Console.Error.WriteLine($"Slow-agent matrix: {rows.Count} matches, {sw.Elapsed.TotalSeconds:0.0}s wall time; 30s turns, {minutes} game-minute cap, no server or live ports.");
            return 0;
        }

        internal static JObj Match(int seed, bool habits, string opponent, int seat, float seconds, string layer = "baseline")
        {
            var controllers = new[] { "llm", "llm" };
            if (opponent == "simple_ai") controllers[1 - seat] = "ai";
            var game = new Game(new GameConfig { Seed = seed, MapSize = 80, Controllers = controllers, MatchHours = 0 });
            var commanders = controllers.Select((c, team) => c == "ai" ? null : new Commander(team, habits, layer)).ToArray();
            var metrics = commanders.Select(c => c == null ? null : new Metrics(c.Team, layer)).ToArray();
            var w = game.World;
            while (!w.GameOver && w.Time < seconds)
            {
                for (int team = 0; team < commanders.Length; team++)
                    if (commanders[team] != null) commanders[team].Tick(game, metrics[team]);
                // Own-unit positions immediately before the fatal step: at most one tick (50ms) before a death.
                var ownBefore = w.Entities.Where(e => !e.Dead && !e.IsStructure && e.IsArmed).Select(e => (e.Id, e.Team, e.Pos)).ToArray();
                long lastEvent = w.Events.Count == 0 ? 0 : w.Events.Last().Seq;
                game.Advance(World.Dt);
                foreach (var metric in metrics.Where(m => m != null))
                {
                    metric.Sample(w, lastEvent, ownBefore);
                    if (layer == "miners") metric.SampleExecutor(w);
                }
            }
            return new JObj().Set("layer", layer).Set("rules_version", StateView.RulesVersion).Set("seed", seed)
                .Set("variant", habits ? "p0_habits" : "plain").Set("opponent", opponent).Set("slow_seat", seat)
                .Set("map_size", 80).Set("turn_s", 30).Set("cap_s", seconds).Set("elapsed_s", w.Time)
                .Set("game_over", w.GameOver).Set("winner", w.Winner).Set("sim_errors", w.Errors)
                .Set("support_radius", AllyRadius).Set("support_n", SupportCount)
                .Set("seats", metrics.Where(m => m != null).Select(m => (object)m.Report(w, commanders[m.Team])).ToList());
        }

        internal sealed class Metrics
        {
            public readonly int Team;
            internal int MinerLosses, UndefendedMinerLosses, CombatLosses, UnsupportedCombatLosses;
            readonly Dictionary<long, float> pending = new Dictionary<long, float>();
            readonly HashSet<long> seen = new HashSet<long>();
            readonly List<float> delays = new List<float>();
            readonly string layer;
            readonly List<object> interventions = new List<object>();
            readonly List<float> hitDelays = new List<float>(), sightDelays = new List<float>();
            long executorCursor;
            int resumes, unmatched;
            public Metrics(int team, string layer = "baseline") { Team = team; this.layer = layer; }

            internal void Sample(World w, long after, (int Id, int Team, Vec2 Pos)[] before)
            {
                foreach (var a in w.Alerts.All.Where(a => a.Team == Team && CombatAlerts.Contains(a.Kind)))
                    if (seen.Add(a.Seq)) pending[a.Seq] = a.StartTick * World.Dt;
                foreach (var ev in w.Events.Where(e => e.Seq > after && e.Type == "destroyed" && e.Team == Team))
                {
                    var def = Defs.Get(ev.Key);
                    if (def == null || def.IsStructure) continue;
                    int allies = before.Count(e => e.Team == Team && e.Id != ev.A && Vec2.Dist(e.Pos, ev.Pos) <= AllyRadius);
                    if (def.HarvestCapacity > 0) { MinerLosses++; if (allies == 0) UndefendedMinerLosses++; }
                    if (def.Weapon != null && !def.IsMine) { CombatLosses++; if (allies < SupportCount) UnsupportedCombatLosses++; }
                }
            }

            internal void Respond(long alertSeq, float now)
            {
                if (!pending.TryGetValue(alertSeq, out var at)) return;
                delays.Add(now - at);
                pending.Remove(alertSeq);
            }

            internal void SampleExecutor(World w)
            {
                var log = w.Observe.Log(Team);
                foreach (var observation in log.After(executorCursor))
                {
                    if (observation.Kind == "miners_resumed") resumes++;
                    if (observation.Kind != "miners_withdrawn") continue;
                    var facts = observation.Body;
                    string reason = facts["reason"] as string;
                    float? trigger = facts["triggered_at_s"] == null ? null : Convert.ToSingle(facts["triggered_at_s"]);
                    float? delay = trigger.HasValue ? observation.Tick * World.Dt - trigger.Value : null;
                    if (delay.HasValue && reason == "hit") hitDelays.Add(delay.Value);
                    else if (delay.HasValue && reason == "seen_weapon_range") sightDelays.Add(delay.Value);
                    else unmatched++;
                    interventions.Add(new JObj().Set("tick", observation.Tick).Set("reason", reason)
                        .Set("triggered_at_s", trigger).Set("trigger_to_order_s", delay).Set("units", facts["units"]));
                }
                executorCursor = log.Head;
            }

            static JObj Latencies(List<float> values) => new JObj().Set("count", values.Count)
                .Set("mean_s", values.Count == 0 ? null : (object)values.Average())
                .Set("p50_s", Percentile(values, .5)).Set("p95_s", Percentile(values, .95));

            internal JObj Report(World w, Commander commander)
            {
                var result = new JObj().Set("team", Team)
                .Set("miner_losses", MinerLosses).Set("undefended_miner_losses", UndefendedMinerLosses)
                .Set("combat_losses", CombatLosses).Set("unsupported_combat_losses", UnsupportedCombatLosses)
                .Set("alerts", seen.Count).Set("responses", delays.Count).Set("unresolved", pending.Count)
                .Set("response_mean_s", delays.Count == 0 ? null : (object)delays.Average())
                .Set("response_p50_s", Percentile(delays, .5)).Set("response_p95_s", Percentile(delays, .95))
                .Set("response_delays_s", delays.Cast<object>().ToList())
                .Set("turns", commander.Turns).Set("commands", commander.Commands).Set("rejected_commands", commander.Rejected)
                .Set("score", w.Scores().FirstOrDefault(s => s.Team == Team)?.Score ?? 0)
                .Set("ore_mined", w.Teams[Team].Stats.OreMined).Set("units_lost", w.Teams[Team].Stats.UnitsLost);
                if (layer == "miners") result.Set("executor", new JObj().Set("withdrawals", interventions.Count).Set("resumes", resumes)
                    .Set("hit_triggered", Latencies(hitDelays)).Set("visible_range_triggered", Latencies(sightDelays))
                    .Set("unmatched", unmatched).Set("interventions", interventions));
                return result;
            }
        }

        internal static object Percentile(IEnumerable<float> values, double fraction)
        {
            var sorted = values.OrderBy(x => x).ToArray();
            return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
        }

        /// <summary>No World state is consulted to choose a plan: data and feed are exactly the player's surfaces.</summary>
        internal sealed class Commander
        {
            public readonly int Team;
            readonly bool habits;
            readonly string layer;
            readonly HashSet<int> retreated = new HashSet<int>(), rallied = new HashSet<int>();
            readonly HashSet<int> armyMembers = new HashSet<int>();
            internal const string ArmySquad = "army";
            bool squadReady;
            long cursor;
            string stream;
            int nextTurn;
            public int Turns, Commands, Rejected;
            public Commander(int team, bool habits, string layer = "baseline") { Team = team; this.habits = habits; this.layer = layer; }

            internal void Tick(Game game, Metrics metrics)
            {
                var w = game.World;
                if (w.Tick < nextTurn || w.Teams[Team].Defeated) return;
                nextTurn = w.Tick + TurnTicks;
                Turns++;
                var state = StateData.Team(w, Team);
                var units = Rows(state, "units").ToList();
                var structures = Rows(state, "structures").ToList();
                var own = (JObj)state["you"];
                var home = Point((JObj)own["start"]);
                if (Turns == 1 && layer == "miners")
                    Send(game, "type", "set_policy", "miners", new Dictionary<string, object>
                    {
                        ["withdraw_on_attack"] = true, ["to"] = new List<object> { (double)home.X, (double)home.Y }, ["resume_after_s"] = 20d
                    });
                var map = (JObj)state["map"];
                var centre = new Vec2(Number(map, "w") / 2, Number(map, "h") / 2);
                var staging = home + (centre - home).Normalized * 10;
                var log = w.Observe.Log(Team);
                var feed = log.Read(stream, cursor, new ObsFilter { Cats = new HashSet<string> { "alert" } }, 4000);
                stream = (string)feed["stream"]; cursor = Convert.ToInt64(feed["next_cursor"]);
                var alerts = Rows(feed, "events").Where(a => CombatAlerts.Contains((string)a["kind"])).ToList();

                bool Has(string key) => structures.Any(s => (string)s["type"] == key);
                var options = Rows((JObj)state["build_options"], "now").Select(o => (string)o["type"]).ToHashSet();
                // Fixed economy plan, including copper income for tanks. Auto placement is a public command option.
                string build = null;
                if (Number((JObj)own["power"], "produced") < Number((JObj)own["power"], "used") + 35) build = "power_plant";
                else build = new[] { "mining_refinery", "barracks", "factory" }.FirstOrDefault(k => !Has(k));
                if (build != null && options.Contains(build)) Send(game, "type", "build", "structure", build);
                var production = (JObj)state["production"];
                int Queue(string producer) => Rows(production, producer).Count();
                int Trucks() => units.Count(u => (string)u["type"] == "mining_truck");
                if (Trucks() + Queue("command_center") < 6 && options.Contains("mining_truck")) Send(game, "type", "train", "unit", "mining_truck");
                if (Has("factory") && Queue("factory") < 2 && options.Contains("light_tank")) Send(game, "type", "train", "unit", "light_tank", "count", 2);
                if (Has("barracks") && Queue("barracks") < 3 && options.Contains("rifleman")) Send(game, "type", "train", "unit", "rifleman", "count", 3);
                foreach (var truck in units.Where(u => (string)u["type"] == "mining_truck" && (string)u["order"] == "idle"))
                    Send(game, "type", "harvest", "units", Ids(new[] { truck }), "ore", Id(truck) % 3 == 0 ? "copper_ore" : "iron_ore");

                var army = units.Where(u => Defs.Get((string)u["type"])?.Weapon != null).ToList();
                if (layer == "squads")
                {
                    var squad = Rows(state, "squads").FirstOrDefault(s => (string)s["name"] == ArmySquad);
                    armyMembers.Clear();
                    if (squad != null) foreach (var id in (List<object>)squad["members"]) armyMembers.Add(Convert.ToInt32(id));
                    squadReady = squad != null;
                    var additions = army.Where(u => !armyMembers.Contains(Id(u))).ToList();
                    if ((!squadReady || additions.Count > 0) && Send(game, "type", "squad", "name", ArmySquad, "add", Ids(additions)))
                    {
                        squadReady = true;
                        foreach (var u in additions) armyMembers.Add(Id(u));
                    }
                }
                if (habits)
                {
                    var fresh = units.Where(u => !retreated.Contains(Id(u))).ToList();
                    if (fresh.Count > 0 && Send(game, "type", "set_retreat", "units", Ids(fresh), "below_pct", 40))
                        foreach (var u in fresh) retreated.Add(Id(u));
                    foreach (var s in structures.Where(s => Defs.Get((string)s["type"])?.Produces is Producer.Barracks or Producer.Factory))
                        if (!rallied.Contains(Id(s)))
                        {
                            var rally = new List<object> { "type", "rally", "structure_id", Id(s), "x", staging.X, "y", staging.Y };
                            if (layer == "squads") { rally.Add("squad"); rally.Add(ArmySquad); }
                            if (Send(game, rally.ToArray())) rallied.Add(Id(s));
                        }
                }

                var responded = new List<JObj>();
                foreach (var alert in alerts.OrderByDescending(a => Number(a, "tick")))
                {
                    var at = ArrayPoint(alert["pos"]);
                    bool response = false;
                    if ((string)alert["kind"] == "harvester_under_attack")
                    {
                        var miners = units.Where(u => (string)u["type"] == "mining_truck" && Vec2.Dist(Point(u), at) <= 12).ToList();
                        if (miners.Count > 0) response = Send(game, "type", "move", "units", Ids(miners), "x", home.X, "y", home.Y);
                    }
                    if (army.Count > 0) response |= Advance(game, army, at);
                    if (response)
                        foreach (var sameFight in alerts.Where(a => Vec2.Dist(ArrayPoint(a["pos"]), at) <= 12))
                        {
                            responded.Add(sameFight);
                            metrics.Respond(Convert.ToInt64(sameFight["alert_seq"]), w.Time);
                        }
                    // One chosen defence location per turn. Other locations need their own response, not an acknowledgement.
                    if (response) break;
                }
                if (responded.Count == 0)
                {
                    var idle = army.Where(u => (string)u["order"] == "idle").ToList();
                    if (idle.Count >= 4)
                    {
                        var enemies = Rows(state, "enemies").Concat(Rows(state, "remembered_enemy_structures"))
                            .Where(e => (string)e["type"] != "derrick").OrderBy(e => Vec2.Dist(Point(e), home)).ToList();
                        // Public map geometry, never another team's StartPos or hidden structure.
                        var goal = enemies.Count > 0 ? Point(enemies[0]) : new Vec2(Number(map, "w") - home.X, Number(map, "h") - home.Y);
                        Advance(game, idle, goal);
                    }
                }
            }

            internal object ArmySelector(List<JObj> units) => squadReady && armyMembers.SetEquals(units.Select(Id)) ? (object)("squad:" + ArmySquad) : Ids(units);
            bool Advance(Game game, List<JObj> units, Vec2 at) => Send(game, "type", "attack_move", "units", ArmySelector(units), "x", at.X, "y", at.Y, "together", habits);
            bool Send(Game game, params object[] kv)
            {
                var command = new Dictionary<string, object>();
                for (int i = 0; i < kv.Length; i += 2) command[(string)kv[i]] = kv[i + 1] is int n ? (double)n : kv[i + 1] is float f ? (double)f : kv[i + 1];
                Commands++;
                var result = (JObj)((List<object>)CommandSafety.ExecuteBatch(game, Team, command, "slow-agent")["results"])[0];
                bool ok = result["ok"] is bool b && b;
                if (!ok) Rejected++;
                return ok;
            }
        }

        static IEnumerable<JObj> Rows(JObj obj, string key) => obj[key] is List<object> rows ? rows.Cast<JObj>() : Enumerable.Empty<JObj>();
        static float Number(JObj obj, string key) => obj[key] == null ? 0 : Convert.ToSingle(obj[key]);
        static int Id(JObj obj) => Convert.ToInt32(obj["id"]);
        static Vec2 Point(JObj obj) => new Vec2(Number(obj, "x"), Number(obj, "y"));
        static Vec2 ArrayPoint(object value) { var p = (List<object>)value; return new Vec2(Convert.ToSingle(p[0]), Convert.ToSingle(p[1])); }
        static List<object> Ids(IEnumerable<JObj> units) => units.Select(u => (object)(double)Id(u)).ToList();
    }
}
