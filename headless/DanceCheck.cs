using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>
    /// Debug aid (--dance-check FILE [--seconds N]): restore a snapshot, run it, and list units that "dance": travel a
    /// lot without getting anywhere (path length over net displacement), grouped by what they were doing. FILE "crowd" is a
    /// mixed army sent to one spot; PEZ_TRACE=id,id prints those units every 0.2 s for the first 20 s. A room's spectator
    /// snapshot (GET /watch/<n>/snapshot) is a FILE that works.
    /// </summary>
    static class DanceCheck
    {
        /// <summary>A mixed army of 48 (mammoths, heavies, artillery, infantry) sent to one spot in one order.</summary>
        static Game Crowd()
        {
            var game = new Game(new GameConfig { Seed = 3, MapSize = 80, Controllers = new[] { "llm", "llm" } });
            var w = game.World;
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var ids = new List<object>();
            string[] mix = { "mammoth_tank", "heavy_tank", "heavy_tank", "artillery", "light_tank", "rifleman", "rocket_soldier", "rifleman" };
            for (int i = 0; i < 48; i++)
            {
                var u = w.SpawnUnit(0, mix[i % mix.Length], hq);
                u.Pos = u.PrevPos = u.GuardPos = hq.Center + new Vec2(4 + (i % 8) * 1.6f, -6 + (i / 8) * 1.6f);
                ids.Add((double)u.Id);
            }
            var r = Commands.Execute(w, 0, new Dictionary<string, object> { ["type"] = "move", ["units"] = ids, ["x"] = (double)(hq.Center.X + 22), ["y"] = (double)hq.Center.Y });
            Console.WriteLine("order: " + r["result"]);
            for (int i = 0; i < 30 * World.TickRate; i++) game.Advance(World.Dt);
            int overlaps = 0; var us = w.Owned(0).Where(e => !e.IsStructure).ToList();
            for (int i = 0; i < us.Count; i++) for (int j = i + 1; j < us.Count; j++) if (Vec2.Dist(us[i].Pos, us[j].Pos) < (us[i].Def.Radius + us[j].Def.Radius) * 0.7f) overlaps++;
            Console.WriteLine($"after 30s: {us.Count(e => e.Order == Order.Idle)} of {us.Count} idle, {overlaps} pairs overlapping by over 30%");
            return game;
        }

        public static int Run(string file, float seconds, bool verbose)
        {
            Game game;
            if (file == "crowd") game = Crowd();
            else
            {
                game = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
                game.Restore(Snapshot.Parse(System.IO.File.ReadAllText(file)));
            }
            var w = game.World;
            // Settle in first (orders in flight finish), then measure.
            for (int i = 0; i < 10 * World.TickRate; i++) game.Advance(World.Dt);
            var path = new Dictionary<int, float>(); var start = new Dictionary<int, Vec2>(); var last = new Dictionary<int, Vec2>();
            var orders = new Dictionary<int, Dictionary<string, int>>();
            var moving = new Dictionary<int, int>(); var facingTurn = new Dictionary<int, float>(); var lastFacing = new Dictionary<int, float>();
            int ticks = (int)(seconds * World.TickRate);
            var trace = (Environment.GetEnvironmentVariable("PEZ_TRACE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
            for (int i = 0; i < ticks; i++)
            {
                game.Advance(World.Dt);
                if (trace.Count > 0 && i % 4 == 0 && i < 20 * World.TickRate)
                    foreach (var id in trace) { var t = w.Get(id); if (t == null) continue;
                        Console.WriteLine($"{w.Time:0.00} #{id} pos {t.Pos.X:0.00},{t.Pos.Y:0.00} f {t.Facing:0.00} {t.OrderName} to {t.OrderPos.X:0.0},{t.OrderPos.Y:0.0} wps {t.Waypoints.Count} loop {t.WaypointLoop} path {(t.Path == null ? "-" : $"{t.PathIdx}/{t.Path.Count} next {t.Path[Math.Min(t.PathIdx, t.Path.Count - 1)].X:0.0},{t.Path[Math.Min(t.PathIdx, t.Path.Count - 1)].Y:0.0}")} ghost {w.Time < t.GhostUntil} grp {t.Group} cap {t.SpeedCap} fuel {t.FuelFraction:P0}"); }
                foreach (var e in w.Entities)
                {
                    if (e.Dead || e.IsStructure || e.IsMine || e.IsCarried) continue;
                    if (!start.ContainsKey(e.Id)) { start[e.Id] = last[e.Id] = e.Pos; path[e.Id] = 0; orders[e.Id] = new(); moving[e.Id] = 0; lastFacing[e.Id] = e.Facing; facingTurn[e.Id] = 0; }
                    path[e.Id] += Vec2.Dist(e.Pos, last[e.Id]); last[e.Id] = e.Pos;
                    var o = e.OrderName + (e.Retreating ? "+retreat" : "") + (w.Time < e.GhostUntil ? "+ghost" : "");
                    orders[e.Id][o] = orders[e.Id].TryGetValue(o, out var c) ? c + 1 : 1;
                    if (e.Moving) moving[e.Id]++;
                    float df = MathF.Abs(e.Facing - lastFacing[e.Id]); if (df > MathF.PI) df = 2 * MathF.PI - df; facingTurn[e.Id] += df; lastFacing[e.Id] = e.Facing;
                }
            }
            var rows = start.Keys.Select(id => w.Get(id)).Where(e => e != null && !e.Dead).Select(e => new
            {
                e, path = path[e.Id], net = Vec2.Dist(start[e.Id], e.Pos), mv = moving[e.Id] / (float)ticks, turn = facingTurn[e.Id],
                top = string.Join(",", orders[e.Id].OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key}:{kv.Value * 100 / ticks}%")),
            }).ToList();
            var dancers = rows.Where(r => r.path > 4f && r.net < r.path * 0.25f).OrderByDescending(r => r.path - r.net).ToList();
            Console.WriteLine($"{rows.Count} units over {seconds:0}s; {dancers.Count} dance (travel > 4 tiles, net < 25% of it); total wasted travel {dancers.Sum(r => r.path - r.net):0} tiles");
            foreach (var g in dancers.GroupBy(r => r.top.Split(':')[0]).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Count(),3} mostly {g.Key}: {string.Join(" ", g.GroupBy(r => r.e.Def.Key).Select(k => $"{k.Key}x{k.Count()}"))}");
            foreach (var r in dancers.Take(verbose ? 60 : 15))
                Console.WriteLine($"  #{r.e.Id} t{r.e.Team} {r.e.Def.Key,-20} path {r.path,5:0.0} net {r.net,4:0.0} moving {r.mv:P0} turned {r.turn,5:0}rad  {r.top}");
            return 0;
        }
    }
}
