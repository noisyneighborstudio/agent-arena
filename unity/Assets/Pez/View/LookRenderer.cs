using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The browser build's "look": when the agent calls look, the gateway asks this page (WebClient.LookLoop) for a picture
    /// of the player's own view, and this renders it offscreen with a second camera set up like PlayerStreams' per-team one
    /// (the stream tier of PezPost, 55 degree pitch, 45 degree yaw, the board framing) so the agent sees what the old native
    /// app's frames showed. The player's own camera is never touched. It only ever draws a world whose point of view is a
    /// team, so the main view's fog overlay (what that player sees, shroud included) is in the picture and nothing unfogged is.
    /// No HUD: the IMGUI HUD isn't part of any camera; the health bars are PezPost's overlay, as in the stream frames.
    /// </summary>
    public class LookRenderer : MonoBehaviour
    {
        public GameRunner Runner;
        public const int Width = 960, Height = 540, Quality = 80;
        const float Pitch = 55f, Yaw = 45f;
        Camera cam;
        RenderTexture rt, resolved;
        Texture2D tex;

        /// <summary>The picture as a JPEG, or null with a reason in <paramref name="why"/> (no view yet, a spectator's view).</summary>
        public byte[] Render(float x, float y, out string why)
        {
            why = null;
            var view = Runner != null ? Runner.View : null;
            if (view == null || Runner.InMenu || Runner.Game == null) { why = "no game on screen yet"; return null; }
            int pov = view.PovTeam;
            if (pov < 0) { why = "this page shows everything (a spectator), not one team's view"; return null; } // never an unfogged picture
            var w = Runner.Game.World;
            if (pov >= w.Teams.Count) { why = "no such team"; return null; }
            Setup();

            float size = RtsCamera.BoardOrthoSize;
            Vector3 focus;
            if (!float.IsNaN(x) && !float.IsNaN(y)) focus = new Vector3(Mathf.Clamp(x, 0, w.Map.W), 0, Mathf.Clamp(y, 0, w.Map.H)); // "look at x,y": anywhere on the map
            else focus = RtsCamera.KeepOverMap(PlayerStreams.Director(w, pov), size, Width / (float)Height, Yaw, Pitch, new Vector2(w.Map.W, w.Map.H)); // a base in a corner frames with the map, not the void

            var rot = Quaternion.Euler(Pitch, Yaw, 0f);
            cam.orthographic = true;
            cam.orthographicSize = size;
            cam.aspect = Width / (float)Height;
            cam.transform.rotation = rot;
            cam.transform.position = focus - rot * Vector3.forward * RtsCamera.BackDistance(size);
            cam.farClipPlane = RtsCamera.FarClip(size);
            cam.cullingMask = Runner.Camera.Cam.cullingMask; // the main view's: its own fog overlay, no team fog layers, no bars layer
            RtsCamera.FitShadows(size);
            RtsCamera.AimSun(Yaw);
            cam.targetTexture = rt;
            try { cam.Render(); }
            finally
            {
                cam.targetTexture = null;
                // Back to the player's own view's sun and shadows before the main camera draws.
                RtsCamera.AimSun(Runner.Camera.Yaw);
                RtsCamera.FitShadows(Runner.Camera.Distance);
            }
            Graphics.Blit(rt, resolved); // the readback can't read the MSAA target
            var prev = RenderTexture.active;
            RenderTexture.active = resolved;
            try
            {
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                return tex.EncodeToJPG(Quality);
            }
            finally { RenderTexture.active = prev; }
        }

        void Setup()
        {
            if (cam != null) return;
            var main = Runner.Camera.Cam;
            cam = new GameObject("LookCamera").AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.RemoveAllCommandBuffers(); // PezPost adds its own
            cam.enabled = false; // rendered by hand
            cam.gameObject.AddComponent<PezPost>().Stream = true; // the stream tier (-streamfx, light by default)
            rt = new RenderTexture(Width, Height, 24) { antiAliasing = 2 };
            resolved = new RenderTexture(Width, Height, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB);
            tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        }
    }
}
