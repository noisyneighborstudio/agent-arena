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
        public readonly List<(float time, int team, string text)> CommandFeed = new List<(float, int, string)>();
        public string AgentStatus;
        System.Diagnostics.Process agentProc, gatewayProc;
        /// <summary>URL outside agents use to join (the local gateway; expose it with tailscale serve).</summary>
        public string GatewayUrl;
        public int GatewayPort = 7790;
        public string Invite;
        World viewWorld;
        bool startingFromMenu;
        bool firstView = true;

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
            string summary = cmdJson;
            try
            {
                var c = Json.Parse(cmdJson) as Dictionary<string, object>;
                var r = Json.Parse(resultJson) as Dictionary<string, object>;
                if (c != null)
                {
                    var type = c.Str("type", "?");
                    if (type == "say") return; // already shown as chat
                    var what = c.Str("structure") ?? c.Str("unit") ?? (c.ContainsKey("target") ? $"#{c.Num("target")}" : null);
                    var where = !float.IsNaN(c.Num("x")) ? $" @{c.Num("x"):0},{c.Num("y"):0} [{StateView.Sector(Game.World.Map, new Vec2(c.Num("x"), c.Num("y")))}]" : "";
                    var units = c.TryGetValue("units", out var u) ? (u is List<object> l ? $" {l.Count} units" : $" {u}") : "";
                    bool ok = r != null && r.TryGetValue("ok", out var o) && o is bool b && b;
                    summary = $"{type} {what}{units}{where} {(ok ? "<color=#7f7>✓</color>" : $"<color=#f75>✗ {r?.Str("error")}</color>")}";
                }
            }
            catch { }
            CommandFeed.Add((Game.World.Time, team, summary));
            if (CommandFeed.Count > 60) CommandFeed.RemoveRange(0, CommandFeed.Count - 60);
        }

        void SetupScene()
        {
            // Lighting: warm low sun, cool sky fill, distance haze.
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0);
            sun.color = new Color(1f, 0.93f, 0.82f);
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            sun.shadowBias = 0.03f;
            sun.shadowNormalBias = 0.25f;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.62f, 0.66f);
            RenderSettings.ambientEquatorColor = new Color(0.5f, 0.47f, 0.42f);
            RenderSettings.ambientGroundColor = new Color(0.26f, 0.22f, 0.18f);
            RenderSettings.fog = false; // no distance haze in an orthographic view
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.55f, 0.62f, 0.68f);
            RenderSettings.fogStartDistance = 55f;
            RenderSettings.fogEndDistance = 160f;
            QualitySettings.shadowDistance = 260f; // the orthographic camera sits 150 units back
            QualitySettings.shadowCascades = 4;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
            QualitySettings.antiAliasing = 4;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;

            var existing = UnityEngine.Camera.main;
            var camGo = existing != null ? existing.gameObject : new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.GetComponent<UnityEngine.Camera>();
            if (cam == null) cam = camGo.AddComponent<UnityEngine.Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = PezPalette.TerrainLicoriceCliffFace;
            cam.orthographic = true; // art pack decision 9: off-axis orthographic
            cam.fieldOfView = 38f;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 500f;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            if (camGo.GetComponent<AudioListener>() == null) camGo.AddComponent<AudioListener>();
            Camera = camGo.GetComponent<RtsCamera>();
            if (Camera == null) Camera = camGo.AddComponent<RtsCamera>();

            Input = gameObject.AddComponent<PlayerInput>();
            Input.Runner = this;
            Hud = gameObject.AddComponent<Hud>();
            Hud.Runner = this;
            gameObject.AddComponent<FrameServer>().Port = Port + 1;
            cam.cullingMask = ~TerrainView.TeamFogMask; // per-team fog overlays are for the player streams only
            gameObject.AddComponent<PlayerStreams>().Runner = this;
        }

        static readonly string[] AgentClis = { "claude", "codex", "grok", "gemini" };

        public void StartGame(GameConfig cfg)
        {
            StopAgents();
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
            try
            {
                gatewayProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/zsh", $"-lic \"{cmd}\"") { UseShellExecute = false, CreateNoWindow = true });
                GatewayUrl = string.IsNullOrEmpty(url) ? $"http://127.0.0.1:{GatewayPort}" : url.TrimEnd('/');
                Debug.Log($"Arena gateway on {GatewayUrl}");
            }
            catch (System.Exception ex) { AgentStatus = "Gateway launch failed: " + ex.Message; }
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
            var focus = HumanTeam >= 0 ? viewWorld.Teams[HumanTeam].StartPos : new Vec2(viewWorld.Map.W / 2f, viewWorld.Map.H / 2f);
            Camera.LookAt(WorldView.W(focus) + new Vector3(3, 0, 3));
            Camera.Distance = HumanTeam >= 0 ? 9f : Mathf.Min(Camera.MaxDist, viewWorld.Map.W * 0.375f); // spectators see most of the map
            CommandFeed.Clear();
            // A restart that came from the API (e.g. the LLM arena) should start playing immediately.
            if (!startingFromMenu && !firstView) InMenu = false;
            firstView = false;
            startingFromMenu = false;
        }

        void Start()
        {
            if (!InMenu) StartGame(MenuConfig); // -autostart, including launching any agent CLIs
        }

        void Update()
        {
            Api?.Pump(Game);
            if (Game.World != viewWorld) RebuildView();
            if (!InMenu)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Pause) || (UnityEngine.Input.GetKeyDown(KeyCode.P) && HumanTeam < 0 && !Hud.Typing)) Game.Paused = !Game.Paused;
                Game.Advance(Time.deltaTime);
            }
            View.Sync(Game.Alpha);
        }

        void OnDestroy() { StopAgents(); StopGateway(); Api?.Stop(); }
        void OnApplicationQuit() { StopAgents(); StopGateway(); Api?.Stop(); }
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
