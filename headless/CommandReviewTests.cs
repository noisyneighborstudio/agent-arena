using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static void CommandReviewRegressions()
        {
            CommandReviewDetachment(); CommandReviewBaselines();
        }

        static void CommandReviewDetachment()
        {
            var (game, units) = ObjectiveScene("rifleman", "mining_truck"); var w = game.World;
            ObjectiveConfig(w); Run(w, 0.2f);
            var nonOrders = new[] {
                StrategyCommand("Hold west.", "ore"), Cmd("type", "say", "text", "West holds."),
                Cmd("type", "set_policy", "miners", Cmd("withdraw_on_attack", false)),
                Cmd("type", "reserve", "item", "steel", "amount", 0), Cmd("type", "save_for"),
                Cmd("type", "set_retreat", "below_pct", 30)
            };
            foreach (var command in nonOrders)
            {
                command["squad"] = "west";
                Check(Ok(Commands.Execute(w, 0, command)) && units.All(e => e.ObjectiveId == "west-plan") && ObjectiveOf(w).Detached.Count == 0,
                    $"CommandReviewDetachment: {command.Str("type")} selectors cannot detach objective members without issuing an order");
            }
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "harvest", "squad", "west", "ore", "any"))) &&
                units[0].ObjectiveId == "west-plan" && units[1].ObjectiveId == null && ObjectiveOf(w).Detached.SetEquals(new[] { units[1].Id }),
                "CommandReviewDetachment: a mixed squad harvest detaches only the mining truck actually ordered");

            var loaded = ObjectiveScene("rifleman", "rifleman", "rifleman", "rifleman", "rifleman", "rifleman", "rifleman", "rifleman", "rifleman"); w = loaded.game.World;
            ObjectiveConfig(w); var apc = At(Mine0(w, "apc"), new Vec2(30, 30));
            Commands.Execute(w, 0, Cmd("type", "load", "squad", "west", "transport", apc.Id));
            Check(ObjectiveOf(w).Detached.Count == apc.Def.Capacity && loaded.units.Count(e => e.ObjectiveId == "west-plan") == loaded.units.Count - apc.Def.Capacity,
                "CommandReviewDetachment: a capacity-limited load leaves unselected passengers attached");

            var idle = ObjectiveScene("rifleman"); w = idle.game.World; ObjectiveConfig(w);
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "move", "units", "idle", "x", 45, "y", 30))) && idle.units[0].ObjectiveId == null,
                "CommandReviewDetachment: units:idle detaches the actual ordered members even after their orders change");
        }

        static string CommandBaseline(Objective o) => Json.Write(new JObj().Set("tick", o.CommandTick)
            .Set("hp", o.BaselineHp.OrderBy(p => p.Key).Select(p => (object)new List<object> { p.Key, p.Value }).ToList())
            .Set("members", o.BaselineMembers.OrderBy(id => id).Cast<object>().ToList())
            .Set("spent", o.BaselineSpent.OrderBy(p => p.Key).Select(p => (object)new List<object> { p.Key, p.Value }).ToList()));

        static void CommandReviewBaselines()
        {
            var (game, units) = ObjectiveScene("rifleman"); var w = game.World; ObjectiveConfig(w); Run(w, 0.5f);
            // A valid tech proposal (even a quote) needs the normal research prerequisites.
            foreach (var key in TechBuildings)
            {
                var spot = w.FindPlacement(0, key);
                if (spot.HasValue) w.SpawnStructure(0, key, spot.Value, 1f);
            }
            w.Teams[0].Add("steel", 5000); w.Teams[0].Add("circuits", 5000);
            string baseline = CommandBaseline(ObjectiveOf(w)); units[0].Hp -= 5;
            var commands = new[] {
                Cmd("type", "say", "text", "Still considering."), StrategyCommand("Hold west.", "ore"),
                Cmd("type", "propose_tech", "name", "Hardy", "base", "rifleman", "hp", 150, "speed", 0.8f, "dry_run", true),
                Cmd("type", "propose_tech", "name", "Hardier", "base", "rifleman", "hp", 155, "speed", 0.8f)
            };
            foreach (var command in commands)
            {
                var result = Commands.Execute(w, 0, command); w.LogCommand(0, command);
                Check(Ok(result) && baseline == CommandBaseline(ObjectiveOf(w)),
                    $"CommandReviewBaselines: {command.Str("type")} preserves HP/member/spending baselines and command tick ");
            }
            // The remaining log exclusions are handled by Game's lobby path before Commands.Execute.
            foreach (string type in new[] { "ready", "start_match" })
            {
                game.Execute(0, Cmd("type", type, "opponent", "scripted")); w.LogCommand(0, Cmd("type", type));
                Check(baseline == CommandBaseline(ObjectiveOf(w)), $"CommandReviewBaselines: lobby {type} cannot reset active objective baselines");
            }
            Check(w.CommandLog.Count == 1 && w.CommandLog[0].Cmd.Str("type") == "set_strategy",
                "CommandReviewBaselines: all four log exclusions remain excluded and strategy is replayed");
            var copy = Resume(Snap(game));
            Commands.Execute(w, 0, Cmd("type", "say", "text", "More deliberation."));
            Commands.Execute(w, 0, Cmd("type", "objective", "id", "west-plan", "pause", true));
            Commands.Execute(copy.World, 0, Cmd("type", "objective", "id", "west-plan", "pause", true));
            Check(Json.Write(Events(w, 0).Last(e => e.Str("kind") == "decision_needed")["changed_since_command"]) ==
                Json.Write(Events(copy.World, 0).Last(e => e.Str("kind") == "decision_needed")["changed_since_command"]),
                "CommandReviewBaselines: skipped chat preserves the same subsequent decision changes as replay");
        }
    }
}
