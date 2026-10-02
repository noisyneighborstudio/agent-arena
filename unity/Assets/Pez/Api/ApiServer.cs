using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Pez.Sim;

namespace Pez.Api
{
    /// <summary>
    /// Local HTTP control API. The listener thread only parses requests and queues them;
    /// Pump() runs them on the game thread between ticks, so the sim stays single-threaded.
    /// </summary>
    public class ApiServer
    {
        class Pending
        {
            public HttpListenerContext Ctx;
            public string Body;
        }

        /// <summary>A parked /api/wait request: answered at its deadline, or early when a priority alert arrives.</summary>
        class Waiter
        {
            public HttpListenerContext Ctx;
            public World World;
            public int Team;
            public long AlertSince, EventSince;
            public int OrdersSince = -1;
            public Priority Min;
            public bool Interruptible;
            public DateTime Started, Deadline;
        }

        readonly List<Waiter> waiters = new List<Waiter>();

        readonly HttpListener listener = new HttpListener();
        readonly ConcurrentQueue<Pending> queue = new ConcurrentQueue<Pending>();
        Thread thread;
        volatile bool running;
        public int Port { get; }
        public Action<string> Log = _ => { };
        /// <summary>Raised on the game thread when a command batch is executed: (team, command json, result json).</summary>
        public Action<int, string, string> OnCommand;
        /// <summary>Host-provided screenshot hook (Unity only): saves a PNG to the given path.</summary>
        public Action<string> OnScreenshot;
        /// <summary>Host-provided camera hook: {"x","y","distance","yaw","edge_pan":false}.</summary>
        public Action<Dictionary<string, object>> OnCamera;

        public ApiServer(int port) { Port = port; }

