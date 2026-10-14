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
        }
    }
}
