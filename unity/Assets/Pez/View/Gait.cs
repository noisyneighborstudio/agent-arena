using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Procedural walk cycle for infantry. The stride follows the ground the soldier actually covers (its interpolated
    /// position from frame to frame), so a soldier held to a group's pace or blocked in a crowd never moonwalks.
    /// Legs swing from the hips and the swinging leg tucks up; the body drops while both feet are planted and rises as the
    /// legs pass (two bobs per stride, which keeps the soles on the ground); the upper body sways over the planted foot,
    /// counter-twists, leans into the direction of travel and into turns. Stopped, the legs settle under the hips and the
    /// soldier shifts its weight on a slow sine.
    ///
    /// Art-pack infantry have no leg nodes, so <see cref="FromModel"/> cuts the two leg boxes out of the body mesh once
    /// per source mesh (cached) and hangs them from hip pivots. Placeholders build real legs (<see cref="Models"/>).
    /// Per frame this is a handful of transform writes and no allocations.
    /// </summary>
    public class Gait
    {
        readonly Transform body, hips, legL, legR;
        readonly float legLength;  // hip to sole, in the units of the legs' parent
        readonly float unit;       // body-local units per leg-parent unit (the model's own scale)
        readonly Vector3 hipsRest;
        readonly float seed;
        float phase, speed, weight, lean, roll, lastYaw;
        Vector3 lastPos;
        bool primed;

        const float MinSwing = 12f, MaxSwing = 30f;  // degrees either side of vertical
        const float FullStrideSpeed = 0.9f;          // world units/s at which the swing reaches MaxSwing

        public Gait(Transform body, Transform hips, Transform legL, Transform legR, float legLength, float unit, int seed)
        {
            this.body = body; this.hips = hips; this.legL = legL; this.legR = legR;
            this.legLength = legLength; this.unit = unit;
            hipsRest = hips != null ? hips.localPosition : Vector3.zero;
            this.seed = (seed * 0.618034f) % 1f * 6.2831853f;
        }

        public bool HasLegs => legL != null && legR != null;

        public void Tick(Transform root, float dt)
        {
            var p = root.position;
            float yaw = root.eulerAngles.y;
            if (!primed || dt <= 0f) { lastPos = p; lastYaw = yaw; primed = true; if (dt <= 0f) return; }
            float dx = p.x - lastPos.x, dz = p.z - lastPos.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            float turn = Mathf.DeltaAngle(lastYaw, yaw) / dt; // deg/s, + is a right turn
            lastPos = p; lastYaw = yaw;
            if (dist > 2f) dist = 0f; // a jump (back out of the fog, out of a transport), not a step

            // Smoothed ground speed; turning on the spot shuffles the feet a little.
            speed = Mathf.Lerp(speed, dist / dt, 1f - Mathf.Exp(-dt * 10f));
            float drive = speed + Mathf.Abs(turn) * Mathf.Deg2Rad * 0.06f;
            float target = Mathf.Clamp01(drive / 0.2f);
            weight = Mathf.MoveTowards(weight, target, dt * (target > weight ? 6f : 4f));

            float scale = body.localScale.y * unit;         // world units per leg unit
            float legWorld = Mathf.Max(0.05f, legLength * scale);
            float swing = Mathf.Lerp(MinSwing, MaxSwing, Mathf.Clamp01(speed / FullStrideSpeed));
            // A full stride (two steps) covers 4 L sin(swing): the planted foot travels back 2 L sin(swing) per step.
            float stride = 4f * legWorld * Mathf.Sin(swing * Mathf.Deg2Rad);
            phase += drive * dt / stride * 6.2831853f;
            if (phase > 6.2831853f) phase -= 6.2831853f;

            float s = Mathf.Sin(phase), c = Mathf.Cos(phase);
            float a = swing * weight * s;                   // left foot forward (deg), right foot mirrors
            if (HasLegs)
            {
                // Foot forward is a negative X rotation for a leg hanging down. The leg swinging forward tucks up.
                legL.localRotation = Quaternion.Euler(-a, 0f, 0f);
                legR.localRotation = Quaternion.Euler(a, 0f, 0f);
                float tuckL = Mathf.Max(0f, c) * weight, tuckR = Mathf.Max(0f, -c) * weight;
                legL.localScale = new Vector3(1f, 1f - 0.2f * tuckL * tuckL, 1f);
                legR.localScale = new Vector3(1f, 1f - 0.2f * tuckR * tuckR, 1f);
            }

            // Rigid legs keep the soles down if the hips drop by L(1 - cos a): lowest with both feet planted, highest
            // as the legs pass. Without legs, a plain two-bobs-per-stride bounce stands in.
            float drop = HasLegs ? legWorld * (1f - Mathf.Cos(a * Mathf.Deg2Rad))
                                 : 0.03f * weight * (0.5f - 0.5f * Mathf.Cos(2f * phase));
            float lift = HasLegs ? 0f : 0.03f * weight - drop;
            body.localPosition = new Vector3(0f, HasLegs ? -drop : lift, 0f);

            // Lean forward with speed and into turns (smoothed so it reads as weight, not a snap).
            float k = 1f - Mathf.Exp(-dt * 8f);
            lean = Mathf.Lerp(lean, Mathf.Clamp01(speed / FullStrideSpeed) * 8f * weight, k);
            roll = Mathf.Lerp(roll, Mathf.Clamp(-turn * Mathf.Min(speed, 1.5f) * 0.03f, -7f, 7f), k);

            float t = Time.time;
            float idle = 1f - weight;
            // Walking: sway over the planted foot once per stride, shoulders counter-twist. Idle: a slow weight shift.
            float sway = -c * 0.012f * weight + Mathf.Sin(t * 2.0944f + seed) * 0.008f * idle;  // 3 s idle period
            float breathe = Mathf.Sin(t * 2.6f + seed) * idle;
            var upper = Quaternion.Euler(lean + breathe * 0.8f, -s * 6f * weight, roll + c * 3f * weight - sway * 120f * idle);
            if (hips != null)
            {
                hips.localPosition = hipsRest + new Vector3(sway, breathe * 0.002f, 0f);
                hips.localRotation = upper;
            }
            else body.localRotation = upper;
        }

        // ---------------------------------------------------------------- art-pack models

        /// <summary>The leg boxes of a model mesh cut out and re-centred on their hip pivots (cached per source mesh).</summary>
        class Split
        {
            public Mesh Rest, Left, Right;
            public Vector3 PivotL, PivotR;
        }

        static readonly Dictionary<Mesh, Split> splits = new Dictionary<Mesh, Split>();

        // Art-pack infantry (v0.4): legs are boxes from the ground to y = 0.22 within |x| <= 0.075; the torso starts at 0.22
        // and is 0.085 wide, so anything wider than 0.08 below the hips belongs to the body.
        const float HipY = 0.22f, LegMaxX = 0.08f;

        /// <summary>
        /// Rig an art-pack soldier: hang the upper body (torso, head, arms/turret, packs) from a hips pivot and cut the legs
        /// out of the body mesh onto two leg pivots. Call before PezMotion caches its node rest poses.
        /// </summary>
        public static Gait FromModel(Transform body, GameObject model, int seed)
        {
            var root = model.transform;
            var kids = new List<Transform>();
            foreach (Transform c in root) kids.Add(c);
            var hips = new GameObject("hips").transform;
            hips.SetParent(root, false);
            hips.localPosition = new Vector3(0f, HipY, 0f);
            Transform legL = null, legR = null;

            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                var t = mf.transform;
                if (t.parent != root || t.localPosition != Vector3.zero || t.localRotation != Quaternion.identity || t.localScale != Vector3.one) continue;
                var sp = SplitOf(mf.sharedMesh);
                if (sp == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (sp.Left != null) AddLeg(root, ref legL, "leg_l", sp.PivotL, sp.Left, mr);
                if (sp.Right != null) AddLeg(root, ref legR, "leg_r", sp.PivotR, sp.Right, mr);
                mf.sharedMesh = sp.Rest;
                if (sp.Rest.vertexCount == 0 && mr != null) mr.enabled = false;
            }
            foreach (var c in kids)
            {
                var lp = c.localPosition;
                c.SetParent(hips, false);
                c.localPosition = lp - hips.localPosition;
            }
            if (legL == null || legR == null)
            {
                // Couldn't find both legs: leave whatever was cut where it hangs and fall back to bob, sway and lean.
                return new Gait(body, hips, null, null, HipY, root.localScale.y, seed);
            }
            return new Gait(body, hips, legL, legR, HipY, root.localScale.y, seed);
        }

        static void AddLeg(Transform root, ref Transform pivot, string name, Vector3 at, Mesh mesh, MeshRenderer src)
        {
            if (pivot == null)
            {
                pivot = new GameObject(name).transform;
                pivot.SetParent(root, false);
                pivot.localPosition = at;
            }
            var go = new GameObject(name + "_" + mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(pivot, false);
            go.transform.localPosition = at - pivot.localPosition;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            if (src != null) r.sharedMaterials = src.sharedMaterials;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            r.receiveShadows = true;
        }

        static Split SplitOf(Mesh src)
        {
            if (src == null) return null;
            if (splits.TryGetValue(src, out var cached)) return cached;
            Split result = null;
            if (src.isReadable && src.subMeshCount == 1 && src.GetTopology(0) == MeshTopology.Triangles)
            {
                var v = src.vertices;
                var uv = src.uv;
                var col = src.colors;
                var tris = src.triangles;
                var rest = new List<int>();
                var left = new List<int>();
                var right = new List<int>();
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 a = v[tris[i]], b = v[tris[i + 1]], c = v[tris[i + 2]];
                    bool leg = Mathf.Max(a.y, b.y, c.y) <= HipY + 0.001f && Mathf.Max(Mathf.Abs(a.x), Mathf.Abs(b.x), Mathf.Abs(c.x)) <= LegMaxX;
                    float cx = (a.x + b.x + c.x) / 3f;
                    var into = !leg ? rest : cx < 0f ? left : right;
                    into.Add(tris[i]); into.Add(tris[i + 1]); into.Add(tris[i + 2]);
                }
                if (left.Count > 0 || right.Count > 0)
                {
                    result = new Split { Rest = Sub(src, v, uv, col, rest, Vector3.zero, "_upper") };
                    if (left.Count > 0) { result.PivotL = Pivot(v, left); result.Left = Sub(src, v, uv, col, left, result.PivotL, "_leg_l"); }
                    if (right.Count > 0) { result.PivotR = Pivot(v, right); result.Right = Sub(src, v, uv, col, right, result.PivotR, "_leg_r"); }
                }
            }
            splits[src] = result;
            return result;
        }

        static Vector3 Pivot(Vector3[] v, List<int> tris)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var i in tris)
            {
                minX = Mathf.Min(minX, v[i].x); maxX = Mathf.Max(maxX, v[i].x);
                minZ = Mathf.Min(minZ, v[i].z); maxZ = Mathf.Max(maxZ, v[i].z);
            }
            return new Vector3((minX + maxX) * 0.5f, HipY, (minZ + maxZ) * 0.5f);
        }

        /// <summary>A mesh of just the given triangles, its vertices shifted so `origin` is the new origin.</summary>
        static Mesh Sub(Mesh src, Vector3[] v, Vector2[] uv, Color[] col, List<int> tris, Vector3 origin, string suffix)
        {
            bool hasUv = uv.Length == v.Length, hasCol = col.Length == v.Length;
            var map = new Dictionary<int, int>();
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var cols = new List<Color>();
            var outTris = new int[tris.Count];
            for (int i = 0; i < tris.Count; i++)
            {
                int o = tris[i];
                if (!map.TryGetValue(o, out int n))
                {
                    n = map[o] = verts.Count;
                    verts.Add(v[o] - origin);
                    if (hasUv) uvs.Add(uv[o]);
                    if (hasCol) cols.Add(col[o]);
                }
                outTris[i] = n;
            }
            var m = new Mesh { name = src.name + suffix };
            m.SetVertices(verts);
            if (hasUv) m.SetUVs(0, uvs);
            if (hasCol) m.SetColors(cols);
            m.SetTriangles(outTris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
