using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        public static int RunGuards()
        {
            SquadGuardBehaviors(); GateSquadGuards(); SnapshotCoversEveryField();
            Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} guard tests FAILED.");
            return failures == 0 ? 0 : 1;
        }

        static void SquadGuardBehaviors()
        {
            GuardData(); GuardConfiguration(); GuardSupport(); GuardScreen(); GuardContinuationAndPriorities(); GuardRetreats(); GuardPriorityAndLoadedTruck(); GuardMotionHandoff();
        }

        static (Game game, List<Entity> units) GuardScene(params string[] keys)
        {
            var game = new Game(new GameConfig { Seed = 7, MapSize = 80, Controllers = new[] { "llm", "llm" } }); var w = game.World;
            foreach (var t in w.Teams) t.ProtectedUntil = 0;
            foreach (var e in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(e);
            Array.Fill(w.Map.Tiles, Terrain.Grass);
            var units = keys.Select((key, i) => At(Mine0(w, key), new Vec2(30.5f + i * 2, 30.5f))).ToList();
            var enemyHq = w.Owned(1).First(e => e.Def.Key == "command_center");
            At(w.SpawnUnit(1, "rifleman", enemyHq), new Vec2(75.5f, 75.5f));
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "add", units.Select(e => (object)(double)e.Id).ToList()));
            return (game, units);
        }

        static JObj GuardConfig(World w, params object[] kv)
        {
            var c = Cmd(kv); c["type"] = "guard"; c["squad"] = "west"; return Commands.Execute(w, 0, c);
        }

        static int GuardNotes(SquadGuard g, string kind, string rule) => g.Notes.Select(n => (Dictionary<string, object>)Json.Parse(n))
            .Count(n => n.Str("kind") == kind && n.Str("rule") == rule);

        static void GuardConfiguration()
        {
            var (game, units) = GuardScene("rifleman"); var w = game.World; string original = Snap(game);
            Check(Ok(GuardConfig(w)) && Snap(game) == original, "an empty guard command leaves the unconfigured sim snapshot unchanged");
            Check(!Ok(GuardConfig(w, "retreat_below_pct", 40)) && !Ok(GuardConfig(w, "withdraw_below_strength_pct", 50)),
                "both retreat guards require an explicit agent destination");
            Check(!Ok(GuardConfig(w, "hold_for_support", Cmd("within", 0))) && !Ok(GuardConfig(w, "screen", Cmd("roles", new List<object> { "unknown" }, "behind", 4))),
                "support distance and exact screen unit keys are validated atomically");
            Check(Ok(GuardConfig(w, "hold_for_support", Cmd("within", 6), "screen", Cmd("roles", new List<object> { "rifleman" }, "behind", 4))),
                "the agent can configure support and screen without enabling retreat guards");
            Check(Ok(GuardConfig(w, "retreat_below_pct", 40, "to", new List<object> { 20d, 30d })) &&
                w.Teams[0].Squads["west"].Guards.SupportWithin == 0 && w.Teams[0].Squads["west"].Guards.ScreenRoles.Count == 0,
                "a replacement guard config turns omitted guards off");
        }

        static void GuardSupport()
        {
            var (game, units) = GuardScene("light_tank", "rifleman"); var w = game.World;
            At(units[0], new Vec2(44.5f, 30.5f)); At(units[1], new Vec2(30.5f, 30.5f));
            Commands.Execute(w, 0, Cmd("type", "attack_move", "squad", "west", "x", 60, "y", 30));
            GuardConfig(w, "hold_for_support", Cmd("within", 6)); Run(w, 0.1f);
            var guard = w.Teams[0].Squads["west"].Guards; var lead = guard.Units[units[0].Id];
            Check(lead.SupportHeld && units[0].Order == Order.Idle && units[0].GuardMotionPaused,
                "support guard holds a leader only when the rear is beyond the supplied distance");
            var stopped = units[0].Pos;
            var foe = Enemy(w, "rifleman", stopped + new Vec2(5.5f, 0)); foe.Cooldown = 10000;
            Run(w, 2);
            Check(Vec2.Dist(units[0].Pos, stopped) < 0.1f && lead.SupportHeld && units[0].Order == Order.Idle,
                "a held leader cannot chase or respond by leaving its post when a visible enemy appears");
            w.Remove(foe);
            Run(w, 18);
            Check(!lead.SupportHeld && units[0].Order != Order.Idle && GuardNotes(guard, "guard_released", "hold_for_support") == 1,
                "the support exit band and cooldown restore the saved advance once the rear catches up");
            Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { units[0].Id }, "x", 65, "y", 25));
            Check(!lead.SupportHeld && !units[0].GuardMotionPaused && lead.Continuation == null,
                "a direct order clears a support hold and its old continuation immediately");
            Run(w, 1);
            Check(units[0].OrderPos.Equals(new Vec2(65, 25)), "standing support does not overwrite the direct route inside its cooldown");
            GuardConfig(w);
            Check(units.All(e => e.SpeedCap == 0), "clearing support restores the original command's pace rather than leaving a slow speed cap");

            var healer = GuardScene("medic", "rifleman", "rifleman"); w = healer.game.World;
            At(healer.units[0], new Vec2(44.5f, 30.5f)); At(healer.units[1], new Vec2(30.5f, 30.5f));
            At(healer.units[2], new Vec2(49, 30.5f)); healer.units[2].Hp = 30;
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 60, "y", 30));
            GuardConfig(w, "hold_for_support", Cmd("within", 6)); Run(w, 1);
            Check(healer.units[0].Order == Order.Idle && Vec2.Dist(healer.units[0].Pos, new Vec2(44.5f, 30.5f)) < 0.1f,
                "a held unarmed leader cannot replace the guard order with a medical trip to a nearby casualty");
        }

        static void GuardScreen()
        {
            var (game, units) = GuardScene("light_tank", "artillery"); var w = game.World;
            Commands.Execute(w, 0, Cmd("type", "attack_move", "squad", "west", "x", 60, "y", 30));
            GuardConfig(w, "screen", Cmd("roles", new List<object> { "artillery" }, "behind", 4)); Run(w, 0.1f);
            var g = w.Teams[0].Squads["west"].Guards; var u = g.Units[units[1].Id];
            Check(u.ScreenActive && !g.Units[units[0].Id].ScreenActive && u.ScreenGoal.X < units[0].Pos.X - 3,
                "screen applies only to exact configured keys, behind the front on the agent's commanded route");
            var before = u.ScreenGoal;
            var hidden = Enemy(w, "rifleman", new Vec2(75, 70)); hidden.Cooldown = 10000;
            Run(w, 2);
            Check(u.ScreenGoal.Equals(before) && GuardNotes(g, "guard_triggered", "screen") == 1 && !w.IsVisibleTo(0, hidden),
                "screen goals stay sticky through the cooldown and do not use hidden enemies to choose a bearing");
            At(units[0], new Vec2(65, 30)); w.Tick += 5 * World.TickRate; SquadGuards.Step(w, 0);
            Check(u.ScreenGoal.X < units[0].Pos.X - 3,
                "passing the commanded destination cannot reverse the screen bearing and send rear units in front");
            Run(w, 40);
            Check(units[1].Pos.X < units[0].Pos.X - 2 && units[1].GuardMotionPaused,
                "the screened unit remains behind the commanded front after it arrives");
            GuardConfig(w);
            Check(!units[1].GuardMotionPaused && !w.Teams[0].Squads["west"].Guards.Enabled,
                "clearing screen releases its motion pause and continues the earlier agent intent");
        }

        static void GuardContinuationAndPriorities()
        {
            var (game, units) = GuardScene("light_tank", "rifleman"); var w = game.World;
            At(units[0], new Vec2(44, 30));
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 55, "y", 30, "waypoints", new List<object> { new List<object> { 60d, 30d } }));
            GuardConfig(w, "hold_for_support", Cmd("within", 6)); Run(w, 0.1f);
            var state = w.Teams[0].Squads["west"].Guards.Units[units[0].Id];
            Check(state.SupportHeld && state.Continuation.Waypoints.Count == 1, "support saves the command's queued route independently of refuel state");
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            w.BeginRefuel(units[0], hq); Run(w, 0.1f);
            Check(units[0].Order == Order.Refuel && state.Continuation.Waypoints.Count == 1 && state.SupportHeld,
                "support does not cancel a native refuel trip or reuse its Resume fields");
            string saved = Snap(game); var copy = Resume(saved);
            Check(Snap(copy) == saved && copy.World.Teams[0].Squads["west"].Guards.Units[units[0].Id].Continuation.Waypoints.Count == 1,
                "support, refuel and queued continuation state round-trip together");
            int tick = w.Tick; Run(w, 10); Run(copy.World, 10);
            Check(SnapForCompare(game, tick) == SnapForCompare(copy, tick), "guard and refuel continuation after a snapshot remain deterministic");
        }

        static void GateSquadGuards()
        {
            GateGuardRetreats();
            var support = GuardScene("light_tank", "rifleman"); var w = support.game.World;
            At(support.units[0], new Vec2(44, 30));
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 65, "y", 30));
            GuardConfig(w, "hold_for_support", Cmd("within", 6)); Run(w, 60);
            var guard = w.Teams[0].Squads["west"].Guards;
            Check(w.SettleCounts.Count == 0 && GuardNotes(guard, "guard_triggered", "hold_for_support") == 1 &&
                GuardNotes(guard, "guard_released", "hold_for_support") == 1,
                $"support guard gate: one hold/release, no oscillation or settles over 60 seconds ({Settles(w)})");
            var screen = GuardScene("light_tank", "artillery"); w = screen.game.World;
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 65, "y", 30));
            GuardConfig(w, "screen", Cmd("roles", new List<object> { "artillery" }, "behind", 4)); Run(w, 60);
            guard = w.Teams[0].Squads["west"].Guards;
            Check(w.SettleCounts.Count == 0 && screen.units.All(e => !e.Dead) && screen.units[1].Pos.X < screen.units[0].Pos.X - 2 &&
                GuardNotes(guard, "guard_triggered", "screen") <= 13,
                $"screen guard gate: sticky trailing goals, bounded interventions and zero settles over 60 seconds ({Settles(w)})");

            var loop = GuardScene("light_tank", "rifleman"); w = loop.game.World;
            At(loop.units[0], new Vec2(44, 30));
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 60, "y", 30,
                "waypoints", new List<object> { new List<object> { 25d, 30d } }, "loop", true));
            GuardConfig(w, "hold_for_support", Cmd("within", 6)); Run(w, 60);
            guard = w.Teams[0].Squads["west"].Guards;
            Check(w.SettleCounts.Count == 0 && loop.units.All(e => e.WaypointLoop) && GuardNotes(guard, "guard_triggered", "hold_for_support") == 1 &&
                GuardNotes(guard, "guard_released", "hold_for_support") == 1,
                $"support patrol gate: different waypoint legs retain one hold/release and zero settles over 60 seconds ({Settles(w)})");
            foreach (int seed in new[] { 1, 2, 3 }) GateGuardHandoffFixture(seed);
        }

        static void GateGuardHandoffFixture(int seed)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "fixtures"))) dir = dir.Parent;
            if (dir == null) { Check(false, "guard handoff movement fixtures folder exists"); return; }
            string file = "guard-handoff-seed" + seed;
            var game = Resume(File.ReadAllText(Path.Combine(dir.FullName, "fixtures", file + ".json")));
            var record = (Dictionary<string, object>)Json.Parse(File.ReadAllText(Path.Combine(dir.FullName, "fixtures", file + "-commands.json")));
            var replay = record.Objs("replay").OrderBy(c => c.In("tick")).ThenBy(c => c.In("seq")).ToList();
            int next = 0, before = game.World.SettleCounts.Values.Sum(), errors = game.World.Errors;
            for (int i = 0; i < 60 * World.TickRate; i++)
            {
                while (next < replay.Count && replay[next].In("tick") <= game.World.Tick)
                {
                    var command = replay[next++];
                    CommandSafety.ExecuteBatch(game, command.In("team"), command.Obj("command"), "slow-agent");
                }
                game.Advance(World.Dt);
            }
            int delta = game.World.SettleCounts.Values.Sum() - before;
            Check(delta == 0 && next == replay.Count && game.World.Errors == errors,
                $"guard handoff seed {seed}: recorded commands after a pre-handoff snapshot have zero new settles over 60 seconds ({delta})");
        }

        static void GuardPriorityAndLoadedTruck()
        {
            var native = GuardScene("light_tank"); var w = native.game.World; var unit = native.units[0];
            unit.RetreatBelow = 0.8f;
            GuardConfig(w, "retreat_below_pct", 40, "to", new List<object> { 20d, 30d });
            unit.Hp = unit.Def.MaxHp * 0.39f; Run(w, 0.5f);
            var state = w.Teams[0].Squads["west"].Guards.Units[unit.Id];
            Check(state.HpRetreated && unit.OrderPos.Equals(state.HpGoal) && unit.LastOrderBy == "squad_guard",
                "a configured squad HP guard owns its explicit destination instead of competing with native retreat");
            Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { unit.Id }, "x", 50, "y", 30)); Run(w, 6);
            Check(unit.OrderPos.Equals(new Vec2(50, 30)) && state.HpSpent && !state.HpRetreated,
                "a manual order for the existing guard wound is not immediately overridden by native retreat either");
            var plain = GuardScene("light_tank", "rifleman"); w = plain.game.World; unit = plain.units[0];
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 55, "y", 30));
            GuardConfig(w, "hold_for_support", Cmd("within", 6)); unit.RetreatBelow = 0.8f; unit.Hp = unit.Def.MaxHp * 0.5f; Run(w, 1);
            Check(unit.Retreating && unit.RetreatSpent && !w.Teams[0].Squads["west"].Guards.Units[unit.Id].HpRetreated,
                "an unrelated support guard preserves the previously configured native HP retreat");

            var loaded = GuardScene("mining_truck"); w = loaded.game.World; unit = loaded.units[0];
            unit.Cargo = 90; unit.CargoType = Map.Iron; unit.Hp = unit.Def.MaxHp * 0.39f;
            var home = w.Teams[0].StartPos;
            GuardConfig(w, "retreat_below_pct", 40, "to", new List<object> { (double)home.X, (double)home.Y }); Run(w, 60);
            Check(w.SettleCounts.Count == 0 && !unit.Dead && unit.Cargo == 0 && unit.Order != Order.Harvest && SquadGuards.Controls(w, unit),
                $"a loaded guarded miner unloads at base and remains withdrawn, with zero settles over 60 seconds ({Settles(w)})");
        }

        static void GuardMotionHandoff()
        {
            var (game, units) = GuardScene("rifleman", "light_tank"); var w = game.World;
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 55, "y", 30));
            GuardConfig(w, "retreat_below_pct", 40, "to", new List<object> { 20d, 30d });
            units[0].Hp = units[0].Def.MaxHp * 0.39f; Run(w, 4);
            units[0].DanceVel = new Vec2(-0.04f, 0); units[0].DanceSpeed = 0.04f; units[0].DanceFor = 1.9f;
            Commands.Execute(w, 0, Cmd("type", "attack_move", "squad", "west", "x", 55, "y", 30, "together", true));
            Check(units[0].DanceVel.LengthSq == 0 && units[0].DanceSpeed == 0 && units[0].DanceFor == 0,
                "a guard-to-direct handoff starts a fresh motion window instead of inheriting the retreat's opposite velocity");
            units[0].DanceVel = new Vec2(0.02f, 0); units[0].DanceSpeed = 0.02f; units[0].DanceFor = 1;
            Commands.Execute(w, 0, Cmd("type", "attack_move", "units", new[] { units[0].Id }, "x", 56, "y", 30));
            Check(units[0].DanceFor == 1 && units[0].DanceSpeed == 0.02f,
                "repeated direct commands after detachment do not reset the motion detector");

            var cluster = GuardScene("light_tank", "rifleman", "rifleman", "rifleman", "rifleman", "rifleman", "rifleman"); w = cluster.game.World;
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 65, "y", 30));
            GuardConfig(w, "screen", Cmd("roles", new List<object> { "rifleman" }, "behind", 4)); Run(w, 0.1f);
            var g = w.Teams[0].Squads["west"].Guards;
            Check(cluster.units.Skip(1).All(e => g.Units[e.Id].ScreenGoal.X <= cluster.units[0].Pos.X - 3.9f),
                "every reserved screen slot stays behind the supplied line, including the formation's front row");
        }

        static void GuardData()
        {
            var g = new SquadGuard(); Check(!g.Enabled, "squad guards are off unless configured");
            g.SupportWithin = 6; g.To = new Vec2(10, 12); g.HasTo = true;
            g.Units[9] = new GuardUnit { SupportHeld = true, SupportAt = 3, Continuation = new GuardContinuation { Order = Order.AttackMove, Pos = new Vec2(45, 12) } };
            var back = SquadGuard.Load((Dictionary<string, object>)Json.Parse(Json.Write(g.Save())));
            Check(Json.Write(g.Save()) == Json.Write(back.Save()), "guard config, bands, control state and independent continuations round-trip exactly");
        }
    }
}
