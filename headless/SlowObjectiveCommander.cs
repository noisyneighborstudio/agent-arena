using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class SlowAgent
    {
        static JObj Object(object value)
        {
            if (value is JObj obj) return obj;
            var result = new JObj();
            if (value is Dictionary<string, object> dictionary) foreach (var pair in dictionary) result.Set(pair.Key, pair.Value);
            return result;
        }
        static int Count(JObj obj, string key) => obj[key] is List<object> list ? list.Count : 0;
        static bool Yes(JObj obj, string key) => obj[key] is bool yes && yes;
        static Vec2 ObjectivePoint(object value) => ArrayPoint(Json.Parse(Json.Write(value)));
        static Dictionary<string, object> PointCommand(Vec2 at) => new Dictionary<string, object> { ["at"] = new List<object> { (double)at.X, (double)at.Y } };

        internal sealed partial class Commander
        {
            internal const string Mission = "army-go";
            readonly Dictionary<string, int> retiredObjectiveSpend = new Dictionary<string, int>();
            readonly Dictionary<string, int> objectiveRejections = new Dictionary<string, int>();
            bool reinforcementDisabled;

            void RecordObjectiveRejection(Dictionary<string, object> command, JObj result)
            {
                string reason = command.Str("type") + ": " + (result["error"] ?? result["reason"] ?? "unspecified");
                objectiveRejections[reason] = objectiveRejections.TryGetValue(reason, out int count) ? count + 1 : 1;
            }
            internal JObj ObjectiveRejections() => objectiveRejections.Aggregate(new JObj(), (o, p) => o.Set(p.Key, p.Value));

            internal static Dictionary<string, int> ObjectiveCaps()
            {
                var caps = new Dictionary<string, int> { ["steel"] = 800 };
                foreach (string key in new[] { "rifleman", "light_tank" })
                {
                    var cost = Defs.Get(key).Cost;
                    foreach (var price in cost.Where(p => p.Key != "steel"))
                    {
                        int cap = 800 / cost["steel"] * price.Value;
                        caps[price.Key] = Math.Max(caps.TryGetValue(price.Key, out int prior) ? prior : 0, cap);
                    }
                }
                return caps;
            }

            void TrainAtKnownProducer(Game game, List<JObj> structures, string key, int count)
            {
                var producer = layer == "objectives" ? structures.FirstOrDefault(s => (string)s["type"] == Defs.ProducerKey(Defs.Get(key).BuiltBy) && Yes(s, "complete")) : null;
                if (producer == null) Send(game, "type", "train", "unit", key, "count", count);
                else Send(game, "type", "train", "unit", key, "count", count, "structure_id", Id(producer));
            }

            static Vec2 KnownGoal(JObj state, Vec2 home)
            {
                var enemies = Rows(state, "enemies").Concat(Rows(state, "remembered_enemy_structures"))
                    .Where(e => (string)e["type"] != "derrick").OrderBy(e => Vec2.Dist(Point(e), home)).ToList();
                var map = Object(state["map"]);
                return enemies.Count > 0 ? Point(enemies[0]) : new Vec2(Number(map, "w") - home.X, Number(map, "h") - home.Y);
            }

            static bool ProducersPresent(List<JObj> structures) => new[] { "barracks", "factory" }.All(key =>
                structures.Any(s => (string)s["type"] == key && Yes(s, "complete")));

            static bool AssemblyReleased(JObj objective) => objective["assemble"] == null || Yes(Object(objective["assemble"]), "released");
            static JObj Spent(JObj objective) => Object(Object(objective?["reinforce"])["spent"]);

            internal JObj ObjectiveBudget(World w)
            {
                var current = Rows(StateData.Team(w, Team), "objectives").FirstOrDefault(o => (string)o["id"] == Mission);
                var spent = Spent(current);
                var total = new JObj(); var remaining = new JObj();
                foreach (var cap in ObjectiveCaps())
                {
                    int value = (retiredObjectiveSpend.TryGetValue(cap.Key, out int retired) ? retired : 0) + (int)Number(spent, cap.Key);
                    total.Set(cap.Key, value); remaining.Set(cap.Key, Math.Max(0, cap.Value - value));
                }
                return new JObj().Set("caps", ObjectiveCaps().Aggregate(new JObj(), (o, p) => o.Set(p.Key, p.Value)))
                    .Set("spent", total).Set("remaining", remaining).Set("reinforcement_disabled", reinforcementDisabled);
            }

            bool SetMission(Game game, JObj previous, Vec2 at, Vec2 staging, bool reinforce)
            {
                var command = new List<object> { "type", "objective", "id", Mission, "squad", ArmySquad, "verb", "go",
                    "at", new List<object> { (double)at.X, (double)at.Y }, "radius", 6,
                    "wake", new List<object> { "squad_below_50", "enemy_strength_seen>squad", "objective_done" } };
                if (previous == null || !AssemblyReleased(previous))
                {
                    var assembly = PointCommand(staging); assembly["min"] = 8d;
                    command.Add("assemble"); command.Add(assembly);
                }
                var spent = Spent(previous);
                var remaining = new Dictionary<string, object>();
                foreach (var cap in ObjectiveCaps()) remaining[cap.Key] = (double)Math.Max(0, cap.Value -
                    (retiredObjectiveSpend.TryGetValue(cap.Key, out int retired) ? retired : 0) - (int)Number(spent, cap.Key));
                if (reinforce && (double)remaining["steel"] >= Defs.Get("rifleman").Cost["steel"])
                {
                    command.Add("reinforce"); command.Add(new Dictionary<string, object>
                    {
                        ["keep"] = new Dictionary<string, object> { ["rifleman"] = 6d, ["light_tank"] = 2d }, ["budget"] = remaining
                    });
                }
                if (!Send(game, command.ToArray())) return false;
                foreach (var cap in ObjectiveCaps()) retiredObjectiveSpend[cap.Key] =
                    (retiredObjectiveSpend.TryGetValue(cap.Key, out int retired) ? retired : 0) + (int)Number(spent, cap.Key);
                return true;
            }

            bool CanResume(JObj state, JObj objective, List<JObj> army, List<JObj> structures)
            {
                string reason = objective["reason"] as string;
                if (reason == "objective_done") return true;
                if (reason == "native_retreat")
                {
                    int id = (int)Number(Object(Object(objective["decision"])["facts"]), "unit");
                    var unit = Rows(state, "units").FirstOrDefault(u => Id(u) == id);
                    return unit == null || (string)unit["order"] == "idle" || unit["order_x"] != null &&
                        Vec2.Dist(Point(unit), new Vec2(Number(unit, "order_x"), Number(unit, "order_y"))) <= .75f;
                }
                if (reason == "squad_gone" || reason == "all_members_detached") return army.Count >= 8;
                if (reason == "squad_below_50") return Number(Rows(state, "squads").First(s => (string)s["name"] == ArmySquad), "strength_pct") >= 50;
                if (reason == "enemy_strength_seen>squad")
                {
                    if (army.Count == 0) return false;
                    var centre = new Vec2(army.Average(u => Number(u, "x")), army.Average(u => Number(u, "y")));
                    float enemyHp = Rows(state, "enemies").Where(e => !Yes(e, "structure") && Defs.Get((string)e["type"])?.Weapon != null &&
                        Vec2.Dist(Point(e), centre) <= 6).Sum(e => Number(e, "hp"));
                    return enemyHp <= army.Sum(u => Number(u, "hp"));
                }
                if (reason == "reinforcement_producer_unavailable")
                {
                    var ids = structures.Select(Id).ToHashSet();
                    var production = Object(state["production"]);
                    return ProducersPresent(structures) && new[] { "barracks", "factory" }.All(key => Rows(production, key)
                        .All(p => p["at"] == null || ids.Contains(Convert.ToInt32(p["at"]))));
                }
                return false; // no implicit slot relocation, tech changes or refunding a lost producer's paid queue
            }

            void ObjectiveTurn(Game game, JObj state, List<JObj> events, List<JObj> army, List<JObj> alerts, List<JObj> structures,
                Vec2 home, Vec2 staging, Metrics metrics)
            {
                var w = game.World;
                var current = Rows(state, "objectives").FirstOrDefault(o => (string)o["id"] == Mission);
                var packet = events.LastOrDefault(e => (string)e["kind"] == "decision_needed" && (string)e["objective_id"] == Mission);
                var latestAlert = alerts.OrderByDescending(a => Number(a, "tick")).FirstOrDefault();
                bool minerResponse = false;
                if (latestAlert != null && (string)latestAlert["kind"] == "harvester_under_attack")
                {
                    var at = ArrayPoint(latestAlert["pos"]);
                    var miners = Rows(state, "units").Where(u => (string)u["type"] == "mining_truck" && Vec2.Dist(Point(u), at) <= 12).ToList();
                    if (miners.Count > 0) minerResponse = Send(game, "type", "move", "units", Ids(miners), "x", home.X, "y", home.Y);
                }
                if (minerResponse) foreach (var alert in alerts.Where(a => Vec2.Dist(ArrayPoint(a["pos"]), ArrayPoint(latestAlert["pos"])) <= 12))
                    metrics.Respond(Convert.ToInt64(alert["alert_seq"]), w.Time);

                if (current != null && Yes(current, "paused"))
                {
                    bool response = false; string action = "hold_paused";
                    if ((string)current["reason"] == "budget_exhausted")
                    {
                        response = SetMission(game, current, ObjectivePoint(current["at"]), staging, reinforce: false);
                        if (response) { reinforcementDisabled = true; action = "same_mission_without_new_budget"; }
                    }
                    else if (CanResume(state, current, army, structures))
                    {
                        response = Send(game, "type", "objective", "id", Mission, "resume", true);
                        action = (string)current["reason"] == "objective_done" ? "resume_completed_go_as_hold" : "resume_same_mission";
                    }
                    else if (packet != null) response = Send(game, "type", "objective", "id", Mission, "pause", true);
                    if (response && packet != null) metrics.ObjectiveRespond(Convert.ToInt64(packet["seq"]), w.Time, action);
                    return;
                }
                if (current == null)
                {
                    if (squadReady && army.Count >= 4 && ProducersPresent(structures)) SetMission(game, null, KnownGoal(state, home), staging, reinforce: true);
                    return;
                }
                if (latestAlert != null && army.Count > 0)
                {
                    var at = ArrayPoint(latestAlert["pos"]);
                    bool changed = Vec2.Dist(at, ObjectivePoint(current["at"])) > 1;
                    bool response = changed && SetMission(game, current, at, staging, reinforce: !reinforcementDisabled);
                    foreach (var alert in alerts.Where(a => Vec2.Dist(ArrayPoint(a["pos"]), at) <= 12))
                    {
                        if (response) metrics.Respond(Convert.ToInt64(alert["alert_seq"]), w.Time);
                        else if (!changed) metrics.CoveredObjectiveAlert(Convert.ToInt64(alert["alert_seq"]));
                    }
                }
                else if (Yes(current, "done"))
                {
                    var next = KnownGoal(state, home);
                    if (Vec2.Dist(next, ObjectivePoint(current["at"])) > 6) SetMission(game, current, next, staging, reinforce: !reinforcementDisabled);
                }
            }
        }

        internal sealed partial class Metrics
        {
            internal int ObjectiveTurnTick;
            internal int ObjectiveHandbacks;
            readonly List<object> objectiveEvents = new List<object>(), objectiveResponses = new List<object>();
            readonly Dictionary<long, float> pendingDecisions = new Dictionary<long, float>();
            readonly Dictionary<string, int> objectiveReasons = new Dictionary<string, int>();
            readonly Dictionary<string, int> pendingObjectiveStart = new Dictionary<string, int>();
            readonly HashSet<long> coveredObjectiveAlerts = new HashSet<long>();
            readonly List<float> objectiveResponseDelays = new List<float>(), activationDelays = new List<float>();
            int objectiveOrders, betweenTurnOrders, objectiveDecisions, assemblyReleases;

            internal void CoveredObjectiveAlert(long seq) => coveredObjectiveAlerts.Add(seq);
            internal void ObjectiveRespond(long seq, float now, string action)
            {
                if (!pendingDecisions.TryGetValue(seq, out float at)) return;
                objectiveResponseDelays.Add(now - at); pendingDecisions.Remove(seq);
                objectiveResponses.Add(new JObj().Set("packet_seq", seq).Set("t", now).Set("action", action).Set("delay_s", now - at));
            }

            void SampleObjective(Observation observation)
            {
                var body = observation.Body ?? new JObj(); string id = body["objective_id"] as string;
                if (id == null) return;
                if (observation.Kind == "objective_started" || observation.Kind == "objective_resumed") pendingObjectiveStart[id] = observation.Tick;
                if (observation.Kind == "objective_phase" && (string)body["reason"] == "assembled") assemblyReleases++;
                if (observation.Kind == "objective_order")
                {
                    objectiveOrders++; if (observation.Tick > ObjectiveTurnTick) betweenTurnOrders++;
                    if (pendingObjectiveStart.TryGetValue(id, out int start))
                    { activationDelays.Add((observation.Tick - start) * World.Dt); pendingObjectiveStart.Remove(id); }
                    objectiveEvents.Add(new JObj().Set("kind", observation.Kind).Set("tick", observation.Tick).Set("objective_id", id)
                        .Set("unit", body["unit"]).Set("order", body["order"]).Set("goal", body["goal"])
                        .Set("since_commander_s", (observation.Tick - ObjectiveTurnTick) * World.Dt));
                }
                if (observation.Kind != "decision_needed") return;
                objectiveDecisions++; pendingDecisions[observation.Seq] = observation.Tick * World.Dt;
                string reason = body["reason"] as string ?? "unknown";
                objectiveReasons[reason] = objectiveReasons.TryGetValue(reason, out int count) ? count + 1 : 1;
                var changed = Object(body["changed_since_command"]); var threats = Object(body["threats"]); var commitments = Object(body["commitments"]);
                objectiveEvents.Add(new JObj().Set("kind", observation.Kind).Set("seq", observation.Seq).Set("tick", observation.Tick)
                    .Set("objective_id", id).Set("reason", reason).Set("triggered_at_s", body["triggered_at_s"]).Set("trigger_basis", body["trigger_basis"])
                    .Set("facts_fields", Object(body["facts"]).Count).Set("lost_units", Count(changed, "lost_units")).Set("hp_changes", Count(changed, "hp"))
                    .Set("visible_threats", Count(threats, "visible")).Set("remembered_threats", Count(threats, "remembered"))
                    .Set("other_objectives", Count(commitments, "other_objectives")).Set("production_commitments", Count(commitments, "production"))
                    .Set("construction_commitments", Count(commitments, "construction")).Set("reserve_resources", Object(commitments["reserves"]).Count)
                    .Set("facts", body["facts"]).Set("changed_since_command", changed).Set("threats", threats).Set("commitments", commitments));
            }

            JObj ObjectiveReport(World w, Commander commander) => new JObj().Set("objective_orders", objectiveOrders).Set("between_turn_orders", betweenTurnOrders)
                .Set("assembly_releases", assemblyReleases).Set("decision_packets", objectiveDecisions).Set("packet_responses", objectiveResponseDelays.Count)
                .Set("unresolved_packets", pendingDecisions.Count).Set("packet_response_latency", Latencies(objectiveResponseDelays))
                .Set("activation_to_order_latency", Latencies(activationDelays)).Set("covered_by_existing_intent", coveredObjectiveAlerts.Count)
                .Set("reasons", objectiveReasons.Aggregate(new JObj(), (o, p) => o.Set(p.Key, p.Value)))
                .Set("budget", commander.ObjectiveBudget(w)).Set("own_movement_settles", ownSettles).Set("settle_events", settleEvents)
                .Set("fresh_retreat_handbacks", ObjectiveHandbacks)
                .Set("rejected_command_reasons", commander.ObjectiveRejections())
                .Set("events", objectiveEvents).Set("responses", objectiveResponses);
        }
    }
}
