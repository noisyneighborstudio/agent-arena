using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static (Game game, List<Entity> units) ObjectiveScene(params string[] keys) => GuardScene(keys);
        static JObj ObjectiveConfig(World w, params object[] fields)
        {
            var supplied = Cmd(fields);
            if (supplied.Str("action") is string action) { supplied.Remove("action"); supplied[action] = true; }
            var config = Cmd("type", "objective", "id", supplied.Str("id", "west-plan"));
            bool control = new[] { "pause", "resume", "cancel" }.Any(key => supplied.TryGetValue(key, out var value) && value is bool yes && yes);
            if (control)
            {
                foreach (var key in new[] { "pause", "resume", "cancel" }) if (supplied.TryGetValue(key, out var value)) config[key] = value;
            }
            else
            {
                config["squad"] = "west"; config["verb"] = "hold"; config["radius"] = 6d;
                foreach (var field in supplied) config[field.Key] = field.Value;
                if ((config.Str("verb") == "hold" || config.Str("verb") == "go") && !config.ContainsKey("at")) config["at"] = new List<object> { 40d, 30d };
            }
            return Commands.Execute(w, 0, config);
        }

        static Dictionary<string, object> ReinforceSpec(int count, int steel, string unit = "rifleman") => Cmd("keep", Cmd(unit, count), "budget", Cmd("steel", steel));
        static JObj ReinforceConfig(World w, params object[] fields)
        {
            // Starting from no members requires explicit assembly; reinforcement never invents it.
            var config = new List<object> { "assemble", Cmd("at", new List<object> { 40d, 30d }, "min", 1) };
            config.AddRange(fields); return ObjectiveConfig(w, config.ToArray());
        }
        static Objective ReinforceObjective(World w) => Objectives.ForSquad(w, 0, "west");
        static List<ProdItem> ReinforceQueue(World w) => w.Teams[0].UnitQueues.Values.SelectMany(q => q).Where(p => p.ObjectiveId != null).ToList();
        static Entity ReinforceProducer(World w, string type = "barracks", bool rally = true)
        {
            var producer = w.SpawnStructure(0, type, w.FindPlacement(0, type).Value, 1f);
            if (rally) Commands.Execute(w, 0, Cmd("type", "rally", "structure_id", producer.Id, "squad", "west", "x", 40, "y", 30));
            return producer;
        }
        static void ReinforceStock(World w, int steel = 10000, int copper = 10000)
        {
            w.Teams[0].Stock.Clear(); w.Teams[0].Granted.Clear();
            w.Teams[0].Stock["steel"] = steel; w.Teams[0].Stock["copper"] = copper;
        }

        static void ObjectiveReinforcementTests()
        {
            ReinforceValidation(); ReinforceDefaults(); ReinforceCountsAndSpending(); ReinforcePartialAndWaiting();
            ReinforceOrdinaryCommitments(); ReinforceOrdinaryReservations(); ReinforceLostProducer(); ReinforceChangedRally();
            ReinforceReplacedCommitment(); ReinforcePersistence();
        }

        static void ReinforceValidation()
        {
            var (game, _) = ObjectiveScene(); var w = game.World;
            var example = Cmd("keep", Cmd("rifleman", 6, "light_tank", 2), "budget", Cmd("steel", 800));
            Check(ObjectiveReinforcements.Parse(w, 0, example, out var plan, out _) && plan.Keep.Count == 2 && plan.Budget.Count == 1,
                  "the documented steel-only reinforcement budget bounds riflemen and tanks without inventing a copper cap");
            Check(!ObjectiveReinforcements.Parse(w, 0, ReinforceSpec(1, 800, "mining_truck"), out _, out _), "a kept type whose costs intersect no supplied cap is rejected");
            Check(!ObjectiveReinforcements.Parse(w, 0, Cmd("keep", Cmd("rifleman", 1), "budget", Cmd("unknown-resource", 800)), out _, out _) &&
                  !ObjectiveReinforcements.Parse(w, 0, ReinforceSpec(1, 800, "barracks"), out _, out _) &&
                  !ObjectiveReinforcements.Parse(w, 0, Cmd("keep", Cmd("rifleman", 1.5f), "budget", Cmd("steel", 800)), out _, out _),
                  "unknown resources, structures and fractional keep counts are rejected");
            Check(!ObjectiveReinforcements.Parse(w, 0, Cmd("keep", Cmd("rifleman", 1), "budget", Cmd("steel", -1)), out _, out _) &&
                  !ObjectiveReinforcements.Parse(w, 0, Cmd("keep", Cmd("rifleman", 1), "budget", Cmd()), out _, out _), "reinforcement requires explicit nonempty nonnegative integer caps");
            Check(ObjectiveReinforcements.Parse(w, 0, ReinforceSpec(0, 0), out var zero, out _) && zero.Keep["rifleman"] == 0,
                  "explicit zero targets and zero caps do not enable any implicit quantity");
        }

        static void ReinforceDefaults()
        {
            var (game, _) = ObjectiveScene("rifleman"); var w = game.World;
            ReinforceProducer(w); ReinforceStock(w);
            ObjectiveConfig(w); Run(w, 2);
            Check(ReinforceObjective(w).Reinforce == null && ReinforceQueue(w).Count == 0, "an objective without reinforcement never queues production");
            var (none, _) = ObjectiveScene(); var nw = none.World; ReinforceStock(nw);
            int entities = nw.Entities.Count;
            Check(Ok(ReinforceConfig(nw, "reinforce", ReinforceSpec(1, 800))), "reinforcement intent can be recorded before the executor validates available producers");
            Run(nw, 0.4f);
            Check(ReinforceObjective(nw).Paused && ReinforceQueue(nw).Count == 0 && nw.Entities.Count == entities,
                  "missing rallied producers pause for a decision without building anything");
            var (unrallied, _) = ObjectiveScene(); var uw = unrallied.World; ReinforceStock(uw); ReinforceProducer(uw, rally: false);
            ReinforceConfig(uw, "reinforce", ReinforceSpec(1, 800)); Run(uw, 0.4f);
            Check(ReinforceObjective(uw).Paused && ReinforceQueue(uw).Count == 0, "an existing producer not rallied to this squad is never selected");
        }

        static void ReinforceCountsAndSpending()
        {
            var (game, units) = ObjectiveScene("rifleman"); var w = game.World;
            var first = ReinforceProducer(w); var second = ReinforceProducer(w); var unrallied = ReinforceProducer(w, rally: false); ReinforceStock(w);
            ReinforceConfig(w, "reinforce", ReinforceSpec(3, 800)); Run(w, 0.4f);
            var objective = ReinforceObjective(w); var queue = ReinforceQueue(w);
            Check(queue.Count == 2 && queue.Select(p => p.StructureId).OrderBy(id => id).SequenceEqual(new[] { first.Id, second.Id }) &&
                  queue.All(p => p.StructureId != unrallied.Id && p.WorkerId != 0 && p.ReinforcementSquad == "west" && p.ObjectiveId == objective.Id),
                  "reinforcement balances the deficit across both supplied rally producers and never uses an unrallied one");
            int stock = w.Teams[0].Amount("steel"); Run(w, 2);
            Check(ReinforceQueue(w).Count == 2 && w.Teams[0].Amount("steel") == stock && objective.Reinforce.Spent["steel"] == 80,
                  "pending committed units prevent repeated queues and repeated spending");
            float before = w.Teams[0].Squads["west"].FormationHp; Run(w, 10);
            Check(w.Teams[0].Squads["west"].Live(w, 0).Count(e => e.Def.Key == "rifleman") == 3 && ReinforceQueue(w).Count == 0 &&
                  w.Teams[0].Squads["west"].FormationHp == before + 250, "completed reinforcements enroll through the ordinary rally and extend the formation baseline");
            int spent = objective.Reinforce.Spent["steel"];
            var lost = w.Teams[0].Squads["west"].Live(w, 0).First(e => e.Def.Key == "rifleman"); w.Remove(lost); Run(w, 1.2f);
            Check(ReinforceQueue(w).Count == 1 && objective.Reinforce.Spent["steel"] == spent + 40, "a later loss queues exactly the newly missing requested unit");

            var (tankGame, _) = ObjectiveScene(); var tw = tankGame.World; ReinforceProducer(tw, "factory"); ReinforceStock(tw);
            ReinforceConfig(tw, "reinforce", ReinforceSpec(1, 800, "light_tank")); Run(tw, 0.4f);
            var tanks = ReinforceObjective(tw).Reinforce;
            Check(tanks.Spent["steel"] == 200 && tanks.Spent["copper"] == 40 && tanks.Budget.Count == 1 &&
                  tw.Teams[0].Amount("steel") == 9800 && tw.Teams[0].Amount("copper") == 9960,
                  "all ordinary costs are paid and reported even when only steel is capped");
        }

        static void ReinforcePartialAndWaiting()
        {
            var (game, _) = ObjectiveScene(); var w = game.World; ReinforceProducer(w); ReinforceStock(w, 60);
            ReinforceConfig(w, "reinforce", ReinforceSpec(3, 200)); Run(w, 0.4f);
            var objective = ReinforceObjective(w);
            Check(ReinforceQueue(w).Count == 1 && objective.Reinforce.Spent["steel"] == 40 && !objective.Paused, "a partially affordable purchase records only the one unit actually paid for");
            Run(w, 2);
            Check(ReinforceQueue(w).Count == 1 && objective.Reinforce.Spent["steel"] == 40 && !objective.Paused, "stock shortage waits at cadence without duplicate queues or budget exhaustion");
            w.Teams[0].Stock["steel"] = 100; Run(w, 1.2f);
            Check(objective.Reinforce.Spent["steel"] == 120 && ReinforceQueue(w).Count + w.Teams[0].Squads["west"].Live(w, 0).Count(e => e.Def.Key == "rifleman") == 3,
                  "new stock pays only the remaining deficit");

            var (limited, _) = ObjectiveScene(); var lw = limited.World; ReinforceProducer(lw); ReinforceStock(lw);
            ReinforceConfig(lw, "reinforce", ReinforceSpec(3, 100)); Run(lw, 0.4f);
            var lo = ReinforceObjective(lw);
            Check(lo.Paused && lo.Reinforce.BudgetExhausted && lo.Reinforce.Spent["steel"] == 80 && ReinforceQueue(lw).Count == 2,
                  "an insufficient cap queues only what fits, then latches budget exhaustion while preserving paid commitments");
            Run(lw, 12);
            Check(ReinforceQueue(lw).Count == 0 && lw.Teams[0].Squads["west"].Live(lw, 0).Count(e => e.Def.Key == "rifleman") == 2 && lo.Reinforce.Spent["steel"] == 80,
                  "paid queues complete while their objective is paused, without spending past the cap");
            var events = Events(lw, 0).Where(e => e.Str("kind") == "decision_needed").ToList();
            Check(events.Count == 1 && events[0].Str("sev") == "critical", "exhaustion delivers a single critical decision packet");
            ReinforceConfig(lw, "action", "resume"); Run(lw, 0.4f);
            Check(lo.Paused && lo.Reinforce.Spent["steel"] == 80 && ReinforceQueue(lw).Count == 0,
                  "resuming an exhausted plan does not reset its budget or purchase free reinforcements");
        }

        static void ReinforceOrdinaryCommitments()
        {
            var (game, _) = ObjectiveScene("rifleman", "rifleman", "rifleman", "rifleman"); var w = game.World;
            var producer = ReinforceProducer(w); ReinforceStock(w);
            Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "count", 2, "structure_id", producer.Id));
            int stock = w.Teams[0].Amount("steel");
            ReinforceConfig(w, "reinforce", ReinforceSpec(6, 800)); Run(w, 2);
            Check(ReinforceQueue(w).Count == 0 && w.Teams[0].UnitQueues.Values.Sum(q => q.Count) == 2 && w.Teams[0].Amount("steel") == stock &&
                  ReinforceObjective(w).Reinforce.Spent.Count == 0, "live members plus ordinary explicitly rallied paid queues satisfy keep counts without another purchase");
            var (assigned, _) = ObjectiveScene(); var aw = assigned.World; ReinforceProducer(aw); ReinforceStock(aw);
            Commands.Execute(aw, 0, Cmd("type", "train", "unit", "rifleman")); Run(aw, 0.1f);
            Check(aw.Teams[0].UnitQueues.Values.SelectMany(q => q).Single().WorkerId != 0, "the ordinary shared item is assigned before its squad commitment is counted");
            ReinforceConfig(aw, "reinforce", ReinforceSpec(1, 800)); Run(aw, 0.4f);
            Check(ReinforceQueue(aw).Count == 0 && ReinforceObjective(aw).Reinforce.Spent.Count == 0,
                  "an ordinary assigned item at a matching rally producer also prevents duplicate reinforcement");
        }

        static void ReinforceOrdinaryReservations()
        {
            var (game, _) = ObjectiveScene(); var w = game.World; ReinforceProducer(w); ReinforceStock(w);
            Commands.Execute(w, 0, Cmd("type", "reserve", "item", "steel", "amount", 10000));
            Commands.Execute(w, 0, Cmd("type", "save_for", "unit", "rifleman"));
            ReinforceConfig(w, "reinforce", ReinforceSpec(2, 80)); Run(w, 0.4f);
            var t = w.Teams[0];
            Check(ReinforceQueue(w).Count == 2 && t.Amount("steel") == 9920 && t.Reserve["steel"] == 10000 && t.SaveFor == null,
                  "reinforcement uses ordinary training: converter reserves do not forbid an explicit purchase and matching save_for clears");
            Check(Json.Write(Events(w, 0)).Contains("reserves"), "the exhaustion packet reports the team's reserves alongside its paid commitments");
        }

        static void ReinforceLostProducer()
        {
            var (game, _) = ObjectiveScene(); var w = game.World;
            var original = ReinforceProducer(w); ReinforceStock(w);
            ReinforceConfig(w, "reinforce", ReinforceSpec(2, 800)); Run(w, 0.4f);
            var spare = ReinforceProducer(w);
            var queued = ReinforceQueue(w); float progress = queued.Sum(p => p.Progress); int spent = ReinforceObjective(w).Reinforce.Spent["steel"];
            w.Remove(original); Run(w, 10);
            Check(ReinforceObjective(w).Paused && ReinforceQueue(w).Count == 2 && queued.All(p => p.WorkerId == 0 && p.StructureId == original.Id) &&
                  queued.Sum(p => p.Progress) == progress && ReinforceObjective(w).Reinforce.Spent["steel"] == spent,
                  "destroying the committed producer preserves paid progress and pauses, rather than migrating to a spare");
            Commands.Execute(w, 0, Cmd("type", "train", "unit", "rifleman", "structure_id", spare.Id)); Run(w, 5);
            Check(queued.All(p => p.WorkerId == 0 && p.Progress < Defs.Get("rifleman").BuildTime) && w.Teams[0].Stats.UnitsBuilt == 1,
                  "blocked objective commitments do not prevent ordinary production at another building");
        }

        static void ReinforceChangedRally()
        {
            var (game, _) = ObjectiveScene(); var w = game.World; var producer = ReinforceProducer(w); ReinforceStock(w);
            ReinforceConfig(w, "reinforce", ReinforceSpec(2, 800)); Run(w, 0.4f);
            var queue = ReinforceQueue(w); float progress = queue.Sum(p => p.Progress);
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "other"));
            Commands.Execute(w, 0, Cmd("type", "rally", "structure_id", producer.Id, "squad", "other", "x", 45, "y", 30)); Run(w, 8);
            Check(ReinforceObjective(w).Paused && queue.All(p => p.WorkerId == 0) && queue.Sum(p => p.Progress) == progress &&
                  w.Teams[0].Squads["other"].Members.Count == 0, "changing rally squads blocks committed production instead of silently enrolling it elsewhere");
            Commands.Execute(w, 0, Cmd("type", "rally", "structure_id", producer.Id, "squad", "west", "x", 40, "y", 30)); Run(w, 10);
            Check(ReinforceObjective(w).Paused && ReinforceQueue(w).Count == 0 && w.Teams[0].Squads["west"].Live(w, 0).Count == 2,
                  "restoring the original rally lets paid work finish while the objective remains paused for its commander");
        }

        static void ReinforceReplacedCommitment()
        {
            var (game, _) = ObjectiveScene(); var w = game.World;
            var producer = ReinforceProducer(w); ReinforceStock(w);
            ReinforceConfig(w, "reinforce", ReinforceSpec(2, 800)); Run(w, 0.4f);
            ReinforceProducer(w);
            var queue = ReinforceQueue(w); float progress = queue.Sum(p => p.Progress);
            ReinforceConfig(w, "id", "replacement", "reinforce", ReinforceSpec(2, 800)); Run(w, 0.4f);
            var replacement = ReinforceObjective(w);
            Check(replacement.Id == "replacement" && ReinforceQueue(w).Count == 2 && replacement.Reinforce.Spent.Count == 0,
                  "a replacement objective counts its squad's already paid commitments from the former objective id");
            progress = queue.Sum(p => p.Progress); w.Remove(producer); Run(w, 1.2f);
            Check(replacement.Paused && queue.Sum(p => p.Progress) == progress && ReinforceQueue(w).Count == 2 &&
                  Json.Write(Events(w, 0)).Contains("commitment_objective"),
                  "loss of an old commitment's pin wakes the replacement commander instead of hiding an unfillable deficit");
        }

        static void ReinforcePersistence()
        {
            var (game, _) = ObjectiveScene(); var w = game.World; var producer = ReinforceProducer(w); ReinforceStock(w);
            ReinforceConfig(w, "reinforce", ReinforceSpec(3, 800)); Run(w, 0.4f);
            var json = Snap(game); var back = Resume(json);
            Check(Snap(back) == json && ReinforceQueue(back.World).Count == 3 && ReinforceQueue(back.World).All(p => p.StructureId == producer.Id &&
                  p.ObjectiveId == "west-plan" && p.ReinforcementSquad == "west") && ReinforceObjective(back.World).Reinforce.Spent["steel"] == 120,
                  "paid source metadata, budget spending and cadence round-trip exactly with production progress");
            Run(back.World, 2);
            Check(ReinforceQueue(back.World).Count == 3 && ReinforceObjective(back.World).Reinforce.Spent["steel"] == 120,
                  "resuming a paid objective neither duplicates commitments nor spends twice");
            var root = Snapshot.Parse(json);
            foreach (var team in root.Obj("world").Objs("teams"))
                if (team.Obj("unit_queues") is Dictionary<string, object> queues)
                    foreach (var value in queues.Values.OfType<List<object>>())
                        foreach (var item in value.OfType<Dictionary<string, object>>()) { item.Remove("objective_id"); item.Remove("reinforcement_squad"); }
            back.Restore(root);
            Check(back.World.Teams[0].UnitQueues.Values.SelectMany(q => q).All(p => p.ObjectiveId == null && p.ReinforcementSquad == null),
                  "older production snapshots keep ordinary unrestricted queue semantics");
            Check(w.Errors == 0 && back.World.Errors == 0, "reinforcement introduces no sim errors");
        }
    }
}
