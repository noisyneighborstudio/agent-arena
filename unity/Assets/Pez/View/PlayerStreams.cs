using System.Collections.Generic;
using System.Linq;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Per-player live streams: a second camera renders each watched team's view in turn (only what that team can
    /// see, under its own fog) and hands JPEG frames to the FrameServer. A small director keeps the camera on the
    /// action: an active alert, otherwise the army in combat, otherwise the base.
    /// </summary>
    public class PlayerStreams : MonoBehaviour
    {
        public GameRunner Runner;
        public int Width = 1280, Height = 720;
        public float Interval = 0.15f; // per stream: about 6-7 frames a second
        Camera cam;
        RenderTexture rt;
        Texture2D tex;
        readonly Dictionary<int, (Vector3 focus, float last, int seat)> state = new Dictionary<int, (Vector3, float, int)>();
        // A viewer looking around takes over the stream camera; the director returns after 20 seconds of no input.
        readonly Dictionary<int, (Vector3 focus, float size, float yaw, float until)> manual = new Dictionary<int, (Vector3, float, float, float)>();
        const float StreamPitch = 55f;

        void Start()
        {
            var main = Runner.Camera.Cam;
            cam = new GameObject("PlayerStreamCamera").AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.enabled = false; // rendered manually
            rt = new RenderTexture(Width, Height, 24) { antiAliasing = 2 };
            tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        }

        void LateUpdate()
        {
            var view = Runner.View;
            if (Runner.InMenu || view == null || cam == null) return;
            var w = Runner.Game.World;
            while (FrameServer.CamOps.TryDequeue(out var op))
            {
                if (op.Team < 0) { Runner.Camera.Nudge(op.Dx, op.Dy, op.Zoom, op.Yaw); continue; }
                if (!state.TryGetValue(op.Team, out var st)) continue;
                (Vector3 focus, float size, float yaw, float until) m = manual.TryGetValue(op.Team, out var cur) && Time.unscaledTime < cur.until ? cur : (st.focus, 11f, 45f, 0f);
                var right = Quaternion.Euler(0, m.yaw, 0) * Vector3.right;
                var fwd = Quaternion.Euler(0, m.yaw, 0) * Vector3.forward;
                m.focus += right * op.Dx * m.size * 2f * Width / Height + fwd * op.Dy * m.size * 2f / Mathf.Sin(StreamPitch * Mathf.Deg2Rad);
                if (!float.IsNaN(op.X) && !float.IsNaN(op.Y)) m.focus = new Vector3(op.X, 0, op.Y); // "look at x,y"
                m.size = Mathf.Clamp(m.size * op.Zoom, 5f, 40f);
                m.yaw += op.Yaw;
                m.until = Time.unscaledTime + 20f;
                manual[op.Team] = m;
                state[op.Team] = (m.focus, st.last - Interval, st.seat); // render the change right away
            }
            // Render at most one stream per frame: the watched team that has waited longest.
            int pick = -1; float oldest = float.MaxValue;
            for (int t = 0; t < w.Teams.Count && t < 8; t++)
            {
                if (!FrameServer.Wanted(t) || w.Teams[t].Left) continue;
                state.TryGetValue(t, out var s);
                if (s.seat != w.Teams[t].Seat) { s = (Vector3.zero, 0, w.Teams[t].Seat); FrameServer.Forget(t); } // seat changed hands
                state[t] = s;
                if (Time.unscaledTime - s.last < Interval) continue;
                if (s.last < oldest) { oldest = s.last; pick = t; }
            }
            if (pick >= 0) Render(w, view, pick);
        }

        Vector3 Director(World w, int team)
        {
            var t = w.Teams[team];
            var alert = w.Alerts.Active(w, team).FirstOrDefault(a => a.Priority >= Priority.High && (w.Tick - a.LastTick) * World.Dt < 10f);
            if (alert != null) return WorldView.W(alert.Pos);
            var fighting = w.Entities.Where(e => !e.Dead && e.Team == team && !e.IsStructure && e.IsArmed &&
                                                 (e.Order == Order.Attack || e.Order == Order.AttackMove || w.Time - e.LastHitTime < 5f)).ToList();
            if (fighting.Count > 0)
                return new Vector3(fighting.Average(e => e.Pos.X), 0, fighting.Average(e => e.Pos.Y));
            var hq = w.Entities.FirstOrDefault(e => !e.Dead && e.Team == team && e.Def.Key == "command_center")
                     ?? w.Entities.FirstOrDefault(e => !e.Dead && e.Team == team && e.IsStructure);
            return WorldView.W(hq != null ? hq.Center : t.StartPos);
        }

        void Render(World w, WorldView view, int team)
        {
            var s = state[team];
            float size = 11f, yaw = 45f;
            Vector3 focus;
            if (manual.TryGetValue(team, out var m) && Time.unscaledTime < m.until) { focus = m.focus; size = m.size; yaw = m.yaw; }
            else
            {
                var target = Director(w, team);
                float dt = s.last == 0 ? 10f : Time.unscaledTime - s.last;
                focus = s.last == 0 ? target : Vector3.Lerp(s.focus, target, 1f - Mathf.Exp(-dt * 1.5f));
            }
            state[team] = (focus, Time.unscaledTime, s.seat);

            var rot = Quaternion.Euler(StreamPitch, yaw, 0f);
            cam.orthographic = true;
            cam.orthographicSize = size;
            cam.transform.rotation = rot;
            cam.transform.position = focus - rot * Vector3.forward * 150f;
            cam.cullingMask = ~(TerrainView.TeamFogMask | 1 << TerrainView.MainFogLayer) | 1 << (TerrainView.TeamFogLayerBase + team);
            view.Terrain.UpdateTeamFog(w, team);

            view.SetPov(team);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            view.SetPov(view.PovTeam); // back to the local view before the main camera draws

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;
            FrameServer.Submit(team, tex.EncodeToJPG(72));
        }
    }
}
