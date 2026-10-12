using System.Collections;
using System.Collections.Generic;
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
    /// The page can also call SendMessage("Pez", "SetSnapshotUrl", url). The gateway pieces (meta, feed, orders, the
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
        float startedAt;
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
            startedAt = Time.unscaledTime;
            // Opened without live=: a player's own view follows its game, a spectator's follows the delayed feed.
            live = Mathf.Max(0f, Num("live", url.Contains("/view/") ? 1f : url.Contains("/watch") ? 2f : 0f));
            Team = askedTeam = (int)Num("team", -1);
            Spectator = Team < 0;
            ContractAwake();
            showFps = Query("fps") == "1";
            if (Query("cam") is string c)
            {
                var p = c.Split(',');
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (p.Length >= 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out var x) && float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var y))
                {
                    camPos = new Vector3(x, 0, y);
                    if (p.Length >= 3 && float.TryParse(p[2], System.Globalization.NumberStyles.Float, inv, out var z)) camZoom = z;
                }
            }
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
                nextDelay = live > 0f ? Mathf.Max(live, Spectator ? 2f : 0f) : 2f; // (before the first one arrives, keep trying)
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
            lastTick = -1;
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

        void Apply(string text)
        {
            var game = Runner.Game;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var root = Snapshot.Parse(text);
            double parseMs = sw.Elapsed.TotalMilliseconds;
            var wd = root.Obj("world");
            int tick = wd.In("tick");
            bool sameGame = Loaded && wd.Str("game_id") == game.World.GameId;
            if (sameGame && tick < lastTick - 200) lastTick = -1; // the server went back (a restart from an older save): follow it
            if (sameGame && tick <= lastTick) return; // nothing newer than what's on screen
            int local = sameGame ? game.World.Tick : tick; float alpha = game.Alpha; // where the view's world is now (the snapshot is a fetch old)
            sw.Restart();
            game.Restore(root);
            game.World.Mirror = true; // the server decides the game; this copy only shows it
            if (local > tick && local - tick <= 40) game.CatchUp(local, alpha); // time never runs backwards on screen (events up to here already played)
            double restoreMs = sw.Elapsed.TotalMilliseconds;
            // The page's team may not be in this game (a seat from an earlier match, or not taken yet): watch everything until it is.
            int team = !seatEnded && askedTeam < game.World.Teams.Count ? askedTeam : -1;
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
            Debug.Log($"pez resync: tick={tick} bytes={text.Length} parse={parseMs:0.#}ms restore={restoreMs:0.#}ms rebind={rebindMs:0.#}ms ({(rebound ? "rebound" : "rebuilt")}) local={local}");
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

        void OnGUI()
        {
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            if (!Loaded) GUI.Label(new Rect(0, 0, Screen.width, Screen.height), error ?? Status ?? (Time.unscaledTime - startedAt > 8f ? "Still loading the game…" : "Loading game…"), style);
            if (showFps)
            {
                style.fontSize = 14; style.alignment = TextAnchor.MiddleRight;
                GUI.Label(new Rect(Screen.width - 130, 4, 124, 22), $"{labelFps:0} fps", style);
                style.fontSize = 22; style.alignment = TextAnchor.MiddleCenter;
            }
        }
    }
}
