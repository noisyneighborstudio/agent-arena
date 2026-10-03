using System.Collections.Generic;
using System.Linq;
using Pez.Sim;
using UnityEngine;
using Terrain = Pez.Sim.Terrain;

namespace Pez.View
{
    /// <summary>
    /// IMGUI HUD built to the art pack's HUD board (06_HUD, design/HUD.dc.html) from the HUD kit (08_HUDKit): top bar with
    /// team cards and speed, alert banner, north-up minimap, team panel (building now, standing orders) or the player's
    /// build panel, command feed, selection card, plus the main menu and the game-over banner.
    /// Laid out in 1920x1080 design units and drawn in screen pixels (k pixels per unit), so text stays crisp at any size.
    /// Panels are rounded 9-slices from textures generated at runtime for the current scale.
    /// </summary>
    public class Hud : MonoBehaviour
    {
        public GameRunner Runner;
        World W => Runner.Game.World;
        int Team => Runner.HumanTeam;

        /// <summary>True while a text field has keyboard focus, so camera keys and hotkeys stand down.</summary>
        public static bool Typing;
        /// <summary>Playing: show the team / standing-orders panel instead of the build panel (O).</summary>
        public bool ShowOrders;

        // ------------------------------------------------------------------ design tokens (HUD kit)
        static readonly Color Ink = new Color32(232, 235, 238, 255), Muted = new Color32(155, 165, 174, 255), Soft = new Color32(197, 204, 210, 255);
        static readonly Color Cream = Mats.Cream, Amber = Mats.Amber, Night = new Color32(16, 19, 23, 255), Btn = new Color32(34, 40, 46, 255);
        static readonly Color Border = new Color32(42, 49, 56, 255), Line = new Color32(58, 64, 71, 255), Liquorice = new Color32(30, 27, 29, 255);
        static readonly Color ButtonBg = new Color32(20, 23, 26, 255), ButtonHover = new Color32(28, 33, 38, 255), InfoGrey = new Color32(74, 79, 87, 255);
        static readonly Color Fuel = new Color32(143, 228, 255, 255), BtnHot = new Color32(46, 53, 60, 255);
        /// <summary>The kit's panel fill rgba(16,19,23,a), with the alpha raised to look like the board's gamma-space blend.</summary>
        static Color PanelFill(float a = 0.9f) => new Color(16 / 255f, 19 / 255f, 23 / 255f, 1f - (1f - a) * 0.45f);
        const float Edge = 16f, Gap = 12f, RightW = 384f, FeedW = 560f, SelW = 520f;
        const string MapName = "Sugar Flats";

        float k = 1f;      // screen pixels per design unit
        float SW, SH;      // screen size in design units
        float fade = 1f;   // multiplies every colour drawn (defeated teams' cards)
        Texture2D white;
        Font fHead, fHeadBold, fSans, fSansBold, fMono, fMonoMed;
        GUIStyle fieldStyle;
        int fieldPx = -1;

        // Panels and fields drawn this pass, for IsOverUi and for dropping text focus on a click elsewhere.
        readonly List<Rect> uiRects = new List<Rect>(), fieldRects = new List<Rect>();
        List<Rect> lastUi = new List<Rect>();
        readonly List<(Rect r, string sector)> sectorHits = new List<(Rect, string)>();
        readonly Dictionary<int, string> drafts = new Dictionary<int, string>();
        int tabTeam = -1;
        bool buildUnits;
        string hoverKey;
        Rect hoverRect;

        // Minimap
        Texture2D minimap;
        Color[] miniPixels;
        float nextMini;
        Rect miniMapRect;

        // ------------------------------------------------------------------ icons
        static readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
        /// <summary>Art-pack icon (Resources/PezIcons/<key>.png), or null.</summary>
        public static Texture2D Icon(string key)
        {
            if (!icons.TryGetValue(key, out var t)) icons[key] = t = Resources.Load<Texture2D>("PezIcons/" + key);
            return t;
        }
        Texture2D HudIcon(string key) => UiIcon("hud/" + key);

        // IMGUI here shows a texture's stored values as if they were linear (see L), so imported sRGB icons would come
        // out washed. Each icon is converted once on the GPU into a copy that stores linear values.
        readonly Dictionary<string, Texture2D> linIcons = new Dictionary<string, Texture2D>();
        readonly HashSet<string> linWanted = new HashSet<string>();

        Texture2D UiIcon(string key)
        {
            if (linIcons.TryGetValue(key, out var t)) return t;
            var src = Icon(key);
            if (src != null) linWanted.Add(key);
            return src;
        }

        void LateUpdate()
        {
            if (linWanted.Count == 0) return;
            var prev = RenderTexture.active;
            foreach (var key in linWanted)
            {
                var src = Icon(key);
                if (src == null) continue;
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(src, rt); // samples sRGB (decoded) into a linear target: the bytes are linear
                RenderTexture.active = rt;
                var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                t.Apply(true);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                linIcons[key] = t;
            }
            RenderTexture.active = prev;
            linWanted.Clear();
        }

        // ------------------------------------------------------------------ public queries

        public bool IsOverUi(Vector2 mouse)
        {
            if (Runner.InMenu) return true;
            var p = new Vector2(mouse.x, Screen.height - mouse.y) / k;
            foreach (var r in lastUi) if (r.Contains(p)) return true;
            return false;
        }

        bool IsAgentTeam(int t) => t >= 0 && t < W.Teams.Count && W.Teams[t].Controller == "llm";

        // ------------------------------------------------------------------ frame

        void OnGUI()
        {
            if (Runner == null || Runner.Game == null) return;
            Setup();
            var ev = Event.current;
            k = Mathf.Max(0.5f, Mathf.Min(Screen.height / 1080f, Screen.width / 1600f));
            SW = Screen.width / k; SH = Screen.height / k;

            // Clicking anywhere but a text box drops its focus; Escape does too.
            var mouse = ev.mousePosition / k;
            if (ev.type == EventType.MouseDown && !fieldRects.Any(r => r.Contains(mouse))) GUIUtility.keyboardControl = 0;
            if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape) GUIUtility.keyboardControl = 0;
            Typing = GUIUtility.keyboardControl != 0;
            uiRects.Clear(); fieldRects.Clear(); sectorHits.Clear();
            hoverKey = null;

            if (Runner.InMenu) Menu();
            else if (Runner.View != null)
            {
                WorldOverlays();
                DragBox();
                TopBar();
                float y = 92;
                y = JoinPanel(y);
                AlertBanner(y);
                RightColumn();
                Feed();
                Selection();
                BuildTooltip();
                if (W.GameOver) GameOver();
            }
            if (ev.type == EventType.Repaint) lastUi = new List<Rect>(uiRects);
        }

        void Setup()
        {
            if (white != null) return;
            white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            white.SetPixel(0, 0, Color.white); white.Apply();
            var fallback = GUI.skin.font;
            Font Load(string n) { var f = Resources.Load<Font>("Fonts/" + n); return f != null ? f : fallback; }
            fHead = Load("ChakraPetch-SemiBold"); fHeadBold = Load("ChakraPetch-Bold");
            fSans = Load("IBMPlexSans-Regular"); fSansBold = Load("IBMPlexSans-SemiBold");
            fMono = Load("IBMPlexMono-Regular"); fMonoMed = Load("IBMPlexMono-Medium");
        }

        // ------------------------------------------------------------------ drawing primitives (design units in, pixels out)

        Rect P(Rect r)
        {
            float x0 = Mathf.Round(r.x * k), y0 = Mathf.Round(r.y * k);
            return new Rect(x0, y0, Mathf.Round(r.xMax * k) - x0, Mathf.Round(r.yMax * k) - y0);
        }
        Color F(Color c) { c.a *= fade; return c; }
        static bool Repaint => Event.current.type == EventType.Repaint;

