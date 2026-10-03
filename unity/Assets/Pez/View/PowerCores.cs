using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The power plant's two towers as open stacks with the reactor glowing from inside, instead of a solid cyan cap
    /// that read as a pencil eraser. The art pack's top is a dark ring (stage_3__M_Dark) with a cyan cylinder poking out of
    /// it (stage_3__M_E_Cyan). Apply hides both and builds, per tower:
    ///  - a thick dark lip around an open throat (outer wall, top annulus, inner wall going down);
    ///  - the glowing core sunk well inside, under a dark cross grille;
    ///  - a band of glow on the inner wall just above the core, so the light visibly fills the throat (the bloom carries
    ///    it out of the mouth; an additive shell of light was tried and read as a glass tube).
    /// The emissive parts use the pack's M_E_Cyan material, so PezShade (power load, low-power flicker) drives them like
    /// the original cap. Everything sits under stage_3, so it rises with the last build stage.
    /// </summary>
    public static class PowerCores
    {
        const int Segments = 28;
        public const string HaloName = "core_halo";
        static Mesh empty;
        static Mesh Empty => empty != null ? empty : empty = new Mesh { name = "retired" };

        public static void Apply(GameObject go)
        {
            var core = PezMotion.FindDeep(go.transform, "stage_3__M_E_Cyan");
            var rim = PezMotion.FindDeep(go.transform, "stage_3__M_Dark");
            if (core == null || rim == null) return;
            var coreMf = core.GetComponent<MeshFilter>(); var rimMf = rim.GetComponent<MeshFilter>();
            var coreR = core.GetComponent<MeshRenderer>(); var rimR = rim.GetComponent<MeshRenderer>();
            if (coreMf == null || rimMf == null || coreR == null || rimR == null || !rimMf.sharedMesh.isReadable || !coreMf.sharedMesh.isReadable) return;
            var coreMat = coreR.sharedMaterial; var rimMat = rimR.sharedMaterial;
            var rimV = rimMf.sharedMesh.vertices; var coreV = coreMf.sharedMesh.vertices;
            var parent = rim.parent;

            var dark = new Builder(); var glow = new Builder();
            foreach (float side in new[] { -1f, 1f })
            {
                if (!Bounds(rimV, side, out var rb) || !Bounds(coreV, side, out var cb)) continue;
                // Positions are in the rim's local space; the rim and core nodes sit at the stage's origin.
                var c = rim.localPosition + rim.localRotation * new Vector3(rb.center.x, 0f, rb.center.z);
                float R = Mathf.Max(rb.extents.x, rb.extents.z), top = rb.max.y + rim.localPosition.y, bottom = rb.min.y + rim.localPosition.y;
                float r = Mathf.Min(Mathf.Max(cb.extents.x, cb.extents.z), R * 0.8f) * 0.96f; // the throat
                // The tower body below has a solid top just under the pack's ring (about top - 0.03), so the core sits just
                // above it and the crown rises instead: a lip 0.16 higher gives the throat its depth.
                float coreY = top - 0.015f, lip = coreY + 0.16f, grilleY = coreY + 0.035f;

                dark.Wall(c, R, bottom, lip, outward: true);       // outer wall of the stack's crown
                dark.Annulus(c, r, R, lip);                        // the lip
                dark.Wall(c, r, coreY, lip, outward: false);       // the throat, looking in
                glow.Wall(c, r * 0.995f, coreY, coreY + 0.06f, outward: false); // light on the wall just above the core
                glow.Disc(c, r * 0.99f, coreY);                    // the core itself, sunk inside
                dark.Bar(c, r * 0.98f, grilleY, 0.022f, 0f);       // a cross grille over it
                dark.Bar(c, r * 0.98f, grilleY, 0.022f, 90f);
            }

            Add(parent, "core_stack", Models.FlatMesh(dark.Mesh("core_stack")), rimMat, true);
            Add(parent, "core_glow", Models.FlatMesh(glow.Mesh("core_glow")), coreMat, false);
            // Retire the pack's cap and ring by emptying their meshes: PezEmerge re-enables every renderer of a stage as
            // it rises, so merely disabling them brings the old "eraser" back.
            rimMf.sharedMesh = Empty; coreMf.sharedMesh = Empty;
        }

        static MeshRenderer Add(Transform parent, string name, Mesh mesh, Material mat, bool shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = shadows;
            return r;
        }

        /// <summary>The bounds of one tower's vertices (the two towers sit either side of x = 0).</summary>
        static bool Bounds(Vector3[] v, float side, out Bounds b)
        {
            b = default; bool any = false;
            foreach (var p in v)
            {
                if (Mathf.Sign(p.x) != side) continue;
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
            }
            return any;
        }

        class Builder
        {
            readonly List<Vector3> v = new List<Vector3>();
            readonly List<int> t = new List<int>();
            readonly List<Color> col = new List<Color>();

            static Vector3 Ring(float a, float r) => new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);

            Vector3 facing; // the side the next faces should show (null = as given)
            bool useFacing;

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color ca, Color cb, Color cc, Color cd)
            {
                // Unity's front face: Cross(b - a, c - a) points out of it. Flip the quad if it faces the wrong way.
                if (useFacing && Vector3.Dot(Vector3.Cross(b - a, c - a), facing) < 0f) { (b, d) = (d, b); (cb, cd) = (cd, cb); }
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                col.Add(ca); col.Add(cb); col.Add(cc); col.Add(cd);
                t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }

            void Face(Vector3 n) { facing = n; useFacing = true; }

            public void Wall(Vector3 c, float r, float y0, float y1, bool outward)
            {
                for (int s = 0; s < Segments; s++)
                {
                    float a0 = s * Mathf.PI * 2f / Segments, a1 = (s + 1) * Mathf.PI * 2f / Segments;
                    Vector3 p0 = c + Ring(a0, r), p1 = c + Ring(a1, r);
                    Vector3 b0 = p0 + Vector3.up * y0, b1 = p1 + Vector3.up * y0, u0 = p0 + Vector3.up * y1, u1 = p1 + Vector3.up * y1;
                    var radial = Ring((a0 + a1) * 0.5f, 1f);
                    Face(outward ? radial : -radial);
                    Quad(b0, u0, u1, b1, Color.white, Color.white, Color.white, Color.white);
                }
            }

            public void Annulus(Vector3 c, float rIn, float rOut, float y)
            {
                var up = Vector3.up * y;
                Face(Vector3.up);
                for (int s = 0; s < Segments; s++)
                {
                    float a0 = s * Mathf.PI * 2f / Segments, a1 = (s + 1) * Mathf.PI * 2f / Segments;
                    Quad(c + up + Ring(a0, rIn), c + up + Ring(a0, rOut), c + up + Ring(a1, rOut), c + up + Ring(a1, rIn),
                         Color.white, Color.white, Color.white, Color.white);
                }
            }

            public void Disc(Vector3 c, float r, float y)
            {
                var up = Vector3.up * y; var mid = c + up;
                for (int s = 0; s < Segments; s++)
                {
                    float a0 = s * Mathf.PI * 2f / Segments, a1 = (s + 1) * Mathf.PI * 2f / Segments;
                    int i = v.Count;
                    Vector3 pa = c + up + Ring(a1, r), pb = c + up + Ring(a0, r);
                    if (Vector3.Dot(Vector3.Cross(pa - mid, pb - mid), Vector3.up) < 0f) (pa, pb) = (pb, pa);
                    v.Add(mid); v.Add(pa); v.Add(pb);
                    col.Add(Color.white); col.Add(Color.white); col.Add(Color.white);
                    t.AddRange(new[] { i, i + 1, i + 2 });
                }
            }

            /// <summary>A flat bar across the throat (a grille strut), half-width w, rotated by deg about the stack's axis.</summary>
            public void Bar(Vector3 c, float halfLen, float y, float w, float deg)
            {
                var q = Quaternion.Euler(0f, deg, 0f);
                Vector3 P(float x, float z, float h) => c + q * new Vector3(x, 0f, z) + Vector3.up * h;
                float hgt = 0.025f;
                // top
                Face(Vector3.up);
                Quad(P(-halfLen, -w, y + hgt), P(-halfLen, w, y + hgt), P(halfLen, w, y + hgt), P(halfLen, -w, y + hgt), Color.white, Color.white, Color.white, Color.white);
                // sides
                Face(q * Vector3.back);
                Quad(P(-halfLen, -w, y), P(-halfLen, -w, y + hgt), P(halfLen, -w, y + hgt), P(halfLen, -w, y), Color.white, Color.white, Color.white, Color.white);
                Face(q * Vector3.forward);
                Quad(P(halfLen, w, y), P(halfLen, w, y + hgt), P(-halfLen, w, y + hgt), P(-halfLen, w, y), Color.white, Color.white, Color.white, Color.white);
            }

            /// <summary>A flared, open shell of light rising from the mouth: cyan at its foot, fading to nothing at the top.</summary>
            public void Halo(Vector3 c, float r0, float y0, float y1, float r1)
            {
                var foot = new Color(0.45f, 0.9f, 1f, 0.38f); var tip = new Color(0.45f, 0.9f, 1f, 0f);
                for (int s = 0; s < Segments; s++)
                {
                    float a0 = s * Mathf.PI * 2f / Segments, a1 = (s + 1) * Mathf.PI * 2f / Segments;
                    Vector3 b0 = c + Ring(a0, r0) + Vector3.up * y0, b1 = c + Ring(a1, r0) + Vector3.up * y0;
                    Vector3 u0 = c + Ring(a0, r1) + Vector3.up * y1, u1 = c + Ring(a1, r1) + Vector3.up * y1;
                    // Both faces, so the shaft reads from any side.
                    useFacing = false;
                    Quad(b0, u0, u1, b1, foot, tip, tip, foot);
                    Quad(b0, b1, u1, u0, foot, foot, tip, tip);
                }
            }

            public Mesh Mesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v);
                m.SetColors(col);
                m.SetTriangles(t, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
