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
        public float Interval = 0.2f;
        public int Width = 1280;
        TcpListener listener;
        Thread thread;
        volatile byte[] latest;

        // Per-player live streams: frames are only rendered for teams someone requested in the last 10 seconds.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> teamFrames = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.DateTime> teamWanted = new System.Collections.Concurrent.ConcurrentDictionary<int, System.DateTime>();
        public static bool Wanted(int team) => teamWanted.TryGetValue(team, out var t) && (System.DateTime.UtcNow - t).TotalSeconds < 10;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.DateTime> frameAt = new System.Collections.Concurrent.ConcurrentDictionary<int, System.DateTime>();
        public static void Submit(int team, byte[] jpg) { teamFrames[team] = jpg; frameAt[team] = System.DateTime.UtcNow; }
        public static void Forget(int team) { teamFrames.TryRemove(team, out _); }

        /// <summary>Camera input from a stream viewer: team -1 is the host's main view, 0-7 a player's own stream.</summary>
        public struct CamOp { public int Team; public float Dx, Dy, Zoom, Yaw, X, Y; } // X/Y: absolute focus (NaN = keep)
        public static readonly System.Collections.Concurrent.ConcurrentQueue<CamOp> CamOps = new System.Collections.Concurrent.ConcurrentQueue<CamOp>();

        static float Q(string query, string key, float def)
        {
            var m = System.Text.RegularExpressions.Regex.Match(query, "[?&]" + key + "=(-?[0-9.]+)");
            return m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
        }
        volatile bool running;
        RenderTexture full, small;
        Texture2D readback;

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
async function loop(){try{const r=await fetch('frame.jpg?'+Date.now(),{cache:'no-store'});if(r.ok){const u=URL.createObjectURL(await r.blob());const old=img.src;img.src=u;if(old.startsWith('blob:'))URL.revokeObjectURL(old);}}catch(e){}setTimeout(loop,150);}
loop();</script></body></html>";

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
            while (running)
            {
                yield return new WaitForSecondsRealtime(Interval);
                yield return wait;
                int sw = Screen.width, sh = Screen.height;
                if (sw <= 0 || sh <= 0) continue;
                int w = Mathf.Min(Width, sw), h = Mathf.RoundToInt(w * sh / (float)sw);
                if (full == null || full.width != sw || full.height != sh) { if (full != null) full.Release(); full = new RenderTexture(sw, sh, 0); }
                if (small == null || small.width != w || small.height != h)
                {
                    if (small != null) small.Release();
                    small = new RenderTexture(w, h, 0);
                    readback = new Texture2D(w, h, TextureFormat.RGB24, false);
                }
                ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
                // The captured texture is upside down on top-left-origin APIs (Metal).
                if (SystemInfo.graphicsUVStartsAtTop) Graphics.Blit(full, small, new Vector2(1, -1), new Vector2(0, 1));
                else Graphics.Blit(full, small);
                var prev = RenderTexture.active;
                RenderTexture.active = small;
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readback.Apply(false);
                RenderTexture.active = prev;
                latest = readback.EncodeToJPG(72);
            }
        }

        void Serve()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); } catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
        }

        void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
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
                        body = latest;
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
