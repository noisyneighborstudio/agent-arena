using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        public static int RunStrategies()
        {
            StrategyRecordsTests(); StrategyPlanningPrivacy(); SnapshotCoversEveryField();
            Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} strategy tests FAILED.");
            return failures == 0 ? 0 : 1;
        }
        static Dictionary<string, object> StrategyCommand(string text, params string[] priorities) => Cmd("type", "set_strategy", "text", text,
            "priorities", priorities.Cast<object>().ToList());
        static JObj StrategyResult(JObj batch, int i = 0) => (JObj)((List<object>)batch["results"])[i];
        static int StrategyNumber(JObj row, string field) => Convert.ToInt32(row[field]);

        static void StrategyRecordsTests()
        {
            StrategyValues(); StrategyOnlyDescribes(); StrategyHumanVersions(); StrategyAdvisories(); StrategyRetries(); StrategyPersistence(); StrategyWaiting();
        }

        static void StrategyValues()
        {
            var game = new Game(new GameConfig { Seed = 7, MapSize = 80, Controllers = new[] { "llm", "llm" } }); var w = game.World; var team = w.Teams[0];
            Check(team.StrategyText == "" && team.Priorities.Count == 0 && team.PlanVersion == 0, "a fresh team has no strategy record and plan version zero");
            var recorded = Commands.Execute(w, 0, StrategyCommand("  Expand east.\nHold the ridge.  ", "  steel  ", "hold", "hold"));
            Check(Ok(recorded) && team.StrategyText == "Expand east.\nHold the ridge." && team.Priorities.SequenceEqual(new[] { "steel", "hold", "hold" }) && team.PlanVersion == 1,
                  "strategy records preserve multiline prose, priority order and duplicates while trimming outer whitespace");
            var same = Commands.Execute(w, 0, StrategyCommand("Expand east.\nHold the ridge.", "steel", "hold", "hold"));
            Check(Ok(same) && same["changed"] is bool changed && !changed && team.PlanVersion == 1, "an identical normalized strategy does not create another version");
            var before = Json.Write(StrategyRecords.View(team)); int version = team.PlanVersion;
            var invalid = new[]
            {
                Cmd("type", "set_strategy", "text", "missing priorities"),
                Cmd("type", "set_strategy", "text", 2, "priorities", new List<object>()),
                StrategyCommand(new string('t', 2001)),
                StrategyCommand("too many", Enumerable.Repeat("p", 17).ToArray()),
                StrategyCommand("too long", new string('p', 121)),
                Cmd("type", "set_strategy", "text", "bad entry", "priorities", new List<object> { "valid", 2d }),
            };
            Check(invalid.All(c => !Ok(Commands.Execute(w, 0, c))) && Json.Write(StrategyRecords.View(team)) == before && team.PlanVersion == version,
                  "missing fields, wrong types and raw size limits fail atomically without changing a strategy");
            Check(Ok(Commands.Execute(w, 0, StrategyCommand(new string('t', 2000), Enumerable.Repeat(new string('p', 120), 16).ToArray()))),
                  "strategy accepts the exact 2000-character and 16-by-120 priority bounds");
            Commands.Execute(w, 0, StrategyCommand(""));
            Check(team.StrategyText == "" && team.Priorities.Count == 0 && team.PlanVersion == 3, "an explicit empty record clears text and priorities as a new plan version");
        }

        static void StrategyOnlyDescribes()
        {
            var (game, units) = ObjectiveScene("light_tank", "rifleman"); var w = game.World;
            GuardConfig(w, "hold_for_support", Cmd("within", 6)); ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(45, 30)); Run(w, 2);
            var copy = Resume(Snap(game));
            string entities = Json.Write(w.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList());
            string guards = Json.Write(w.Teams[0].Squads["west"].Guards.Save());
            string objective = Json.Write(ObjectiveOf(w).View()); string policy = Json.Write(w.Teams[0].Miners.Save());
            int steel = w.Teams[0].Amount("steel");
            Commands.Execute(w, 0, StrategyCommand("Retreat everyone. Buy eight tanks. Protect every mine. Attack their base immediately.", "fight now", "spend everything"));
            Check(entities == Json.Write(w.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList()) && guards == Json.Write(w.Teams[0].Squads["west"].Guards.Save()) &&
                  objective == Json.Write(ObjectiveOf(w).View()) && policy == Json.Write(w.Teams[0].Miners.Save()) && steel == w.Teams[0].Amount("steel"),
                  "strategy prose issues no orders, moves no units, spends nothing and changes no configured executor payload");
            Run(w, 60); Run(copy.World, 60);
            Check(Json.Write(w.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList()) == Json.Write(copy.World.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList()) &&
                  Json.Write(StateView.Stockpile(w.Teams[0])) == Json.Write(StateView.Stockpile(copy.World.Teams[0])),
                  "a strategy containing executable-looking words cannot change sixty seconds of existing gameplay");
            var state = StateData.Team(w, 0); var textState = StateView.TeamState(w, 0); var feed = w.Observe.Log(0).Snapshot();
            Check(StrategyNumber(state, "plan_version") == 1 && StrategyNumber(textState, "plan_version") == 1 && StrategyNumber(feed, "plan_version") == 1 &&
                  Json.Write(state["strategy"]) == Json.Write(StrategyRecords.View(w.Teams[0])) && Json.Write(feed["strategy"]) == Json.Write(state["strategy"]),
                  "both state formats and the owning observation snapshot show the same descriptive strategy and version");
        }

        static void StrategyHumanVersions()
        {
            var game = new Game(new GameConfig { Seed = 7, MapSize = 80, Controllers = new[] { "llm", "llm" } }); var w = game.World; var t = w.Teams[0];
            Commands.Execute(w, 0, StrategyCommand("Expand east.", "ore"));
            w.SetOrders(0, "  Hold their bridge.\nKeep the miners safe.  ");
            Check(t.PlanVersion == 2 && t.OrdersVersion == 1 && t.StandingOrders == "Hold their bridge.\nKeep the miners safe." && t.StrategyText == "Expand east.",
                  "a changed human standing order advances the plan version while preserving its existing separate multiline content");
            w.SetOrders(0, "Hold their bridge.\nKeep the miners safe.");
            Check(t.PlanVersion == 2 && t.OrdersVersion == 1, "unchanged human instructions advance neither version");
            w.SetOrders(0, new string('x', 2001));
            Check(t.StandingOrders.Length == 2000 && t.PlanVersion == 3 && t.OrdersVersion == 2, "human standing-order truncation remains exactly as before");
            w.SetOrders(0, "");
            Check(t.PlanVersion == 4 && t.OrdersVersion == 3 && t.StrategyText == "Expand east.", "clearing human instructions changes the plan without clearing the agent's strategy record");
        }

        static void StrategyAdvisories()
        {
            var (game, units) = ObjectiveScene("rifleman"); var w = game.World;
            JObj Send(object c) => CommandSafety.ExecuteBatch(game, 0, c, "player");
            var current = Send(Cmd("plan_version", 0, "commands", new List<object> { StrategyCommand("Hold here.", "ore"), Cmd("type", "move", "units", new[] { units[0].Id }, "x", 40, "y", 30) }));
            Check(StrategyNumber(current, "plan_version") == 1 && StrategyNumber(current, "current_plan_version") == 0 && current["plan_changed"] is bool ownChanged && !ownChanged &&
                  StrategyResult(current, 1)["plan_changed"] is bool moved && !moved,
                  "a batch compares its plan at receipt time, so its own set_strategy does not manufacture a stale-plan warning");
            w.SetOrders(0, "Wait for your escort.");
            var stale = Send(Cmd("plan_version", 1, "commands", new List<object> { Cmd("type", "stop", "units", new[] { units[0].Id }) }));
            Check(Ok(StrategyResult(stale)) && units[0].Order == Order.Idle && stale["plan_changed"] is bool mismatch && mismatch &&
                  StrategyNumber(stale, "expected_plan_version") == 1 && StrategyNumber(stale, "current_plan_version") == 2 && StrategyNumber(stale, "plan_version") == 2,
                  "a human plan change is reported with expected/current versions while the requested command still executes");
            var overrides = Send(Cmd("plan_version", 0, "commands", new List<object> { Cmd("type", "say", "text", "checked", "plan_version", 2) }));
            Check(overrides["plan_changed"] is bool envelope && envelope && StrategyResult(overrides)["plan_changed"] is bool item && !item &&
                  StrategyNumber(StrategyResult(overrides), "expected_plan_version") == 2, "per-command plan metadata overrides the batch expectation using the same stable receipt-time current version");
            var single = Send(Cmd("type", "say", "text", "single", "plan_version", 2));
            Check(StrategyResult(single)["plan_changed"] is bool valid && !valid && StrategyNumber(single, "plan_version") == 2, "single-command requests accept the same advisory plan metadata");
            int before = w.Events.Count;
            var invalid = Send(Cmd("commands", new List<object> { Cmd("type", "say", "text", "must not run"), Cmd("type", "stop", "units", new[] { units[0].Id }, "plan_version", 1.5f) }));
            Check(!Ok(invalid) && w.Events.Count == before && StrategyNumber(invalid, "plan_version") == 2, "invalid plan metadata is rejected before any part of a new batch executes");
            Check(!Ok(Send(Cmd("plan_version", -1, "commands", new List<object>()))) &&
                  !Ok(Send(Cmd("plan_version", "2", "commands", new List<object>()))), "plan metadata requires a nonnegative integer, rather than coercing strings or negative versions");
            var last = w.CommandLog.LastOrDefault();
            Check(last != null && !last.Cmd.ContainsKey("plan_version"), "plan comparison metadata stays out of replayed commands");
        }

        static void StrategyRetries()
        {
            var (game, units) = ObjectiveScene("rifleman"); var w = game.World;
            JObj Send(object c) => CommandSafety.ExecuteBatch(game, 0, c, "player");
            var batch = Cmd("request_id", "strategy-first", "plan_version", 0, "commands", new List<object> { StrategyCommand("First plan.", "ore") });
            string first = Json.Write(Send(batch));
            w.SetOrders(0, "A newer human plan.");
            var replay = Send(Cmd("request_id", "strategy-first", "plan_version", 2, "commands", new List<object> { StrategyCommand("Must not replace.", "attack") }));
            Check(Json.Write(replay) == first && w.Teams[0].StrategyText == "First plan." && w.Teams[0].PlanVersion == 2,
                  "a repeated batch returns its original version advice and result verbatim without changing the newer plan");
            var single = StrategyCommand("Third plan.", "hold"); single["request_id"] = "single-strategy"; single["plan_version"] = 2d;
            string result = Json.Write(StrategyResult(Send(single))); w.SetOrders(0, "Fourth human plan.");
            Check(Json.Write(StrategyResult(Send(single))) == result && w.Teams[0].PlanVersion == 4, "per-command receipts preserve their original strategy version and comparison after later human changes");
            var copy = Resume(Snap(game));
            Check(Json.Write(CommandSafety.ExecuteBatch(copy, 0, batch, "player")) == first && copy.World.Teams[0].PlanVersion == 4,
                  "strategy and version receipts remain idempotent across a full engine snapshot");
        }

        static void StrategyPersistence()
        {
            var game = new Game(new GameConfig { Seed = 7, MapSize = 64, Open = true, Controllers = new[] { "llm", "llm" }, HouseAIs = 0 }); var w = game.World;
            Commands.Execute(w, 0, StrategyCommand("Expand east.\nHold north.", "ore", "deny crystal")); w.SetOrders(0, "Protect the bridge.");
            var json = Snap(game); var copy = Resume(json);
            Check(Snap(copy) == json && copy.World.Teams[0].StrategyText == w.Teams[0].StrategyText && copy.World.Teams[0].Priorities.SequenceEqual(w.Teams[0].Priorities) &&
                  copy.World.Teams[0].PlanVersion == 2, "strategy text, ordered priorities and combined plan version round-trip exactly");
            var old = Snapshot.Parse(json);
            foreach (var team in old.Obj("world").Objs("teams")) { team.Remove("strategy_text"); team.Remove("priorities"); team.Remove("plan_version"); }
            copy.Restore(old);
            Check(copy.World.Teams.All(t => t.StrategyText == "" && t.Priorities.Count == 0 && t.PlanVersion == 0) && copy.World.Teams[0].OrdersVersion == 1,
                  "older snapshots default to no strategy and version zero without changing existing human instructions");
            while (w.Teams.Count < World.Flavors.Length)
            {
                var added = game.Join("llm", "Fixture " + w.Teams.Count, null, out var error, out _);
                if (added == null) { Check(false, "recycle fixture could not fill seats: " + error); return; }
            }
            w.Leave(0); var seat = game.Join("llm", "Replacement", null, out _, out _);
            Check(seat != null && seat.Id == 0 && seat.StrategyText == "" && seat.Priorities.Count == 0 && seat.PlanVersion == 0 && seat.StandingOrders == "",
                  "a recycled seat clears the previous occupant's strategy, priorities, versions and human instructions");
            game.NextMatch();
            Check(game.World.Teams.All(t => t.StrategyText == "" && t.Priorities.Count == 0 && t.PlanVersion == 0), "the next match starts with no retained strategy program or record");
            game.Restart(new GameConfig { Seed = 7, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            Check(game.World.Teams.Count == 2 && game.World.Teams.All(t => t.StrategyText == "" && t.Priorities.Count == 0 && t.PlanVersion == 0),
                  "a newly populated match also resets all strategy state rather than inheriting an earlier record");
        }

        static void StrategyWaiting()
        {
            var game = new Game(new GameConfig { Seed = 7, MapSize = 64, Open = true, Controllers = new string[0], MinPlayers = 2 });
            var seat = game.Join("llm", "Preparing", null, out _, out _);
            var result = CommandSafety.ExecuteBatch(game, seat.Id, StrategyCommand("Prepare first.", "ore"), "player");
            Check(!Ok(StrategyResult(result)) && StrategyResult(result)["error"].ToString().StartsWith("waiting_for_players:") && seat.PlanVersion == 0 && seat.StrategyText == "",
                  "waiting-room strategy commands keep the existing ordinary-command refusal and cannot start the match");
        }
    }
}
