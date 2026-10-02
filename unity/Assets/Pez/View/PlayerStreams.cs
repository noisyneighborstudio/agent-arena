using System.Collections.Generic;
using System.Linq;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Per-player live streams: a second camera renders each watched team's view in turn (only what that team can
    /// see, under its own fog) and hands its frames to the FrameServer: every frame (up to FrameServer.StreamFps) while
    /// someone is watching the stream live, a few a second when it's only polled for snapshots. A small director keeps
    /// the camera on the action: an active alert, otherwise the army in combat, otherwise the base.
    /// </summary>
    public class PlayerStreams : MonoBehaviour
    {
        public GameRunner Runner;
        public int Width = 1280, Height = 720; // the cap for player streams and look stills (bandwidth)
        public float SnapshotInterval = 0.15f; // a stream polled for snapshots (the look tool), not watched live
        Camera cam;
        RenderTexture rt, resolved;
        readonly Dictionary<int, (Vector3 focus, float last, int seat)> state = new Dictionary<int, (Vector3, float, int)>();
        // The director's target is a scan over every entity: refresh it a few times a second, not every frame.
        readonly Dictionary<int, (Vector3 target, float at)> directed = new Dictionary<int, (Vector3, float)>();
        // A viewer looking around takes over the stream camera; the director returns after 20 seconds of no input.
        readonly Dictionary<int, (Vector3 focus, float size, float yaw, float until)> manual = new Dictionary<int, (Vector3, float, float, float)>();
        const float StreamPitch = 55f;
        /// <summary>The boards' framing, as on the main view (RtsCamera.BoardOrthoSize).</summary>
        float DefaultSize => RtsCamera.BoardOrthoSize;
        Vector3 OverMap(World w, Vector3 focus, float size, float yaw) =>
            RtsCamera.KeepOverMap(focus, size, Width / (float)Height, yaw, StreamPitch, new Vector2(w.Map.W, w.Map.H));

        void Start()
        {
            var main = Runner.Camera.Cam;
            cam = new GameObject("PlayerStreamCamera").AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.enabled = false; // rendered manually
            rt = new RenderTexture(Width, Height, 24) { antiAliasing = 2 };
            resolved = new RenderTexture(Width, Height, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB); // the async readback can't read MSAA
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
                (Vector3 focus, float size, float yaw, float until) m = manual.TryGetValue(op.Team, out var cur) && Time.unscaledTime < cur.until ? cur : (st.focus, DefaultSize, 45f, 0f);
                var right = Quaternion.Euler(0, m.yaw, 0) * Vector3.right;
                var fwd = Quaternion.Euler(0, m.yaw, 0) * Vector3.forward;
                m.focus += right * op.Dx * m.size * 2f * Width / Height + fwd * op.Dy * m.size * 2f / Mathf.Sin(StreamPitch * Mathf.Deg2Rad);
                if (!float.IsNaN(op.X) && !float.IsNaN(op.Y)) m.focus = new Vector3(op.X, 0, op.Y); // "look at x,y"
                m.size = Mathf.Clamp(m.size * op.Zoom, 8f, 40f); // the art pack's zoom range
                m.yaw += op.Yaw;
                m.focus = OverMap(w, m.focus, m.size, m.yaw);
                m.until = Time.unscaledTime + 20f;
                manual[op.Team] = m;
                state[op.Team] = (m.focus, st.last - SnapshotInterval, st.seat); // render the change right away
            }
            bool rendered = false;
            for (int t = 0; t < w.Teams.Count && t < 8; t++)
            {
                if (!FrameServer.Wanted(t) || w.Teams[t].Left) continue;
                state.TryGetValue(t, out var s);
                if (s.seat != w.Teams[t].Seat) { s = (Vector3.zero, 0, w.Teams[t].Seat); FrameServer.Forget(t); directed.Remove(t); } // seat changed hands
                state[t] = s;
                // Live streams render at the stream's frame cap (the 0.9 absorbs frame-time jitter).
                float gap = FrameServer.Streaming(t) ? 0.9f / FrameServer.StreamFps : SnapshotInterval;
                if (Time.unscaledTime - s.last < gap || FrameServer.Busy(t)) continue;
                Render(w, view, t);
                rendered = true;
            }
            if (rendered)
            {
                // Back to the local view (and its sun) before the main camera draws.
                RtsCamera.AimSun(Runner.Camera.Yaw);
                view.SetPov(view.PovTeam);
            }
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
            float size = DefaultSize, yaw = 45f;
            Vector3 focus;
            if (manual.TryGetValue(team, out var m) && Time.unscaledTime < m.until) { focus = m.focus; size = m.size; yaw = m.yaw; }
            else
            {
                if (!directed.TryGetValue(team, out var d) || Time.unscaledTime - d.at > 0.25f) directed[team] = d = (Director(w, team), Time.unscaledTime);
                var target = OverMap(w, d.target, size, yaw); // a base in a corner frames with the map, not the void
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
            RtsCamera.AimSun(yaw); // the sun sits at the upper left of whichever view renders
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            Graphics.Blit(rt, resolved);
            FrameServer.Encode(resolved, team);
        }
    }
}
