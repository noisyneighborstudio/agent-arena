using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// RTS camera: WASD/arrows/edge-pan, middle-drag pan (left drag too in the browser), scroll zoom, Q/E rotate.
    /// Browser extras: touch (drag pans, pinch zooms, twist rotates, tap picks) and on-screen buttons, F flies to your
    /// base, M fits the map, and zoom eases.
    /// Default is the art pack's off-axis orthographic view (yaw 45°, pitch 55°): constant unit scale and two
    /// readable faces per building. In orthographic mode Distance is the half-height of the view in tiles.
    /// </summary>
    public class RtsCamera : MonoBehaviour
    {
        public Vector3 Focus;
        public float Distance = 12f, Yaw = 45f, Pitch = 55f;
        // Art pack camera: ortho size 8 (close) to 40 (strategic).
        public float MinDist = 8f, MaxDist = 40f;
        /// <summary>
        /// The boards' framing (07_Camera "ortho height 17", 06_HUD, hero_offaxis): ortho size 9, about 60 px per tile
        /// at 1080p. A fixed world extent, so a Retina window frames the same as the boards rather than twice as wide.
        /// </summary>
        public const float BoardOrthoSize = 9f;
        public float DefaultDist => Mathf.Clamp(BoardOrthoSize, MinDist, MaxDist);

        // Art pack lighting: the warm sun sits at the upper left of the screen (a little beyond the focus), about
        // 57 degrees up, so shadows fall to the lower right. It is defined against the view, so it follows the yaw.
        const float SunPitch = 57f, SunYawFromView = 112.6f;

        /// <summary>
        /// How far back along its view the orthographic camera sits for an ortho half-height: just far enough that the
        /// tallest things (aircraft, rising smoke) stay past the near plane. Close, so the sun's shadow map (fitted from
        /// the camera out to ShadowReach) spends its texels on what's on screen.
        /// </summary>
        public static float BackDistance(float orthoSize) => orthoSize + 14f;
        /// <summary>Shadow distance for an ortho half-height: past the far edge of the visible ground, no further.</summary>
        public static float ShadowReach(float orthoSize) => BackDistance(orthoSize) + orthoSize * 0.8f + 6f;
        /// <summary>
        /// Far clip for an ortho half-height: just past the farthest ground. A tight near/far keeps the (linear, for
        /// orthographic) depth buffer precise, which the ambient occlusion and the water's shore line read.
        /// </summary>
        public static float FarClip(float orthoSize) => BackDistance(orthoSize) + orthoSize * 1.6f + 20f;

        static float shadowReachSet = -1f;
        /// <summary>Fit the sun's shadow distance to a view of this ortho half-height (only touches QualitySettings on a change).</summary>
        public static void FitShadows(float orthoSize)
        {
            float r = ShadowReach(orthoSize);
            if (Mathf.Abs(r - shadowReachSet) < 0.01f) return;
            QualitySettings.shadowDistance = shadowReachSet = r;
        }

        /// <summary>Point the scene's sun for a camera looking along `yaw`.</summary>
        public static void AimSun(float yaw)
        {
            var sun = RenderSettings.sun;
            if (sun != null) sun.transform.rotation = Quaternion.Euler(SunPitch, yaw + SunYawFromView, 0f);
        }
        public Vector2 Bounds = new Vector2(64, 64);
        /// <summary>Edge-pan is off in the browser: the page has no pointer lock, so a pointer resting at (or leaving through) the window edge would drift the camera for good.</summary>
        public bool EdgePan = !GameRunner.IsWeb;
        public Camera Cam { get; private set; }
        Vector3 dragAnchor;
        bool dragging;
        /// <summary>Is the mouse over the HUD (GameRunner sets it)? A browser left drag that starts there isn't a pan.</summary>
        public System.Func<Vector2, bool> OverUi;
        /// <summary>The last left press in the browser became a pan, so its release isn't a click.</summary>
        public bool Panned { get; private set; }
        Vector2 pressAt;
        bool pressed;

        void Awake()
        {
            Cam = GetComponent<Camera>();
            EdgePan = !GameRunner.IsWeb;
            camLog = GameRunner.IsWeb && WebClient.Query("camlog") == "1";
        }

        // ?camlog=1 in the page URL prints the camera pose to the browser console whenever it moves (for scripted checks).
        bool camLog;
        string lastLog;

        public void LookAt(Vector3 p) { Focus = new Vector3(p.x, 0, p.z); }

        /// <summary>While this returns a point, the camera rides along with it (a selected mobile unit) instead of panning.</summary>
        public System.Func<Vector3?> Follow;
        public bool Following { get; private set; }

        // ------------------------------------------------------------------ browser zoom, fly-to and touch state

        /// <summary>Farthest zoom-out. In the browser it grows with the map so `M` can fit all of a 144+ tile arena.</summary>
        public float ZoomMax => GameRunner.IsWeb ? Mathf.Max(MaxDist, FitDistance()) : MaxDist;

        /// <summary>
        /// Ortho half-height that shows the whole map at any yaw (the map's circumscribed circle: it fits the screen's
        /// height squashed by the pitch, and its width at the camera's aspect), with a little margin.
        /// </summary>
        public float FitDistance()
        {
            float r = Bounds.magnitude * 0.5f, aspect = Cam != null ? Cam.aspect : 16f / 9f;
            return Mathf.Max(r * Mathf.Sin(Pitch * Mathf.Deg2Rad), r / Mathf.Max(0.2f, aspect)) * 1.05f;
        }

        struct Pose { public Vector3 Focus; public float Distance, Yaw; }
        const float FlyTime = 0.35f, ZoomEase = 14f;
        // The zoom eases toward zoomTarget; a change to Distance that wasn't ours (API, view rebuild, kitchen sink) resets it.
        float zoomTarget, distSet = -1f;
        readonly List<float> scrollTimes = new List<float>();
        Vector2 zoomAnchor; // the screen point (-1..1) the ground stays under while zooming
        bool flying, fitActive;
        float flyT;
        Pose flyFrom, flyTo, beforeFit;
        int homeIdx;
        GameRunner runner;
        GameRunner Runner => runner != null ? runner : runner = FindFirstObjectByType<GameRunner>();

        // Touch: one finger grabs the ground, two pinch/twist; a finger that didn't move or join another is a tap.
        readonly HashSet<int> owned = new HashSet<int>();
        bool touchSeen;
        int liveCount, lastTouchFrame = -100, tapFrame = -100;
        bool touchMoved, multi, twisting;
        Vector2 touchStart, tapAt, prevMid;
        float prevDist, prevAng, twistAcc;
        // Camera buttons (+, -, rotate left, rotate right): held by a finger (padFinger) or the mouse (padFinger = -2).
        int padFinger = -1, padBtn = -1;
        float padHeld;
        /// <summary>
        /// How far (HUD pixels, at the HUD's scale) the pad sits left of the right column and up from the bottom edge. The
        /// default clears the selection card, which fills the bottom edge up to the column; the HUD can move it.
        /// </summary>
        public Vector2 PadInset = new Vector2(0f, 232f);

        /// <summary>A touch is (or just was) on screen: Unity's simulated mouse events from it aren't clicks.</summary>
        public bool TouchRecent => Time.frameCount - lastTouchFrame <= 2;

        /// <summary>A one-finger tap that didn't pan, once (consumed by the caller).</summary>
        public bool TakeTap(out Vector2 pos)
        {
            pos = tapAt;
            if (Time.frameCount - tapFrame > 2) return false;
            tapFrame = -100;
            return true;
        }

        Vector2 Ndc(Vector2 px) => new Vector2(px.x / Screen.width * 2f - 1f, px.y / Screen.height * 2f - 1f);

        /// <summary>Ground offset from the focus of the point under screen position `ndc`, for an orthographic view.</summary>
        Vector3 Off(Vector2 ndc, float yaw, float dist)
        {
            var rot = Quaternion.Euler(0, yaw, 0);
            return (rot * Vector3.right * ndc.x * Cam.aspect + rot * Vector3.forward * ndc.y / Mathf.Sin(Pitch * Mathf.Deg2Rad)) * dist;
        }

        void ZoomBy(float factor, Vector2 ndc)
        {
            zoomTarget = Mathf.Clamp(zoomTarget * factor, MinDist, ZoomMax);
            zoomAnchor = ndc;
            Manual();
        }

        /// <summary>Hands-on input ends a fly-to and forgets the "fitted" pose `M` would return from.</summary>
        void Manual() { flying = false; fitActive = false; }

        void FlyTo(Pose to)
        {
            flyFrom = new Pose { Focus = Focus, Distance = zoomTarget, Yaw = Yaw };
            to.Focus = new Vector3(to.Focus.x, 0, to.Focus.z);
            to.Distance = Mathf.Clamp(to.Distance, MinDist, ZoomMax);
            flyTo = to; flyT = 0f; flying = true;
        }

        /// <summary>`F`: your base (command center, else start position); a spectator's presses cycle through the teams' bases.</summary>
        void FlyHome()
        {
            var r = Runner;
            if (r == null || r.Game == null) return;
            var w = r.Game.World;
            Vec2 at;
            if (r.HumanTeam >= 0) at = GameRunner.OpeningFocus(w, r.HumanTeam);
            else
            {
                var seats = new List<int>();
                foreach (var t in w.Teams) if (!t.Left && !t.Defeated) seats.Add(t.Id);
                if (seats.Count == 0) at = new Vec2(w.Map.W / 2f, w.Map.H / 2f);
                else
                {
                    at = default;
                    for (int i = 0; i < seats.Count; i++) // the next base, skipping one we're already looking at
                    {
                        at = GameRunner.OpeningFocus(w, seats[homeIdx++ % seats.Count]);
                        var p = WorldView.W(at);
                        if (seats.Count == 1 || (new Vector3(p.x, 0, p.z) - Focus).magnitude > 1.5f) break;
                    }
                }
            }
            r.View?.Selected.Clear(); // a followed unit would pull the camera straight back
            fitActive = false;
            float d = zoomTarget > DefaultDist * 2f ? DefaultDist : zoomTarget;
            FlyTo(new Pose { Focus = WorldView.W(at), Distance = d, Yaw = Yaw });
        }

        /// <summary>`M`: zoom out over the whole map; pressed again, back to where you were.</summary>
        void ToggleFitMap()
        {
            Runner?.View?.Selected.Clear();
            if (fitActive) { fitActive = false; FlyTo(beforeFit); return; }
            beforeFit = new Pose { Focus = Focus, Distance = zoomTarget, Yaw = Yaw };
            FlyTo(new Pose { Focus = new Vector3(Bounds.x / 2f, 0, Bounds.y / 2f), Distance = FitDistance(), Yaw = Yaw });
            fitActive = true;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            bool web = GameRunner.IsWeb;
            bool keys = !Hud.Typing; // don't pan while someone is typing orders
            if (distSet < 0f || !Mathf.Approximately(Distance, distSet)) { zoomTarget = Distance; flying = false; }
            if (web && keys && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.LeftCommand) && Runner != null && !Runner.InMenu)
            {
                // PlayerInput leaves F and M alone in the browser (nobody commands there).
                if (Input.GetKeyDown(KeyCode.F)) FlyHome();
                if (Input.GetKeyDown(KeyCode.M)) ToggleFitMap();
            }
            var fwd = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            var right = Quaternion.Euler(0, Yaw, 0) * Vector3.right;
            float speed = Distance * 1.1f;
            var move = Vector3.zero;
            if (keys && (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))) move += fwd;
            if (keys && (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))) move -= fwd;
            if (keys && (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))) move += right;
            if (keys && (Input.GetKey(KeyCode.A) && !Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftArrow))) move -= right;
            if (EdgePan && Application.isFocused)
            {
                var m = Input.mousePosition;
                const int edge = 6;
                if (m.x >= 0 && m.y >= 0 && m.x <= Screen.width && m.y <= Screen.height)
                {
                    if (m.x < edge) move -= right;
                    if (m.x > Screen.width - edge) move += right;
                    if (m.y < edge) move -= fwd;
                    if (m.y > Screen.height - edge) move += fwd;
                }
            }
            if (move != Vector3.zero) Manual();
            var follow = Follow?.Invoke();
            Following = follow.HasValue;
            if (Following) Focus = Vector3.Lerp(Focus, new Vector3(follow.Value.x, 0, follow.Value.z), 1f - Mathf.Exp(-6f * dt));
            else Focus += move.normalized * speed * dt;

            if (keys && Input.GetKey(KeyCode.Q)) { Yaw += 70f * dt; Manual(); }
            if (keys && Input.GetKey(KeyCode.E)) { Yaw -= 70f * dt; Manual(); }

            bool touching = Input.touchCount > 0;
            if (touching) { lastTouchFrame = Time.frameCount; touchSeen = true; }
            float scroll = Input.mouseScrollDelta.y;
            if (camLog && scroll != 0f) Debug.Log($"SCROLL {scroll:F3}");
            if (web)
            {
                // A wheel notch and a trackpad's stream of tiny deltas should zoom alike. The browser hands Unity those
                // deltas already flattened (about +-1 per event), so the size can't tell them apart; the rate does: a lone
                // event is a notch (a full step), and the denser the events the smaller each one counts, so a 60 Hz
                // trackpad swipe zooms at a steady rate (about 3x a second) however many events it sends.
                if (Mathf.Abs(scroll) > 0.001f)
                {
                    float at = Time.unscaledTime;
                    scrollTimes.Add(at);
                    scrollTimes.RemoveAll(t => at - t > 0.1f);
                    float step = 0.22f / (1f + 2.4f * (scrollTimes.Count - 1));
                    ZoomBy(Mathf.Exp(-Mathf.Clamp(scroll, -1f, 1f) * step), Ndc(Input.mousePosition));
                }
                TouchUpdate(dt);
                PadUpdate(dt);
                if (flying)
                {
                    flyT += dt / FlyTime;
                    float e = Mathf.SmoothStep(0f, 1f, Mathf.Min(1f, flyT));
                    Focus = Vector3.Lerp(flyFrom.Focus, flyTo.Focus, e);
                    Distance = zoomTarget = flyFrom.Distance * Mathf.Pow(flyTo.Distance / flyFrom.Distance, e);
                    Yaw = flyFrom.Yaw + Mathf.DeltaAngle(flyFrom.Yaw, flyTo.Yaw) * e;
                    if (flyT >= 1f) flying = false;
                }
                else
                {
                    zoomTarget = Mathf.Clamp(zoomTarget, MinDist, ZoomMax);
                    float nd = Mathf.Abs(zoomTarget - Distance) < 0.002f * Distance ? zoomTarget : Distance * Mathf.Pow(zoomTarget / Distance, 1f - Mathf.Exp(-ZoomEase * dt));
                    // Zoom about the pointer / pinch midpoint: the ground under it stays under it.
                    if (Cam.orthographic && !Following) Focus += Off(zoomAnchor, Yaw, Distance) - Off(zoomAnchor, Yaw, nd);
                    Distance = nd;
                }
            }
            else if (Mathf.Abs(scroll) > 0.01f) Distance = Mathf.Clamp(Distance * (1f - scroll * 0.1f), MinDist, MaxDist);

            // Middle-drag pans by grabbing the ground point.
            if (Input.GetMouseButtonDown(2)) { dragging = GroundPoint(Input.mousePosition, out dragAnchor); if (dragging) Manual(); }
            if (Input.GetMouseButtonUp(2)) dragging = false;
            // The browser only watches, and trackpads have no middle button: a left drag off the HUD pans there too.
            // It starts past a few pixels, so a click still picks a unit. (A touch is handled above; Unity's simulated
            // mouse from it is skipped here so it can't pan twice.)
            if (web && !touching)
            {
                var mp = (Vector2)Input.mousePosition;
                if (Input.GetMouseButtonDown(0)) { pressed = !OverPad(mp) && (OverUi == null || !OverUi(mp)); pressAt = mp; Panned = false; }
                if (pressed && !Panned && (mp - pressAt).magnitude > 6f) { Panned = dragging = GroundPoint(pressAt, out dragAnchor); if (dragging) Manual(); }
                if (Input.GetMouseButtonUp(0)) { if (Panned) dragging = false; pressed = false; }
            }
            if (dragging && !Following && GroundPoint(Input.mousePosition, out var now)) Focus += dragAnchor - now;

            Distance = Mathf.Clamp(Distance, MinDist, ZoomMax); // the API and stream viewers can set it directly
            // Anywhere on the map, corners included: the view may hang off the edge (the off-axis map is a diamond on screen,
            // so keeping the whole view over the map made its corners unreachable).
            if (Cam.orthographic) { Focus.x = Mathf.Clamp(Focus.x, 0, Bounds.x); Focus.z = Mathf.Clamp(Focus.z, 0, Bounds.y); }
            else
            {
                Focus.x = Mathf.Clamp(Focus.x, 0, Bounds.x);
                Focus.z = Mathf.Clamp(Focus.z, -4, Bounds.y);
            }
            AimSun(Yaw);
            if (Cam.orthographic)
            {
                var rot = Quaternion.Euler(Pitch, Yaw, 0);
                Cam.orthographicSize = Distance;
                transform.position = Focus - rot * Vector3.forward * BackDistance(Distance);
                transform.rotation = rot;
                FitShadows(Distance);
                Cam.farClipPlane = FarClip(Distance);
            }
            else
            {
                // Perspective: look a bit more top-down when zoomed out.
                float pitch = Mathf.Lerp(Pitch, 68f, Mathf.InverseLerp(MinDist, MaxDist, Distance));
                var rot = Quaternion.Euler(pitch, Yaw, 0);
                transform.position = Focus - rot * Vector3.forward * Distance;
                transform.rotation = rot;
            }
            distSet = Distance;
            if (!web) zoomTarget = Distance;
            if (camLog)
            {
                string line = $"CAM x={Focus.x:F2} z={Focus.z:F2} d={Distance:F2} yaw={Yaw:F1} max={ZoomMax:F1} fly={(flying ? 1 : 0)}";
                if (line != lastLog && Time.frameCount % 4 == 0) { Debug.Log(lastLog = line); }
            }
        }

        // ------------------------------------------------------------------ touch

        void TouchUpdate(float dt)
        {
            int n = Input.touchCount;
            if (n == 0)
            {
                // No fingers down: drop any the browser took away without an Ended (a lost focus, an alert).
                owned.Clear();
                if (padFinger >= 0) { padFinger = -1; padBtn = -1; }
                liveCount = 0;
                return;
            }
            var live = new List<Touch>(2);
            for (int i = 0; i < n; i++)
            {
                var t = Input.GetTouch(i);
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                if (t.phase == TouchPhase.Began)
                {
                    int b = PadButtonAt(t.position);
                    if (b >= 0 && padFinger == -1) { padFinger = t.fingerId; PadPress(b); }
                    else if (b < 0 && (OverUi == null || !OverUi(t.position))) owned.Add(t.fingerId);
                }
                else if (ended)
                {
                    if (t.fingerId == padFinger) { padFinger = -1; padBtn = -1; }
                    else if (owned.Remove(t.fingerId) && owned.Count == 0 && !touchMoved && !multi && t.phase == TouchPhase.Ended)
                    {
                        tapAt = t.position; tapFrame = Time.frameCount;
                    }
                }
                if (!ended && owned.Contains(t.fingerId)) live.Add(t);
            }
            live.Sort((a, b) => a.fingerId.CompareTo(b.fingerId));
            if (live.Count > 2) live.RemoveRange(2, live.Count - 2);
            int cnt = live.Count;
            bool rebase = cnt != liveCount;
            if (rebase && liveCount == 0 && cnt > 0 && owned.Count == cnt) { touchMoved = false; multi = false; Panned = false; }
            liveCount = cnt;
            if (cnt == 1)
            {
                var t = live[0];
                if (rebase)
                {
                    touchStart = t.position;
                    if (touchMoved) GroundPoint(t.position, out dragAnchor); // one finger left a pinch: carry on panning from here
                }
                if (!touchMoved && (t.position - touchStart).magnitude > 10f) { touchMoved = true; Panned = true; Manual(); GroundPoint(t.position, out dragAnchor); }
                else if (touchMoved && !Following && GroundPoint(t.position, out var now)) Focus += dragAnchor - now;
            }
            else if (cnt == 2)
            {
                multi = touchMoved = Panned = true;
                Manual();
                Vector2 p0 = live[0].position, p1 = live[1].position, mid = (p0 + p1) * 0.5f;
                float dist = (p1 - p0).magnitude, ang = Mathf.Atan2(p1.y - p0.y, p1.x - p0.x) * Mathf.Rad2Deg;
                if (rebase) { twistAcc = 0f; twisting = false; }
                else if (Cam.orthographic)
                {
                    // The midpoint drags the ground along; spread zooms about it; a twist turns the view about it.
                    var rot = Quaternion.Euler(0, Yaw, 0);
                    var d = mid - prevMid;
                    if (!Following) Focus -= (rot * Vector3.right * d.x + rot * Vector3.forward * d.y / Mathf.Sin(Pitch * Mathf.Deg2Rad)) * 2f * Distance / Screen.height;
                    var ndc = Ndc(mid);
                    if (dist > 24f && prevDist > 24f) ZoomBy(prevDist / dist, ndc);
                    float da = Mathf.DeltaAngle(prevAng, ang);
                    twistAcc += da;
                    if (!twisting && Mathf.Abs(twistAcc) > 6f) { twisting = true; da = twistAcc; } // a pinch's wobble isn't a twist
                    if (twisting)
                    {
                        float before = Yaw;
                        Yaw += da; // fingers turning counter-clockwise carry the world with them
                        if (!Following) Focus += Off(ndc, before, Distance) - Off(ndc, Yaw, Distance);
                    }
                }
                prevMid = mid; prevDist = dist; prevAng = ang;
            }
        }

        // ------------------------------------------------------------------ camera buttons

        /// <summary>Phones and narrow windows, and any device that has shown us a finger (Input.touchSupported is true in every desktop browser, so it says nothing).</summary>
        bool PadVisible => GameRunner.IsWeb && (Application.isMobilePlatform || touchSeen || Screen.width < 800) && Runner != null && !Runner.InMenu;

        /// <summary>The HUD's scale (Hud.Setup's formula), so the pad keeps clear of its right column.</summary>
        static float HudScale => Mathf.Max(0.5f, Mathf.Min(Screen.height / 1080f, Screen.width / 1600f));

        /// <summary>The four buttons in GUI coordinates (top-left origin): a 2x2 block just left of the HUD's right column, at its bottom.</summary>
        Rect[] PadRects()
        {
            float k = HudScale, s = Mathf.Clamp(Mathf.Min(Screen.width, Screen.height) * 0.11f, 44f, 84f), gap = 6f; // a finger-sized button (about 44 CSS px) whatever the pixel ratio
            float right = Screen.width - (384f + 16f + 12f) * k - PadInset.x * k, bottom = Screen.height - 16f * k - PadInset.y * k;
            var r = new Rect[4];
            for (int i = 0; i < 4; i++)
                r[i] = new Rect(right - (2 - i % 2) * s - (1 - i % 2) * gap, bottom - (2 - i / 2) * s - (1 - i / 2) * gap, s, s);
            return r;
        }

        int PadButtonAt(Vector2 screen)
        {
            if (!PadVisible) return -1;
            var g = new Vector2(screen.x, Screen.height - screen.y);
            var rs = PadRects();
            for (int i = 0; i < rs.Length; i++) if (rs[i].Contains(g)) return i;
            return -1;
        }

        /// <summary>Is this screen point on a camera button? (A press there is the button's, not a pan or a pick.)</summary>
        public bool OverPad(Vector2 screen) => PadButtonAt(screen) >= 0;

        /// <summary>A press: one step at once (zoom in/out, a 15 degree turn); held past a beat it keeps going.</summary>
        void PadPress(int b)
        {
            padBtn = b; padHeld = 0f;
            if (b == 0) ZoomBy(0.7f, Vector2.zero);
            else if (b == 1) ZoomBy(1f / 0.7f, Vector2.zero);
            else { Yaw += b == 2 ? 15f : -15f; Manual(); }
        }

        void PadUpdate(float dt)
        {
            if (Input.touchCount == 0)
            {
                var mp = (Vector2)Input.mousePosition;
                if (Input.GetMouseButtonDown(0) && padFinger == -1) { int b = PadButtonAt(mp); if (b >= 0) { padFinger = -2; PadPress(b); } }
                if (Input.GetMouseButtonUp(0) && padFinger == -2) { padFinger = -1; padBtn = -1; }
            }
            if (padBtn < 0) return;
            padHeld += dt;
            if (padHeld < 0.35f) return;
            if (padBtn == 0) ZoomBy(Mathf.Exp(-1.6f * dt), Vector2.zero);
            else if (padBtn == 1) ZoomBy(Mathf.Exp(1.6f * dt), Vector2.zero);
            else { Yaw += (padBtn == 2 ? 80f : -80f) * dt; Manual(); }
        }

        static Texture2D padTex, padTexHeld;

        /// <summary>A 64px rounded square (radius 10, 1px border #2A3138) in the HUD's panel colour.</summary>
        static Texture2D RoundedTex(Color fill)
        {
            const int N = 64; const float R = 10f;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var border = new Color32(0x2A, 0x31, 0x38, 255);
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x + 0.5f - N / 2f) - (N / 2f - R), 0f), dy = Mathf.Max(Mathf.Abs(y + 0.5f - N / 2f) - (N / 2f - R), 0f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy) - R; // < 0 inside
                    float a = Mathf.Clamp01(0.5f - d), edge = Mathf.Clamp01(d + 2f);
                    var c = Color.Lerp(fill, border, edge);
                    c.a = a * Mathf.Lerp(fill.a, 1f, edge);
                    px[y * N + x] = c;
                }
            t.SetPixels(px); t.Apply();
            return t;
        }

        static Texture2D turnLeftTex, turnRightTex;

        /// <summary>A white open circle with an arrowhead, 64px, anti-aliased: counter-clockwise (the rotate-left button) or its mirror.</summary>
        static Texture2D TurnTex(bool ccw)
        {
            const int N = 64; const float R = 18f, Half = 2.6f, A0 = 55f, A1 = 360f;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[N * N];
            // The arrowhead: a triangle at the arc's end (angle A1), pointing along the turn.
            var end = new Vector2(Mathf.Cos(A1 * Mathf.Deg2Rad), Mathf.Sin(A1 * Mathf.Deg2Rad)) * R;
            var tan = new Vector2(-Mathf.Sin(A1 * Mathf.Deg2Rad), Mathf.Cos(A1 * Mathf.Deg2Rad));
            var nor = new Vector2(Mathf.Cos(A1 * Mathf.Deg2Rad), Mathf.Sin(A1 * Mathf.Deg2Rad));
            Vector2 tip = end + tan * 11f, b1 = end + nor * 8.5f, b2 = end - nor * 8.5f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    var p = new Vector2(x + 0.5f - N / 2f, y + 0.5f - N / 2f);
                    float ang = Mathf.Atan2(p.y, p.x) * Mathf.Rad2Deg; if (ang < 0) ang += 360f;
                    float ring = Half - Mathf.Abs(p.magnitude - R); // > 0 inside the stroke
                    float inArc = (ang >= A0 && ang <= A1 - 2f) ? 1f : 0f;
                    float tri = Mathf.Min(Edge(tip, b2, p), Mathf.Min(Edge(b2, b1, p), Edge(b1, tip, p)));
                    float a = Mathf.Max(Mathf.Clamp01(ring + 0.5f) * inArc, Mathf.Clamp01(tri + 0.5f));
                    px[y * N + x] = new Color(1f, 1f, 1f, a);
                }
            if (!ccw) { var m = new Color[N * N]; for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) m[y * N + x] = px[y * N + (N - 1 - x)]; px = m; }
            t.SetPixels(px); t.Apply();
            return t;
        }

        /// <summary>Signed distance of p inside the half-plane left of a-&gt;b (positive inside a counter-clockwise triangle).</summary>
        static float Edge(Vector2 a, Vector2 b, Vector2 p)
        {
            var d = (b - a).normalized;
            return d.x * (p.y - a.y) - d.y * (p.x - a.x);
        }

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || !PadVisible) return;
            if (padTex == null) { padTex = RoundedTex(new Color(16 / 255f, 19 / 255f, 23 / 255f, 0.9f)); padTexHeld = RoundedTex(new Color(42 / 255f, 49 / 255f, 56 / 255f, 0.95f)); }
            GUI.depth = -100;
            var rs = PadRects();
            for (int i = 0; i < 4; i++)
            {
                var r = rs[i];
                GUI.color = Color.white;
                GUI.DrawTexture(r, padBtn == i ? padTexHeld : padTex);
                GUI.color = new Color(0.91f, 0.93f, 0.95f, 1f);
                var c = r.center; float u = r.width * 0.2f, w = Mathf.Max(2f, r.width * 0.07f);
                if (i < 2)
                {
                    GUI.DrawTexture(new Rect(c.x - u, c.y - w / 2f, 2f * u, w), Texture2D.whiteTexture);
                    if (i == 0) GUI.DrawTexture(new Rect(c.x - w / 2f, c.y - u, w, 2f * u), Texture2D.whiteTexture);
                }
                else
                {
                    if (turnLeftTex == null) { turnLeftTex = TurnTex(true); turnRightTex = TurnTex(false); }
                    float q = r.width * 0.7f;
                    GUI.DrawTexture(new Rect(c.x - q / 2f, c.y - q / 2f, q, q), i == 2 ? turnLeftTex : turnRightTex);
                }
            }
            GUI.color = Color.white;
        }

        /// <summary>Remote look-around (stream viewers): pan by a fraction of the view, zoom, rotate.</summary>
        public void Nudge(float fx, float fy, float zoom, float yaw)
        {
            var right = Quaternion.Euler(0, Yaw, 0) * Vector3.right;
            var fwd = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            float halfH = Cam.orthographic ? Distance : Distance * 0.35f;
            Focus += right * fx * halfH * 2f * Cam.aspect + fwd * fy * halfH * 2f / Mathf.Sin(Pitch * Mathf.Deg2Rad);
            Distance = Mathf.Clamp(Distance * zoom, MinDist, ZoomMax);
            Yaw += yaw;
        }

        /// <summary>
        /// Clamp an orthographic view's focus so the picture stays over the map: the view's ground footprint (a
        /// rectangle turned by the yaw) may hang off an edge by a corner, never by half the screen. A view wider
        /// than the map centres on it.
        /// </summary>
        public static Vector3 KeepOverMap(Vector3 focus, float orthoSize, float aspect, float yaw, float pitch, Vector2 bounds)
        {
            const float Overhang = 0.5f; // 1 = the whole footprint stays inside; 0.5 leaves at most about a quarter of the screen off-map, at a map corner
            float halfW = orthoSize * aspect, halfD = orthoSize / Mathf.Max(0.2f, Mathf.Sin(pitch * Mathf.Deg2Rad));
            float c = Mathf.Abs(Mathf.Cos(yaw * Mathf.Deg2Rad)), s = Mathf.Abs(Mathf.Sin(yaw * Mathf.Deg2Rad));
            float ex = (c * halfW + s * halfD) * Overhang, ez = (s * halfW + c * halfD) * Overhang;
            focus.x = 2 * ex >= bounds.x ? bounds.x / 2 : Mathf.Clamp(focus.x, ex, bounds.x - ex);
            focus.z = 2 * ez >= bounds.y ? bounds.y / 2 : Mathf.Clamp(focus.z, ez, bounds.y - ez);
            focus.y = 0;
            return focus;
        }

        public bool GroundPoint(Vector3 screen, out Vector3 p)
        {
            var ray = Cam.ScreenPointToRay(screen);
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out float d)) { p = ray.GetPoint(d); return true; }
            p = default;
            return false;
        }
    }
}
