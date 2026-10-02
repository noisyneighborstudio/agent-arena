using System.Collections.Generic;
using System.Linq;
using Pez.Sim;
using UnityEngine;
using Terrain = Pez.Sim.Terrain;

namespace Pez.View
{
    /// <summary>IMGUI HUD: C&C-style sidebar, minimap, scoreboard, chat/command feed, selection, menus.</summary>
    public class Hud : MonoBehaviour
    {
        public GameRunner Runner;
        const float SideW = 270f;
        const float MiniSize = 210f;
        Texture2D white, minimap;
        Color32[] miniPixels;
        float nextMini;
        GUIStyle label, small, title, button, chat, banner, box;
        bool stylesReady;
        Rect sideRect, miniRect;

        World W => Runner.Game.World;
        int Team => Runner.HumanTeam;
        float Scale => Mathf.Max(1f, Screen.height / 1080f);

        public bool IsOverUi(Vector2 mouse)
        {
            var gui = new Vector2(mouse.x, Screen.height - mouse.y) / Scale;
            return Runner.InMenu || sideRect.Contains(gui) || miniRect.Contains(gui);
        }

        void Styles()
        {
            if (stylesReady) return;
            stylesReady = true;
            white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply();
            label = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
            label.normal.textColor = new Color(0.92f, 0.92f, 0.88f);
            small = new GUIStyle(label) { fontSize = 12, wordWrap = true };
            title = new GUIStyle(label) { fontSize = 18, fontStyle = FontStyle.Bold };
            title.normal.textColor = new Color(1f, 0.72f, 0.2f);
            button = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleLeft, richText = true, padding = new RectOffset(8, 6, 4, 4) };
            chat = new GUIStyle(label) { fontSize = 15, wordWrap = true };
            banner = new GUIStyle(label) { fontSize = 48, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            box = new GUIStyle(GUI.skin.box);
        }

        void Fill(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = old; }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
        string TeamTag(int t) => t >= 0 ? $"<color=#{Hex(Mats.Team(t))}>{W.Teams[t].PlayerName ?? W.Teams[t].Name}</color>" : "server";

        void OnGUI()
        {
            if (Runner == null || Runner.Game == null) return;
            Styles();
            GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1));
            float sw = Screen.width / Scale, sh = Screen.height / Scale;

            if (Runner.InMenu) { Menu(sw, sh); return; }

