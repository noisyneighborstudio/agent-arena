using UnityEngine;

namespace Pez.View
{
    /// <summary>Angled RTS camera: WASD/arrows/edge-pan, middle-drag pan, scroll zoom, Q/E rotate.</summary>
    public class RtsCamera : MonoBehaviour
    {
        public Vector3 Focus;
        public float Distance = 26f, Yaw = 0f, Pitch = 52f;
        public float MinDist = 8f, MaxDist = 70f;
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
            var fwd = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            var right = Quaternion.Euler(0, Yaw, 0) * Vector3.right;
            float speed = Distance * 1.1f;
            var move = Vector3.zero;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) move += fwd;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) move -= fwd;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) move += right;
            if (Input.GetKey(KeyCode.A) && !Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftArrow)) move -= right;
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

            if (Input.GetKey(KeyCode.Q)) Yaw += 70f * dt;
            if (Input.GetKey(KeyCode.E)) Yaw -= 70f * dt;

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f) Distance = Mathf.Clamp(Distance * (1f - scroll * 0.1f), MinDist, MaxDist);

            // Middle-drag pans by grabbing the ground point.
            if (Input.GetMouseButtonDown(2)) { dragging = GroundPoint(Input.mousePosition, out dragAnchor); }
            if (Input.GetMouseButtonUp(2)) dragging = false;
            if (dragging && GroundPoint(Input.mousePosition, out var now)) Focus += dragAnchor - now;

            Focus.x = Mathf.Clamp(Focus.x, 0, Bounds.x);
            Focus.z = Mathf.Clamp(Focus.z, -4, Bounds.y);
            // Look a bit more top-down when zoomed out.
            float pitch = Mathf.Lerp(Pitch, 68f, Mathf.InverseLerp(MinDist, MaxDist, Distance));
            var rot = Quaternion.Euler(pitch, Yaw, 0);
            transform.position = Focus - rot * Vector3.forward * Distance;
            transform.rotation = rot;
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
