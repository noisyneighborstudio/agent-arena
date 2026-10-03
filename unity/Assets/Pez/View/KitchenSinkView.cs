using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The kitchen sink room's host side (GameRunner -kitchensink; the scenario itself is Sim/KitchenSink.cs). It opens
    /// the camera on the first district at the game's default angle, and keeps the room light on a machine it shares
    /// with the live arena: 30 fps while someone watches the stream, 3 fps once nobody has for a minute.
    /// </summary>
    public class KitchenSinkView : MonoBehaviour
    {
        public const int Fps = 30, IdleFps = 3;
        public const float IdleAfter = 60f;
        public GameRunner Runner;
        float lastWatched;
        World placedFor;

        void Update()
        {
            var w = Runner != null && Runner.Game != null ? Runner.Game.World : null;
            if (w?.Showcase != null && placedFor != w && Runner.View != null && Runner.View.World == w && Runner.Camera != null)
            {
                placedFor = w;
                var d = w.Showcase.Districts[0];
                Runner.Camera.LookAt(WorldView.W(d.Focus));
                Runner.Camera.Distance = d.Zoom;
                Runner.Camera.Yaw = 45f;
            }
            if (FrameServer.Streaming(FrameServer.MainView) || FrameServer.Wanted(FrameServer.MainView)) lastWatched = Time.unscaledTime;
            int want = Time.unscaledTime - lastWatched < IdleAfter ? Fps : IdleFps;
            if (Application.targetFrameRate != want) Application.targetFrameRate = want;
        }
    }
}
