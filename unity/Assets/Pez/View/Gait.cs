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
    /// Candy troopers (build_models.candy_trooper: the rifleman, and the soldiers built on it) have hips, leg_l and leg_r
    /// nodes, which <see cref="FromModel"/> drives as they are. Older art-pack infantry have no leg nodes, so it cuts the
    /// two leg boxes out of the body mesh once per source mesh (cached) and hangs them from hip pivots. Placeholders build
    /// real legs (<see cref="Models"/>).
    ///
    /// A trooper built with knees (build_models.candy_trooper knees=True: knee_l / knee_r under its legs; the medic and the
    /// sniper) bends the swinging leg's knee instead of tucking it.
    ///
    /// Kneeling (<see cref="Kneel"/>, asked for by SoldierWorks, in the soldier's own <see cref="KneelPose"/>): one knee goes
    /// down, the other foot is planted ahead, the hips come down onto them and the upper body leans in, and the walk and the
    /// idle sway give way to it (a kneeling soldier holds still). With knees, the front shin stands upright on its planted
    /// foot (the hips drop exactly as far as its thigh comes up) and the kneeling shin lies back along the ground. With
    /// one-piece legs (the commando), the front leg reaches forward straight and the back leg tucks and swings back until its
    /// end meets the ground as the hips drop to the front sole. Gait is the only thing that moves the hips and the legs, so
    /// the walk and the kneel blend rather than fight.
    /// Per frame this is a handful of transform writes and no allocations.
    /// </summary>
    public class Gait
    {
        /// <summary>How a soldier kneels: its timing, its lean, and the shape of its legs on the ground.</summary>
        public sealed class KneelPose
        {
            /// <summary>Seconds to go down and to get up (eased).</summary>
            public float Down = 0.3f, Up = 0.3f;
            /// <summary>The upper body leans in this far (degrees); Still is how much of the idle sway it holds back.</summary>
            public float Lean = 14f, Still = 0.7f;
            /// <summary>Knees: the kneeling thigh's angle forward of vertical, the kneeling shin's angle back from straight
            /// down (90 lies flat along the ground), and the knee's height off the ground (leg units).</summary>
            public float Thigh = 15f, Shin = 95f, KneeHigh = 0.036f;
            /// <summary>One-piece legs: the front leg's reach forward (degrees) and the back leg's tuck (its length).</summary>
            public float Reach = 58f, Tuck = 0.8f;
            /// <summary>The left knee goes down (the right foot ahead); by default it's the right knee.</summary>
            public bool LeftDown;
        }

        readonly Transform body, hips, legL, legR, kneeL, kneeR;
        readonly float legLength;  // hip to sole, in the units of the legs' parent
        readonly float kneeLength; // hip to knee (knees only)
        readonly float unit;       // body-local units per leg-parent unit (the model's own scale)
        readonly Vector3 hipsRest;
        readonly float seed;
        float phase, speed, weight, lean, roll, lastYaw, kneel;
        Vector3 lastPos;
        bool primed;

        const float MinSwing = 12f, MaxSwing = 30f;  // degrees either side of vertical
        const float FullStrideSpeed = 0.9f;          // world units/s at which the swing reaches MaxSwing
        const float KneeBend = 50f;                  // knees: the swinging leg's knee bends this far at the top of its tuck

        /// <summary>Kneel (true) or stand: set every frame by whoever knows why (SoldierWorks), in <see cref="Pose"/>.
        /// Ignored without a pose or legs.</summary>
        public bool Kneel;
        /// <summary>How this soldier kneels (null: it doesn't).</summary>
        public KneelPose Pose;
        /// <summary>How far down it is: 0 standing to 1 kneeling (eased).</summary>
        public float Kneeling { get; private set; }

        public Gait(Transform body, Transform hips, Transform legL, Transform legR, float legLength, float unit, int seed,
                    Transform kneeL = null, Transform kneeR = null)
        {
            this.body = body; this.hips = hips; this.legL = legL; this.legR = legR;
            this.legLength = legLength; this.unit = unit;
            hipsRest = hips != null ? hips.localPosition : Vector3.zero;
            this.seed = (seed * 0.618034f) % 1f * 6.2831853f;
            if (kneeL != null && kneeR != null && HasLegs)
            {
                this.kneeL = kneeL; this.kneeR = kneeR;
                kneeLength = -kneeL.localPosition.y;
            }
        }

        public bool HasLegs => legL != null && legR != null;
        public bool HasKnees => kneeL != null;

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
            float tuckL = Mathf.Max(0f, c) * weight, tuckR = Mathf.Max(0f, -c) * weight;

            // Kneeling (eased): the legs fold from wherever the walk had them.
            var pose = HasLegs ? Pose : null;
            if (pose != null)
            {
                float kt = Kneel ? 1f : 0f;
                kneel = Mathf.MoveTowards(kneel, kt, dt / Mathf.Max(0.01f, kt > kneel ? pose.Down : pose.Up));
            }
            else kneel = 0f;
            float kk = kneel * kneel * (3f - 2f * kneel);
            Kneeling = kk;
            bool leftDown = pose != null && pose.LeftDown;

            // Rigid legs keep the soles down if the hips drop by L(1 - cos a): lowest with both feet planted, highest
            // as the legs pass. Without legs, a plain two-bobs-per-stride bounce stands in.
            float drop = HasLegs ? legWorld * (1f - Mathf.Cos(a * Mathf.Deg2Rad))
                                 : 0.03f * weight * (0.5f - 0.5f * Mathf.Cos(2f * phase));
            if (HasKnees)
            {
                // Foot forward is a negative X rotation for a leg hanging down. The leg swinging forward bends its knee.
                // Kneeling: the kneeling thigh a little forward of vertical and its shin lying back; the hips sit as high as
                // that knee and thigh hold them, and the front thigh comes up until its shin stands upright on its foot.
                float th = 0f, fr = 0f, sh = 0f, down = 0f;
                if (pose != null)
                {
                    float K = kneeLength, L = legLength;
                    float hip = pose.KneeHigh + K * Mathf.Cos(pose.Thigh * Mathf.Deg2Rad);
                    fr = Mathf.Acos(Mathf.Clamp((hip - (L - K)) / K, -1f, 1f)) * Mathf.Rad2Deg;
                    th = pose.Thigh;
                    sh = pose.Thigh + pose.Shin;
                    down = (L - hip) * scale;
                }
                legL.localRotation = Quaternion.Euler(Mathf.Lerp(-a, leftDown ? -th : -fr, kk), 0f, 0f);
                legR.localRotation = Quaternion.Euler(Mathf.Lerp(a, leftDown ? -fr : -th, kk), 0f, 0f);
                kneeL.localRotation = Quaternion.Euler(Mathf.Lerp(KneeBend * tuckL * tuckL, leftDown ? sh : fr, kk), 0f, 0f);
                kneeR.localRotation = Quaternion.Euler(Mathf.Lerp(KneeBend * tuckR * tuckR, leftDown ? fr : sh, kk), 0f, 0f);
                drop = Mathf.Lerp(drop, down, kk);
            }
            else if (HasLegs)
            {
                // Foot forward is a negative X rotation for a leg hanging down. The leg swinging forward tucks up.
                // Kneeling: the front leg reaches forward straight, and the back leg tucks and swings back just far enough
                // that its end (the knee) meets the ground as the hips drop to the front sole.
                float reach = pose != null ? pose.Reach : 0f, tuck = pose != null ? pose.Tuck : 1f;
                float cosF = Mathf.Cos(reach * Mathf.Deg2Rad);
                float back = Mathf.Acos(Mathf.Clamp01(cosF / tuck)) * Mathf.Rad2Deg;
                legL.localRotation = Quaternion.Euler(Mathf.Lerp(-a, leftDown ? back : -reach, kk), 0f, 0f);
                legR.localRotation = Quaternion.Euler(Mathf.Lerp(a, leftDown ? -reach : back, kk), 0f, 0f);
                legL.localScale = new Vector3(1f, Mathf.Lerp(1f - 0.2f * tuckL * tuckL, leftDown ? tuck : 1f, kk), 1f);
                legR.localScale = new Vector3(1f, Mathf.Lerp(1f - 0.2f * tuckR * tuckR, leftDown ? 1f : tuck, kk), 1f);
                drop = Mathf.Lerp(drop, legWorld * (1f - cosF), kk);
            }
            float lift = HasLegs ? 0f : 0.03f * weight - drop;
            body.localPosition = new Vector3(0f, HasLegs ? -drop : lift, 0f);

            // Lean forward with speed and into turns (smoothed so it reads as weight, not a snap).
            float k = 1f - Mathf.Exp(-dt * 8f);
            lean = Mathf.Lerp(lean, Mathf.Clamp01(speed / FullStrideSpeed) * 8f * weight, k);
            roll = Mathf.Lerp(roll, Mathf.Clamp(-turn * Mathf.Min(speed, 1.5f) * 0.03f, -7f, 7f), k);

            float t = Time.time;
            float idle = (1f - weight) * (1f - (pose != null ? pose.Still * kk : 0f));   // kneeling, it holds still
            // Walking: sway over the planted foot once per stride, shoulders counter-twist. Idle: a slow weight shift.
            float sway = -c * 0.012f * weight + Mathf.Sin(t * 2.0944f + seed) * 0.008f * idle;  // 3 s idle period
            float breathe = Mathf.Sin(t * 2.6f + seed) * idle;
            var upper = Quaternion.Euler(lean + breathe * 0.8f + (pose != null ? pose.Lean * kk : 0f), -s * 6f * weight, roll + c * 3f * weight - sway * 120f * idle);
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
            // Candy troopers (build_models.candy_trooper) come rigged: hips over two legs hung from the root at the hip
            // joints, soles on the ground. Drive those as they are.
            var rigHips = PezMotion.FindDeep(root, "hips");
            var rigL = PezMotion.FindDeep(root, "leg_l");
            var rigR = PezMotion.FindDeep(root, "leg_r");
            if (rigHips != null && rigL != null && rigR != null && rigL.parent == root && rigR.parent == root)
                return new Gait(body, rigHips, rigL, rigR, rigL.localPosition.y, root.localScale.y, seed,
                                PezMotion.FindDeep(rigL, "knee_l"), PezMotion.FindDeep(rigR, "knee_r")); // knees, if it has them
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
            // Carry the bevel data and tangents (Models.BevelData) so Pez/Model shades the pieces like the rest.
            var tan = src.tangents; bool hasTan = tan.Length == v.Length;
            var nrm = src.normals; bool hasNrm = nrm.Length == v.Length; // smoothed by angle (Models.SmoothByAngle): keep them
            var outNrm = new List<Vector3>();
            var extra = new List<Vector4>[3];
            for (int k = 0; k < 3; k++) { extra[k] = new List<Vector4>(); src.GetUVs(3 + k, extra[k]); if (extra[k].Count != v.Length) extra[k] = null; }
            var outTan = new List<Vector4>();
            var outExtra = new[] { new List<Vector4>(), new List<Vector4>(), new List<Vector4>() };
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
                    if (hasTan) outTan.Add(tan[o]);
                    if (hasNrm) outNrm.Add(nrm[o]);
                    for (int k = 0; k < 3; k++) if (extra[k] != null) outExtra[k].Add(extra[k][o]);
                }
                outTris[i] = n;
            }
            var m = new Mesh { name = src.name + suffix };
            m.SetVertices(verts);
            if (hasUv) m.SetUVs(0, uvs);
            if (hasCol) m.SetColors(cols);
            if (hasTan) m.SetTangents(outTan);
            for (int k = 0; k < 3; k++) if (extra[k] != null) m.SetUVs(3 + k, outExtra[k]);
            m.SetTriangles(outTris, 0);
            if (hasNrm) m.SetNormals(outNrm); else m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
