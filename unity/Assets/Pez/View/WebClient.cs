using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;
using UnityEngine;
using UnityEngine.Networking;

namespace Pez.View
{
    /// <summary>
    /// The browser build is a view of a game the server owns: it fetches a snapshot, restores it, and ticks it locally
    /// (AIs included) so units move smoothly. With live=N it fetches again every N seconds and swaps the fresher world
    /// in under the running view (GameRunner.AdoptWorld). Added by GameRunner on the web player only.
    /// Page URL query: snapshot=URL (default snapshot.json), live=SECONDS (0 = static), team=N (point of view; default
    /// -1, a spectator who sees everything), cam=x,y,zoom (opening camera), q=0..5 (quality level, default 5), fps=1 (fps label).
    /// The page can also call SendMessage("Pez", "SetSnapshotUrl", url) and SendMessage("Pez", "LookAt", "x,y[,zoom]"). The gateway pieces (meta, feed, orders, the
    /// commander box) are in WebClientContract.cs. c=CODE on a player view turns on the commander box.
    /// </summary>
    public partial class WebClient : MonoBehaviour
    {
        public GameRunner Runner;
        /// <summary>The first snapshot is in: the runner may build the view and tick.</summary>
        public bool Loaded { get; private set; }
        /// <summary>Point of view: a team's fog, or -1 for everything.</summary>
        public int Team = -1;
        int askedTeam = -1;

        string url = "snapshot.json", error;
        float live;
        int lastTick = -1;
        bool fetching, showFps;
        Vector3? camPos;
        float camZoom = float.NaN;

        // Frame-time meter: a log line every 5 s, and (fps=1) a label from the last second.
        readonly List<float> frames = new List<float>(1024);
        float windowStart, labelStart, labelFps;
        int labelFrames;
        GUIStyle style;

        static Dictionary<string, string> query;

