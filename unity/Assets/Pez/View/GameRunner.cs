using System.Collections.Generic;
using System.Linq;
using Pez.Api;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Owns the Game, the HTTP API and the view. Created automatically at startup (see Bootstrap),
    /// so the project needs no authored scene content.
    /// Command line: -team0 human|ai|claude|codex|llm -team1 ... -model0 sonnet -effort0 medium -orders0 "text" -seed N -mapsize 80 -open -port 7777 -speed 1 -autostart
    ///               -resume (resume the saved game; an -open arena does by default) -fresh (discard it) -snapshot path
    /// The running game is saved to ~/.config/pezz/rooms/game-PORT.json (see RoomSaver), so a relaunch picks it up.
    /// </summary>
    public class GameRunner : MonoBehaviour
    {
        public Game Game { get; private set; }
        public ApiServer Api { get; private set; }
        public int Port = 7777;
        public string ApiError;
        public bool InMenu = true;
        public int HumanTeam = -1;
        public GameConfig MenuConfig;
        public WorldView View { get; private set; }
        public RtsCamera Camera { get; private set; }
        public PlayerInput Input { get; private set; }
        public Hud Hud { get; private set; }
        /// <summary>Commands agents sent over the API, for the HUD's command feed: plain text, sectors as bare "E5".</summary>
        public readonly List<(float time, int team, string text, bool ok, string error)> CommandFeed = new List<(float, int, string, bool, string)>();
        public string AgentStatus;
        System.Diagnostics.Process agentProc, gatewayProc;
        /// <summary>URL outside agents use to join (the local gateway; expose it with tailscale serve).</summary>
        public string GatewayUrl;
        public int GatewayPort = 7790;
        public string Invite;
        World viewWorld;
        bool startingFromMenu;
        bool firstView = true;
        RoomSaver saver;
        bool resumed;

        void Awake()
        {
            Application.runInBackground = true; // LLMs drive the game while the window is unfocused
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            MenuConfig = ParseArgs(out bool autostart);
            Game = new Game(MenuConfig);
            InMenu = !autostart;
            SetupScene();
            StartApi();
            // A saved game for this port (a new build swapped in, or the app restarted): carry on with it.
            var args = System.Environment.GetCommandLineArgs();
            int si = System.Array.IndexOf(args, "-snapshot");
            saver = new RoomSaver(si >= 0 && si + 1 < args.Length ? args[si + 1] : RoomSaver.DefaultFile(Port)) { Log = Debug.Log };
            resumed = saver.TryResume(Game, Api, args.Contains("-resume"), MenuConfig.Open && autostart, args.Contains("-fresh"));
            if (resumed) InMenu = false;
            if (Api != null)
            {
                Api.OnRestart = () => saver.Forget(Game);
                Api.OnSave = () => saver.Save(Game, Api, "requested");
            }
        }

        GameConfig ParseArgs(out bool autostart)
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string name, string def) { int i = System.Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
            autostart = args.Contains("-autostart");
            Port = int.Parse(Arg("-port", "7777"));
            return new GameConfig
            {
                Seed = int.Parse(Arg("-seed", Random.Range(1, 99999).ToString())),
                MapSize = int.Parse(Arg("-mapsize", "80")),
                Speed = float.Parse(Arg("-speed", "1"), System.Globalization.CultureInfo.InvariantCulture),
                Controllers = new[] { Arg("-team0", "human"), Arg("-team1", "ai") },
                Orders = new[] { Arg("-orders0", ""), Arg("-orders1", "") },
                Open = args.Contains("-open"),
            };
        }

        void StartApi()
        {
            try
            {
                Api = new ApiServer(Port) { Log = Debug.Log };
                Api.OnCommand = OnApiCommand;
                Api.OnScreenshot = path => ScreenCapture.CaptureScreenshot(path);
                Api.OnCamera = d =>
                {
                    if (!float.IsNaN(d.Num("x"))) Camera.LookAt(new Vector3(d.Num("x"), 0, d.Num("y")));
                    if (!float.IsNaN(d.Num("distance"))) Camera.Distance = d.Num("distance");
                    if (!float.IsNaN(d.Num("yaw"))) Camera.Yaw = d.Num("yaw");
                    if (d.Str("menu") == "close") InMenu = false;
                    if (d.TryGetValue("edge_pan", out var ep) && ep is bool on) Camera.EdgePan = on;
                    // Inspect entities in the HUD's selection card (e.g. for screenshots): {"select": [id, ...]}.
                    if (d.TryGetValue("select", out var sel) && sel is List<object> ids && View != null)
                    {
                        View.Selected.Clear();
                        foreach (var id in ids) if (id is double n) View.Selected.Add((int)n);
                    }
                };
                Api.Start();
            }
            catch (System.Exception ex)
            {
                ApiError = ex.Message;
                Debug.LogError($"Pezz API failed to start on port {Port}: {ex.Message}");
                Api = null;
            }
        }

        void OnApiCommand(int team, string cmdJson, string resultJson)
        {
            string summary = cmdJson, error = null;
            bool ok = false;
            try
            {
                var c = Json.Parse(cmdJson) as Dictionary<string, object>;
                var r = Json.Parse(resultJson) as Dictionary<string, object>;
                if (c != null)
                {
                    var type = c.Str("type", "?");
                    if (type == "say") return; // already shown as chat
                    var what = c.Str("structure") ?? c.Str("unit") ?? (c.ContainsKey("target") ? $"#{c.Num("target")}" : null);
                    what = what?.Replace('_', ' ');
                    var where = !float.IsNaN(c.Num("x")) ? $" at {StateView.Sector(Game.World.Map, new Vec2(c.Num("x"), c.Num("y")))}" : "";
                    var units = c.TryGetValue("units", out var u) ? (u is List<object> l ? $" {l.Count} unit{(l.Count == 1 ? "" : "s")}" : $" {u}") : "";
                    ok = r != null && r.TryGetValue("ok", out var o) && o is bool b && b;
                    if (!ok) error = r?.Str("error");
                    var verb = type.Replace('_', ' ');
                    summary = $"{char.ToUpperInvariant(verb[0])}{verb.Substring(1)}{(what != null ? " " + what : "")}{units}{where}";
                }
            }
            catch { }
            CommandFeed.Add((Game.World.Time, team, summary, ok, error));
            if (CommandFeed.Count > 60) CommandFeed.RemoveRange(0, CommandFeed.Count - 60);
        }

        void SetupScene()
        {
            // Lighting (art pack, render.html): warm sun #FFE6C4 from the upper left of the screen, shadows falling to
            // the lower right, and a cool sky fill (hemisphere #BFD4FF over #5A4A36) so shaded faces go cool, not grey.
            // Sun, sky fill, reflections and shadow settings: Look.SetupLighting (see docs/render/QUALITY_PASS.md).
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            Look.SetupLighting(sun);
            RenderSettings.sun = sun;
            RtsCamera.AimSun(45f); // re-aimed every frame against the camera's yaw
            RenderSettings.fog = false; // no distance haze in an orthographic view
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.55f, 0.62f, 0.68f);
            RenderSettings.fogStartDistance = 55f;
            RenderSettings.fogEndDistance = 160f;
            RtsCamera.FitShadows(RtsCamera.BoardOrthoSize); // refitted to each view as it renders
            QualitySettings.antiAliasing = 4;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;

            var existing = UnityEngine.Camera.main;
            var camGo = existing != null ? existing.gameObject : new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.GetComponent<UnityEngine.Camera>();
            if (cam == null) cam = camGo.AddComponent<UnityEngine.Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color32(42, 36, 32, 255); // the art pack's map backdrop #2A2420
            cam.orthographic = true; // art pack decision 9: off-axis orthographic
            cam.fieldOfView = 38f;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 500f;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            if (camGo.GetComponent<AudioListener>() == null) camGo.AddComponent<AudioListener>();
            Camera = camGo.GetComponent<RtsCamera>();
            if (Camera == null) Camera = camGo.AddComponent<RtsCamera>();
            Camera.Follow = FollowPoint;

            Input = gameObject.AddComponent<PlayerInput>();
            Input.Runner = this;
            Hud = gameObject.AddComponent<Hud>();
            Hud.Runner = this;
            gameObject.AddComponent<FrameServer>().Port = Port + 1;
            // Per-team fog overlays are for the player streams only; bars are drawn after post-processing (PezPost).
            cam.cullingMask = ~TerrainView.TeamFogMask & ~(1 << Bars.Layer);
            if (camGo.GetComponent<PezPost>() == null) camGo.AddComponent<PezPost>();
            gameObject.AddComponent<PlayerStreams>().Runner = this;
        }

        static readonly string[] AgentClis = { "claude", "codex", "grok", "gemini" };

        public void StartGame(GameConfig cfg)
        {
            StopAgents();
            saver?.Forget(Game); // a new game replaces the saved one
            startingFromMenu = true;
            // The sim only knows "llm"; which CLI plays is the launcher's business.
            var simControllers = cfg.Controllers.Select(c => AgentClis.Contains(c) ? "llm" : c).ToArray();
            Game.Restart(new GameConfig { Seed = cfg.Seed, Speed = cfg.Speed, MapSize = cfg.MapSize, Controllers = simControllers, Orders = (string[])cfg.Orders?.Clone(), Open = cfg.Open });
            InMenu = false;
            if (cfg.Controllers.Any(c => AgentClis.Contains(c))) LaunchAgents(cfg.Controllers);
            if (cfg.Open) LaunchGateway(); else StopGateway();
        }

        /// <summary>Repo root (for arena/battle.mjs): -repo arg, else derived from the app or editor location.</summary>
        static string RepoRoot()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-repo");
            if (i >= 0 && i + 1 < args.Length) return args[i + 1];
            foreach (var up in new[] { "../../../..", "../.." }) // Build/Pezz.app/Contents, or unity/Assets
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, up));
                if (System.IO.File.Exists(System.IO.Path.Combine(root, "arena", "battle.mjs"))) return root;
            }
            return null;
        }

        void LaunchAgents(string[] controllers)
        {
            var root = RepoRoot();
            if (root == null) { AgentStatus = "Can't find arena/battle.mjs; launch with -repo /path/to/pez"; Debug.LogError(AgentStatus); return; }
            var players = string.Join(" ", controllers.Select(c => c == "llm" ? "external" : c));
            // Per-team model/effort (-model0 sonnet -effort0 medium) map to the arena's per-player flags.
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string name) { int i = System.Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var extra = "";
            for (int t = 0; t < controllers.Length; t++)
            {
                if (!AgentClis.Contains(controllers[t])) continue;
                if (Arg($"-model{t}") is string m) extra += $" --model-{controllers[t]} {m}";
                if (Arg($"-effort{t}") is string ef) extra += $" --effort-{controllers[t]} {ef}";
            }
            var log = System.IO.Path.Combine(root, "arena", "logs", "unity-agents.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log));
            // A login+interactive shell picks up the user's PATH (node, claude, codex) even when launched from Finder.
            var cmd = $"cd '{root}' && exec node arena/battle.mjs {players}{extra} --attach --no-restart --url http://127.0.0.1:{Port} --minutes 120 > '{log}' 2>&1";
            try
            {
                agentProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/zsh", $"-lic \"{cmd}\"") { UseShellExecute = false, CreateNoWindow = true });
                AgentStatus = $"Agents launched ({players}); log: arena/logs/unity-agents.log";
                Debug.Log(AgentStatus);
            }
            catch (System.Exception ex) { AgentStatus = "Agent launch failed: " + ex.Message; Debug.LogError(AgentStatus); }
        }

        /// <summary>Start the arena gateway (mcp/gateway.js) so outside agents can join this game.</summary>
        void LaunchGateway()
        {
            if (gatewayProc != null && !gatewayProc.HasExited) return;
            var root = RepoRoot();
            if (root == null) { AgentStatus = "Can't find the repo to start the arena gateway; launch with -repo /path/to/pez"; return; }
            var log = System.IO.Path.Combine(root, "arena", "logs", "gateway.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log));
            var args = System.Environment.GetCommandLineArgs();
            int gi = System.Array.IndexOf(args, "-gatewayport");
            if (gi >= 0 && gi + 1 < args.Length && int.TryParse(args[gi + 1], out var gp)) GatewayPort = gp;
            int ui = System.Array.IndexOf(args, "-gatewayurl"); // the public (e.g. tailnet) URL to show and to put in links
            var url = ui >= 0 && ui + 1 < args.Length ? args[ui + 1] : System.Environment.GetEnvironmentVariable("PEZZ_PUBLIC_URL");
            var pub = string.IsNullOrEmpty(url) ? "" : $"PEZZ_PUBLIC_URL='{url}' ";
            int ii = System.Array.IndexOf(args, "-invite");
            Invite = ii >= 0 && ii + 1 < args.Length ? args[ii + 1] : null;
            if (!string.IsNullOrEmpty(Invite)) pub += $"PEZZ_INVITE='{Invite}' ";
            var cmd = $"cd '{root}' && PEZZ_GAME=http://127.0.0.1:{Port} PEZZ_GATEWAY_PORT={GatewayPort} {pub}exec node mcp/gateway.js > '{log}' 2>&1";
            if (PortInUse(GatewayPort))
            {
                // A long-running gateway (e.g. the LaunchAgent) already serves this port. Use it, so agents' MCP
                // sessions survive the game restarting.
                GatewayUrl = string.IsNullOrEmpty(url) ? $"http://127.0.0.1:{GatewayPort}" : url.TrimEnd('/');
                Debug.Log($"Using the running arena gateway on {GatewayUrl}");
                return;
            }
            try
            {
                gatewayProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/zsh", $"-lic \"{cmd}\"") { UseShellExecute = false, CreateNoWindow = true });
                GatewayUrl = string.IsNullOrEmpty(url) ? $"http://127.0.0.1:{GatewayPort}" : url.TrimEnd('/');
                Debug.Log($"Arena gateway on {GatewayUrl}");
            }
            catch (System.Exception ex) { AgentStatus = "Gateway launch failed: " + ex.Message; }
        }

        static bool PortInUse(int port)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                return c.ConnectAsync("127.0.0.1", port).Wait(300) && c.Connected;
            }
            catch { return false; }
        }

        /// <summary>One selected mobile unit: the main camera follows it until it's deselected (or dies, or slips out of sight).</summary>
        Vector3? FollowPoint()
        {
            if (View == null || InMenu || View.Selected.Count != 1) return null;
            var e = Game.World.Get(View.Selected.First());
            if (e == null || e.IsStructure || e.IsMine) return null;
            return View.Views.TryGetValue(e.Id, out var v) && v.Rig.Root.gameObject.activeInHierarchy ? v.Rig.Root.position : (Vector3?)null;
        }

        void StopGateway()
        {
            if (gatewayProc == null) return;
            try { if (!gatewayProc.HasExited) System.Diagnostics.Process.Start("/bin/kill", $"-TERM {gatewayProc.Id}")?.WaitForExit(2000); } catch { }
            gatewayProc = null;
            GatewayUrl = null;
        }

        void StopAgents()
        {
            if (agentProc == null) return;
            try
            {
                // SIGTERM (not Kill/SIGKILL) so battle.mjs can shut down the CLIs it spawned.
                if (!agentProc.HasExited) System.Diagnostics.Process.Start("/bin/kill", $"-TERM {agentProc.Id}")?.WaitForExit(2000);
            }
            catch { }
            agentProc = null;
            AgentStatus = null;
        }

        void RebuildView()
        {
            Game.World.ErrorLog = msg => Debug.LogError("Sim error (game continues): " + msg);
            if (View != null) Destroy(View.gameObject);
            viewWorld = Game.World;
            HumanTeam = System.Array.IndexOf(Game.Config.Controllers, "human");
            View = new GameObject("World").AddComponent<WorldView>();
            View.Init(viewWorld, HumanTeam);
            Camera.Bounds = new Vector2(viewWorld.Map.W, viewWorld.Map.H);
            View.MapRebuilt = () => Camera.Bounds = new Vector2(viewWorld.Map.W, viewWorld.Map.H); // the arena grew
            // Open on a base at the art pack's ~40 px per tile (06_HUD, hero_offaxis): the player's own, or for a
            // spectator the first seated team's; the map centre if nobody has a base yet. RtsCamera keeps it over the map.
            Camera.LookAt(WorldView.W(OpeningFocus(viewWorld, HumanTeam)));
            Camera.Distance = Camera.DefaultDist;
            CommandFeed.Clear();
            // A restart that came from the API (e.g. the LLM arena) should start playing immediately.
            if (!startingFromMenu && !firstView) InMenu = false;
            firstView = false;
            startingFromMenu = false;
        }

        static Vec2 OpeningFocus(World w, int team)
        {
            var seats = team >= 0 ? new[] { w.Teams[team] } : w.Teams.Where(t => !t.Left && !t.Defeated).ToArray();
            foreach (var t in seats)
            {
                var hq = w.Entities.FirstOrDefault(e => !e.Dead && e.Team == t.Id && e.Def.Key == "command_center");
                if (hq != null) return hq.Center;
                if (t.StartPos.X > 0 || t.StartPos.Y > 0) return t.StartPos;
            }
            return new Vec2(w.Map.W / 2f, w.Map.H / 2f);
        }

        void Start()
        {
            if (resumed) ResumeSession();
            else if (!InMenu) StartGame(MenuConfig); // -autostart, including launching any agent CLIs
        }

        /// <summary>The saved game is already loaded: bring back the in-app agents and the gateway around it.</summary>
        void ResumeSession()
        {
            startingFromMenu = true;
            if (MenuConfig.Controllers.Any(c => AgentClis.Contains(c))) LaunchAgents(MenuConfig.Controllers);
            if (Game.Config.Open) LaunchGateway();
        }

        void Update()
        {
            Api?.Pump(Game);
            if (Game.World != viewWorld) RebuildView();
            if (!InMenu)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Pause) || (UnityEngine.Input.GetKeyDown(KeyCode.P) && HumanTeam < 0 && !Hud.Typing)) Game.Paused = !Game.Paused;
                Game.Advance(Time.deltaTime);
                saver?.Tick(Game, Api);
            }
            if (Time.unscaledDeltaTime > 0) Game.RenderFps = Mathf.Lerp(Game.RenderFps, 1f / Time.unscaledDeltaTime, 0.05f);
            View.Sync(Game.Alpha);
        }

        void OnDestroy() { StopAgents(); StopGateway(); Api?.Stop(); }
        void OnApplicationQuit()
        {
            if (!InMenu) saver?.Save(Game, Api, "quit");
            StopAgents(); StopGateway(); Api?.Stop();
        }
    }

    public static class Bootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            if (Object.FindFirstObjectByType<GameRunner>() != null) return;
            var go = new GameObject("Pez");
            go.AddComponent<GameRunner>();
        }
    }
}
