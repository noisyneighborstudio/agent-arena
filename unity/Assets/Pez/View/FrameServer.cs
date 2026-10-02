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
        volatile bool running;
        RenderTexture full, small;
        Texture2D readback;

        const string Page = @"<!doctype html><html><head><meta name=viewport content='width=device-width,initial-scale=1'><title>Pezz</title>
<style>html,body{margin:0;background:#0b0c0e;height:100%;display:flex;align-items:center;justify-content:center}img{max-width:100vw;max-height:100vh}</style></head>
<body><img id=f><script>
const img=document.getElementById('f');
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
                    if (path.Contains("frame.jpg"))
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
