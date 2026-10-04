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
        /// A snapshot without its events, plus the last 300 events: the saved events are a window (old ones are dropped),
        /// so after a resume the two games' windows differ in how far back they reach, not in what happened.
        /// </summary>
        static string SnapForCompare(Game g)
        {
            var s = Snap(g);
            int cut = s.LastIndexOf(",\"events\":[", StringComparison.Ordinal);
            return s.Substring(0, cut) + string.Join("|", g.World.Events.Skip(Math.Max(0, g.World.Events.Count - 300)).Select(e => $"{e.Seq} {e.Tick} {e.Type} {e.Team} {e.A} {e.B} {e.Text}"));
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
            [typeof(Entity)] = "Id Team Def Pos PrevPos Facing TurretFacing Hp Dead Origin BuildProgress Rally Order OrderPos GuardPos TargetId Path PathIdx Cooldown " +
                "SpeedCap Group ProgressPos ProgressAt GhostUntil ProgressDist Waypoints WaypointLoop RetreatBelow Retreating RepathTimer Moving LastAttackerId LastHitTime " +
                "LastCallForHelp LastAttackerTeam Responding HomePos Cargo CargoType HarvestType HarvestTile WorkTimer Burning Dock DockAt DepositId Working Offline CarrierId Passengers Fuel Landed Stranded " +
                "FuelCap AtDepot FuelWarned NoAutoRefuelUntil ResumeOrder ResumePos ResumeGuard ResumeTarget ResumeWaypoints ResumeSpeedCap MineQueue LastFiredAt " +
                "Prospecting ProspectCenter ProspectRadius SkipSites SurveyFailure SurveyFailedAt ZoneId",
            [typeof(Team)] = "Id Name Controller PlayerName StandingOrders OrdersVersion Stock Rates PowerProduced PowerUsed Detected Revealed Defeated StartPos " +
                "Visible Explored Left Resigned StalledSince House Seat ProtectedUntil StructureQueue UnitQueues KnownEnemyStructures Surveyed SurveySites Zones " +
                "SurfaceWarnedAt HomeOreWarned LastCommandAt Reserve Stats",
            [typeof(World)] = "Map Paths MapVersion Open MaxPlayers MaxMapSize GrowStep SafeJoinDistance ProtectionSeconds Teams ById Entities Projectiles Events " +
                "EventCounts Alerts Tick GameOver Winner Errors LastError nextId nextSeq seatCounter rng rateSnapshot airWarned StallGrace ArenaChampion contested Inventions GameId Regrown SuddenDeathAt DerrickRespawns " +
                "| ErrorLog loggedErrors cells cellsW cellsH nearTarget nearHelp nearMine nearSep nearVis Showcase OreVersion oreDirty beingMined regrowNow scoreCache scoreCacheTick NextMatchIn derricksChecked groupSpan", // Showcase: the kitchen sink room is never saved (rebuilt from code)
            [typeof(Invention)] = "Key Name Chassis WeaponFrom Summary Team Def ResearchCost ResearchTime Progress Novelty PriceFactor ProposedAt ResearchedAt Built Lost Kills",
            [typeof(Map)] = "W H Tiles Ore OreType Occupant Spawns Deep OreScale nextDepositId OreBase OreBaseType | writable fieldsChanged fieldTiles fieldRoots BaseMissing",
            [typeof(SimpleAI)] = "team Passive nextThink waveSize outpostTargets nextProspect prospectRadius knownSurface",
            [typeof(Game)] = "World Config Speed Paused ais accumulator nextHouseCheck ResumedFrom LastSaved restartIn | RenderFps Restarted",
            [typeof(AlertLog)] = "All open nextSeq",
            [typeof(Alert)] = "Seq Team Kind Priority Pos StartTick LastTick Count Victims Attackers Lost Amount",
            [typeof(GameEvent)] = "Seq Tick Type Team A B Pos Pos2 Key Text",
            [typeof(Projectile)] = "Id Team SourceId TargetId Pos PrevPos TargetPos Weapon",
            [typeof(DeepDeposit)] = "Id Pos Type Amount Initial MineId",
            [typeof(ProdItem)] = "Key Progress StructureId",
            [typeof(ZoneFlag)] = "ZoneId FlaggedBy FlaggedAt",
            [typeof(TeamStats)] = "UnitsBuilt StructuresBuilt UnitsLost StructuresLost Kills OreMined Built KillValue KillsValued SalvageLeft DeepMined DerricksCaptured DerrickSteel SalvageMined UpkeepPaid",
            [typeof(GameConfig)] = "Seed MapSize Speed Controllers Orders Open MaxPlayers MaxMapSize OreScale HouseAIs HouseResignAbove MatchHours | KitchenSink",
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
            var enemy = w.Owned(0).First(e => !e.IsStructure);
            w.Alerts.Raise(w, 2, "base_under_attack", Priority.Critical, hq.Center, hq, enemy);
            w.Emit("chat", 2, text: "Holding \"the ridge\" — ünïcode ok");
            Step(game, 3);
            Check(Ok(r1) && Ok(r2) && Ok(r3) && Ok(r4) && Ok(r5), $"set up a mid-game scene ({r1["result"] ?? r1["error"]}; {r2["result"]}; {r3["result"]}; {r4["result"]}; {r5["result"] ?? r5["error"]})");
            Check(Ok(r6) && Ok(r7) && Ok(r8) && w.Inventions.Count == 2 && !w.Inventions["t2:rampart"].Done && w.Inventions["t2:rampart"].Progress > 0,
                  $"two inventions in play ({r6["result"] ?? r6["error"]}; {r7["result"] ?? r7["error"]}; {r8["result"] ?? r8["error"]})");

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
            Check(tb.Explored.Count(x => x) == t2.Explored.Count(x => x) && tb.Explored.Length == t2.Explored.Length && tb.Visible.SequenceEqual(t2.Visible), "explored and visible tiles survive");
            var tank2 = b.Get(tank.Id);
            Check(tank2 != null && tank2.Waypoints.Count == tank.Waypoints.Count && tank2.WaypointLoop && tank2.RetreatBelow == tank.RetreatBelow && tank2.Order == tank.Order &&
                  (tank.Path == null) == (tank2.Path == null), $"a patrolling tank keeps its order, waypoints, loop and retreat setting ({tank2?.OrderName}, {tank2?.Waypoints.Count} waypoints)");
            var gs2 = b.Get(gunship.Id);
            Check(gs2 != null && gs2.Fuel == gunship.Fuel && gs2.Order == gunship.Order && gs2.ResumeOrder == gunship.ResumeOrder, $"an aircraft keeps its fuel and refuel trip ({gs2?.Fuel:0.0}s, {gs2?.OrderName})");
            Check(b.Get(stranded.Id)?.Stranded == true && b.Get(apc.Id)?.Passengers.Count == 2 && b.Get(apc.Passengers[0])?.CarrierId == apc.Id, "stranded vehicles and transport passengers survive");
            Check(b.Get(truck.Id)?.Cargo == truck.Cargo && b.Get(layer.Id)?.MineQueue.Count == layer.MineQueue.Count && layer.MineQueue.Count > 0, $"truck cargo and a mine layer's queue survive ({layer.MineQueue.Count} mines to lay)");
            var site = tb.StructureQueue.FirstOrDefault();
            Check(site != null && b.Get(site.StructureId) != null && !b.Get(site.StructureId).IsComplete && b.Get(site.StructureId).BuildProgress == w.Get(site.StructureId).BuildProgress,
                  $"a structure under construction keeps its progress ({site?.Key} {b.Get(site?.StructureId ?? 0)?.BuildProgress:P0})");
            Check(tb.UnitQueues[Producer.Barracks].Count == 3 && tb.UnitQueues[Producer.Barracks][0].Progress == t2.UnitQueues[Producer.Barracks][0].Progress, "unit production queues survive");
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
            Check(b.Events.Any(e => e.Type == "chat" && e.Text.Contains("ünïcode")) && b.Events[b.Events.Count - 1].Seq == w.Events[w.Events.Count - 1].Seq, "recent events (with their sequence numbers) survive");
            Check(back.Config.Controllers.SequenceEqual(cfg.Controllers) && back.Config.Seed == cfg.Seed && back.ResumedFrom != null, "the game config survives and the game knows it was resumed");

            // Determinism: the original and the restored game, run on, stay in lockstep.
            Step(game, 120);
            Step(back, 120);
            var ja = SnapForCompare(game); var jb = SnapForCompare(back);
            Check(ja == jb, $"two more minutes later the original and the resumed game are identical: {FirstDiff(ja, jb)}");
            if (ja != jb)
                Check(Math.Abs(w.Entities.Count - b.Entities.Count) <= w.Entities.Count / 10, $"(or at least close: {w.Entities.Count} vs {b.Entities.Count} entities)");
            Check(w.Errors == 0 && b.Errors == 0, $"no sim errors ({w.LastError ?? b.LastError})");

            BusyGame();
            OlderSnapshotsLoad(json);
            TokensSurviveRestart();
        }

        /// <summary>Four scripted AIs fighting on a big map for 20 minutes: how big is the save, and does it resume exactly?</summary>
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
            var ja = SnapForCompare(game); var jb = SnapForCompare(back);
            Check(ja == jb, $"and the resumed copy plays on identically: {FirstDiff(ja, jb)}");
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
                foreach (var k in new[] { "visible", "explored", "power_produced", "power_used", "rates", "stats", "unit_queues", "revealed" }) t.Remove(k);
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
            int nextIdBefore = w.Entities.Max(e => e.Id);
            Step(g, 30);
            Check(w.Errors == 0 && w.Entities.Where(e => e.Id > nextIdBefore).All(e => !orig.ById.ContainsKey(e.Id) || orig.ById[e.Id].Def == e.Def) && !w.GameOver,
                  $"and it plays on without errors, never reusing an id ({w.LastError})");
            root["min_reader"] = (double)(Snapshot.Schema + 1);
            try { Snapshot.Parse(Json.Write(root)); err = null; } catch (FormatException ex) { err = ex.Message; }
            Check(err != null && err.Contains("newer build"), $"a snapshot that needs a newer reader is refused rather than misread ({err})");
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
