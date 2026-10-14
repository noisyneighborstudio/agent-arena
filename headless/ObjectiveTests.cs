using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        public static int RunObjectives()
        {
            LiteralObjectives(); GateObjectives(); GateObjectiveInteractions(); SnapshotCoversEveryField();
            Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} objective tests FAILED.");
            return failures == 0 ? 0 : 1;
        }
        static Objective ObjectiveOf(World w) => Objectives.ForSquad(w, 0, "west");
        static List<object> ObjectivePoint(float x, float y) => new List<object> { (double)x, (double)y };
        static void LiteralObjectives()
        {
            ObjectiveValidation(); ObjectiveGoAndAssembly(); ObjectiveHoldCombat(); ObjectiveEscortAndPatrol();
            ObjectiveDirectAndGuard(); ObjectiveFogAndPackets(); ObjectivePersistence(); ObjectiveNativeHandoff(); ObjectiveReinforcementTests(); ObjectiveInteractions();
        }

        static void ObjectiveValidation()
        {
            var (game, units) = ObjectiveScene("rifleman"); var w = game.World; string original = Snap(game);
            Check(w.Teams.All(t => t.Objectives.Count == 0) && units.All(e => e.ObjectiveId == null), "objectives are absent unless an agent configures them");
            Check(!Ok(ObjectiveConfig(w, "radius", 0)) && !Ok(ObjectiveConfig(w, "verb", "secure_mining_area")) && Snap(game) == original,
                "invalid radius and tactical verbs fail atomically without enabling anything");
            Check(!Ok(ObjectiveConfig(w, "wake", new List<object> { "choose_best_enemy" })) && !Ok(ObjectiveConfig(w, "assemble", Cmd("at", ObjectivePoint(20, 30), "min", 1.5f))),
                "wake conditions and assembly counts are literal and validated");
            Check(Ok(ObjectiveConfig(w, "wake", new List<object> { "objective_done" })), "the documented hold accepts objective_done without inventing a success condition");
            Run(w, 2); Check(!ObjectiveOf(w).Done && !ObjectiveOf(w).Paused, "an indefinite hold never completes itself or raises a done wake");
            Check(!Ok(ObjectiveConfig(w, "verb", "patrol", "waypoints", new List<object> { ObjectivePoint(40, 30), ObjectivePoint(41, 30) })),
                "a patrol too tight for the existing movement invariant is rejected");
            var foe = w.Owned(1).First(e => !e.IsStructure);
            Check(!Ok(ObjectiveConfig(w, "verb", "escort", "target_id", foe.Id)), "escort cannot follow an enemy through hidden world state");
            Check(!Ok(ObjectiveConfig(w, "verb", "escort", "target_id", units[0].Id)), "a squad cannot order its own escort target to follow itself");
            var crowded = ObjectiveScene("rifleman", "rifleman"); w = crowded.game.World;
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "east", "add", new[] { crowded.units[1].Id }));
            ObjectiveConfig(w, "radius", 0.2f); Run(w, 0.5f);
            Commands.Execute(w, 0, Cmd("type", "objective", "id", "east-plan", "squad", "east", "verb", "hold", "at", ObjectivePoint(40, 30), "radius", 0.2f)); Run(w, 0.5f);
            Check(w.Teams[0].Objectives["east-plan"].Paused && w.Teams[0].Objectives["east-plan"].PauseReason == "no_open_slots",
                "objectives pause when their supplied area has no distinct slot rather than sharing another objective's owned goal");
        }

        static void ObjectiveGoAndAssembly()
        {
            var (game, units) = ObjectiveScene("rifleman", "rifleman"); var w = game.World;
            Check(Ok(ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(42, 30))), "go records the exact supplied destination");
            Run(w, 20);
            Check(ObjectiveOf(w).Done && !ObjectiveOf(w).Paused && units.All(e => Vec2.Dist(e.Pos, new Vec2(42, 30)) <= 6),
                "go attack-moves to owned slots, completes once and then holds the supplied area");
            int orders = w.EventCounts.TryGetValue("objective_order", out int count) ? count : 0; Run(w, 10);
            Check(w.EventCounts["objective_order"] == orders, "a settled objective does not continuously replace its units' orders");
            var assembly = ObjectiveScene("rifleman", "rifleman"); w = assembly.game.World;
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30), "assemble", Cmd("at", ObjectivePoint(25, 30), "min", 3)); Run(w, 15);
            Check(!ObjectiveOf(w).Assembled && assembly.units.All(e => e.OrderPos.X < 35), "assembly waits for the agent's minimum and sends nobody ahead early");
            var fresh = At(Mine0(w, "rifleman"), new Vec2(25, 30));
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "add", new[] { fresh.Id })); Run(w, 0.2f);
            Check(ObjectiveOf(w).Assembled, "an explicitly added member satisfying the assembly minimum releases the march once");
            At(fresh, new Vec2(10, 20)); Run(w, 1);
            Check(ObjectiveOf(w).Assembled, "assembly release is latched instead of oscillating when a member leaves the point");
            var empty = ObjectiveScene(); w = empty.game.World; ObjectiveConfig(w); Run(w, 0.2f);
            Check(ObjectiveOf(w).Paused && ObjectiveOf(w).PauseReason == "squad_gone", "an empty squad has no implicit training or assembly behavior");
            var early = ObjectiveScene("light_tank", "rifleman"); w = early.game.World;
            At(early.units[0], new Vec2(44, 30)); At(early.units[1], new Vec2(10, 30));
            var outside = Enemy(w, "rifleman", new Vec2(49.7f, 30)); outside.Hp = 5000; outside.GuardMotionPaused = true; outside.Cooldown = 10000;
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(45, 30), "radius", 2); Run(w, 5);
            Check(!ObjectiveOf(w).Done && Objectives.Bounded(w, early.units[0]) && outside.Hp == 5000 && Vec2.Dist(early.units[0].Pos, new Vec2(45, 30)) <= 2,
                "an early go arrival holds its supplied area while the rest arrive instead of chasing or shooting outside it");
        }

        static void ObjectiveHoldCombat()
        {
            var (game, units) = ObjectiveScene("rifleman"); var w = game.World;
            At(units[0], new Vec2(40, 30)); ObjectiveConfig(w, "radius", 10);
            var enemy = Enemy(w, "rifleman", new Vec2(45.8f, 30)); enemy.Hp = 5000; enemy.GuardMotionPaused = true; enemy.Cooldown = 10000;
            Run(w, 20);
            Check(units[0].Pos.X > 41 && enemy.Hp < 5000 && units[0].Order == Order.Idle,
                "hold permits the ordinary in-area chase and engagement without yanking the unit back after leaving its slot");
            Check(w.EventCounts["objective_order"] <= 2 && w.SettleCounts.Count == 0, "a fixed in-area target causes no repeated hold orders or settles");
            w.Remove(enemy); Run(w, 10);
            Check(Vec2.Dist(units[0].Pos, new Vec2(40, 30)) < 1.6f, "after the engagement an idle unit returns to its own supplied hold slot");
            var outside = Enemy(w, "rifleman", new Vec2(51, 30)); outside.Hp = 5000; outside.GuardMotionPaused = true; outside.Cooldown = 10000;
            Run(w, 5);
            Check(Vec2.Dist(units[0].Pos, new Vec2(40, 30)) < 1.6f, "hold does not pursue a contact outside the agent's radius");
        }

        static void ObjectiveEscortAndPatrol()
        {
            var (game, units) = ObjectiveScene("rifleman", "rifleman"); var w = game.World;
            var target = At(Mine0(w, "mining_truck"), new Vec2(35, 30)); w.SetOrder(target, Order.Move, new Vec2(50, 30));
            Check(Ok(ObjectiveConfig(w, "verb", "escort", "target_id", target.Id, "radius", 6)), "escort takes one explicit own unit and no substitute target");
            Run(w, 35);
            Check(Vec2.Dist(ObjectiveOf(w).Anchor, target.Pos) <= 1.1f && units.All(e => !e.Dead && Vec2.Dist(e.Pos, target.Pos) <= 6),
                "escort follows the known moving target with stable cadence and bounded offsets");
            w.Remove(target); Run(w, 0.2f);
            Check(ObjectiveOf(w).Paused && ObjectiveOf(w).PauseReason == "escort_target_gone", "a dead escort target pauses instead of choosing another unit");
            var patrol = ObjectiveScene("rifleman", "rifleman"); w = patrol.game.World;
            ObjectiveConfig(w, "verb", "patrol", "waypoints", new List<object> { ObjectivePoint(40, 30), ObjectivePoint(25, 30) }); Run(w, 50);
            Check(w.EventCounts.TryGetValue("objective_phase", out int phases) && phases >= 2 && phases <= 8 && !ObjectiveOf(w).Done,
                "patrol advances a saved squad-wide waypoint phase and loops only the supplied route");
        }

        static void ObjectiveDirectAndGuard()
        {
            var (game, units) = ObjectiveScene("rifleman", "rifleman"); var w = game.World;
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30)); Run(w, 0.2f);
            Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { units[0].Id }, "x", 20, "y", 20)); Run(w, 6);
            Check(ObjectiveOf(w).Detached.Contains(units[0].Id) && units[0].ObjectiveId == null && units[0].OrderPos.Equals(new Vec2(20, 20)),
                "a direct named order detaches a unit and wins over its old objective");
            Check(Events(w, 0).Any(e => e.Str("kind") == "detached_by_order" && e.Str("source") == "objective"), "objective detachment explains its source in the owning feed");
            ObjectiveConfig(w, "resume", true); Run(w, 0.2f);
            Check(!ObjectiveOf(w).Detached.Contains(units[0].Id) && units[0].ObjectiveId == "west-plan", "explicit resume hands the squad's members back without silently doing so earlier");
            Commands.Execute(w, 0, Cmd("type", "set_retreat", "units", new[] { units[0].Id }, "below_pct", 40));
            Check(ObjectiveOf(w).Detached.Contains(units[0].Id), "a successful direct unit configuration also detaches rather than retaining hidden objective authority");

            var guarded = ObjectiveScene("light_tank", "light_tank"); w = guarded.game.World;
            GuardConfig(w, "withdraw_below_strength_pct", 75, "to", ObjectivePoint(20, 30)); ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30));
            w.Remove(guarded.units[1]); Run(w, 0.5f);
            var o = ObjectiveOf(w); var packets = Events(w, 0).Where(e => e.Str("kind") == "decision_needed").ToList();
            Check(o.Paused && o.PauseReason == "guard_withdraw_below_strength_pct" && packets.Count == 1 && packets[0].Obj("objective") != null,
                "a strength guard enriches one objective pause packet instead of sending duplicate critical decisions");
            Check(guarded.units[0].OrderPos.Equals(w.Teams[0].Squads["west"].Guards.Units[guarded.units[0].Id].StrengthGoal),
                "the shared writer keeps the guard's stronger withdrawal over the paused objective's hold");

            var membership = ObjectiveScene("light_tank", "rifleman"); w = membership.game.World;
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "east", "add", new[] { membership.units[1].Id }));
            GuardConfig(w, "retreat_below_pct", 40, "to", ObjectivePoint(20, 30)); ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(55, 30));
            Commands.Execute(w, 0, Cmd("type", "objective", "id", "east-plan", "squad", "east", "verb", "hold", "at", ObjectivePoint(35, 30), "radius", 6)); Run(w, 0.4f);
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "remove", new[] { membership.units[1].Id }));
            Check(!ObjectiveOf(w).Paused && !w.Teams[0].Objectives["east-plan"].Paused && membership.units[1].ObjectiveId == "east-plan" &&
                !w.Teams[0].Objectives["east-plan"].Detached.Contains(membership.units[1].Id) && w.Teams[0].Squads["west"].Guards.RetreatBelowPct == 40,
                "removing a live nonmember is a legal no-op that preserves both objectives and the actual member's guard configuration");
        }

        static void ObjectiveFogAndPackets()
        {
            var (game, units) = ObjectiveScene("rifleman"); var w = game.World;
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(45, 30), "wake", new List<object> { "enemy_strength_seen>squad" });
            var hidden = Enemy(w, "rifleman", new Vec2(75, 75)); hidden.Hp = 5000; hidden.GuardMotionPaused = true; hidden.Cooldown = 10000;
            Run(w, 0.5f);
            Check(!ObjectiveOf(w).Paused && !w.IsVisibleTo(0, hidden), "true enemy strength in fog cannot trigger an objective wake");
            At(hidden, units[0].Pos + new Vec2(3, 0)); Run(w, 0.5f);
            var o = ObjectiveOf(w); var packet = (Dictionary<string, object>)Json.Parse(o.Packet);
            Check(o.Paused && packet.Str("reason") == "enemy_strength_seen>squad" && packet.Obj("threats").Arr("visible").Count > 0,
                "only observed nearby combat HP triggers the literal enemy strength wake and enters its decision packet");
            Check(packet.Obj("changed_since_command").ContainsKey("orders") && packet.Obj("changed_since_command").ContainsKey("retreats") && packet.Obj("commitments").ContainsKey("reserves"),
                "decision packets include own order/retreat facts and open resource commitments");
            Check(!Events(w, 1).Any(e => e.Str("kind") == "decision_needed"), "objective decisions never enter a rival observation stream");
            string rival = Json.Write(ApiServer.ViewSnapshot(game, 1));
            Check(!rival.Contains("west-plan") && !rival.Contains("enemy_strength_seen>squad"), "objective configuration and saved decision packets remain private in rival mirrors");
            int before = Events(w, 0).Count(e => e.Str("kind") == "decision_needed"); Run(w, 5);
            Check(Events(w, 0).Count(e => e.Str("kind") == "decision_needed") == before, "a paused objective emits one decision packet and does not automatically rearm");

            var memory = ObjectiveScene("rifleman"); w = memory.game.World; At(memory.units[0], new Vec2(40, 30)); ObjectiveConfig(w);
            var contact = Enemy(w, "rifleman", new Vec2(43, 30)); contact.GuardMotionPaused = true; contact.Cooldown = 10000; Run(w, 0.5f);
            var known = ObjectiveOf(w).Threats[contact.Id]; var knownPosition = known.Position; float knownHp = known.Hp;
            At(contact, new Vec2(75, 75)); contact.Hp = 5000; Run(w, 0.5f); ObjectiveConfig(w, "pause", true);
            var remembered = ((Dictionary<string, object>)Json.Parse(ObjectiveOf(w).Packet)).Obj("threats").Objs("remembered").First(t => t.In("id") == contact.Id);
            var rememberedPos = remembered.Arr("pos");
            Check(remembered.Str("src") == "memory" && new Vec2(SnapIO.ToF(rememberedPos[0]), SnapIO.ToF(rememberedPos[1])).Equals(knownPosition) && MathF.Abs(SnapIO.ToF(remembered["hp"]) - knownHp) < 0.01f,
                "a decision labels remembered contacts and keeps their observed position and HP rather than reading new fog facts");
        }

        static void ObjectivePersistence()
        {
            var (game, units) = ObjectiveScene("rifleman", "rifleman"); var w = game.World;
            ObjectiveConfig(w, "verb", "patrol", "waypoints", new List<object> { ObjectivePoint(40, 30), ObjectivePoint(25, 30) }); Run(w, 8);
            string saved = Snap(game); var copy = Resume(saved);
            Check(Snap(copy) == saved, "objective config, member slots, pending base intent, phase and decision baselines round-trip exactly");
            int tick = w.Tick; Run(w, 20); Run(copy.World, 20);
            Check(SnapForCompare(game, tick) == SnapForCompare(copy, tick), "objective continuation after restart remains deterministic");
            ObjectiveConfig(w, "pause", true); var paused = Resume(Snap(game));
            Check(ObjectiveOf(paused.World).Paused && ObjectiveOf(paused.World).Packet == ObjectiveOf(w).Packet,
                "a latched pause preserves its complete saved decision packet across restart");
            Check(((List<object>)StateData.Team(w, 0)["objectives"]).Count == 1 && ((List<object>)w.Observe.Log(0).Snapshot()["objectives"]).Count == 1,
                "state and observation snapshots expose the owning objectives");
            ObjectiveConfig(w, "cancel", true);
            Check(w.Teams[0].Objectives.Count == 0 && units.All(e => e.ObjectiveId == null), "cancel releases objective authority without an automatic replacement plan");
        }

        static void ObjectiveNativeHandoff()
        {
            var (game, units) = ObjectiveScene("light_tank"); var w = game.World; var unit = units[0];
            unit.RetreatBelow = 0.4f; unit.Hp = unit.Def.MaxHp * 0.3f; Run(w, 35);
            Check(unit.Retreating && unit.RetreatSpent, "native handoff fixture finishes a once-per-wound retreat");
            ObjectiveConfig(w, "verb", "go", "at", ObjectivePoint(40, 30)); Run(w, 0.5f);
            Check(!unit.Retreating && unit.RetreatSpent && unit.LastOrderBy == "objective", "explicit objective assignment hands back a completed native retreat while preserving the spent wound");
            unit.Retreating = true; ObjectiveConfig(w, "pause", true); Run(w, 0.5f);
            Check(unit.Retreating, "ordinary paused objective ticks do not cancel a native retreat");
            ObjectiveConfig(w, "resume", true); Run(w, 0.5f);
            Check(!unit.Retreating && unit.RetreatSpent, "explicit resume can hand the unit back again without rearming native retreat");
        }

        static void GateObjectives()
        {
            foreach (string verb in new[] { "go", "hold", "patrol" })
            {
                var (game, units) = ObjectiveScene("rifleman", "rifleman", "light_tank"); var w = game.World;
                if (verb == "patrol") ObjectiveConfig(w, "verb", verb, "waypoints", new List<object> { ObjectivePoint(45, 30), ObjectivePoint(25, 30) });
                else ObjectiveConfig(w, "verb", verb, "at", ObjectivePoint(45, 30));
                Run(w, 60);
                Check(w.SettleCounts.Count == 0 && w.Errors == 0 && units.All(e => !e.Dead), $"objective {verb} gate: 60 seconds, zero settles and no sim errors ({Settles(w)})");
            }
            var escort = ObjectiveScene("rifleman", "light_tank"); var ew = escort.game.World;
            var truck = At(Mine0(ew, "mining_truck"), new Vec2(35, 30)); ew.SetOrder(truck, Order.Move, new Vec2(55, 30));
            ObjectiveConfig(ew, "verb", "escort", "target_id", truck.Id); Run(ew, 60);
            Check(ew.SettleCounts.Count == 0 && ew.Errors == 0 && escort.units.All(e => !e.Dead && Vec2.Dist(e.Pos, truck.Pos) <= 6),
                $"objective escort gate: follows the explicit target for 60 seconds with zero settles ({Settles(ew)})");
            var assemble = ObjectiveScene("rifleman", "rifleman", "light_tank"); var aw = assemble.game.World;
            ObjectiveConfig(aw, "verb", "go", "at", ObjectivePoint(55, 30), "assemble", Cmd("at", ObjectivePoint(25, 30), "min", 3)); Run(aw, 60);
            Check(aw.SettleCounts.Count == 0 && aw.Errors == 0 && ObjectiveOf(aw).Assembled && ObjectiveOf(aw).Done,
                $"objective assembly gate: one release, zero settles and no assembly/advance oscillation over 60 seconds ({Settles(aw)})");
            var hold = ObjectiveScene("rifleman"); var hw = hold.game.World; At(hold.units[0], new Vec2(40, 30)); ObjectiveConfig(hw, "radius", 10);
            var foe = Enemy(hw, "rifleman", new Vec2(45.8f, 30)); foe.Hp = 5000; foe.GuardMotionPaused = true; foe.Cooldown = 10000; Run(hw, 60);
            Check(hw.SettleCounts.Count == 0 && hw.EventCounts["objective_order"] <= 2 && foe.Hp < 5000 && hold.units[0].Pos.X > 41,
                $"objective combat hold gate: in-area chase engages without repeated slot returns or settles for 60 seconds ({Settles(hw)})");
        }
    }
}