        // Colour management: in this linear-space player IMGUI takes GUI.color as linear (only text colours are converted),
        // so every textured draw is a white mask tinted with L(c), the kit's sRGB colour converted to linear. Panel
        // translucency is compensated in PanelFill, because linear-space blending lets more of the world through than
        // the board's (browser, gamma-space) rgba panels.
        Color L(Color c) { var l = c.linear; l.a = c.a * fade; return l; }
        static Texture2D Mask(int w, int h, FilterMode filter = FilterMode.Point) =>
            new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };

        void Fill(Rect r, Color c)
        {
            if (!Repaint) return;
            var o = GUI.color; GUI.color = L(c); GUI.DrawTexture(P(r), white); GUI.color = o;
        }

        /// <summary>An imported (sRGB) texture such as an icon: drawn untinted, with optional alpha.</summary>
        void Tex(Rect r, Texture t, float alpha = 1f, ScaleMode mode = ScaleMode.StretchToFill)
        {
            if (!Repaint || t == null) return;
            var o = GUI.color; GUI.color = new Color(1, 1, 1, alpha * fade); GUI.DrawTexture(P(r), t, mode); GUI.color = o;
        }

        void Tinted(Rect pr, Texture t, Color c)
        {
            var o = GUI.color; GUI.color = L(c); GUI.DrawTexture(pr, t); GUI.color = o;
        }

        /// <summary>Anti-aliased coverage of pixel (x, y) in a w x h rounded box with left/right corner radii.</summary>
        static float Coverage(int x, int y, int w, int h, float rl, float rr)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float rad = px < w / 2f ? rl : rr;
            if (rad <= 0) return 1f;
            float cx = Mathf.Clamp(px, rad, w - rad), cy = Mathf.Clamp(py, rad, h - rad);
            float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - rad;
            return Mathf.Clamp01(0.5f - d);
        }

        readonly Dictionary<(int, int), (Texture2D fill, Texture2D border)> roundTex = new Dictionary<(int, int), (Texture2D, Texture2D)>();

        /// <summary>
        /// Rounded rectangle with an optional inner border: runtime 9-slices (a fill mask and a border mask, generated
        /// for the current pixel radius) whose corners draw 1:1 in pixels and whose edges and centre stretch.
        /// </summary>
        void Round(Rect r, float radius, Color fill, Color border = default, float bw = 0f)
        {
            if (!Repaint) return;
            var pr = P(r);
            if (pr.width < 1 || pr.height < 1) return;
            int rp = Mathf.Max(1, Mathf.RoundToInt(Mathf.Min(radius * k, pr.width / 2f, pr.height / 2f)));
            int bp = bw > 0 ? Mathf.Max(1, Mathf.RoundToInt(bw * k)) : 0;
            if (!roundTex.TryGetValue((rp, bp), out var tex))
            {
                int n = rp * 2 + 2;
                var fp = new Color32[n * n]; var bpx = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float outer = Coverage(x, y, n, n, rp, rp);
                        float inner = bp > 0 ? InnerCoverage(x, y, n, rp, bp) : 1f;
                        fp[y * n + x] = new Color32(255, 255, 255, (byte)(255 * outer * inner));
                        bpx[y * n + x] = new Color32(255, 255, 255, (byte)(255 * outer * (1f - inner)));
                    }
                tex = (Mask(n, n), Mask(n, n));
                tex.fill.SetPixels32(fp); tex.fill.Apply();
                tex.border.SetPixels32(bpx); tex.border.Apply();
                roundTex[(rp, bp)] = tex;
            }
            if (fill.a > 0) NineSlice(pr, tex.fill, rp, fill);
            if (bp > 0 && border.a > 0) NineSlice(pr, tex.border, rp, border);
        }

        void NineSlice(Rect pr, Texture2D tex, int rp, Color c)
        {
            int m = tex.width;
            float u1 = rp / (float)m, u2 = (m - rp) / (float)m;
            float[] xs = { pr.x, pr.x + rp, pr.xMax - rp, pr.xMax }, ys = { pr.y, pr.y + rp, pr.yMax - rp, pr.yMax }, us = { 0, u1, u2, 1 };
            var o = GUI.color; GUI.color = L(c);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    var dst = new Rect(xs[i], ys[j], xs[i + 1] - xs[i], ys[j + 1] - ys[j]);
                    // Texture rows run bottom-up, screen rows top-down: row j samples from the top of the texture.
                    if (dst.width > 0 && dst.height > 0) GUI.DrawTextureWithTexCoords(dst, tex, new Rect(us[i], 1f - us[j + 1], us[i + 1] - us[i], us[j + 1] - us[j]));
                }
            GUI.color = o;
        }

        static float InnerCoverage(int x, int y, int n, int rp, int bp)
        {
            // The same rounded box shrunk by the border width (radius shrinks with it).
            float px = x + 0.5f - bp, py = y + 0.5f - bp, w = n - 2 * bp, rad = Mathf.Max(0, rp - bp);
            if (px < 0 || py < 0 || px > w || py > w) return 0f;
            float cx = Mathf.Clamp(px, rad, w - rad), cy = Mathf.Clamp(py, rad, w - rad);
            float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - rad;
            float edge = Mathf.Min(Mathf.Min(px, py), Mathf.Min(w - px, w - py));
            return rad > 0 ? Mathf.Clamp01(0.5f - d) : Mathf.Clamp01(edge + 0.5f);
        }

        /// <summary>HUD kit panel: rgba(16,19,23,.9), 1 px #2A3138 border, radius 12. Registers it as UI.</summary>
        void Panel(Rect r, float alpha = 0.9f, float radius = 12f)
        {
            Round(r, radius, PanelFill(alpha), Border, 1f);
            uiRects.Add(r);
        }

        // Shaped masks (team stripes, hazard bands) are baked per pixel size and cached.
        readonly Dictionary<string, Texture2D> shapes = new Dictionary<string, Texture2D>();

        Texture2D Shape(string key, Rect pr, float rl, float rr, System.Func<int, int, int, int, float> alpha)
        {
            int w = Mathf.Max(1, (int)pr.width), h = Mathf.Max(1, (int)pr.height);
            var full = $"{key}:{w}x{h}:{rl:0}:{rr:0}";
            if (shapes.TryGetValue(full, out var t)) return t;
            t = Mask(w, h);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(255 * alpha(x, y, w, h) * Coverage(x, y, w, h, rl, rr)));
            t.SetPixels32(px); t.Apply();
            if (shapes.Count > 400) { foreach (var old in shapes.Values) Destroy(old); shapes.Clear(); }
            shapes[full] = t;
            return t;
        }

        /// <summary>Solid fill with rounded left/right corners (team stripes and alert tabs inside rounded panels).</summary>
        void SolidShape(Rect r, Color c, float rl, float rr)
        {
            if (!Repaint) return;
            var pr = P(r);
            Tinted(pr, Shape("solid", pr, rl * k, rr * k, (x, y, w, h) => 1f), c);
        }

        /// <summary>The critical hazard stripe: licorice and cream bands at 135 degrees (never red, which reads as Cherry).</summary>
        void Hazard(Rect r, float band, float rl = 0f, float rr = 0f)
        {
            if (!Repaint) return;
            var pr = P(r);
            int period = Mathf.Max(2, Mathf.RoundToInt(band * 2 * k));
            Tinted(pr, Shape("solid", pr, rl * k, rr * k, (x, y, w, h) => 1f), Liquorice);
            Tinted(pr, Shape("stripe" + period, pr, rl * k, rr * k, (x, y, w, h) => (((x - y) % period + period) % period) < period / 2 ? 0f : 1f), Cream);
        }

        // ---- text

        readonly Dictionary<(Font, int, TextAnchor, bool), GUIStyle> styles = new Dictionary<(Font, int, TextAnchor, bool), GUIStyle>();
        readonly Dictionary<(Font, int, char), float> charW = new Dictionary<(Font, int, char), float>();

        GUIStyle St(Font f, float size, TextAnchor a = TextAnchor.MiddleLeft, bool wrap = false)
        {
            int px = Mathf.Max(6, Mathf.RoundToInt(size * k));
            var key = (f, px, a, wrap);
            if (!styles.TryGetValue(key, out var s))
                styles[key] = s = new GUIStyle { font = f, fontSize = px, alignment = a, wordWrap = wrap, richText = true, clipping = TextClipping.Overflow, padding = new RectOffset() };
            return s;
        }

        void Text(Rect r, string s, Font f, float size, Color c, TextAnchor a = TextAnchor.MiddleLeft, bool wrap = false)
        {
            if (!Repaint || string.IsNullOrEmpty(s)) return;
            var st = St(f, size, a, wrap);
            st.normal.textColor = F(c);
            GUI.Label(P(r), s, st);
        }

        float TextW(string s, Font f, float size) => string.IsNullOrEmpty(s) ? 0 : St(f, size).CalcSize(new GUIContent(s)).x / k;
        float TextH(string s, Font f, float size, float width) => St(f, size, TextAnchor.UpperLeft, true).CalcHeight(new GUIContent(s), width * k) / k;

        float CharW(char ch, Font f, float size)
        {
            int px = Mathf.Max(6, Mathf.RoundToInt(size * k));
            if (!charW.TryGetValue((f, px, ch), out var w)) charW[(f, px, ch)] = w = St(f, size).CalcSize(new GUIContent(ch.ToString())).x / k;
            return w;
        }

        /// <summary>Letter-spaced caps (the kit's Chakra Petch labels, 0.12-0.14 em). Draws when c.a > 0; returns the width.</summary>
        float Spaced(float x, float y, float h, string s, Font f, float size, Color c, float em, bool draw = true)
        {
            float cx = x, sp = em * size;
            foreach (char ch in s)
            {
                float w = CharW(ch, f, size);
                if (draw) Text(new Rect(cx, y, w + 4, h), ch.ToString(), f, size, c);
                cx += w + sp;
            }
            return Mathf.Max(0, cx - x - sp);
        }

        void Head(float x, float y, string s) => Spaced(x, y, 18, s, fHead, 13, Muted, 0.14f);

        string Ellipsize(string s, Font f, float size, float width)
        {
            if (TextW(s, f, size) <= width) return s;
            while (s.Length > 1 && TextW(s + "…", f, size) > width) s = s.Substring(0, s.Length - 1);
            return s.TrimEnd() + "…";
        }

        // ---- interaction

        bool Hover(Rect r) => r.Contains(Event.current.mousePosition / k);

        /// <summary>A click on r (left by default; button 1 = right). Consumes the event.</summary>
        bool Click(Rect r, int button = 0)
        {
            var ev = Event.current;
            if (ev.type != EventType.MouseDown || ev.button != button || !Hover(r)) return false;
            ev.Use();
            return true;
        }

        /// <summary>HUD kit button: filled (cream) or quiet (#22282E / outline). Returns true on click.</summary>
        bool Button(Rect r, string label, bool primary, bool enabled = true, bool outline = false, float size = 15f, Font font = null)
        {
            bool hover = enabled && Hover(r);
            float was = fade;
            if (!enabled) fade *= 0.4f;
            if (primary) Round(r, 8, hover ? Color.white : Cream);
            else if (outline) Round(r, 8, hover ? Btn : new Color(0, 0, 0, 0), Line, 1f);
            else Round(r, 8, hover ? BtnHot : Btn);
            Text(r, label, font ?? (primary ? fSansBold : fSans), size, primary ? Night : Ink, TextAnchor.MiddleCenter);
            fade = was;
            return enabled && Click(r);
        }

        /// <summary>A multi-line text box in the kit's field style (#101317, #3A4047 border, radius 8).</summary>
        string Field(Rect r, string value, bool multiline, int id)
        {
            Round(r, 8, Night, Line, 1f);
            int px = Mathf.RoundToInt(14 * k);
            if (fieldStyle == null || fieldPx != px)
            {
                fieldPx = px;
                fieldStyle = new GUIStyle(GUI.skin.textArea) { font = fSans, fontSize = px, wordWrap = true, richText = false, padding = new RectOffset(Mathf.RoundToInt(12 * k), Mathf.RoundToInt(12 * k), Mathf.RoundToInt(10 * k), Mathf.RoundToInt(10 * k)) };
                foreach (var s in new[] { fieldStyle.normal, fieldStyle.hover, fieldStyle.focused, fieldStyle.active, fieldStyle.onNormal, fieldStyle.onFocused })
                {
                    s.background = null; s.textColor = Ink;
                }
                fieldStyle.border = new RectOffset();
            }
            GUI.skin.settings.cursorColor = Cream;
            GUI.skin.settings.selectionColor = new Color(Cream.r, Cream.g, Cream.b, 0.3f);
            fieldRects.Add(r);
            uiRects.Add(r);
            GUI.SetNextControlName("pezfield" + id);
            return multiline ? GUI.TextArea(P(r), value ?? "", fieldStyle) : GUI.TextField(P(r), value ?? "", fieldStyle);
        }

        static string FormatTime(float s) => $"{(int)s / 60:00}:{(int)s % 60:00}";
        static string FormatLong(float s) { int n = Mathf.CeilToInt(Mathf.Max(0, s)); return n >= 3600 ? $"{n / 3600}:{n % 3600 / 60:00}:{n % 60:00}" : $"{n / 60}:{n % 60:00}"; }

        /// <summary>The match clock for the top bar: the stage now (past normal play) and what comes next.</summary>
        string MatchClock()
        {
            var next = W.NextStage();
            if (next == null) return W.Phase == MatchPhase.Ended ? " · match over" : "";
            string what = next.Value.phase switch { "sudden_death" => "sudden death", "decay" => "decay", _ => "match ends" };
            string now = W.Phase == MatchPhase.SuddenDeath ? " · SUDDEN DEATH" : W.Phase == MatchPhase.Decay ? " · DECAY" : "";
            return $"{now} · {what} in {FormatLong(next.Value.at - W.Time)}";
        }
        static string Num(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        string Flavor(int t) => t >= 0 && t < W.Teams.Count ? W.Teams[t].Name : "Server";
        string Player(int t) => t >= 0 && t < W.Teams.Count ? (W.Teams[t].PlayerName ?? ControllerName(W.Teams[t].Controller)) : "Arena";
        static string ControllerName(string c) => c switch { "human" => "Human", "ai" => "Scripted AI", "llm" => "Agent", _ => c };
        static string Sentence(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
        static string Pretty(string key) => Sentence(key.Replace('_', ' '));

        // ------------------------------------------------------------------ top bar

        void TopBar()
        {
            const float y = 16, h = 60;
            // Brand: PEZ | clock, map and players.
            string clock = FormatTime(W.Time);
            string where = $"{MapName} · {W.ActivePlayers} player{(W.ActivePlayers == 1 ? "" : "s")} · {W.Map.W}×{W.Map.H}{MatchClock()}";
            float wPez = Spaced(0, 0, 0, "PEZ", fHeadBold, 24, Ink, 0.04f, false);
            float bw = 20 + wPez + 16 + 1 + 16 + TextW(clock, fMonoMed, 22) + 16 + TextW(where, fSans, 14) + 20;
            var brand = new Rect(Edge, y, bw, h);
            Panel(brand, 0.88f);
            float x = brand.x + 20;
            x += Spaced(x, y + 21, 18, "PEZ", fHeadBold, 24, Ink, 0.04f) + 16;
            Fill(new Rect(x, y + 16, 1, 28), Border); x += 17;
            Text(new Rect(x, y, 120, h), clock, fMonoMed, 22, Ink); x += TextW(clock, fMonoMed, 22) + 16;
            Text(new Rect(x, y, 400, h), where, fSans, 14, Muted);

            // Speed: pause, 1x 2x 4x, fog.
            var game = Runner.Game;
            string fog = Team >= 0 ? "Fog on" : "Fog off";
            float sw = 8 + 44 + 3 * 50 + 6 + 1 + 12 + TextW(fog, fSans, 14) + 8 + 8;
            var speed = new Rect(SW - Edge - sw, y, sw, h);
            Panel(speed, 0.88f);
            x = speed.x + 8;
            var pr = new Rect(x, y + 8, 44, 44);
            bool hoverP = Hover(pr);
            Round(pr, 8, game.Paused ? Cream : hoverP ? BtnHot : Btn);
            // Paused: cream button with the bars drawn dark (the icon itself is cream).
            if (game.Paused) { Fill(new Rect(pr.x + 15, pr.y + 15, 4.5f, 14), Night); Fill(new Rect(pr.x + 24.5f, pr.y + 15, 4.5f, 14), Night); }
            else Tex(new Rect(pr.x + 14, pr.y + 14, 16, 16), HudIcon("pause"));
            if (Click(pr)) game.Paused = !game.Paused;
            x += 50;
            foreach (var s in new[] { 1f, 2f, 4f })
            {
                var r = new Rect(x, y + 8, 44, 44);
                bool on = !game.Paused && Mathf.Abs(game.Speed - s) < 0.01f;
                Round(r, 8, on ? Cream : Hover(r) ? BtnHot : Btn);
                Text(r, $"{s:0}×", on ? fMonoMed : fMono, 15, on ? Night : Ink, TextAnchor.MiddleCenter);
                if (Click(r)) { game.Speed = s; game.Paused = false; }
                x += 50;
            }
            Fill(new Rect(x, y + 16, 1, 28), Border);
            Text(new Rect(x + 13, y, 100, h), fog, fSans, 14, Muted);

            // Team cards, centred between the two.
            TeamCards(brand.xMax + Gap, speed.x - Gap, y, h);
        }

        void TeamCards(float left, float right, float y, float h)
        {
            var teams = W.Teams.Where(t => !t.Left || W.Teams.Count <= 4).ToList();
            if (Team >= 0) teams = teams.OrderBy(t => t.Id == Team ? 0 : 1).ToList();
            var power = HudIcon("power"); var stock = HudIcon("stock"); var units = HudIcon("units");
            List<(string icon, string text)> Stats(Sim.Team t, bool full)
            {
                int n = W.Entities.Count(e => !e.Dead && e.Team == t.Id && !e.IsStructure && !e.IsMine);
                if (!full) return new List<(string, string)> { ("units", n.ToString()) };
                return new List<(string, string)> { ("power", $"{t.PowerUsed}/{t.PowerProduced}"), ("stock", Num(t.Amount("steel"))), ("units", n.ToString()) };
            }
            float CardW(Sim.Team t, bool full)
            {
                float w = 8 + 16 + Mathf.Max(TextW(t.Name, fSansBold, 16), TextW(Player(t.Id), fSans, 13)) + 18;
                foreach (var (_, text) in Stats(t, full)) w += 16 + 16 + 6 + TextW(text, fMono, 15);
                return w;
            }
            bool vs = teams.Count == 2;
            float avail = right - left;
            bool full = true;
            float total = teams.Sum(t => CardW(t, true)) + (teams.Count - 1) * Gap + (vs ? 24 + Gap : 0);
            if (total > avail) { full = false; total = teams.Sum(t => CardW(t, false)) + (teams.Count - 1) * 8; }
            float x = left + Mathf.Max(0, (avail - total) / 2);
            for (int i = 0; i < teams.Count; i++)
            {
                var t = teams[i];
                float w = CardW(t, full);
                if (x + w > right) break;
                bool out_ = t.Defeated || t.Left;
                float was = fade;
                if (out_) fade *= 0.45f;
                var r = new Rect(x, y, w, h);
                Panel(r, 0.88f);
                SolidShape(new Rect(x + 1, y + 1, 8, h - 2), Mats.Team(t.Id), 11, 0);
                float cx = x + 8 + 16;
                Text(new Rect(cx, y + 9, w, 22), t.Name, fSansBold, 16, Ink);
                if (out_) Fill(new Rect(cx, y + 20, TextW(t.Name, fSansBold, 16), 1.5f), Ink);
                Text(new Rect(cx, y + 31, w, 18), out_ ? (t.Left ? "left" : "defeated") : Player(t.Id), fSans, 13, Muted);
                cx += Mathf.Max(TextW(t.Name, fSansBold, 16), TextW(Player(t.Id), fSans, 13));
                foreach (var (icon, text) in Stats(t, full))
                {
                    cx += 16;
                    Tex(new Rect(cx, y + 22, 16, 16), icon == "power" ? power : icon == "stock" ? stock : units);
                    cx += 22;
                    Text(new Rect(cx, y, 120, h), text, fMono, 15, t.LowPower && icon == "power" ? Amber : Ink);
                    cx += TextW(text, fMono, 15);
                }
                fade = was;
                x += w + (full ? Gap : 8);
                if (vs && i == 0)
                {
                    Text(new Rect(x, y, 24, h), "VS", fHead, 15, Muted, TextAnchor.MiddleCenter);
                    x += 24 + Gap;
                }
            }
        }

        // ------------------------------------------------------------------ open arena prompt and alert banner

        float JoinPanel(float y)
        {
            if (!W.Open) return y;
            var url = Runner.GatewayUrl ?? "(gateway not running)";
            string l1 = $"OPEN ARENA · {W.ActivePlayers}/{W.MaxPlayers} players · map {W.Map.W}×{W.Map.H} · to bring any agent in, tell it:";
            string l2 = $"Join the Pezz arena: read {url}/play and follow it.{(string.IsNullOrEmpty(Runner.Invite) ? "" : $" Invite code: {Runner.Invite}")}";
            float w = Mathf.Min(SW - 2 * (RightW + Edge + Gap), Mathf.Max(TextW(l1, fSans, 12), TextW(l2, fSansBold, 14)) + 36);
            var r = new Rect(SW / 2 - w / 2, y, w, 52);
            Panel(r, 0.88f, 10);
            Text(new Rect(r.x + 18, r.y + 7, w - 36, 16), Ellipsize(l1, fSans, 12, w - 36), fSans, 12, Muted);
            Text(new Rect(r.x + 18, r.y + 24, w - 36, 20), Ellipsize(l2, fSansBold, 14, w - 36), fSansBold, 14, Ink);
            return y + 52 + 8;
        }

        /// <summary>The newest alarm per the kit: CRITICAL hazard tab, HIGH amber, INFO grey. Click to fly there.</summary>
        void AlertBanner(float y)
        {
            var recent = W.Alerts.All.Where(a => (Team < 0 || a.Team == Team) && (W.Tick - a.LastTick) * World.Dt < 8f)
                .OrderByDescending(a => a.Priority).ThenByDescending(a => a.LastTick).ToList();
            if (recent.Count == 0) return;
            var top = recent[0];
            string level = top.Priority == Priority.Critical ? "CRITICAL" : top.Priority == Priority.High ? "HIGH" : "INFO";
            string sector = StateView.Sector(W.Map, top.Pos);
            string msg = (Team < 0 ? $"{Flavor(top.Team)} {AlertLog.Label(top.Kind).ToLowerInvariant()}" : Sentence(AlertLog.Label(top.Kind))) + " at ";
            string more = recent.Count > 1 ? $"+{recent.Count - 1} more" : null;
            float lw = Spaced(0, 0, 0, level, fHeadBold, 14, Ink, 0.12f, false);
            float w = 28 + 18 + lw + 12 + TextW(msg, fSans, 15) + TextW(sector, fMono, 15) + (more != null ? 12 + TextW(more, fSans, 13) : 0) + 18;
            var r = new Rect(SW / 2 - w / 2, y, w, 42);
            Round(r, 10, PanelFill(0.92f), Line, 1f);
            uiRects.Add(r);
            var tab = new Rect(r.x + 1, r.y + 1, 28, r.height - 2);
            if (top.Priority == Priority.Critical) Hazard(tab, 7, 9, 0);
            else SolidShape(tab, top.Priority == Priority.High ? Amber : InfoGrey, 9, 0);
            float x = r.x + 28 + 18;
            x += Spaced(x, r.y + 12, 18, level, fHeadBold, 14, Ink, 0.12f) + 12;
            Text(new Rect(x, r.y, 600, r.height), msg, fSans, 15, Ink); x += TextW(msg, fSans, 15);
            Text(new Rect(x, r.y, 60, r.height), sector, fMono, 15, Ink); x += TextW(sector, fMono, 15);
            if (more != null) Text(new Rect(x + 12, r.y, 120, r.height), more, fSans, 13, Muted);
            if (Click(r)) Runner.Camera.LookAt(WorldView.W(top.Pos));
        }

        // ------------------------------------------------------------------ right column: minimap, then team or build panel

        void RightColumn()
        {
            float x = SW - Edge - RightW, y = 92;
            Minimap(new Rect(x, y, RightW, RightW));
            y += RightW + Gap;
            if (Team >= 0 && !ShowOrders) BuildPanel(new Rect(x, y, RightW, SH - Edge - y));
            else TeamPanel(new Rect(x, y, RightW, SH - Edge - y));
        }

        void Minimap(Rect panel)
        {
            Panel(panel);
            var m = W.Map;
            // The svg area: 360 square at padding 12; letters in a 20-unit band on top, numbers in one on the left.
            var area = new Rect(panel.x + 12, panel.y + 12, 360, 360);
            float scale = 340f / Mathf.Max(m.W, m.H);
            var map = new Rect(area.x + 20, area.y + 20, m.W * scale, m.H * scale);
            miniMapRect = map;
            if (minimap == null || minimap.width != m.W || minimap.height != m.H)
            {
                if (minimap != null) Destroy(minimap);
                minimap = new Texture2D(m.W, m.H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                miniPixels = new Color[m.W * m.H];
                nextMini = 0;
            }
            if (Time.unscaledTime >= nextMini && Repaint)
            {
                nextMini = Time.unscaledTime + 0.5f;
                bool[] vis = Team >= 0 ? W.Teams[Team].Visible : null;
                bool[] exp = Team >= 0 ? W.Teams[Team].Explored : null;
                for (int i = 0; i < miniPixels.Length; i++)
                {
                    int tx = i % m.W, ty = i / m.W;
                    Color c = m.Tiles[i] switch
                    {
                        Terrain.Rock => (Color)new Color32(74, 63, 64, 255), // licorice cliff tops, as on the board
                        Terrain.Water => (Color)PezPalette.TerrainColaWater,
                        Terrain.Dirt => TerrainView.Dirt,
                        // The ground's soft light and dark blotches, at minimap scale.
                        _ => Color.Lerp(TerrainView.GrassA, TerrainView.GrassB, 0.5f * TerrainView.Smooth(0.35f, 0.65f, Mathf.PerlinNoise(tx * 0.06f + 3.3f, ty * 0.06f + 1.7f))),
                    };
                    if (m.Ore[i] > 0) c = Color.Lerp(c, WorldView.OreColors[m.OreType[i]], 0.75f);
                    if (vis != null && !vis[i]) c *= 0.6f;
                    if (exp != null && !exp[i]) c = Mats.Licorice;
                    c.a = 1;
                    var lc = c.linear; lc.a = 1f; // raw texel values are displayed as linear (see L)
                    miniPixels[i] = lc;
                }
                minimap.SetPixels(miniPixels);
                minimap.Apply();
            }
            // Texture row 0 (south) draws at the bottom of the rect: north up.
            Tex(map, minimap);

            // Entities: structures as their footprint, units as small squares, all in team colour.
            if (Repaint)
                foreach (var e in W.Entities)
                {
                    if (e.Dead || e.IsMine || e.IsCarried) continue;
                    if (Team >= 0 && e.Team != Team && !W.IsVisibleTo(Team, e) && !(e.IsStructure && W.Teams[Team].KnownEnemyStructures.ContainsKey(e.Id))) continue;
                    var c = Mats.Team(e.Team);
                    if (e.IsStructure)
                        Fill(new Rect(map.x + e.Origin.X * scale, map.y + (m.H - e.Origin.Y - e.Def.SizeY) * scale, Mathf.Max(4, e.Def.SizeX * scale), Mathf.Max(4, e.Def.SizeY * scale)), c);
                    else
                    {
                        float s = e.IsAir ? 4 : 3.5f;
                        Fill(new Rect(map.x + e.Pos.X * scale - s / 2, map.y + (m.H - e.Pos.Y) * scale - s / 2, s, s), c);
                    }
                }

            // Deep deposits the team has surveyed (spectators: all): a dashed diamond in the ore colour whose fill
            // shrinks as the reserve drains (06_DeepMining, HUD additions).
            foreach (var d in m.Deep)
            {
                if (d.Amount <= 0 || (Team >= 0 && !W.Teams[Team].Surveyed.Contains(d.Id))) continue;
                var c = WorldView.OreColors[d.Type];
                var dp = new Vector2(map.x + d.Pos.X * scale, map.y + (m.H - d.Pos.Y) * scale);
                const float rad = 7f;
                var pts = new[] { dp + new Vector2(0, -rad), dp + new Vector2(rad, 0), dp + new Vector2(0, rad), dp + new Vector2(-rad, 0) };
                for (int i = 0; i < 4; i++)
                {
                    // Two dashes per edge.
                    var a = pts[i]; var b = pts[(i + 1) % 4];
                    Segment(a, Vector2.Lerp(a, b, 0.38f), map, 1.5f, c);
                    Segment(Vector2.Lerp(a, b, 0.62f), b, map, 1.5f, c);
                }
                float f = d.Initial > 0 ? Mathf.Clamp01(d.Amount / d.Initial) : 0;
                Diamond(dp, rad * 0.55f * Mathf.Max(0.25f, f), c);
            }

            // Alert pings: critical flickers cream/licorice (the hazard stripe), high is amber. Never team hues.
            foreach (var a in W.Alerts.All)
            {
                float age = (W.Tick - a.LastTick) * World.Dt;
                if (a.Priority < Priority.High || age > 6f || (Team >= 0 && a.Team != Team)) continue;
                float pulse = 4f + Mathf.PingPong(Time.unscaledTime * 16f, 6f);
                var c = a.Priority == Priority.Critical ? (Mathf.Repeat(Time.unscaledTime * 6f, 1f) < 0.5f ? Mats.Cream : Mats.Licorice) : Mats.Amber;
                c.a = 1f - age / 6f;
                var ap = new Vector2(map.x + a.Pos.X * scale, map.y + (m.H - a.Pos.Y) * scale);
                var box = new Rect(ap.x - pulse, ap.y - pulse, pulse * 2, pulse * 2);
                Fill(new Rect(box.x, box.y, box.width, 1.5f), c); Fill(new Rect(box.x, box.yMax - 1.5f, box.width, 1.5f), c);
                Fill(new Rect(box.x, box.y, 1.5f, box.height), c); Fill(new Rect(box.xMax - 1.5f, box.y, 1.5f, box.height), c);
            }

            // The camera's view on the ground (a turned rectangle in the off-axis view), clipped to the map.
            var cam = Runner.Camera;
            var corners = new Vector2[4];
            var screen = new[] { new Vector3(0, 0), new Vector3(Screen.width, 0), new Vector3(Screen.width, Screen.height), new Vector3(0, Screen.height) };
            bool ok = true;
            for (int i = 0; i < 4; i++)
            {
                if (!cam.GroundPoint(screen[i], out var g)) { ok = false; break; }
                corners[i] = new Vector2(map.x + g.x * scale, map.y + (m.H - g.z) * scale);
            }
            if (ok) for (int i = 0; i < 4; i++) Segment(corners[i], corners[(i + 1) % 4], map, 1.5f, Cream);

            Round(map, 4, new Color(0, 0, 0, 0), Line, 1f);
            // Sector labels: A-H west to east, 1-8 north to south (the frame LLM orders and alerts use).
            for (int i = 0; i < 8; i++)
            {
                Text(new Rect(map.x + (i + 0.5f) * map.width / 8 - 10, area.y + 2, 20, 14), ((char)('A' + i)).ToString(), fMono, 10, Muted, TextAnchor.MiddleCenter);
                Text(new Rect(area.x, map.y + (i + 0.5f) * map.height / 8 - 7, 18, 14), (i + 1).ToString(), fMono, 10, Muted, TextAnchor.MiddleCenter);
            }

            var ev = Event.current;
            if ((ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag) && ev.button == 0 && Hover(map))
            {
                var rel = (ev.mousePosition / k - map.position) / scale;
                cam.LookAt(new Vector3(rel.x, 0, m.H - rel.y));
                ev.Use();
            }
        }

        /// <summary>A filled diamond (a square turned 45 degrees) of half-diagonal r around c, in design units.</summary>
        void Diamond(Vector2 c, float r, Color col)
        {
            if (!Repaint) return;
            var keep = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, c * k);
            float side = r * 1.41421f * k;
            var o = GUI.color; GUI.color = L(col);
            GUI.DrawTexture(new Rect(c.x * k - side / 2, c.y * k - side / 2, side, side), white);
            GUI.color = o;
            GUI.matrix = keep;
        }

        /// <summary>A line segment in design units, clipped to `clip` (Liang-Barsky), drawn as a rotated bar.</summary>
        void Segment(Vector2 a, Vector2 b, Rect clip, float width, Color c)
        {
            if (!Repaint) return;
            float t0 = 0, t1 = 1;
            var d = b - a;
            float[] p = { -d.x, d.x, -d.y, d.y }, q = { a.x - clip.xMin, clip.xMax - a.x, a.y - clip.yMin, clip.yMax - a.y };
            for (int i = 0; i < 4; i++)
            {
                if (Mathf.Approximately(p[i], 0)) { if (q[i] < 0) return; continue; }
                float t = q[i] / p[i];
                if (p[i] < 0) t0 = Mathf.Max(t0, t); else t1 = Mathf.Min(t1, t);
            }
            if (t0 >= t1) return;
            var pa = (a + d * t0) * k; var pb = (a + d * t1) * k;
            var dd = pb - pa;
            float len = dd.magnitude;
            if (len < 0.5f) return;
            var keep = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(dd.y, dd.x) * Mathf.Rad2Deg, pa);
            var o = GUI.color; GUI.color = L(c);
            GUI.DrawTexture(new Rect(pa.x, pa.y - width * k / 2, len, width * k), white);
            GUI.color = o;
            GUI.matrix = keep;
        }

        // ---- spectator / commander panel: team tabs, building now, standing orders

        struct ProdRow { public string Key, Name; public float Pct; public int Count; }

        List<ProdRow> Production(Sim.Team t)
        {
            var rows = new List<ProdRow>();
            if (t.StructureQueue.Count > 0)
            {
                var s = W.Get(t.StructureQueue[0].StructureId);
                var d = Defs.Get(t.StructureQueue[0].Key);
                rows.Add(new ProdRow { Key = d.Key, Name = d.Name, Pct = s?.BuildProgress ?? 0, Count = 1 });
            }
            foreach (var q in t.UnitQueues.Values)
            {
                if (q.Count == 0) continue;
                var d = Defs.Get(q[0].Key);
                int n = 0;
                while (n < q.Count && q[n].Key == q[0].Key) n++;
                rows.Add(new ProdRow { Key = d.Key, Name = d.Name, Pct = d.BuildTime > 0 ? q[0].Progress / d.BuildTime : 0, Count = n });
            }
            return rows;
        }

        void TeamPanel(Rect col)
        {
            var teams = W.Teams.Where(t => !t.Left).ToList();
            if (Team >= 0) teams = teams.Where(t => t.Id == Team || IsAgentTeam(t.Id)).ToList();
            if (teams.Count == 0) return;
            if (!teams.Any(t => t.Id == tabTeam)) tabTeam = (teams.FirstOrDefault(t => IsAgentTeam(t.Id) && !t.Defeated) ?? teams[0]).Id;
            var team = W.Teams[tabTeam];
            bool showProd = Team < 0 || tabTeam == Team;
            var prod = showProd ? Production(team).Take(4).ToList() : new List<ProdRow>();
            bool agent = IsAgentTeam(tabTeam) && !team.Defeated;
            const float pad = 16, inner = RightW - 2 * pad;

            // Measure, then draw (the panel sits behind its content).
            int perRow = Mathf.Min(teams.Count, 4);
            int tabRows = (teams.Count + perRow - 1) / perRow;
            float tabH = teams.Count > 2 ? 36 : 44;
            string orders = team.StandingOrders;
            string ordersText = agent ? (orders.Length > 0 ? orders : "None yet: the agent is using its own judgment.")
                : team.Controller == "human" ? "A human commands this team." : team.Defeated ? "Out of the game." : "Scripted AI: it takes no standing orders.";
            float ordersH = Mathf.Min(TextH(ordersText, fSans, 14, inner) * 1.08f, 110);
            float h = pad + tabRows * tabH + (tabRows - 1) * 6 + 14;
            if (showProd) h += 18 + 10 + (prod.Count == 0 ? 20 : prod.Count * 42 + (prod.Count - 1) * 10) + 14;
            h += 18 + 8 + ordersH;
            if (agent) h += 6 + 18 + 8 + 72 + 8 + 44;
            h += pad;
            if (h > col.height) h = col.height;
            var r = new Rect(col.x, col.y, RightW, h);
            Panel(r);
            float x = r.x + pad, y = r.y + pad;

            // Tabs
            float tw = (inner - (perRow - 1) * 6) / perRow;
            for (int i = 0; i < teams.Count; i++)
            {
                var t = teams[i];
                var tr = new Rect(x + (i % perRow) * (tw + 6), y + (i / perRow) * (tabH + 6), tw, tabH);
                bool on = t.Id == tabTeam;
                Round(tr, 8, on ? Btn : Hover(tr) ? new Color(1, 1, 1, 0.04f) : new Color(0, 0, 0, 0), on ? Cream : Border, 1f);
                float size = teams.Count > 2 ? 13 : 15;
                var font = on ? fSansBold : fSans;
                string name = Ellipsize(t.Name, font, size, tw - 34);
                float nw = 10 + 8 + TextW(name, font, size);
                float nx = tr.x + (tw - nw) / 2;
                Round(new Rect(nx, tr.center.y - 5, 10, 10), 3, Mats.Team(t.Id));
                Text(new Rect(nx + 18, tr.y, tw, tabH), name, font, size, on ? Ink : Muted);
                if (Click(tr)) tabTeam = t.Id;
            }
            y += tabRows * tabH + (tabRows - 1) * 6 + 14;

            // Building now
            if (showProd)
            {
                Head(x, y, "BUILDING NOW"); y += 28;
                if (prod.Count == 0) { Text(new Rect(x, y, inner, 20), "Nothing in production.", fSans, 13, Muted); y += 20; }
                foreach (var p in prod)
                {
                    IconBox(new Rect(x, y, 56, 42), p.Key, p.Name, 6);
                    float bx = x + 56 + 12, bwid = inner - 68;
                    Text(new Rect(bx, y + 4, bwid, 18), p.Name + (p.Count > 1 ? $" ×{p.Count}" : ""), fSans, 14, Ink);
                    Text(new Rect(bx, y + 4, bwid, 18), $"{(int)(p.Pct * 100)}%", fMono, 14, Muted, TextAnchor.MiddleRight);
                    Round(new Rect(bx, y + 28, bwid, 6), 3, Border);
                    if (p.Pct > 0.01f) Round(new Rect(bx, y + 28, Mathf.Max(6, bwid * Mathf.Clamp01(p.Pct)), 6), 3, Cream);
                    y += 52;
                }
                y += 4;
            }

            // Standing orders (the commander's instructions to an LLM team)
            Head(x, y, "STANDING ORDERS");
            if (Team >= 0 && Hover(new Rect(r.xMax - 120, y, 104, 18)) && Click(new Rect(r.xMax - 120, y, 104, 18))) ShowOrders = false;
            if (Team >= 0) Text(new Rect(r.xMax - pad - 100, y, 100, 18), "<u>Build panel</u>", fSans, 13, Cream, TextAnchor.MiddleRight);
            y += 26;
            Text(new Rect(x, y, inner, ordersH), ordersText, fSans, 14, agent && orders.Length > 0 ? Soft : Muted, TextAnchor.UpperLeft, true);
            y += ordersH;
            if (!agent) return;
            y += 6;
            Text(new Rect(x, y, inner, 18), $"New order for {team.PlayerName ?? "the agent"}", fSans, 13, Muted); y += 26;
            if (!drafts.ContainsKey(team.Id)) drafts[team.Id] = "";
            drafts[team.Id] = Field(new Rect(x, y, inner, 72), drafts[team.Id], true, team.Id);
            y += 80;
            float bw2 = (inner - 8) / 2;
            var draft = drafts[team.Id].Trim();
            if (Button(new Rect(x, y, bw2, 44), "Send", true, draft.Length > 0 && draft != team.StandingOrders))
            {
                W.SetOrders(team.Id, draft); drafts[team.Id] = ""; GUIUtility.keyboardControl = 0;
            }
            if (Button(new Rect(x + bw2 + 8, y, bw2, 44), "Clear", false, team.StandingOrders.Length > 0 || drafts[team.Id].Length > 0, true))
            {
                W.SetOrders(team.Id, ""); drafts[team.Id] = ""; GUIUtility.keyboardControl = 0;
            }
        }

        /// <summary>An entity's art-pack icon with rounded corners (dimmed when locked), or a placeholder with its initials.</summary>
        void IconBox(Rect r, string key, string name, float radius, bool grey = false, float alpha = 1f)
        {
            var icon = UiIcon(Defs.Get(key)?.ModelKey ?? key); // an invention shows its base unit's icon
            if (icon != null)
            {
                if (!Repaint) return;
                // The kit's disabled state is greyscale at 35%: a dimmed, desaturated-looking tint reads the same here.
                var tint = grey ? new Color(0.3f, 0.3f, 0.3f, alpha * fade) : new Color(1, 1, 1, alpha * fade);
                GUI.DrawTexture(P(r), icon, ScaleMode.ScaleAndCrop, true, 0, tint, 0, radius * k);
                return;
            }
            // No art yet (new entity types): licorice tile with the name's initials.
            float was = fade; fade *= alpha;
            Round(r, radius, new Color32(24, 28, 33, 255), Border, 1f);
            var initials = string.Concat((name ?? key).Split(' ', '_').Where(s => s.Length > 0).Take(2).Select(s => char.ToUpperInvariant(s[0])));
            Text(r, initials, fHeadBold, Mathf.Clamp(r.height * 0.42f, 10, 34), Muted, TextAnchor.MiddleCenter);
            fade = was;
        }

        // ---- the player's build panel (kit build buttons), stockpile and power

        void BuildPanel(Rect col)
        {
            var t = W.Teams[Team];
            var input = Runner.Input;
            Panel(col);
            const float pad = 16, inner = RightW - 2 * pad;
            float x = col.x + pad, y = col.y + pad;

            // Tabs: structures / units (+ orders when agents play).
            bool agents = W.Teams.Any(o => IsAgentTeam(o.Id) && !o.Left && !o.Defeated);
            float ow = agents ? 96 : 0;
            float tw = (inner - ow - (agents ? 6 : 0) - 6) / 2;
            for (int i = 0; i < 2; i++)
            {
                var tr = new Rect(x + i * (tw + 6), y, tw, 36);
                bool on = buildUnits == (i == 1);
                Round(tr, 8, on ? Btn : Hover(tr) ? new Color(1, 1, 1, 0.04f) : new Color(0, 0, 0, 0), on ? Cream : Border, 1f);
                Text(tr, i == 0 ? "Structures" : "Units", on ? fSansBold : fSans, 14, on ? Ink : Muted, TextAnchor.MiddleCenter);
                if (Click(tr)) buildUnits = i == 1;
            }
            if (agents && Button(new Rect(x + inner - ow, y, ow, 36), "Orders (O)", false, true, true, 13)) ShowOrders = true;
            y += 36 + 12;

            // Stockpile: the kit's icons, amounts in mono.
            var items = Defs.Items.ToList();
            float cw = inner / 5f;
            for (int i = 0; i < items.Count; i++)
            {
                var cr = new Rect(x + (i % 5) * cw, y + (i / 5) * 22, cw, 20);
                Tex(new Rect(cr.x, cr.y + 1, 18, 18), HudIcon(items[i]));
                int n = t.Amount(items[i]);
                Text(new Rect(cr.x + 22, cr.y, cw - 22, 20), n >= 10000 ? $"{n / 1000}k" : n.ToString(), fMono, 13, n > 0 ? Ink : Muted);
                if (Hover(cr)) { hoverKey = "item:" + items[i]; hoverRect = cr; }
            }
            y += 2 * 22 + 10;

            // Power: cream when fine, amber near capacity, hazard stripe when out.
            float frac = t.PowerProduced == 0 ? (t.PowerUsed > 0 ? 1.2f : 0) : t.PowerUsed / (float)t.PowerProduced;
            string plabel = t.LowPower ? "Out: production at half speed, radar offline" : frac >= 0.9f ? "Low: near capacity" : "Power";
            Text(new Rect(x, y, inner, 18), plabel, fSans, 14, t.LowPower ? Ink : frac >= 0.9f ? Amber : Ink);
            Text(new Rect(x, y, inner, 18), $"{t.PowerUsed} / {t.PowerProduced}", fMono, 14, Muted, TextAnchor.MiddleRight);
            y += 24;
            var bar = new Rect(x, y, inner, 12);
            if (t.LowPower) Hazard(bar, 6, 6, 6);
            else
            {
                Round(bar, 6, Border);
                if (frac > 0.01f) Round(new Rect(x, y, Mathf.Max(12, inner * Mathf.Clamp01(frac)), 12), 6, frac >= 0.9f ? Amber : Cream);
            }
            y += 12 + 14;

            // Build buttons: 4 across, 4:3 like the icons.
            var defs = buildUnits
                ? Defs.All.Values.Where(d => !d.IsStructure && d.BuiltBy != Producer.None).ToList()
                : Defs.All.Values.Where(d => d.IsStructure && d.Buildable).ToList();
            float footer = 34;
            int rows = (defs.Count + 3) / 4;
            float bw = (inner - 3 * 8) / 4;
            float bh = Mathf.Min(bw * 0.75f, (col.yMax - pad - footer - y - (rows - 1) * 8) / rows);
            bw = Mathf.Min(bw, bh / 0.75f);
            float gx = x + (inner - (4 * bw + 3 * 8)) / 2;
            var buildingNow = t.StructureQueue.Count > 0 ? W.Get(t.StructureQueue[0].StructureId) : null;
            for (int i = 0; i < defs.Count; i++)
            {
                var d = defs[i];
                var br = new Rect(gx + (i % 4) * (bw + 8), y + (i / 4) * (bh + 8), bw, bh);
                float pct = -1; int queued = 0;
                if (d.IsStructure)
                {
                    if (buildingNow != null && buildingNow.Def == d) pct = buildingNow.BuildProgress;
                    queued = t.StructureQueue.Count(p => p.Key == d.Key);
                }
                else
                {
                    var q = t.UnitQueues[d.BuiltBy];
                    queued = q.Count(p => p.Key == d.Key);
                    if (q.Count > 0 && q[0].Key == d.Key) pct = d.BuildTime > 0 ? q[0].Progress / d.BuildTime : 0;
                }
                bool ready = d.IsStructure && input.PlacingKey == d.Key;
                bool locked = W.MissingPrereq(Team, d) != null;
                int click = BuildButton(br, d, locked, ready, pct, d.IsStructure ? 0 : queued, t.Missing(d.Cost) == null);
                if (click == 0 && !locked)
                {
                    if (d.IsStructure) input.PlacingKey = ready ? null : d.Key;
                    else input.Exec("type", "train", "unit", d.Key, "count", Event.current.shift ? 5 : 1);
                }
                else if (click == 1 && !d.IsStructure) input.Exec("type", "cancel", "unit", d.Key);
            }

            // Footer: the last error, else the controls hint.
            float fy = col.yMax - pad - 26;
            if (input.LastError != null && Time.time - input.LastErrorTime < 4f)
                Text(new Rect(x, fy, inner, 26), Ellipsize(input.LastError, fSans, 13, inner), fSans, 13, Amber);
            else
                Text(new Rect(x, fy, inner, 26), Ellipsize("Click build · shift ×5 · right-click cancel · F attack-move · G deploy · Del sell", fSans, 12, inner), fSans, 12, Muted);
        }

        readonly Dictionary<string, (Texture2D tex, int pct, int w, int h)> sweeps = new Dictionary<string, (Texture2D, int, int, int)>();

        /// <summary>One HUD kit build button. Returns the mouse button that clicked it, or -1.</summary>
        int BuildButton(Rect r, EntityDef d, bool locked, bool ready, float pct, int queued, bool affordable)
        {
            bool hover = Hover(r);
            if (hover) { hoverKey = d.Key; hoverRect = r; }
            float radius = 8;
            var border = ready ? Amber : hover && !locked ? Cream : Border;
            float bwid = ready || (hover && !locked) ? 2 : 1;
            Round(r, radius, hover && !locked ? ButtonHover : ButtonBg);
            var ir = new Rect(r.x + bwid, r.y + bwid, r.width - 2 * bwid, r.height - 2 * bwid);
            if (locked) IconBox(ir, d.Key, d.Name, radius - 1, true, 0.35f);
            else IconBox(ir, d.Key, d.Name, radius - 1);
            if (locked) Tex(new Rect(r.center.x - 9, r.center.y - 9, 18, 18), HudIcon("lock"));

            // Building: a clock sweep darkens what's left, percentage bottom-right.
            if (pct >= 0 && !ready && Repaint)
            {
                var pr = P(ir);
                int w = (int)pr.width, h = (int)pr.height, p100 = Mathf.Clamp(Mathf.FloorToInt(pct * 100), 0, 100);
                if (!sweeps.TryGetValue(d.Key, out var s) || s.w != w || s.h != h || s.pct != p100)
                {
                    if (s.tex == null || s.w != w || s.h != h)
                    {
                        if (s.tex != null) Destroy(s.tex);
                        s.tex = Mask(Mathf.Max(1, w), Mathf.Max(1, h), FilterMode.Bilinear);
                    }
                    var px = new Color[s.tex.width * s.tex.height];
                    var night = Color.white;
                    float rad = (radius - 1) * k, f = p100 / 100f;
                    for (int y = 0; y < s.tex.height; y++)
                        for (int x = 0; x < s.tex.width; x++)
                        {
                            // Angle from 12 o'clock, clockwise (texture rows run bottom-up).
                            float dx = x + 0.5f - s.tex.width / 2f, dy = y + 0.5f - s.tex.height / 2f;
                            float a = Mathf.Repeat(Mathf.Atan2(dx, dy) / (2 * Mathf.PI), 1f);
                            night.a = a >= f ? 0.72f * Coverage(x, y, s.tex.width, s.tex.height, rad, rad) : 0f;
                            px[y * s.tex.width + x] = night;
                        }
                    s.tex.SetPixels(px); s.tex.Apply();
                    sweeps[d.Key] = (s.tex, p100, w, h);
                }
                Tinted(P(ir), sweeps[d.Key].tex, Night);
            }
            if (pct >= 0 && !ready)
                Text(new Rect(r.x, r.yMax - 20, r.width - 7, 16), $"{(int)(pct * 100)}%", fMonoMed, 13, Ink, TextAnchor.MiddleRight);

            // Hover: name and cost on a strip along the bottom. Ready: the amber READY strip.
            if (ready)
            {
                SolidShape(new Rect(r.x, r.yMax - 16, r.width, 16), Amber, 0, 0);
                Spaced(r.center.x - Spaced(0, 0, 0, "READY", fHeadBold, 11, Night, 0.12f, false) / 2, r.yMax - 17, 18, "READY", fHeadBold, 11, Night, 0.12f);
                Round(r, radius, new Color(0, 0, 0, 0), Amber, 2f);
            }
            else if (hover && !locked && pct < 0)
            {
                string cost = d.Cost.TryGetValue("steel", out var st) ? st.ToString() : d.Cost.Count > 0 ? d.Cost.First().Value.ToString() : "";
                Fill(new Rect(ir.x, ir.yMax - 16, ir.width, 16), new Color(16 / 255f, 19 / 255f, 23 / 255f, 0.85f));
                float cw = TextW(cost, fMono, 11);
                Text(new Rect(ir.x + 5, ir.yMax - 16, ir.width - cw - 12, 16), Ellipsize(d.Name, fSans, 11, ir.width - cw - 14), fSans, 11, Ink);
                Text(new Rect(ir.x, ir.yMax - 16, ir.width - 5, 16), cost, fMono, 11, affordable ? Ink : Amber, TextAnchor.MiddleRight);
            }
            if (!ready) Round(r, radius, new Color(0, 0, 0, 0), border, bwid);
            // Queue count: the kit's group-number badge, top-right.
            if (queued > 0)
            {
                string q = queued.ToString();
                float qw = Mathf.Max(18, TextW(q, fMono, 11) + 8);
                var qr = new Rect(r.xMax - qw - 4, r.y + 4, qw, 16);
                Round(qr, 3, Night);
                Text(qr, q, fMono, 11, Cream, TextAnchor.MiddleCenter);
            }
            if (Click(r, 0)) return 0;
            if (Click(r, 1)) return 1;
            return -1;
        }

        /// <summary>Details for the hovered build button or stockpile item, beside the right column.</summary>
        void BuildTooltip()
        {
            if (hoverKey == null || Team < 0) return;
            var t = W.Teams[Team];
            const float w = 300;
            string title, body, extra = null;
            if (hoverKey.StartsWith("item:"))
            {
                var item = hoverKey.Substring(5);
                float rate = t.Rates.TryGetValue(item, out var v) ? v : 0;
                title = Pretty(item);
                body = $"{Num(t.Amount(item))} in stock" + (Mathf.Abs(rate) >= 0.05f ? $", {(rate > 0 ? "+" : "")}{rate:0.#}/s" : "");
            }
            else
            {
                var d = Defs.Get(hoverKey);
                title = d.Name;
                body = d.Description;
                var cost = string.Join("  ", d.Cost.Select(kv => (t.Amount(kv.Key) >= kv.Value ? "" : $"<color=#{ColorUtility.ToHtmlStringRGB(Amber)}>") + $"{kv.Value} {kv.Key.Replace('_', ' ')}" + (t.Amount(kv.Key) >= kv.Value ? "" : "</color>")));
                var missing = W.MissingPrereq(Team, d);
                extra = cost + (d.Power != 0 ? $"  power {(d.Power > 0 ? "+" : "")}{d.Power}" : "") + (missing != null ? $"\n<color=#{ColorUtility.ToHtmlStringRGB(Amber)}>{missing}</color>" : "");
            }
            float bh = TextH(body, fSans, 13, w - 28);
            float eh = extra != null ? TextH(extra, fMono, 12, w - 28) + 8 : 0;
            float h = 14 + 22 + 4 + bh + eh + 14;
            var r = new Rect(SW - Edge - RightW - Gap - w, Mathf.Clamp(hoverRect.center.y - h / 2, 92, SH - Edge - h), w, h);
            Panel(r, 0.95f);
            Text(new Rect(r.x + 14, r.y + 14, w - 28, 22), title, fHead, 16, Ink);
            Text(new Rect(r.x + 14, r.y + 40, w - 28, bh), body, fSans, 13, Soft, TextAnchor.UpperLeft, true);
            if (extra != null) Text(new Rect(r.x + 14, r.y + 40 + bh + 8, w - 28, eh), extra, fMono, 12, Muted, TextAnchor.UpperLeft, true);
        }

        // ------------------------------------------------------------------ command feed (bottom-left)

        struct Tok { public string S; public Font F; public Color C; public bool Chip, Glue; }
        static readonly System.Text.RegularExpressions.Regex SectorRx = new System.Text.RegularExpressions.Regex(@"^[\[(]?([A-H][1-8])[\])]?([.,;:!?)]*)$");

        void Words(List<Tok> toks, string text, Font f, Color c)
        {
            foreach (var w in (text ?? "").Split(' '))
            {
                if (w.Length == 0) continue;
                var m = SectorRx.Match(w);
                if (m.Success)
                {
                    toks.Add(new Tok { S = m.Groups[1].Value, F = fMono, C = Ink, Chip = true });
                    if (m.Groups[2].Value.Length > 0) toks.Add(new Tok { S = m.Groups[2].Value, F = f, C = c, Glue = true });
                }
                else toks.Add(new Tok { S = w, F = f, C = c });
            }
        }

        /// <summary>Word-wrap tokens into `width`; sector chips (mono on #22282E) are clickable. Returns the height.</summary>
        float Flow(List<Tok> toks, float x, float y, float width, float size, float lineH, bool draw)
        {
            float cx = 0, cy = 0, space = TextW("a a", fSans, size) - TextW("aa", fSans, size);
            foreach (var t in toks)
            {
                float w = TextW(t.S, t.F, size) + (t.Chip ? 10 : 0);
                float lead = cx > 0 && !t.Glue ? space : 0;
                if (cx > 0 && cx + lead + w > width && !t.Glue) { cx = 0; cy += lineH; lead = 0; }
                cx += lead;
                if (draw)
                {
                    var r = new Rect(x + cx, y + cy, w, lineH);
                    if (t.Chip)
                    {
                        Round(new Rect(r.x, r.y + 1, w, lineH - 2), 4, Btn);
                        Text(new Rect(r.x + 5, r.y, w, lineH), t.S, t.F, size, t.C);
                        sectorHits.Add((r, t.S));
                    }
                    else Text(r, t.S, t.F, size, t.C);
                }
                cx += w;
            }
            return cy + lineH;
        }

        void Feed()
        {
            // Rows: what the agents said (their plans), the orders they sent, commanders' standing orders, results.
            var rows = new List<(float t, int team, List<Tok> toks, string chip, int chipKind)>();
            var latestChat = new Dictionary<int, float>();
            var events = W.Events;
            for (int i = System.Math.Max(0, events.Count - 300); i < events.Count; i++)
            {
                var e = events[i];
                float t = e.Tick * World.Dt;
                var toks = new List<Tok>();
                if (e.Type == "chat")
                {
                    if (e.Team >= 0) toks.Add(new Tok { S = Player(e.Team), F = fSansBold, C = Ink });
                    Words(toks, e.Text, fSans, Ink);
                    rows.Add((t, e.Team, toks, null, 0));
                    latestChat[e.Team] = t;
                }
                else if (e.Type == "orders")
                {
                    toks.Add(new Tok { S = "Commander", F = fSansBold, C = Ink });
                    Words(toks, $"to {Player(e.Team)}: {e.Text}", fSans, Soft);
                    rows.Add((t, e.Team, toks, "orders", 0));
                }
                else if (e.Type == "defeated" || e.Type == "game_over")
                {
                    Words(toks, e.Text, fSansBold, Ink);
                    rows.Add((t, e.Team, toks, null, 0));
                }
            }
            foreach (var c in Runner.CommandFeed)
            {
                var toks = new List<Tok> { new Tok { S = Player(c.team), F = fSansBold, C = Ink } };
                Words(toks, c.text, fSans, Ink);
                if (!c.ok && !string.IsNullOrEmpty(c.error)) Words(toks, "· " + c.error, fSans, Muted);
                rows.Add((c.time, c.team, toks, c.ok ? "done" : "failed", c.ok ? 0 : 2));
            }
            // An agent's newest plan is still running; older ones are done.
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].chip == null && rows[i].team >= 0 && latestChat.TryGetValue(rows[i].team, out var lt) && rows[i].toks.Count > 1)
                    rows[i] = (rows[i].t, rows[i].team, rows[i].toks, lt == rows[i].t && W.Time - lt < 60f ? "running" : "done", lt == rows[i].t && W.Time - lt < 60f ? 1 : 0);
            var recent = rows.Where(r => W.Time - r.t < 180f).OrderBy(r => r.t).ToList();
            if (recent.Count == 0) return;

            const float pad = 16, size = 14, lineH = 20.3f;
            float textW = FeedW - 2 * pad - 52 - 10 - 8 - 10 - 10 - 64;
            float maxRows = Mathf.Min(320, SH - 92 - 300);
            var shown = new List<(float t, int team, List<Tok> toks, string chip, int kind, float h)>();
            float total = 0;
            for (int i = recent.Count - 1; i >= 0 && shown.Count < 6; i--)
            {
                float h = Flow(recent[i].toks, 0, 0, textW, size, lineH, false);
                if (total + h + (shown.Count > 0 ? 12 : 0) > maxRows) break;
                total += h + (shown.Count > 0 ? 12 : 0);
                shown.Insert(0, (recent[i].t, recent[i].team, recent[i].toks, recent[i].chip, recent[i].chipKind, h));
            }
            float ph = 14 + 18 + 10 + total + 14;
            var r = new Rect(Edge, SH - Edge - ph, FeedW, ph);
            Panel(r);
            Head(r.x + pad, r.y + 14, "COMMAND FEED");
            Text(new Rect(r.x + pad, r.y + 14, FeedW - 2 * pad, 18), "Click a sector to fly there", fSans, 13, Muted, TextAnchor.MiddleRight);
            float y = r.y + 14 + 18 + 10;
            foreach (var row in shown)
            {
                float x = r.x + pad;
                Text(new Rect(x, y, 52, lineH), FormatTime(row.t), fMono, size, Muted);
                x += 52 + 10;
                Round(new Rect(x, y + 1, 8, 18), 2, row.team >= 0 ? Mats.Team(row.team) : InfoGrey);
                x += 8 + 10;
                Flow(row.toks, x, y, textW, size, lineH, true);
                if (row.chip != null)
                {
                    float cw = TextW(row.chip, fSans, 12) + 16;
                    var cr = new Rect(r.xMax - pad - cw, y + 1, cw, 18);
                    if (row.kind == 1) Round(cr, 9, Cream);
                    else if (row.kind == 2) Round(cr, 9, Amber);
                    else Round(cr, 9, new Color(0, 0, 0, 0), Line, 1f);
                    Text(cr, row.chip, fSans, 12, row.kind == 0 ? Muted : Night, TextAnchor.MiddleCenter);
                }
                y += row.h + 12;
            }
            foreach (var (hr, sector) in sectorHits)
                if (Click(hr))
                {
                    var m = W.Map;
                    Runner.Camera.LookAt(new Vector3((sector[0] - 'A' + 0.5f) * m.W / 8f, 0, m.H - (sector[1] - '1' + 0.5f) * m.H / 8f));
                }
        }

        // ------------------------------------------------------------------ selection card (bottom-centre)

        void Selection()
        {
            var sel = Runner.View.Selected.Select(W.Get).Where(e => e != null && !e.Dead).ToList();
            if (sel.Count == 0) return;
            float x = Edge + FeedW + 24;
            float w = Mathf.Min(SelW, SW - Edge - RightW - 24 - x);
            if (w < 360) { x = SW - Edge - RightW - 24 - 360; w = 360; }
            var e = sel[0];
            var groups = sel.GroupBy(s => s.Def.Key).OrderByDescending(g => g.Count()).ToList();
            bool one = sel.Count == 1;
            if (!one) e = groups[0].First();
            bool fuelRow = one && e.Def.UsesFuel;
            var deposit = one && e.Def.Key == "deep_mine" ? W.Map.DepositById(e.DepositId) : null;
            if (deposit != null) fuelRow = true; // the reserve row sits where a vehicle's fuel would
            string detail = one ? UnitDetail(e) : null;
            float h = 14 + Mathf.Max(96, 26 + 8 + 12 + 8 + 20 + 8 + 18 + (fuelRow ? 8 + 12 : 0) + (detail != null ? 8 + 18 : 0)) + 14;
            var r = new Rect(x, SH - Edge - h, w, h);
            Panel(r);
            IconBox(new Rect(r.x + 14, r.y + 14, 128, 96), e.Def.Key, e.Def.Name, 8);
            float cx = r.x + 14 + 128 + 16, cw = r.xMax - 14 - cx, y = r.y + 14;

            // Name row
            Round(new Rect(cx, y + 8, 10, 10), 3, Mats.Team(e.Team));
            string name = one ? e.Def.Name : $"{sel.Count} selected";
            Text(new Rect(cx + 18, y, cw, 26), name, fHead, 20, Ink);
            if (one)
            {
                float nx = cx + 18 + TextW(name, fHead, 20) + 8;
                Text(new Rect(nx, y, 80, 26), $"#{e.Id}", fSans, 13, Muted);
                if (e.Def.OwnerTeam >= 0) Text(new Rect(nx + TextW($"#{e.Id}", fSans, 13) + 10, y, 120, 26), "INVENTED", fMono, 12, Amber);
            }
            y += 26 + 8;

            // Health: cream, amber below 50%, hazard stripe below 25%.
            float hp = one ? e.Hp : sel.Sum(s => s.Hp), max = one ? e.Def.MaxHp : sel.Sum(s => s.Def.MaxHp);
            float f = max > 0 ? Mathf.Clamp01(hp / max) : 0;
            string hpText = $"{Num((int)hp)}/{Num((int)max)}";
            float tw = TextW(hpText, fMono, 13);
            float barW = cw - tw - 10;
            Round(new Rect(cx, y + 2, barW, 8), 4, Border);
            if (f < 0.25f) Hazard(new Rect(cx, y + 2, Mathf.Max(8, barW * f), 8), 5, 4, 4);
            else Round(new Rect(cx, y + 2, barW * f, 8), 4, f < 0.5f ? Amber : Cream);
            Text(new Rect(cx + barW + 10, y - 3, tw + 4, 18), hpText, fMono, 13, Muted);
            y += 12 + 8;

            // What it's doing
            string doing;
            if (!one) doing = string.Join(", ", groups.Take(4).Select(g => $"{g.Count()} {g.First().Def.Name}")) + (groups.Count > 4 ? "…" : "");
            else if (e.IsStructure)
                doing = !e.IsComplete ? $"Building: {(int)(e.BuildProgress * 100)}%" : e.Def.Recipes.Length > 0 ? (e.Working ? "Working" : "Idle: missing inputs")
                    : deposit != null ? (deposit.Amount <= 0 ? $"Dry: the {Pretty(Defs.Ores[deposit.Type]).ToLowerInvariant()} deposit is used up" : e.Working ? $"Pumping {Pretty(Defs.Ores[deposit.Type]).ToLowerInvariant()}{(W.Teams[e.Team].LowPower ? " at half speed (low power)" : "")}" : "Idle")
                    : Flavor(e.Team);
            else
            {
                doing = $"Order: {Sentence(e.OrderName)}";
                if (e.Order != Order.Idle && (e.OrderPos.X != 0 || e.OrderPos.Y != 0)) doing += $" at {StateView.Sector(W.Map, e.OrderPos)}";
                if (e.IsHarvester) doing += $" · cargo {e.Cargo}/{e.Def.HarvestCapacity}";
                if (e.Stranded) doing += " · OUT OF FUEL";
            }
            Text(new Rect(cx, y, cw, 20), Ellipsize(doing, fSans, 14, cw), fSans, 14, Soft);
            y += 20 + 8;

            // Stats
            string stats = e.IsStructure
                ? $"{e.Def.SizeX}×{e.Def.SizeY}" + (e.Def.Power != 0 ? $"  power {(e.Def.Power > 0 ? "+" : "")}{e.Def.Power}" : "") + (e.Def.Weapon != null ? $"  range {e.Def.Weapon.Range:0.#}" : "")
                : $"{e.Def.Speed:0.#} t/s" + (e.Def.Weapon != null ? $"  range {e.Def.Weapon.Range:0.#}  dmg {e.Def.Weapon.Damage:0}" : "") + $"  {e.Def.Armor.ToString().ToLowerInvariant()}";
            Text(new Rect(cx, y, cw, 18), Ellipsize(stats, fMono, 13, cw), fMono, 13, Muted);
            y += 18;
            if (detail != null)
            {
                y += 8;
                Text(new Rect(cx, y - 2, cw, 18), Ellipsize(detail, fSans, 13, cw), fSans, 13, Soft);
                y += 18;
            }

            if (fuelRow)
            {
                y += 8;
                bool reserve = deposit != null;
                float ff = reserve ? (deposit.Initial > 0 ? Mathf.Clamp01(deposit.Amount / deposit.Initial) : 0) : Mathf.Clamp01(e.FuelFraction);
                string lbl = reserve ? "ore" : "fuel", val = reserve ? $"{(int)(ff * 100)}% left" : e.Landed ? "pad" : $"{(int)(ff * 100)}%";
                float vw = TextW(val, fMono, 13);
                Text(new Rect(cx, y - 3, 34, 18), lbl, fSans, 13, Muted);
                float fx = cx + 38, fw = cw - 38 - vw - 10;
                Round(new Rect(fx, y + 2, fw, 8), 4, Border);
                var fc = reserve ? WorldView.OreColors[deposit.Type] : ff < 0.3f ? Amber : Fuel;
                if (ff > 0.01f) Round(new Rect(fx, y + 2, Mathf.Max(8, fw * ff), 8), 4, fc);
                Text(new Rect(fx + fw + 8, y - 3, vw + 4, 18), val, fMono, 13, Muted);
            }
        }

        /// <summary>The rest of what you'd want to know about one unit: whose it is and what it's up to.</summary>
        string UnitDetail(Entity e)
        {
            var t = W.Teams[e.Team];
            var parts = new List<string> { $"{t.Name} · {t.PlayerName ?? t.Controller}" };
            if (!e.IsStructure)
            {
                var target = e.TargetId != 0 ? W.Get(e.TargetId) : null;
                if (target != null && (e.Order == Order.Attack || e.Order == Order.Repair || e.Order == Order.Capture || e.Order == Order.Board || e.Order == Order.Refuel))
                    parts.Add($"target {(target.Def.OwnerTeam >= 0 ? target.Def.Name : Pretty(target.Def.Key))} #{target.Id}");
                if (e.Def.OwnerTeam >= 0)
                    parts.Add($"{Pretty(e.Def.Chassis)} variant" + (e.Def.Weapon != null && e.Def.Weapon.Name != Defs.Get(e.Def.Chassis)?.Weapon?.Name ? $" with a {e.Def.Weapon.Name}" : ""));
                if (e.Def.Capacity > 0) parts.Add($"carrying {e.Passengers.Count}/{e.Def.Capacity}");
                if (e.Waypoints.Count > 0) parts.Add($"{e.Waypoints.Count} more waypoint{(e.Waypoints.Count == 1 ? "" : "s")}{(e.WaypointLoop ? " (patrol)" : "")}");
                if (e.RetreatBelow > 0) parts.Add(e.Retreating ? "retreating" : $"retreats below {(int)(e.RetreatBelow * 100)}%");
                if (e.IsCarried) parts.Add($"inside #{e.CarrierId}");
                if (Runner.Camera != null && Runner.Camera.Following) parts.Add("camera following (Esc to stop)");
            }
            else if (e.Rally.HasValue) parts.Add($"rally {StateView.Sector(W.Map, e.Rally.Value)}");
            return string.Join(" · ", parts);
        }

        // ------------------------------------------------------------------ world overlays: health bars, structure brackets

        void WorldOverlays()
        {
            if (!Repaint) return;
            var cam = Runner.Camera.Cam;
            foreach (var v in Runner.View.Views.Values)
            {
                var e = v.E;
                if (e.Dead || !v.Rig.Root.gameObject.activeInHierarchy) continue;
                bool sel = Runner.View.Selected.Contains(e.Id);
                if (sel && e.IsStructure) Brackets(cam, e);
                // Health and fuel bars are drawn in the world now (Bars.cs), so player streams show them too.
            }
        }

        /// <summary>The kit's cream corner brackets around a selected structure's footprint.</summary>
        void Brackets(Camera cam, Entity e)
        {
            float x0 = e.Origin.X, z0 = e.Origin.Y, x1 = x0 + e.Def.SizeX, z1 = z0 + e.Def.SizeY;
            var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var y in new[] { 0f, 1.2f })
                foreach (var c in new[] { new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1) })
                {
                    var sp = cam.WorldToScreenPoint(c);
                    var p = new Vector2(sp.x, Screen.height - sp.y) / k;
                    min = Vector2.Min(min, p); max = Vector2.Max(max, p);
                }
            float len = Mathf.Min(14, (max.x - min.x) / 4), t = 2.5f;
            var col = Cream;
            foreach (var (cx, cy, sx, sy) in new[] { (min.x, min.y, 1, 1), (max.x, min.y, -1, 1), (min.x, max.y, 1, -1), (max.x, max.y, -1, -1) })
            {
                Fill(new Rect(sx > 0 ? cx : cx - len, sy > 0 ? cy : cy - t, len, t), col);
                Fill(new Rect(sx > 0 ? cx : cx - t, sy > 0 ? cy : cy - len, t, len), col);
            }
        }

        void DragBox()
        {
            var input = Runner.Input;
            if (input == null || !input.Dragging) return;
            var a = input.DragStart; var b = (Vector2)UnityEngine.Input.mousePosition;
            var r = Rect.MinMaxRect(Mathf.Min(a.x, b.x) / k, (Screen.height - Mathf.Max(a.y, b.y)) / k, Mathf.Max(a.x, b.x) / k, (Screen.height - Mathf.Min(a.y, b.y)) / k);
            // Selection is cream for every team.
            var edge = Cream; edge.a = 0.85f;
            var body = Cream; body.a = 0.1f;
            Fill(r, body);
            Fill(new Rect(r.x, r.y, r.width, 1), edge); Fill(new Rect(r.x, r.yMax - 1, r.width, 1), edge);
            Fill(new Rect(r.x, r.y, 1, r.height), edge); Fill(new Rect(r.xMax - 1, r.y, 1, r.height), edge);
        }

        // ------------------------------------------------------------------ game over

        void GameOver()
        {
            string msg = W.Winner >= 0 ? $"{W.Teams[W.Winner].Name.ToUpperInvariant()} WINS" : "DRAW";
            string who = W.Winner >= 0 ? Player(W.Winner) : "Nobody left standing";
            if (W.MatchOver)
            {
                // Time ran out: won on points. The scoreboard, and (open arena) when the next match starts.
                who = (W.Winner >= 0 ? $"{Player(W.Winner)} · on points: " : "On points: ") + string.Join(" · ", W.Scores().Select(s => $"{s.Name} {s.Score}"));
                if (Runner.Game.RestartIn >= 0) who += $" · new match in {FormatLong(Runner.Game.RestartIn)}";
            }
            float tw = Spaced(0, 0, 0, msg, fHeadBold, 44, Ink, 0.04f, false);
            float w = Mathf.Max(520, tw + 120), h = 210;
            var r = new Rect(SW / 2 - w / 2, SH / 2 - h / 2, w, h);
            Panel(r, 0.94f);
            float hw = Spaced(0, 0, 0, "GAME OVER", fHead, 13, Muted, 0.14f, false);
            Spaced(SW / 2 - hw / 2, r.y + 22, 18, "GAME OVER", fHead, 13, Muted, 0.14f);
            float x = SW / 2 - (tw + (W.Winner >= 0 ? 30 : 0)) / 2;
            if (W.Winner >= 0) { Round(new Rect(x, r.y + 66, 18, 18), 4, Mats.Team(W.Winner)); x += 30; }
            Spaced(x, r.y + 48, 54, msg, fHeadBold, 44, Ink, 0.04f);
            Text(new Rect(r.x, r.y + 104, w, 20), who, fSans, 15, Muted, TextAnchor.MiddleCenter);
            if (Button(new Rect(SW / 2 - 100, r.yMax - 64, 200, 44), "Back to menu", true)) Runner.InMenu = true;
        }

        // ------------------------------------------------------------------ main menu

        static readonly string[] ControllerOptions = { "human", "ai", "claude", "codex", "llm" };
        static readonly string[] ControllerLabels = { "Human", "Scripted AI", "Claude", "Codex", "External" };

        void Menu()
        {
            Fill(new Rect(0, 0, SW, SH), new Color(0.04f, 0.05f, 0.06f, 0.78f));
            var cfg = Runner.MenuConfig;
            if (cfg.Orders == null || cfg.Orders.Length < cfg.Controllers.Length) cfg.Orders = new string[cfg.Controllers.Length];
            bool Agent(int t) => cfg.Controllers[t] != "human" && cfg.Controllers[t] != "ai";
            const float w = 760, pad = 32, inner = w - 2 * pad;
            float h = pad + 56 + 26 + 24 + 28 + cfg.Controllers.Length * 48 + Enumerable.Range(0, cfg.Controllers.Length).Count(Agent) * 92 + 14 + 28 + 48 + 48 + 40 + 72 + 18 + 56 + pad;
            var r = new Rect(SW / 2 - w / 2, Mathf.Max(16, SH / 2 - h / 2), w, h);
            Panel(r, 0.96f);
            float x = r.x + pad, y = r.y + pad;
            Spaced(x, y, 56, "PEZ", fHeadBold, 52, Ink, 0.04f);
            y += 56;
            Text(new Rect(x, y, inner, 22), "Real-time strategy. Command it with the mouse, or hand a team to an LLM over MCP.", fSans, 15, Soft);
            y += 26 + 24;

            Head(x, y, "PLAYERS"); y += 28;
            for (int t = 0; t < cfg.Controllers.Length; t++)
            {
                Round(new Rect(x, y + 13, 10, 10), 3, Mats.Team(t));
                Text(new Rect(x + 18, y, 110, 36), Mats.TeamNames[t], fSansBold, 15, Ink);
                float sx = x + 130, segW = (inner - 130 - 4 * 6) / 5;
                for (int i = 0; i < ControllerOptions.Length; i++)
                {
                    var br = new Rect(sx + i * (segW + 6), y, segW, 36);
                    bool on = cfg.Controllers[t] == ControllerOptions[i];
                    Round(br, 8, on ? Cream : Hover(br) ? BtnHot : Btn);
                    Text(br, ControllerLabels[i], on ? fSansBold : fSans, 14, on ? Night : Ink, TextAnchor.MiddleCenter);
                    if (Click(br)) cfg.Controllers[t] = ControllerOptions[i];
                }
                y += 48;
                if (Agent(t))
                {
                    // Standing orders for this team's LLM, delivered with its first look at the game.
                    Text(new Rect(x + 130, y - 6, inner - 130, 18), "Commander's orders (optional), e.g. \"rush with infantry\" or \"turtle and tech to stealth bombers\"", fSans, 12, Muted);
                    cfg.Orders[t] = Field(new Rect(x + 130, y + 16, inner - 130, 64), cfg.Orders[t] ?? "", true, 100 + t);
                    y += 92;
                }
            }
            y += 14;

            Head(x, y, "MAP"); y += 28;
            Text(new Rect(x, y, 100, 36), "Seed", fSans, 14, Muted);
            var seedStr = Field(new Rect(x + 60, y, 120, 36), cfg.Seed.ToString(), false, 200);
            if (int.TryParse(seedStr, out var s)) cfg.Seed = s;
            if (Button(new Rect(x + 188, y, 96, 36), "Random", false, true, true, 14)) cfg.Seed = Random.Range(1, 99999);
            float zx = x + 310, zw = (inner - 310 - 4 * 6) / 5;
            for (int i = 0; i < Map.Presets.Length && i < 5; i++)
            {
                var br = new Rect(zx + i * (zw + 6), y, zw, 36);
                bool on = Map.Presets[i].size == cfg.MapSize;
                Round(br, 8, on ? Cream : Hover(br) ? BtnHot : Btn);
                Text(br, $"{Map.Presets[i].name} {Map.Presets[i].size}", on ? fSansBold : fSans, 13, on ? Night : Ink, TextAnchor.MiddleCenter);
                if (Click(br)) cfg.MapSize = Map.Presets[i].size;
            }
            y += 48;

            var tr = new Rect(x, y, inner, 28);
            var box = new Rect(x, y + 5, 18, 18);
            Round(box, 4, cfg.Open ? Cream : Night, cfg.Open ? Cream : Line, 1f);
            if (cfg.Open) Text(box, "✓", fSansBold, 14, Night, TextAnchor.MiddleCenter);
            Text(new Rect(x + 28, y, inner - 28, 28), "<b>Open arena</b>: outside agents can join mid-game (the map grows with each join, up to 8 players)", fSans, 14, Ink);
            if (Click(tr)) cfg.Open = !cfg.Open;
            y += 48;

            Text(new Rect(x, y, inner, 20), $"LLM control API: http://127.0.0.1:{Runner.Port}/" + (Runner.ApiError != null ? $"  ({Runner.ApiError})" : ""), fMono, 13, Runner.ApiError != null ? Amber : Muted);
            y += 40;
            Text(new Rect(x, y - 14, inner, 72), "<b>Claude</b> / <b>Codex</b>: the game launches that CLI and hands it the team over MCP (arena/battle.mjs).\n<b>External</b>: leave the team for any MCP client you connect yourself (mcp/server.js, PEZ_TEAM=n).", fSans, 13, Muted, TextAnchor.UpperLeft, true);
            y += 58;
            if (Runner.AgentStatus != null) Text(new Rect(x, y - 4, inner, 20), Runner.AgentStatus, fSans, 13, Amber);
            y += 18 + 8;
            var start = new Rect(x, r.yMax - pad - 52, inner, 52);
            Round(start, 8, Hover(start) ? Color.white : Cream);
            float sw = Spaced(0, 0, 0, "START", fHeadBold, 20, Night, 0.12f, false);
            Spaced(start.center.x - sw / 2, start.y + 15, 22, "START", fHeadBold, 20, Night, 0.12f);
            if (Click(start)) Runner.StartGame(cfg);
        }
    }
}
