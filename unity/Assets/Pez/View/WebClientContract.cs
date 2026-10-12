using System.Collections;
using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;
using UnityEngine.Networking;

namespace Pez.View
{
    /// <summary>
    /// What the browser view knows beyond the picture, for the HUD and the page: the gateway's meta (mode, delay, map name,
    /// how a seat ended), the command feed, and the commander's standing-orders box (an HTML overlay: the browser does text
    /// entry better than IMGUI). Everything here is absent, quietly, when the snapshot isn't a gateway URL
    /// (/view/&lt;v&gt;/snapshot or /watch[/n]/snapshot), e.g. on the dev server.
    /// </summary>
    public partial class WebClient
    {
        /// <summary>Set in Awake; null when not on the web.</summary>
        public static WebClient Instance;
        /// <summary>Watching with no team, or the seat has ended.</summary>
        public bool Spectator;
        /// <summary>45 for the delayed spectator feed, 0 live.</summary>
        public float DelaySeconds;
        /// <summary>How old the picture is: time since the last snapshot arrived plus the age it had when the gateway sent it.</summary>
        public float DataAge;
        /// <summary>One line for the HUD, or null when all is well: Reconnecting…, the waiting text, an expired-link message.</summary>
        public string Status;
        /// <summary>How this player's game ended (null while playing).</summary>
        public SeatResult Result;
        /// <summary>The command feed, oldest first, at most 12.</summary>
        public List<FeedItem> Feed = new List<FeedItem>();
        /// <summary>From meta; null = the HUD's default.</summary>
        public string MapName;
        /// <summary>A player view with ?c=CODE and a live seat: the commander box applies.</summary>
        public bool CanCommand => commander != null && baseUrl != null && baseUrl.Contains("/view/") && !Spectator;
        /// <summary>The old viewer's wording: Sending…, Sent: …, Cleared: …, Not sent: …</summary>
        public string OrdersStatus;

        public class SeatResult { public string Outcome; public int Kills; public float PlayedS; public string LastSeen; public string Rejoin; }
        public class FeedItem { public float TimeS; public int Team; public string Text; public bool? Done; public string Error; }

        const string Reconnecting = "Reconnecting…";
        // The old viewer's message for a view link whose seat is gone (the gateway sends the same words with its 401).
        const string ExpiredStatus = "Not connected: this view link isn't valid any more (its seat changed hands or the game restarted)";

        string baseUrl, commander, loggedStatus;
        bool seatEnded, metaNow, metaGone;
        float fetchedAt, snapAge;

        /// <summary>The snapshot URL as an absolute address (no cache-buster).</summary>
        string ResolvedUrl()
        {
            var u = url;
            try { if (u.IndexOf("://") < 0 && !string.IsNullOrEmpty(Application.absoluteURL)) u = new System.Uri(new System.Uri(Application.absoluteURL), u).ToString(); } catch { }
            return u;
        }

        /// <summary>The gateway's address for this view (the snapshot URL minus /snapshot), or null for anything else.</summary>
        void DeriveBase()
        {
            var u = ResolvedUrl();
            int q = u.IndexOf('?'); if (q >= 0) u = u.Substring(0, q);
            baseUrl = u.EndsWith("/snapshot") && (u.Contains("/view/") || u.Contains("/watch")) ? u.Substring(0, u.Length - "/snapshot".Length) : null;
        }

        void ContractAwake()
        {
            commander = Query("c") is string c && c.Length > 0 ? c : null;
            DeriveBase();
            // Nothing else may fall to the page while its text box has focus; the bridge flips this when it does.
            if (GameRunner.IsWeb) WebGLInput.captureAllKeyboardInput = true;
        }

        void StartContract()
        {
            DeriveBase();
            if (baseUrl == null) return;
            metaGone = false;
            StartCoroutine(MetaLoop());
            StartCoroutine(FeedLoop());
        }

        // ---------------------------------------------------------------- headers

