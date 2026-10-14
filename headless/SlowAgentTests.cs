using System;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        public static int RunSlowAgent()
        {
            SlowCommanderHarness();
            Console.WriteLine(failures == 0 ? "All slow-agent tests passed." : $"{failures} slow-agent test(s) FAILED.");
            return failures == 0 ? 0 : 1;
        }

        static void SlowCommanderHarness()
        {
            Console.WriteLine("\n-- slow commander measurement harness");
            var game = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
            var commander = new SlowAgent.Commander(0, false);
            var metric = new SlowAgent.Metrics(0);
            commander.Tick(game, metric);
            int firstCommands = commander.Commands;
            for (int i = 0; i < SlowAgent.TurnTicks - 1; i++) { game.Advance(World.Dt); commander.Tick(game, metric); }
            Check(commander.Turns == 1 && commander.Commands == firstCommands, "slow commander sends no commands inside its 30s think interval");
            game.Advance(World.Dt); commander.Tick(game, metric);
            Check(commander.Turns == 2, "slow commander next decides exactly at 30s");
            Check(SlowAgent.Percentile(Array.Empty<float>(), .5) == null && Convert.ToSingle(SlowAgent.Percentile(new[] { 1f, 2f, 30f }, .95)) == 30,
                "empty responses stay missing and p95 uses nearest rank");

            var w = new World(2, 7, 80);
            var hq = w.Owned(0).First(e => e.IsStructure);
            var truck = w.SpawnUnit(0, "mining_truck", hq);
            long eventCursor = w.Events.Count == 0 ? 0 : w.Events.Last().Seq;
            w.Emit("destroyed", 0, truck.Id, pos: truck.Pos, key: truck.Def.Key);
            var deaths = new SlowAgent.Metrics(0);
            deaths.Sample(w, eventCursor, Array.Empty<(int Id, int Team, Vec2 Pos)>());
            Check(deaths.MinerLosses == 1 && deaths.UndefendedMinerLosses == 1 && deaths.CombatLosses == 0,
                "observer counts a truck's unprotected death without calling it a combat loss");
            var soldier = w.SpawnUnit(0, "rifleman", hq);
            eventCursor = w.Events.Last().Seq;
            w.Emit("destroyed", 0, soldier.Id, pos: soldier.Pos, key: soldier.Def.Key);
            var supported = new SlowAgent.Metrics(0);
            supported.Sample(w, eventCursor, new[] { (101, 0, soldier.Pos), (102, 0, soldier.Pos), (103, 0, soldier.Pos) });
            Check(supported.CombatLosses == 1 && supported.UnsupportedCombatLosses == 0, "three nearby armed allies meet the support threshold");
            var unsupported = new SlowAgent.Metrics(0);
            unsupported.Sample(w, eventCursor, new[] { (101, 0, soldier.Pos), (102, 0, soldier.Pos), (103, 0, soldier.Pos + new Vec2(9, 0)) });
            Check(unsupported.UnsupportedCombatLosses == 1, "allies outside eight tiles do not count as support");
            w.Tick = 100;
            var alert = w.Alerts.Raise(w, 0, "combat", Priority.High, soldier.Pos);
            supported.Sample(w, w.Events.Last().Seq, Array.Empty<(int, int, Vec2)>());
            var report = supported.Report(w, commander);
            Check(Convert.ToInt32(report["unresolved"]) == 1 && report["response_p50_s"] == null, "an unanswered alert remains unresolved with missing latency");
            supported.Respond(alert.Seq, 35);
            report = supported.Report(w, commander);
            Check(Convert.ToInt32(report["unresolved"]) == 0 && Convert.ToSingle(report["response_p50_s"]) == 30, "response latency starts at original alert time");
            var hiddenGame = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
            var cleanGame = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
            var hidden = hiddenGame.World.SpawnUnit(1, "heavy_tank", hiddenGame.World.Owned(1).First(e => e.IsStructure));
            hiddenGame.World.UpdateVisibility();
            Check(!hiddenGame.World.IsVisibleTo(0, hidden), "fog fixture's extra enemy is hidden from the slow commander");
            new SlowAgent.Commander(0, false).Tick(hiddenGame, new SlowAgent.Metrics(0));
            new SlowAgent.Commander(0, false).Tick(cleanGame, new SlowAgent.Metrics(0));
            Check(Json.Write(hiddenGame.World.CommandLog.Select(c => (object)c.Cmd).ToList()) == Json.Write(cleanGame.World.CommandLog.Select(c => (object)c.Cmd).ToList()),
                "adding a hidden enemy does not alter the slow commander's commands");

            var a = SlowAgent.Match(2, false, "self", 0, 60);
            var b = SlowAgent.Match(2, false, "self", 0, 60);
            Check(Json.Write(a) == Json.Write(b), "the same slow-command match is deterministic including response metrics");
            string diagnosticDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pez-slow-diagnostic-" + Guid.NewGuid().ToString("N"));
            var diagnostic = SlowAgent.Match(2, false, "self", 0, 60, "baseline", diagnosticDirectory);
            foreach (var seat in ((System.Collections.Generic.List<object>)diagnostic["seats"]).Cast<JObj>()) seat.Remove("movement_diagnostics");
            Check(Json.Write(a) == Json.Write(diagnostic), "optional snapshot diagnostics preserve every ordinary baseline metric");
            if (System.IO.Directory.Exists(diagnosticDirectory)) System.IO.Directory.Delete(diagnosticDirectory, true);
            Check(!game.World.Teams[0].Miners.WithdrawOnAttack && !((JObj)((System.Collections.Generic.List<object>)a["seats"])[0]).Contains("executor"),
                "baseline commander never enables miner policy or adds executor metrics");
            SlowMinerLayerHarness();
            SlowSquadLayerHarness();
            SlowGuardLayerHarness();
            SlowObjectiveLayerHarness();
        }

        static void SlowMinerLayerHarness()
        {
            var game = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
            var commander = new SlowAgent.Commander(0, false, "miners");
            var metric = new SlowAgent.Metrics(0, "miners");
            commander.Tick(game, metric);
            var policy = game.World.Teams[0].Miners;
            Check(policy.WithdrawOnAttack && !policy.ToBase && policy.ResumeAfterS == 20 && Vec2.Dist(policy.Destination, game.World.Teams[0].StartPos) < .1f,
                "miners layer explicitly sets home destination and 20s clear delay");
            game.World.Tick = SlowAgent.TurnTicks;
            commander.Tick(game, metric);
            Check(game.World.CommandLog.Count(c => c.Cmd.Str("type") == "set_policy") == 1, "miner policy is configured once, not every slow turn");

            game.World.Observe.Note(0, "orders", "miners_withdrawn", Sev.Notice, "own",
                new JObj().Set("reason", "hit").Set("triggered_at_s", 29.95f), "measurement fixture");
            game.World.Observe.Note(0, "orders", "miners_withdrawn", Sev.Notice, "own",
                new JObj().Set("reason", "seen_weapon_range").Set("triggered_at_s", 30f), "measurement fixture");
            game.World.Observe.Note(0, "orders", "miners_withdrawn", Sev.Notice, "own", new JObj().Set("reason", "unknown"), "measurement fixture");
            metric.SampleExecutor(game.World);
            metric.SampleExecutor(game.World);
            var executor = (JObj)metric.Report(game.World, commander)["executor"];
            Check(Convert.ToInt32(executor["withdrawals"]) == 3 && Convert.ToInt32(executor["unmatched"]) == 1 &&
                Convert.ToInt32(((JObj)executor["hit_triggered"])["count"]) == 1 && Convert.ToInt32(((JObj)executor["visible_range_triggered"])["count"]) == 1,
                "executor measurements separate hit/sight triggers, preserve unmatched and consume observations once");
            Check(Math.Abs(Convert.ToSingle(((JObj)executor["hit_triggered"])["mean_s"]) - .05f) < .001f,
                "executor trigger-to-order time is separate from commander alert response");
        }

        static void SlowSquadLayerHarness()
        {
            var game = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
            var commander = new SlowAgent.Commander(0, true, "squads");
            var metric = new SlowAgent.Metrics(0, "squads");
            commander.Tick(game, metric);
            var state = StateData.Team(game.World, 0);
            var army = ((System.Collections.Generic.List<object>)state["units"]).Cast<JObj>().Where(u => Defs.Get((string)u["type"]).Weapon != null).ToList();
            Check((string)commander.ArmySelector(army) == "squad:army" && commander.ArmySelector(army.Take(1).ToList()) is System.Collections.Generic.List<object>,
                "squad addressing substitutes exactly the full live army and preserves explicit subsets");
            Check(game.World.Teams[0].Squads[SlowAgent.Commander.ArmySquad].Members.Count == army.Count && !game.World.Teams[0].Miners.WithdrawOnAttack,
                "squads layer enrolls only the own combat army and leaves miner behaviour off");
            var hq = game.World.Owned(0).First(e => e.Def.Key == "command_center");
            var newcomer = game.World.SpawnUnit(0, "rifleman", hq);
            var barracks = game.World.SpawnStructure(0, "barracks", game.World.FindPlacement(0, "barracks").Value, 1f);
            game.World.Tick = SlowAgent.TurnTicks;
            commander.Tick(game, metric);
            var amendments = game.World.CommandLog.Where(c => c.Cmd.Str("type") == "squad").ToList();
            Check(amendments.Count == 2 && amendments[1].Cmd.Arr("add").Count == 1 && Convert.ToInt32(amendments[1].Cmd.Arr("add")[0]) == newcomer.Id,
                "later squad amendments add only new combat members without recreating the squad");
            Check(barracks.RallySquad == SlowAgent.Commander.ArmySquad && game.World.SpawnUnit(0, "rifleman", barracks) is var trained &&
                game.World.Teams[0].Squads[SlowAgent.Commander.ArmySquad].Members.Contains(trained.Id),
                "existing P0 producer rally enrolls newly trained combat units in the same named army");
            game.World.Tick += SlowAgent.TurnTicks;
            commander.Tick(game, metric);
            Check(!game.World.CommandLog.Any(c => c.Tick == game.World.Tick && c.Cmd.Str("type") == "squad") && !metric.Report(game.World, commander).Contains("executor"),
                "rally enrollment needs no redundant membership command or executor metric schema");
            Check(!game.World.Teams[0].Squads[SlowAgent.Commander.ArmySquad].Guards.Enabled,
                "squads layer leaves every guard disabled");
        }

        static void SlowGuardLayerHarness()
        {
            foreach (var layer in new[] { "baseline", "miners", "squads" })
            {
                var off = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
                new SlowAgent.Commander(0, false, layer).Tick(off, new SlowAgent.Metrics(0, layer));
                Check(off.World.Teams[0].Squads.Values.All(s => !s.Guards.Enabled), $"{layer} layer never enables a squad guard");
            }
            var game = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
            var commander = new SlowAgent.Commander(0, false, "guards");
            var metric = new SlowAgent.Metrics(0, "guards");
            commander.Tick(game, metric);
            var guard = game.World.Teams[0].Squads[SlowAgent.Commander.ArmySquad].Guards;
            Check(guard.RetreatBelowPct == 40 && guard.SupportWithin == 6 && guard.ScreenRoles.SequenceEqual(new[] { "rifleman" }) &&
                guard.ScreenBehind == 4 && guard.WithdrawBelowStrengthPct == 50 && guard.HasTo && Vec2.Dist(guard.To, game.World.Teams[0].StartPos) < .1f &&
                !game.World.Teams[0].Miners.WithdrawOnAttack, "guards layer explicitly supplies its complete fixed profile and home destination, without miner policy");
            game.World.Tick = SlowAgent.TurnTicks;
            commander.Tick(game, metric);
            Check(game.World.CommandLog.Count(c => c.Cmd.Str("type") == "guard") == 1, "guard profile is configured once rather than resetting its latches every turn");
            var army = ((System.Collections.Generic.List<object>)StateData.Team(game.World, 0)["units"]).Cast<JObj>()
                .Where(u => Defs.Get((string)u["type"]).Weapon != null).ToList();
            Check((string)commander.ArmySelector(army) == "squad:army" && commander.ArmySelector(army.Take(1).ToList()) is System.Collections.Generic.List<object>,
                "guards retain exact full-army versus explicit-subset addressing");
            game.World.Observe.Note(0, "orders", "guard_triggered", Sev.Notice, "own",
                new JObj().Set("rule", "retreat_below_pct").Set("reason", "hp_below").Set("action", "move").Set("trigger_basis", "condition_observed").Set("triggered_at_s", 29.9f), "measurement fixture");
            game.World.Observe.Note(0, "orders", "decision_needed", Sev.Critical, "own",
                new JObj().Set("rule", "withdraw_below_strength_pct").Set("reason", "strength_below").Set("action", "wake").Set("triggered_at_s", 30f), "measurement fixture");
            game.World.Observe.Note(0, "orders", "guard_triggered", Sev.Notice, "own", new JObj().Set("rule", "screen"), "measurement fixture");
            metric.SampleExecutor(game.World); metric.SampleExecutor(game.World);
            var executor = (JObj)metric.Report(game.World, commander)["guard_executor"];
            Check(Convert.ToInt32(executor["guard_triggered"]) == 2 && Convert.ToInt32(executor["decision_needed"]) == 1 && Convert.ToInt32(executor["unmatched"]) == 1 &&
                Convert.ToInt32(((JObj)executor["guard_trigger_latency"])["count"]) == 1 && Convert.ToInt32(((JObj)executor["decision_latency"])["count"]) == 1,
                "guard events preserve unmatched trigger times and measure decisions separately, without cursor duplicates");
        }
    }
}