            HealthBars();
            Scoreboard();
            Feed(sh);
            if (Team >= 0) { Sidebar(sw, sh); Selection(sw, sh); }
            else { sideRect = Rect.zero; SpectatorPanel(sw); }
            Minimap(sw, sh);
            DragBox();
            if (W.GameOver)
            {
                Fill(new Rect(0, sh / 2 - 60, sw, 120), new Color(0, 0, 0, 0.6f));
                var msg = W.Winner >= 0 ? $"{(W.Teams[W.Winner].PlayerName ?? W.Teams[W.Winner].Name).ToUpper()} WINS" : "DRAW";
                banner.normal.textColor = W.Winner >= 0 ? Mats.Team(W.Winner) : Color.white;
                GUI.Label(new Rect(0, sh / 2 - 60, sw, 90), msg, banner);
                if (GUI.Button(new Rect(sw / 2 - 80, sh / 2 + 30, 160, 26), "Back to menu")) Runner.InMenu = true;
            }
        }

        // ---------------------------------------------------------------- menu

        static readonly string[] ControllerOptions = { "human", "ai", "claude", "codex", "llm" };

        void Menu(float sw, float sh)
        {
            Fill(new Rect(0, 0, sw, sh), new Color(0.02f, 0.03f, 0.04f, 0.72f));
            var r = new Rect(sw / 2 - 330, sh / 2 - 230, 660, 460);
            Fill(r, new Color(0.08f, 0.09f, 0.1f, 0.95f));
            GUILayout.BeginArea(new Rect(r.x + 24, r.y + 18, r.width - 48, r.height - 36));
            var big = new GUIStyle(title) { fontSize = 40 };
            GUILayout.Label("PEZ", big);
            GUILayout.Label("Real-time strategy. Command it with the mouse, or hand a team to an LLM over MCP.", small);
            GUILayout.Space(14);
            var cfg = Runner.MenuConfig;
            for (int t = 0; t < cfg.Controllers.Length; t++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<color=#{Hex(Mats.Team(t))}><b>{new[] { "Blue", "Red", "Green", "Yellow" }[t]}</b></color>", label, GUILayout.Width(80));
                int cur = System.Array.IndexOf(ControllerOptions, cfg.Controllers[t]);
                int next = GUILayout.Toolbar(System.Math.Max(0, cur), new[] { "Human", "Scripted AI", "Claude", "Codex", "External" });
                if (next != cur) cfg.Controllers[t] = ControllerOptions[next];
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Map seed", label, GUILayout.Width(80));
            var seedStr = GUILayout.TextField(cfg.Seed.ToString(), GUILayout.Width(100));
            if (int.TryParse(seedStr, out var s)) cfg.Seed = s;
            if (GUILayout.Button("Random", GUILayout.Width(80))) cfg.Seed = Random.Range(1, 99999);
            GUILayout.EndHorizontal();
            GUILayout.Space(12);
            GUILayout.Label($"LLM control API: <b>http://127.0.0.1:{Runner.Port}/</b>  {(Runner.ApiError != null ? "<color=#ff6644>(" + Runner.ApiError + ")</color>" : "")}", small);
            GUILayout.Label("<b>Claude</b> / <b>Codex</b>: the game launches that CLI and hands it the team over MCP (arena/battle.mjs).\n<b>External</b>: leave the team for any MCP client you connect yourself (mcp/server.js, PEZ_TEAM=n).", small);
            if (Runner.AgentStatus != null) GUILayout.Label($"<color=#ffb84d>{Runner.AgentStatus}</color>", small);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("<b>START</b>", new GUIStyle(button) { alignment = TextAnchor.MiddleCenter, fontSize = 20 }, GUILayout.Height(44))) Runner.StartGame(cfg);
            GUILayout.EndArea();
        }

        // ---------------------------------------------------------------- in game

        void Scoreboard()
        {
            float y = 8;
            Fill(new Rect(8, y, 330, 24 + W.Teams.Count * 20), new Color(0, 0, 0, 0.45f));
            GUI.Label(new Rect(16, y + 2, 320, 22), $"<b>{FormatTime(W.Time)}</b>   speed x{Runner.Game.Speed:0.##}{(Runner.Game.Paused ? "  PAUSED" : "")}   API :{Runner.Port}", label);
            y += 22;
            foreach (var t in W.Teams)
            {
                var tag = $"<color=#{Hex(Mats.Team(t.Id))}><b>{t.Name}</b></color> {t.PlayerName ?? t.Controller}";
                var stats = t.Defeated ? "<color=#888>defeated</color>" : $"steel {t.Amount("steel")}  S{W.Owned(t.Id).Count(e => e.IsStructure)}  U{W.Owned(t.Id).Count(e => !e.IsStructure)}  K{t.Stats.Kills}";
                GUI.Label(new Rect(16, y, 320, 20), $"{tag}  {stats}", label);
                y += 20;
            }
        }

        static string FormatTime(float s) => $"{(int)s / 60:00}:{(int)s % 60:00}";

        void Feed(float sh)
        {
            // Chat and LLM commands, newest at the bottom, fading with age.
            var lines = new List<(float t, string s)>();
            foreach (var e in W.Events.Skip(System.Math.Max(0, W.Events.Count - 400)))
                if (e.Type == "chat" || e.Type == "defeated" || e.Type == "game_over")
                    lines.Add((e.Tick * World.Dt, e.Type == "chat" ? $"{TeamTag(e.Team)}: {e.Text}" : $"<b>{e.Text}</b>"));
            foreach (var c in Runner.CommandFeed) lines.Add((c.time, $"<size=12><color=#aaa>{TeamTag(c.team)} ▸ {c.text}</color></size>"));
            var recent = lines.Where(l => W.Time - l.t < 30f).OrderBy(l => l.t).ToList();
            recent = recent.Skip(System.Math.Max(0, recent.Count - 9)).ToList();
            float y = sh - 16;
            float width = 560;
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                float h = chat.CalcHeight(new GUIContent(recent[i].s), width);
                y -= h + 2;
                float a = Mathf.Clamp01((30f - (W.Time - recent[i].t)) / 5f);
                Fill(new Rect(10, y, width + 12, h + 2), new Color(0, 0, 0, 0.45f * a));
                var old = GUI.color; GUI.color = new Color(1, 1, 1, a);
                GUI.Label(new Rect(16, y, width, h + 2), recent[i].s, chat);
                GUI.color = old;
            }
        }

        static readonly Dictionary<string, string> ItemColor = new Dictionary<string, string>
        {
            { "iron_ore", "c98a6e" }, { "copper_ore", "f2a35a" }, { "crystal", "6fd0ff" }, { "uranium", "8cff5a" },
            { "steel", "d6dde3" }, { "copper", "ff9c4a" }, { "circuits", "5cff95" }, { "lenses", "9be8ff" }, { "plasma", "ff6be6" }, { "composite", "b08cff" },
        };

        string CostLine(EntityDef d, Team t) => string.Join(" ", d.Cost.Select(kv =>
            $"<color=#{(t.Amount(kv.Key) >= kv.Value ? ItemColor[kv.Key] : "ff5f4a")}>{kv.Value} {Short(kv.Key)}</color>"));

        static string Short(string item) => item switch
        {
            "iron_ore" => "iron", "copper_ore" => "cu ore", "copper" => "cu", "circuits" => "circ", "composite" => "comp", _ => item,
        };

        void Sidebar(float sw, float sh)
        {
            sideRect = new Rect(sw - SideW, 0, SideW, sh);
            Fill(sideRect, new Color(0.07f, 0.075f, 0.08f, 0.93f));
            Fill(new Rect(sw - SideW, 0, 2, sh), new Color(1f, 0.6f, 0.15f, 0.8f));
            var t = W.Teams[Team];
            float x = sw - SideW + 10, y = MiniSize + 18;

            // Stockpile: two columns, amount and net rate.
            int col = 0;
            foreach (var item in Defs.Items)
            {
                int n = t.Amount(item);
                float r = t.Rates.TryGetValue(item, out var v) ? v : 0;
                string rate = Mathf.Abs(r) >= 0.05f ? $" <size=10><color=#{(r > 0 ? "8f8" : "f88")}>{(r > 0 ? "+" : "")}{r:0.#}</color></size>" : "";
                GUI.Label(new Rect(x + col * 125, y, 125, 18), $"<size=12><color=#{ItemColor[item]}>{Short(item)}</color> <b>{n}</b>{rate}</size>", small);
                col ^= 1;
                if (col == 0) y += 16;
            }
            y += 6;
            float frac = t.PowerProduced == 0 ? 0 : Mathf.Clamp01(t.PowerUsed / (float)t.PowerProduced);
            Fill(new Rect(x, y, 250, 8), new Color(0.2f, 0.2f, 0.2f));
            Fill(new Rect(x, y, 250 * frac, 8), t.LowPower ? new Color(1f, 0.25f, 0.2f) : new Color(0.3f, 0.9f, 0.4f));
            GUI.Label(new Rect(x, y + 7, 250, 18), $"<size=11>Power {t.PowerUsed}/{t.PowerProduced}{(t.LowPower ? "  <color=#ff5544>LOW POWER</color>" : "")}</size>", small);
            y += 26;

            var input = Runner.Input;
            var buildingNow = t.StructureQueue.Count > 0 ? W.Get(t.StructureQueue[0].StructureId) : null;
            var cell = new GUIStyle(button) { fontSize = 11, padding = new RectOffset(5, 3, 2, 2), wordWrap = false };
            const float bw = 124, bh = 32;

            GUI.Label(new Rect(x, y, 250, 18), "<b>STRUCTURES</b>", label); y += 19;
            col = 0;
            foreach (var d in Defs.All.Values.Where(d => d.IsStructure && d.Buildable))
            {
                var missing = W.MissingPrereq(Team, d);
                string extra = buildingNow != null && buildingNow.Def == d ? $" <color=#ffb84d>{(int)(buildingNow.BuildProgress * 100)}%</color>" : "";
                bool placing = input.PlacingKey == d.Key;
                GUI.enabled = missing == null;
                var tip = $"<b>{d.Name}</b>: {d.Description}\nCost: {d.CostText}" + (missing != null ? $"\n<color=#ff7755>{missing}</color>" : "");
                if (GUI.Button(new Rect(x + col * (bw + 2), y, bw, bh), new GUIContent($"{(placing ? "▶" : "")}{d.Name}{extra}\n<size=9>{CostLine(d, t)}</size>", tip), cell))
                    input.PlacingKey = placing ? null : d.Key;
                GUI.enabled = true;
                col ^= 1;
                if (col == 0) y += bh + 2;
            }
            if (col == 1) y += bh + 2;
            y += 4;
            GUI.Label(new Rect(x, y, 250, 18), "<b>UNITS</b>", label); y += 19;
            col = 0;
            foreach (var d in Defs.All.Values.Where(d => !d.IsStructure))
            {
                var missing = W.MissingPrereq(Team, d);
                var q = t.UnitQueues[d.BuiltBy];
                int queued = q.Count(p => p.Key == d.Key);
                string prog = q.Count > 0 && q[0].Key == d.Key ? $" {(int)(q[0].Progress / d.BuildTime * 100)}%" : "";
                GUI.enabled = missing == null;
                var tip = $"<b>{d.Name}</b>: {d.Description}\nCost: {d.CostText}" + (missing != null ? $"\n<color=#ff7755>{missing}</color>" : "");
                var label2 = $"{d.Name}{(queued > 0 ? $" <color=#7fd0ff>x{queued}{prog}</color>" : "")}\n<size=9>{CostLine(d, t)}</size>";
                if (GUI.Button(new Rect(x + col * (bw + 2), y, bw, bh), new GUIContent(label2, tip), cell))
                {
                    if (Event.current.button == 1) input.Exec("type", "cancel", "unit", d.Key);
                    else input.Exec("type", "train", "unit", d.Key, "count", Event.current.shift ? 5 : 1);
                }
                GUI.enabled = true;
                col ^= 1;
                if (col == 0) y += bh + 2;
            }
            if (col == 1) y += bh + 2;

            // Tooltip / help / errors share the bottom of the sidebar.
            float by = Mathf.Max(y + 4, sh - 96);
            if (!string.IsNullOrEmpty(GUI.tooltip))
                GUI.Label(new Rect(x, by, 250, 92), $"<size=11>{GUI.tooltip}</size>", small);
            else if (input.LastError != null && Time.time - input.LastErrorTime < 4f)
                GUI.Label(new Rect(x, by, 250, 60), $"<color=#ff7755>{input.LastError}</color>", small);
            else
                GUI.Label(new Rect(x, by, 250, 92), "<size=10><color=#999>Click: build/train (shift x5, right-click cancel). Right-click map: move/attack/mine/rally. F+right-click: attack-move. G: deploy. X: stop. Del: sell. WASD/edge pan, Q/E rotate, wheel zoom.</color></size>", small);
        }

        void SpectatorPanel(float sw)
        {
            float x = sw - 290, y = 270;
            Fill(new Rect(x - 8, y - 4, 290, 120), new Color(0, 0, 0, 0.45f));
            GUI.Label(new Rect(x, y, 280, 20), "<b>SPECTATING</b>", label);
            y += 22;
            GUI.Label(new Rect(x, y, 280, 20), $"Speed x{Runner.Game.Speed:0.##}", label);
            if (GUI.Button(new Rect(x + 120, y, 30, 20), "-")) Runner.Game.Speed = Mathf.Max(0.25f, Runner.Game.Speed / 2);
            if (GUI.Button(new Rect(x + 154, y, 30, 20), "+")) Runner.Game.Speed = Mathf.Min(8f, Runner.Game.Speed * 2);
            if (GUI.Button(new Rect(x + 190, y, 70, 20), Runner.Game.Paused ? "Resume" : "Pause")) Runner.Game.Paused = !Runner.Game.Paused;
            y += 26;
            GUI.Label(new Rect(x, y, 280, 60), "<size=12><color=#aaa>Fog is off while spectating. Team commands from LLMs appear in the feed bottom-left.</color></size>", small);
        }

        void Selection(float sw, float sh)
        {
            var sel = Runner.View.Selected.Select(W.Get).Where(e => e != null).ToList();
            if (sel.Count == 0) return;
            var r = new Rect(sw / 2 - 260, sh - 70, 520, 60);
            Fill(r, new Color(0, 0, 0, 0.55f));
            string text;
            if (sel.Count == 1)
            {
                var e = sel[0];
                text = $"<b>{e.Def.Name}</b> #{e.Id}   HP {(int)e.Hp}/{e.Def.MaxHp}   {(e.IsStructure ? (e.IsComplete ? "" : $"building {(int)(e.BuildProgress * 100)}%") : e.OrderName)}" +
                       (e.IsHarvester ? $"   cargo {e.Cargo}/{e.Def.HarvestCapacity} {(e.CargoType >= 0 ? Defs.Ores[e.CargoType] : "")}{(e.HarvestType >= 0 ? $" (assigned {Defs.Ores[e.HarvestType]})" : "")}" : "") +
                       (e.IsStructure && e.Def.Recipes.Length > 0 ? $"   {(e.Working ? "<color=#7f7>working</color>" : "<color=#f97>idle: missing inputs</color>")}" : "") +
                       $"\n<size=12><color=#aaa>{e.Def.Description}</color></size>";
            }
            else text = $"<b>{sel.Count} selected</b>   " + string.Join(", ", sel.GroupBy(e => e.Def.Name).Select(g => $"{g.Count()} {g.Key}"));
            GUI.Label(new Rect(r.x + 10, r.y + 6, r.width - 20, r.height - 8), text, small);
        }

        void HealthBars()
        {
            var cam = Runner.Camera.Cam;
            foreach (var v in Runner.View.Views.Values)
            {
                var e = v.E;
                if (e.Dead || !v.Rig.Root.gameObject.activeInHierarchy) continue;
                bool sel = Runner.View.Selected.Contains(e.Id);
                if (!sel && e.Hp >= e.Def.MaxHp && e.IsComplete) continue;
                float h = e.IsStructure ? 1.6f : 0.9f;
                var sp = cam.WorldToScreenPoint(v.Rig.Root.position + Vector3.up * h);
                if (sp.z < 0) continue;
                var p = new Vector2(sp.x, Screen.height - sp.y) / Scale;
                float w = e.IsStructure ? 46 : 26;
                float f = Mathf.Clamp01(e.Hp / e.Def.MaxHp);
                Fill(new Rect(p.x - w / 2 - 1, p.y - 1, w + 2, 6), new Color(0, 0, 0, 0.7f));
                Fill(new Rect(p.x - w / 2, p.y, w * f, 4), f > 0.6f ? new Color(0.3f, 0.95f, 0.35f) : f > 0.3f ? new Color(1f, 0.8f, 0.2f) : new Color(1f, 0.25f, 0.2f));
                if (!e.IsComplete) { Fill(new Rect(p.x - w / 2, p.y + 6, w * e.BuildProgress, 3), new Color(1f, 0.7f, 0.2f)); }
            }
        }

        void DragBox()
        {
            var input = Runner.Input;
            if (input == null || !input.Dragging) return;
            var a = input.DragStart / Scale; var b = (Vector2)UnityEngine.Input.mousePosition / Scale;
            float sh = Screen.height / Scale;
            var r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), sh - Mathf.Max(a.y, b.y), Mathf.Max(a.x, b.x), sh - Mathf.Min(a.y, b.y));
            Fill(r, new Color(0.3f, 1f, 0.4f, 0.12f));
            Fill(new Rect(r.x, r.y, r.width, 1), new Color(0.3f, 1f, 0.4f, 0.8f));
            Fill(new Rect(r.x, r.yMax, r.width, 1), new Color(0.3f, 1f, 0.4f, 0.8f));
            Fill(new Rect(r.x, r.y, 1, r.height), new Color(0.3f, 1f, 0.4f, 0.8f));
            Fill(new Rect(r.xMax, r.y, 1, r.height), new Color(0.3f, 1f, 0.4f, 0.8f));
        }

        void Minimap(float sw, float sh)
        {
            var m = W.Map;
            if (minimap == null || minimap.width != m.W)
            {
                minimap = new Texture2D(m.W, m.H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                miniPixels = new Color32[m.W * m.H];
            }
            if (Time.unscaledTime >= nextMini)
            {
                nextMini = Time.unscaledTime + 0.25f;
                bool[] vis = Team >= 0 ? W.Teams[Team].Visible : null;
                bool[] exp = Team >= 0 ? W.Teams[Team].Explored : null;
                for (int i = 0; i < miniPixels.Length; i++)
                {
                    Color c = m.Ore[i] > 0 ? WorldView.OreColors[m.OreType[i]] : m.Tiles[i] switch
                    {
                        Terrain.Rock => new Color(0.4f, 0.38f, 0.36f),
                        Terrain.Water => new Color(0.1f, 0.3f, 0.45f),
                        Terrain.Dirt => TerrainView.Dirt,
                        _ => TerrainView.GrassA,
                    };
                    if (vis != null && !vis[i]) c *= 0.5f;
                    if (exp != null && !exp[i]) c = new Color(0.02f, 0.02f, 0.03f);
                    miniPixels[i] = c;
                }
                foreach (var e in W.Entities)
                {
                    if (e.Dead || (Team >= 0 && e.Team != Team && !W.IsVisibleTo(Team, e) && !(e.IsStructure && W.Teams[Team].KnownEnemyStructures.ContainsKey(e.Id)))) continue;
                    Color32 c = Mats.Team(e.Team);
                    if (e.IsStructure)
                    {
                        for (int y = 0; y < e.Def.SizeY; y++) for (int x = 0; x < e.Def.SizeX; x++) miniPixels[m.Idx(e.Origin.X + x, e.Origin.Y + y)] = c;
                    }
                    else
                    {
                        var t = Int2.Of(e.Pos);
                        if (m.InBounds(t.X, t.Y)) miniPixels[m.Idx(t.X, t.Y)] = Color.Lerp(c, Color.white, 0.35f);
                    }
                }
                minimap.SetPixels32(miniPixels);
                minimap.Apply();
            }
            float size = Team >= 0 ? MiniSize : SideW - 20;
            miniRect = new Rect(sw - size - (Team >= 0 ? (SideW - size) / 2 : 10), 10, size, size);
            Fill(new Rect(miniRect.x - 2, miniRect.y - 2, size + 4, size + 4), new Color(1f, 0.6f, 0.15f, 0.7f));
            // Texture row 0 (south) already draws at the bottom of the rect, matching the camera marker math below.
            GUI.DrawTexture(miniRect, minimap);
            // Camera focus marker
            var f = Runner.Camera.Focus;
            var mp = new Vector2(miniRect.x + f.x / m.W * size, miniRect.y + (1 - f.z / m.H) * size);
            Fill(new Rect(mp.x - 3, mp.y - 3, 6, 6), Color.white);
            var ev = Event.current;
            if ((ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag) && ev.button == 0 && miniRect.Contains(ev.mousePosition))
            {
                var rel = (ev.mousePosition - miniRect.position) / size;
                Runner.Camera.LookAt(new Vector3(rel.x * m.W, 0, (1 - rel.y) * m.H));
                ev.Use();
            }
        }
    }
}
