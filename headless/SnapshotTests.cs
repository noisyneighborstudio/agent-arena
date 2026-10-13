using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>Saved games: a restart resumes the same world, seats and tokens.</summary>
    public static partial class Tests
    {
        static readonly DateTime SnapTime = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        static string Snap(Game g) => Json.Write(Snapshot.Write(g, SnapTime));

        /// <summary>
        /// A snapshot without its events, plus the events from tick `fromTick` on (the tick the copy was resumed at): the saved
        /// events are a window (old ones are dropped), so after a resume the two games' windows differ in how far back they
        /// reach, not in what happened.
        /// </summary>
        static string SnapForCompare(Game g, int fromTick)
        {
            var s = Snap(g);
            int cut = s.LastIndexOf(",\"events\":[", StringComparison.Ordinal);
            return s.Substring(0, cut) + string.Join("|", g.World.Events.Where(e => e.Tick >= fromTick).Select(e => $"{e.Seq} {e.Tick} {e.Type} {e.Team} {e.A} {e.B} {e.Text}"));
        }

        static Game Resume(string json)
        {
            var g = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
            g.Restore(Snapshot.Parse(json));
            return g;
        }

        static void Step(Game g, float seconds) { for (int i = 0; i < seconds * World.TickRate; i++) g.Advance(World.Dt); }

        /// <summary>Where two snapshots first differ, with some context, for a readable failure.</summary>
        static string FirstDiff(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            if (i == a.Length && i == b.Length) return "identical";
            int from = Math.Max(0, i - 160);
            return $"at char {i}:\n  A …{a.Substring(from, Math.Min(260, a.Length - from))}\n  B …{b.Substring(from, Math.Min(260, b.Length - from))}";
        }

        /// <summary>
        /// Every field of the sim's state classes, sorted into "saved" (Snapshot.cs writes and reads it) or "not state"
        /// (caches, scratch lists, hooks). A field added to the sim and not to either list fails here, so nobody adds
        /// state that a restart would silently lose.
        /// </summary>
        static readonly Dictionary<Type, string> SavedFields = new Dictionary<Type, string>
        {
            [typeof(Entity)] = "Id Team Def Pos PrevPos Facing TurretFacing Hp Dead Origin BuildProgress Rally Order OrderPos GuardPos TargetId UntilCapturable EngageDerricks Path PathIdx Cooldown " +
                "SpeedCap Group ProgressPos ProgressAt GhostUntil ProgressDist Waypoints WaypointLoop RetreatBelow Retreating RetreatSpent GivingWay OwnBlockedSince RepathTimer Moving LastAttackerId LastHitTime " +
                "LastCallForHelp LastAttackerTeam Responding HomePos Cargo CargoType HarvestType HarvestTile WorkTimer Burning Dock DockAt BaySide DepositId Working Output Offline CarrierId Passengers Fuel Landed Stranded " +
                "GrantedShare CargoSalvage FuelCap AtDepot FuelWarned NoAutoRefuelUntil ResumeOrder ResumePos ResumeGuard ResumeTarget ResumeWaypoints ResumeSpeedCap MineQueue LastFiredAt " +
                "Prospecting ProspectCenter ProspectRadius SkipSites SurveyFailure SurveyFailedAt ZoneId | CmdId", // CmdId: the observation feed's command tracking, which restarts (as a new stream) with the process
            [typeof(Team)] = "Id Name Controller PlayerName StandingOrders OrdersVersion Stock Rates PowerProduced PowerUsed PlasmaOwed Detected Revealed Defeated StartPos " +
                "Visible Explored Left Resigned StalledSince House Seat ProtectedUntil StructureQueue UnitQueues KnownEnemyStructures Surveyed SurveySites Zones " +
                "SurfaceWarnedAt HomeOreWarned LastActiveAt Reserve SaveFor SaveForCost Stats Granted",
            [typeof(World)] = "Map Paths MapVersion Open MaxPlayers MaxMapSize GrowStep SafeJoinDistance ProtectionSeconds Teams ById Entities Projectiles Events " +
                "EventCounts Alerts Tick GameOver Winner Errors LastError nextId nextSeq seatCounter rng rateSnapshot airWarned derrickHinted StallGrace ArenaChampion contested Inventions GameId Regrown SuddenDeathAt DerrickRespawns " +
                "| barredBases ErrorLog loggedErrors cells cellsW cellsH nearTarget nearHelp nearMine nearSep nearSpot nearCrush nearVis Showcase OreVersion oreDirty beingMined regrowNow scoreCache scoreCacheTick NextMatchIn derricksChecked groupSpan Mirror MirrorScores Feed Touched", // Feed: the observation feed starts a new stream on resume (readers get a gap and a snapshot); Showcase: the kitchen sink room is never saved (rebuilt from code); Mirror: set by the browser client on its copy
            [typeof(Invention)] = "Key Name Chassis WeaponFrom Summary Team Def ResearchCost ResearchTime Progress Novelty PriceFactor ProposedAt ResearchedAt Built Lost Kills",
            [typeof(Map)] = "W H Tiles Ore OreType Occupant Spawns Deep OreScale nextDepositId OreBase OreBaseType Salvage | writable fieldsChanged fieldTiles fieldRoots BaseMissing SalvageMissing",
            [typeof(SimpleAI)] = "team Passive nextThink waveSize outpostTargets nextProspect prospectRadius knownSurface",
            [typeof(Game)] = "World Config Speed Paused ais accumulator nextHouseCheck ResumedFrom LastSaved restartIn Waiting waitedBefore waitedSeen waitingSince | RenderFps Restarted Now",
            [typeof(AlertLog)] = "All open nextSeq",
            [typeof(Alert)] = "Seq Team Kind Priority Pos StartTick LastTick Count Victims Attackers Lost Amount",
            [typeof(GameEvent)] = "Seq Tick Type Team A B Pos Pos2 Key Text | SeenBy",
            [typeof(Projectile)] = "Id Team SourceId TargetId Pos PrevPos TargetPos Weapon",
            [typeof(DeepDeposit)] = "Id Pos Type Amount Initial MineId",
            [typeof(ProdItem)] = "Key Progress StructureId WorkerId GrantedShare | CmdId",
            [typeof(ZoneFlag)] = "ZoneId FlaggedBy FlaggedAt",
            [typeof(TeamStats)] = "UnitsBuilt StructuresBuilt UnitsLost StructuresLost Kills OreMined Built KillValue KillsValued SalvageLeft DeepMined DerricksCaptured DerrickSteel SalvageMined UpkeepPaid",
            [typeof(GameConfig)] = "Seed MapSize Speed Controllers Orders Open MaxPlayers MaxMapSize OreScale HouseAIs HouseResignAbove MatchHours MinPlayers | KitchenSink",
        };

        static void SnapshotCoversEveryField()
        {
            var missing = new List<string>();
            foreach (var (type, listed) in SavedFields)
            {
                var known = new HashSet<string>(listed.Split(new[] { ' ', '|' }, StringSplitOptions.RemoveEmptyEntries));
                foreach (var f in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    var name = f.Name.StartsWith("<") ? f.Name.Substring(1, f.Name.IndexOf('>') - 1) : f.Name; // auto-property backing fields
                    if (!known.Contains(name)) missing.Add($"{type.Name}.{name}");
                }
            }
            Check(missing.Count == 0, missing.Count == 0 ? "every field of the sim's state is either saved in a snapshot or marked as not state"
                : $"new sim state not in the saved game: {string.Join(", ", missing)}. Save and load it in Sim/Snapshot.cs (missing = its default, for older " +
                  "snapshots), then list it in SavedFields in headless/SnapshotTests.cs (after a | if it's a cache, not state)");
        }

        static void Snapshots()
        {
            SnapshotCoversEveryField();
            // ---- A busy mid-game: four seats (two scripted AIs fighting, two agent seats), several minutes in.
            var cfg = new GameConfig { Seed = 11, MapSize = 112, Controllers = new[] { "ai", "ai", "llm", "llm" } };
            var game = new Game(cfg);
            var w = game.World;
            w.Teams[2].ProtectedUntil = 8 * 60; // keep the agent seat's base standing through the warm-up, whatever the AIs do
            Step(game, 8 * 60);

            // Hand-made situations on the agent seats, covering the state a snapshot has to carry.
            var t2 = w.Teams[2];
            t2.Add("iron_ore", 6000); t2.Add("copper_ore", 2000); t2.Add("steel", 4000); t2.Add("copper", 1000); t2.Add("circuits", 1000); t2.Add("plasma", 300);
            var hq = w.Owned(2).First(e => e.Def.Key == "command_center");
            var airfield = w.SpawnStructure(2, "airfield", w.FindPlacement(2, "airfield").Value, 1f);
            var barracks = w.SpawnStructure(2, "barracks", w.FindPlacement(2, "barracks").Value, 1f);
            var factory = w.SpawnStructure(2, "factory", w.FindPlacement(2, "factory").Value, 1f);
            factory.Rally = hq.Center + new Vec2(3, 5);
            var gunship = w.SpawnUnit(2, "gunship", airfield);
            gunship.Fuel = 30;
            var tank = w.SpawnUnit(2, "light_tank", factory);
            var r1 = Commands.Execute(w, 2, Cmd("type", "move", "units", new[] { tank.Id }, "x", hq.Center.X - 12, "y", hq.Center.Y + 4,
                "waypoints", new List<object> { new List<object> { (double)hq.Center.X - 14, (double)hq.Center.Y + 10 }, new List<object> { (double)hq.Center.X - 6, (double)hq.Center.Y + 12 } }, "loop", true));
            Commands.Execute(w, 2, Cmd("type", "set_retreat", "units", new[] { tank.Id }, "below_pct", 30));
            var sieger = w.SpawnUnit(2, "heavy_tank", factory);
            var foeBase = w.Owned(0).First(e => e.IsStructure);
            w.SetOrder(sieger, Order.Attack, foeBase.Center, foeBase.Id); sieger.UntilCapturable = true;
            var raider = w.SpawnUnit(2, "light_tank", factory);
            var r9 = Commands.Execute(w, 2, Cmd("type", "attack_move", "units", new[] { raider.Id }, "x", w.Map.W / 2, "y", w.Map.H / 2, "derricks", true));
            var stranded = w.SpawnUnit(2, "heavy_tank", factory);
            stranded.Fuel = 0; stranded.Stranded = true;
            var apc = w.SpawnUnit(2, "apc", factory);
            foreach (var rifle in Enumerable.Range(0, 2).Select(_ => w.SpawnUnit(2, "rifleman", barracks))) { rifle.CarrierId = apc.Id; apc.Passengers.Add(rifle.Id); rifle.Pos = apc.Pos; }
            var layer = w.SpawnUnit(2, "minelayer", factory);
            var r2 = Commands.Execute(w, 2, Cmd("type", "lay_mines", "units", new[] { layer.Id }, "x", hq.Center.X - 6, "y", hq.Center.Y + 8, "count", 3));
            var truck = w.SpawnUnit(2, "mining_truck", hq);
            truck.Cargo = 70; truck.CargoType = 1;
            var r3 = Commands.Execute(w, 2, Cmd("type", "build", "structure", "power_plant"));
            var r4 = Commands.Execute(w, 2, Cmd("type", "train", "unit", "rifleman", "count", 3));
            var dep = w.Map.Deep.First(d => d.MineId == 0);
            t2.Surveyed.Add(dep.Id); t2.SurveySites.Add(dep.Pos);
            var mine = w.SpawnStructure(2, "deep_mine", Int2.Of(dep.Pos), 1f);
            mine.DepositId = dep.Id; dep.MineId = mine.Id; dep.Amount -= 123.25f;
            t2.Zones[dep.Id] = new ZoneFlag { ZoneId = dep.Id, FlaggedBy = 12345, FlaggedAt = w.Time - 3.5f };
            var surveyor = w.SpawnUnit(2, "geological_surveyor", factory);
            var r5 = Commands.Execute(w, 2, Cmd("type", "prospect", "units", new[] { surveyor.Id }, "radius", 30));
            // Agent inventions: one researched (with a unit in the field and one queued), one still in research.
            w.SpawnStructure(2, "electronics_plant", w.FindPlacement(2, "electronics_plant").Value, 1f);
            var r6 = Commands.Execute(w, 2, Cmd("type", "propose_tech", "name", "Lancer", "base", "light_tank", "weapon_from", "rocket_soldier", "hp", 360));
            if (w.Inventions.TryGetValue("t2:lancer", out var lancerInv)) lancerInv.Progress = lancerInv.ResearchTime;
            var lancer = w.SpawnUnit(2, "t2:lancer", factory);
            var r7 = Commands.Execute(w, 2, Cmd("type", "train", "unit", "t2:lancer"));
            var r8 = Commands.Execute(w, 2, Cmd("type", "propose_tech", "name", "Rampart", "base", "light_tank", "hp", 480, "speed", 2.1f));
            w.SetOrders(2, "Hold the ridge; expand east.");
            w.Teams[3].ProtectedUntil = w.Time + 200;
            t2.LastActiveAt = w.Time - 123.5f;
            var enemy = w.Owned(0).First(e => !e.IsStructure);
            w.Alerts.Raise(w, 2, "base_under_attack", Priority.Critical, hq.Center, hq, enemy);
            w.Emit("chat", 2, text: "Holding \"the ridge\" — ünïcode ok");
            Step(game, 3);
            Check(Ok(r1) && Ok(r2) && Ok(r3) && Ok(r4) && Ok(r5), $"set up a mid-game scene ({r1["result"] ?? r1["error"]}; {r2["result"]}; {r3["result"]}; {r4["result"]}; {r5["result"] ?? r5["error"]})");
            Check(Ok(r6) && Ok(r7) && Ok(r8) && w.Inventions.Count == 2 && !w.Inventions["t2:rampart"].Done && w.Inventions["t2:rampart"].Progress > 0,
                  $"two inventions in play ({r6["result"] ?? r6["error"]}; {r7["result"] ?? r7["error"]}; {r8["result"] ?? r8["error"]})");

            // Set last, so the scene's three seconds of play haven't moved them: part of the truck's load is salvage not yet delivered,
            // the second queued rifleman was paid a quarter from the arena's gift, and some ore on a tile is salvage.
            truck.CargoSalvage = 30; t2.UnitQueues[Producer.Barracks][1].GrantedShare = 0.25f;
            int salvageTile = Enumerable.Range(0, w.Map.Ore.Length).First(i => w.Map.Ore[i] > 40); w.Map.Salvage[salvageTile] = 40;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var json = Snap(game);
            long writeMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var back = Resume(json);
            long readMs = sw.ElapsedMilliseconds;
            var b = back.World;
            Console.WriteLine($"      busy game ({w.Map.W}x{w.Map.H}, {w.Teams.Count} seats, {w.Entities.Count} entities, {w.Events.Count} events, {w.Time / 60:0.0} min): snapshot {json.Length / 1024} KB, write {writeMs} ms, read {readMs} ms");
            Check(json.Length < 3_000_000, $"a busy game's snapshot is a reasonable size ({json.Length / 1024} KB)");

            // Round trip: the restored game writes out exactly the same snapshot.
            var again = Snap(back);
            Check(again == json, "save -> load -> save gives the identical snapshot: " + FirstDiff(json, again));
            Check(b.Tick == w.Tick && b.Teams.Count == 4 && b.Entities.Count == w.Entities.Count && b.Map.W == w.Map.W, $"tick, seats, entities and map size survive (tick {b.Tick}, {b.Entities.Count} entities)");
            var tb = b.Teams[2];
            Check(tb.Amount("steel") == t2.Amount("steel") && tb.StandingOrders == t2.StandingOrders && tb.OrdersVersion == t2.OrdersVersion && tb.Seat == t2.Seat,
                  "stockpile, standing orders and seat survive");
            Check(t2.Granted.Count > 0 && tb.Granted.Count == t2.Granted.Count && t2.Granted.All(kv => tb.Granted.TryGetValue(kv.Key, out var gv) && gv == kv.Value) && hq.GrantedShare == 1f && b.Get(hq.Id)?.GrantedShare == 1f && tank.GrantedShare == 0f && b.Get(tank.Id)?.GrantedShare == 0f,
                  "what a team was given (its granted stock and its gifted base) survives, and what it built or trained itself isn't flagged");
            Check(tb.UnitQueues[Producer.Barracks][1].GrantedShare == 0.25f && b.Get(truck.Id)?.CargoSalvage == 30 && b.Map.Salvage[salvageTile] == 40,
                  "the share of a queued unit the gift paid, the salvage in a truck's load and the salvage on the ground survive");
            Check(tb.Explored.Count(x => x) == t2.Explored.Count(x => x) && tb.Explored.Length == t2.Explored.Length && tb.Visible.SequenceEqual(t2.Visible), "explored and visible tiles survive");
            var tank2 = b.Get(tank.Id);
            Check(tank2 != null && tank2.Waypoints.Count == tank.Waypoints.Count && tank2.WaypointLoop && tank2.RetreatBelow == tank.RetreatBelow && tank2.Order == tank.Order &&
                  (tank.Path == null) == (tank2.Path == null), $"a patrolling tank keeps its order, waypoints, loop and retreat setting ({tank2?.OrderName}, {tank2?.Waypoints.Count} waypoints)");
            var sg2 = b.Get(sieger.Id);
            Check(sg2 != null && sg2.Order == Order.Attack && sg2.TargetId == foeBase.Id && sg2.UntilCapturable, $"an attack told to stop at capturable still will ({sg2?.OrderName})");
            var rd2 = b.Get(raider.Id);
            Check(Ok(r9) && rd2 != null && rd2.Order == Order.AttackMove && rd2.EngageDerricks, $"an attack_move told to shoot derricks still will ({Said(r9)}; {rd2?.OrderName})");
            var gs2 = b.Get(gunship.Id);
            Check(gs2 != null && gs2.Fuel == gunship.Fuel && gs2.Order == gunship.Order && gs2.ResumeOrder == gunship.ResumeOrder, $"an aircraft keeps its fuel and refuel trip ({gs2?.Fuel:0.0}s, {gs2?.OrderName})");
            Check(b.Get(stranded.Id)?.Stranded == true && b.Get(apc.Id)?.Passengers.Count == 2 && b.Get(apc.Passengers[0])?.CarrierId == apc.Id, "stranded vehicles and transport passengers survive");
            Check(b.Get(truck.Id)?.Cargo == truck.Cargo && b.Get(layer.Id)?.MineQueue.Count == layer.MineQueue.Count && layer.MineQueue.Count > 0, $"truck cargo and a mine layer's queue survive ({layer.MineQueue.Count} mines to lay)");
            var site = tb.StructureQueue.FirstOrDefault();
            Check(site != null && b.Get(site.StructureId) != null && !b.Get(site.StructureId).IsComplete && b.Get(site.StructureId).BuildProgress == w.Get(site.StructureId).BuildProgress,
                  $"a structure under construction keeps its progress ({site?.Key} {b.Get(site?.StructureId ?? 0)?.BuildProgress:P0})");
            Check(tb.UnitQueues[Producer.Barracks].Count == 3 && tb.UnitQueues[Producer.Barracks][0].Progress == t2.UnitQueues[Producer.Barracks][0].Progress &&
                  tb.UnitQueues[Producer.Barracks][0].WorkerId == t2.UnitQueues[Producer.Barracks][0].WorkerId && t2.UnitQueues[Producer.Barracks][0].WorkerId != 0, "unit production queues survive, with the building working each unit");
            var dep2 = b.Map.DepositById(dep.Id);
            Check(dep2 != null && dep2.MineId == mine.Id && dep2.Amount == dep.Amount && b.Get(mine.Id)?.DepositId == dep.Id && tb.Surveyed.Contains(dep.Id), "deep mines, their deposits and survey results survive");
            var sv2 = b.Get(surveyor.Id);
            Check(tb.Zones.TryGetValue(dep.Id, out var zf) && zf.FlaggedBy == 12345 && zf.FlaggedAt == t2.Zones[dep.Id].FlaggedAt && sv2 != null && sv2.Prospecting == surveyor.Prospecting &&
                  sv2.ProspectRadius == surveyor.ProspectRadius && surveyor.Prospecting, $"mining zone flags and a prospecting surveyor survive ({sv2?.OrderName})");
            var lancerDef = b.Def("t2:lancer");
            Check(lancerDef != null && lancerDef.OwnerTeam == 2 && lancerDef.Chassis == "light_tank" && lancerDef.MaxHp == 360 && lancerDef.Weapon.HitsAir &&
                  lancerDef.Weapon.Damage == w.Def("t2:lancer").Weapon.Damage && lancerDef.CostText == w.Def("t2:lancer").CostText && lancerDef.BuildTime == w.Def("t2:lancer").BuildTime &&
                  b.Get(lancer.Id)?.Def == lancerDef && tb.UnitQueues[Producer.Factory].Any(q => q.Key == "t2:lancer") && b.MissingPrereq(2, lancerDef) == null,
                  $"a researched invention keeps its stats and price, its unit in the field and its place in the queue ({lancerDef?.CostText}, {lancerDef?.BuildTime}s)");
            var rampart = b.Inventions.TryGetValue("t2:rampart", out var rp) ? rp : null;
            Check(rampart != null && rampart.Progress == w.Inventions["t2:rampart"].Progress && !rampart.Done && b.MissingPrereq(2, rampart.Def).Contains("researched") &&
                  b.MissingPrereq(3, lancerDef).Contains("only they can build it"),
                  $"research in progress resumes where it was ({rampart?.Pct}%), and inventions stay their team's own");
            Check(Defs.Get("t2:lancer") == lancerDef, "key-only lookups (the view, the API) see the resumed game's inventions");
            Check(b.Alerts.Active(b, 2).Any(a => a.Kind == "base_under_attack" && a.Priority == Priority.Critical) && b.Alerts.LastSeq == w.Alerts.LastSeq, "active alerts survive");
            Check(b.IsProtected(3) && b.Teams[3].ProtectedUntil == w.Teams[3].ProtectedUntil, "newcomer protection survives");
            Check(b.Teams.All(t => t.LastActiveAt == w.Teams[t.Id].LastActiveAt) && tb.LastActiveAt < b.Time - 120, $"every seat's idle clock survives ({b.Time - tb.LastActiveAt:0}s idle)");
            Check(b.Events.Any(e => e.Type == "chat" && e.Text.Contains("ünïcode")) && b.Events[b.Events.Count - 1].Seq == w.Events[w.Events.Count - 1].Seq, "recent events (with their sequence numbers) survive");
            Check(back.Config.Controllers.SequenceEqual(cfg.Controllers) && back.Config.Seed == cfg.Seed && back.ResumedFrom != null, "the game config survives and the game knows it was resumed");

            // Determinism: the original and the restored game, run on, stay in lockstep.
            Step(game, 120);
            Step(back, 120);
            int shared = back.World.Tick - 120 * World.TickRate; // the events since the save: both games hold all of those
            var ja = SnapForCompare(game, shared); var jb = SnapForCompare(back, shared);
            Check(ja == jb, $"two more minutes later the original and the resumed game are identical: {FirstDiff(ja, jb)}");
            if (ja != jb)
                Check(Math.Abs(w.Entities.Count - b.Entities.Count) <= w.Entities.Count / 10, $"(or at least close: {w.Entities.Count} vs {b.Entities.Count} entities)");
            Check(w.Errors == 0 && b.Errors == 0, $"no sim errors ({w.LastError ?? b.LastError})");

            BusyGame();
            DerrickHintsSurvive();
            FoggedSnapshots();
            FoggedDerrickHints();
            FoggedFusion();
            FoggedOwnerEvents();
            OlderSnapshotsLoad(json);
            TokensSurviveRestart();
        }

        /// <summary>Four scripted AIs fighting on a big map for 20 minutes: how big is the save, and does it resume exactly?</summary>
        /// <summary>A player's browser copy (ViewSnapshot with a team): what fog hides stays out, and it still runs.</summary>
        /// <summary>Which derricks a team's units have stopped beside is that team's to know: a player's copy of the game carries only its own.</summary>
        static void FoggedDerrickHints()
        {
            var g = new Game(new GameConfig { Seed = 7, MapSize = 96, Controllers = new[] { "llm", "llm" } });
            var w = g.World;
            foreach (var t in w.Teams) t.ProtectedUntil = 0;
            foreach (var u in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(u);
            var ds = w.Derricks.ToList();
            ds[0].Team = 1; ds[1].Team = 0; // each side holds one
            var hq0 = w.Owned(0).First(e => e.Def.Key == "command_center"); var hq1 = w.Owned(1).First(e => e.Def.Key == "command_center");
            var tank0 = At(w.SpawnUnit(0, "light_tank", hq0), ds[0].Center + new Vec2(-12, 0)); // ours stops by theirs
            var tank1 = At(w.SpawnUnit(1, "light_tank", hq1), ds[1].Center + new Vec2(-12, 0)); // theirs stops by ours
            Commands.Execute(w, 0, Cmd("type", "attack_move", "units", new[] { tank0.Id }, "x", ds[0].Center.X - 3, "y", ds[0].Center.Y));
            Commands.Execute(w, 1, Cmd("type", "attack_move", "units", new[] { tank1.Id }, "x", ds[1].Center.X - 3, "y", ds[1].Center.Y));
            Step(g, 15);
            List<Dictionary<string, object>> Hinted(string json) => Snapshot.Parse(json).Obj("world").Arr("derrick_hinted").OfType<List<object>>().Select(l => new Dictionary<string, object> { ["team"] = l[0], ["id"] = l[1] }).ToList();
            var whole = Hinted(Snap(g));
            Check(w.Events.Count(e => e.Type == "derrick_skipped" && e.Team == 0) == 1 && w.Events.Count(e => e.Type == "derrick_skipped" && e.Team == 1) == 1 && whole.Count == 2,
                  $"set up: each side's tank was told about the other's derrick ({whole.Count} hints in the whole save)");
            var mine = Hinted(Json.Write(ApiServer.ViewSnapshot(g, 0)));
            Check(mine.Count == 1 && (double)mine[0]["team"] == 0 && (double)mine[0]["id"] == ds[0].Id, $"a player's copy carries only its own hint, not the rival's ({mine.Count} entries)");
            Check(Hinted(Json.Write(ApiServer.ViewSnapshot(g, -1))).Count == 2, "and the spectator's copy keeps both");
        }

        static void FoggedSnapshots()
        {
            var g = new Game(new GameConfig { Seed = 3, MapSize = 64, Controllers = new[] { "llm", "ai" } });
            Step(g, 3);
            var w = g.World;
            foreach (var t in w.Teams) t.ProtectedUntil = 0;
            var hq0 = w.Owned(0).First(e => e.IsStructure); var hq1 = w.Owned(1).First(e => e.IsStructure);
            var mine = At(w.SpawnUnit(0, "light_tank", hq0), hq0.Center + new Vec2(3, 0));
            var hidden = At(w.SpawnUnit(1, "heavy_tank", hq1), hq1.Center + new Vec2(-3, 0));
            var seen = At(w.SpawnUnit(1, "scout_buggy", hq1), mine.Pos + new Vec2(2, 0));
            Commands.Execute(w, 1, Cmd("type", "move", "units", new[] { seen.Id }, "x", hq1.Center.X, "y", hq1.Center.Y));
            w.SetOrders(1, "Secret: rush their refinery at minute five.");
            w.Teams[1].Stock["steel"] = 4321;
            seen.GrantedShare = mine.GrantedShare = 1f; // flagged as gifts, to see whose flag the copy carries
            Step(g, 0.5f);
            Check(w.IsVisibleTo(0, seen) && !w.IsVisibleTo(0, hidden), "set up: one enemy in sight, one hidden at its base");
            var json = Json.Write(ApiServer.ViewSnapshot(g, 0));
            var copy = Resume(json);
            var cw = copy.World;
            Check(cw.Get(mine.Id) != null && cw.Get(seen.Id) != null && cw.Get(hidden.Id) == null && cw.Get(hq1.Id) == null,
                  $"the copy has its own units and the enemy it sees, not the hidden enemy or an unscouted base ({cw.Entities.Count} entities of {w.Entities.Count})");
            var cs = cw.Get(seen.Id);
            Check(cs.Path == null || cs.Path.Count <= 4, $"a seen enemy's path is cut to its next steps ({seen.Path?.Count} -> {cs.Path?.Count})");
            Check(!json.Contains("Secret: rush") && string.IsNullOrEmpty(cw.Teams[1].StandingOrders) && cw.Teams[1].Amount("steel") == 0 &&
                  !(Snapshot.Parse(json).Obj("world").Arr("teams")[1] as Dictionary<string, object>).ContainsKey("visible"),
                  "a rival's orders, stockpile and vision aren't in the copy (the copy works out what the rival's units it has can see)");
            Check(cw.Teams[0].Amount("steel") == w.Teams[0].Amount("steel") && cw.Teams[0].Visible.SequenceEqual(w.Teams[0].Visible), "the player's own stockpile and vision are");
            // What the arena gave a team is private economy data too: the player's own gifts are in its copy, a rival's are not.
            var jt = Snapshot.Parse(json).Obj("world").Arr("teams");
            var jents = Snapshot.Parse(json).Obj("world").Arr("entities").OfType<Dictionary<string, object>>().ToList();
            Check(w.Teams[1].Granted.Count > 0 && !(jt[1] as Dictionary<string, object>).ContainsKey("granted") && cw.Teams[1].Granted.Count == 0 &&
                  (jt[0] as Dictionary<string, object>).ContainsKey("granted") && cw.Teams[0].Granted.Count == w.Teams[0].Granted.Count && cw.Teams[0].Gift("iron_ore") == w.Teams[0].Gift("iron_ore"),
                  "a rival's granted amounts stay out of the copy, the player's own are in it");
            var seenJson = jents.First(e => (int)(double)e["id"] == seen.Id); var mineJson = jents.First(e => (int)(double)e["id"] == mine.Id);
            Check(!seenJson.ContainsKey("granted_share") && cw.Get(seen.Id).GrantedShare == 0f && mineJson.ContainsKey("granted_share") && cw.Get(mine.Id).GrantedShare == 1f,
                  "and a visible rival's granted flag is stripped while the player's own entity keeps its flag");
            Check(cw.Teams[1].Name == w.Teams[1].Name && cw.Teams[1].Stats.Kills == w.Teams[1].Stats.Kills, "rivals keep their public face: name and kills");
            Check(cw.Teams[1].StartPos.X == ApiServer.HiddenStart && cw.Teams[1].StartPos.Y == ApiServer.HiddenStart && cw.Teams[0].StartPos.X == w.Teams[0].StartPos.X && cw.Teams[0].StartPos.Y == w.Teams[0].StartPos.Y,
                  $"where a rival's base is stays out of the copy (it's for scouting); the player's own start is there ({cw.Teams[1].StartPos})");
            int teams = cw.Teams.Count;
            cw.Mirror = true; // as the browser runs it
            Step(copy, 20);
            Check(cw.Errors == 0 && copy.World.Teams.Count == teams, $"the copy runs on for 20 s with no sim errors and seats no house AI of its own ({cw.LastError})");
            Check(!cw.Teams[1].Defeated && !cw.GameOver && !cw.Events.Any(e => e.Type == "defeated" || e.Type == "game_over"),
                  "a mirror doesn't decide the game: the rival it can't see isn't counted out");
            var ms = Snapshot.Parse(json).Obj("world").Arr("mirror_scores");
            Check(ms.Count == w.Scores().Count && ms.Count >= 2, $"a player's copy carries the server's scoreboard ({ms.Count} teams), which its fogged world can't count");
            var spec = Json.Write(ApiServer.ViewSnapshot(g));
            Check(Resume(spec).World.Get(hidden.Id) != null && !spec.Contains("Secret: rush"), "a spectator's copy still has everything but the orders");
        }

        /// <summary>
        /// Fusion reactors in a player's browser copy: a rival's owed plasma (which says it runs reactors, and how many) and a
        /// rival reactor's output stay out of it; the player's own, and a spectator's copy, keep them.
        /// </summary>
        static void FoggedFusion()
        {
            var g = new Game(new GameConfig { Seed = 3, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            var w = g.World;
            foreach (var t in w.Teams) t.ProtectedUntil = 0;
            // One of the rival's reactors stands in the player's sight, one at the rival's own (unscouted) base, and the player has one.
            var seenR = w.SpawnStructure(1, "fusion_reactor", w.FindPlacement(0, "fusion_reactor").Value, 1f);
            var hiddenR = w.SpawnStructure(1, "fusion_reactor", w.FindPlacement(1, "fusion_reactor").Value, 1f);
            var ownR = w.SpawnStructure(0, "fusion_reactor", w.FindPlacement(0, "fusion_reactor").Value, 1f);
            for (int i = 0; i < 3 * World.TickRate + 7; i++) // half-fed, ending mid-second so some burn is owed
            {
                foreach (var t in w.Teams) t.Add("plasma", 0.5f * World.FusionBurn * World.Dt);
                w.Step();
            }
            Check(w.Teams[1].PlasmaOwed > 0 && w.Teams[0].PlasmaOwed > 0 && seenR.Output > 0f && seenR.Output < 1f && ownR.Output > 0f && w.IsVisibleTo(0, seenR) && !w.IsVisibleTo(0, hiddenR),
                  $"set up: both teams burn plasma on reactors, one rival reactor in sight and one hidden (owed {w.Teams[1].PlasmaOwed:0.0000}/{w.Teams[0].PlasmaOwed:0.0000}, output {seenR.Output:0.00})");

            var world = Snapshot.Parse(Json.Write(ApiServer.ViewSnapshot(g, 0))).Obj("world");
            var teams = world.Arr("teams");
            var ents = world.Arr("entities").OfType<Dictionary<string, object>>().ToList();
            Dictionary<string, object> Ent(Entity e) => ents.FirstOrDefault(x => Convert.ToInt32(x["id"]) == e.Id);
            Check(!((Dictionary<string, object>)teams[1]).ContainsKey("plasma_owed") && ((Dictionary<string, object>)teams[0]).ContainsKey("plasma_owed"),
                  "a rival's owed plasma (it says whether they run reactors, and how many) isn't in the player's copy; the player's own is");
            Check(Ent(seenR) != null && !Ent(seenR).ContainsKey("output") && Ent(hiddenR) == null && Ent(ownR) != null && Ent(ownR).ContainsKey("output"),
                  "a rival's reactor in sight is in the copy without its output, one at an unscouted base isn't, and the player's own keeps its output");
            var spec = Snapshot.Parse(Json.Write(ApiServer.ViewSnapshot(g))).Obj("world");
            var specEnts = spec.Arr("entities").OfType<Dictionary<string, object>>().ToList();
            Check(((Dictionary<string, object>)spec.Arr("teams")[1]).ContainsKey("plasma_owed") && specEnts.Any(x => Convert.ToInt32(x["id"]) == seenR.Id && x.ContainsKey("output")) && specEnts.Any(x => Convert.ToInt32(x["id"]) == hiddenR.Id),
                  "a spectator's copy still has every team's owed plasma and every reactor's output");
            // And the copy still runs.
            var copy = Resume(Json.Write(ApiServer.ViewSnapshot(g, 0)));
            copy.World.Mirror = true;
            Step(copy, 5);
            Check(copy.World.Errors == 0, $"the copy runs on with a rival's reactor in it ({copy.World.LastError})");
        }

        /// <summary>
        /// A rival's economy events (power totals, the starved reactor, upkeep, research) say what only its owner should know:
        /// they stay out of a player's copy even when the spot they name is in the player's sight.
        /// </summary>
        static void FoggedOwnerEvents()
        {
            var g = new Game(new GameConfig { Seed = 3, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            var w = g.World;
            foreach (var t in w.Teams) t.ProtectedUntil = 0;
            // Each team has a reactor with no plasma, six turrets (90 power drawn against the command center's 20) and no steel for their upkeep.
            foreach (int id in new[] { 0, 1 })
            {
                w.SpawnStructure(id, "fusion_reactor", w.FindPlacement(id, "fusion_reactor").Value, 1f);
                for (int i = 0; i < 6; i++) w.SpawnStructure(id, "gun_turret", w.FindPlacement(id, "gun_turret").Value, 1f);
                w.Teams[id].Stock["steel"] = 0;
            }
            w.Emit("research_started", 1, pos: w.Teams[1].StartPos, key: "t1:secret", text: "research started on Secret (t1:secret), 90s");
            for (int i = 0; i < 3 * World.TickRate; i++) w.Step();
            var owner = new[] { "low_power", "defences_offline", "research_started" };
            bool Had(string ty, int tm) => w.Events.Any(e => e.Type == ty && e.Team == tm);
            Check(Had("low_power", 1) && Had("defences_offline", 1) && Had("research_started", 1) && Had("low_power", 0) && Had("defences_offline", 0),
                  "set up: both teams have had a low-power and a defences-offline event, and the rival a research event");
            Array.Fill(w.Teams[0].Visible, true); // the whole map in the player's sight, the rival's base and the spots its events name
            string json = Json.Write(ApiServer.ViewSnapshot(g, 0));
            var evs = Snapshot.Parse(json).Obj("world").Arr("events").OfType<Dictionary<string, object>>().ToList();
            int Team(Dictionary<string, object> e) => e.TryGetValue("team", out var tv) ? Convert.ToInt32(tv) : -1;
            Check(!evs.Any(e => Team(e) == 1 && owner.Contains((string)e["type"])) && !json.Contains("Secret (t1:secret)") && !evs.Any(e => e.TryGetValue("text", out var xt) && xt is string xs && xs.Contains("fusion_reactor")),
                  $"a rival's low_power, defences_offline and research events don't reach the player's copy, though the spots they name are in sight ({evs.Count(e => Team(e) == 1)} rival events kept)");
            Check(evs.Any(e => Team(e) == 0 && (string)e["type"] == "low_power") && evs.Any(e => Team(e) == 0 && (string)e["type"] == "defences_offline"),
                  "the player's own are in it");
            Check(!evs.Any(e => Team(e) == 1 && ((string)e["type"] == "low_power" || e.TryGetValue("text", out var tx) && tx is string txs && txs.Contains("produced,"))),
                  "and no rival power totals");
            var spec = Snapshot.Parse(Json.Write(ApiServer.ViewSnapshot(g))).Obj("world").Arr("events").OfType<Dictionary<string, object>>().ToList();
            Check(owner.All(ty => spec.Any(e => Team(e) == 1 && (string)e["type"] == ty)), "a spectator's copy still has them all");
            // An event of the rival's that is just what happened in sight still gets through.
            var sight = w.Emit("built", 1, 99, pos: w.Teams[1].StartPos, key: "gun_turret");
            var evs2 = Snapshot.Parse(Json.Write(ApiServer.ViewSnapshot(g, 0))).Obj("world").Arr("events").OfType<Dictionary<string, object>>().ToList();
            Check(evs2.Any(e => Convert.ToInt32(e["seq"]) == sight.Seq), "while what happened in the player's sight (a building finished) still reaches it");
        }

        /// <summary>
        /// "Once per derrick every two minutes" is state, not a look back through the event log: a save keeps only the newest 1500
        /// events (a big fight makes 2500 in two minutes), so a resumed game that read the log would say it again.
        /// </summary>
        static void DerrickHintsSurvive()
        {
            var game = new Game(new GameConfig { Seed = 7, MapSize = 96, Controllers = new[] { "llm", "llm" } });
            var w = game.World;
            foreach (var u in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(u);
            var d = w.Derricks.First();
            d.Team = 1;
            var hq0 = w.Owned(0).First(e => e.Def.Key == "command_center");
            var tank = At(w.SpawnUnit(0, "light_tank", hq0), d.Center + new Vec2(-12, 0));
            Commands.Execute(w, 0, Cmd("type", "attack_move", "units", new[] { tank.Id }, "x", d.Center.X - 3, "y", d.Center.Y));
            Step(game, 15);
            int saidFirst = w.Events.Count(e => e.Type == "derrick_skipped");
            // A save from before the hints were kept as state (no derrick_hinted) still has the hint event: it counts, so the first resume doesn't repeat it.
            var old = (Dictionary<string, object>)Json.Parse(Snap(game));
            ((Dictionary<string, object>)old["world"]).Remove("derrick_hinted");
            var older = Resume(Json.Write(old));
            Commands.Execute(older.World, 0, Cmd("type", "attack_move", "units", new[] { tank.Id }, "x", d.Center.X - 3, "y", d.Center.Y + 2));
            Step(older, 10);
            Check(saidFirst == 1 && older.World.Events.Count(e => e.Type == "derrick_skipped") == 1, $"a save from before hints were state, resumed within the two minutes, doesn't repeat the hint its events show ({older.World.Events.Count(e => e.Type == "derrick_skipped")} hint events after resuming)");
            for (int i = 0; i < 2500; i++) w.Emit("shot", 1, 5, 6, pos: new Vec2(40, 40)); // far more than the 1500 events a save holds
            var back = Resume(Snap(game));
            Check(Snap(back) == Snap(game), "a game that has given a derrick hint saves and loads back to the same snapshot");
            var move = Cmd("type", "attack_move", "units", new[] { tank.Id }, "x", d.Center.X - 3, "y", d.Center.Y + 2);
            Commands.Execute(back.World, 0, move); // the tank stops by the derrick again
            Step(back, 10);
            Check(saidFirst == 1 && back.World.Events.Count(e => e.Type == "derrick_skipped") == 0, $"a resumed game doesn't repeat the hint its units already got ({saidFirst} before the save, {back.World.Events.Count(e => e.Type == "derrick_skipped")} after)");
            Step(back, 120);
            Commands.Execute(back.World, 0, move);
            Step(back, 10);
            Check(back.World.Events.Count(e => e.Type == "derrick_skipped") == 1, "and says it again once the two minutes are up");
        }

        static void BusyGame()
        {
            var game = new Game(new GameConfig { Seed = 21, MapSize = 160, Controllers = new[] { "ai", "ai", "ai", "ai" } });
            var w = game.World;
            Step(game, 20 * 60);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var json = Snap(game);
            long ms = sw.ElapsedMilliseconds;
            var back = Resume(json);
            Console.WriteLine($"      busy game ({w.Map.W}x{w.Map.H}, {w.Teams.Count} AIs, {w.Entities.Count(e => !e.Dead)} entities, {w.Projectiles.Count} shells in flight, {w.Time / 60:0} min): snapshot {json.Length / 1024} KB in {ms} ms");
            Check(Snap(back) == json && json.Length < 3_000_000, $"a 20-minute four-AI game saves in {json.Length / 1024} KB and round-trips exactly");
            Step(game, 60); Step(back, 60);
            int shared = back.World.Tick - 60 * World.TickRate; // the events since the save: both games hold all of those
            var ja = SnapForCompare(game, shared); var jb = SnapForCompare(back, shared);
            Check(ja == jb, $"and the resumed copy plays on identically: {FirstDiff(ja, jb)}");
            if (ja != jb && Environment.GetEnvironmentVariable("PEZ_ALERTDBG") != null)
            {
                var la = ja.Substring(ja.IndexOf("\"alerts\":{")).Split("{\"seq\"").ToList(); var lb = jb.Substring(jb.IndexOf("\"alerts\":{")).Split("{\"seq\"").ToList();
                foreach (var x in lb.Except(la)) Console.WriteLine("  only resumed: " + x.Substring(0, Math.Min(300, x.Length)));
                foreach (var x in la.Except(lb)) Console.WriteLine("  only original: " + x.Substring(0, Math.Min(300, x.Length)));
            }
        }

        /// <summary>A newer build loads an older snapshot: fields it doesn't find get defaults, unknown ones are ignored.</summary>
        static void OlderSnapshotsLoad(string json)
        {
            var root = (Dictionary<string, object>)Json.Parse(json);
            root["schema"] = 0.0;
            root["from_the_future"] = new Dictionary<string, object> { ["x"] = 1.0 };
            root.Remove("game"); // no AI state: scripted seats get fresh controllers
            var wd = (Dictionary<string, object>)root["world"];
            foreach (var k in new[] { "rng", "rate_snapshot", "air_warned", "next_id", "next_seq", "seat_counter", "event_counts", "alerts", "projectiles" }) wd.Remove(k);
            foreach (Dictionary<string, object> t in (List<object>)wd["teams"])
                foreach (var k in new[] { "visible", "explored", "power_produced", "power_used", "rates", "stats", "unit_queues", "revealed", "last_active_at" }) t.Remove(k);
            foreach (Dictionary<string, object> e in (List<object>)wd["entities"])
            {
                foreach (var k in new[] { "fuel", "prev_pos", "path", "path_idx", "resume_order", "waypoints", "progress_pos", "landed", "stranded" }) e.Remove(k);
                e["a_field_from_a_newer_build"] = "ignored";
            }
            ((List<object>)wd["entities"]).Add(new Dictionary<string, object> { ["id"] = 999999.0, ["team"] = 0.0, ["def"] = "unit_type_that_was_removed", ["pos"] = new List<object> { 5.0, 5.0 } });
            var g = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
            string err = null;
            try { g.Restore(root); } catch (Exception ex) { err = ex.Message; }
            var w = g.World;
            Check(err == null && w.Teams.Count == 4 && w.Entities.Count > 50, $"an older snapshot (fields missing) still loads ({err ?? $"{w.Entities.Count} entities"})");
            var orig = Resume(json).World;
            Check(w.Teams.All(t => t.PowerProduced == orig.Teams[t.Id].PowerProduced) && w.Teams[2].Explored.Any(x => x), "missing power and vision are rebuilt from the world");
            Check(w.Entities.Where(e => e.Def.UsesFuel).All(e => e.Fuel == e.Def.Fuel) && w.Get(999999) == null, "missing fuel starts full; an entity type the build no longer has is dropped");
            Check(w.Teams.All(t => t.LastActiveAt == w.Time) && w.Time > 0, "a save from before the idle clock counts every seat as active at the load, not idle since the start");
            int nextIdBefore = w.Entities.Max(e => e.Id);
            Step(g, 30);
            Check(w.Errors == 0 && w.Entities.Where(e => e.Id > nextIdBefore).All(e => !orig.ById.ContainsKey(e.Id) || orig.ById[e.Id].Def == e.Def) && !w.GameOver,
                  $"and it plays on without errors, never reusing an id ({w.LastError})");
            root["min_reader"] = (double)(Snapshot.Schema + 1);
            try { Snapshot.Parse(Json.Write(root)); err = null; } catch (FormatException ex) { err = ex.Message; }
            Check(err != null && err.Contains("newer build"), $"a snapshot that needs a newer reader is refused rather than misread ({err})");
            SalvageCountMigrates();
        }

        /// <summary>A save from before salvage counted when delivered: the salvage aboard trucks was already off the score; it comes back.</summary>
        static void SalvageCountMigrates()
        {
            var game = new Game(new GameConfig { Seed = 5, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            var w = game.World;
            var truck0 = w.Owned(0).First(e => e.IsHarvester); var truck1 = w.Owned(1).First(e => e.IsHarvester);
            w.Teams[0].Stats.OreMined = 1000; w.Teams[0].Stats.SalvageMined = 400; truck0.Cargo = 150; truck0.CargoType = 0; // 150 of the 400 are aboard, counted at pickup
            w.Teams[1].Stats.OreMined = 500; w.Teams[1].Stats.SalvageMined = 50; truck1.Cargo = 150; truck1.CargoType = 0;
            var current = Resume(Snap(game)).World;
            Check(current.Teams[0].Stats.SalvageMined == 400 && current.Teams[1].Stats.SalvageMined == 50, "a save with the salvage layer is taken as it is");
            var root = (Dictionary<string, object>)Json.Parse(Snap(game));
            ((Dictionary<string, object>)((Dictionary<string, object>)root["world"])["map"]).Remove("salvage");
            var g = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
            g.Restore(root);
            var b = g.World;
            Check(b.Teams[0].Stats.SalvageMined == 250 && b.Teams[1].Stats.SalvageMined == 0,
                  $"from an older save each truck's load comes back out of the salvage count, not below zero ({b.Teams[0].Stats.SalvageMined} of 400 less 150; {b.Teams[1].Stats.SalvageMined} of 50 less 150)");
            // A truck holding salvage that was counted at pickup, lost after the load: the score no longer pays for it.
            var game2 = new Game(new GameConfig { Seed = 5, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            var w2 = game2.World;
            var truck2 = w2.Owned(0).First(e => e.IsHarvester);
            w2.Teams[0].Stats.OreMined = 1000; w2.Teams[0].Stats.SalvageMined = 150; truck2.Cargo = 150; truck2.CargoType = 0;
            var root2 = (Dictionary<string, object>)Json.Parse(Snap(game2));
            ((Dictionary<string, object>)((Dictionary<string, object>)root2["world"])["map"]).Remove("salvage");
            var g2 = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
            g2.Restore(root2);
            var lost2 = g2.World.Owned(0).First(e => e.IsHarvester && e.Cargo == 150);
            g2.World.Remove(lost2); // lost before it delivered
            Check(g2.World.Teams[0].Stats.SalvageMined == 0 && g2.World.Scores().First(s => s.Team == 0).OreMined == 1000, "so 1000 delivered field ore with 150 salvage aboard a lost truck scores 1000, not 850");
        }

        /// <summary>The host saves, stops and starts again: the same seats answer to the same tokens.</summary>
        static void TokensSurviveRestart()
        {
            var file = Path.Combine(Path.GetTempPath(), $"pezz-test-{Environment.ProcessId}", "rooms", "game-7795.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            if (File.Exists(file)) File.Delete(file);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            (Game game, ApiServer api, RoomSaver saver, Thread loop, Func<bool> stop) Host(int port, bool resume)
            {
                var g = new Game(new GameConfig { Seed = 5, MapSize = 64, Open = true, Controllers = new[] { "ai" } });
                var a = new ApiServer(port);
                var s = new RoomSaver(file) { IntervalSeconds = 30 };
                if (resume) s.TryResume(g, a, false, true, false);
                a.OnRestart = () => s.Forget(g);
                a.OnSave = () => s.Save(g, a, null);
                a.Start();
                bool halt = false;
                var t = new Thread(() =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
                    while (!halt) { double now = clock.Elapsed.TotalSeconds; g.Advance((float)(now - last)); last = now; a.Pump(g); s.Tick(g, a); Thread.Sleep(5); }
                }) { IsBackground = true };
                t.Start();
                return (g, a, s, t, () => halt = true);
            }
            Dictionary<string, object> Call(int port, string path, string token = null, string body = null)
            {
                var req = new HttpRequestMessage(body != null ? HttpMethod.Post : HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
                if (token != null) req.Headers.Add("Authorization", "Bearer " + token);
                if (body != null) req.Content = new StringContent(body);
                var res = http.SendAsync(req).Result;
                var d = (Dictionary<string, object>)Json.Parse(res.Content.ReadAsStringAsync().Result);
                d["_status"] = (double)(int)res.StatusCode;
                return d;
            }

            var h1 = Host(7795, false);
            var joined = Call(7795, "/api/register", body: "{\"name\":\"Claude\"}");
            string token = joined.Str("token"), view = joined.Str("view_token");
            int team = (int)joined.Num("team");
            Thread.Sleep(2500); // a join is saved within a couple of seconds, without waiting for the periodic save
            Check(File.Exists(file), "a new seat is saved soon after the join");
            var saved = Call(7795, "/api/admin/save", body: "{}");
            var mode = File.GetUnixFileMode(file);
            Check(saved["ok"] is bool ok && ok && mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite) &&
                  !Directory.GetFiles(Path.GetDirectoryName(file)).Any(f => f.Contains(".tmp")), $"POST /api/admin/save writes the snapshot atomically, mode 0600 ({mode}, {saved.Num("bytes")} bytes)");
            int tickSaved = (int)saved.Num("tick");
            h1.stop(); h1.loop.Join(); h1.api.Stop();

            var h2 = Host(7794, true);
            Thread.Sleep(300);
            var who = Call(7794, "/api/whoami", token);
            var vwho = Call(7794, $"/api/view/whoami?view={view}");
            Check(who["ok"] is bool w1 && w1 && (int)who.Num("team") == team && vwho["ok"] is bool w2 && w2 && (int)vwho.Num("team") == team,
                  $"after the restart the same token and view link still work for the same seat (team {who.Num("team")})");
            var status = Call(7794, "/api/status");
            Check(status.Str("resumed_from") != null && status.Num("tick") >= tickSaved, $"/api/status says the game was resumed ({status.Str("resumed_from")}, tick {status.Num("tick")} >= {tickSaved})");
            Check(Convert.ToInt32(Call(7794, "/api/whoami", "not-a-real-token")["_status"]) == 401, "unknown tokens are still refused");
            var state = Call(7794, "/api/state?format=json", token);
            Check(state.Obj("you") != null, "the resumed seat can read its state");
            Check(h2.game.World.Events.Any(e => e.Type == "chat" && e.Text.StartsWith("Back online: your game has been resumed")), "the arena chat says the game is back");
            // The browser client's feed: the whole world, loadable by a reader, with no tokens and no orders text.
            Call(7794, $"/api/admin/orders?team={team}", body: "{\"text\":\"Secret plan: flank west at dawn.\"}");
            var vreq = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:7794/api/view/snapshot");
            vreq.Headers.Add("Accept-Encoding", "gzip");
            var vres = http.SendAsync(vreq).Result;
            string vjson;
            using (var gz = new System.IO.Compression.GZipStream(vres.Content.ReadAsStreamAsync().Result, System.IO.Compression.CompressionMode.Decompress))
            using (var rd = new StreamReader(gz)) vjson = rd.ReadToEnd();
            var vsnap = Snapshot.Parse(vjson);
            var vgame = Resume(vjson);
            Check(vres.Content.Headers.ContentEncoding.Contains("gzip") && vres.Headers.CacheControl?.NoStore == true && vgame.World.Teams.Count == h2.game.World.Teams.Count && vgame.World.Entities.Count > 0,
                  $"/api/view/snapshot is gzipped, uncacheable, and loads back into a game ({vgame.World.Entities.Count} entities, {vjson.Length / 1024} KB)");
            Check(!vjson.Contains("\"token\"") && !vjson.Contains("view_token") && !vjson.Contains(token) && !vjson.Contains(view) && !vsnap.ContainsKey("api"), "the spectator snapshot carries no tokens or api section");
            Check(!vjson.Contains("flank west") && vgame.World.Teams.All(t => string.IsNullOrEmpty(t.StandingOrders)) && vsnap.Obj("config").Arr("orders").All(o => string.IsNullOrEmpty(o as string)),
                  "the spectator snapshot carries no standing orders or commander orders");
            var own = Call(7794, $"/api/view/snapshot?view={view}");
            Check(Convert.ToInt32(own["_status"]) == 200 && own.Obj("world") != null && !Json.Write(own).Contains(token),
                  "a view token gets that team's own (fogged) snapshot, still without tokens");
            Call(7794, "/api/admin/restart", body: "{}");
            Thread.Sleep(200);
            Check(!File.Exists(file) && Convert.ToInt32(Call(7794, "/api/whoami", token)["_status"]) == 401, "an admin restart starts a fresh game and drops the saved one (old tokens end)");
            h2.stop(); h2.loop.Join(); h2.api.Stop();

            // A closed game isn't resumed by an open-arena launch unless asked; -resume brings back any saved game.
            var closed = new Game(new GameConfig { Seed = 9, MapSize = 48, Controllers = new[] { "human", "ai" } });
            Step(closed, 5);
            var s3 = new RoomSaver(file);
            s3.Save(closed, null, null);
            var g3 = new Game(new GameConfig { Seed = 1, MapSize = 48, Open = true, Controllers = new[] { "ai" } });
            Check(!s3.TryResume(g3, null, false, true, false) && s3.TryResume(g3, null, true, false, false) && g3.World.Tick == closed.World.Tick,
                  "an open launch doesn't pick up a saved closed game; an explicit resume does");
            Check(!s3.TryResume(g3, null, true, true, true) && !File.Exists(file), "a fresh start discards the saved game");
            File.WriteAllText(file, "{not json");
            Check(!s3.TryResume(g3, null, true, true, false) && !File.Exists(file) && Directory.GetFiles(Path.GetDirectoryName(file)).Any(f => f.Contains(".unreadable-")),
                  "an unreadable snapshot is set aside (kept for diagnosis) and a new game starts");
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(file)), true);
        }
    }
}
