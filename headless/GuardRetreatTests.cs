using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static List<object> RetreatPoint(float x = 20, float y = 30) => new List<object> { (double)x, (double)y };
        static SquadGuard RetreatGuard(Game game) => game.World.Teams[0].Squads["west"].Guards;
        static int RetreatNotes(SquadGuard g, string kind, string rule) => g.Notes.Select(n => (Dictionary<string, object>)Json.Parse(n))
            .Count(n => n.Str("kind") == kind && n.Str("rule") == rule);

        static void GuardRetreats()
        {
            HpGuardDefaults(); HpGuardBand(); HpGuardDirectOrder(); RetreatGuardPersistence(); StrengthGuardLatch(); RetreatGuardKnowledge();
        }

        static void HpGuardDefaults()
        {
            var (game, units) = GuardScene("light_tank"); var w = game.World; var unit = units[0];
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 45, "y", 30));
            unit.Hp = unit.Def.MaxHp * 0.1f;
            Run(w, 1);
            Check(!RetreatGuard(game).Enabled && RetreatGuard(game).Notes.Count == 0 && unit.OrderPos.Equals(new Vec2(45, 30)),
                  "unconfigured squads do not retreat damaged units or raise guard events");
            var invalid = GuardConfig(w, "retreat_below_pct", 40);
            Check(!Ok(invalid) && !RetreatGuard(game).Enabled, "an HP guard without the agent's destination is rejected atomically");
            Check(Ok(GuardConfig(w, "retreat_below_pct", 40, "to", RetreatPoint())), "HP retreat starts only with an explicit threshold and destination");
            Run(w, 0.4f);
            Check(RetreatGuard(game).Units[unit.Id].HpRetreated && RetreatNotes(RetreatGuard(game), "guard_triggered", "retreat_below_pct") == 1,
                  "the enabled HP guard records the triggering own-unit fact once");
        }

        static void HpGuardBand()
        {
            var (game, units) = GuardScene("light_tank"); var w = game.World; var unit = units[0];
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 55, "y", 30));
            GuardConfig(w, "retreat_below_pct", 40, "to", RetreatPoint());
            unit.Hp = unit.Def.MaxHp * 0.39f; Run(w, 0.4f);
            var g = RetreatGuard(game); var state = g.Units[unit.Id]; var goal = state.HpGoal;
            unit.Hp = unit.Def.MaxHp * 0.44f; Run(w, 6);
            Check(state.HpSpent && state.HpRetreated && state.HpGoal.Equals(goal) && RetreatNotes(g, "guard_triggered", "retreat_below_pct") == 1,
                  "healing inside the five-point HP exit band never rearms or changes the retreat goal");
            unit.Hp = unit.Def.MaxHp * 0.46f; Run(w, 0.4f);
            Check(!state.HpSpent && state.HpRetreated && Vec2.Dist(unit.OrderPos, goal) < 1, "healing beyond the exit band rearms after cooldown without returning to the old advance");
            unit.Hp = unit.Def.MaxHp * 0.39f; Run(w, 6);
            Check(state.HpRetreated && RetreatNotes(g, "guard_triggered", "retreat_below_pct") == 1, "a still-latched retreat is not reissued after healing or a later wound");

            var (coolGame, coolUnits) = GuardScene("light_tank"); var cw = coolGame.World; var cu = coolUnits[0];
            GuardConfig(cw, "retreat_below_pct", 40, "to", RetreatPoint());
            cu.Hp = cu.Def.MaxHp * 0.39f; Run(cw, 0.4f); cu.Hp = cu.Def.MaxHp * 0.8f;
            Run(cw, 2);
            Check(RetreatGuard(coolGame).Units[cu.Id].HpSpent, "even full healing cannot rearm the HP guard inside its five-second cooldown");
        }

        static void HpGuardDirectOrder()
        {
            var (game, units) = GuardScene("light_tank"); var w = game.World; var unit = units[0];
            GuardConfig(w, "retreat_below_pct", 40, "to", RetreatPoint());
            unit.Hp = unit.Def.MaxHp * 0.39f; Run(w, 0.4f);
            Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { unit.Id }, "x", 50, "y", 30));
            Run(w, 6);
            var g = RetreatGuard(game); var state = g.Units[unit.Id];
            Check(!state.HpRetreated && state.HpSpent && Vec2.Dist(unit.OrderPos, new Vec2(50, 30)) < 1 && RetreatNotes(g, "guard_triggered", "retreat_below_pct") == 1,
                  "a direct order below the HP threshold clears the retreat latch and wins for the existing wound");
            unit.Hp = unit.Def.MaxHp * 0.41f; Run(w, 1); unit.Hp = unit.Def.MaxHp * 0.39f; Run(w, 1);
            Check(!state.HpRetreated && RetreatNotes(g, "guard_triggered", "retreat_below_pct") == 1, "crossing the entry threshold without leaving the rearm band does not cancel the direct order");
            unit.Hp = unit.Def.MaxHp * 0.8f; Run(w, 6); unit.Hp = unit.Def.MaxHp * 0.39f; Run(w, 0.4f);
            Check(state.HpRetreated && RetreatNotes(g, "guard_triggered", "retreat_below_pct") == 2, "only a rearmed new wound may trigger HP retreat after the direct order");
        }

        static void RetreatGuardPersistence()
        {
            var (game, units) = GuardScene("light_tank", "light_tank"); var w = game.World;
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 55, "y", 30, "together", true,
                "waypoints", new List<object> { new List<object> { 60d, 30d } }));
            GuardConfig(w, "retreat_below_pct", 40, "to", RetreatPoint());
            foreach (var unit in units) unit.Hp = unit.Def.MaxHp * 0.39f;
            Run(w, 0.4f);
            string json = Snap(game); var back = Resume(json); var bg = RetreatGuard(back);
            Check(Snap(back) == json && units.All(e => bg.Units[e.Id].HpRetreated && bg.Units[e.Id].HpSpent && bg.Units[e.Id].Action == GuardAction.HpRetreat),
                  "HP spent state, owned retreat slots and independent continuations survive a snapshot exactly");
            Check(units.All(e => bg.Units[e.Id].Continuation != null && bg.Units[e.Id].Continuation.Order == Order.Move &&
                  bg.Units[e.Id].Continuation.Group != 0 && bg.Units[e.Id].Continuation.Waypoints.Count == 1),
                  "retreat continuations preserve the interrupted together order and its remaining waypoints");
            int notes = bg.Notes.Count; Run(back.World, 6);
            Check(bg.Notes.Count == notes && units.All(e => bg.Units[e.Id].HpRetreated), "resuming a latched HP retreat does not repeat its intervention");
            Check(Vec2.Dist(bg.Units[units[0].Id].HpGoal, bg.Units[units[1].Id].HpGoal) >= 1.19f, "simultaneous HP retreats reserve separate destinations");
        }

        static void StrengthGuardLatch()
        {
            var (game, units) = GuardScene("light_tank", "light_tank", "light_tank"); var w = game.World;
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 55, "y", 30));
            GuardConfig(w, "withdraw_below_strength_pct", 75, "to", RetreatPoint());
            w.Remove(units[2]); Run(w, 0.4f);
            var g = RetreatGuard(game);
            Check(g.StrengthWithdrawn && units.Take(2).All(e => g.Units[e.Id].StrengthAssigned) &&
                  RetreatNotes(g, "guard_triggered", "withdraw_below_strength_pct") == 1 && RetreatNotes(g, "decision_needed", "withdraw_below_strength_pct") == 1,
                  "a casualty-driven strength drop latches one withdrawal and one critical decision packet");
            var observations = (Dictionary<string, object>)Json.Parse(Json.Write(w.Observe.Log(0).Read(null, 0, new ObsFilter { Full = true }, 500)));
            Check(observations.Arr("events").OfType<Dictionary<string, object>>()
                  .Any(e => e.Str("kind") == "decision_needed" && e.Str("sev") == "critical"), "strength withdrawal delivers decision_needed at critical severity to observe");
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "add", new int[0])); Run(w, 6);
            Check(g.StrengthWithdrawn && RetreatNotes(g, "decision_needed", "withdraw_below_strength_pct") == 1,
                  "recovered strength cannot automatically send a withdrawn squad back into battle");
            Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { units[0].Id }, "x", 50, "y", 30)); Run(w, 6);
            Check(g.Units[units[0].Id].StrengthExempt && Vec2.Dist(units[0].OrderPos, new Vec2(50, 30)) < 1 && g.Units[units[1].Id].StrengthAssigned,
                  "a direct unit order is exempt from the old squad-strength latch while other members stay withdrawn");
            var back = Resume(Snap(game)); var saved = RetreatGuard(back);
            Check(saved.StrengthWithdrawn && saved.Units[units[0].Id].StrengthExempt && saved.Units[units[1].Id].StrengthAssigned,
                  "strength withdrawal and direct-order exemptions survive save/resume");
            Run(back.World, 6);
            Check(RetreatNotes(saved, "decision_needed", "withdraw_below_strength_pct") == 1 && Vec2.Dist(back.World.Get(units[0].Id).OrderPos, new Vec2(50, 30)) < 1,
                  "a resumed strength latch neither repeats its packet nor overrides an exempt unit");
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            Commands.Execute(w, 0, Cmd("type", "rally", "structure_id", hq.Id, "squad", "west", "x", 55, "y", 30));
            var fresh = w.SpawnUnit(0, "light_tank", hq); Run(w, 0.4f);
            Check(g.Units[fresh.Id].StrengthAssigned && RetreatNotes(g, "decision_needed", "withdraw_below_strength_pct") == 1,
                  "new eligible rally members inherit the existing strength withdrawal without another decision packet");
        }

        static void RetreatGuardKnowledge()
        {
            var (game, units) = GuardScene("light_tank", "light_tank"); var w = game.World;
            GuardConfig(w, "retreat_below_pct", 40, "withdraw_below_strength_pct", 50, "to", RetreatPoint());
            var enemyHq = w.Owned(1).First(e => e.Def.Key == "command_center");
            var enemy = w.SpawnUnit(1, "light_tank", enemyHq);
            var at = new Vec2(75, 75); At(enemy, at);
            w.Step();
            Check(!w.IsVisibleTo(0, enemy), "retreat fog fixture keeps the enemy outside sight");
            enemy.Hp = 1; w.Remove(enemy); Run(w, 1);
            Check(!RetreatGuard(game).StrengthWithdrawn && RetreatGuard(game).Notes.Count == 0, "hidden enemy damage and death never trigger own HP or squad-strength guards");
            units[0].Hp = units[0].Def.MaxHp * 0.39f; Run(w, 0.4f);
            string notes = Json.Write(RetreatGuard(game).Notes);
            Check(RetreatGuard(game).Units[units[0].Id].HpRetreated && !notes.Contains("\"enemy\"") && !notes.Contains("75,75"),
                  "a low own HP triggers from the team's knowledge without exposing a hidden attacker's location");
            var own = Json.Write(ApiServer.ViewSnapshot(game, 0)); var rival = Json.Write(ApiServer.ViewSnapshot(game, 1));
            Check(own.Contains("hp_retreated") && !rival.Contains("hp_retreated") && !rival.Contains("hp_below_threshold"), "retreat goals and intervention facts stay private in rival mirrors");
        }

        static void GateGuardRetreats()
        {
            var (hpGame, hpUnits) = GuardScene("light_tank", "light_tank", "light_tank"); var hw = hpGame.World;
            Commands.Execute(hw, 0, Cmd("type", "move", "squad", "west", "x", 55, "y", 30));
            GuardConfig(hw, "retreat_below_pct", 40, "to", RetreatPoint());
            foreach (var unit in hpUnits) unit.Hp = unit.Def.MaxHp * 0.39f;
            Run(hw, 60);
            Check(hw.SettleCounts.Count == 0 && hpUnits.All(e => !e.Dead && Vec2.Dist(e.Pos, RetreatGuard(hpGame).Units[e.Id].HpGoal) < 2) &&
                  RetreatNotes(RetreatGuard(hpGame), "guard_triggered", "retreat_below_pct") == hpUnits.Count,
                  $"HP guard gate: three retreats arrive once over 60 seconds with zero settles ({Settles(hw)})");

            var (strengthGame, strengthUnits) = GuardScene("light_tank", "light_tank", "light_tank", "light_tank"); var sw = strengthGame.World;
            Commands.Execute(sw, 0, Cmd("type", "move", "squad", "west", "x", 55, "y", 30));
            GuardConfig(sw, "withdraw_below_strength_pct", 75, "to", RetreatPoint());
            sw.Remove(strengthUnits[2]); sw.Remove(strengthUnits[3]); Run(sw, 60);
            Check(sw.SettleCounts.Count == 0 && strengthUnits.Take(2).All(e => !e.Dead && Vec2.Dist(e.Pos, RetreatGuard(strengthGame).Units[e.Id].StrengthGoal) < 2) &&
                  RetreatNotes(RetreatGuard(strengthGame), "guard_triggered", "withdraw_below_strength_pct") == 1 &&
                  RetreatNotes(RetreatGuard(strengthGame), "decision_needed", "withdraw_below_strength_pct") == 1,
                  $"strength guard gate: withdrawal stays latched over 60 seconds with zero settles ({Settles(sw)})");
        }
    }
}
