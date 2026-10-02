using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Spectator stream: serves the rendered screen (HUD included) as JPEG frames plus a tiny
    /// watch page, so a battle can be watched from another machine. Read-only by design: it is a
    /// separate port from the control API so exposing it can't hand out control of a team.
    /// A raw TCP server (not HttpListener) so it answers whatever Host a proxy like tailscale serve sends.
    /// </summary>
    public class FrameServer : MonoBehaviour
    {
        public int Port = 7778;
        public float IdleInterval = 0.2f; // the host's view when nobody is streaming it
        public int Width = 1280, MaxHeight = 720;
        // Live streams go out through the host's uplink (and Cloudflare) once per viewer: 30 fps of 1280x720 JPEG at
        // quality 70 keeps a viewer around 0.5-0.8 MB/s. Stills (frame.jpg, team/N.jpg) use the same frames.
        public const int StreamFps = 30, Quality = 70, MainView = -1;
        TcpListener listener;
        Thread thread;

        // Live streams, keyed by team (MainView is the host's screen): frames are only rendered for streams someone
        // requested in the last 10 seconds. Each new frame bumps the stream's sequence number and wakes its watchers.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> teamFrames = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.DateTime> teamWanted = new System.Collections.Concurrent.ConcurrentDictionary<int, System.DateTime>();
        public static bool Wanted(int team) => teamWanted.TryGetValue(team, out var t) && (System.DateTime.UtcNow - t).TotalSeconds < 10;
        // Streams with a live viewer render at StreamFps; ones only polled for snapshots render slower.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> watchers = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        public static bool Streaming(int team) => watchers.TryGetValue(team, out var n) && n > 0;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.DateTime> frameAt = new System.Collections.Concurrent.ConcurrentDictionary<int, System.DateTime>();
        static readonly object frameSignal = new object();
        static readonly System.Collections.Generic.Dictionary<int, (int seq, long stamp)> frameSeq = new System.Collections.Generic.Dictionary<int, (int, long)>();
        static int Seq(int team) => frameSeq.TryGetValue(team, out var f) ? f.seq : 0;

        /// <summary>Publish a stream's frame. Encodes finish out of order, so one older than the last published is dropped.</summary>
        public static void Submit(int team, byte[] jpg, long stamp)
        {
            lock (frameSignal)
            {
                frameSeq.TryGetValue(team, out var f);
                if (stamp <= f.stamp) return;
                teamFrames[team] = jpg; frameAt[team] = System.DateTime.UtcNow;
                frameSeq[team] = (f.seq + 1, stamp);
                Monitor.PulseAll(frameSignal);
            }
        }
        public static void Forget(int team) { teamFrames.TryRemove(team, out _); }

        // Frames are read back from the GPU asynchronously and JPEG-encoded on worker threads, so a 60fps stream
        // doesn't stall the game. A stream with too many frames in flight skips rendering until they land.
        static readonly int[] inFlight = new int[9]; // MainView and teams 0-7
        static readonly System.Collections.Concurrent.ConcurrentBag<byte[]> pixelPool = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
        public static bool Busy(int team) => Volatile.Read(ref inFlight[team + 1]) >= 3;

        /// <summary>Encode src (an R8G8B8A8_SRGB, non-MSAA texture) as the stream's next frame.</summary>
        public static void Encode(RenderTexture src, int team)
        {
            int w = src.width, h = src.height;
            long stamp = Time.frameCount;
            Interlocked.Increment(ref inFlight[team + 1]);
            UnityEngine.Rendering.AsyncGPUReadback.Request(src, 0, req =>
            {
                if (req.hasError) { Interlocked.Decrement(ref inFlight[team + 1]); return; }
                var data = req.GetData<byte>();
                if (!pixelPool.TryTake(out var px) || px.Length != data.Length) px = new byte[data.Length];
                data.CopyTo(px);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        // The bytes are already sRGB-encoded: encode them as-is.
                        var jpg = ImageConversion.EncodeArrayToJPG(px, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, Quality);
                        Submit(team, jpg, stamp);
                    }
                    catch (System.Exception ex) { Debug.LogWarning("Frame encode failed: " + ex.Message); }
                    finally { pixelPool.Add(px); Interlocked.Decrement(ref inFlight[team + 1]); }
                });
            });
        }

        /// <summary>Camera input from a stream viewer: team -1 is the host's main view, 0-7 a player's own stream.</summary>
        public struct CamOp { public int Team; public float Dx, Dy, Zoom, Yaw, X, Y; public int Follow; } // X/Y: absolute focus (NaN = keep); Follow: entity id to ride along with (0 = stop, -1 = no change)

        /// <summary>"What's under this point of team N's stream?" Answered on the main thread, where the stream camera lives.</summary>
        public class PickReq { public int Team; public float U, V; public string Result; public readonly System.Threading.ManualResetEventSlim Done = new System.Threading.ManualResetEventSlim(false); }
        public static readonly System.Collections.Concurrent.ConcurrentQueue<PickReq> PickOps = new System.Collections.Concurrent.ConcurrentQueue<PickReq>();
        public static readonly System.Collections.Concurrent.ConcurrentQueue<CamOp> CamOps = new System.Collections.Concurrent.ConcurrentQueue<CamOp>();

        static float Q(string query, string key, float def)
        {
            var m = System.Text.RegularExpressions.Regex.Match(query, "[?&]" + key + "=(-?[0-9.]+)");
            return m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
        }
        volatile bool running;
        RenderTexture full, small;

        const string Page = @"<!doctype html><html><head><meta name=viewport content='width=device-width,initial-scale=1'><title>Pezz</title>
