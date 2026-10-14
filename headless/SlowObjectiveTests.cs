using System;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static void SlowObjectiveLayerHarness()
        {
            foreach (string layer in new[] { "baseline", "miners", "squads", "guards" })
            {
                var off = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
                new SlowAgent.Commander(0, false, layer).Tick(off, new SlowAgent.Metrics(0, layer));
                Check(off.World.Teams[0].Objectives.Count == 0, $"{layer} layer never configures an objective");
            }
            var caps = SlowAgent.Commander.ObjectiveCaps();
            Check(caps["steel"] == 800 && caps["copper"] == 4 * Defs.Get("light_tank").Cost["copper"],
                "objective reinforcement caps explicitly cover the actual tank copper price");

            var game = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
            var w = game.World;
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
            var barracks = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
            var factory = w.SpawnStructure(0, "factory", w.FindPlacement(0, "factory").Value, 1f);
            for (int i = 0; i < 6; i++) w.SpawnUnit(0, "rifleman", hq);
            var commander = new SlowAgent.Commander(0, false, "objectives");
            var metric = new SlowAgent.Metrics(0, "objectives");
            commander.Tick(game, metric);
            var mission = w.Teams[0].Objectives[SlowAgent.Commander.Mission];
            Check(mission.Verb == "go" && mission.Radius == 6 && mission.HasAssembly && mission.AssemblyMin == 8 &&
                mission.Reinforce.Keep["rifleman"] == 6 && mission.Reinforce.Keep["light_tank"] == 2 &&
                mission.Reinforce.Budget["steel"] == 800 && mission.Reinforce.Budget["copper"] == 160,
                "objectives layer supplies the complete explicit go/assembly/counts/budget profile");
            Check(barracks.RallySquad == SlowAgent.Commander.ArmySquad && factory.RallySquad == SlowAgent.Commander.ArmySquad &&
                !w.Teams[0].Miners.WithdrawOnAttack && !w.Teams[0].Squads[SlowAgent.Commander.ArmySquad].Guards.Enabled,
                "both already-owned producers join the named cohort without miner policy or guards");
            w.Teams[0].Give("steel", 800); w.Teams[0].Give("copper", 160);
            void Advance(float seconds)
            {
                for (int i = 0; i < seconds * World.TickRate; i++) { game.Advance(World.Dt); metric.SampleExecutor(w); }
            }
            Advance(17);
            var tanks = w.Owned(0).Where(e => e.Def.Key == "light_tank").ToList();
            Check(tanks.Count == 2 && mission.Reinforce.Spent["steel"] == 400 && mission.Reinforce.Spent["copper"] == 80,
                "reinforcement executes actual paid tank purchases between commander turns");
            w.Remove(tanks[0]); Advance(1.1f);
            w.Remove(tanks[1]); Advance(1.1f);
            Check(mission.Paused && mission.PauseReason == "budget_exhausted" && mission.Reinforce.Spent["steel"] == 800 && mission.Reinforce.Spent["copper"] == 160,
                "the lifetime budget exhausts on actual replacements and pauses without resetting spent");
            while (w.Tick < SlowAgent.TurnTicks) { game.Advance(World.Dt); metric.SampleExecutor(w); }
            commander.Tick(game, metric);
            var replacement = w.Teams[0].Objectives[SlowAgent.Commander.Mission];
            var budget = commander.ObjectiveBudget(w);
            Check(replacement.Reinforce == null && Convert.ToInt32(((JObj)budget["spent"])["steel"]) == 800 &&
                Convert.ToInt32(((JObj)budget["remaining"])["copper"]) == 0,
                "the next slow turn preserves spent caps and explicitly continues without a new reinforcement budget");
            Check(!w.CommandLog.Any(c => (c.Cmd.Str("type") == "attack_move" || c.Cmd.Str("type") == "move") &&
                c.Cmd.Ids("units").Any(id => w.Teams[0].Squads[SlowAgent.Commander.ArmySquad].Members.Contains(id))),
                "objective commander never sends competing primitive army or idle-subset orders");
            var summary = (JObj)metric.Report(w, commander)["objective_executor"];
            Check(Convert.ToInt32(summary["between_turn_orders"]) > 0 && Convert.ToInt32(summary["decision_packets"]) > 0 &&
                Convert.ToInt32(summary["packet_responses"]) > 0, "objective orders and decision responses are recorded separately between fixed thirty-second turns");
            SlowObjectiveRetreatResponse();
            SlowObjectiveFreshRetreatHandback();
        }

        static void SlowObjectiveRetreatResponse()
        {
            var game = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
            var w = game.World;
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
            w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
            w.SpawnStructure(0, "factory", w.FindPlacement(0, "factory").Value, 1f);
            for (int i = 0; i < 6; i++) w.SpawnUnit(0, "rifleman", hq);
            var hurt = w.Owned(0).First(e => e.Def.Key == "rifleman");
            At(hurt, w.Paths.NearestPassable(Int2.Of(hq.Center + new Vec2(18, 0))).Center);
            var commander = new SlowAgent.Commander(0, true, "objectives");
            var metric = new SlowAgent.Metrics(0, "objectives");
            commander.Tick(game, metric);
            hurt.Hp = hurt.Def.MaxHp * .2f;
            for (int i = 0; i < SlowAgent.TurnTicks; i++) { game.Advance(World.Dt); metric.SampleExecutor(w); }
            var mission = w.Teams[0].Objectives[SlowAgent.Commander.Mission];
            Check(mission.Paused && mission.PauseReason == "native_retreat" && hurt.Order == Order.Idle && hurt.RetreatSpent,
                "native retreat owns the unit until arrival and leaves its objective paused for the commander");
            commander.Tick(game, metric);
            Check(!mission.Paused && hurt.RetreatSpent && w.CommandLog.Any(c => c.Tick == SlowAgent.TurnTicks && c.Cmd.Str("type") == "objective" &&
                c.Cmd.TryGetValue("resume", out var flag) && flag is bool yes && yes),
                "the next thirty-second turn explicitly resumes after publicly visible retreat arrival without rearming the wound");
        }

        static void SlowObjectiveFreshRetreatHandback()
        {
            var game = new Game(new GameConfig { Seed = 1, MapSize = 80, Controllers = new[] { "llm", "llm" }, MatchHours = 0 });
            var w = game.World;
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            w.SpawnStructure(0, "power_plant", w.FindPlacement(0, "power_plant").Value, 1f);
            var barracks = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
            w.SpawnStructure(0, "factory", w.FindPlacement(0, "factory").Value, 1f);
            for (int i = 0; i < 2; i++) w.SpawnUnit(0, "rifleman", hq);
            var commander = new SlowAgent.Commander(0, true, "objectives");
            var metric = new SlowAgent.Metrics(0, "objectives");
            commander.Tick(game, metric);
            game.Advance(World.Dt);
            var fresh = w.SpawnUnit(0, "rifleman", barracks);
            while (w.Tick < SlowAgent.TurnTicks) { game.Advance(World.Dt); metric.SampleExecutor(w); }
            var mission = w.Teams[0].Objectives[SlowAgent.Commander.Mission];
            Check(!mission.Paused && fresh.ObjectiveId == mission.Id && !mission.Assembled,
                "a rallied fresh unit joins the still-active eight-member assembly before its next slow turn");
            commander.Tick(game, metric);
            Check(fresh.RetreatBelow == .4f && fresh.ObjectiveId == mission.Id && !mission.Detached.Contains(fresh.Id) && metric.ObjectiveHandbacks == 1,
                "fresh P0 retreat configuration is followed by one explicit handback to its active objective");
            Check(w.CommandLog.Count(c => c.Tick == SlowAgent.TurnTicks && c.Cmd.Str("type") == "objective" &&
                c.Cmd.TryGetValue("resume", out var flag) && flag is bool yes && yes) == 1,
                "fresh-member handback uses a single resume and does not restart an assembling mission");
        }
    }
}
