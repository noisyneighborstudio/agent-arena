using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Structure foundations. Every multi-tile art-pack structure stands on a flat 0.08 pad (stage_0__M_SugarPad, inside
    /// its footprint by the pack's 0.04 margin). Apply turns that pad into a concrete plinth: 0.12 high with a chamfered
    /// top edge, and a ramp down to the ground in front of each roll-up door (factory, barracks, command center, and
    /// the refinery dock, whose kraft dock floor becomes its ramp). The plinth replaces the pad's mesh on the same node,
    /// so it rises with stage_0 during construction like the pad did, and footprints, pivots and nodes are unchanged.
    /// The airfield keeps its pad's height (its aircraft lift sits flush in it). Doors face -Z (south), as in the pack.
    ///
    /// Units are 2D in the sim; HeightAt gives the visual ground height on a plinth or its ramp, so units leaving a
    /// producer (or a truck reversing into the refinery dock) roll down the ramp instead of clipping through the slab.
    /// </summary>
    public static class Plinths
    {
        public const float Height = 0.12f, Chamfer = 0.035f;

        public struct Ramp { public float X0, X1, Length; }
        public class Spec { public float Size, Top; public Ramp[] Ramps; }

        static readonly Dictionary<Mesh, (Mesh mesh, Spec spec, bool lane)> cache = new Dictionary<Mesh, (Mesh, Spec, bool)>();

        /// <summary>Rebuild a structure model's pad as a plinth with driveway ramps. Returns its shape, or null if it has no pad.</summary>
        public static Spec Apply(GameObject model, string key)
        {
            var padT = PezMotion.FindDeep(model.transform, "stage_0__M_SugarPad");
            var padMf = padT != null ? padT.GetComponent<MeshFilter>() : null;
            var padR = padT != null ? padT.GetComponent<MeshRenderer>() : null;
            if (padMf == null || padR == null || padMf.sharedMesh == null) return null;
            var offset = (model.transform.worldToLocalMatrix * padT.localToWorldMatrix).GetColumn(3);
            if (new Vector3(offset.x, offset.y, offset.z).sqrMagnitude > 1e-6f) return null; // pads sit at the model origin

            // The refinery's kraft dock floor (stage_0__M_Kraft) is the truck lane: it becomes the ramp's surface.
            var laneT = PezMotion.FindDeep(model.transform, "stage_0__M_Kraft");
            var laneR = laneT != null ? laneT.GetComponent<MeshRenderer>() : null;

            var src = padMf.sharedMesh;
            if (!cache.TryGetValue(src, out var built))
            {
                var b = src.bounds;
                float size = Mathf.Min(b.size.x, b.size.z);
                var spec = new Spec { Size = size, Top = key == "airfield" ? b.max.y : Height, Ramps = DoorRamps(model, size) };
                bool lane = laneR != null && spec.Ramps.Length > 0 && LaneOverRamp(model, laneT, spec);
                built = (Build(spec), spec, lane);
                built.mesh.name = src.name + "_plinth";
                cache[src] = built;
            }
            padMf.sharedMesh = built.mesh;
            var padMat = padR.sharedMaterial;
            var rampMat = built.lane ? laneR.sharedMaterial : padMat;
            padR.sharedMaterials = new[] { padMat, rampMat };
            if (built.lane) laneR.enabled = false; // the ramp is the dock floor now
            return built.spec;
        }

        /// <summary>A ramp in front of each roll-up door: as wide as the door plus a margin, from the plinth's south edge to the door.</summary>
        static Ramp[] DoorRamps(GameObject model, float size)
        {
            var door = PezMotion.FindDeep(model.transform, "door");
            if (door == null) return new Ramp[0];
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = -lo;
            foreach (var mf in door.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var m = model.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var bb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(bb.center + Vector3.Scale(bb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                }
            }
            if (lo.x > hi.x) return new Ramp[0];
            float hs = size * 0.5f, edge = hs - Chamfer - 0.06f;
            float length = Mathf.Clamp((lo.z + hi.z) * 0.5f + hs, 0.18f, size * 0.45f);
            float x0 = Mathf.Max(lo.x - 0.05f, -edge), x1 = Mathf.Min(hi.x + 0.05f, edge);
            return x1 - x0 > 0.1f ? new[] { new Ramp { X0 = x0, X1 = x1, Length = length } } : new Ramp[0];
        }

        static bool LaneOverRamp(GameObject model, Transform lane, Spec spec)
        {
            var mf = lane.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;
            var c = (model.transform.worldToLocalMatrix * lane.localToWorldMatrix).MultiplyPoint3x4(mf.sharedMesh.bounds.center);
            foreach (var r in spec.Ramps) if (c.x > r.X0 && c.x < r.X1 && c.z < -spec.Size * 0.5f + r.Length + 0.2f) return true;
            return false;
        }

        // ---------------------------------------------------------------- geometry

        static Mesh Build(Spec s)
        {
            var verts = new List<Vector3>();
            var tris = new[] { new List<int>(), new List<int>() }; // 0: plinth, 1: ramp surfaces
            float hs = s.Size * 0.5f, H = s.Top, c = Mathf.Min(Chamfer, H * 0.45f), inner = hs - c;

            void Poly(int sub, Vector3 normal, params Vector3[] p)
            {
                for (int i = 1; i + 1 < p.Length; i++)
                {
                    Vector3 a = p[0], b = p[i], d = p[i + 1];
                    if (Vector3.Dot(Vector3.Cross(b - a, d - a), normal) < 0) (b, d) = (d, b); // Unity winding: outward
                    int n = verts.Count;
                    verts.Add(a); verts.Add(b); verts.Add(d);
                    tris[sub].Add(n); tris[sub].Add(n + 1); tris[sub].Add(n + 2);
                }
            }

            // Top: a grid over the inset rectangle, minus each ramp's notch.
            var xs = new List<float> { -inner, inner };
            var zs = new List<float> { -inner, inner };
            foreach (var r in s.Ramps) { xs.Add(r.X0); xs.Add(r.X1); if (-hs + r.Length > -inner) zs.Add(-hs + r.Length); }
            xs.Sort(); zs.Sort();
            bool InNotch(float x, float z) { foreach (var r in s.Ramps) if (x > r.X0 && x < r.X1 && z < -hs + r.Length) return true; return false; }
            for (int i = 0; i + 1 < xs.Count; i++)
                for (int j = 0; j + 1 < zs.Count; j++)
                {
                    float x0 = xs[i], x1 = xs[i + 1], z0 = zs[j], z1 = zs[j + 1];
                    if (x1 - x0 < 1e-4f || z1 - z0 < 1e-4f || InNotch((x0 + x1) * 0.5f, (z0 + z1) * 0.5f)) continue;
                    Poly(0, Vector3.up, new Vector3(x0, H, z0), new Vector3(x1, H, z0), new Vector3(x1, H, z1), new Vector3(x0, H, z1));
                }

            // Chamfer strips and walls on the north, east and west sides.
            Poly(0, new Vector3(0, 1, 1), new Vector3(-hs, H - c, hs), new Vector3(hs, H - c, hs), new Vector3(inner, H, inner), new Vector3(-inner, H, inner));
            Poly(0, new Vector3(1, 1, 0), new Vector3(hs, H - c, -hs), new Vector3(hs, H - c, hs), new Vector3(inner, H, inner), new Vector3(inner, H, -inner));
            Poly(0, new Vector3(-1, 1, 0), new Vector3(-hs, H - c, -hs), new Vector3(-hs, H - c, hs), new Vector3(-inner, H, inner), new Vector3(-inner, H, -inner));
            Poly(0, Vector3.forward, new Vector3(-hs, 0, hs), new Vector3(hs, 0, hs), new Vector3(hs, H - c, hs), new Vector3(-hs, H - c, hs));
            Poly(0, Vector3.right, new Vector3(hs, 0, -hs), new Vector3(hs, 0, hs), new Vector3(hs, H - c, hs), new Vector3(hs, H - c, -hs));
            Poly(0, Vector3.left, new Vector3(-hs, 0, -hs), new Vector3(-hs, 0, hs), new Vector3(-hs, H - c, hs), new Vector3(-hs, H - c, -hs));

            // South side: chamfer and wall in segments between the ramps; each ramp with its surface and two cheeks.
            var cuts = new List<(float a, float b)>();
            float from = -hs;
            var ramps = new List<Ramp>(s.Ramps);
            ramps.Sort((p, q) => p.X0.CompareTo(q.X0));
            foreach (var r in ramps) { cuts.Add((from, r.X0)); from = r.X1; }
            cuts.Add((from, hs));
            foreach (var (a, b) in cuts)
            {
                if (b - a < 1e-4f) continue;
                float ai = a <= -hs + 1e-4f ? -inner : a, bi = b >= hs - 1e-4f ? inner : b;
                Poly(0, new Vector3(0, 1, -1), new Vector3(a, H - c, -hs), new Vector3(b, H - c, -hs), new Vector3(bi, H, -inner), new Vector3(ai, H, -inner));
                Poly(0, Vector3.back, new Vector3(a, 0, -hs), new Vector3(b, 0, -hs), new Vector3(b, H - c, -hs), new Vector3(a, H - c, -hs));
            }
            foreach (var r in ramps)
            {
                float zt = -hs + r.Length;
                Poly(1, new Vector3(0, r.Length, -H), new Vector3(r.X0, 0, -hs), new Vector3(r.X1, 0, -hs), new Vector3(r.X1, H, zt), new Vector3(r.X0, H, zt));
                Poly(0, Vector3.right, new Vector3(r.X0, 0, -hs), new Vector3(r.X0, H - c, -hs), new Vector3(r.X0, H, -inner), new Vector3(r.X0, H, zt));
                Poly(0, Vector3.left, new Vector3(r.X1, 0, -hs), new Vector3(r.X1, H - c, -hs), new Vector3(r.X1, H, -inner), new Vector3(r.X1, H, zt));
            }

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(tris[0], 0);
            mesh.SetTriangles(tris[1], 1);
            return Models.FlatMesh(mesh); // flat normals and the chamfer shading data, like every art-pack mesh
        }

        // ---------------------------------------------------------------- unit heights

        static readonly Dictionary<int, (Vector3 center, Spec spec)> placed = new Dictionary<int, (Vector3, Spec)>();
        static readonly Dictionary<long, int> byTile = new Dictionary<long, int>();
        static long Tile(int x, int z) => ((long)x << 32) ^ (uint)z;

        public static void Register(int id, Vector3 center, Spec spec)
        {
            if (spec == null) return;
            Unregister(id);
            placed[id] = (center, spec);
            float hs = spec.Size * 0.5f;
            for (int x = Mathf.FloorToInt(center.x - hs); x <= Mathf.FloorToInt(center.x + hs); x++)
                for (int z = Mathf.FloorToInt(center.z - hs); z <= Mathf.FloorToInt(center.z + hs); z++)
                    byTile[Tile(x, z)] = id;
        }

        public static void Unregister(int id)
        {
            if (!placed.TryGetValue(id, out var p)) return;
            placed.Remove(id);
            float hs = p.spec.Size * 0.5f;
            for (int x = Mathf.FloorToInt(p.center.x - hs); x <= Mathf.FloorToInt(p.center.x + hs); x++)
                for (int z = Mathf.FloorToInt(p.center.z - hs); z <= Mathf.FloorToInt(p.center.z + hs); z++)
                    if (byTile.TryGetValue(Tile(x, z), out var o) && o == id) byTile.Remove(Tile(x, z));
        }

        public static void Clear() { placed.Clear(); byTile.Clear(); }

        /// <summary>Visual ground height at a world point: a plinth's top, partway down a ramp, or 0.</summary>
        public static float HeightAt(Vector3 p)
        {
            if (byTile.Count == 0 || !byTile.TryGetValue(Tile(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z)), out var id)) return 0f;
            var (center, s) = placed[id];
            float hs = s.Size * 0.5f, lx = p.x - center.x, lz = p.z - center.z;
            if (lx < -hs || lx > hs || lz < -hs || lz > hs) return 0f;
            foreach (var r in s.Ramps)
                if (lx > r.X0 && lx < r.X1 && lz < -hs + r.Length) return s.Top * Mathf.Clamp01((lz + hs) / r.Length);
            return s.Top;
        }
    }
}
