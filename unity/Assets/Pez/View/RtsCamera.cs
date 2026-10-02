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
        public float MinDist = 4f, MaxDist = 45f;
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
            Focus += move.normalized * speed * dt;

            if (keys && Input.GetKey(KeyCode.Q)) Yaw += 70f * dt;
            if (keys && Input.GetKey(KeyCode.E)) Yaw -= 70f * dt;

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f) Distance = Mathf.Clamp(Distance * (1f - scroll * 0.1f), MinDist, MaxDist);

            // Middle-drag pans by grabbing the ground point.
            if (Input.GetMouseButtonDown(2)) { dragging = GroundPoint(Input.mousePosition, out dragAnchor); }
            if (Input.GetMouseButtonUp(2)) dragging = false;
            if (dragging && GroundPoint(Input.mousePosition, out var now)) Focus += dragAnchor - now;

            Focus.x = Mathf.Clamp(Focus.x, 0, Bounds.x);
            Focus.z = Mathf.Clamp(Focus.z, -4, Bounds.y);
            if (Cam.orthographic)
            {
                var rot = Quaternion.Euler(Pitch, Yaw, 0);
                Cam.orthographicSize = Distance;
                transform.position = Focus - rot * Vector3.forward * 150f;
                transform.rotation = rot;
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
