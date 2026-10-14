using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static void ObjectiveInteractions()
        {
            ObjectiveHpGuardReplacement(); ObjectiveSupportDirectResume(); ObjectiveFuelReplacement();
            ObjectiveScreenCompletion(); ObjectiveNativeRetreatWake(); ObjectiveStrengthResumeBlocker(); ObjectiveAllHpBlocked(); ObjectiveDisbandAuthority();
        }

        static void ObjectiveHpGuardReplacement()
        {
            var (game, units) = ObjectiveScene("light_tank", "rifleman"); var w = game.World; var unit = units[0];
            GuardConfig(w, "retreat_below_pct", 40, "to", ObjectivePoint(20, 30));
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30)); Run(w, 0.2f);
            unit.Hp = unit.Def.MaxHp * 0.3f; Run(w, 0.4f);
            var guarded = w.Teams[0].Squads["west"].Guards.Units[unit.Id];
            Check(guarded.HpRetreated && guarded.BaseObjective == "west-plan" && guarded.Continuation.Pos.X > 50 && unit.LastOrderBy == "squad_guard", "an HP guard owns one retreat while retaining the objective's base intent");
            ObjectiveConfig(w, "id", "latest-go", "verb", "go", "at", ObjectivePoint(50, 32)); Run(w, 0.4f);
            Check(guarded.HpRetreated && Vec2.Dist(unit.OrderPos, guarded.HpGoal) < 0.1f && ObjectiveOf(w).Id == "latest-go",
                  "replacing an objective stages its new destination without overriding a stronger HP retreat");
            GuardConfig(w); Run(w, 0.4f);
            Check(!ObjectiveOf(w).Paused && unit.LastOrderBy == "objective" && Vec2.Dist(unit.OrderPos, ObjectiveOf(w).Members[unit.Id].Goal) < 0.1f,
                  "explicit guard replacement hands control to the latest active objective instead of losing its cached base intent");
        }

        static void ObjectiveSupportDirectResume()
        {
            var (game, units) = ObjectiveScene("light_tank", "rifleman"); var w = game.World;
            At(units[0], new Vec2(44, 30)); At(units[1], new Vec2(30, 30));
            GuardConfig(w, "hold_for_support", Cmd("within", 6));
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(65, 30)); Run(w, 0.4f);
            var state = w.Teams[0].Squads["west"].Guards.Units[units[0].Id];
            Check(state.SupportHeld && state.BasePending && state.Continuation.Pos.X > 60 && units[0].Order == Order.Idle,
                  "support holds the leader while preserving its objective destination as the single pending intent");
            Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { units[0].Id }, "x", 20, "y", 30)); Run(w, 2);
            Check(ObjectiveOf(w).Detached.Contains(units[0].Id) && !state.SupportHeld && !state.BasePending && units[0].OrderPos.X < 25,
                  "a direct order removes both stale objective authority and the old support continuation");
            ObjectiveConfig(w, "resume", true); Run(w, 0.4f);
            Check(!ObjectiveOf(w).Detached.Contains(units[0].Id) && state.BaseObjective == "west-plan" && state.Continuation.Pos.X > 60,
                  "explicit resume stages the objective's route again without restoring the unrelated direct destination");
            Run(w, 60);
            Check(w.SettleCounts.Count == 0 && w.Errors == 0 && units.All(e => !e.Dead && Vec2.Dist(e.Pos, new Vec2(65, 30)) < 7),
                  $"support and objective resumption complete their shared advance without a dance ({Settles(w)})");
        }

        static void ObjectiveFuelReplacement()
        {
            var (game, units) = ObjectiveScene("light_tank"); var w = game.World; var unit = units[0];
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center"); At(unit, hq.Center + new Vec2(5, 0));
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30)); Run(w, 0.2f);
            unit.Fuel = unit.FuelMax * 0.5f; w.BeginRefuel(unit, hq);
            var originalFuelDestination = unit.ResumePos;
            ObjectiveConfig(w, "pause", true); Run(w, 0.2f);
            Check(unit.Order == Order.Refuel && unit.ResumePos.Equals(originalFuelDestination), "an objective pause waits for native refuelling without overwriting its own resume fields");
            ObjectiveConfig(w, "id", "fuel-latest", "verb", "go", "at", ObjectivePoint(45, 30)); Run(w, 0.2f);
            Check(unit.Order == Order.Refuel && unit.ResumePos.Equals(originalFuelDestination) && w.Teams[0].Squads["west"].Guards.Units[unit.Id].BasePending,
                  "a replacement objective stages its later goal behind the ongoing refuel trip");
            var copy = Resume(Snap(game)); int from = w.Tick;
            Run(w, 30); Run(copy.World, 30);
            Check(unit.Order != Order.Refuel && unit.FuelFraction > 0.9f && !ObjectiveOf(w).Paused &&
                  (unit.LastOrderBy == "objective" || ObjectiveOf(w).Done) && Vec2.Dist(unit.OrderPos, originalFuelDestination) > 3,
                  "after refuelling the current objective wins over the saved obsolete destination");
            Check(SnapForCompare(game, from) == SnapForCompare(copy, from), "pending objective replacement and native fuel continuation remain deterministic across a restart");
        }

        static void ObjectiveScreenCompletion()
        {
            var (game, units) = ObjectiveScene("light_tank", "artillery"); var w = game.World;
            GuardConfig(w, "screen", Cmd("roles", new List<object> { "artillery" }, "behind", 4));
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(45, 30), "radius", 6); Run(w, 0.4f);
            var guard = w.Teams[0].Squads["west"].Guards;
            Check(guard.Units[units[1].Id].ScreenActive && !ObjectiveOf(w).Done, "screen first follows the explicitly commanded advancing front");
            Run(w, 60);
            Check(units.All(e => !e.Dead && Vec2.Dist(e.Pos, new Vec2(45, 30)) <= 6) && !guard.Units[units[1].Id].ScreenActive &&
                  Events(w, 0).Any(e => e.Str("kind") == "guard_released" && e.Str("rule") == "screen" && e.Str("reason") == "objective_front_arrived"),
                  "when the go front has arrived, screen releases its stale travel goal and restores the same objective's existing base intent");
            Check(ObjectiveOf(w).Done && w.SettleCounts.Count == 0, "go finishes at its supplied owned slots without a frozen screened straggler or a dance");
        }

        static void ObjectiveNativeRetreatWake()
        {
            var (game, units) = ObjectiveScene("light_tank", "rifleman"); var w = game.World; var tank = units[0];
            Commands.Execute(w, 0, Cmd("type", "set_retreat", "units", new[] { tank.Id }, "below_pct", 40));
            GuardConfig(w, "hold_for_support", Cmd("within", 6));
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30)); Run(w, 0.2f);
            tank.Hp = tank.Def.MaxHp * 0.3f; Run(w, 1);
            Check(tank.Retreating && tank.RetreatSpent && ObjectiveOf(w).Paused && ObjectiveOf(w).PauseReason == "native_retreat",
                  "an ongoing objective reports a native retreat that blocks its base intent while preserving the retreat order");
            int decisions = Events(w, 0).Count(e => e.Str("kind") == "decision_needed"); Run(w, 35);
            Check(decisions == 1 && Events(w, 0).Count(e => e.Str("kind") == "decision_needed") == 1 && tank.Retreating,
                  "a permanently latched native retreat wakes the commander once rather than leaving an active stalled objective");
            ObjectiveConfig(w, "resume", true); Run(w, 0.4f);
            Check(!tank.Retreating && tank.RetreatSpent && !ObjectiveOf(w).Paused, "explicit resumption clears native retreat authority while preserving the spent wound");
        }

        static void ObjectiveStrengthResumeBlocker()
        {
            var (game, units) = ObjectiveScene("light_tank", "light_tank"); var w = game.World;
            GuardConfig(w, "withdraw_below_strength_pct", 75, "to", ObjectivePoint(20, 30));
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30)); w.Remove(units[1]); Run(w, 0.4f);
            var guard = w.Teams[0].Squads["west"].Guards; var unit = units[0];
            ObjectiveConfig(w, "resume", true); Run(w, 0.2f);
            Check(ObjectiveOf(w).Paused && ObjectiveOf(w).PauseReason == "guard_withdraw_below_strength_pct" && guard.StrengthWithdrawn &&
                  Events(w, 0).Count(e => e.Str("kind") == "decision_needed") == 2,
                  "resuming under an existing strength latch explains the still-blocked program without resetting the guard");
            ObjectiveConfig(w, "id", "strength-latest", "verb", "go", "at", ObjectivePoint(45, 30)); Run(w, 0.2f);
            Check(ObjectiveOf(w).Paused && ObjectiveOf(w).Id == "strength-latest" && Vec2.Dist(unit.OrderPos, guard.Units[unit.Id].StrengthGoal) < 0.1f &&
                  Events(w, 0).Count(e => e.Str("kind") == "decision_needed") == 3,
                  "a replacement objective reports an inherited strength blocker while the guard's retreat remains authoritative");
        }

        static void ObjectiveAllHpBlocked()
        {
            var (game, units) = ObjectiveScene("light_tank"); var w = game.World; var unit = units[0];
            GuardConfig(w, "retreat_below_pct", 40, "to", ObjectivePoint(20, 30));
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30)); Run(w, 0.2f); unit.Hp = unit.Def.MaxHp * 0.3f; Run(w, 0.4f);
            Check(ObjectiveOf(w).Paused && ObjectiveOf(w).PauseReason == "guard_retreat" && Events(w, 0).Count(e => e.Str("kind") == "decision_needed") == 1,
                  "an objective with every member under a latched HP retreat pauses and wakes its commander");
            unit.Hp = unit.Def.MaxHp * 0.8f; Run(w, 6);
            Check(ObjectiveOf(w).Paused && w.Teams[0].Squads["west"].Guards.Units[unit.Id].HpRetreated && unit.Pos.X < 24,
                  "healing all withdrawn members still does not return them to a paused advance");
            GuardConfig(w); Run(w, 0.2f);
            Check(ObjectiveOf(w).Paused && unit.Order == Order.Idle && unit.Pos.X < 24, "clearing an HP guard commits the paused hold at the unit's current place");
            ObjectiveConfig(w, "resume", true); Run(w, 0.4f);
            Check(!ObjectiveOf(w).Paused && unit.LastOrderBy == "objective" && unit.OrderPos.X > 50,
                  "only explicit resume after clearing the blocker hands all withdrawn members back to the advance");
        }

        static void ObjectiveDisbandAuthority()
        {
            var (game, units) = ObjectiveScene("rifleman"); var w = game.World;
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30)); Run(w, 0.4f);
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "disband", true));
            var objective = w.Teams[0].Objectives["west-plan"];
            Check(objective.Paused && units[0].Order == Order.Idle && units[0].ObjectiveId == null && !units[0].GuardMotionPaused,
                  "disband commits the requested pause before removing its writer and releases dangling objective authority");
            Run(w, 4);
            Check(units[0].Order == Order.Idle && !w.Teams[0].Squads.ContainsKey("west") && w.Errors == 0,
                  "a removed squad cannot continue its old objective advance through an abandoned queue");
        }

        static void GateObjectiveInteractions()
        {
            var (game, units) = ObjectiveScene("light_tank", "rifleman", "artillery"); var w = game.World;
            GuardConfig(w, "retreat_below_pct", 40, "hold_for_support", Cmd("within", 6),
                "screen", Cmd("roles", new List<object> { "artillery" }, "behind", 4), "to", ObjectivePoint(20, 30));
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(65, 30)); units[0].Hp = units[0].Def.MaxHp * 0.3f;
            var writes = units.ToDictionary(e => e.Id, _ => 0); int maximum = 0;
            for (int i = 0; i < 60 * World.TickRate; i++)
            {
                var touched = new List<Entity>(); w.Touched = touched;
                try { w.Step(); } finally { w.Touched = null; }
                foreach (var unit in units)
                {
                    int count = touched.Count(e => e.Id == unit.Id); writes[unit.Id] += count; maximum = Math.Max(maximum, count);
                }
            }
            Check(maximum <= 1 && w.SettleCounts.Count == 0 && w.Errors == 0 && units.All(e => !e.Dead),
                  $"combined HP, support, screen and go gate has one order write per unit per tick and zero settles over 60 seconds (max {maximum}; {Settles(w)})");
            Check(writes[units[0].Id] <= 3 && Events(w, 0).Count(e => e.Str("rule") == "retreat_below_pct" && e.Str("kind") == "guard_triggered") == 1,
                  "the stronger HP retreat remains sticky while weaker objective and formation intents continue evaluating");
        }
    }
}
