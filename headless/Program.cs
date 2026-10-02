using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>
    /// pez-headless [--port 7777] [--controllers llm,ai] [--seed N] [--speed 1] [--selftest]
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
                Speed = float.Parse(Arg("--speed", "1"), System.Globalization.CultureInfo.InvariantCulture),
                Controllers = Arg("--controllers", "llm,ai").Split(','),
            };
            if (args.Contains("--selftest")) return SelfTest(cfg, int.Parse(Arg("--max-minutes", "30")));

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
                    Console.WriteLine($"t={w.Time / 60:0}m " + string.Join(" | ", w.Teams.Select(t => $"{t.Name}: ${t.Credits} pow {t.PowerProduced}/{t.PowerUsed} S{w.Owned(t.Id).Count(e => e.IsStructure)} U{w.Owned(t.Id).Count(e => !e.IsStructure)} K{t.Stats.Kills} ore{t.Stats.OreHarvested}")));
            }
            Console.WriteLine($"Simulated {w.Time / 60:0.0} game-minutes in {sw.Elapsed.TotalSeconds:0.0}s real.");
            Console.WriteLine(Json.Write(ApiServer.Status(game)));
            Console.WriteLine(StateView.AsciiMap(w, 0));
            return w.GameOver ? 0 : 2;
        }
    }
}