        /// <summary>One value from the page URL's query string, or null.</summary>
        public static string Query(string key)
        {
            if (query == null)
            {
                query = new Dictionary<string, string>();
                var u = Application.absoluteURL ?? "";
                int q = u.IndexOf('?'), h = u.IndexOf('#');
                if (q >= 0)
                {
                    var qs = h > q ? u.Substring(q + 1, h - q - 1) : u.Substring(q + 1);
                    foreach (var kv in qs.Split(new[] { '&' }, System.StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = kv.IndexOf('=');
                        var k = System.Uri.UnescapeDataString(eq >= 0 ? kv.Substring(0, eq) : kv);
                        query[k] = eq >= 0 ? System.Uri.UnescapeDataString(kv.Substring(eq + 1).Replace('+', ' ')) : "";
                    }
                }
            }
            return query.TryGetValue(key, out var v) ? v : null;
        }

        static float Num(string key, float def) =>
            Query(key) is string s && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;

        /// <summary>The web player's default quality level is High (3), the standalone's Ultra (5): pick one from q=, before the scene's own settings go on top.</summary>
        public static void ApplyQuality()
        {
            int q = Mathf.Clamp((int)Num("q", 5), 0, QualitySettings.names.Length - 1);
            QualitySettings.SetQualityLevel(q, true);
            Debug.Log($"pez quality: level {q} ({QualitySettings.names[q]})");
        }

        void Awake()
        {
            if (Query("snapshot") is string s && s.Length > 0) url = s;
            Instance = this;
            // Opened without live=: a player's own view follows its game, a spectator's follows the delayed feed.
            live = Mathf.Max(0f, Num("live", url.Contains("/view/") ? 1f : url.Contains("/watch") ? 2f : 0f));
            Team = askedTeam = (int)Num("team", -1);
            Spectator = Team < 0;
            ContractAwake();
            showFps = Query("fps") == "1";
            if (Query("cam") is string c && ParseCam(c, out var at, out var zoom)) { camPos = at; camZoom = zoom; }
            windowStart = labelStart = Time.unscaledTime;
        }

        void Start() { StartCoroutine(Loop()); StartContract(); }

        // Why the player snapshot isn't being polled (the seat's link 401s); meta decides what happens next.
        bool expired;
        float nextDelay;
        int failures;

        IEnumerator Loop()
        {
            while (true)
            {
                if (expired) { yield return WaitOrWake(10f); continue; } // meta (every 10 s, or on the wake) says when the spectator feed takes over
                // A spectator polls a little faster than the delayed feed is sampled (every 2 s), so no sample is ever skipped.
                nextDelay = live > 0f ? (Spectator ? Mathf.Min(live, 1.5f) : live) : 2f; // (before the first one arrives, keep trying)
                yield return Fetch();
                if (live <= 0f && Loaded && failures == 0) yield break; // a static view: the one snapshot is all there is
                yield return WaitOrWake(nextDelay);
            }
        }

        /// <summary>Waits <paramref name="seconds"/>, or less if something asked the loops to look again now (mode change, 401).</summary>
        IEnumerator WaitOrWake(float seconds)
        {
            wake = false;
            for (float t = 0f; t < seconds && !wake; t += 0.1f) yield return new WaitForSecondsRealtime(0.1f);
            wake = false;
        }
        bool wake;

        /// <summary>Page hook: follow a different snapshot URL (applied even if its tick is older than the last).</summary>
        public void SetSnapshotUrl(string newUrl)
        {
            if (string.IsNullOrEmpty(newUrl)) return;
            url = newUrl;
            lastTick = -1; newestTick = -1; serverOffset = float.NaN; queue.Clear(); Runner?.Game?.Replay.Clear();
            StopAllCoroutines();
            fetching = false;
            expired = false; failures = 0; seatEnded = false; Result = null;
            StartCoroutine(Loop());
            StartContract();
        }

        string FetchUrl()
        {
            var u = url;
            try { if (u.IndexOf("://") < 0 && !string.IsNullOrEmpty(Application.absoluteURL)) u = new System.Uri(new System.Uri(Application.absoluteURL), u).ToString(); } catch { }
            if (live > 0f) u += (u.Contains("?") ? "&" : "?") + "_=" + System.DateTime.UtcNow.Ticks; // past any cache
            return u;
        }

        IEnumerator Fetch()
        {
            if (fetching) yield break;
            fetching = true;
            using (var req = UnityWebRequest.Get(FetchUrl()))
            {
                yield return req.SendWebRequest();
                long code = req.responseCode;
                var body = req.downloadHandler != null ? req.downloadHandler.text : null;
                if (req.result == UnityWebRequest.Result.Success)
                {
                    NoteHeaders(req);
                    try
                    {
                        Apply(body); error = null; failures = 0;
                        fetchedAt = Time.unscaledTime;
                        Status = null; // reconnected, or the waiting/expired line is out of date
                    }
                    catch (System.Exception ex)
                    {
                        error = "Couldn't read the game: " + ex.Message;
                        Debug.LogError($"pez snapshot: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                else if (code == 503) // nothing to show yet: the spectator feed hasn't started (or has nothing)
                {
                    Status = WaitingText(body) ?? "Waiting for the game…";
                    nextDelay = 2f;
                    failures = 0;
                }
                else if (code == 401) // the seat's link is dead: stop asking for the player's snapshot until meta says what's next
                {
                    Status = ExpiredStatus;
                    expired = true;
                    metaNow = true; wake = true;
                    Debug.LogWarning($"pez snapshot: 401 from {url}: {ErrorOf(body) ?? "link expired"}");
                }
                else
                {
                    failures++;
                    nextDelay = Mathf.Min(8f, Mathf.Pow(2f, failures - 1)); // 1, 2, 4, 8 s
                    error = $"Couldn't fetch the game ({req.error})";
                    if (Loaded) Status = Reconnecting;
                    if (failures <= 1) Debug.LogWarning($"pez snapshot: {error} from {url}");
                }
            }
            fetching = false;
        }

        // ---------------------------------------------------------------- the jitter buffer
        // Snapshots don't arrive on the game's clock (the server samples every so often, the page polls on its own
        // timer, fetches take varying time). Applying each on arrival made the local copy run ahead, then get pulled
        // back and replayed forward from the snapshot, every few seconds. Instead the copy plays a little behind the
        // newest snapshot (one poll interval and half a second), each snapshot is swapped in when the local clock
        // reaches its tick (same tick: nothing to replay), and the local clock runs a few percent fast or slow to hold
        // that lag (RateScale, read by GameRunner).

        struct Pending { public Dictionary<string, object> Root; public Dictionary<string, object> World; public int Tick; public int Bytes; public double ParseMs; }
        readonly List<Pending> queue = new List<Pending>();
        int newestTick = -1;
        float serverOffset = float.NaN; // smoothed (server tick - local clock * tick rate): the server's clock without arrival jitter
        /// <summary>How fast the local game clock runs (1 = real time); GameRunner scales Game.Advance by it.</summary>
        public float RateScale { get; private set; } = 1f;

        float TickRateNow => World.TickRate * Mathf.Max(0.1f, Runner.Game.Speed);
        float BufferTicks => ((Spectator ? 2f : Mathf.Max(live, 0.5f)) + 0.5f) * TickRateNow;

        void Apply(string text)
        {
            var game = Runner.Game;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var root = Snapshot.Parse(text);
            double parseMs = sw.Elapsed.TotalMilliseconds;
            var wd = root.Obj("world");
            int tick = wd.In("tick");
            bool sameGame = Loaded && wd.Str("game_id") == game.World.GameId;
            if (sameGame && tick < lastTick - 200) { lastTick = -1; newestTick = -1; serverOffset = float.NaN; queue.Clear(); game.Replay.Clear(); } // the server went back (a restart from an older save): follow it
            if (sameGame && (tick <= lastTick || tick <= newestTick)) return; // nothing newer than what's on screen or queued
            var p = new Pending { Root = root, World = wd, Tick = tick, Bytes = text.Length, ParseMs = parseMs };
            newestTick = tick;
            // The orders in it, for the copy to give at their ticks (Game.Replay): it plays behind the newest snapshot, so it
            // hears of each one before reaching it.
            if (!sameGame) game.Replay.Clear();
            foreach (var x in wd.Arr("commands"))
                if (x is List<object> l && l.Count >= 4 && l[0] is double seq && l[1] is double at && l[2] is double by && l[3] is Dictionary<string, object> cmd)
                    game.Replay[(long)seq] = new LoggedCommand { Seq = (long)seq, Tick = (int)at, Team = (int)by, Cmd = cmd };
            float offset = tick - Time.unscaledTime * TickRateNow;
            serverOffset = float.IsNaN(serverOffset) || Mathf.Abs(offset - serverOffset) > BufferTicks * 2 ? offset : Mathf.Lerp(serverOffset, offset, 0.2f);
            // A first load, a new match, a different point of view, or far behind (a hidden tab): straight to it.
            int local = game.World.Tick;
            if (!sameGame || Team != WantedTeam(game.World) || tick - local > BufferTicks * 4) { queue.Clear(); ApplyNow(p, sameGame); return; }
            queue.Add(p);
        }

        /// <summary>The team this page shows for a world (the asked-for one, if the game has it and the seat hasn't ended).</summary>
        int WantedTeam(World w) => !seatEnded && askedTeam < w.Teams.Count ? askedTeam : -1;

        /// <summary>Each frame: swap in the queued snapshot the local clock has reached, and set the clock's pace.</summary>
        void PlayQueue()
        {
            if (!Loaded || Runner == null || Runner.Game == null) { RateScale = 1f; return; }
            var game = Runner.Game;
            int at = -1;
            for (int i = 0; i < queue.Count; i++) if (queue[i].Tick <= game.World.Tick) at = i;
            if (at >= 0)
            {
                var p = queue[at];
                queue.RemoveRange(0, at + 1);
                ApplyNow(p, true);
            }
            if (newestTick < 0 || live <= 0f) { RateScale = 1f; return; }
            // Where the server's game is now, extrapolated from the newest snapshot, minus the lag this copy keeps.
            float target = serverOffset + Time.unscaledTime * TickRateNow - BufferTicks;
            float err = target - (game.World.Tick + game.Alpha);
            RateScale = Mathf.Clamp(1f + err / (8f * TickRateNow), 0.9f, 1.12f); // closes the gap over ~8 s, within a pace nobody notices
        }

        void ApplyNow(Pending p, bool sameGame)
        {
            var game = Runner.Game;
            var wd = p.World;
            int tick = p.Tick;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int local = sameGame ? game.World.Tick : tick; float alpha = game.Alpha; // where the view's world is now
            game.Restore(p.Root);
            game.World.Mirror = true; // the server decides the game; this copy only shows it
            game.World.MirrorScores = wd.Arr("mirror_scores").OfType<Dictionary<string, object>>().Select(s => new MatchScore
            {
                Team = (int)s.Num("team"), Name = s.Str("name"), Player = s.Str("player"), Territory = (int)s.Num("territory"),
                OreMined = (int)s.Num("ore_mined"), KillValue = (int)s.Num("kill_value"), TerritoryPoints = (float)s.Num("territory_points"),
                EconomyPoints = (float)s.Num("economy_points"), KillPoints = (float)s.Num("kill_points"), Score = (int)s.Num("score"),
            }).ToList() is var ms && ms.Count > 0 ? ms : null;
            if (local > tick && local - tick <= 40) game.CatchUp(local, alpha); // the frame or two past the snapshot's tick (events up to here already played)
            double restoreMs = sw.Elapsed.TotalMilliseconds;
            // The page's team may not be in this game (a seat from an earlier match, or not taken yet): watch everything until it is.
            int team = WantedTeam(game.World);
            if (team != Team && !seatEnded) Debug.Log($"pez team: showing {(team < 0 ? "everything" : "team " + team)} (asked for {askedTeam}, the game has {game.World.Teams.Count})");
            Team = team;
            Spectator = seatEnded || team < 0;
            ShowStandingOrders(game.World);
            sw.Restart();
            // The same game: the view moves over to the new world. A different one, the first, or a new point of view: Update rebuilds the view.
            bool rebound = sameGame && Team == Runner.HumanTeam && Runner.AdoptWorld(game.World);
            double rebindMs = sw.Elapsed.TotalMilliseconds;
            lastTick = tick;
            if (!Loaded)
            {
                Loaded = true;
                Runner.InMenu = false;
                Runner.Hud.enabled = Runner.Input.enabled = true;
            }
            Debug.Log($"pez resync: tick={tick} bytes={p.Bytes} parse={p.ParseMs:0.#}ms restore={restoreMs:0.#}ms rebind={rebindMs:0.#}ms ({(rebound ? "rebound" : "rebuilt")}) local={local} rate={RateScale:0.00} queued={queue.Count}");
        }

        /// <summary>"x,y[,zoom]" in map tiles (zoom NaN when absent), as in the page's cam= parameter.</summary>
        static bool ParseCam(string c, out Vector3 at, out float zoom)
        {
            var p = c.Split(',');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var num = System.Globalization.NumberStyles.Float;
            at = default; zoom = float.NaN;
            if (p.Length < 2 || !float.TryParse(p[0], num, inv, out var x) || !float.TryParse(p[1], num, inv, out var y)) return false;
            at = new Vector3(x, 0, y);
            if (p.Length >= 3 && float.TryParse(p[2], num, inv, out var z)) zoom = z;
            return true;
        }

        /// <summary>
        /// Page hook: SendMessage("Pez", "LookAt", "x,y[,zoom]") flies the camera there (the CI room's index uses it, so
        /// picking an exhibit doesn't reload the build). Before the first view is built it becomes the opening camera.
        /// </summary>
        public void LookAt(string c)
        {
            if (c == null || !ParseCam(c, out var at, out var zoom)) return;
            var cam = Runner != null && Runner.View != null ? Runner.Camera : null;
            if (cam == null) { camPos = at; camZoom = zoom; return; }
            Runner.View.Selected.Clear(); // a followed unit would pull the camera straight back
            cam.FlyTo(at, float.IsNaN(zoom) ? cam.Distance : zoom);
        }

        /// <summary>GameRunner built the view: put the camera where the page asked, the first time only.</summary>
        public void OnViewBuilt(RtsCamera cam)
        {
            if (camPos.HasValue) { cam.LookAt(camPos.Value); if (!float.IsNaN(camZoom)) cam.Distance = camZoom; }
            camPos = null;
        }

        void Update()
        {
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            UpdateContract();
            PlayQueue();
            ReportLoading();
            if (dt <= 0f) return;
            frames.Add(dt);
            labelFrames++;
            if (now - labelStart >= 1f) { labelFps = labelFrames / (now - labelStart); labelFrames = 0; labelStart = now; }
            if (now - windowStart < 5f) return;
            float total = 0f;
            foreach (var f in frames) total += f;
            frames.Sort();
            float p95 = frames[Mathf.Min(frames.Count - 1, (int)(frames.Count * 0.95f))]; // the slowest 5% of frames
            Debug.Log($"pez fps: avg={frames.Count / total:0.#} p5={1f / p95:0.#} frames={frames.Count} w={Screen.width} h={Screen.height}");
            frames.Clear();
            windowStart = now;
        }

        // The page's loading screen covers everything until the first view has been drawn: it hears what the game is
        // waiting on (the waiting line, an error) and the map's name, then "ready" two frames after the view is built.
        string sentLoad;
        int drawnFrames;

        void ReportLoading()
        {
            if (sentLoad == "ready") return;
            string state = !Loaded ? (error != null ? "trouble" : "join") : Runner.View == null || drawnFrames++ < 2 ? "build" : "ready";
            string status = Loaded ? null : error ?? Status;
            var key = $"{state}|{status}|{MapName}";
            if (key == sentLoad) return;
            sentLoad = state == "ready" ? "ready" : key;
            PezOverlay.Loading(state, status, MapName);
        }

        void OnGUI()
        {
            if (!showFps) return;
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleRight, normal = { textColor = Color.white } };
            GUI.Label(new Rect(Screen.width - 130, 4, 124, 22), $"{labelFps:0} fps", style);
        }
    }
}
