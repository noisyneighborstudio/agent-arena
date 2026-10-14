using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        public static int RunSquads()
        {
            NamedSquads(); SnapshotCoversEveryField();
            Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} squad tests FAILED.");
            return failures == 0 ? 0 : 1;
        }

        static void NamedSquads()
        {
            Console.WriteLine("\n-- named squads (addressing only)");
            SquadMembership(); SquadSelectors(); SquadRally(); SquadPersistence(); SquadKnowledge(); SquadCapacity(); ObjectiveRetentionCapacity();
        }

        static (Game game, Entity a, Entity b, Entity truck) SquadScene()
        {
            var game = new Game(new GameConfig { Seed = 7, MapSize = 80, Controllers = new[] { "llm", "llm" } });
            var w = game.World;
            foreach (var t in w.Teams) t.ProtectedUntil = 0;
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var a = At(w.SpawnUnit(0, "light_tank", hq), new Vec2(20, 30));
            var b = At(w.SpawnUnit(0, "artillery", hq), new Vec2(22, 30));
            var truck = At(w.SpawnUnit(0, "mining_truck", hq), new Vec2(24, 30));
            return (game, a, b, truck);
        }

        static void SquadMembership()
        {
            var (game, a, b, truck) = SquadScene(); var w = game.World; var team = w.Teams[0];
            Check(team.Squads.Count == 0 && w.Owned(0).All(e => e.RallySquad == null), "a fresh game has no squads or rally enrollment");
            string orders = Json.Write(w.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList());
            float steel = team.Amount("steel"); long seq = w.CommandSeq;
            var formed = Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "add", new[] { a.Id, b.Id, truck.Id, a.Id }));
            var west = team.Squads["west"];
            Check(Ok(formed) && west.Members.SetEquals(new[] { a.Id, b.Id, truck.Id }), "squads include armed and unarmed mobile units, deduplicating ids");
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "spare"));
            float existingBaseline = west.FormationHp;
            Pez.Sim.Squads.EnrollSpawn(w, a, "spare"); Pez.Sim.Squads.EnrollSpawn(w, a, "west");
            Check(!team.Squads["spare"].Members.Contains(a.Id) && west.FormationHp == existingBaseline, "spawn enrollment cannot transfer an existing member or count its HP twice");
            Check(orders == Json.Write(w.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList()) && steel == team.Amount("steel") && seq == w.CommandSeq,
                  "forming a squad changes no positions, orders, production, stockpile or replay sequence");
            Check(west.FormationHp == a.Hp + b.Hp + truck.Hp, "the formation baseline is the members' current HP when explicitly formed");
            var enemy = w.Owned(1).First(e => !e.IsStructure);
            var invalid = Commands.Execute(w, 0, Cmd("type", "squad", "name", "east", "add", new[] { a.Id, enemy.Id }));
            Check(!Ok(invalid) && !team.Squads.ContainsKey("east") && west.Members.Contains(a.Id), "foreign-member validation is atomic, with no partial transfer");
            var hq = w.Owned(0).First(e => e.IsStructure);
            Check(!Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "bad", "add", new[] { hq.Id }))) &&
                  !Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "bad", "add", new[] { int.MaxValue }))), "structures and nonexistent ids cannot enter a squad");
            Check(!Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "bad", "add", new[] { a.Id }, "remove", new[] { a.Id }))) && west.Members.Contains(a.Id),
                  "conflicting add/remove is refused atomically");
            a.Hp *= 0.5f; b.Hp *= 0.75f;
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "east", "add", new[] { a.Id }))) && !west.Members.Contains(a.Id) &&
                  team.Squads["east"].Members.Contains(a.Id) && west.FormationHp == b.Hp + truck.Hp && team.Squads["east"].FormationHp == a.Hp,
                  "transferring a unit keeps single membership and reforms both affected squads at their current HP");
            Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { a.Id }, "x", 35, "y", 30));
            Check(team.Squads["east"].Members.Contains(a.Id), "a direct order keeps P4 squad membership; objectives do not exist yet");
            var order = a.Order; var pos = a.OrderPos;
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "east", "remove", new[] { a.Id }))) && team.Squads["east"].Members.Count == 0 &&
                  team.Squads["east"].FormationHp == 0 && a.Order == order && a.OrderPos.Equals(pos), "explicit removal resets formation HP and leaves the order unchanged");
            string maxName = new string('s', 24);
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", maxName))) &&
                  !Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", new string('s', 25)))) &&
                  !Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", ""))) &&
                  !Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "west\n"))), "squad names accept 1-24 characters and reject empty, overlong or control-bearing names");
            Check(Ok(Commands.Execute(w, 1, Cmd("type", "squad", "name", "west", "add", new[] { enemy.Id }))), "squad names are local to each team");
            string unchanged = Json.Write(w.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList());
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "east", "disband", true))) && !team.Squads.ContainsKey("east") &&
                  unchanged == Json.Write(w.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList()), "disbanding removes only addressing state");
        }

        static void SquadSelectors()
        {
            var (game, a, b, truck) = SquadScene(); var w = game.World;
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "add", new[] { a.Id, b.Id, truck.Id }));
            var r = Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 35, "y", 30));
            Check(Ok(r) && new[] { a, b, truck }.All(e => e.Order == Order.Move), "squad: selects every live mobile member, including mining trucks");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "set_retreat", "units", "squad:west", "below_pct", 40))) &&
                  new[] { a, b, truck }.All(e => e.RetreatBelow == 0.4f), "units:squad: uses the same resolver for non-movement unit commands");
            string before = Json.Write(w.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList());
            Check(!Ok(Commands.Execute(w, 0, Cmd("type", "stop", "squad", "missing"))) &&
                  !Ok(Commands.Execute(w, 0, Cmd("type", "stop", "units", "squad:missing"))) &&
                  !Ok(Commands.Execute(w, 0, Cmd("type", "stop", "units", "squad:"))) &&
                  !Ok(Commands.Execute(w, 0, Cmd("type", "stop", "squad", "west", "units", "all"))) &&
                  before == Json.Write(w.Entities.Select(e => (object)Snapshot.WriteEntity(e)).ToList()), "missing, malformed or conflicting squad selectors never fall through to all units");
            float formation = w.Teams[0].Squads["west"].FormationHp;
            w.Remove(b);
            var view = w.Teams[0].Squads["west"].View(w, 0);
            Check(!w.Teams[0].Squads["west"].Members.Contains(b.Id) && w.Teams[0].Squads["west"].FormationHp == formation &&
                  (int)view["strength_pct"] < 100, "deaths remove membership but retain casualty HP in the strength denominator");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "stop", "units", "squad:west"))) && a.Order == Order.Idle && truck.Order == Order.Idle,
                  "a squad selector addresses only surviving members");
            Check(!Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "add", new[] { b.Id }))), "dead units cannot be added back");
        }

        static void SquadCapacity()
        {
            var (game, a, b, truck) = SquadScene(); var w = game.World; var team = w.Teams[0];
            bool accepted = true;
            for (int i = 0; i < 32; i++)
                accepted &= Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "slot-" + i)));
            Check(accepted && team.Squads.Count == 32 && team.Squads.Values.All(s => s.Members.Count == 0),
                "the squad capacity includes 32 explicitly retained empty squads");
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "slot-0", "add", new[] { a.Id }));
            var refused = Commands.Execute(w, 0, Cmd("type", "squad", "name", "overflow", "add", new[] { a.Id }));
            Check(!Ok(refused) && team.Squads.Count == 32 && !team.Squads.ContainsKey("overflow") && team.Squads["slot-0"].Members.Contains(a.Id),
                "a new squad at capacity is refused before transferring any unit");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "slot-1", "add", new[] { a.Id }))) &&
                !team.Squads["slot-0"].Members.Contains(a.Id) && team.Squads["slot-1"].Members.Contains(a.Id),
                "existing squads remain editable at capacity");
            Check(Ok(Commands.Execute(w, 1, Cmd("type", "squad", "name", "overflow"))), "squad capacity is local to each team");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "slot-2", "disband", true))) &&
                Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", "replacement"))) && team.Squads.Count == 32,
                "explicit disband frees a squad slot without evicting retained squads");
            var restored = Resume(Snap(game));
            Check(restored.World.Teams[0].Squads.Count == 32 &&
                !Ok(Commands.Execute(restored.World, 0, Cmd("type", "squad", "name", "after-restore"))),
                "snapshot restore preserves the occupied squad capacity including empty squads");
        }

        static void ObjectiveRetentionCapacity()
        {
            var (game, a, b, truck) = SquadScene(); var w = game.World; var team = w.Teams[0];
            bool accepted = true;
            for (int i = 0; i < 64; i++)
            {
                string squad = "archive-" + i;
                accepted &= Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", squad)));
                accepted &= Ok(Commands.Execute(w, 0, Cmd("type", "objective", "id", "plan-" + i, "squad", squad,
                    "verb", "hold", "at", ObjectivePoint(30, 30), "radius", 4)));
                accepted &= Ok(Commands.Execute(w, 0, Cmd("type", "squad", "name", squad, "disband", true)));
            }
            Check(accepted && team.Squads.Count == 0 && team.Objectives.Count == 64 && team.Objectives.Values.All(o => o.Paused),
                "retained disband-paused objectives count toward a separate 64-record capacity");
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "new-squad"));
            var refused = Commands.Execute(w, 0, Cmd("type", "objective", "id", "overflow", "squad", "new-squad",
                "verb", "hold", "at", ObjectivePoint(30, 30), "radius", 4));
            Check(!Ok(refused) && team.Objectives.Count == 64 && !team.Objectives.ContainsKey("overflow"),
                "creating and disbanding squads cannot grow retained objectives past their independent capacity");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "objective", "id", "plan-0", "squad", "new-squad",
                "verb", "hold", "at", ObjectivePoint(30, 30), "radius", 4))) && team.Objectives.Count == 64,
                "an existing objective id can be replaced at capacity");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "objective", "id", "same-squad-replacement", "squad", "new-squad",
                "verb", "hold", "at", ObjectivePoint(30, 30), "radius", 4))) && team.Objectives.Count == 64 && !team.Objectives.ContainsKey("plan-0"),
                "a new objective id can replace the same squad's record at capacity without growing retention");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "objective", "id", "plan-1", "cancel", true))) && team.Objectives.Count == 63,
                "explicit objective cancellation frees retained capacity");
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "last-squad"));
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "objective", "id", "last-plan", "squad", "last-squad",
                "verb", "hold", "at", ObjectivePoint(30, 30), "radius", 4))) && team.Objectives.Count == 64,
                "a freed objective slot can be reused without automatic eviction");
            Check(Resume(Snap(game)).World.Teams[0].Objectives.Count == 64, "retained objective capacity round-trips through snapshots");
        }

        static void SquadRally()
        {
            var (game, a, b, truck) = SquadScene(); var w = game.World;
            var barracks = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
            var another = w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "add", new[] { a.Id, b.Id }));
            float baseline = w.Teams[0].Squads["west"].FormationHp;
            w.Remove(b);
            var r = Commands.Execute(w, 0, Cmd("type", "rally", "structure_id", barracks.Id, "squad", "west", "x", 35, "y", 30));
            Check(Ok(r) && barracks.RallySquad == "west" && another.RallySquad == "west", "rally enrollment preserves the existing same-producer-class behavior");
            var fresh = w.SpawnUnit(0, "rifleman", another);
            Check(w.Teams[0].Squads["west"].Members.Contains(fresh.Id) && fresh.Order == Order.Move && fresh.OrderPos.Equals(new Vec2(35, 30)) &&
                  w.Teams[0].Squads["west"].FormationHp == baseline + fresh.Hp, "new rally units join, retain ordinary rally movement, and extend the casualty-aware baseline");
            var rally = barracks.Rally;
            Check(!Ok(Commands.Execute(w, 0, Cmd("type", "rally", "structure_id", barracks.Id, "squad", "missing", "x", 40, "y", 30))) &&
                  barracks.Rally.Equals(rally) && barracks.RallySquad == "west", "an unknown rally squad leaves the old rally point and enrollment intact");
            Commands.Execute(w, 0, Cmd("type", "rally", "structure_id", barracks.Id, "x", 36, "y", 30));
            var plain = w.SpawnUnit(0, "rifleman", barracks);
            Check(barracks.RallySquad == null && another.RallySquad == null && !w.Teams[0].Squads["west"].Members.Contains(plain.Id) && plain.OrderPos.Equals(new Vec2(36, 30)),
                  "a rally command without squad clears enrollment while keeping normal rally movement");
            Commands.Execute(w, 0, Cmd("type", "rally", "structure_id", barracks.Id, "squad", "west", "x", 35, "y", 30));
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "disband", true));
            Check(barracks.RallySquad == null && another.RallySquad == null && barracks.Rally.HasValue, "disband clears future enrollment without clearing the rally point");

            var enemyBarracks = w.SpawnStructure(1, "barracks", w.FindPlacement(1, "barracks").Value, 1f);
            Commands.Execute(w, 1, Cmd("type", "squad", "name", "enemy"));
            Commands.Execute(w, 1, Cmd("type", "rally", "structure_id", enemyBarracks.Id, "squad", "enemy", "x", 50, "y", 50));
            enemyBarracks.Hp = enemyBarracks.Def.MaxHp * 0.4f;
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var engineer = At(w.SpawnUnit(0, "engineer", hq), enemyBarracks.Center);
            w.SetOrder(engineer, Order.Capture, enemyBarracks.Center, enemyBarracks.Id);
            Run(w, 0.1f);
            Check(enemyBarracks.Team == 0 && enemyBarracks.RallySquad == null && !enemyBarracks.Rally.HasValue, "capturing a producer clears the previous owner's rally and squad enrollment");
        }

        static void SquadPersistence()
        {
            var (game, a, b, truck) = SquadScene(); var w = game.World;
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "add", new[] { a.Id, b.Id }));
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            Commands.Execute(w, 0, Cmd("type", "rally", "structure_id", hq.Id, "squad", "west", "x", 35, "y", 30));
            float baseline = w.Teams[0].Squads["west"].FormationHp; w.Remove(b);
            var json = Snap(game); var back = Resume(json);
            Check(Snap(back) == json && back.World.Teams[0].Squads["west"].Members.SetEquals(new[] { a.Id }) &&
                  back.World.Teams[0].Squads["west"].FormationHp == baseline && back.World.Get(hq.Id).RallySquad == "west", "membership, casualty baseline and rally enrollment round-trip exactly");
            var root = Snapshot.Parse(json);
            foreach (var team in root.Obj("world").Objs("teams")) team.Remove("squads");
            foreach (var entity in root.Obj("world").Objs("entities")) entity.Remove("rally_squad");
            back.Restore(root);
            Check(back.World.Teams.All(t => t.Squads.Count == 0) && back.World.Entities.All(e => e.RallySquad == null), "older snapshots load without enabling squads or enrollment");
        }

        static void SquadKnowledge()
        {
            var (game, a, b, truck) = SquadScene(); var w = game.World;
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "west", "add", new[] { a.Id, b.Id, truck.Id }));
            var enemy = w.Owned(1).First(e => !e.IsStructure);
            Commands.Execute(w, 1, Cmd("type", "squad", "name", "private-enemy", "add", new[] { enemy.Id }));
            var state = StateData.Team(w, 0); var feed = w.Observe.Log(0).Snapshot();
            Check(((List<object>)state["squads"]).Count == 1 && ((List<object>)feed["squads"]).Count == 1 &&
                  !Json.Write(state).Contains("private-enemy") && !Json.Write(feed).Contains("private-enemy"), "state and observation snapshots expose only the requesting team's squads");
            var squad = (JObj)((List<object>)state["squads"])[0];
            Check(squad["objective"] == null && ((List<object>)squad["centre"]).Count == 2 && (int)squad["strength_pct"] == 100,
                  "squad projections provide members, centre, strength and a null objective");
            int asOf = w.Tick; w.Tick++;
            Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { a.Id }, "x", 35, "y", 30)); w.Tick++;
            var result = CommandSafety.ExecuteBatch(game, 0, Cmd("type", "stop", "squad", "west", "as_of_tick", asOf), "commander");
            var row = (JObj)((List<object>)result["results"])[0];
            Check(((List<int>)row["units"]).OrderBy(id => id).SequenceEqual(new[] { a.Id, b.Id, truck.Id }.OrderBy(id => id)) &&
                  Json.Write(row["stale"]).Contains(a.Id.ToString()), "squad commands track affected members and report stale facts for those same own units");
            var own = Snapshot.Parse(Json.Write(ApiServer.ViewSnapshot(game, 0))).Obj("world").Objs("teams").ToList();
            var rival = Snapshot.Parse(Json.Write(ApiServer.ViewSnapshot(game, 1))).Obj("world").Objs("teams").ToList();
            var spec = Snapshot.Parse(Json.Write(ApiServer.ViewSnapshot(game))).Obj("world").Objs("teams").ToList();
            Check(own[0].ContainsKey("squads") && !own[1].ContainsKey("squads") && !rival[0].ContainsKey("squads") && spec.All(t => t.ContainsKey("squads")),
                  "mirrors preserve own squads, hide rival plans, and retain both for the spectator");
            Check(w.Errors == 0, "squad commands and snapshots introduce no sim errors");
        }
    }
}
