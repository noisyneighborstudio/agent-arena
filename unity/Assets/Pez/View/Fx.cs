using UnityEngine;

namespace Pez.View
{
    /// <summary>Animates a transient effect object: grow/fade and self-destruct.</summary>
    public class FxLife : MonoBehaviour
    {
        public float Life = 0.5f, Grow = 1f;
        public Vector3 Velocity;
        public float Gravity;
        public Light Light;
        public Material Mat;
        public Color Color;
        public float Spin;
        float age;
        Vector3 baseScale;

        void Start() { baseScale = transform.localScale; }

        void Update()
        {
            age += Time.deltaTime;
            float t = age / Life;
            if (t >= 1f) { Destroy(gameObject); if (Mat != null) Destroy(Mat); return; }
            transform.localScale = baseScale * (1f + (Grow - 1f) * t);
            Velocity += Vector3.down * Gravity * Time.deltaTime;
            transform.position += Velocity * Time.deltaTime;
            if (Spin != 0) transform.Rotate(Spin * Time.deltaTime, Spin * 0.7f * Time.deltaTime, 0);
            if (transform.position.y < 0.02f && Gravity > 0) { var p = transform.position; p.y = 0.02f; transform.position = p; Velocity *= 0.5f; Velocity.y = 0; }
            if (Light != null) Light.intensity *= 1f - Mathf.Clamp01(Time.deltaTime * 12f);
            if (Mat != null) { var c = Color; c.a *= 1f - t; Mat.color = c; }
        }
    }

    public static class Fx
    {
        static GameObject Blob(Vector3 pos, float size, Color c, bool additive, float life, float grow)
        {
            var mat = Mats.UnlitInstance(c, additive);
            var t = Models.Part(null, PrimitiveType.Sphere, pos, Vector3.one * size, mat);
            t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var f = t.gameObject.AddComponent<FxLife>();
            f.Life = life; f.Grow = grow; f.Mat = mat; f.Color = c;
            return t.gameObject;
        }

        static void Flash(Vector3 pos, Color c, float intensity, float range)
        {
            var go = new GameObject("flash");
            go.transform.position = pos + Vector3.up * 0.5f;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.color = c; l.intensity = intensity; l.range = range;
            l.shadows = LightShadows.None;
            var f = go.AddComponent<FxLife>();
            f.Life = 0.35f; f.Light = l;
        }

        public static void MuzzleFlash(Vector3 pos, float size)
        {
            Blob(pos, size, new Color(1f, 0.85f, 0.4f, 1f), true, 0.08f, 1.6f);
            Flash(pos, new Color(1f, 0.8f, 0.4f), 2.5f, 2.5f + size * 6);
        }

        public static void Tracer(Vector3 a, Vector3 b, Color c)
        {
            var go = new GameObject("tracer");
            var lr = go.AddComponent<LineRenderer>();
            var mat = Mats.UnlitInstance(c, true);
            lr.sharedMaterial = mat;
            lr.positionCount = 2;
            lr.SetPosition(0, a); lr.SetPosition(1, b);
            lr.startWidth = 0.035f; lr.endWidth = 0.02f;
            lr.startColor = lr.endColor = Color.white;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var f = go.AddComponent<FxLife>();
            f.Life = 0.07f; f.Mat = mat; f.Color = c;
        }

        public static void Beam(Vector3 a, Vector3 b, Color c, float width)
        {
            var go = new GameObject("beam");
            var lr = go.AddComponent<LineRenderer>();
            var mat = Mats.UnlitInstance(c, true);
            lr.sharedMaterial = mat;
            lr.positionCount = 2;
            lr.SetPosition(0, a); lr.SetPosition(1, b);
            lr.startWidth = width; lr.endWidth = width * 0.6f;
            lr.startColor = lr.endColor = Color.white;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var f = go.AddComponent<FxLife>();
            f.Life = 0.22f; f.Mat = mat; f.Color = c;
            Flash(b, c, 3f, 3f);
        }

        public static void Explosion(Vector3 pos, float size)
        {
            Blob(pos + Vector3.up * size * 0.3f, size * 0.6f, new Color(1f, 0.95f, 0.7f, 1f), true, 0.15f, 2.2f);
            Blob(pos + Vector3.up * size * 0.3f, size * 0.8f, new Color(1f, 0.5f, 0.1f, 0.9f), true, 0.35f, 2.4f);
            Flash(pos, new Color(1f, 0.6f, 0.25f), 4f + size * 4f, 3f + size * 5f);
            int smoke = Mathf.Clamp((int)(size * 5), 2, 12);
            for (int i = 0; i < smoke; i++)
            {
                var g = Blob(pos + Random.insideUnitSphere * size * 0.4f + Vector3.up * size * 0.3f, size * Random.Range(0.3f, 0.6f),
                    new Color(0.227f, 0.188f, 0.149f, 0.5f), false, Random.Range(1.2f, 2.4f), 2.5f); // warm sugar-dust smoke #3A3026
                g.GetComponent<FxLife>().Velocity = new Vector3(Random.Range(-0.3f, 0.3f), Random.Range(0.5f, 1.2f), Random.Range(-0.3f, 0.3f)) * Mathf.Sqrt(size);
            }
            int sparks = Mathf.Clamp((int)(size * 8), 3, 20);
            for (int i = 0; i < sparks; i++)
            {
                var g = Blob(pos + Vector3.up * 0.2f, 0.06f, new Color(1f, 0.7f, 0.2f, 1f), true, Random.Range(0.3f, 0.7f), 0.3f);
                var f = g.GetComponent<FxLife>();
                f.Velocity = (Random.onUnitSphere + Vector3.up * 1.2f) * Random.Range(2f, 5f) * Mathf.Sqrt(size);
                f.Gravity = 9f;
            }
        }

        public static void Debris(Vector3 pos, float size, Color c, int n)
        {
            var mat = Mats.Lit(c * 0.5f, 0.2f, 0.3f);
            for (int i = 0; i < n; i++)
            {
                var t = Models.Part(null, PrimitiveType.Cube, pos + Vector3.up * 0.3f, Vector3.one * Random.Range(0.06f, 0.18f) * size, mat);
                var f = t.gameObject.AddComponent<FxLife>();
                f.Life = Random.Range(1.5f, 3f); f.Grow = 0.6f; f.Gravity = 9f; f.Spin = Random.Range(-400f, 400f);
                f.Velocity = (Random.onUnitSphere + Vector3.up * 1.5f) * Random.Range(1.5f, 4f);
            }
        }

        public static void Scorch(Vector3 pos, float size)
        {
            var mat = Mats.UnlitInstance(new Color(0.03f, 0.03f, 0.02f, 0.7f), false);
            var t = Models.Part(null, PrimitiveType.Cylinder, new Vector3(pos.x, 0.03f, pos.z), new Vector3(size, 0.002f, size), mat);
            t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var f = t.gameObject.AddComponent<FxLife>();
            f.Life = 25f; f.Mat = mat; f.Color = new Color(0.03f, 0.03f, 0.02f, 0.7f);
        }
    }
}
