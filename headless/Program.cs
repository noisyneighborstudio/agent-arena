using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>
    /// pez-headless [--port 7777] [--controllers llm,ai] [--seed N] [--map-size 80] [--speed 1] [--selftest]
    ///              [--open [--max-players 8] [--max-map-size 320] [--house-ais 1] [--house-resign-above 5]]
    ///              open arena: outside agents join through the gateway; scripted "house" seats are passive and make room
    ///              [--resume | --fresh] [--snapshot ~/.config/pezz/rooms/game-PORT.json] [--save-interval 30] [--match-hours 4]
    ///              the game is saved every 30 game-seconds, soon after a join, on POST /api/admin/save and on SIGTERM/SIGINT;
    ///              --resume restores the saved game at startup (an --open arena does by default; --fresh starts over)
    /// Runs the game with no graphics. LLMs connect over the HTTP API (usually via the MCP server).
    /// --selftest plays AI vs AI as fast as possible and prints the result.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
            var cfg = new GameConfig
            {
                Seed = int.Parse(Arg("--seed", "1337")),
                MapSize = int.Parse(Arg("--map-size", "80")),
                OreScale = float.Parse(Arg("--ore-scale", "1"), System.Globalization.CultureInfo.InvariantCulture),
                Open = args.Contains("--open"),
                MaxPlayers = int.Parse(Arg("--max-players", "8")),
                MaxMapSize = int.Parse(Arg("--max-map-size", "320")),
                HouseAIs = int.Parse(Arg("--house-ais", "1")),
                HouseResignAbove = int.Parse(Arg("--house-resign-above", "5")),
                Speed = float.Parse(Arg("--speed", "1"), System.Globalization.CultureInfo.InvariantCulture),
                Controllers = Arg("--controllers", "llm,ai").Split(','),
                MatchHours = float.Parse(Arg("--match-hours", "4"), System.Globalization.CultureInfo.InvariantCulture),
            };
            World.Profile = args.Contains("--profile");
            World.RegrowthDisabled = args.Contains("--no-regrowth"); // balance baselines
            if (args.Contains("--selftest")) return SelfTest(cfg, int.Parse(Arg("--max-minutes", "30")));
            if (args.Contains("--test")) return Tests.Run();
            if (args.Contains("--test-kitchen-sink")) return Tests.RunKitchenSink();
            if (args.Contains("--test-economy")) return Tests.RunEconomy();
            if (args.Contains("--resume-check")) return ResumeCheck(Arg("--resume-check", ""), float.Parse(Arg("--seconds", "600"), System.Globalization.CultureInfo.InvariantCulture));
            if (args.Contains("--trace")) return Trace(cfg, int.Parse(Arg("--trace", "1")), float.Parse(Arg("--seconds", "60")));

            var game = new Game(cfg);
            int port = int.Parse(Arg("--port", "7777"));
            var api = new ApiServer(port) { Log = Console.WriteLine };
            api.OnCommand = (team, cmd, res) => Console.WriteLine($"[{game.World.Time,6:0.0}s] team{team} {cmd} -> {res}");
            var saver = new RoomSaver(Arg("--snapshot", RoomSaver.DefaultFile(port)))
            {
                Log = Console.WriteLine,
                IntervalSeconds = float.Parse(Arg("--save-interval", "30"), System.Globalization.CultureInfo.InvariantCulture),
            };
            bool resumed = saver.TryResume(game, api, args.Contains("--resume"), cfg.Open, args.Contains("--fresh"));
            api.OnRestart = () => saver.Forget(game);
            game.Restarted = () => saver.Forget(game); // the next match, after one ends on points (open arenas)
            api.OnSave = () => saver.Save(game, api, "requested");
            api.Start();
            Console.WriteLine(resumed
                ? $"Headless game resumed at {game.World.Time:0}s: controllers [{string.Join(",", game.Config.Controllers)}], speed {game.Speed}"
                : $"Headless game: seed {cfg.Seed}, controllers [{string.Join(",", cfg.Controllers)}], speed {cfg.Speed}");

            // SIGTERM / SIGINT / SIGHUP: finish the current loop, save, exit (the handler only sets a flag; the sim is single-threaded).
            var signals = new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP }
                .Select(sig => PosixSignalRegistration.Create(sig, ctx => { ctx.Cancel = true; stopRequested = true; })).ToList();

            var sw = Stopwatch.StartNew();
            double last = 0;
            long lastSeq = 0;
            bool announcedOver = false;
            while (!stopRequested)
            {
                double now = sw.Elapsed.TotalSeconds;
                game.Advance((float)(now - last));
                last = now;
                api.Pump(game);
                saver.Tick(game, api);
                var w = game.World;
                foreach (var e in w.Events.Where(e => e.Seq > lastSeq && (e.Type == "chat" || e.Type == "defeated" || e.Type == "game_over")))
                    Console.WriteLine($"[{e.Tick * World.Dt,6:0.0}s] {(e.Team >= 0 ? w.Teams[e.Team].Name : "")}: {e.Text}");
                if (w.Events.Count > 0) lastSeq = w.Events[^1].Seq;
                if (w.GameOver && !announcedOver) { announcedOver = true; Console.WriteLine(Json.Write(ApiServer.Status(game))); }
                if (!w.GameOver) announcedOver = false;
                Thread.Sleep(5);
            }
            saver.Save(game, api, "shutdown");
            api.Stop();
            GC.KeepAlive(signals);
            return 0;
        }

        static volatile bool stopRequested;

        /// <summary>
        /// Deploy rehearsal: load a saved game (a copy; the file is only read), run it on for a while with no server, and
        /// report what the new rules make of it. Never binds a port.
        /// </summary>
        static int ResumeCheck(string file, float seconds)
        {
            var game = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
            game.Restore(Snapshot.Parse(System.IO.File.ReadAllText(file)));
            var w = game.World;
            void Report(string when)
            {
                Console.WriteLine($"{when}: t={w.Time / 3600:0.00}h map {w.Map.W}x{w.Map.H}, field tiles {w.Map.OreBase.Count(x => x > 0)} (orig ore {w.Map.OreBase.Sum()}), surface ore {w.Map.Ore.Sum()}, regrown {w.Regrown}, " +
                                  $"derricks {w.Derricks.Count()} (held {w.Derricks.Count(d => d.Team >= 0)}), match {World.PhaseName(w.Phase)} (sudden death in {(w.SuddenDeathAt - w.Time) / 60:0} min), players {w.ActivePlayers}, game_over {w.GameOver}, errors {w.Errors} {w.LastError}");
                foreach (var t in w.Teams.Where(t => !t.Left && !t.Defeated))
                    Console.WriteLine($"   {t.Name} ({t.PlayerName}): structures {w.Owned(t.Id).Count(e => e.IsStructure)}, units {w.Owned(t.Id).Count(e => !e.IsStructure)}, steel {t.Amount("steel")}, kills {t.Stats.Kills}, ore {t.Stats.OreMined}");
            }
            Report("resumed");
            for (int i = 0; i < seconds * World.TickRate && !w.GameOver; i++) game.Advance(World.Dt);
            Report($"after {seconds:0}s");
            foreach (var e in w.Events.Where(e => e.Type == "chat" && e.Team < 0).TakeLast(12)) Console.WriteLine($"   [{e.Tick * World.Dt / 60:0.0}m] {e.Text}");
            return w.Errors == 0 ? 0 : 1;
        }

        /// <summary>Debug aid: run AI vs AI and print one entity's state every second.</summary>
        static int Trace(GameConfig cfg, int id, float seconds)
        {
            cfg.Controllers = cfg.Controllers.Select(_ => "ai").ToArray();
            var game = new Game(cfg);
            var w = game.World;
            while (w.Time < seconds)
            {
                game.Advance(World.Dt);
                if (w.Tick % World.TickRate != 0) continue;
                var e = w.Get(id);
                if (e == null) { Console.WriteLine($"{w.Time:0}s #{id} gone"); break; }
                Console.WriteLine($"{w.Time,4:0}s #{id} {e.Def.Key} pos {e.Pos} order {e.Order} moving {e.Moving} path {(e.Path == null ? "null" : $"{e.PathIdx}/{e.Path.Count}")} tile {(e.HarvestTile.HasValue ? e.HarvestTile.Value.ToString() : "-")} cargo {e.Cargo} type {e.HarvestType}/{e.CargoType} repath {e.RepathTimer:0.0}");
            }
            return 0;
        }

        static int SelfTest(GameConfig cfg, int maxMinutes)
        {
            cfg.Controllers = cfg.Controllers.Select(_ => "ai").ToArray();
            var game = new Game(cfg);
            var w = game.World;
            var sw = Stopwatch.StartNew();
            int maxTicks = maxMinutes * 60 * World.TickRate;
            while (!w.GameOver && w.Tick < maxTicks)
            {
                game.Advance(World.Dt / cfg.Speed);
                if (w.Tick % (60 * World.TickRate) == 0)
                    Console.WriteLine($"t={w.Time / 60:0}m " + string.Join(" | ", w.Teams.Select(t => $"{t.Name}: steel {t.Amount("steel")} circ {t.Amount("circuits")} plasma {t.Amount("plasma")} pow {t.PowerProduced}/{t.PowerUsed} S{w.Owned(t.Id).Count(e => e.IsStructure)} U{w.Owned(t.Id).Count(e => !e.IsStructure)} K{t.Stats.Kills} ore{t.Stats.OreMined}")));
                // Economy line (balance runs): ore mined so far, how much of it deep, what's regrown and what's left on the surface.
                if (w.Tick % (5 * 60 * World.TickRate) == 0)
                {
                    int mined = w.Teams.Sum(t => t.Stats.OreMined), deep = w.Teams.Sum(t => t.Stats.DeepMined), salvage = w.Teams.Sum(t => t.Stats.SalvageLeft);
                    int share = mined > 0 ? 100 * deep / mined : 0;
                    Console.WriteLine($"econ t={w.Time / 60:0}m mined {mined} surface {mined - deep} deep {deep} deep_share {share}% regrown {w.Regrown} salvage_left {salvage} salvage_mined {w.Teams.Sum(t => t.Stats.SalvageMined)} surface_left {w.Map.Ore.Sum()} kills {w.Teams.Sum(t => t.Stats.Kills)} " +
                                      $"derricks {w.Derricks.Count()} held {w.Derricks.Count(d => d.Team >= 0)} captures {w.Teams.Sum(t => t.Stats.DerricksCaptured)} derrick_steel {(int)w.Teams.Sum(t => t.Stats.DerrickSteel)} upkeep {(int)w.Teams.Sum(t => t.Stats.UpkeepPaid)} offline {w.Entities.Count(e => !e.Dead && e.Offline)} phase {World.PhaseName(w.Phase)}");
                }
            }
            Console.WriteLine($"Simulated {w.Time / 60:0.0} game-minutes in {sw.Elapsed.TotalSeconds:0.0}s real.");
            int Count(string type) => w.EventCounts.TryGetValue(type, out var n) ? n : 0;
            Console.WriteLine($"Fuel: {Count("low_fuel")} trips home to refuel, {Count("refuelled")} refuelled, {Count("stranded")} stranded, {Count("crashed")} crashed; " +
                              $"{w.Entities.Count(e => !e.Dead && e.Stranded)} still stranded.");
            Console.WriteLine($"Stalls: {w.Teams.Count(t => t.Resigned)} resigned: {string.Join(", ", w.Teams.Where(t => t.Resigned).Select(t => t.Name))}");
            Console.WriteLine("Deep mining: " + string.Join(" | ", w.Teams.Select(t => $"{t.Name} {t.SurveySites.Count} surveys, {t.Zones.Count} zones flagged, {w.Owned(t.Id).Count(e => e.Def.Key == "deep_mine")} deep mines")));
            if (World.Profile) Console.WriteLine("Profile (s): " + string.Join(", ", World.Timings.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:0.0}")));
            foreach (var t in w.Teams)
                Console.WriteLine($"{t.Name} built: " + string.Join(", ", t.Stats.Built.Select(kv => $"{kv.Key} x{kv.Value}")));
            Console.WriteLine(Json.Write(ApiServer.Status(game)));
            if (Environment.GetEnvironmentVariable("PEZZ_DUMP_TRUCKS") == "1")
                foreach (var e in w.Entities.Where(e => !e.Dead && e.IsHarvester))
                    Console.WriteLine($"truck #{e.Id} t{e.Team} at {e.Pos} {e.OrderName} dock {e.Dock} type {e.HarvestType} tile {e.HarvestTile} cargo {e.Cargo} fuel {e.Fuel:0} stranded {e.Stranded}");
            Console.WriteLine(StateView.AsciiMap(w, 0));
            return w.GameOver ? 0 : 2;
        }
    }
}
