using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// RTS camera: WASD/arrows/edge-pan, middle-drag pan, scroll zoom, Q/E rotate.
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
        public bool EdgePan = true;
        public Camera Cam { get; private set; }
        Vector3 dragAnchor;
        bool dragging;

        void Awake()
        {
            Cam = GetComponent<Camera>();
        }

        public void LookAt(Vector3 p) { Focus = new Vector3(p.x, 0, p.z); }

        /// <summary>While this returns a point, the camera rides along with it (a selected mobile unit) instead of panning.</summary>
        public System.Func<Vector3?> Follow;
        public bool Following { get; private set; }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            bool keys = !Hud.Typing; // don't pan while someone is typing orders
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
            var follow = Follow?.Invoke();
            Following = follow.HasValue;
            if (Following) Focus = Vector3.Lerp(Focus, new Vector3(follow.Value.x, 0, follow.Value.z), 1f - Mathf.Exp(-6f * dt));
            else Focus += move.normalized * speed * dt;

            if (keys && Input.GetKey(KeyCode.Q)) Yaw += 70f * dt;
            if (keys && Input.GetKey(KeyCode.E)) Yaw -= 70f * dt;

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f) Distance = Mathf.Clamp(Distance * (1f - scroll * 0.1f), MinDist, MaxDist);

            // Middle-drag pans by grabbing the ground point.
            if (Input.GetMouseButtonDown(2)) { dragging = GroundPoint(Input.mousePosition, out dragAnchor); }
            if (Input.GetMouseButtonUp(2)) dragging = false;
            if (dragging && !Following && GroundPoint(Input.mousePosition, out var now)) Focus += dragAnchor - now;

            Distance = Mathf.Clamp(Distance, MinDist, MaxDist); // the API and stream viewers can set it directly
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
        }

        /// <summary>Remote look-around (stream viewers): pan by a fraction of the view, zoom, rotate.</summary>
        public void Nudge(float fx, float fy, float zoom, float yaw)
        {
            var right = Quaternion.Euler(0, Yaw, 0) * Vector3.right;
            var fwd = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            float halfH = Cam.orthographic ? Distance : Distance * 0.35f;
            Focus += right * fx * halfH * 2f * Cam.aspect + fwd * fy * halfH * 2f / Mathf.Sin(Pitch * Mathf.Deg2Rad);
            Distance = Mathf.Clamp(Distance * zoom, MinDist, MaxDist);
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
