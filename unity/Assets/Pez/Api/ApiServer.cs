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
            /// <summary>Set by a handler for a big read-only payload: gzipped when the client accepts it, never cached.</summary>
            public bool Bulk;
        }

        /// <summary>A parked /api/wait request: answered at its deadline, or early when a priority alert arrives.</summary>
        class Waiter
        {
            public HttpListenerContext Ctx;
            public World World;
            public int Team;
            public long AlertSince, EventSince;
            public int OrdersSince = -1;
            /// <summary>In a waiting room: the players seated when the wait began (another joining, or the match starting, ends it). -1 otherwise.</summary>
            public int Seated = -1, Ready = -1;
            public Priority Min;
            public bool Interruptible, Json;
            public DateTime Started, Deadline;
        }

        readonly List<Waiter> waiters = new List<Waiter>();

        /// <summary>A parked /api/observe long-poll: answered when something it wakes on arrives (after min_wait), or at its deadline.</summary>
        class ObsWaiter
        {
            public HttpListenerContext Ctx;
            public World World;
            public int Team, Limit;
            public string Stream;
            public long Cursor;
            public ObsFilter Filter;
            public bool Snapshot;
            public DateTime Started, MinUntil, Deadline;
            public long CheckedHead = -1;
        }
        readonly List<ObsWaiter> obsWaiters = new List<ObsWaiter>();

        /// <summary>
        /// One command controller per team: a lease a client claims. While one is live, commands must carry it
        /// (X-Pezz-Controller header or controller= query); read-only observers never need one. Leases lapse after
        /// their TTL without use, and force:true takes over (the feed tells everyone it changed hands).
        /// </summary>
        class Lease { public string Id, Holder; public int Seat; public DateTime Expires; public float Ttl; }
        readonly Dictionary<(World, int), Lease> leases = new Dictionary<(World, int), Lease>();

        Lease LiveLease(World w, int team)
        {
            if (!leases.TryGetValue((w, team), out var l)) return null;
            if (l.Expires < DateTime.UtcNow || l.Seat != w.Teams[team].Seat) { leases.Remove((w, team)); return null; }
            return l;
        }

        static JObj LeaseJson(Lease l) => l == null ? new JObj().Set("held", false)
            : new JObj().Set("held", true).Set("holder", l.Holder).Set("expires_in_s", (float)Math.Round((l.Expires - DateTime.UtcNow).TotalSeconds, 1)).Set("ttl_s", l.Ttl);

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
        /// <summary>Host hook: POST /api/admin/restart started a fresh game (the host drops its saved snapshot).</summary>
        public Action OnRestart;
        /// <summary>Host hook: POST /api/admin/save writes the snapshot now (before a planned restart). Returns the result.</summary>
        public Func<JObj> OnSave;

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
                if (payload != null) Respond(p.Ctx, status, payload, contentType, p.Bulk); // null = parked waiter
            }
            ServiceWaiters(game);
            ServiceObsWaiters(game);
        }

        void ServiceObsWaiters(Game game)
        {
            var now = DateTime.UtcNow;
            for (int i = obsWaiters.Count - 1; i >= 0; i--)
            {
                var o = obsWaiters[i];
                var w = game.World;
                bool done = w != o.World || now >= o.Deadline || o.Team >= w.Teams.Count;
                if (!done)
                {
                    var log = w.Observe.Log(o.Team);
                    if (log.Stream != o.Stream) done = true; // a new seat or game since the poll began: answer with the gap
                    else if (log.Head != o.CheckedHead || now >= o.MinUntil)
                    {
                        o.CheckedHead = log.Head;
                        var (wake, immediate) = log.Wakes(o.Cursor, o.Filter);
                        done = wake && (immediate || now >= o.MinUntil);
                    }
                }
                if (!done) continue;
                obsWaiters.RemoveAt(i);
                if (o.Team >= w.Teams.Count) { Respond(o.Ctx, 410, Json.Write(new JObj().Set("ok", false).Set("error", "that seat no longer exists in this game")), "application/json"); continue; }
                var r = w.Observe.Log(o.Team).Read(o.Stream, o.Cursor, o.Filter, o.Limit, o.Snapshot);
                r.Set("waited_s", (float)Math.Round((now - o.Started).TotalSeconds, 2));
                Respond(o.Ctx, 200, Json.Write(r), "application/json");
            }
        }

        void ServiceWaiters(Game game)
        {
            var now = DateTime.UtcNow;
            for (int i = waiters.Count - 1; i >= 0; i--)
            {
                var wt = waiters[i];
                var w = game.World;
                bool restarted = w != wt.World;
                var fresh = restarted ? new List<Alert>() : w.Alerts.Since(w, wt.Team, wt.AlertSince, wt.Min).ToList();
                // Only news cuts a wait short: a new place or a worse kind of trouble, not the fight it already knows
                // about (which otherwise turns every wait into 0s). And every wait runs at least a second.
                bool news = fresh.Any(a => !w.Alerts.IsContinuation(w, a, wt.AlertSince));
                // New commander's orders also end the wait: the model should read them right away.
                bool newOrders = !restarted && wt.OrdersSince >= 0 && w.Teams[wt.Team].OrdersVersion > wt.OrdersSince;
                // A waiting room has no ticks: a join or the start is the news (time-outs above are on the real clock).
                bool seating = wt.Seated >= 0 && (!game.Waiting || game.OutsidePlayers != wt.Seated || game.OutsideReadyPlayers != wt.Ready);
                bool interrupt = (seating || wt.Interruptible && (news || newOrders)) && (now - wt.Started).TotalSeconds >= 1.0;
                if (!interrupt && !restarted && now < wt.Deadline && !w.GameOver) continue;
                waiters.RemoveAt(i);
                Respond(wt.Ctx, 200, Json.Write(WaitResult(game, wt, interrupt, fresh, now)), "application/json");
            }
        }

        /// <summary>A state with the waiting room's news first (what it's waiting for, and the lone player's choice), if it's waiting.</summary>
        static JObj WithWaiting(Game game, JObj state, bool json)
        {
            if (game.Waiting) state.Insert(0, "waiting", json ? (object)game.WaitingJson() : game.WaitingText());
            return state;
        }

        static JObj WaitResult(Game game, Waiter wt, bool interrupted, List<Alert> fresh, DateTime now)
        {
            var w = game.World;
            return new JObj()
                .Set("interrupted", interrupted)
                .Set("waited_s", (float)Math.Round((now - wt.Started).TotalSeconds, 1))
                .Set("new_alerts", wt.Json ? fresh.OrderByDescending(a => a.Priority).Select(a => (object)StateData.Alert(w, a)).ToList() : StateView.AlertsJson(w, fresh.OrderByDescending(a => a.Priority)))
                .Set("ready_changed", wt.Ready >= 0 && game.OutsideReadyPlayers != wt.Ready)
                .Set("orders_changed", wt.OrdersSince >= 0 && w.Teams.Count > wt.Team && w.Teams[wt.Team].OrdersVersion > wt.OrdersSince)
                .Set("state", w.Teams.Count == 0 ? null // a new match that waits for players starts with no seats
                    : WithWaiting(game, wt.Json ? StateData.Team(w, Math.Min(wt.Team, w.Teams.Count - 1), wt.EventSince) : StateView.TeamState(w, Math.Min(wt.Team, w.Teams.Count - 1), wt.EventSince), wt.Json));
        }

        // ---- Player tokens (open arena). Control tokens act for one team; view tokens only read its fogged view.
        readonly Dictionary<string, (World world, int team, int seat)> controlTokens = new Dictionary<string, (World, int, int)>();
        readonly Dictionary<string, (World world, int team, int seat)> viewTokens = new Dictionary<string, (World, int, int)>();
        /// <summary>Bumped whenever a token is issued, so the host saves soon after a join (a restart mustn't lose a new seat).</summary>
        public int TokensVersion { get; private set; }

        /// <summary>The live tokens for this world, for the game snapshot (a resumed game keeps everyone's tokens).</summary>
        public List<object> SaveTokens(World w)
        {
            var list = new List<object>();
            foreach (var (kind, table) in new[] { ("control", controlTokens), ("view", viewTokens) })
                foreach (var kv in table)
                    if (kv.Value.world == w) list.Add(new JObj().Set("token", kv.Key).Set("kind", kind).Set("team", kv.Value.team).Set("seat", kv.Value.seat));
            return list;
        }

        /// <summary>Re-issue saved tokens for a restored world. Returns how many were restored.</summary>
        public int RestoreTokens(World w, List<object> saved)
        {
            int n = 0;
            if (saved == null) return 0;
            foreach (var o in saved)
            {
                if (!(o is Dictionary<string, object> d)) continue;
                var token = d.Str("token");
                int team = (int)d.Num("team", -1), seat = (int)d.Num("seat", -1);
                if (string.IsNullOrEmpty(token) || team < 0 || team >= w.Teams.Count) continue;
                (d.Str("kind") == "view" ? viewTokens : controlTokens)[token] = (w, team, seat);
                n++;
            }
            TokensVersion++;
            return n;
        }

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
                if (!controlTokens.TryGetValue(tok, out var owner) || owner.world != w || w.Teams[owner.team].Seat != owner.seat) throw new UnauthorizedAccessException("unknown or expired token; join again");
                if (w.Teams[owner.team].Left) throw new UnauthorizedAccessException($"your team is out of this arena (you left, or it was resigned: {World.IdleResignAfter / 60:0} minutes with no command, state or wait call, or no way left to make progress); join again to play");
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
                if (!viewTokens.TryGetValue(vt, out var owner) || owner.world != w || w.Teams[owner.team].Seat != owner.seat) throw new UnauthorizedAccessException("unknown or expired view link");
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
                    return "Pezz RTS control API\n\nGET  /api/rules\nGET  /api/state?team=N[&since=SEQ]\nGET  /api/map?team=N\nGET  /api/alerts?team=N[&since=SEQ&min=medium|high|critical]\nGET  /api/wait?team=N&seconds=S[&since=ALERT_SEQ&events_since=SEQ&min=high|critical|none]  (returns early on a new priority alert)\nPOST /api/command?team=N   body: {\"commands\":[...]} | [...] | {...}\nPOST /api/join?team=N      body: {\"name\":\"Claude\"}\nGET  /api/observe?team=N[&cursor=SEQ&stream=ID&wait=S&min_wait=S&cats=..&exclude=..&kinds=..&min=info|notice|warning|critical&wake=..&wake_kinds=..&detail=compact|full&limit=N]  (one team's fogged observation feed; long-polls with wait)\nGET  /api/observe/snapshot?team=N\nGET|POST /api/controller?team=N  body: {\"action\":\"claim|renew|release\",\"holder\":\"name\",\"ttl_s\":120,\"force\":false}\nGET  /api/status\nGET  /api/view/snapshot[?view=V]  (the world as a saved-game snapshot, no tokens or orders; with a view token, that team's fogged copy)\nPOST /api/admin/restart    body: {\"seed\":1,\"map_size\":112,\"controllers\":[\"llm\",\"llm\"],\"speed\":1,\"match_hours\":4}\nPOST /api/admin/speed      body: {\"speed\":0.5}\nPOST /api/admin/save       (saves the game now; a restarted host resumes it)\nGET  /api/admin/inventions (every agent-invented unit in this game, with its record)\nPOST /api/admin/orders?team=N  body: {\"text\":\"standing orders for that team's commander\"}\n\n" + Commands.Help;
                case "/api/rules":
                    return Json.Write(StateView.Rules());
                case "/api/state":
                    {
                        int team = TeamParam(req, w);
                        w.Teams[team].LastActiveAt = w.Time; // command, state and wait keep a seat (World.CheckIdle)
                        long since = long.TryParse(req.QueryString["since"], out var s) ? s : 0;
                        bool json = req.QueryString["format"] == "json";
                        return Json.Write(WithWaiting(game, json ? StateData.Team(w, team, since) : StateView.TeamState(w, team, since), json));
                    }
                case "/api/alerts":
                    {
                        int team = TeamParam(req, w);
                        long since = long.TryParse(req.QueryString["since"], out var s) ? s : 0;
                        var min = ParsePriority(req.QueryString["min"], Priority.Medium);
                        return Json.Write(new JObj().Set("last_alert_seq", w.Alerts.LastSeq)
                            .Set("orders_version", w.Teams[team].OrdersVersion).Set("standing_orders", w.Teams[team].StandingOrders)
                            .Set("new_alerts", StateView.AlertsJson(w, w.Alerts.Since(w, team, since, min).OrderByDescending(a => a.Priority)))
                            .Set("active", StateView.AlertsJson(w, w.Alerts.Active(w, team))));
                    }
                case "/api/wait":
                    {
                        // Long-poll: let the game run for `seconds`, but come back early if a new alert at or
                        // above `min` (default high) is raised for this team, the way a human hears "base under attack".
                        int team = TeamParam(req, w);
                        w.Teams[team].LastActiveAt = w.Time;
                        float secs = Math.Max(0.5f, Math.Min(60f, float.TryParse(req.QueryString["seconds"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sv) ? sv : 5f));
                        var wt = new Waiter
                        {
                            Ctx = p.Ctx, World = w, Team = team,
                            AlertSince = long.TryParse(req.QueryString["since"], out var a1) ? a1 : w.Alerts.LastSeq,
                            OrdersSince = int.TryParse(req.QueryString["orders_version"], out var ov) ? ov : -1,
                            EventSince = long.TryParse(req.QueryString["events_since"], out var e1) ? e1 : 0,
                            Min = ParsePriority(req.QueryString["min"], Priority.High),
                            Interruptible = req.QueryString["min"] != "none",
                            Json = req.QueryString["format"] == "json",
                            Started = DateTime.UtcNow,
                            Seated = game.Waiting ? game.OutsidePlayers : -1,
                            Ready = game.Waiting ? game.OutsideReadyPlayers : -1,
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
                        var closed = game.ClosedToJoins();
                        if (closed != null)
                        {
                            status = 409;
                            var next = game.NextMatchInS;
                            return Json.Write(new JObj().Set("ok", false).Set("error", "room_closed_to_joins: " + closed).Set("code", "room_closed_to_joins")
                                .Set("next_match_in_s", next.HasValue ? (object)StateView.Secs(next.Value) : null));
                        }
                        var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object>;
                        var name = Text.Name(d?.Str("name"));
                        var team = game.Join("llm", name, d?.Str("team_name"), out var err, out var nameProblem);
                        if (team == null) { status = 409; return Json.Write(new JObj().Set("ok", false).Set("error", err)); }
                        var token = NewToken(); var view = NewToken();
                        controlTokens[token] = (w, team.Id, team.Seat);
                        viewTokens[view] = (w, team.Id, team.Seat);
                        TokensVersion++;
                        Log($"Player joined: {name} as {team.Name} (team {team.Id}); map now {w.Map.W}x{w.Map.H}");
                        var reply = new JObj().Set("ok", true).Set("token", token).Set("view_token", view)
                            .Set("team", team.Id).Set("flavor", team.Name).Set("team_name", team.Name).Put("team_name_rejected", nameProblem).Set("name", name)
                            .Set("base", $"{(int)team.StartPos.X},{(int)team.StartPos.Y} sector {StateView.Sector(team.StartPos)}")
                            .Set("map", $"{w.Map.W}x{w.Map.H}")
                            // Late joiners: how long this match has left, and what comes next.
                            .Set("match", StateView.MatchJson(w)).Set("match_summary", StateView.MatchText(w))
                            .Set("room_status", game.RoomStatus).Set("ready", team.Ready).Set("ready_players", game.OutsideReadyPlayers);
                        if (game.Waiting) reply.Set("waiting", game.WaitingJson());
                        return Json.Write(reply);
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
                    {
                        // What an agent needs to pick a room: its status, whether it can join and why not, a verdict on how far the
                        // match has gone (Game.Verdict), the clock (null while waiting: no match yet), seats, and who's really playing.
                        bool waiting = game.Waiting, clock = !waiting && w.SuddenDeathAt > 0;
                        var why = game.WhyNotJoinable();
                        var scores = w.Scores().ToDictionary(s => s.Team, s => s.Score);
                        var lobby = new JObj()
                            .Set("open", w.Open).Set("rules_version", StateView.RulesVersion).Set("map", $"{w.Map.W}x{w.Map.H}").Set("max_map", w.MaxMapSize).Set("sector_tiles", StateView.SectorTiles)
                            .Set("players", w.ActivePlayers).Set("max_players", w.MaxPlayers).Set("time_s", (float)Math.Round(w.Time, 1))
                            .Set("champion", w.ArenaChampion) // set while one team has cleared the arena (null otherwise)
                            .Set("match", StateView.MatchJson(w))
                            .Set("status", game.RoomStatus).Set("joinable", why == null).Set("why", why)
                            .Set("verdict", game.Verdict()).Set("verdict_text", game.VerdictText())
                            .Set("age_s", waiting ? null : (object)(int)w.Time).Set("match_hours", game.Config.MatchHours)
                            .Set("sudden_death_in_s", clock ? (object)StateView.Secs(w.SuddenDeathAt - w.Time) : null)
                            .Set("ends_in_s", clock ? (object)StateView.Secs(w.MatchEndsAt - w.Time) : null)
                            .Set("seats_free", w.SeatsFree);
                        lobby.Set("ready_players", game.OutsideReadyPlayers);
                        if (waiting) lobby.Set("min_players", game.Config.MinPlayers).Set("waiting_s", StateView.Secs(game.WaitedSeconds))
                            .Set("waiting_ready_s", game.ReadyCountdownSeconds < 0 ? null : (object)StateView.Secs(game.ReadyCountdownSeconds));
                        return Json.Write(lobby
                            .Set("teams", w.Teams.Select(t => new JObj().Set("flavor", t.Name).Set("team_name", t.Name).Set("player", t.PlayerName ?? t.Controller)
                                .Set("ready", t.Ready).Set("status", t.Resigned ? "resigned" : t.Left ? "left" : t.Defeated ? "eliminated" : "playing").Set("house", t.House)
                                .Set("score", scores.TryGetValue(t.Id, out var sc) ? (object)sc : null) // null: not on the scoreboard (left or out)
                                .Set("structures", w.Owned(t.Id).Count(e => e.IsStructure)).Set("kills", t.Stats.Kills)
                                // Game seconds since this seat's player last called command, state or wait (null for house AI, which has no
                                // player); active: still playing and heard from within World.ActiveWithin. CI and the gateway's reaper skip idle seats.
                                .Set("idle_s", t.House ? null : (object)(int)(w.Time - t.LastActiveAt))
                                .Set("active", w.IsActive(t))).ToList()));
                    }
                case "/api/whoami":
                    {
                        int team = TeamParam(req, w);
                        return Json.Write(new JObj().Set("ok", true).Set("team", team).Set("seat", w.Teams[team].Seat).Set("flavor", w.Teams[team].Name));
                    }
                case "/api/view/whoami":
                    {
                        // Which team a view link belongs to (the gateway uses this to pick the right live stream).
                        int team = ViewTeam(req, w);
                        if (team < 0) { status = 400; return Json.Write(new JObj().Set("ok", false).Set("error", "a view token is required")); }
                        return Json.Write(new JObj().Set("ok", true).Set("team", team).Set("seat", w.Teams[team].Seat).Set("flavor", w.Teams[team].Name));
                    }
                case "/api/view/map":
                    return Json.Write(ViewMap(w, ViewTeam(req, w)));
                case "/api/view/frame":
                    return Json.Write(ViewFrame(w, ViewTeam(req, w)));
                case "/api/view/snapshot":
                    {
                        // The whole world for a client that renders it itself (the browser build). Without a view token it
                        // has no fog, like team=-1 frames, so the gateway serves that delayed (/watch/snapshot) and never raw.
                        // A view token (?view=) gets that team's fogged copy instead, live: the gateway's /view/<token>/snapshot.
                        p.Bulk = true;
                        return Json.Write(ViewSnapshot(game, ViewTeam(req, w)));
                    }
                case "/api/viewlink":
                    {
                        // A player's own read-only watch-link token (for links made after joining, e.g. the commander link).
                        int team = TeamParam(req, w);
                        var vt = viewTokens.FirstOrDefault(kv => kv.Value.world == w && kv.Value.team == team && kv.Value.seat == w.Teams[team].Seat).Key;
                        if (vt == null) { status = 404; return Json.Write(new JObj().Set("ok", false).Set("error", "no view link for this seat")); }
                        return Json.Write(new JObj().Set("ok", true).Set("view_token", vt));
                    }
                case "/api/view/unit":
                    {
                        // Details for one entity, as the viewing team knows it: everything about its own, what can be
                        // seen about a visible enemy, nothing about anything hidden in fog.
                        int team = ViewTeam(req, w);
                        var e = w.Get(int.TryParse(req.QueryString["id"], out var uid) ? uid : 0);
                        bool mine = e != null && e.Team == team;
                        if (e == null || !(mine || team < 0 || w.IsVisibleTo(team, e))) { status = 404; return Json.Write(new JObj().Set("ok", false).Set("error", "not visible")); }
                        return Json.Write(UnitDetails(w, e, mine || team < 0));
                    }
                case "/api/join":
                    {
                        int team = TeamParam(req, w);
                        var d = p.Body != null ? Json.Parse(p.Body) as Dictionary<string, object> : null;
                        var name = Text.Name(d?.Str("name") ?? "LLM");
                        w.Teams[team].PlayerName = name;
                        var nameProblem = d?.Str("team_name") != null ? w.NameTeam(w.Teams[team], d.Str("team_name")) : null; // the seat keeps its name unless the player picks one
                        if (w.Teams[team].Controller != "human") w.Teams[team].Controller = "llm";
                        w.Teams[team].LastActiveAt = w.Time; // a seat taken over now isn't already idle
                        w.Emit("chat", team, text: $"{name} has taken command of {w.Teams[team].Name}.");
                        return Json.Write(new JObj().Set("ok", true).Set("team", team).Set("name", w.Teams[team].Name).Set("team_name", w.Teams[team].Name).Put("team_name_rejected", nameProblem));
                    }
                case "/api/command":
                    {
                        if (method != "POST") { status = 405; return "{\"ok\":false,\"error\":\"POST required\"}"; }
                        int team = TeamParam(req, w);
                        var lease = LiveLease(w, team);
                        var given = req.Headers["X-Pezz-Controller"] ?? req.QueryString["controller"];
                        if (lease != null && given != lease.Id)
                        {
                            status = 409;
                            return Json.Write(new JObj().Set("ok", false).Set("code", "controller_conflict")
                                .Set("error", $"another client ({lease.Holder}) is this team's command controller; send its lease as X-Pezz-Controller, wait for it to lapse, or claim with force:true at /api/controller")
                                .Set("controller", LeaseJson(lease)));
                        }
                        if (lease != null) lease.Expires = DateTime.UtcNow.AddSeconds(lease.Ttl);
                        w.Teams[team].LastActiveAt = w.Time;
                        var parsed = Json.Parse(p.Body ?? "[]");
                        return Json.Write(CommandSafety.ExecuteBatch(game, team, parsed,
                            lease?.Holder ?? w.Teams[team].PlayerName ?? w.Teams[team].Controller, OnCommand));
                    }
                case "/api/observe":
                case "/api/observe/snapshot":
                    {
                        // One team's observation feed (Observations.cs). A control token or a view token: the feed is that
                        // team's own fogged view, so there is no spectator (all-seeing) feed. Readers keep their own cursors.
                        int team = ViewTeam(req, w);
                        if (team < 0) { status = 400; return Json.Write(new JObj().Set("ok", false).Set("error", "observe is one team's view: send a player token, a view token (view=) or team=N")); }
                        if (TokenFrom(req) != null) w.Teams[team].LastActiveAt = w.Time; // a player observing is playing
                        var log = w.Observe.Log(team);
                        if (path == "/api/observe/snapshot") return Json.Write(new JObj().Set("ok", true).Set("stream", log.Stream).Set("cursor", log.Head).Set("snapshot", log.Snapshot()));
                        var q = req.QueryString;
                        float F(string k, float def, float lo, float hi) => Math.Max(lo, Math.Min(hi, float.TryParse(q[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def));
                        long cursor = long.TryParse(q["cursor"], out var cv) ? cv : -1;
                        var ow = new ObsWaiter
                        {
                            Ctx = p.Ctx, World = w, Team = team, Stream = q["stream"], Cursor = cursor,
                            Filter = ObsFilter.Parse(k => q[k]), Limit = (int)F("limit", 500, 1, 2000), Snapshot = q["snapshot"] != "0",
                            Started = DateTime.UtcNow,
                        };
                        float wait = F("wait", 0, 0, 60);
                        ow.MinUntil = ow.Started.AddSeconds(F("min_wait", 0, 0, 60));
                        ow.Deadline = ow.Started.AddSeconds(Math.Max(wait, (float)(ow.MinUntil - ow.Started).TotalSeconds));
                        // Answer now: no wait asked, a fresh start, or a cursor that isn't on this stream (the gap is news).
                        bool gap = cursor < 0 || (ow.Stream != null && ow.Stream != log.Stream) || cursor > log.Head || cursor < log.First - 1;
                        if (wait <= 0 || gap || (log.Wakes(cursor, ow.Filter) is var wk && wk.wake && (wk.now || ow.MinUntil <= ow.Started)))
                            return Json.Write(log.Read(ow.Stream, cursor, ow.Filter, ow.Limit, ow.Snapshot).Set("waited_s", 0));
                        if (ow.Stream == null) ow.Stream = log.Stream;
                        obsWaiters.Add(ow);
                        return null;
                    }
                case "/api/controller":
                    {
                        // GET: who holds this team's command lease. POST {"action":"claim"|"renew"|"release","holder":"name","ttl_s":120,"force":false,"lease":"..."}.
                        int team = TeamParam(req, w);
                        var lease = LiveLease(w, team);
                        if (method != "POST") return Json.Write(new JObj().Set("ok", true).Set("controller", LeaseJson(lease)));
                        var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object> ?? new Dictionary<string, object>();
                        var action = d.Str("action", "claim");
                        var mine = d.Str("lease") ?? req.Headers["X-Pezz-Controller"];
                        float ttl = Math.Max(10f, Math.Min(3600f, d.Num("ttl_s", 120)));
                        var holder = Text.Clean(d.Str("holder", "controller") ?? "controller", 40);
                        if (action == "release")
                        {
                            if (lease != null && lease.Id == mine) { leases.Remove((w, team)); w.Observe.Note(team, "match", "controller_changed", Sev.Notice, "server", new JObj().Set("from", lease.Holder).Set("to", null), $"{lease.Holder} released command of this team"); }
                            return Json.Write(new JObj().Set("ok", true).Set("controller", LeaseJson(LiveLease(w, team))));
                        }
                        if (action == "renew")
                        {
                            if (lease == null || lease.Id != mine) { status = 409; return Json.Write(new JObj().Set("ok", false).Set("code", "controller_conflict").Set("error", "that lease isn't live; claim again").Set("controller", LeaseJson(lease))); }
                            lease.Expires = DateTime.UtcNow.AddSeconds(lease.Ttl);
                            return Json.Write(new JObj().Set("ok", true).Set("lease", lease.Id).Set("controller", LeaseJson(lease)));
                        }
                        bool force = d.TryGetValue("force", out var fo) && fo is bool fb && fb;
                        if (lease != null && lease.Id != mine && !force)
                        {
                            status = 409;
                            return Json.Write(new JObj().Set("ok", false).Set("code", "controller_conflict").Set("error", $"{lease.Holder} holds command of this team; force:true takes over").Set("controller", LeaseJson(lease)));
                        }
                        var nl = new Lease { Id = lease != null && lease.Id == mine ? lease.Id : NewToken().Substring(0, 24), Holder = holder, Seat = w.Teams[team].Seat, Ttl = ttl, Expires = DateTime.UtcNow.AddSeconds(ttl) };
                        leases[(w, team)] = nl;
                        if (lease == null || lease.Id != nl.Id)
                            w.Observe.Note(team, "match", "controller_changed", Sev.Warning, "server", new JObj().Set("from", lease?.Holder).Set("to", holder).Set("forced", lease != null),
                                lease != null ? $"{holder} took command of this team from {lease.Holder}" : $"{holder} is now this team's command controller");
                        return Json.Write(new JObj().Set("ok", true).Set("lease", nl.Id).Set("controller", LeaseJson(nl))
                            .Set("note", "Send the lease as the X-Pezz-Controller header on every command. Commands renew it; it lapses after ttl_s without one."));
                    }
                case "/api/admin/observe":
                    {
                        // Host: the feed's sampling cadence, heartbeat and retention for this game.
                        var f = w.Observe;
                        if (method == "POST")
                        {
                            var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object>;
                            if (d != null)
                            {
                                if (!float.IsNaN(d.Num("interval_s"))) f.IntervalS = Math.Max(0.25f, Math.Min(10f, d.Num("interval_s")));
                                if (!float.IsNaN(d.Num("heartbeat_s"))) f.HeartbeatS = Math.Max(1f, Math.Min(300f, d.Num("heartbeat_s")));
                                if (!float.IsNaN(d.Num("retention"))) f.Retention = (int)Math.Max(50, Math.Min(100000, d.Num("retention")));
                            }
                        }
                        return Json.Write(new JObj().Set("ok", true).Set("interval_s", f.IntervalS).Set("heartbeat_s", f.HeartbeatS).Set("retention", f.Retention));
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
                            OreScale = d?.Num("ore_scale", game.Config.OreScale) ?? game.Config.OreScale,
                            MaxPlayers = game.Config.MaxPlayers, MaxMapSize = game.Config.MaxMapSize,
                            HouseAIs = game.Config.HouseAIs, HouseResignAbove = game.Config.HouseResignAbove,
                            KitchenSink = game.Config.KitchenSink, // the showcase room restarts as the showcase
                            MatchHours = d?.Num("match_hours", game.Config.MatchHours) ?? game.Config.MatchHours,
                            MinPlayers = (int)(d?.Num("min_players", game.Config.MinPlayers) ?? game.Config.MinPlayers),
                        };
                        if (d != null && d.TryGetValue("controllers", out var cs) && cs is List<object> cl2) cfg.Controllers = cl2.Select(x => x.ToString()).ToArray();
                        else cfg.Controllers = game.Config.Controllers;
                        if (d != null && d.TryGetValue("orders", out var os) && os is List<object> ol) cfg.Orders = ol.Select(x => x?.ToString() ?? "").ToArray();
                        else cfg.Orders = game.Config.Orders;
                        game.Restart(cfg);
                        OnRestart?.Invoke();
                        return Json.Write(new JObj().Set("ok", true).Set("seed", cfg.Seed).Set("map_size", game.World.Map.W).Set("ore_scale", cfg.OreScale).Set("controllers", cfg.Controllers.ToList()));
                    }
                case "/api/admin/showcase":
                    // The kitchen sink room's catalog (districts, exhibits, camera targets, legend) for its viewer page.
                    if (w.Showcase == null) { status = 404; return "{\"ok\":false,\"error\":\"not the kitchen sink room\"}"; }
                    return Json.Write(w.Showcase.Catalog());
                case "/api/admin/inventions":
                    // Host-only: every invention in this game with its inventor and record, for the gateway's central
                    // registry (designs evaluated later for permanent inclusion). Not a player endpoint.
                    return Json.Write(Tech.RegistryJson(w));
                case "/api/admin/save":
                    {
                        // Host-only: save the game now (e.g. right before stopping the host for a new build).
                        if (OnSave == null) { status = 501; return "{\"ok\":false,\"error\":\"this host doesn't save games\"}"; }
                        var r = OnSave();
                        if (!(r["ok"] is bool sok && sok)) status = 500;
                        return Json.Write(r);
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
                        return Json.Write(new JObj().Set("ok", true).Set("team", team).Set("standing_orders", w.Teams[team].StandingOrders).Set("orders_version", w.Teams[team].OrdersVersion).Set("plan_version", w.Teams[team].PlanVersion));
                    }
                case "/api/admin/speed":
                    {
                        var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object>;
                        game.Speed = Math.Max(0.1f, Math.Min(8f, d.Num("speed", 1)));
                        if (d.TryGetValue("paused", out var pz) && pz is bool pb) game.Paused = pb;
                        return Json.Write(new JObj().Set("ok", true).Set("speed", game.Speed).Set("paused", game.Paused));
                    }
                case "/api/admin/announce":
                    {
                        // Host-only: a line in the arena chat for everyone in this game (e.g. "new capability").
                        var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object>;
                        var text = Text.Clean(d?.Str("text", "") ?? "", 300);
                        if (text.Length == 0) { status = 400; return Json.Write(new JObj().Set("ok", false).Set("error", "text is empty")); }
                        w.Emit("chat", -1, text: text);
                        return Json.Write(new JObj().Set("ok", true));
                    }
                case "/api/admin/viewlink":
                    {
                        // Host-only: a read-only view token for any seat (the sessions dashboard's "watch any player"). Seats
                        // that joined without one (the host's own llm seats) get one minted; it's saved with the others.
                        int team = TeamParam(req, w);
                        var vt = viewTokens.FirstOrDefault(kv => kv.Value.world == w && kv.Value.team == team && kv.Value.seat == w.Teams[team].Seat).Key;
                        if (vt == null) { vt = NewToken(); viewTokens[vt] = (w, team, w.Teams[team].Seat); TokensVersion++; }
                        return Json.Write(new JObj().Set("ok", true).Set("team", team).Set("view_token", vt));
                    }
                case "/api/admin/kick":
                    {
                        // Operator-only (the gateway forwards /api/admin only from /admin, with the operator secret): remove a seat as if it had left.
                        var d = Json.Parse(p.Body ?? "{}") as Dictionary<string, object>;
                        int team = (int)d.Num("team", -1);
                        if (team < 0 || team >= w.Teams.Count || w.Teams[team].Left) { status = 400; return Json.Write(new JObj().Set("ok", false).Set("error", "no such active team")); }
                        return Json.Write(new JObj().Set("ok", true).Set("result", w.Leave(team)));
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
            return new JObj().Set("w", m.W).Set("h", m.H).Set("version", w.MapVersion).Set("ore_version", w.OreVersion).Set("tiles", tiles.ToString()).Set("ore", ore.ToString())
                .Set("sector_tiles", StateView.SectorTiles).Set("team", team).Set("flavor", team >= 0 ? w.Teams[team].Name : "Spectator");
        }

        static JObj UnitDetails(World w, Entity e, bool full)
        {
            var t = e.Team >= 0 ? w.Teams[e.Team] : null; // null: neutral (an unclaimed derrick)
            var d = e.Def;
            var o = new JObj().Set("ok", true).Set("id", e.Id).Set("type", d.Key).Set("name", d.Name).Set("team", e.Team)
                .Set("flavor", t?.Name ?? "Neutral").Set("player", t == null ? "nobody: any engineer can capture it" : t.PlayerName ?? t.Controller)
                .Set("hp", (int)e.Hp).Set("max_hp", d.MaxHp).Set("armor", d.Armor.ToString().ToLowerInvariant())
                .Set("structure", e.IsStructure).Set("air", e.IsAir).Set("description", d.Description);
            if (!e.IsStructure) o.Set("speed", d.Speed);
            if (d.Weapon != null) o.Set("weapon", $"{d.Weapon.Name}: {d.Weapon.Damage} dmg, range {d.Weapon.Range}, every {d.Weapon.Cooldown}s");
            if (d.OwnerTeam >= 0) o.Set("invention", new JObj().Set("base", d.Chassis).Set("spec", Tech.Spec(d)).Set("designed_by", w.Teams[d.OwnerTeam].Name));
            if (!full) return o;
            if (e.IsStructure)
            {
                o.Set("complete", e.IsComplete).Set("progress_pct", StateView.Pct(e.BuildProgress));
                if (d.Recipes.Length > 0) o.Set("working", e.Working);
                if (d.Key == "deep_mine") { var dep = w.Map.DepositById(e.DepositId); if (dep != null) o.Set("deposit", $"{Defs.Ores[dep.Type]}: {(int)dep.Amount}/{(int)dep.Initial} left"); }
                if (e.Rally.HasValue) o.Set("rally", StateView.Sector(e.Rally.Value));
                return o;
            }
            o.Set("order", e.OrderName).Set("x", Math.Round(e.Pos.X, 1)).Set("y", Math.Round(e.Pos.Y, 1)).Set("sector", StateView.Sector(e.Pos));
            if (e.Order != Order.Idle) o.Set("order_sector", StateView.Sector(e.OrderPos));
            var target = e.TargetId != 0 ? w.Get(e.TargetId) : null;
            if (target != null && e.Order != Order.Idle) o.Set("target", $"{target.Def.Name} #{target.Id}");
            if (d.UsesFuel) o.Set("fuel_pct", StateView.Pct(e.FuelFraction)).Set("landed", e.Landed).Set("stranded", e.Stranded);
            if (e.DockName != null) o.Set("dock", e.DockName);
            if (e.IsHarvester) o.Set("cargo", $"{e.Cargo}/{d.HarvestCapacity}{(e.CargoType >= 0 ? " " + Defs.Ores[e.CargoType] : "")}");
            if (d.Capacity > 0) o.Set("passengers", $"{e.Passengers.Count}/{d.Capacity}");
            if (e.Waypoints.Count > 0) o.Set("waypoints", e.Waypoints.Count).Set("patrol", e.WaypointLoop);
            if (e.RetreatBelow > 0) o.Set("retreat_below_pct", StateView.Pct(e.RetreatBelow)).Set("retreating", e.Retreating);
            if (StateView.SurveyStatus(w, e) is JObj survey) o.Set("survey", survey);
            if (e.IsCarried) o.Set("inside", e.CarrierId);
            return o;
        }

        /// <summary>
        /// The saved-game snapshot for a client that renders it itself, minus what the viewer must not read: never the API
        /// section (tokens) or anyone else's orders. A spectator (team -1) gets the whole world, so it's served delayed. A
        /// player's team gets ViewFrame's fog rules: its own team in full; rivals cut to what's public (name, seat, status,
        /// kills; not where their base is); enemy units and buildings only while it sees them (buildings also once seen), with their plans cut to the
        /// next few steps of their path; events, shots and alerts only where it can see. Nothing here starts a rival's AI or
        /// a house seat in the client's local copy: that would be a guess, corrected by the next snapshot anyway.
        /// </summary>
        public static JObj ViewSnapshot(Game game, int team = -1)
        {
            var w = game.World;
            var snap = Snapshot.Write(game); // the host's `api` (tokens) section is added by RoomSaver, never here
            snap.Remove("api");
            if (snap["config"] is JObj cfg && cfg["orders"] is List<object> orders)
                cfg["orders"] = orders.Select((o, i) => (object)(i == team ? o : "")).ToList();
            if (snap["game"] is JObj g)
            {
                g["next_house_check"] = 1e9; // the host seats house AIs; the client's copy waits for its snapshot to say so
                if (team >= 0 && g["ais"] is List<object> ais) g["ais"] = ais.OfType<JObj>().Where(a => Num(a["team"]) == team).Cast<object>().ToList();
            }
            if (!(snap["world"] is JObj world)) return snap;
            if (world["teams"] is List<object> teams)
                foreach (var t in teams.OfType<JObj>())
                {
                    int id = (int)Num(t["id"]);
                    t.Remove("command_receipts"); // retry receipts belong to the server, never a rendered mirror
                    t.Remove("gone_units");
                    if (id == team) continue;
                    t.Remove("standing_orders"); // a commander's orders are the team's plan
                    if (team < 0) continue;
                    foreach (var k in new[] { "stock", "granted", "rates", "power_produced", "power_used", "visible", "explored", "detected", "revealed",
                                              "structure_queue", "unit_queues", "known_enemy_structures", "surveyed", "survey_sites", "reserve",
                                              "save_for", "save_for_cost", "zones", "stalled_since", "surface_warned_at", "home_ore_warned", "last_active_at", "orders_version", "plasma_owed" })
                        t.Remove(k);
                    t.Remove("miner_policy"); // only this team's explicit policy executes in its fogged mirror
                    t.Remove("squads"); // rival membership and intentions belong to its commander
                    t.Remove("objectives");
                    t.Remove("strategy_text");
                    t.Remove("priorities");
                    t.Remove("plan_version");
                    if (t["stats"] is JObj st) t["stats"] = new JObj().Set("kills", st["kills"] ?? 0);
                    // Where a rival's base is, is for scouting: their start goes far off the map (not 0,0, a real corner, where
                    // the client's copy would then think a protected rival's reserved ore lay).
                    t["start"] = SnapIO.V(new Vec2(HiddenStart, HiddenStart));
                }
            if (world["events"] is List<object> events)
            {
                foreach (var e in events.OfType<JObj>()) if ((e["type"] as string) == "orders" && (team < 0 || Num(e["team"] ?? -1) != team)) e.Remove("text"); // World.SetOrders logs the text as an event
                if (team >= 0)
                    world["events"] = events.OfType<JObj>().Where(e => PublicEvent(e["type"] as string) || Num(e["team"] ?? -1) == team || (!OwnerEvent(e["type"] as string) && Sees(w, team, e["pos"]))).Cast<object>().ToList();
            }
            // Orders given lately, which the client's copy gives again at their ticks (Game.Replay) so it moves as the game did
            // rather than sliding to it at the next snapshot. A player's copy gets only its own team's.
            world["commands"] = w.CommandLog.Where(c => team < 0 || c.Team == team)
                .Select(c => (object)new List<object> { c.Seq, c.Tick, c.Team, Exact(c.Cmd) }).ToList();
            if (team < 0) return snap;
            var t0 = w.Teams[team];
            // The scoreboard counts everyone's territory, ore and kills, which the fog hides: send the server's own count.
            world["mirror_scores"] = w.Scores().Select(s => (object)new JObj().Set("team", s.Team).Set("name", s.Name).Set("player", s.Player)
                .Set("territory", s.Territory).Set("ore_mined", s.OreMined).Set("kill_value", s.KillValue).Set("territory_points", s.TerritoryPoints)
                .Set("economy_points", s.EconomyPoints).Set("kill_points", s.KillPoints).Set("score", s.Score)).ToList();
            if (world["entities"] is List<object> ents)
            {
                var keep = new List<object>();
                foreach (var eo in ents.OfType<JObj>())
                {
                    var e = w.Get((int)Num(eo["id"]));
                    if (e == null || e.Team == team) { keep.Add(eo); continue; }
                    if (e.IsCarried || !(w.IsVisibleTo(team, e) || (e.IsStructure && t0.KnownEnemyStructures.ContainsKey(e.Id)))) continue;
                    // Where it's going is its owner's business: keep the next few steps so it moves on smoothly until the next snapshot.
                    if (eo["path"] is List<object> path)
                    {
                        int at = (int)Num(eo["path_idx"] ?? 0);
                        var next = path.Skip(at).Take(4).ToList();
                        eo["path"] = next; eo["path_idx"] = 0;
                        if (next.Count > 0) eo["order_pos"] = next[next.Count - 1];
                    }
                    else eo.Remove("order_pos");
                    foreach (var k in new[] { "objective_id", "guard_motion_paused", "last_order_by", "last_order_tick", "last_retreat_tick", "rally_squad", "rally", "waypoints", "waypoint_loop", "resume_order", "resume_pos", "resume_guard", "resume_target", "resume_waypoints",
                                              "mine_queue", "home_pos", "harvest_tile", "prospect_center", "prospect_radius", "skip_sites", "passengers", "retreat_below", "fuel", "fuel_cap", "until_capturable", "engage_derricks", "output", "granted_share", "cargo_salvage" })
                        eo.Remove(k);
                    eo["guard_pos"] = eo["pos"];
                    keep.Add(eo);
                }
                world["entities"] = keep;
            }
            if (world["projectiles"] is List<object> shots)
                world["projectiles"] = shots.OfType<JObj>().Where(p => Num(p["team"]) == team || Sees(w, team, p["pos"])).Cast<object>().ToList();
            if (world["alerts"] is JObj al && al["alerts"] is List<object> alerts)
                al["alerts"] = alerts.OfType<JObj>().Where(a => Num(a["team"]) == team).Cast<object>().ToList();
            if (world["rate_snapshot"] is List<object> rates)
                world["rate_snapshot"] = rates.OfType<JObj>().Where(r => Num(r["team"]) == team).Cast<object>().ToList();
            if (world["air_warned"] is List<object> aw)
                world["air_warned"] = aw.OfType<List<object>>().Where(x => x.Count > 0 && Num(x[0]) == team).Cast<object>().ToList();
            if (world["derrick_hinted"] is List<object> dh) // which derricks a rival's units stopped beside is theirs to know
                world["derrick_hinted"] = dh.OfType<List<object>>().Where(x => x.Count > 0 && Num(x[0]) == team).Cast<object>().ToList();
            // Deep deposits are found by surveying: a player's copy only has the ones it surveyed.
            if (world["map"] is JObj map && map["deep"] is List<object> deep)
                map["deep"] = deep.OfType<JObj>().Where(d => t0.Surveyed.Contains((int)Num(d["id"]))).Cast<object>().ToList();
            return snap;
        }

        /// <summary>A command as given, numbers in full (Json.Write rounds to 2 places): the copy must give the same order.</summary>
        static object Exact(object v) => v switch
        {
            double d => new JExact(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
            Dictionary<string, object> m => m.ToDictionary(kv => kv.Key, kv => Exact(kv.Value)),
            List<object> l => l.Select(Exact).ToList(),
            _ => v,
        };

        /// <summary>A rival's base position in a player's snapshot: hidden, far off any map.</summary>
        public const float HiddenStart = -10000f;

        static double Num(object o) => o is double d ? d : o is float f ? f : o is int i ? i : o is long l ? l : o is IConvertible c ? c.ToDouble(System.Globalization.CultureInfo.InvariantCulture) : 0;

        /// <summary>Events everyone hears about (the scoreboard and chat), wherever they happen.</summary>
        /// <summary>
        /// Events that only tell their own team about its economy or its units' state (power totals, upkeep, research, deep ore,
        /// fuel): a rival's never reaches a player's copy, even when the spot it names is in sight. These are the types
        /// StateView.EventList shows only to the team they belong to.
        /// </summary>
        static bool OwnerEvent(string type) => (type != null && type.StartsWith("objective_", StringComparison.Ordinal)) || type is "miners_withdrawn" or "miners_resumed" or "guard_triggered" or "guard_released" or "detached_by_order" or "decision_needed" or "low_power" or "power_restored" or "defences_offline" or "defences_online" or "research_started" or "researched"
            or "unstalled" or "surveyed" or "survey_failed" or "drilled" or "drill_failed" or "depleted" or "low_fuel" or "stranded" or "refuelled" or "retreating"
            or "out_of_range" or "capture_stopped" or "derrick_skipped";

        static bool PublicEvent(string type) => type == "chat" || type == "defeated" || type == "game_over" || type == "joined" || type == "left" || type == "resigned" || type == "match";

        static bool Sees(World w, int team, object pos)
        {
            if (!(pos is List<object> p) || p.Count < 2) return false;
            var m = w.Map;
            int x = Math.Clamp((int)Num(p[0]), 0, m.W - 1), y = Math.Clamp((int)Num(p[1]), 0, m.H - 1);
            return w.Teams[team].Visible[m.Idx(x, y)];
        }

        /// <summary>
        /// A unit queue as the viewer's production list: a row for each unit a building is working on (each barracks has its own),
        /// or the head of a queue nothing is working on. The first row of a unit says how many of it are queued.
        /// </summary>
        static List<object> UnitProduction(Team t, Producer kind)
        {
            var q = t.UnitQueues[kind];
            var now = t.Working(kind);
            if (now.Count == 0 && q.Count > 0) now.Add(q[0]);
            var counted = new HashSet<string>();
            return now.Select(p => (object)new JObj().Set("type", p.Key).Set("count", counted.Add(p.Key) ? q.Count(x => x.Key == p.Key) : 1)
                .Set("pct", (int)(p.Progress / Defs.Get(p.Key).BuildTime * 100)).Set("building", p.WorkerId != 0 ? (object)p.WorkerId : null)).ToList();
        }

        internal static JObj ViewFrame(World w, int team)
        {
            JObj Planning(Team t) => new JObj().Set("plan_version", t.PlanVersion)
                .Set("strategy", new JObj().Set("text", t.StrategyText).Set("priorities", t.Priorities.Cast<object>().ToList()))
                .Set("objectives", Objectives.View(w, t.Id))
                .Set("interventions", t.Squads.Values.SelectMany(s => s.Guards.Notes).Select(n => Json.Parse(n))
                    .OfType<Dictionary<string, object>>().OrderByDescending(n => n.Num("t", 0)).Take(8).Cast<object>().ToList());
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
                // An invention is drawn as its base unit (ModelKey); its player-chosen name rides along at index 10. A structure's
                // facing (index 6) is where its door faces: its bay for a drop-off, else south, where the models' doors are.
                var row = new List<object> { e.Id, e.Def.OwnerTeam >= 0 ? e.Def.ModelKey : e.Def.Key, e.Team, Math.Round(c.X, 2), Math.Round(c.Y, 2), (int)(100 * e.Hp / e.Def.MaxHp),
                    Math.Round(e.IsStructure ? w.DoorFacing(e) : e.Facing, 2), e.IsStructure ? StateView.Pct(e.BuildProgress) : -1, e.IsStructure ? e.Def.SizeX : 0,
                    // fuel %: your own units only (spectators see all); -1 = not shown
                    e.Def.UsesFuel && (team < 0 || e.Team == team) ? StateView.Pct(e.FuelFraction) : -1 };
                if (e.Def.OwnerTeam >= 0) row.Add(e.Def.Name);
                ents.Add(row);
            }
            var shots = new List<object>();
            // Tracers matter for an instant; blasts, deaths, boarding and salvage stay listed long enough that a late poll
            // (background tab, slow link, the 0.5 s spectator buffer) still sees them. The viewer dedupes by seq.
            for (int i = w.Events.Count - 1; i >= 0; i--)
            {
                var ev = w.Events[i];
                int age = w.Tick - ev.Tick;
                if (age > 3 * World.TickRate) break;
                int keep = ev.Type == "shot" || ev.Type == "fire" ? 10 : ev.Type == "hit" ? 20
                         : ev.Type == "destroyed" || ev.Type == "boarded" || ev.Type == "salvaged" ? 3 * World.TickRate : -1;
                if (age > keep) continue;
                if (team >= 0 && ev.Team != team && !w.Teams[team].Visible[m.Idx(Math.Clamp((int)ev.Pos.X, 0, m.W - 1), Math.Clamp((int)ev.Pos.Y, 0, m.H - 1))]) continue;
                shots.Add(new List<object> { ev.Seq, ev.Type, Math.Round(ev.Pos.X, 1), Math.Round(ev.Pos.Y, 1), Math.Round(ev.Pos2.X, 1), Math.Round(ev.Pos2.Y, 1), ev.Team, ev.A });
            }
            shots.Reverse();
            // ore_version: new salvage or regrowth on bare ground; the web viewer fetches the map again (replays keep to version).
            var o = new JObj().Set("tick", w.Tick).Set("time_s", (float)Math.Round(w.Time, 1)).Set("version", w.MapVersion).Set("ore_version", w.OreVersion)
                .Set("teams", w.Teams.Select(t => new JObj().Set("id", t.Id).Set("flavor", t.Name).Set("player", t.PlayerName ?? t.Controller)
                    .Set("status", t.Resigned ? "resigned" : t.Left ? "left" : t.Defeated ? "eliminated" : "playing").Set("kills", t.Stats.Kills)
                    // Spectators (delayed) see everyone's size; a player only sees their own (in "you").
                    .Set("units", team < 0 ? (object)w.Entities.Count(e => !e.Dead && e.Team == t.Id && !e.IsStructure && !e.IsMine) : null)
                    .Set("structures", team < 0 ? (object)w.Entities.Count(e => !e.Dead && e.Team == t.Id && e.IsStructure) : null)
                    .Set("power", team < 0 ? $"{t.PowerUsed}/{t.PowerProduced}" : null)
                    .Set("steel", team < 0 ? (object)t.Amount("steel") : null)
                    .Set("planning", team < 0 ? Planning(t) : null)).ToList())
                .Set("entities", ents).Set("effects", shots)
                .Set("chat", w.Events.Where(e => e.Type == "chat").Reverse().Take(8).Reverse()
                    .Select(e => $"{(e.Team >= 0 ? w.Teams[e.Team].Name : "arena")}: {e.Text}").ToList());
            if (shroud != null) o.Set("shroud", shroud);
            o.Set("match", StateView.MatchJson(w)); // the clock, the scoreboard, and after the end the results
            // Deep deposits: a player sees the ones their surveyors found; spectators see all. [x, y, type, % left]
            o.Set("deposits", w.Map.Deep.Where(d => team < 0 || w.Teams[team].Surveyed.Contains(d.Id))
                .Select(d => (object)new List<object> { Math.Round(d.Pos.X, 1), Math.Round(d.Pos.Y, 1), d.Type, d.Initial > 0 ? (int)(100 * d.Amount / d.Initial) : 0 }).ToList());
            if (team >= 0)
            {
                var t = w.Teams[team];
                o.Set("you", new JObj().Set("team", team).Set("flavor", t.Name).Set("player", t.PlayerName).Set("stockpile", StateView.Stockpile(t))
                    .Set("planning", Planning(t))
                    .Set("power", $"{t.PowerUsed}/{t.PowerProduced}").Set("status", t.Resigned ? "resigned" : t.Left ? "left" : t.Defeated ? "eliminated" : "playing")
                    .Set("power_used", t.PowerUsed).Set("power_produced", t.PowerProduced).Set("steel", t.Amount("steel"))
                    .Set("units", w.Entities.Count(e => !e.Dead && e.Team == team && !e.IsStructure && !e.IsMine))
                    .Set("structures", w.Entities.Count(e => !e.Dead && e.Team == team && e.IsStructure))
                    .Set("kills", t.Stats.Kills).Set("protected_for_s", w.IsProtected(team) ? (int)(t.ProtectedUntil - w.Time) : 0)
                    .Set("production", t.StructureQueue.Select(p => { var s0 = w.Get(p.StructureId); return (object)new JObj().Set("type", p.Key).Set("pct", (int)((s0?.BuildProgress ?? 0) * 100)); })
                        .Concat(t.UnitQueues.Where(kv => kv.Value.Count > 0).SelectMany(kv => UnitProduction(t, kv.Key))).ToList()));
                o.Set("alerts", w.Alerts.Active(w, team).Take(4).Select(a => AlertLog.Describe(w, a)).ToList());
                o.Set("alert_items", w.Alerts.Active(w, team).Take(4).Select(a => (object)new JObj().Set("priority", a.Priority.ToString().ToLowerInvariant())
                    .Set("label", AlertLog.Label(a.Kind)).Set("sector", StateView.Sector(a.Pos)).Set("x", (int)a.Pos.X).Set("y", (int)a.Pos.Y)).ToList());
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
                .Set("match", StateView.MatchJson(w))
                .Set("resumed_from", game.ResumedFrom).Set("last_saved", game.LastSaved)
                .Set("sim_errors", w.Errors).Set("last_sim_error", w.LastError)
                .Set("movement_settles", w.SettleCounts.Values.Sum()).Set("movement_settles_by", w.SettleCounts.OrderByDescending(kv => kv.Value).Take(8).Aggregate(new JObj(), (j, kv) => j.Set(kv.Key, kv.Value))) // the dance guard (docs/MOVEMENT.md).Set("render_fps", MathF.Round(game.RenderFps, 1))
                .Set("teams", w.Teams.Select(t => new JObj()
                    .Set("team", t.Id).Set("seat", t.Seat).Set("name", t.Name).Set("controller", t.Controller).Set("player", t.PlayerName)
                    .Set("stockpile", StateView.Stockpile(t)).Set("defeated", t.Defeated)
                    .Set("structures", w.Owned(t.Id).Count(e => e.IsStructure)).Set("units", w.Owned(t.Id).Count(e => !e.IsStructure))
                    .Set("kills", t.Stats.Kills).Set("ore_mined", t.Stats.OreMined)).ToList())
                .Set("chat", w.Events.Where(e => e.Type == "chat").Reverse().Take(10).Reverse()
                    .Select(e => $"[{e.Tick * World.Dt:0}s] {(e.Team >= 0 ? w.Teams[e.Team].Name : "server")}: {e.Text}").ToList());
        }

        static void Respond(HttpListenerContext ctx, int status, string payload, string contentType, bool bulk = false)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                if (bulk)
                {
                    ctx.Response.AddHeader("Cache-Control", "no-store");
                    if ((ctx.Request.Headers["Accept-Encoding"] ?? "").Contains("gzip"))
                    {
                        using (var ms = new MemoryStream())
                        {
                            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest)) gz.Write(bytes, 0, bytes.Length);
                            bytes = ms.ToArray();
                        }
                        ctx.Response.AddHeader("Content-Encoding", "gzip");
                    }
                    ctx.Response.AddHeader("Vary", "Accept-Encoding");
                }
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
