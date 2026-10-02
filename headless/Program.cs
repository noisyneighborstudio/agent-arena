using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>
    /// pez-headless [--port 7777] [--controllers llm,ai] [--seed N] [--map-size 80] [--speed 1] [--selftest]
    ///              [--open [--max-players 8] [--max-map-size 160] [--house-ais 1] [--house-resign-above 5]]
    ///              open arena: outside agents join through the gateway; scripted "house" seats are passive and make room
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
                Open = args.Contains("--open"),
                MaxPlayers = int.Parse(Arg("--max-players", "8")),
                MaxMapSize = int.Parse(Arg("--max-map-size", "160")),
                HouseAIs = int.Parse(Arg("--house-ais", "1")),
                HouseResignAbove = int.Parse(Arg("--house-resign-above", "5")),
                Speed = float.Parse(Arg("--speed", "1"), System.Globalization.CultureInfo.InvariantCulture),
                Controllers = Arg("--controllers", "llm,ai").Split(','),
            };
            World.Profile = args.Contains("--profile");
            if (args.Contains("--selftest")) return SelfTest(cfg, int.Parse(Arg("--max-minutes", "30")));
            if (args.Contains("--test")) return Tests.Run();
            if (args.Contains("--trace")) return Trace(cfg, int.Parse(Arg("--trace", "1")), float.Parse(Arg("--seconds", "60")));

            var game = new Game(cfg);
            var api = new ApiServer(int.Parse(Arg("--port", "7777"))) { Log = Console.WriteLine };
            api.OnCommand = (team, cmd, res) => Console.WriteLine($"[{game.World.Time,6:0.0}s] team{team} {cmd} -> {res}");
            api.Start();
            Console.WriteLine($"Headless game: seed {cfg.Seed}, controllers [{string.Join(",", cfg.Controllers)}], speed {cfg.Speed}");

            var sw = Stopwatch.StartNew();
            double last = 0;
            long lastSeq = 0;
            bool announcedOver = false;
            while (true)
            {
                double now = sw.Elapsed.TotalSeconds;
                game.Advance((float)(now - last));
                last = now;
                api.Pump(game);
                var w = game.World;
                foreach (var e in w.Events.Where(e => e.Seq > lastSeq && (e.Type == "chat" || e.Type == "defeated" || e.Type == "game_over")))
                    Console.WriteLine($"[{e.Tick * World.Dt,6:0.0}s] {(e.Team >= 0 ? w.Teams[e.Team].Name : "")}: {e.Text}");
                if (w.Events.Count > 0) lastSeq = w.Events[^1].Seq;
                if (w.GameOver && !announcedOver) { announcedOver = true; Console.WriteLine(Json.Write(ApiServer.Status(game))); }
                if (!w.GameOver) announcedOver = false;
                Thread.Sleep(5);
            }
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
            }
            Console.WriteLine($"Simulated {w.Time / 60:0.0} game-minutes in {sw.Elapsed.TotalSeconds:0.0}s real.");
            if (World.Profile) Console.WriteLine("Profile (s): " + string.Join(", ", World.Timings.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:0.0}")));
            foreach (var t in w.Teams)
                Console.WriteLine($"{t.Name} built: " + string.Join(", ", t.Stats.Built.Select(kv => $"{kv.Key} x{kv.Value}")));
            Console.WriteLine(Json.Write(ApiServer.Status(game)));
            Console.WriteLine(StateView.AsciiMap(w, 0));
            return w.GameOver ? 0 : 2;
        }
    }
}