<style>html,body{margin:0;background:#0b0c0e;height:100%;display:flex;align-items:center;justify-content:center;overflow:hidden}
img{max-width:100vw;max-height:100vh;cursor:grab;user-select:none;-webkit-user-drag:none}img:active{cursor:grabbing}
#h{position:fixed;bottom:10px;right:12px;color:#9c9488;font:12px -apple-system,sans-serif;background:rgba(20,17,19,.8);padding:6px 10px;border-radius:8px}</style></head>
<body><img id=f draggable=false><div id=h>drag to pan · scroll to zoom · WASD/arrows pan · Q/E rotate</div><script>
const img=document.getElementById('f');
// Look around: input goes back to the game camera.
let drag=null,acc={dx:0,dy:0,zoom:1,yaw:0},sending=false;
function flush(){if(sending)return;const a=acc;if(!a.dx&&!a.dy&&a.zoom===1&&!a.yaw)return;acc={dx:0,dy:0,zoom:1,yaw:0};sending=true;
fetch(`cam?dx=${a.dx.toFixed(4)}&dy=${a.dy.toFixed(4)}&zoom=${a.zoom.toFixed(3)}&yaw=${a.yaw.toFixed(1)}`).finally(()=>{sending=false;setTimeout(flush,30)});}
img.addEventListener('mousedown',e=>{drag={x:e.clientX,y:e.clientY};e.preventDefault()});
addEventListener('mouseup',()=>drag=null);
addEventListener('mousemove',e=>{if(!drag)return;const r=img.getBoundingClientRect();acc.dx-=(e.clientX-drag.x)/r.width;acc.dy+=(e.clientY-drag.y)/r.height;drag={x:e.clientX,y:e.clientY};flush()});
addEventListener('wheel',e=>{e.preventDefault();acc.zoom*=e.deltaY<0?0.9:1.11;flush()},{passive:false});
addEventListener('keydown',e=>{const k=e.key.toLowerCase();const s=.08;if(k==='a'||k==='arrowleft')acc.dx-=s;if(k==='d'||k==='arrowright')acc.dx+=s;if(k==='w'||k==='arrowup')acc.dy+=s;if(k==='s'||k==='arrowdown')acc.dy-=s;if(k==='q')acc.yaw+=15;if(k==='e')acc.yaw-=15;flush()});
// The stream is MJPEG: the browser shows each frame as it arrives. Reconnect if it drops (e.g. the game restarted).
img.onerror=()=>setTimeout(()=>img.src='stream?'+Date.now(),1000);img.src='stream';</script></body></html>";

        void Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                running = true;
                thread = new Thread(Serve) { IsBackground = true, Name = "PezFrames" };
                thread.Start();
                StartCoroutine(Capture());
                Debug.Log($"Pezz spectator stream on http://127.0.0.1:{Port}/");
            }
            catch (System.Exception ex) { Debug.LogWarning("Frame server failed: " + ex.Message); }
        }

        IEnumerator Capture()
        {
            var wait = new WaitForEndOfFrame();
            float last = -999f;
            while (running)
            {
                yield return wait;
                float gap = Streaming(MainView) ? 0.9f / StreamFps : IdleInterval;
                if (Time.unscaledTime - last < gap || Busy(MainView)) continue;
                int sw = Screen.width, sh = Screen.height;
                if (sw <= 0 || sh <= 0) continue;
                last = Time.unscaledTime;
                // Cap the host view at Width x MaxHeight (1280x720 for a 16:9 window) whatever the window's shape.
                int w = Mathf.Min(Width, sw, Mathf.RoundToInt(MaxHeight * sw / (float)sh)), h = Mathf.RoundToInt(w * sh / (float)sw);
                if (full == null || full.width != sw || full.height != sh) { if (full != null) full.Release(); full = new RenderTexture(sw, sh, 0); }
                if (small == null || small.width != w || small.height != h)
                {
                    if (small != null) small.Release();
                    small = new RenderTexture(w, h, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB);
                }
                ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
                // The captured texture is upside down on top-left-origin APIs (Metal).
                if (SystemInfo.graphicsUVStartsAtTop) Graphics.Blit(full, small, new Vector2(1, -1), new Vector2(0, 1));
                else Graphics.Blit(full, small);
                Encode(small, MainView);
            }
        }

        void Serve()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); } catch { break; }
                // A thread per connection: streams hold theirs for as long as someone watches.
                new Thread(() => Handle(client)) { IsBackground = true, Name = "PezFrameClient" }.Start();
            }
        }

        /// <summary>Push every new frame of a stream as multipart MJPEG until the viewer goes away.</summary>
        void PushFrames(NetworkStream stream, int team)
        {
            watchers.AddOrUpdate(team, 1, (_, n) => n + 1);
            try { PushFramesUntilGone(stream, team); }
            finally { watchers.AddOrUpdate(team, 0, (_, n) => n - 1); }
        }

        void PushFramesUntilGone(NetworkStream stream, int team)
        {
            stream.WriteTimeout = 5000;
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=pezframe\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            int sent = -1;
            var lastFrame = System.DateTime.UtcNow;
            // Never push faster than StreamFps, whatever rate frames arrive at: frames that land inside the gap are
            // skipped and the newest goes out when it ends.
            var pace = System.Diagnostics.Stopwatch.StartNew();
            long minGapMs = 1000 / StreamFps;
            while (running)
            {
                teamWanted[team] = System.DateTime.UtcNow;
                long wait = minGapMs - pace.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                byte[] jpg;
                lock (frameSignal)
                {
                    if (Seq(team) == sent)
                    {
                        Monitor.Wait(frameSignal, 500);
                        // Nothing for a while (the seat emptied, or the game is in its menu): end it; the viewer reconnects.
                        if (Seq(team) == sent && (System.DateTime.UtcNow - lastFrame).TotalSeconds > 10) return;
                        continue;
                    }
                    sent = Seq(team);
                    teamFrames.TryGetValue(team, out jpg);
                }
                if (jpg == null) continue;
                pace.Restart();
                lastFrame = System.DateTime.UtcNow;
                var part = Encoding.ASCII.GetBytes($"--pezframe\r\nContent-Type: image/jpeg\r\nContent-Length: {jpg.Length}\r\n\r\n");
                stream.Write(part, 0, part.Length);
                stream.Write(jpg, 0, jpg.Length);
                stream.Write(CrLf, 0, 2);
            }
        }
        static readonly byte[] CrLf = { 13, 10 };

        void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.NoDelay = true;
                    stream.ReadTimeout = 3000;
                    // Read just the request line; headers are irrelevant here.
                    var line = new StringBuilder();
                    int b;
                    while ((b = stream.ReadByte()) >= 0 && b != '\n') line.Append((char)b);
                    var parts = line.ToString().Split(' ');
                    var path = parts.Length > 1 ? parts[1] : "/";
                    byte[] body; string status = "200 OK", type;
                    var cm = System.Text.RegularExpressions.Regex.Match(path, @"^/(?:team/(\d+)/)?cam(\?.*)?$");
                    var tm = System.Text.RegularExpressions.Regex.Match(path, @"^/team/(\d+)\.jpg");
                    var sm = System.Text.RegularExpressions.Regex.Match(path, @"^/(?:team/(\d+)/)?stream(\?.*)?$");
                    var pm = System.Text.RegularExpressions.Regex.Match(path, @"^/team/(\d+)/pick(\?.*)?$");
                    if (pm.Success)
                    {
                        // u, v: 0..1 across and down the stream image.
                        var req = new PickReq { Team = int.Parse(pm.Groups[1].Value), U = Mathf.Clamp01(Q(pm.Groups[2].Value, "u", 0.5f)), V = Mathf.Clamp01(Q(pm.Groups[2].Value, "v", 0.5f)) };
                        PickOps.Enqueue(req);
                        req.Done.Wait(800);
                        var pb = Encoding.UTF8.GetBytes(req.Result ?? "{\"ok\":false,\"error\":\"no answer\"}");
                        var ph = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {pb.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                        stream.Write(ph, 0, ph.Length); stream.Write(pb, 0, pb.Length);
                        return;
                    }
                    if (sm.Success) { PushFrames(stream, sm.Groups[1].Success ? int.Parse(sm.Groups[1].Value) : MainView); return; }
                    if (cm.Success)
                    {
                        // dx/dy: pan as a fraction of the view; zoom: multiplier; yaw: degrees.
                        var q = cm.Groups[2].Value;
                        CamOps.Enqueue(new CamOp
                        {
                            Team = cm.Groups[1].Success ? int.Parse(cm.Groups[1].Value) : -1,
                            Dx = Mathf.Clamp(Q(q, "dx", 0), -1, 1), Dy = Mathf.Clamp(Q(q, "dy", 0), -1, 1),
                            Zoom = Mathf.Clamp(Q(q, "zoom", 1), 0.5f, 2f), Yaw = Mathf.Clamp(Q(q, "yaw", 0), -90, 90),
                            X = Q(q, "x", float.NaN), Y = Q(q, "y", float.NaN),
                            Follow = (int)Q(q, "follow", -1),
                        });
                        body = Encoding.ASCII.GetBytes("{\"ok\":true}"); type = "application/json";
                    }
                    else if (tm.Success)
                    {
                        int team = int.Parse(tm.Groups[1].Value);
                        teamWanted[team] = System.DateTime.UtcNow;
                        teamFrames.TryGetValue(team, out body);
                        // A stream nobody watched for a while has a stale last frame: don't serve it.
                        if (!frameAt.TryGetValue(team, out var at) || (System.DateTime.UtcNow - at).TotalSeconds > 2) body = null;
                        type = "image/jpeg";
                        if (body == null) { body = new byte[0]; status = "503 Service Unavailable"; }
                    }
                    else if (path.Contains("frame.jpg"))
                    {
                        teamFrames.TryGetValue(MainView, out body);
                        type = "image/jpeg";
                        if (body == null) { body = new byte[0]; status = "503 Service Unavailable"; }
                    }
                    else { body = Encoding.UTF8.GetBytes(Page); type = "text/html; charset=utf-8"; }
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    stream.Write(head, 0, head.Length);
                    stream.Write(body, 0, body.Length);
                }
            }
            catch { }
        }

        void OnDestroy()
        {
            running = false;
            try { listener?.Stop(); } catch { }
        }
    }
}
