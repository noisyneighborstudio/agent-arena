using System.Collections.Generic;
using System.Linq;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The browser view's half of the HUD (parity with the old player/spectator page, mcp/viewer.html): window-size
    /// layouts (narrow under 1100 CSS px, phone under 640), the team strip as a looping ticker, the read-only base panel,
    /// drawers, status and result cards. The native HUD keeps its layout; everything here is guarded by GameRunner.IsWeb
    /// except the team strip, which also stops dropping cards on a crowded native bar.
    /// </summary>
    public partial class Hud
    {
        // ------------------------------------------------------------------ window layout

        bool narrow, phone;
        float below = 92;      // y under the top bar (and its second row on a phone), where the columns start
        float mmCanvas = 360;  // the minimap's square, 216 in a narrow window and 150 on a phone
        float cssK = 1f;       // CSS pixels per design unit (the ticker's speed is in CSS pixels a second)
        float feedTop;         // top of the command feed this pass (the selection card stacks over it in a narrow window)
        float stackTop;        // top of the bottom-left stack (feed, selection card) in this pass; stackTopPrev is last pass's
        float stackTopPrev;
        float sideBottom, sideBottomPrev; // bottom of the right column's side panel (a phone puts the result card under it)
        /// <summary>Height of a drawer panel's header row and the gap under it.</summary>
        const float DrawerHeadH = 28 + 14;

        /// <summary>
        /// Where the commander text box sits in the side panel, in screen pixels (top-left origin, y down), for the page's
        /// HTML overlay to cover. Rect.zero when there is none: no command link, a folded drawer, or not a player's view.
        /// </summary>
        public Rect CommanderBoxRect { get; private set; }
        Rect pendingCommander;

#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern int PezHud_InnerWidth();
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern int PezHud_ScreenWidth();
        static float CssInnerWidth() => PezHud_InnerWidth();
        static float CssScreenWidth() => PezHud_ScreenWidth();
#else
        static float CssInnerWidth() => Screen.width;
        static float CssScreenWidth() => Screen.width;
#endif

        /// <summary>
        /// Screen pixels per design unit and the margins and columns for this window. Native: the board's 1920x1080 layout
        /// scaled to the window. Browser: the old page's fit(): the HUD's zoom (K) follows the window in CSS pixels, and the
        /// device pixel ratio (Unity's canvas is CSS size x ratio) turns that into pixels.
        /// </summary>
        void Layout()
        {
            stackTopPrev = stackTop;
            sideBottomPrev = sideBottom; sideBottom = 0;
            pendingCommander = Rect.zero;
            if (!GameRunner.IsWeb)
            {
                k = cssK = Mathf.Max(0.5f, Mathf.Min(Screen.height / 1080f, Screen.width / 1600f));
                SW = Screen.width / k; SH = Screen.height / k;
                narrow = phone = false;
                Edge = 16; RightW = 384; FeedW = 560; mmCanvas = 360; below = 92;
                stackTop = feedTop = SH - Edge;
                return;
            }
            float inner = Mathf.Max(1f, CssInnerWidth()), scr = CssScreenWidth();
            float dpr = Screen.width / inner, h = Screen.height / dpr;
            float w = Mathf.Min(inner, scr > 0 ? scr : inner); // (a phone's innerWidth grows to fit overflow, so the screen's width caps it)
            narrow = w < 1100; phone = w < 640;
            cssK = phone ? 0.85f : narrow ? Mathf.Max(0.78f, Mathf.Min(1f, w / 1100f)) : Mathf.Max(0.62f, Mathf.Min(1.2f, Mathf.Min(w / 1920f, h / 1000f)));
            k = cssK * dpr;
            SW = Screen.width / k; SH = Screen.height / k;
            Edge = narrow ? 8 : 16;
            RightW = phone ? Mathf.Min(384, SW - 2 * Edge) : narrow ? 300 : 384;
            FeedW = phone ? SW - 2 * Edge : Mathf.Min(560, SW - 2 * Edge);
            mmCanvas = phone ? 150 : narrow ? 216 : 360;
            below = Edge + 60 + (phone ? 8 + 60 + 10 : narrow ? 10 : 16);
            stackTop = feedTop = SH - Edge;
        }

        float mmPad => narrow ? 8 : 12;

        // The strip of the top row beside the minimap where banners and cards go: all of it on a wide window; in a narrow
        // one the minimap takes the right-hand corner, so they sit in what's left (they'd cover it otherwise).
        float BandL => narrow ? Edge : 0;
        float BandR => narrow ? SW - Edge - (mmCanvas + 2 * mmPad) - 8 : SW;
        float BandCx => (BandL + BandR) / 2;
        float BandW => BandR - BandL - (narrow ? 0 : 24);

        // ------------------------------------------------------------------ top bar text

        /// <summary>The brand panel's line: whose view this is, who is still playing, the map and the match clock.</summary>
        string WebWhere()
        {
            var wc = WebClient.Instance;
            int playing = W.Teams.Count(t => !t.Defeated && !t.Left);
            string who = Team < 0 || (wc != null && wc.Spectator)
                ? "Spectating" + (wc != null && wc.DelaySeconds > 0 ? $" · {Mathf.RoundToInt(wc.DelaySeconds)}s delay" : "")
                : W.Teams[Team].Name;
            string dims = $"{W.Map.W}×{W.Map.H}";
            if (!string.IsNullOrEmpty(wc?.MapName)) dims = wc.MapName + " " + dims;
            return $"{who} · {playing} playing · {dims}{MatchClock()}";
        }

        static float liveInterval = -1f;
        /// <summary>Seconds between snapshots (the page's live=), 0 for a static view.</summary>
        static float LiveInterval()
        {
            if (liveInterval < 0) liveInterval = float.TryParse(WebClient.Query("live"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? Mathf.Max(0, v) : 0;
            return liveInterval;
        }

        /// <summary>Connection trouble, loudly (reconnecting, waiting for the feed, an expired link); else, quietly, a stale picture.</summary>
        float StatusBanner(float y)
        {
            var wc = WebClient.Instance;
            if (!GameRunner.IsWeb || wc == null) return y;
            string msg = wc.Status;
            bool subtle = false;
            if (string.IsNullOrEmpty(msg))
            {
                float live = LiveInterval();
                // A spectator feed is meant to be DelaySeconds behind: only lateness beyond that is worth a word.
                float late = wc.DataAge - wc.DelaySeconds;
                if (live <= 0 || late <= 3 * live) return y;
                msg = $"data {Mathf.RoundToInt(late)}s late"; subtle = true;
            }
            float size = subtle ? 13 : 15, h = subtle ? 28 : 42;
            float w = Mathf.Min(BandW, TextW(msg, fSans, size) + (subtle ? 28 : 28 + 18 + 18));
            var r = new Rect(BandCx - w / 2, y, w, h);
            Round(r, subtle ? 8 : 10, PanelFill(0.92f), Line, 1f);
            uiRects.Add(r);
            if (!subtle) SolidShape(new Rect(r.x + 1, r.y + 1, 28, h - 2), Amber, 9, 0);
            float tx = subtle ? r.x + 14 : r.x + 28 + 18;
            Text(new Rect(tx, r.y, w - (tx - r.x) - 8, h), Ellipsize(msg, fSans, size, w - (tx - r.x) - 14), fSans, size, subtle ? Muted : Ink);
            return y + h + 8;
        }

        // ------------------------------------------------------------------ drawers

        /// <summary>A panel folded to its header or not. Remembered per drawer; until someone chooses, the default follows the window.</summary>
        bool DrawerOpen(string key)
        {
            string v = PlayerPrefs.GetString("pezz.drawer." + key, "");
            if (v.Length > 0) return v == "open";
            return !phone && !(Team < 0 && WebClient.Instance?.Result == null && key == "side"); // (a spectator's side panel holds only a note)
        }

        /// <summary>Header row of a drawer panel: the title, optional help text (the first variant that fits) and the fold button.</summary>
        void DrawerHead(Rect r, string key, string title, string[] helps = null, float top = 16)
        {
            const float pad = 16;
            var btn = new Rect(r.xMax - pad - 32, r.y + top, 32, 28);
            float tw = Spaced(r.x + pad, r.y + top + 5, 18, title, fHead, 13, Muted, 0.14f);
            if (helps != null && !narrow)
            {
                float room = btn.x - 10 - (r.x + pad + tw + 16);
                foreach (var help in helps)
                    if (TextW(help, fSans, 13) <= room) { Text(new Rect(btn.x - 10 - room, r.y + top, room, 28), help, fSans, 13, Muted, TextAnchor.MiddleRight); break; }
            }
            bool open = DrawerOpen(key);
            Round(btn, 7, Hover(btn) ? BtnHot : Btn, Line, 1f);
            var c = btn.center;
            var clip = new Rect(btn.x - 4, btn.y - 4, btn.width + 8, btn.height + 8);
            Vector2 a, b, d;
            if (open) { a = c + new Vector2(-4.4f, -2.2f); b = c + new Vector2(0, 2.4f); d = c + new Vector2(4.4f, -2.2f); }
            else { a = c + new Vector2(-2.2f, -4.4f); b = c + new Vector2(2.4f, 0); d = c + new Vector2(-2.2f, 4.4f); }
            Segment(a, b, clip, 1.8f, Cream); Segment(b, d, clip, 1.8f, Cream);
            uiRects.Add(btn);
            if (Click(btn)) { PlayerPrefs.SetString("pezz.drawer." + key, open ? "closed" : "open"); PlayerPrefs.Save(); }
        }

        string[] FeedHelp => Team < 0
            ? new[] { "Drag to pan · scroll to zoom · Q/E rotate · M whole map · click a sector to fly there", "Drag to pan · scroll to zoom · M whole map", "M whole map" }
            : new[] { "Drag to pan · scroll to zoom · Q/E rotate · F your base · M whole map · click a sector to fly there", "Drag to pan · scroll to zoom · F your base · M whole map", "F base · M whole map" };

        // ------------------------------------------------------------------ right column

        void WebRightColumn()
        {
            float mmP = mmCanvas + 2 * mmPad;
            float y = below;
            Minimap(new Rect(SW - Edge - mmP, y, mmP, mmP));
            y += mmP + Gap;
            // A narrow window stacks the feed and selection card at the bottom: the side panel gives way to them.
            float bottom = narrow ? stackTopPrev - 8 : SH - Edge;
            float sideW = RightW;
            var col = new Rect(SW - Edge - sideW, y, sideW, Mathf.Max(48, bottom - y));
            if (Team >= 0) BasePanel(col);
            else TeamPanel(col);
        }

        /// <summary>
        /// A player's own base in the browser: what's being built, the stockpile and power, and the standing orders. Read-only:
        /// the copy of the game here is a picture of the server's, so there are no build buttons and no placement.
        /// </summary>
        void BasePanel(Rect col)
        {
            var t = W.Teams[Team];
            var wc = WebClient.Instance;
            bool open = DrawerOpen("side"), canCommand = wc != null && wc.CanCommand;
            const float pad = 16, commanderH = 18 + 8 + 72 + 8 + 44 + 8 + 18;
            float inner = col.width - 2 * pad, top = open ? pad : 10;
            string orders = t.StandingOrders ?? "";
            string ordersText = orders.Length > 0 ? orders : "None yet: your agent is using its own judgment.";
            float ordersH = Mathf.Min(TextH(ordersText, fSans, 14, inner) * 1.08f, 110);
            float prot = t.ProtectedUntil - W.Time;
            var prod = open ? Production(t).Take(4).ToList() : new List<ProdRow>();
            float Height()
            {
                if (!open) return 2 * top + 28;
                float h = pad + DrawerHeadH + 28 + (prod.Count == 0 ? 20 : 52 * prod.Count) + 4 + 10 + 28 + 2 * 22 + 10 + 24 + 12 + 14 + 26 + ordersH;
                if (prot > 0) h += 8 + 18;
                if (canCommand) h += 10 + commanderH;
                return h + pad;
            }
            while (Height() > col.height && prod.Count > 0) prod.RemoveAt(prod.Count - 1);
            var r = new Rect(col.x, col.y, col.width, Mathf.Min(Height(), col.height));
            sideBottom = r.yMax;
            Panel(r);
            DrawerHead(r, "side", "YOUR BASE", null, top);
            if (!open) return;
            float x = r.x + pad, y = r.y + pad + DrawerHeadH;
            y = ProductionBlock(prod, x, y, inner) + 10;
            Head(x, y, "STOCKPILE"); y += 28;
            y = StockPower(t, x, y, inner);
            Head(x, y, "STANDING ORDERS"); y += 26;
            Text(new Rect(x, y, inner, ordersH), ordersText, fSans, 14, orders.Length > 0 ? Soft : Muted, TextAnchor.UpperLeft, true);
            y += ordersH;
            if (prot > 0)
            {
                y += 8;
                Text(new Rect(x, y, inner, 18), $"Newcomer protection: {FormatLong(prot)} left.", fSans, 13, Muted);
                y += 18;
            }
            if (canCommand)
            {
                // The page's text box and buttons sit here (an HTML overlay: typing is the browser's job).
                var gap = new Rect(x, y + 10, inner, commanderH);
                uiRects.Add(gap);
                pendingCommander = new Rect(Mathf.Round(gap.x * k), Mathf.Round(gap.y * k), Mathf.Round(gap.width * k), Mathf.Round(gap.height * k));
            }
        }

        // ------------------------------------------------------------------ team strip

        enum CardKind { Own, Rival, Spec, Full, Compact }

        readonly Dictionary<int, int> unitCount = new Dictionary<int, int>();
        bool stripCompact;
        float tickOff, tickLast;

        List<(string icon, string text)> CardStats(Sim.Team t, CardKind kind)
        {
            unitCount.TryGetValue(t.Id, out int n);
            var l = new List<(string, string)>();
            // A rival on a player's view is fogged: its stockpile, power and army are not in the copy, but its kills are public.
            if (kind == CardKind.Rival) l.Add(("kills", t.Stats.Kills.ToString()));
            else if (kind == CardKind.Compact) l.Add(("units", n.ToString()));
            else
            {
                l.Add(("power", $"{t.PowerUsed}/{t.PowerProduced}")); l.Add(("stock", Num(t.Amount("steel")))); l.Add(("units", n.ToString()));
                if (kind == CardKind.Spec) l.Add(("kills", t.Stats.Kills.ToString()));
            }
            if (phone && kind == CardKind.Own) l.RemoveRange(1, l.Count - 1); // (the old phone strip: name and power only)
            return l;
        }

        float CardNameSize(CardKind kind) => kind == CardKind.Rival ? 14 : 16;
        bool NameOnly(CardKind kind) => phone && kind == CardKind.Own;

        float CardW(Sim.Team t, CardKind kind)
        {
            float nm = TextW(t.Name, fSansBold, CardNameSize(kind));
            if (!NameOnly(kind)) nm = Mathf.Max(nm, TextW(Player(t.Id), fSans, 13));
            float w = 8 + 16 + nm + 18;
            foreach (var (_, text) in CardStats(t, kind)) w += 16 + 16 + 6 + TextW(text, fMono, 15);
            return w;
        }

        void DrawCard(Rect r, Sim.Team t, CardKind kind, bool register)
        {
            bool out_ = t.Defeated || t.Left;
            float was = fade;
            if (out_) fade *= 0.45f;
            if (register) Panel(r, 0.88f);
            else Round(r, 12, PanelFill(0.88f), Border, 1f); // (inside the ticker's clip group, whose own rect is the UI)
            SolidShape(new Rect(r.x + 1, r.y + 1, 8, r.height - 2), Mats.Team(t.Id), 11, 0);
            float cx = r.x + 8 + 16, y = r.y, nsz = CardNameSize(kind);
            float nameW = TextW(t.Name, fSansBold, nsz);
            if (NameOnly(kind))
            {
                Text(new Rect(cx, y, r.width, r.height), t.Name, fSansBold, nsz, Ink);
                if (out_) Fill(new Rect(cx, y + r.height / 2, nameW, 1.5f), Ink);
            }
            else
            {
                Text(new Rect(cx, y + 9, r.width, 22), t.Name, fSansBold, nsz, Ink);
                if (out_) Fill(new Rect(cx, y + 20, nameW, 1.5f), Ink);
                Text(new Rect(cx, y + 31, r.width, 18), out_ ? (t.Left ? "left" : "defeated") : Player(t.Id), fSans, 13, Muted);
                nameW = Mathf.Max(nameW, TextW(Player(t.Id), fSans, 13));
            }
            cx += nameW;
            foreach (var (icon, text) in CardStats(t, kind))
            {
                cx += 16;
                var ir = new Rect(cx, y + 22, 16, 16);
                if (icon == "kills") KillsIcon(ir);
                else Tex(ir, HudIcon(icon));
                cx += 22;
                Text(new Rect(cx, y, 120, r.height), text, fMono, 15, t.LowPower && icon == "power" ? Amber : Ink);
                cx += TextW(text, fMono, 15);
            }
            fade = was;
        }

        /// <summary>The old page's kills glyph: a ring with four ticks (a crosshair).</summary>
        void KillsIcon(Rect r)
        {
            Round(new Rect(r.x + 3, r.y + 3, 10, 10), 5, new Color(0, 0, 0, 0), Cream, 1.5f);
            Fill(new Rect(r.center.x - 0.75f, r.y + 0.5f, 1.5f, 3.5f), Cream); Fill(new Rect(r.center.x - 0.75f, r.yMax - 4f, 1.5f, 3.5f), Cream);
            Fill(new Rect(r.x + 0.5f, r.center.y - 0.75f, 3.5f, 1.5f), Cream); Fill(new Rect(r.xMax - 4f, r.center.y - 0.75f, 3.5f, 1.5f), Cream);
        }

        /// <summary>
        /// Whether a team still has a card on the strip: everyone playing, the viewer's own team, and a team that's out
        /// (defeated or left) for 30 s after it went out, by its last "defeated" or "left" event. No such event in the
        /// recent window means it went out long ago.
        /// </summary>
        bool OnStrip(Sim.Team t)
        {
            if ((!t.Defeated && !t.Left) || t.Id == Team) return true;
            for (int i = W.Events.Count - 1; i >= 0; i--)
            {
                var e = W.Events[i];
                if (e.Team == t.Id && (e.Type == "defeated" || e.Type == "left")) return (W.Tick - e.Tick) * World.Dt < 30f;
            }
            return false;
        }

        /// <summary>
        /// The team cards between `left` and `right`. A player's own card is pinned at the left (never clipped or scrolled),
        /// then "VS", then everyone else (a team that's out stays, struck through, for 30 s: OnStrip); when the rest don't fit they loop as a ticker (35 CSS px a second, paused under
        /// the pointer) instead of being dropped. Native cards pick the compact or full form once per width (with hysteresis,
        /// so a growing number doesn't flip it), and tick when even compact is too wide.
        /// </summary>
        void TeamStrip(float left, float right, float y, float h)
        {
            bool web = GameRunner.IsWeb;
            var teams = W.Teams.Where(OnStrip).ToList();
            if (teams.Count == 0) return;
            unitCount.Clear();
            foreach (var e in W.Entities)
                if (!e.Dead && e.Team >= 0 && !e.IsStructure && !e.IsMine) unitCount[e.Team] = (unitCount.TryGetValue(e.Team, out int c) ? c : 0) + 1;
            int pinned = web ? Team : -1;
            if (!web && Team >= 0) teams = teams.OrderBy(t => t.Id == Team ? 0 : 1).ToList();
            bool vsNative = !web && teams.Count == 2;
            float avail = right - left;

            CardKind Kind(Sim.Team t) => !web ? (stripCompact ? CardKind.Compact : CardKind.Full) : t.Id == pinned ? CardKind.Own : pinned >= 0 ? CardKind.Rival : CardKind.Spec;
            if (!web)
            {
                // Full form while it fits; compact only once it doesn't, and back only with room to spare (numbers growing a digit mustn't flip it).
                float full = teams.Sum(t => CardW(t, CardKind.Full)) + (teams.Count - 1) * Gap + (vsNative ? 24 + Gap : 0);
                stripCompact = stripCompact ? full > avail - 24 : full > avail;
            }

            float cg = web ? 10 : stripCompact ? 8 : Gap;
            float x = left;
            var pin = web && pinned >= 0 ? teams.FirstOrDefault(t => t.Id == pinned) : null;
            var rest = teams.Where(t => t != pin).ToList();
            if (pin != null)
            {
                float pw = CardW(pin, Kind(pin));
                DrawCard(new Rect(x, y, pw, h), pin, Kind(pin), true);
                x += pw + cg;
                if (rest.Count > 0)
                {
                    Text(new Rect(x, y, 24, h), "VS", fHead, 15, Muted, TextAnchor.MiddleCenter);
                    x += 24 + cg;
                }
            }
            var area = new Rect(x, y, Mathf.Max(0, right - x), h);
            var widths = rest.Select(t => CardW(t, Kind(t))).ToList();
            float vsExtra = vsNative ? 24 + Gap : 0;
            float width = widths.Sum() + (rest.Count - 1) * cg + vsExtra;
            if (width <= area.width + 1)
            {
                float cx = area.x + (area.width - width) / 2;
                for (int i = 0; i < rest.Count; i++)
                {
                    DrawCard(new Rect(cx, y, widths[i], h), rest[i], Kind(rest[i]), true);
                    cx += widths[i] + cg;
                    if (vsNative && i == 0) { Text(new Rect(cx, y, 24, h), "VS", fHead, 15, Muted, TextAnchor.MiddleCenter); cx += 24 + Gap; }
                }
                tickOff = 0;
                return;
            }

            // The ticker: two copies of the list, scrolled left and wrapped, clipped to the strip.
            uiRects.Add(area);
            float setW = widths.Sum() + rest.Count * cg;
            var ev = Event.current;
            if (ev.type == EventType.Repaint)
            {
                float now = Time.unscaledTime, dt = Mathf.Min(0.1f, now - tickLast);
                tickLast = now;
                if (!Hover(area)) tickOff = (tickOff + dt * 35f / cssK) % setW;
            }
            if (ev.type != EventType.Repaint) return;
            GUI.BeginGroup(P(area));
            for (int copy = 0; copy < 2; copy++)
            {
                float cx = -tickOff + copy * setW;
                for (int i = 0; i < rest.Count; i++)
                {
                    if (cx < area.width && cx + widths[i] > 0) DrawCard(new Rect(cx, 0, widths[i], h), rest[i], Kind(rest[i]), false);
                    cx += widths[i] + cg;
                }
            }
            GUI.EndGroup();
        }

        // ------------------------------------------------------------------ result cards

        static string OutcomeText(string outcome) => outcome switch
        {
            "eliminated" => "Eliminated",
            "resigned" => "Resigned (no way left to make progress)",
            "left" => "Left the arena",
            "game_over" or "ended" => "Game over for this seat",
            _ => outcome ?? "Game over",
        };

        /// <summary>
        /// Top-centre card for a finished seat (how it went, and that the view is now a delayed spectator's) and/or a match
        /// that ended on points (the winner and the scoreboard). Browser only.
        /// </summary>
        void ResultsCard(float y)
        {
            var res = WebClient.Instance?.Result;
            bool over = W.MatchOver;
            if (res == null && !over) return;
            var wc = WebClient.Instance;
            const float pad = 20;
            var scores = over ? W.Scores() : new List<MatchScore>();
            // Where it goes: centred on a wide window; beside the right column in a narrow one; under it on a phone.
            float cxMid = SW / 2, maxW = Mathf.Min(560, SW - 24);
            if (narrow && !phone) { cxMid = (Edge + SW - Edge - RightW - 8) / 2; maxW = Mathf.Min(560, SW - 2 * Edge - RightW - 8); }
            if (phone) { cxMid = SW / 2; maxW = SW - 2 * Edge; y = Mathf.Max(y, sideBottomPrev + 8); }
            // Column widths of the scoreboard: team, then four numbers.
            float teamW = scores.Count == 0 ? 0 : scores.Max(s => 16 + TextW($"{s.Name} · {s.Player}", fSans, 14));
            // (a phone's card is narrower than the table: the team column and the numbers give way)
            teamW = Mathf.Min(teamW, maxW - 2 * pad - 4 * 50);
            float numW = Mathf.Min(72, (maxW - 2 * pad - teamW) / 4), tableW = teamW + 4 * numW;
            string nowSpectating = $"Now spectating the whole room{(wc != null && wc.DelaySeconds > 0 ? $", {Mathf.RoundToInt(wc.DelaySeconds)}s behind" : "")}. {res?.Rejoin}";
            string statsLine = res == null ? null : $"{res.Kills} kills · played {FormatLong(res.PlayedS)}" + (string.IsNullOrEmpty(res.LastSeen) ? "" : $" · last seen {res.LastSeen}");
            float w = Mathf.Clamp(Mathf.Max(tableW, statsLine != null ? TextW(statsLine, fMono, 14) : 0, 360 - 2 * pad) + 2 * pad, Mathf.Min(360, maxW), maxW);
            if (phone) w = maxW;
            float inner = w - 2 * pad;
            string note = over ? $"Each column is a share of what all players still in held between them (100 each).{NewMatchText()}" : null;

            float Body(float top, bool draw, Rect r)
            {
                float cy = top;
                float cx = r.x + pad;
                if (res != null)
                {
                    if (draw) Spaced(r.center.x - Spaced(0, 0, 0, "YOUR GAME", fHead, 13, Muted, 0.14f, false) / 2, cy, 18, "YOUR GAME", fHead, 13, Muted, 0.14f);
                    cy += 22;
                    if (draw) Text(new Rect(cx, cy, inner, 30), Ellipsize(OutcomeText(res.Outcome), fHeadBold, 22, inner), fHeadBold, 22, Ink, TextAnchor.MiddleCenter);
                    cy += 30 + 4;
                    if (draw) Text(new Rect(cx, cy, inner, 20), statsLine, fMono, 14, Muted, TextAnchor.MiddleCenter);
                    cy += 20 + 8;
                    float nh = TextH(nowSpectating, fSans, 13, inner);
                    if (draw) Text(new Rect(cx, cy, inner, nh), nowSpectating, fSans, 13, Muted, TextAnchor.UpperCenter, true);
                    cy += nh;
                    if (over) cy += 12;
                }
                if (over)
                {
                    if (draw) Spaced(r.center.x - Spaced(0, 0, 0, "MATCH OVER: TIME", fHead, 13, Muted, 0.14f, false) / 2, cy, 18, "MATCH OVER: TIME", fHead, 13, Muted, 0.14f);
                    cy += 22;
                    string big = W.Winner >= 0 ? $"{W.Teams[W.Winner].Name} · {Player(W.Winner)} wins on points" : "A draw on points";
                    float bigW = TextW(big, fHeadBold, 20) + (W.Winner >= 0 ? 24 : 0);
                    if (draw)
                    {
                        float bx = r.center.x - bigW / 2;
                        if (W.Winner >= 0) { Round(new Rect(bx, cy + 8, 14, 14), 4, Mats.Team(W.Winner)); bx += 24; }
                        Text(new Rect(bx, cy, inner, 30), big, fHeadBold, 20, Ink);
                    }
                    cy += 30 + 6;
                    float tx = r.center.x - tableW / 2;
                    if (draw)
                    {
                        Text(new Rect(tx, cy, teamW, 20), "Team", fSansBold, 13, Muted);
                        string[] cols = { "Territory", "Economy", "Kills", "Score" };
                        for (int i = 0; i < 4; i++) Text(new Rect(tx + teamW + i * numW, cy, numW, 20), cols[i], fSansBold, 13, Muted, TextAnchor.MiddleRight);
                    }
                    cy += 22;
                    foreach (var s in scores)
                    {
                        if (draw)
                        {
                            Round(new Rect(tx, cy + 5, 10, 10), 3, Mats.Team(s.Team));
                            Text(new Rect(tx + 16, cy, teamW, 20), Ellipsize($"{s.Name} · {s.Player}", fSans, 14, teamW - 16), fSans, 14, Ink);
                            float[] v = { s.TerritoryPoints, s.EconomyPoints, s.KillPoints, s.Score };
                            for (int i = 0; i < 4; i++) Text(new Rect(tx + teamW + i * numW, cy, numW, 20), v[i].ToString("0"), i == 3 ? fMonoMed : fMono, 14, i == 3 ? Ink : Soft, TextAnchor.MiddleRight);
                        }
                        cy += 22;
                    }
                    cy += 6;
                    float nh = TextH(note, fSans, 13, inner);
                    if (draw) Text(new Rect(cx, cy, inner, nh), note, fSans, 13, Muted, TextAnchor.UpperCenter, true);
                    cy += nh;
                }
                return cy;
            }

            var probe = new Rect(cxMid - w / 2, y, w, 0);
            float h = Body(y + pad, false, probe) - y + pad;
            var card = new Rect(cxMid - w / 2, y, w, Mathf.Min(h, SH - y - Edge));
            Panel(card, 0.94f);
            Body(card.y + pad, true, card);
        }

        /// <summary>" A new match starts in m:ss: join again for a seat." (an open arena, once the results have been up a while).</summary>
        string NewMatchText()
        {
            float next = W.NextMatchIn >= 0 ? W.NextMatchIn : Runner.Game.RestartIn;
            return next >= 0 ? $" A new match starts in {FormatLong(next)}: join again for a seat." : "";
        }
    }
}