        public void Start()
        {
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Prefixes.Add($"http://localhost:{Port}/");
            listener.Start();
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "PezApi" };
            thread.Start();
            Log($"Pezz API listening on http://127.0.0.1:{Port}/");
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); listener.Close(); } catch { }
        }

        void Loop()
        {
            while (running)
            {
                try
                {
                    var ctx = listener.GetContext();
                    string body = null;
                    if (ctx.Request.HasEntityBody)
                        using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                    queue.Enqueue(new Pending { Ctx = ctx, Body = body });
                }
                catch (Exception ex) { if (running) Log("API listener error: " + ex.Message); }
            }
        }

        /// <summary>Call from the game thread every frame/tick.</summary>
        public void Pump(Game game)
        {
            while (queue.TryDequeue(out var p))
            {
                int status = 200;
                string payload, contentType = "application/json";
                try
                {
                    payload = Handle(game, p, ref contentType, ref status);
                }
                catch (UnauthorizedAccessException ex)
                {
                    status = 401;
                    payload = Json.Write(new JObj().Set("ok", false).Set("error", ex.Message));
                }
                catch (Exception ex)
                {
                    status = 400;
                    payload = Json.Write(new JObj().Set("ok", false).Set("error", ex.Message));
                }
                if (payload != null) Respond(p.Ctx, status, payload, contentType); // null = parked waiter
            }
            ServiceWaiters(game);
        }

        void ServiceWaiters(Game game)
        {
            var now = DateTime.UtcNow;
            for (int i = waiters.Count - 1; i >= 0; i--)
            {
                var wt = waiters[i];
                var w = game.World;
                bool restarted = w != wt.World;
                var fresh = restarted ? new List<Alert>() : w.Alerts.Since(wt.Team, wt.AlertSince, wt.Min).ToList();
                // New commander's orders also end the wait: the model should read them right away.
                bool newOrders = !restarted && wt.OrdersSince >= 0 && w.Teams[wt.Team].OrdersVersion > wt.OrdersSince;
                bool interrupt = wt.Interruptible && (fresh.Count > 0 || newOrders);
                if (!interrupt && !restarted && now < wt.Deadline && !w.GameOver) continue;
                waiters.RemoveAt(i);
                Respond(wt.Ctx, 200, Json.Write(WaitResult(w, wt, interrupt, fresh, now)), "application/json");
            }
        }

        static JObj WaitResult(World w, Waiter wt, bool interrupted, List<Alert> fresh, DateTime now) => new JObj()
            .Set("interrupted", interrupted)
            .Set("waited_s", (float)Math.Round((now - wt.Started).TotalSeconds, 1))
            .Set("new_alerts", StateView.AlertsJson(w, fresh.OrderByDescending(a => a.Priority)))
            .Set("orders_changed", wt.OrdersSince >= 0 && w.Teams.Count > wt.Team && w.Teams[wt.Team].OrdersVersion > wt.OrdersSince)
            .Set("state", StateView.TeamState(w, Math.Min(wt.Team, w.Teams.Count - 1), wt.EventSince));

        // ---- Player tokens (open arena). Control tokens act for one team; view tokens only read its fogged view.
        readonly Dictionary<string, (World world, int team)> controlTokens = new Dictionary<string, (World, int)>();
        readonly Dictionary<string, (World world, int team)> viewTokens = new Dictionary<string, (World, int)>();

        static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        static string TokenFrom(HttpListenerRequest req)
        {
            var auth = req.Headers["Authorization"];
            if (auth != null && auth.StartsWith("Bearer ")) return auth.Substring(7).Trim();
            return req.QueryString["token"];
        }

        /// <summary>The team a request acts for: from its control token, or (local callers) an explicit team=N.</summary>
        int TeamParam(HttpListenerRequest req, World w)
        {
            var tok = TokenFrom(req);
            if (tok != null)
            {
                if (!controlTokens.TryGetValue(tok, out var owner) || owner.world != w) throw new UnauthorizedAccessException("unknown or expired token; join again");
                if (w.Teams[owner.team].Left) throw new UnauthorizedAccessException("you left this arena; join again to play");
                return owner.team;
            }
            var s = req.QueryString["team"];
            if (s == null || !int.TryParse(s, out var t) || t < 0 || t >= w.Teams.Count)
                throw new ArgumentException($"query parameter team=0..{w.Teams.Count - 1} (or a player token) is required");
            return t;
        }

        /// <summary>Team for a read-only view: a view token, a control token, team=N, or -1 for the all-seeing spectator.</summary>
        int ViewTeam(HttpListenerRequest req, World w)
        {
            var vt = req.QueryString["view"];
            if (vt != null)
            {
                if (!viewTokens.TryGetValue(vt, out var owner) || owner.world != w) throw new UnauthorizedAccessException("unknown or expired view link");
                return owner.team;
            }
            if (TokenFrom(req) != null || req.QueryString["team"] != null) return TeamParam(req, w);
            return -1;
        }

        string Handle(Game game, Pending p, ref string contentType, ref int status)
        {
            var req = p.Ctx.Request;
            var w = game.World;
            var path = req.Url.AbsolutePath.TrimEnd('/');
            var method = req.HttpMethod;

            switch (path)
            {
                case "":
                case "/api":
                    contentType = "text/plain";
                    return "Pezz RTS control API\n\nGET  /api/rules\nGET  /api/state?team=N[&since=SEQ]\nGET  /api/map?team=N\nGET  /api/alerts?team=N[&since=SEQ&min=medium|high|critical]\nGET  /api/wait?team=N&seconds=S[&since=ALERT_SEQ&events_since=SEQ&min=high|critical|none]  (returns early on a new priority alert)\nPOST /api/command?team=N   body: {\"commands\":[...]} | [...] | {...}\nPOST /api/join?team=N      body: {\"name\":\"Claude\"}\nGET  /api/status\nPOST /api/admin/restart    body: {\"seed\":1,\"map_size\":112,\"controllers\":[\"llm\",\"llm\"],\"speed\":1}\nPOST /api/admin/speed      body: {\"speed\":0.5}\nPOST /api/admin/orders?team=N  body: {\"text\":\"standing orders for that team's commander\"}\n\n" + Commands.Help;
                case "/api/rules":
                    return Json.Write(StateView.Rules());
                case "/api/state":
                    {
                        int team = TeamParam(req, w);
                        long since = long.TryParse(req.QueryString["since"], out var s) ? s : 0;
                        return Json.Write(StateView.TeamState(w, team, since));
                    }
                case "/api/alerts":
                    {
                        int team = TeamParam(req, w);
                        long since = long.TryParse(req.QueryString["since"], out var s) ? s : 0;
                        var min = ParsePriority(req.QueryString["min"], Priority.Medium);
                        return Json.Write(new JObj().Set("last_alert_seq", w.Alerts.LastSeq)
                            .Set("orders_version", w.Teams[team].OrdersVersion).Set("standing_orders", w.Teams[team].StandingOrders)
                            .Set("new_alerts", StateView.AlertsJson(w, w.Alerts.Since(team, since, min).OrderByDescending(a => a.Priority)))
                            .Set("active", StateView.AlertsJson(w, w.Alerts.Active(w, team))));
                    }
                case "/api/wait":
                    {
                        // Long-poll: let the game run for `seconds`, but come back early if a new alert at or
                        // above `min` (default high) is raised for this team, the way a human hears "base under attack".
                        int team = TeamParam(req, w);
                        float secs = Math.Max(0.5f, Math.Min(60f, float.TryParse(req.QueryString["seconds"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sv) ? sv : 5f));
                        var wt = new Waiter
                        {
                            Ctx = p.Ctx, World = w, Team = team,
                            AlertSince = long.TryParse(req.QueryString["since"], out var a1) ? a1 : w.Alerts.LastSeq,
                            OrdersSince = int.TryParse(req.QueryString["orders_version"], out var ov) ? ov : -1,
                            EventSince = long.TryParse(req.QueryString["events_since"], out var e1) ? e1 : 0,
                            Min = ParsePriority(req.QueryString["min"], Priority.High),
                            Interruptible = req.QueryString["min"] != "none",
                            Started = DateTime.UtcNow,
                        };
                        wt.Deadline = wt.Started.AddSeconds(secs);
                        waiters.Add(wt);
                        return null;
                    }
                case "/api/map":
                    contentType = "text/plain";
                    return StateView.AsciiMap(w, TeamParam(req, w));
                case "/api/status":
                    return Json.Write(Status(game));
                case "/api/register":
                    {
                        // Open arena: a new outside player joins. Returns a control token (keep it secret) and a
                        // read-only view token for their personal web view.
                        if (method != "POST") { status = 405; return "{\"ok\":false,\"error\":\"POST required\"}"; }
                        var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object>;
                        var name = Text.Name(d?.Str("name"));
                        var team = w.AddTeam("llm", name, out var err);
                        if (team == null) { status = 409; return Json.Write(new JObj().Set("ok", false).Set("error", err)); }
                        var token = NewToken(); var view = NewToken();
                        controlTokens[token] = (w, team.Id);
                        viewTokens[view] = (w, team.Id);
                        Log($"Player joined: {name} as {team.Name} (team {team.Id}); map now {w.Map.W}x{w.Map.H}");
                        return Json.Write(new JObj().Set("ok", true).Set("token", token).Set("view_token", view)
                            .Set("team", team.Id).Set("flavor", team.Name).Set("name", name)
                            .Set("base", $"{(int)team.StartPos.X},{(int)team.StartPos.Y} sector {StateView.Sector(w.Map, team.StartPos)}")
                            .Set("map", $"{w.Map.W}x{w.Map.H}"));
                    }
                case "/api/leave":
                    {
                        if (method != "POST") { status = 405; return "{\"ok\":false,\"error\":\"POST required\"}"; }
                        int team = TeamParam(req, w);
                        var msg = w.Leave(team);
                        Log(msg);
                        return Json.Write(new JObj().Set("ok", true).Set("result", msg));
                    }
                case "/api/lobby":
                    return Json.Write(new JObj()
                        .Set("open", w.Open).Set("map", $"{w.Map.W}x{w.Map.H}").Set("max_map", w.MaxMapSize)
                        .Set("players", w.ActivePlayers).Set("max_players", w.MaxPlayers).Set("time_s", (float)Math.Round(w.Time, 1))
                        .Set("teams", w.Teams.Select(t => new JObj().Set("flavor", t.Name).Set("player", t.PlayerName ?? t.Controller)
                            .Set("status", t.Left ? "left" : t.Defeated ? "eliminated" : "playing")
                            .Set("structures", w.Owned(t.Id).Count(e => e.IsStructure)).Set("kills", t.Stats.Kills)).ToList()));
                case "/api/view/map":
                    return Json.Write(ViewMap(w, ViewTeam(req, w)));
                case "/api/view/frame":
                    return Json.Write(ViewFrame(w, ViewTeam(req, w)));
                case "/api/join":
                    {
                        int team = TeamParam(req, w);
                        var d = p.Body != null ? Json.Parse(p.Body) as Dictionary<string, object> : null;
                        var name = Text.Name(d?.Str("name") ?? "LLM");
                        w.Teams[team].PlayerName = name;
                        if (w.Teams[team].Controller != "human") w.Teams[team].Controller = "llm";
                        w.Emit("chat", team, text: $"{name} has taken command of {w.Teams[team].Name}.");
                        return Json.Write(new JObj().Set("ok", true).Set("team", team).Set("name", w.Teams[team].Name));
                    }
                case "/api/command":
                    {
                        if (method != "POST") { status = 405; return "{\"ok\":false,\"error\":\"POST required\"}"; }
                        int team = TeamParam(req, w);
                        var parsed = Json.Parse(p.Body ?? "[]");
                        List<object> cmds = parsed is List<object> l ? l
                            : parsed is Dictionary<string, object> d && d.TryGetValue("commands", out var c) && c is List<object> cl ? cl
                            : new List<object> { parsed };
                        var results = new List<object>();
                        foreach (var cmd in cmds)
                        {
                            var r = cmd is Dictionary<string, object> cd ? Commands.Execute(w, team, cd) : new JObj().Set("ok", false).Set("error", "each command must be an object");
                            results.Add(r);
                            OnCommand?.Invoke(team, Json.Write(cmd), Json.Write(r));
                        }
                        return Json.Write(new JObj().Set("results", results).Set("stockpile", StateView.Stockpile(w.Teams[team])).Set("time_s", (float)Math.Round(w.Time, 1)));
                    }
                case "/api/admin/restart":
                    {
                        var d = p.Body != null ? Json.Parse(p.Body) as Dictionary<string, object> : null;
                        var cfg = new GameConfig
                        {
                            Seed = (int)(d?.Num("seed", game.Config.Seed) ?? game.Config.Seed),
                            Speed = d?.Num("speed", game.Speed) ?? game.Speed,
                            MapSize = (int)(d?.Num("map_size", game.Config.MapSize) ?? game.Config.MapSize),
                            Open = d != null && d.TryGetValue("open", out var op) ? op is bool ob && ob : game.Config.Open,
                        };
                        if (d != null && d.TryGetValue("controllers", out var cs) && cs is List<object> cl2) cfg.Controllers = cl2.Select(x => x.ToString()).ToArray();
                        else cfg.Controllers = game.Config.Controllers;
                        if (d != null && d.TryGetValue("orders", out var os) && os is List<object> ol) cfg.Orders = ol.Select(x => x?.ToString() ?? "").ToArray();
                        else cfg.Orders = game.Config.Orders;
                        game.Restart(cfg);
                        return Json.Write(new JObj().Set("ok", true).Set("seed", cfg.Seed).Set("map_size", game.World.Map.W).Set("controllers", cfg.Controllers.ToList()));
                    }
                case "/api/admin/screenshot":
                    {
                        if (OnScreenshot == null) { status = 501; return "{\"ok\":false,\"error\":\"no renderer (headless)\"}"; }
                        var file = req.QueryString["path"] ?? Path.Combine(Path.GetTempPath(), $"pez-{DateTime.Now:HHmmss}.png");
                        OnScreenshot(file);
                        return Json.Write(new JObj().Set("ok", true).Set("path", file).Set("note", "written at end of frame"));
                    }
                case "/api/admin/camera":
                    {
                        var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object>;
                        OnCamera?.Invoke(d);
                        return Json.Write(new JObj().Set("ok", OnCamera != null));
                    }
                case "/api/admin/orders":
                    {
                        // Human commander -> team. GET reads, POST {"text": "..."} replaces (empty clears).
                        int team = TeamParam(req, w);
                        if (method == "POST")
                        {
                            var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object>;
                            w.SetOrders(team, d?.Str("text") ?? "");
                        }
                        return Json.Write(new JObj().Set("ok", true).Set("team", team).Set("standing_orders", w.Teams[team].StandingOrders).Set("orders_version", w.Teams[team].OrdersVersion));
                    }
                case "/api/admin/speed":
                    {
                        var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object>;
                        game.Speed = Math.Max(0.1f, Math.Min(8f, d.Num("speed", 1)));
                        if (d.TryGetValue("paused", out var pz) && pz is bool pb) game.Paused = pb;
                        return Json.Write(new JObj().Set("ok", true).Set("speed", game.Speed).Set("paused", game.Paused));
                    }
                default:
                    status = 404;
                    return Json.Write(new JObj().Set("ok", false).Set("error", "not found; GET / for help"));
            }
        }

        static Priority ParsePriority(string s, Priority def) => s switch
        {
            "medium" => Priority.Medium,
            "high" => Priority.High,
            "critical" => Priority.Critical,
            _ => def,
        };

        // ---- Web viewer data (a player's fogged view, or everything for the spectator)

        static JObj ViewMap(World w, int team)
        {
            var m = w.Map;
            var tiles = new System.Text.StringBuilder(m.W * m.H);
            var ore = new System.Text.StringBuilder(m.W * m.H);
            for (int i = 0; i < m.Tiles.Length; i++)
            {
                tiles.Append("gdrw"[(int)m.Tiles[i]]);
                ore.Append(m.Ore[i] <= 0 ? '.' : "icxu"[m.OreType[i]]);
            }
            return new JObj().Set("w", m.W).Set("h", m.H).Set("version", w.MapVersion).Set("tiles", tiles.ToString()).Set("ore", ore.ToString())
                .Set("team", team).Set("flavor", team >= 0 ? w.Teams[team].Name : "Spectator");
        }

        static JObj ViewFrame(World w, int team)
        {
            var m = w.Map;
            string shroud = null;
            if (team >= 0)
            {
                var t = w.Teams[team];
                var sb = new System.Text.StringBuilder(m.W * m.H);
                for (int i = 0; i < t.Visible.Length; i++) sb.Append(t.Visible[i] ? '2' : t.Explored[i] ? '1' : '0');
                shroud = sb.ToString();
            }
            var ents = new List<object>();
            foreach (var e in w.Entities)
            {
                if (e.Dead || e.IsCarried) continue;
                bool known = team < 0 || w.IsVisibleTo(team, e) || (e.IsStructure && w.Teams[team].KnownEnemyStructures.ContainsKey(e.Id));
                if (!known) continue;
                var c = e.Center;
                ents.Add(new List<object> { e.Id, e.Def.Key, e.Team, Math.Round(c.X, 2), Math.Round(c.Y, 2), (int)(100 * e.Hp / e.Def.MaxHp),
                    Math.Round(e.Facing, 2), e.IsStructure ? (int)(e.BuildProgress * 100) : -1, e.IsStructure ? e.Def.SizeX : 0 });
            }
            var shots = new List<object>();
            long since = w.Events.Count > 0 ? w.Events[w.Events.Count - 1].Seq - 400 : 0;
            foreach (var ev in w.Events)
            {
                if (ev.Seq < since || w.Tick - ev.Tick > 10) continue;
                if (ev.Type != "shot" && ev.Type != "fire" && ev.Type != "hit" && ev.Type != "destroyed") continue;
                if (team >= 0 && ev.Team != team && !w.Teams[team].Visible[m.Idx(Math.Clamp((int)ev.Pos.X, 0, m.W - 1), Math.Clamp((int)ev.Pos.Y, 0, m.H - 1))]) continue;
                shots.Add(new List<object> { ev.Seq, ev.Type, Math.Round(ev.Pos.X, 1), Math.Round(ev.Pos.Y, 1), Math.Round(ev.Pos2.X, 1), Math.Round(ev.Pos2.Y, 1), ev.Team });
            }
            var o = new JObj().Set("tick", w.Tick).Set("time_s", (float)Math.Round(w.Time, 1)).Set("version", w.MapVersion)
                .Set("teams", w.Teams.Select(t => new JObj().Set("id", t.Id).Set("flavor", t.Name).Set("player", t.PlayerName ?? t.Controller)
                    .Set("status", t.Left ? "left" : t.Defeated ? "eliminated" : "playing").Set("kills", t.Stats.Kills)).ToList())
                .Set("entities", ents).Set("effects", shots)
                .Set("chat", w.Events.Where(e => e.Type == "chat").Reverse().Take(8).Reverse()
                    .Select(e => $"{(e.Team >= 0 ? w.Teams[e.Team].Name : "arena")}: {e.Text}").ToList());
            if (shroud != null) o.Set("shroud", shroud);
            if (team >= 0)
            {
                var t = w.Teams[team];
                o.Set("you", new JObj().Set("team", team).Set("flavor", t.Name).Set("player", t.PlayerName).Set("stockpile", StateView.Stockpile(t))
                    .Set("power", $"{t.PowerUsed}/{t.PowerProduced}").Set("status", t.Left ? "left" : t.Defeated ? "eliminated" : "playing"));
                o.Set("alerts", w.Alerts.Active(w, team).Take(4).Select(a => AlertLog.Describe(w, a)).ToList());
                o.Set("standing_orders", t.StandingOrders);
            }
            return o;
        }

        public static JObj Status(Game game)
        {
            var w = game.World;
            return new JObj()
                .Set("tick", w.Tick).Set("time_s", (float)Math.Round(w.Time, 1))
                .Set("speed", game.Speed).Set("paused", game.Paused).Set("map_size", w.Map.W)
                .Set("game_over", w.GameOver).Set("winner", w.Winner)
                .Set("sim_errors", w.Errors).Set("last_sim_error", w.LastError)
                .Set("teams", w.Teams.Select(t => new JObj()
                    .Set("team", t.Id).Set("name", t.Name).Set("controller", t.Controller).Set("player", t.PlayerName)
                    .Set("stockpile", StateView.Stockpile(t)).Set("defeated", t.Defeated)
                    .Set("structures", w.Owned(t.Id).Count(e => e.IsStructure)).Set("units", w.Owned(t.Id).Count(e => !e.IsStructure))
                    .Set("kills", t.Stats.Kills).Set("ore_mined", t.Stats.OreMined)).ToList())
                .Set("chat", w.Events.Where(e => e.Type == "chat").Reverse().Take(10).Reverse()
                    .Select(e => $"[{e.Tick * World.Dt:0}s] {(e.Team >= 0 ? w.Teams[e.Team].Name : "server")}: {e.Text}").ToList());
        }

        static void Respond(HttpListenerContext ctx, int status, string payload, string contentType)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType + "; charset=utf-8";
                ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }
    }
}