        /// <summary>Delay, age and mode ride on every snapshot response.</summary>
        void NoteHeaders(UnityWebRequest req)
        {
            var d = req.GetResponseHeader("x-pezz-delay-s");
            if (d != null && float.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ds)) DelaySeconds = ds;
            var a = req.GetResponseHeader("x-pezz-snapshot-age-s");
            snapAge = a != null && float.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var age) ? age : 0f;
            var m = req.GetResponseHeader("x-pezz-mode");
            if (m == "spectator" && !seatEnded && baseUrl != null && baseUrl.Contains("/view/")) { SetSeatEnded(true); metaNow = true; }
        }

        /// <summary>The seat ended: the same address now serves the delayed room, seen by nobody in particular. A seat never comes back (a new join is a new link), so this only ever turns on.</summary>
        void SetSeatEnded(bool ended)
        {
            if (!ended || seatEnded) return;
            seatEnded = true;
            Debug.Log("pez team: the seat has ended: spectating the room");
            Spectator = true;
            lastTick = -1; // the delayed room is older than the last live picture
            expired = false; failures = 0;
            Status = null;
            Feed.Clear();
            wake = true;
        }

        static string ErrorOf(string body)
        {
            try { return (Json.Parse(body) as Dictionary<string, object>)?.Str("error"); } catch { return null; }
        }

        /// <summary>The "waiting" text of a 503 (or meta), with the seconds spelled out if that's all there is.</summary>
        static string WaitingText(string body)
        {
            try
            {
                if (!(Json.Parse(body) is Dictionary<string, object> d)) return null;
                if (d.Str("waiting") is string w && w.Length > 0) return w;
                float s = d.Num("starts_in_s");
                return float.IsNaN(s) ? null : $"Spectator feed starts in {Mathf.CeilToInt(s)}s.";
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- meta

        IEnumerator MetaLoop()
        {
            while (baseUrl != null && !metaGone)
            {
                using (var req = UnityWebRequest.Get(baseUrl + "/meta?_=" + System.DateTime.UtcNow.Ticks))
                {
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        try { ApplyMeta(req.downloadHandler.text); }
                        catch (System.Exception ex) { Debug.LogWarning("pez meta: " + ex.Message); }
                    }
                    else if (req.responseCode == 404) { metaGone = true; Debug.Log("pez meta: this gateway has none"); } // an older gateway: no spam
                }
                metaNow = false;
                float t = 0f;
                while (t < 10f && !metaNow) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
            }
        }

        void ApplyMeta(string text)
        {
            if (!(Json.Parse(text) is Dictionary<string, object> m) || !(m.TryGetValue("ok", out var ok) && ok is bool b && b)) return;
            bool ended = m.Str("mode") == "spectator" && baseUrl.Contains("/view/");
            if (m.Num("delay_s") is float ds && !float.IsNaN(ds)) DelaySeconds = ds;
            MapName = m.Str("map_name");
            if (!ended && askedTeam < 0 && m.Num("team") is float mt && mt >= 0) askedTeam = (int)mt; // a player view opened without team=
            if (m.TryGetValue("results", out var r) && r is Dictionary<string, object> rd)
            {
                Result = new SeatResult
                {
                    Outcome = rd.Str("outcome"), Kills = (int)Mathf.Max(0, rd.Num("kills", 0)), PlayedS = Mathf.Max(0, rd.Num("played_s", 0)),
                    LastSeen = rd.Str("last_seen"), Rejoin = m.Str("rejoin"),
                };
                ended = true; // a result is a seat that's over
            }
            else Result = null;
            if (ended) SetSeatEnded(true);
            if (!Loaded && Status == null) Status = m.Str("waiting"); // nothing on screen yet: say why
        }

        // ---------------------------------------------------------------- command feed

        IEnumerator FeedLoop()
        {
            var seen = new Dictionary<string, FeedItem>(); // an item keeps the game time it first got, however the clocks drift
            while (baseUrl != null && baseUrl.Contains("/view/"))
            {
                if (Loaded && !Spectator && !expired)
                {
                    using (var req = UnityWebRequest.Get(baseUrl + "/feed?_=" + System.DateTime.UtcNow.Ticks))
                    {
                        yield return req.SendWebRequest();
                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            try { ApplyFeed(req.downloadHandler.text, seen); }
                            catch (System.Exception ex) { Debug.LogWarning("pez feed: " + ex.Message); }
                        }
                    }
                }
                yield return new WaitForSecondsRealtime(1.5f);
            }
        }

        void ApplyFeed(string text, Dictionary<string, FeedItem> seen)
        {
            if (!(Json.Parse(text) is Dictionary<string, object> d) || !d.TryGetValue("items", out var arr) || !(arr is List<object> items)) return;
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float gameNow = Runner.Game.World.Time;
            var list = new List<FeedItem>();
            foreach (var o in items)
            {
                if (!(o is Dictionary<string, object> f)) continue;
                var t = f.Str("text") ?? "";
                var key = f.Str("at") + t;
                if (!seen.TryGetValue(key, out var item))
                {
                    float tm = f.Num("time_s"); // (the gateway may stamp game time itself)
                    if (float.IsNaN(tm)) tm = f.TryGetValue("at", out var at) && at is double ms ? gameNow - (nowMs - (long)ms) / 1000f : gameNow;
                    item = new FeedItem { TimeS = Mathf.Max(0f, tm), Team = Team, Text = t, Done = f.TryGetValue("ok", out var ok) && ok is bool b ? b : (bool?)null, Error = f.Str("error") };
                    seen[key] = item;
                }
                list.Add(item);
            }
            if (list.Count > 12) list.RemoveRange(0, list.Count - 12);
            if (seen.Count > 100) seen.Clear();
            Feed = list;
        }

        // ---------------------------------------------------------------- standing orders

        /// <summary>POST {c, text} to the gateway; empty text clears. Status lines are the old viewer's.</summary>
        public void SendOrders(string text)
        {
            if (!CanCommand) return;
            StartCoroutine(PostOrders(text ?? ""));
        }

        IEnumerator PostOrders(string text)
        {
            SetOrdersStatus("Sending…");
            var body = "{\"c\":" + Quote(commander) + ",\"text\":" + Quote(text) + "}";
            using (var req = new UnityWebRequest(baseUrl + "/orders", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("content-type", "application/json");
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    SetOrdersStatus(text.Length > 0 ? "Sent: your agent gets them right away, even mid-wait." : "Cleared: your agent uses its own judgment.");
                    PezOverlay.SetText(text, true); // what the agent has now is what the box shows
                }
                else SetOrdersStatus("Not sent: " + (ErrorOf(req.downloadHandler?.text) ?? req.error));
            }
        }

        static string Quote(string s) => Json.Write(s);

        void SetOrdersStatus(string s) { OrdersStatus = s; PezOverlay.SetStatus(s); }

        string shownOrders;
        /// <summary>The team's own standing orders from the snapshot fill the box, unless the player is in the middle of typing.</summary>
        void ShowStandingOrders(World w)
        {
            if (commander == null || Team < 0 || Team >= w.Teams.Count) return;
            var so = w.Teams[Team].StandingOrders ?? "";
            if (so == shownOrders) return;
            shownOrders = so;
            PezOverlay.SetText(so, false);
        }

        // ---------------------------------------------------------------- the commander box (HTML overlay)

        Rect sentRect; bool sentVisible; int sentW, sentH; float sentAt = -10f;

        /// <summary>The HUD's box in screen pixels (top-left origin); zero when it has none (no code, folded, spectating).</summary>
        Rect? HudBox() => Runner != null && Runner.Hud != null ? Runner.Hud.CommanderBoxRect : (Rect?)null;

        void UpdateContract()
        {
            DataAge = Loaded ? Time.unscaledTime - fetchedAt + snapAge : 0f;
            if (Status != loggedStatus) { loggedStatus = Status; Debug.Log("pez status: " + (Status ?? "ok")); }
            if (!GameRunner.IsWeb) return;
            bool show = CanCommand && Loaded && !Runner.InMenu;
            var box = HudBox() ?? Rect.zero;
            if (box.width < 8 || box.height < 8) show = false;
            // Only when it moved, plus now and then in case the page re-laid itself out.
            if (show == sentVisible && (!show || (box == sentRect && sentW == Screen.width && sentH == Screen.height)) && Time.unscaledTime - sentAt < 2f) return;
            sentRect = box; sentVisible = show; sentW = Screen.width; sentH = Screen.height; sentAt = Time.unscaledTime;
            PezOverlay.Box(box.x, box.y, box.width, box.height, show);
        }

        /// <summary>Page hook (the box's Send button): SendMessage("Pez", "OnCommanderSend", text).</summary>
        public void OnCommanderSend(string text) { if (text != null && text.Trim().Length > 0) SendOrders(text.Trim()); else if (text != null) SendOrders(""); }

        /// <summary>Page hook: the box's text area gained (1) or lost (0) focus. While it has it Unity must not see keys.</summary>
        public void OnCommanderFocus(string focused)
        {
            if (GameRunner.IsWeb) WebGLInput.captureAllKeyboardInput = focused != "1";
        }
    }

    /// <summary>The bridge to the page's commander box (Plugins/WebGL/PezOverlay.jslib; a no-op anywhere else).</summary>
    static class PezOverlay
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void PezCommanderBox(float x, float y, float w, float h, int visible, int screenW, int screenH);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void PezCommanderStatus(string text);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void PezCommanderText(string text, int force);
        /// <summary>The rect is in Unity screen pixels, top left origin; the page maps it onto the canvas (it knows the CSS scale).</summary>
        public static void Box(float x, float y, float w, float h, bool visible) => PezCommanderBox(x, y, w, h, visible ? 1 : 0, Screen.width, Screen.height);
        public static void SetStatus(string text) => PezCommanderStatus(text ?? "");
        public static void SetText(string text, bool force) => PezCommanderText(text ?? "", force ? 1 : 0);
#else
        public static void Box(float x, float y, float w, float h, bool visible) { }
        public static void SetStatus(string text) { }
        public static void SetText(string text, bool force) { }
#endif
    }
}
