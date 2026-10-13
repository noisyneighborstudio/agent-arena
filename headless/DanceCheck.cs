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
            if (Environment.GetEnvironmentVariable("PEZ_CROWDDBG") != null)
            {
                foreach (var u in us.Where(e => e.Def.Armor == Armor.Vehicle && Vec2.Dist(e.Pos, new Vec2(26.6f, 9.7f)) < 3f)) Console.WriteLine($"  near vehicle #{u.Id} {u.Def.Key} {u.OrderName} at {u.Pos} to {u.OrderPos} moving {u.Moving} facing {u.Facing:0.00} givingway {u.GivingWay}");
                foreach (var u in us.Where(e => e.Order != Order.Idle)) Console.WriteLine($"  busy #{u.Id} {u.Def.Key} {u.OrderName} at {u.Pos} to {u.OrderPos} moving {u.Moving} ghost {w.Time < u.GhostUntil}");
                for (int i = 0; i < us.Count; i++) for (int j = i + 1; j < us.Count; j++) if (Vec2.Dist(us[i].Pos, us[j].Pos) < (us[i].Def.Radius + us[j].Def.Radius) * 0.7f)
                    Console.WriteLine($"  overlap #{us[i].Id} {us[i].Def.Key} {us[i].OrderName} / #{us[j].Id} {us[j].Def.Key} {us[j].OrderName} {Vec2.Dist(us[i].Pos, us[j].Pos):0.00}");
            }
            foreach (var ev in w.Events.Where(ev => ev.Type == "crushed")) Console.WriteLine($"  [{ev.Tick * World.Dt:0.0}s] {ev.Text}");
            return game;
        }

        public static int Run(string file, float seconds, bool verbose)
        {
            Game game;
            if (file == "crowd") game = Crowd();
            else if (file == "trucks" || file == "truck")
            {
                // The starting economy alone (1 team): its trucks mining and delivering, nobody in the way but each other.
                game = new Game(new GameConfig { Seed = 4, MapSize = 80, Controllers = new[] { "llm", "llm" } });
                if (file == "truck") foreach (var t in game.World.Entities.Where(e => e.IsHarvester && (e.Team != 0 || e != game.World.Owned(0).First(x => x.IsHarvester))).ToList()) game.World.Remove(t);
            }
            else if (file.StartsWith("ai"))
            {
                // "ai" or "aiMINUTES": two scripted AIs play that long first (default 20 minutes).
                game = new Game(new GameConfig { Seed = 5, MapSize = 96, Controllers = new[] { "ai", "ai" } });
                float min = file.Length > 2 ? float.Parse(file.Substring(2)) : 20f;
                for (int i = 0; i < min * 60 * World.TickRate && !game.World.GameOver; i++) game.Advance(World.Dt);
                var ev = game.World.Events.Where(x => x.Type == "crushed").ToList();
                Console.WriteLine($"after {min:0} min of AI vs AI: {ev.Count / 1} crushed events, kills {string.Join("/", game.World.Teams.Select(t => t.Stats.Kills))}, game over {game.World.GameOver} at {game.World.Time / 60:0.0} min; units now: {string.Join(", ", game.World.Entities.Where(x => !x.Dead && !x.IsStructure).GroupBy(x => x.Def.Key).Select(g => $"{g.Key}x{g.Count()}"))}");
                foreach (var x in ev.Take(12)) Console.WriteLine($"  [{x.Tick * World.Dt / 60:0.0}m] {x.Text}");
                if (Environment.GetEnvironmentVariable("PEZ_QUEUEDBG") != null) Console.WriteLine(game.World.QueueReport());
                if (Environment.GetEnvironmentVariable("PEZ_AIDBG") != null)
                    foreach (var t in game.World.Teams)
                    {
                        Console.WriteLine($"  team {t.Id} start {t.StartPos} known: {string.Join("; ", t.KnownEnemyStructures.Select(kv => $"#{kv.Key} {kv.Value.key} t{kv.Value.team} at {kv.Value.origin.X},{kv.Value.origin.Y} {(game.World.Get(kv.Key) is Entity k ? $"alive hp {k.Hp:0} visible {t.Visible[game.World.Map.Idx(k.Origin.X, k.Origin.Y)]}" : "gone")}"))}");
                    }
            }
            else
            {
                game = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
                game.Restore(Snapshot.Parse(System.IO.File.ReadAllText(file)));
            }
            var w = game.World;
            // Settle in first (orders in flight finish), then measure.
            for (int i = 0; i < 10 * World.TickRate; i++) game.Advance(World.Dt);
            w.SettleCounts.Clear();
            var path = new Dictionary<int, float>(); var start = new Dictionary<int, Vec2>(); var last = new Dictionary<int, Vec2>();
            var orders = new Dictionary<int, Dictionary<string, int>>();
            var moving = new Dictionary<int, int>(); var facingTurn = new Dictionary<int, float>(); var lastFacing = new Dictionary<int, float>(); var flips = new Dictionary<int, int>(); var slid = new Dictionary<int, float>(); var slidStill = new Dictionary<int, float>();
            int ticks = (int)(seconds * World.TickRate);
            var trace = (Environment.GetEnvironmentVariable("PEZ_TRACE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
            for (int i = 0; i < ticks; i++)
            {
                game.Advance(World.Dt);
                if (trace.Count > 0 && i % (Environment.GetEnvironmentVariable("PEZ_TRACE_EVERY") is string ev0 ? int.Parse(ev0) : 4) == 0 && i < 20 * World.TickRate)
                    foreach (var id in trace) { var t = w.Get(id); if (t == null) continue;
                        Console.WriteLine($"{w.Time:0.00} #{id} pos {t.Pos.X:0.00},{t.Pos.Y:0.00} f {t.Facing:0.00} {t.OrderName} to {t.OrderPos.X:0.0},{t.OrderPos.Y:0.0} wps {t.Waypoints.Count} loop {t.WaypointLoop} path {(t.Path == null ? "-" : $"{t.PathIdx}/{t.Path.Count} next {t.Path[Math.Min(t.PathIdx, t.Path.Count - 1)].X:0.0},{t.Path[Math.Min(t.PathIdx, t.Path.Count - 1)].Y:0.0}")} ghost {w.Time < t.GhostUntil} tgt {t.TargetId} cd {t.Cooldown:0.0} fired {w.Time - t.LastFiredAt:0.0} grp {t.Group} mv {t.Moving} cap {t.SpeedCap} fuel {t.FuelFraction:P0}"); }
                foreach (var e in w.Entities)
                {
                    if (e.Dead || e.IsStructure || e.IsMine || e.IsCarried) continue;
                    if (!start.ContainsKey(e.Id)) { start[e.Id] = last[e.Id] = e.Pos; path[e.Id] = 0; orders[e.Id] = new(); moving[e.Id] = 0; lastFacing[e.Id] = e.Facing; facingTurn[e.Id] = 0; flips[e.Id] = 0; slid[e.Id] = 0; slidStill[e.Id] = 0; }
                    var dp = e.Pos - last[e.Id]; float dl = dp.Length;
                    if (!e.IsAir && dl > 0.004f)
                    {
                        // Sliding: moved while not walking/driving, or crabwise (over 0.8 rad off its facing).
                        float off = MathF.Abs(MathF.Atan2(MathF.Sin(dp.Angle - e.Facing), MathF.Cos(dp.Angle - e.Facing)));
                        if (!e.Moving) slidStill[e.Id] += dl; else if (off > 0.8f) slid[e.Id] += dl;
                    }
                    path[e.Id] += dl; last[e.Id] = e.Pos;
                    var o = e.OrderName + (e.Retreating ? "+retreat" : "") + (w.Time < e.GhostUntil ? "+ghost" : "");
                    orders[e.Id][o] = orders[e.Id].TryGetValue(o, out var c) ? c + 1 : 1;
                    if (e.Moving) moving[e.Id]++;
                    float df = MathF.Abs(e.Facing - lastFacing[e.Id]); if (df > MathF.PI) df = 2 * MathF.PI - df; facingTurn[e.Id] += df; lastFacing[e.Id] = e.Facing; if (df > 0.8f) flips[e.Id]++;
                }
            }
            var rows = start.Keys.Select(id => w.Get(id)).Where(e => e != null && !e.Dead).Select(e => new
            {
                e, path = path[e.Id], net = Vec2.Dist(start[e.Id], e.Pos), mv = moving[e.Id] / (float)ticks, turn = facingTurn[e.Id], flips = flips[e.Id], slid = slid[e.Id], still = slidStill[e.Id],
                top = string.Join(",", orders[e.Id].OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key}:{kv.Value * 100 / ticks}%")),
            }).ToList();
            var dancers = rows.Where(r => r.path > 4f && r.net < r.path * 0.25f).OrderByDescending(r => r.path - r.net).ToList();
            var crushed = w.Events.Where(ev => ev.Type == "crushed").GroupBy(ev => ev.A).Select(g => g.First()).ToList();
            Console.WriteLine($"run over: {crushed.Count} ({crushed.Count(ev => ev.Text.Contains("your own"))} by their own side)");
            Console.WriteLine($"{rows.Count} units over {seconds:0}s; {dancers.Count} dance (travel > 4 tiles, net < 25% of it); total wasted travel {dancers.Sum(r => r.path - r.net):0} tiles");
            foreach (var g in dancers.GroupBy(r => r.top.Split(':')[0]).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Count(),3} mostly {g.Key}: {string.Join(" ", g.GroupBy(r => r.e.Def.Key).Select(k => $"{k.Key}x{k.Count()}"))}");
            foreach (var r in dancers.Take(verbose ? 60 : 15))
                Console.WriteLine($"  #{r.e.Id} t{r.e.Team} {r.e.Def.Key,-20} path {r.path,5:0.0} net {r.net,4:0.0} moving {r.mv:P0} turned {r.turn,5:0}rad  {r.top}");
            // Spinners: snap round (over 0.8 rad in a tick) again and again, moving or not.
            var spinners = rows.Where(r => r.flips >= seconds / 5f).OrderByDescending(r => r.flips).ToList();
            Console.WriteLine($"{spinners.Count} spin (a facing snap of over 0.8 rad at least every 5 s)");
            foreach (var r in spinners.Take(verbose ? 60 : 15))
                Console.WriteLine($"  #{r.e.Id} t{r.e.Team} {r.e.Def.Key,-20} flips {r.flips,4} path {r.path,5:0.0} net {r.net,4:0.0} moving {r.mv:P0}  {r.top}");
            Console.WriteLine($"settled by the dance guard: {w.SettleCounts.Values.Sum()}" + (w.SettleCounts.Count > 0 ? ": " + string.Join(", ", w.SettleCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")) : ""));
            var sliders = rows.Where(r => r.slid + r.still > 1f).OrderByDescending(r => r.slid + r.still).ToList();
            Console.WriteLine($"{sliders.Count} slide over a tile ({sliders.Sum(r => r.slid):0} tiles crabwise while moving, {sliders.Sum(r => r.still):0} while standing)");
            foreach (var g in sliders.GroupBy(r => r.e.Def.Key).OrderByDescending(g => g.Sum(r => r.slid + r.still)))
                Console.WriteLine($"  {g.Key,-20} x{g.Count(),3}: crabwise {g.Sum(r => r.slid),5:0.0} standing {g.Sum(r => r.still),5:0.0}");
            foreach (var r in sliders.Take(verbose ? 40 : 10))
                Console.WriteLine($"  #{r.e.Id} t{r.e.Team} {r.e.Def.Key,-20} crabwise {r.slid,5:0.0} standing {r.still,5:0.0} path {r.path,5:0.0}  {r.top}");
            return 0;
        }

        /// <summary>
        /// Debug aid (--drift A --to B): how far a copy restored from snapshot A and run to B's tick ends up from B, unit by
        /// unit: what a browser view, which runs on between snapshots, has to correct (and slide) at each one.
        /// </summary>
        public static int Drift(string a, string b)
        {
            var game = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
            game.Restore(Snapshot.Parse(System.IO.File.ReadAllText(a)));
            var truth = new Game(new GameConfig { Seed = 1, MapSize = 48, Controllers = new[] { "llm" } });
            truth.Restore(Snapshot.Parse(System.IO.File.ReadAllText(b)));
            var w = game.World; var t = truth.World;
            int from = w.Tick;
            // As the browser does: B's orders, given again at their ticks (snapshots from before rules 37's host carry none).
            int replayed = 0;
            foreach (var x in Snapshot.Parse(System.IO.File.ReadAllText(b)).Obj("world").Arr("commands"))
                if (x is List<object> l && l.Count >= 4 && l[3] is Dictionary<string, object> cmd && Environment.GetEnvironmentVariable("PEZ_NOREPLAY") == null)
                { game.Replay[(long)(double)l[0]] = new LoggedCommand { Seq = (long)(double)l[0], Tick = (int)(double)l[1], Team = (int)(double)l[2], Cmd = cmd }; replayed++; }
            Console.WriteLine($"B carries {replayed} orders to replay");
            while (w.Tick < t.Tick) game.CatchUp(Math.Min(t.Tick, w.Tick + 60), 0);
            var rows = t.Entities.Where(e => !e.Dead && !e.IsStructure && !e.IsMine && !e.IsCarried && w.ById.ContainsKey(e.Id))
                .Select(e => (e, c: w.ById[e.Id], d: Vec2.Dist(e.Pos, w.ById[e.Id].Pos))).OrderByDescending(r => r.d).ToList();
            Console.WriteLine($"ran {from} to {t.Tick} ({(t.Tick - from) * World.Dt:0.0}s): {rows.Count} units; off by > 0.1 tile: {rows.Count(r => r.d > 0.1f)}, > 0.5: {rows.Count(r => r.d > 0.5f)}, > 2: {rows.Count(r => r.d > 2f)}; mean {(rows.Count > 0 ? rows.Average(r => r.d) : 0):0.00}");
            int sameOrder = rows.Count(r => r.d > 0.1f && r.e.Order == r.c.Order && Vec2.Dist(r.e.OrderPos, r.c.OrderPos) < 0.5f && r.e.TargetId == r.c.TargetId);
            Console.WriteLine($"  of those off by > 0.1, {sameOrder} have the same order in both (drift without a new command)");
            foreach (var r in rows.Take(25))
                Console.WriteLine($"  #{r.e.Id} t{r.e.Team} {r.e.Def.Key,-16} off {r.d,5:0.00}  truth {r.e.OrderName} to {r.e.OrderPos} tgt {r.e.TargetId}  copy {r.c.OrderName} to {r.c.OrderPos} tgt {r.c.TargetId}");
            return 0;
        }
    }
}
