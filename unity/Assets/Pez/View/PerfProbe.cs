using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Frame-time measurement, off unless the app is started with -perfprobe PATH. Uncaps the frame rate (so the numbers
    /// show headroom, not the 60 fps cap) and every 5 s appends a line to PATH: frames, average / p50 / p95 / max frame
    /// time from unscaledDeltaTime, the GPU time when the platform reports it (FrameTimingManager), and how many
    /// player-stream frames were rendered per second. Used to compare rendering changes; never on in the arena.
    /// </summary>
    public class PerfProbe : MonoBehaviour
    {
        string path;
        readonly List<float> ms = new List<float>(4096);
        readonly List<double> gpu = new List<double>(4096);
        readonly FrameTiming[] timings = new FrameTiming[1];
        float windowStart;
        int streamStart;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-perfprobe");
            if (i < 0) return;
            var probe = new GameObject("PerfProbe").AddComponent<PerfProbe>();
            probe.path = i + 1 < args.Length ? args[i + 1] : "/tmp/pezz-perf.txt";
            DontDestroyOnLoad(probe.gameObject);
        }

        void LateUpdate()
        {
            Application.targetFrameRate = -1; // GameRunner caps at 60 in Awake; measure what the frame really costs
            QualitySettings.vSyncCount = 0;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0) gpu.Add(timings[0].gpuFrameTime);
            if (Time.frameCount < 30) { windowStart = Time.unscaledTime; streamStart = PlayerStreams.RenderCount; return; }
            ms.Add(Time.unscaledDeltaTime * 1000f);
            float span = Time.unscaledTime - windowStart;
            if (span < 5f) return;
            ms.Sort();
            float avg = 0; foreach (var m in ms) avg += m; avg /= ms.Count;
            double g = 0; foreach (var x in gpu) g += x; g = gpu.Count > 0 ? g / gpu.Count : 0;
            int streams = PlayerStreams.RenderCount - streamStart;
            var line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:yyyy-MM-ddTHH:mm:ss} frames={1} avg_ms={2:F2} p50_ms={3:F2} p95_ms={4:F2} max_ms={5:F2} fps={6:F1} gpu_ms={7:F2} stream_frames_per_s={8:F1} screen={9}x{10}",
                System.DateTime.Now, ms.Count, avg, ms[ms.Count / 2], ms[(int)(ms.Count * 0.95f)], ms[ms.Count - 1], ms.Count / span, g, streams / span,
                Screen.width, Screen.height);
            try { System.IO.File.AppendAllText(path, line + "\n"); } catch { }
            ms.Clear(); gpu.Clear();
            windowStart = Time.unscaledTime; streamStart = PlayerStreams.RenderCount;
        }
    }
}
