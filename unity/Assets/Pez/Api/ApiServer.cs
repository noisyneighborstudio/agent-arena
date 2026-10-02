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
            Log($"Pez API listening on http://127.0.0.1:{Port}/");
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
                catch (Exception ex)
                {
                    status = 400;
                    payload = Json.Write(new JObj().Set("ok", false).Set("error", ex.Message));
                }
                Respond(p.Ctx, status, payload, contentType);
            }
        }

        static int TeamParam(HttpListenerRequest req, World w)
        {
            var s = req.QueryString["team"];
            if (s == null || !int.TryParse(s, out var t) || t < 0 || t >= w.Teams.Count)
                throw new ArgumentException($"query parameter team=0..{w.Teams.Count - 1} is required");
            return t;
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
                    return "Pez RTS control API\n\nGET  /api/rules\nGET  /api/state?team=N[&since=SEQ]\nGET  /api/map?team=N\nPOST /api/command?team=N   body: {\"commands\":[...]} | [...] | {...}\nPOST /api/join?team=N      body: {\"name\":\"Claude\"}\nGET  /api/status\nPOST /api/admin/restart    body: {\"seed\":1,\"controllers\":[\"llm\",\"llm\"],\"speed\":1}\nPOST /api/admin/speed      body: {\"speed\":0.5}\n\n" + Commands.Help;
                case "/api/rules":
                    return Json.Write(StateView.Rules());
                case "/api/state":
                    {
                        int team = TeamParam(req, w);
                        long since = long.TryParse(req.QueryString["since"], out var s) ? s : 0;
                        return Json.Write(StateView.TeamState(w, team, since));
                    }
                case "/api/map":
                    contentType = "text/plain";
                    return StateView.AsciiMap(w, TeamParam(req, w));
                case "/api/status":
                    return Json.Write(Status(game));
                case "/api/join":
                    {
                        int team = TeamParam(req, w);
                        var d = p.Body != null ? Json.Parse(p.Body) as Dictionary<string, object> : null;
                        var name = d?.Str("name") ?? "LLM";
                        w.Teams[team].PlayerName = name.Length > 40 ? name.Substring(0, 40) : name;
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
                        var cfg = new GameConfig { Seed = (int)(d?.Num("seed", game.Config.Seed) ?? game.Config.Seed), Speed = d?.Num("speed", game.Speed) ?? game.Speed };
                        if (d != null && d.TryGetValue("controllers", out var cs) && cs is List<object> cl2) cfg.Controllers = cl2.Select(x => x.ToString()).ToArray();
                        else cfg.Controllers = game.Config.Controllers;
                        game.Restart(cfg);
                        return Json.Write(new JObj().Set("ok", true).Set("seed", cfg.Seed).Set("controllers", cfg.Controllers.ToList()));
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

        public static JObj Status(Game game)
        {
            var w = game.World;
            return new JObj()
                .Set("tick", w.Tick).Set("time_s", (float)Math.Round(w.Time, 1))
                .Set("speed", game.Speed).Set("paused", game.Paused)
                .Set("game_over", w.GameOver).Set("winner", w.Winner)
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
