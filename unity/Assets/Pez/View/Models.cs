using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>Handles to the moving parts of a procedural model.</summary>
    public class Rig
    {
        public Transform Root, Body, Turret, Barrel, Spinner, Bin;
        public float Altitude;
        // Art-pack models (glTF from the v0.2 handoff) are driven by the pack's own components.
        public GameObject Model;
        public PezMotion Motion;
        public PezEmerge Emerge;
        public bool HasModel => Model != null;
        public Vector3 BarrelRest;
        /// <summary>Art-pack models: the barrel's far end (the middle of its +Z face) in barrel space. Shots, flashes and
        /// tracers start here.</summary>
        public Vector3 MuzzleLocal;
        public bool HasMuzzle;
        /// <summary>Twin barrels (heavy tank): each barrel's muzzle in barrel space; shots alternate L, R.</summary>
        public Vector3 MuzzleL, MuzzleR;
        public bool Twin;
        public Gait Gait;            // infantry walk cycle (legs, hips) or null
        public Plinths.Spec Plinth;  // a structure's foundation (its pad turned into a plinth with driveway ramps) or null
    }

    /// <summary>
    /// Procedural models built from primitives so the MVP needs no imported art.
    /// Everything faces +Z; one tile is one world unit. Swap these for real meshes later
    /// by returning the same Rig handles.
    /// </summary>
    public static class Models
    {
        // Placeholder materials follow the art pack's neutral palette: the world stays unflavoured, and the four
        // team hues (plus hazard yellow and red) are never used outside the team mask.
        static readonly Color Steel = PezPalette.MaterialsSpringSteel;
        static readonly Color DarkSteel = PezPalette.MaterialsSmokePlastic;
        static readonly Color Concrete = PezPalette.MaterialsSugarPad;
        static readonly Color Track = PezPalette.MaterialsLicorice;
        static readonly Color Skin = PezPalette.MaterialsCreamPlastic;
        static readonly Color Kraft = PezPalette.MaterialsKraft;

        public static Transform Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.Destroy(go.GetComponent<Collider>());
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            t.localScale = scale;
            t.localEulerAngles = euler;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return t;
        }

        static Transform Empty(Transform parent, string name, Vector3 pos = default)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            return t;
        }

        static readonly Dictionary<string, GameObject> modelCache = new Dictionary<string, GameObject>();
        static readonly Dictionary<(Material, int), Material> tinted = new Dictionary<(Material, int), Material>();
        static readonly Dictionary<string, float> Altitudes = new Dictionary<string, float> { { "gunship", 2.4f }, { "stealth_bomber", 3.2f } };

        /// <summary>The art pack's model for a key (Resources/PezModels/<key>.glb), or null if there isn't one yet.</summary>
        public static GameObject ModelFor(string key)
        {
            if (!modelCache.TryGetValue(key, out var m)) modelCache[key] = m = Resources.Load<GameObject>("PezModels/" + key);
            return m;
        }

        static Mesh dashedRing;
        static int gaitSeed;

        /// <summary>
        /// The HUD kit's selection ring: a flat dashed circle (diameter 1, in XZ), drawn in cream for every team.
        /// </summary>
        public static Transform SelectionRing(Transform parent, Material mat)
        {
            if (dashedRing == null)
            {
                const int dashes = 20, steps = 4;
                const float outer = 0.5f, inner = 0.44f, fill = 0.6f; // each dash covers 60% of its slot
                var verts = new List<Vector3>();
                var tris = new List<int>();
                for (int d = 0; d < dashes; d++)
                {
                    float a0 = d * Mathf.PI * 2f / dashes, a1 = a0 + Mathf.PI * 2f / dashes * fill;
                    int start = verts.Count;
                    for (int k = 0; k <= steps; k++)
                    {
                        float a = Mathf.Lerp(a0, a1, k / (float)steps);
                        var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                        verts.Add(dir * outer);
                        verts.Add(dir * inner);
                    }
                    for (int k = 0; k < steps; k++)
                    {
                        int o = start + k * 2;
                        tris.AddRange(new[] { o, o + 1, o + 2, o + 2, o + 1, o + 3 });
                    }
                }
                dashedRing = new Mesh { name = "selection_ring" };
                dashedRing.SetVertices(verts);
                var white = new Color[verts.Count];
                for (int i = 0; i < white.Length; i++) white[i] = Color.white;
                dashedRing.colors = white;
                dashedRing.SetTriangles(tris, 0);
                dashedRing.RecalculateBounds();
            }
            var go = new GameObject("ring", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = dashedRing;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static readonly Dictionary<Mesh, Mesh> flatMeshes = new Dictionary<Mesh, Mesh>();

        /// <summary>
        /// The art pack's .glb files carry no normals and share vertices between faces. glTFast fills the gap with
        /// Mesh.RecalculateNormals, which smooths across every corner, so boxes shade like pillows. The handoff's
        /// renders (three.js) flat-shade normal-less meshes: unweld each mesh once and recompute, so every face is flat.
        /// </summary>
        /// Also swaps the instance's glTF materials for the tuned Pez/Model PBR ones (Look.Convert), and stores the
        /// bevel data Pez/Model shades its chamfered edges from (see BevelData).
        public static void FlatShade(GameObject go)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var src = mf.sharedMesh;
                if (src == null) continue;
                if (!flatMeshes.TryGetValue(src, out var flat)) flatMeshes[src] = flat = Unweld(src);
                mf.sharedMesh = flat;
            }
            Look.Convert(go);
        }

        /// <summary>
        /// Per triangle corner, the bevel Pez/Model shades (a "shading chamfer": the edges of every convex crease get a
        /// narrow band with the chamfer's normal, so they catch a highlight line like a real 45-degree chamfer; geometry,
        /// silhouettes, pivots and footprints are untouched). Returned per unwelded vertex:
        ///   uv3 = distance to each of the triangle's three edges (exact when interpolated; 1000 for an edge that gets
        ///         no bevel: coplanar seams such as a quad's diagonal, and concave creases), w = bevel width (0 = none);
        ///   uv4, uv5 = each edge's bevel normal in tangent space, xy only (z is positive);
        ///   tangent = a unit tangent in the face plane (w = 1), the frame those normals are expressed in.
        /// The bevel width scales with the part (connected piece) it's on: 3% of its size, 0.012 to 0.05 units.
        /// </summary>
        static (Vector4[] dist, Vector4[] bev01, Vector4[] bev2, Vector4[] tan) BevelData(Vector3[] v, List<int[]> subs)
        {
            // Weld by position (the source may split vertices by material or UV), then index edges.
            var ids = new Dictionary<Vector3Int, int>();
            int Id(Vector3 p) { var k = new Vector3Int(Mathf.RoundToInt(p.x * 2000f), Mathf.RoundToInt(p.y * 2000f), Mathf.RoundToInt(p.z * 2000f)); if (!ids.TryGetValue(k, out int i)) ids[k] = i = ids.Count; return i; }
            var tris = new List<int>();
            foreach (var t in subs) tris.AddRange(t);
            int nt = tris.Count / 3;
            var pid = new int[tris.Count];
            for (int i = 0; i < tris.Count; i++) pid[i] = Id(v[tris[i]]);
            // Connected pieces (union-find over welded vertices), for the bevel width.
            var parent = new int[ids.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            for (int t = 0; t < nt; t++) { int r0 = Find(pid[t * 3]); parent[Find(pid[t * 3 + 1])] = r0; parent[Find(pid[t * 3 + 2])] = Find(r0); }
            var lo = new Dictionary<int, Vector3>(); var hi = new Dictionary<int, Vector3>();
            for (int i = 0; i < tris.Count; i++)
            {
                int r = Find(pid[i]); var p = v[tris[i]];
                lo[r] = lo.TryGetValue(r, out var l0) ? Vector3.Min(l0, p) : p;
                hi[r] = hi.TryGetValue(r, out var h0) ? Vector3.Max(h0, p) : p;
            }
            var normals = new Vector3[nt];
            for (int t = 0; t < nt; t++)
                normals[t] = Vector3.Cross(v[tris[t * 3 + 1]] - v[tris[t * 3]], v[tris[t * 3 + 2]] - v[tris[t * 3]]).normalized; // outward (Unity winding)
            var edges = new Dictionary<long, List<int>>(); // edge key -> triangle * 3 + corner opposite the edge
            long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            for (int t = 0; t < nt; t++)
                for (int c = 0; c < 3; c++)
                {
                    long k = Key(pid[t * 3 + (c + 1) % 3], pid[t * 3 + (c + 2) % 3]);
                    if (!edges.TryGetValue(k, out var l)) edges[k] = l = new List<int>(2);
                    l.Add(t * 3 + c);
                }
            var dist = new Vector4[tris.Count]; var bev01 = new Vector4[tris.Count]; var bev2 = new Vector4[tris.Count]; var tan = new Vector4[tris.Count];
            var bevTs = new Vector2[3];
            var keep = new bool[3]; var h = new float[3];
            for (int t = 0; t < nt; t++)
            {
                var n = normals[t];
                var p0 = v[tris[t * 3]]; var p1 = v[tris[t * 3 + 1]]; var p2 = v[tris[t * 3 + 2]];
                var tg = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                var bt = Vector3.Cross(n, tg);
                float area2 = Vector3.Cross(p1 - p0, p2 - p0).magnitude;
                int root = Find(pid[t * 3]);
                float size = (hi[root] - lo[root]).magnitude;
                float width = Mathf.Clamp(size * 0.03f, 0.012f, 0.05f);
                for (int c = 0; c < 3; c++)
                {
                    int ia = t * 3 + (c + 1) % 3, ib = t * 3 + (c + 2) % 3;
                    var ea = v[tris[ia]]; var eb = v[tris[ib]];
                    float len = (eb - ea).magnitude;
                    h[c] = len > 1e-6f ? area2 / len : 0f;
                    var l = edges[Key(pid[ia], pid[ib])];
                    Vector3 bevel = Vector3.zero;
                    bool crease = false;
                    if (l.Count == 1)
                    {
                        // An open edge (a plate's rim): lean the bevel outward in the face's plane.
                        var outward = Vector3.Cross(eb - ea, n).normalized;
                        if (Vector3.Dot(outward, v[tris[t * 3 + c]] - ea) > 0) outward = -outward;
                        bevel = (n + outward).normalized; crease = true;
                    }
                    foreach (int other in l)
                    {
                        int ot = other / 3;
                        if (ot == t) continue;
                        var n2 = normals[ot];
                        if (Vector3.Dot(n, n2) > SoftEdgeCos) continue; // a soft edge (a curved surface's facets): no crease
                        // Convex when the neighbour's far corner lies behind this face.
                        if (Vector3.Dot(v[tris[other]] - ea, n) < -1e-4f) { crease = true; bevel = (n + n2).normalized; }
                    }
                    keep[c] = crease && h[c] > width * 1.5f; // skip slivers narrower than the bevel
                    bevTs[c] = keep[c] ? new Vector2(Vector3.Dot(bevel, tg), Vector3.Dot(bevel, bt)) : Vector2.zero;
                }
                bool any = keep[0] || keep[1] || keep[2];
                for (int c = 0; c < 3; c++)
                {
                    int o = t * 3 + c;
                    dist[o] = new Vector4(keep[0] ? (c == 0 ? h[0] : 0) : 1000f, keep[1] ? (c == 1 ? h[1] : 0) : 1000f, keep[2] ? (c == 2 ? h[2] : 0) : 1000f, any ? width : 0f);
                    bev01[o] = new Vector4(bevTs[0].x, bevTs[0].y, bevTs[1].x, bevTs[1].y);
                    bev2[o] = new Vector4(bevTs[2].x, bevTs[2].y, 0, 0);
                    tan[o] = new Vector4(tg.x, tg.y, tg.z, 1f);
                }
            }
            return (dist, bev01, bev2, tan);
        }

        /// <summary>A flat-shaded copy of a mesh with the chamfer shading data (for procedural meshes such as plinths).</summary>
        public static Mesh FlatMesh(Mesh src) => Unweld(src);

        static Mesh Unweld(Mesh src)
        {
            if (!src.isReadable || src.GetTopology(0) != MeshTopology.Triangles) return src;
            var v = src.vertices;
            var uv = src.uv;
            var col = src.colors;
            bool hasUv = uv.Length == v.Length, hasCol = col.Length == v.Length;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var cols = new List<Color>();
            var subs = new List<int[]>();
            var srcSubs = new List<int[]>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var tris = src.GetTriangles(s);
                srcSubs.Add(tris);
                var outTris = new int[tris.Length];
                for (int i = 0; i < tris.Length; i++)
                {
                    outTris[i] = verts.Count;
                    verts.Add(v[tris[i]]);
                    if (hasUv) uvs.Add(uv[tris[i]]);
                    if (hasCol) cols.Add(col[tris[i]]);
                }
                subs.Add(outTris);
            }
            var m = new Mesh { name = src.name + "_flat" };
            if (verts.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(verts);
            // Unwelded vertex i is source corner i (submeshes in order): the bevel data lines up one to one.
            var (dist, bev01, bev2, tan) = BevelData(v, srcSubs);
            m.SetUVs(3, dist); m.SetUVs(4, bev01); m.SetUVs(5, bev2);
            m.SetTangents(tan);
            if (hasUv) m.SetUVs(0, uvs);
            if (hasCol) m.SetColors(cols);
            m.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) m.SetTriangles(subs[s], s);
            m.SetNormals(SmoothByAngle(v, srcSubs));
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Edges between faces closer than this (cosine; about 35 degrees) are soft: curved surfaces modelled as facets
        /// (cylinders, domes, tanks) shade smooth and get no bevel line, instead of stepping in bands. Sharper edges (box
        /// corners, 45-degree chamfers) stay hard and flat-shaded, as the style wants.
        /// </summary>
        const float SoftEdgeCos = 0.82f;

        /// <summary>
        /// Per triangle corner: the face normal, averaged (area-weighted) with every face meeting at that point whose normal
        /// is within the soft-edge angle of this face's. Same corner order as the unwelded mesh.
        /// </summary>
        static Vector3[] SmoothByAngle(Vector3[] v, List<int[]> subs)
        {
            var tris = new List<int>();
            foreach (var t in subs) tris.AddRange(t);
            int nt = tris.Count / 3;
            var ids = new Dictionary<Vector3Int, int>();
            int Id(Vector3 p) { var k = new Vector3Int(Mathf.RoundToInt(p.x * 2000f), Mathf.RoundToInt(p.y * 2000f), Mathf.RoundToInt(p.z * 2000f)); if (!ids.TryGetValue(k, out int i)) ids[k] = i = ids.Count; return i; }
            var faceN = new Vector3[nt]; var faceA = new Vector3[nt]; // unit normal; area-weighted normal
            var around = new Dictionary<int, List<int>>();             // welded point -> faces touching it
            for (int t = 0; t < nt; t++)
            {
                var c = Vector3.Cross(v[tris[t * 3 + 1]] - v[tris[t * 3]], v[tris[t * 3 + 2]] - v[tris[t * 3]]);
                faceA[t] = c; faceN[t] = c.normalized;
                for (int k = 0; k < 3; k++)
                {
                    int id = Id(v[tris[t * 3 + k]]);
                    if (!around.TryGetValue(id, out var l)) around[id] = l = new List<int>(6);
                    l.Add(t);
                }
            }
            var normals = new Vector3[tris.Count];
            for (int t = 0; t < nt; t++)
                for (int k = 0; k < 3; k++)
                {
                    var sum = Vector3.zero;
                    foreach (int o in around[Id(v[tris[t * 3 + k]])])
                        if (Vector3.Dot(faceN[t], faceN[o]) > SoftEdgeCos) sum += faceA[o];
                    normals[t * 3 + k] = sum.sqrMagnitude > 1e-12f ? sum.normalized : faceN[t];
                }
            return normals;
        }

        static readonly Dictionary<(Material, int), Material> oreTinted = new Dictionary<(Material, int), Material>();
        static readonly Dictionary<Material, Material> oreSource = new Dictionary<Material, Material>(); // tinted copy -> original

        /// <summary>
        /// Recolour every ore material on a model to an ore type: M_OreTint (deep mine ore tube, deposit stake cap, art
        /// pack v0.4) and M_Ore_* (the mining truck's load, bin_ore, which ships as M_Ore_Cinnamon). Crystal and uranium
        /// glow, as they do in the field (their emission means a charged ore). Re-tinting is safe: a tinted copy maps back
        /// to its original, so copies never chain.
        /// </summary>
        public static void TintOre(GameObject go, int oreType)
        {
            if (oreType < 0 || oreType >= WorldView.OreColors.Length) return;
            var color = WorldView.OreColors[oreType];
            bool glow = oreType >= 2;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (oreSource.TryGetValue(m, out var original)) m = original;
                    if (!m.name.StartsWith("M_OreTint") && !m.name.StartsWith("M_Ore_")) continue;
                    if (!oreTinted.TryGetValue((m, oreType), out var t))
                    {
                        t = new Material(m) { name = m.name + "_ore" + oreType, enableInstancing = true };
                        t.color = color;
                        if (t.HasProperty("_BaseColor")) t.SetColor("_BaseColor", color);
                        if (t.HasProperty("baseColorFactor")) t.SetColor("baseColorFactor", color);
                        if (glow)
                        {
                            t.EnableKeyword("_EMISSION");
                            t.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                            // The field's own emissive (crystal glows laser cyan, uranium acid), not the paler albedo,
                            // so a hauled load reads as the same stuff as the field it came from. HDR: feeds the bloom.
                            Color e = oreType == 2 ? (Color)PezPalette.EmissiveCyanLaserOptics : (Color)PezPalette.EmissiveAcidUraniumElectronics;
                            if (t.HasProperty("_EmissionColor")) t.SetColor("_EmissionColor", e * 2.2f);
                            if (t.HasProperty("emissiveFactor")) t.SetColor("emissiveFactor", e * 1.4f);
                        }
                        else if (t.HasProperty("_EmissionColor")) t.SetColor("_EmissionColor", Color.black);
                        oreTinted[(m, oreType)] = t;
                        oreSource[t] = m;
                    }
                    if (mats[i] != t) { mats[i] = t; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        /// <summary>Swap every M_Team material for a copy tinted to the team's flavour.</summary>
        public static void TintTeam(GameObject go, int team)
        {
            var color = Mats.Team(team);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !m.name.StartsWith("M_Team")) continue;
                    if (!tinted.TryGetValue((m, team), out var t))
                    {
                        t = new Material(m) { name = m.name + "_t" + team, enableInstancing = true };
                        t.color = color; // baseColorFactor is the shader's [MainColor]
                        tinted[(m, team)] = t;
                    }
                    mats[i] = t;
                    changed = true;
                }
                if (changed) r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }
        }

        /// <summary>
        /// The muzzle of an art-pack barrel: the middle of the far (+Z) end of the barrel's meshes, in barrel space. Every
        /// barrel in the pack points along its node's +Z (checked per model: light 0.525, heavy 0.69, artillery 0.85 on a
        /// node pitched 60 degrees up, laser and SAM racks to their emitter faces), so the tip follows recoil and pitch.
        /// </summary>
        static bool BarrelTip(Transform barrel, out Vector3 tip)
        {
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = -lo;
            foreach (var mf in barrel.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    var p = barrel.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                }
            }
            tip = new Vector3((lo.x + hi.x) * 0.5f, (lo.y + hi.y) * 0.5f, hi.z);
            return hi.z > lo.z;
        }

        /// <summary>
        /// Split a twin-barrel mesh (two parallel barrels, one node) into barrel_l and barrel_r children by the side of
        /// x = 0 each triangle lies on, so each can recoil on its own shot. Returns each one's muzzle in barrel space.
        /// </summary>
        static bool SplitTwin(Transform barrel, out Vector3 muzzleL, out Vector3 muzzleR)
        {
            muzzleL = muzzleR = Vector3.zero;
            var mf = barrel.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) return false;
            var src = mf.sharedMesh;
            if (!twinMeshes.TryGetValue(src, out var made)) twinMeshes[src] = made = SplitMesh(src);
            if (made == null) return false;
            for (int side = 0; side < 2; side++)
            {
                var go = new GameObject(side == 0 ? "barrel_l" : "barrel_r", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(mf.transform.parent, false);
                go.transform.localPosition = mf.transform.localPosition;
                go.transform.localRotation = mf.transform.localRotation;
                go.transform.localScale = mf.transform.localScale;
                go.GetComponent<MeshFilter>().sharedMesh = made[side];
                go.GetComponent<MeshRenderer>().sharedMaterials = mf.GetComponent<MeshRenderer>().sharedMaterials;
                var b = made[side].bounds;
                var tip = barrel.InverseTransformPoint(go.transform.TransformPoint(new Vector3(b.center.x, b.center.y, b.max.z)));
                if (side == 0) muzzleL = tip; else muzzleR = tip;
            }
            Object.DestroyImmediate(mf.gameObject);
            return true;
        }

        static readonly Dictionary<Mesh, Mesh[]> twinMeshes = new Dictionary<Mesh, Mesh[]>();

        /// <summary>The two halves of a twin-barrel mesh (shared by every heavy tank), or null if it isn't one.</summary>
        static Mesh[] SplitMesh(Mesh src)
        {
            var v = src.vertices; var n = src.normals; var tg = src.tangents;
            var uv3 = new List<Vector4>(); var uv4 = new List<Vector4>(); var uv5 = new List<Vector4>();
            src.GetUVs(3, uv3); src.GetUVs(4, uv4); src.GetUVs(5, uv5);
            var tris = src.triangles;
            var made = new Mesh[2];
            for (int side = 0; side < 2; side++)
            {
                var map = new Dictionary<int, int>();
                var vi = new List<int>(); var ti = new List<int>();
                for (int t = 0; t < tris.Length; t += 3)
                {
                    float cx = (v[tris[t]].x + v[tris[t + 1]].x + v[tris[t + 2]].x) / 3f;
                    if ((cx < 0f) != (side == 0)) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        int o = tris[t + c];
                        if (!map.TryGetValue(o, out int ni)) { map[o] = ni = vi.Count; vi.Add(o); }
                        ti.Add(ni);
                    }
                }
                if (ti.Count == 0) return null;
                var m = new Mesh { name = src.name + (side == 0 ? "_l" : "_r") };
                var pv = new Vector3[vi.Count]; var pn = new Vector3[vi.Count]; var pt = new Vector4[vi.Count];
                var p3 = new Vector4[vi.Count]; var p4 = new Vector4[vi.Count]; var p5 = new Vector4[vi.Count];
                for (int i = 0; i < vi.Count; i++)
                {
                    int o = vi[i];
                    pv[i] = v[o]; if (n.Length > o) pn[i] = n[o]; if (tg.Length > o) pt[i] = tg[o];
                    if (uv3.Count > o) p3[i] = uv3[o]; if (uv4.Count > o) p4[i] = uv4[o]; if (uv5.Count > o) p5[i] = uv5[o];
                }
                m.vertices = pv; m.normals = pn; m.tangents = pt;
                m.SetUVs(3, p3); m.SetUVs(4, p4); m.SetUVs(5, p5);
                m.SetTriangles(ti, 0);
                m.RecalculateBounds();
                made[side] = m;
            }
            return made;
        }

        public static Rig Build(string key, int team)
        {
            var root = new GameObject(key).transform;
            var rig = new Rig { Root = root };
            rig.Body = Empty(root, "body");
            var prefab = ModelFor(key);
            if (prefab != null)
            {
                var go = Object.Instantiate(prefab, rig.Body, false);
                go.name = key; // PezMotion reads its profile from the object name
                FlatShade(go);
                if (Pez.Sim.Defs.Get(key)?.IsStructure == true) rig.Plinth = Plinths.Apply(go, key);
                TintTeam(go, team);
                rig.Model = go;
                rig.Turret = PezMotion.FindDeep(go.transform, "turret");
                rig.Barrel = PezMotion.FindDeep(go.transform, "barrel");
                rig.Spinner = PezMotion.FindDeep(go.transform, "spinner");
                rig.Bin = PezMotion.FindDeep(go.transform, "bin");
                // The heavy tank's twin barrels are one node in the pack: split them so they can fire one at a time.
                if (key == "heavy_tank" && rig.Barrel != null) rig.Twin = SplitTwin(rig.Barrel, out rig.MuzzleL, out rig.MuzzleR);
                // Soldiers get hips and legs cut from their body mesh before PezMotion caches the turret's rest pose.
                if (Pez.Sim.Defs.Get(key)?.Armor == Pez.Sim.Armor.Infantry) rig.Gait = Gait.FromModel(rig.Body, go, ++gaitSeed);
                rig.Motion = go.AddComponent<PezMotion>();
                // Visual turrets keep up with the sim's aim so shots leave the barrel, not the side of it.
                if (rig.Motion.profile.turretYawSpeed > 0) rig.Motion.profile.turretYawSpeed = Mathf.Max(rig.Motion.profile.turretYawSpeed, 240f);
                // Idle scan per class (MOTION.md): heavies sweep +-25 deg, artillery holds still.
                if (key == "heavy_tank") rig.Motion.scanAmp = 25f;
                if (key == "artillery") rig.Motion.idleScan = false;
                rig.Emerge = go.AddComponent<PezEmerge>();
                if (Altitudes.TryGetValue(key, out var alt)) rig.Altitude = alt;
                if (rig.Barrel != null) { rig.BarrelRest = rig.Barrel.localPosition; rig.HasMuzzle = BarrelTip(rig.Barrel, out rig.MuzzleLocal); }
                return rig;
            }
            var tc = Mats.Team(team);
            var teamMat = Mats.Lit(tc, 0.45f, 0.25f);
            var teamDark = Mats.Lit(tc * 0.55f, 0.35f, 0.2f);
            var steel = Mats.Lit(Steel, 0.5f, 0.6f);
            var dark = Mats.Lit(DarkSteel, 0.3f, 0.5f);
            var concrete = Mats.Lit(Concrete, 0.1f, 0f);
            var track = Mats.Lit(Track, 0.1f, 0.2f);
            var b = rig.Body;
            switch (key)
            {
                case "light_tank": Tank(rig, teamMat, teamDark, steel, track, 1f, false); break;
                case "heavy_tank": Tank(rig, teamMat, teamDark, steel, track, 1.3f, true); break;
                case "mining_truck":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.3f, 0.14f, 0), new Vector3(0.18f, 0.28f, 1.05f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.3f, 0.14f, 0), new Vector3(0.18f, 0.28f, 1.05f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.38f, -0.1f), new Vector3(0.62f, 0.32f, 0.8f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.62f, 0.25f), new Vector3(0.36f, 0.24f, 0.25f), Mats.Lit(new Color(0.2f, 0.3f, 0.4f), 0.9f, 0.4f));
                    rig.Bin = Part(b, PrimitiveType.Cube, new Vector3(0, 0.58f, -0.25f), new Vector3(0.5f, 0.12f, 0.45f), Mats.Glow(new Color(0.95f, 0.7f, 0.15f), 0.8f));
                    rig.Spinner = Empty(b, "cutter", new Vector3(0, 0.2f, 0.55f));
                    Part(rig.Spinner, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.24f, 0.33f, 0.24f), steel, new Vector3(0, 0, 90));
                    for (int i = 0; i < 4; i++)
                        Part(rig.Spinner, PrimitiveType.Cube, Vector3.zero, new Vector3(0.6f, 0.05f, 0.32f), dark, new Vector3(i * 45, 0, 0));
                    rig.Turret = rig.Spinner;
                    break;
                case "rifleman":
                case "rocket_soldier":
                case "laser_trooper":
                case "medic":
                case "engineer":
                case "sniper":
                case "commando":
                    {
                        float s = 0.85f;
                        // Smoke-plastic torso on licorice legs; the team colour sits on the helmet, on top, where it reads at full zoom-out.
                        Part(b, PrimitiveType.Capsule, new Vector3(0, 0.3f * s, 0), new Vector3(0.16f, 0.13f, 0.12f) * s, Mats.Lit(DarkSteel, 0.5f, 0));
                        Part(b, PrimitiveType.Sphere, new Vector3(0, 0.5f * s, 0), Vector3.one * 0.1f * s, Mats.Lit(Skin, 0.6f, 0));
                        Part(b, PrimitiveType.Sphere, new Vector3(0, 0.53f * s, -0.005f), new Vector3(0.11f, 0.06f, 0.11f) * s, teamMat);
                        rig.Turret = Empty(b, "arms", new Vector3(0, 0.32f * s, 0));
                        if (key == "rifleman")
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.06f, 0, 0.12f), new Vector3(0.03f, 0.04f, 0.26f), dark);
                        else if (key == "engineer")
                        {
                            // Hard hat and toolbox.
                            Part(b, PrimitiveType.Sphere, new Vector3(0, 0.55f * s, 0), new Vector3(0.14f, 0.07f, 0.14f) * s, teamMat);
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.1f, -0.1f, 0.03f), new Vector3(0.07f, 0.06f, 0.12f), Mats.Lit(Kraft, 0.2f, 0f));
                        }
                        else if (key == "sniper")
                        {
                            // Ghillie-dark body overlay and a long scoped rifle.
                            Part(b, PrimitiveType.Capsule, new Vector3(0, 0.3f * s, 0), new Vector3(0.17f, 0.135f, 0.13f) * s, Mats.Lit(Kraft * 0.6f, 0.05f, 0));
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.06f, 0, 0.2f), new Vector3(0.025f, 0.035f, 0.46f), dark);
                            Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(0, 1.4f, 0.05f), new Vector3(0.9f, 0.12f, 0.9f), Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 1.5f), new Vector3(90, 0, 0));
                        }
                        else if (key == "commando")
                        {
                            // Beret and a satchel of charges.
                            Part(b, PrimitiveType.Sphere, new Vector3(0.02f, 0.55f * s, 0), new Vector3(0.13f, 0.04f, 0.13f) * s, Mats.Lit(Track, 0.4f, 0));
                            Part(b, PrimitiveType.Cube, new Vector3(0, 0.26f * s, -0.09f), new Vector3(0.14f, 0.12f, 0.07f) * s, Mats.Lit(Kraft, 0.2f, 0));
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.08f, 0, 0.08f), new Vector3(0.06f, 0.05f, 0.08f), Mats.Glow(PezPalette.EmissiveAmberIndustryDocking, 2f));
                        }
                        else if (key == "medic")
                        {
                            // White pack with a red cross on the back, no weapon.
                            // White pack with a team-coloured plus (never a red cross: it would read as Cherry).
                            Part(b, PrimitiveType.Cube, new Vector3(0, 0.27f * s, -0.08f), new Vector3(0.14f, 0.16f, 0.07f) * s, Mats.Lit(PezPalette.MaterialsBone, 0.3f, 0));
                            Part(b, PrimitiveType.Cube, new Vector3(0, 0.27f * s, -0.12f), new Vector3(0.1f, 0.03f, 0.01f) * s, teamMat);
                            Part(b, PrimitiveType.Cube, new Vector3(0, 0.27f * s, -0.12f), new Vector3(0.03f, 0.1f, 0.01f) * s, teamMat);
                        }
                        else if (key == "laser_trooper")
                        {
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.06f, 0, 0.13f), new Vector3(0.045f, 0.05f, 0.28f), Mats.Lit(new Color(0.85f, 0.88f, 0.9f), 0.8f, 0.6f));
                            Part(rig.Barrel, PrimitiveType.Cube, new Vector3(0, 0.6f, 0), new Vector3(0.6f, 0.3f, 0.9f), Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 3f));
                        }
                        else
                            rig.Barrel = Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0.08f, 0.07f, 0.02f), new Vector3(0.07f, 0.17f, 0.07f), Mats.Lit(Kraft, 0.3f, 0f), new Vector3(90, 0, 0));
                        Legs(rig, s, key == "sniper" ? Mats.Lit(Kraft * 0.45f, 0.05f, 0) : Mats.Lit(Track, 0.2f, 0));
                        break;
                    }
                case "command_center":
                    Pad(b, 3, 3, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(-0.35f, 0.45f, 0.3f), new Vector3(1.6f, 0.7f, 1.5f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(-0.35f, 0.85f, 0.3f), new Vector3(1.4f, 0.1f, 1.3f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(0.75f, 0.35f, -0.6f), new Vector3(0.9f, 0.6f, 0.9f), teamDark);
                    rig.Turret = Empty(b, "crane", new Vector3(0.9f, 0.1f, 0.9f));
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0.9f, 0), new Vector3(0.12f, 1.8f, 0.12f), Mats.Lit(new Color(0.95f, 0.75f, 0.1f), 0.4f, 0.3f));
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 1.75f, -0.6f), new Vector3(0.1f, 0.1f, 1.5f), Mats.Lit(new Color(0.95f, 0.75f, 0.1f), 0.4f, 0.3f));
                    Part(b, PrimitiveType.Cube, new Vector3(-0.35f, 0.5f, -0.46f), new Vector3(1.2f, 0.4f, 0.05f), Mats.Glow(new Color(0.6f, 0.85f, 1f), 1.2f));
                    break;
                case "power_plant":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, 0.35f), new Vector3(1.6f, 0.4f, 0.8f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(-0.4f, 0.6f, -0.35f), new Vector3(0.6f, 0.55f, 0.6f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.4f, 0.6f, -0.35f), new Vector3(0.6f, 0.55f, 0.6f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(-0.4f, 1.12f, -0.35f), new Vector3(0.5f, 0.02f, 0.5f), Mats.Glow(new Color(0.3f, 0.9f, 1f), 2.5f));
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.4f, 1.12f, -0.35f), new Vector3(0.5f, 0.02f, 0.5f), Mats.Glow(new Color(0.3f, 0.9f, 1f), 2.5f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.48f, 0.35f), new Vector3(1.62f, 0.06f, 0.82f), teamMat);
                    break;
                case "mining_refinery":
                    Pad(b, 3, 3, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0.2f, 0.5f, 0.55f), new Vector3(2.2f, 0.9f, 1.4f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0.2f, 0.98f, 0.55f), new Vector3(2.2f, 0.08f, 1.4f), teamMat);
                    for (int i = 0; i < 2; i++)
                    {
                        Part(b, PrimitiveType.Cylinder, new Vector3(-0.9f + i * 0.7f, 0.9f, 0.9f), new Vector3(0.5f, 0.75f, 0.5f), Mats.Lit(new Color(0.75f, 0.72f, 0.6f), 0.5f, 0.6f));
                        Part(b, PrimitiveType.Sphere, new Vector3(-0.9f + i * 0.7f, 1.65f, 0.9f), new Vector3(0.5f, 0.25f, 0.5f), teamDark);
                    }
                    // Dock pad where harvesters unload (south edge).
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.06f, -1.0f), new Vector3(1.2f, 0.06f, 0.9f), Mats.Lit(new Color(0.3f, 0.3f, 0.28f), 0.1f, 0f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.1f, -1.0f), new Vector3(1.0f, 0.02f, 0.08f), Mats.Glow(new Color(1f, 0.75f, 0.2f), 1.5f));
                    break;
                case "barracks":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.35f, 0.15f), new Vector3(1.6f, 0.6f, 1.2f), Mats.Lit(new Color(0.42f, 0.4f, 0.3f), 0.1f, 0f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.72f, 0.15f), new Vector3(1.7f, 0.12f, 1.3f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, -0.47f), new Vector3(0.4f, 0.45f, 0.05f), dark);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.7f, 1.0f, 0.6f), new Vector3(0.03f, 0.6f, 0.03f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0.85f, 1.45f, 0.6f), new Vector3(0.3f, 0.18f, 0.02f), Mats.Glow(tc, 0.8f));
                    break;
                case "factory":
                    Pad(b, 3, 3, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.6f, 0.2f), new Vector3(2.6f, 1.1f, 2.2f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 1.15f, 0.2f), new Vector3(2.4f, 1.1f, 0.9f), teamDark, new Vector3(0, 0, 90));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.45f, -0.92f), new Vector3(1.4f, 0.8f, 0.06f), Mats.Lit(new Color(0.12f, 0.12f, 0.12f), 0.4f, 0.6f));
                    for (int i = 0; i < 5; i++)
                        Part(b, PrimitiveType.Cube, new Vector3(-0.56f + i * 0.28f, 0.45f, -0.95f), new Vector3(0.12f, 0.8f, 0.02f), Mats.Lit(new Color(0.95f, 0.75f, 0.1f), 0.4f, 0.3f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 1.2f, -0.9f), new Vector3(2.0f, 0.12f, 0.04f), Mats.Glow(tc, 1.4f));
                    break;
                case "radar_dome":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, 0.1f), new Vector3(1.5f, 0.4f, 1.4f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.47f, 0.1f), new Vector3(1.52f, 0.05f, 1.42f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(-0.25f, 0.6f, 0.2f), new Vector3(0.9f, 0.8f, 0.9f), Mats.Lit(new Color(0.88f, 0.88f, 0.85f), 0.6f, 0.1f));
                    rig.Turret = Empty(b, "dish", new Vector3(0.45f, 0.5f, -0.35f));
                    Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.25f, 0), new Vector3(0.05f, 0.25f, 0.05f), dark);
                    Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.55f, 0.05f), new Vector3(0.6f, 0.03f, 0.6f), Mats.Lit(new Color(0.8f, 0.8f, 0.8f), 0.7f, 0.5f), new Vector3(70, 0, 0));
                    Part(rig.Turret, PrimitiveType.Sphere, new Vector3(0, 0.6f, 0.2f), Vector3.one * 0.07f, Mats.Glow(new Color(1f, 0.2f, 0.15f), 3f));
                    break;
                case "gun_turret":
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.15f, 0), new Vector3(0.85f, 0.15f, 0.85f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.32f, 0), new Vector3(0.6f, 0.04f, 0.6f), teamMat);
                    rig.Turret = Empty(b, "turret", new Vector3(0, 0.45f, 0));
                    Part(rig.Turret, PrimitiveType.Sphere, Vector3.zero, new Vector3(0.55f, 0.4f, 0.55f), steel);
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.03f, 0.4f), new Vector3(0.09f, 0.3f, 0.09f), dark, new Vector3(90, 0, 0));
                    break;

                // ---- New economy structures
                case "outpost":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0.1f), new Vector3(1.3f, 0.5f, 1.2f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.57f, 0.1f), new Vector3(1.32f, 0.05f, 1.22f), teamMat);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.45f, 1.0f, 0.45f), new Vector3(0.04f, 0.45f, 0.04f), dark);
                    Part(b, PrimitiveType.Sphere, new Vector3(0.45f, 1.48f, 0.45f), Vector3.one * 0.09f, Mats.Glow(tc, 3f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.08f, -0.8f), new Vector3(0.9f, 0.04f, 0.04f), Mats.Glow(new Color(1f, 0.75f, 0.2f), 1.5f));
                    break;
                case "electronics_plant":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.35f, 0.1f), new Vector3(1.6f, 0.6f, 1.3f), Mats.Lit(new Color(0.75f, 0.77f, 0.8f), 0.6f, 0.3f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.66f, 0.1f), new Vector3(1.62f, 0.04f, 1.32f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.38f, -0.56f), new Vector3(1.3f, 0.3f, 0.03f), Mats.Glow(new Color(0.2f, 1f, 0.45f), 1.8f));
                    for (int i = 0; i < 3; i++) Part(b, PrimitiveType.Cylinder, new Vector3(-0.5f + i * 0.5f, 0.85f, 0.45f), new Vector3(0.14f, 0.2f, 0.14f), dark);
                    break;
                case "optics_lab":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, 0), new Vector3(1.6f, 0.4f, 1.6f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.46f, 0), new Vector3(1.62f, 0.04f, 1.62f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(0, 0.5f, 0), new Vector3(1.1f, 0.9f, 1.1f), Mats.Glow(new Color(0.3f, 0.85f, 1f), 0.9f));
                    rig.Turret = Empty(b, "prism", new Vector3(0, 1.05f, 0));
                    Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.22f, 0.4f, 0.22f), Mats.Glow(new Color(0.6f, 0.95f, 1f), 3f), new Vector3(45, 0, 45));
                    break;
                case "enrichment_plant":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0.35f), new Vector3(1.6f, 0.5f, 0.8f), dark);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.56f, 0.35f), new Vector3(1.62f, 0.04f, 0.82f), teamMat);
                    for (int i = 0; i < 3; i++)
                    {
                        Part(b, PrimitiveType.Cylinder, new Vector3(-0.5f + i * 0.5f, 0.55f, -0.35f), new Vector3(0.36f, 0.5f, 0.36f), Mats.Lit(new Color(0.3f, 0.32f, 0.3f), 0.6f, 0.7f));
                        Part(b, PrimitiveType.Cylinder, new Vector3(-0.5f + i * 0.5f, 0.55f, -0.35f), new Vector3(0.38f, 0.08f, 0.38f), Mats.Glow(new Color(0.4f, 1f, 0.2f), 2.5f));
                    }
                    break;
                case "composite_foundry":
                    Pad(b, 2, 2, concrete);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.4f, 0.1f), new Vector3(1.5f, 0.7f, 1.3f), Mats.Lit(new Color(0.12f, 0.12f, 0.15f), 0.8f, 0.5f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.78f, 0.1f), new Vector3(1.2f, 0.12f, 1.0f), Mats.Lit(new Color(0.18f, 0.18f, 0.22f), 0.8f, 0.5f), new Vector3(0, 45, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, -0.56f), new Vector3(1.1f, 0.08f, 0.03f), Mats.Glow(new Color(0.75f, 0.3f, 1f), 2.5f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.82f, 0.1f), new Vector3(1.52f, 0.03f, 1.32f), teamMat);
                    break;
                case "fusion_reactor":
                    Pad(b, 3, 3, concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.3f, 0), new Vector3(2.4f, 0.25f, 2.4f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.56f, 0), new Vector3(2.0f, 0.04f, 2.0f), teamMat);
                    for (int i = 0; i < 6; i++)
                    {
                        float a = i * Mathf.PI / 3;
                        Part(b, PrimitiveType.Cube, new Vector3(Mathf.Cos(a) * 0.9f, 0.9f, Mathf.Sin(a) * 0.9f), new Vector3(0.18f, 0.7f, 0.18f), dark);
                    }
                    rig.Turret = Empty(b, "core", new Vector3(0, 1.0f, 0));
                    Part(rig.Turret, PrimitiveType.Sphere, Vector3.zero, Vector3.one * 0.75f, Mats.Glow(new Color(1f, 0.35f, 0.9f), 3.5f));
                    break;
                case "airfield":
                    Pad(b, 3, 3, Mats.Lit(new Color(0.22f, 0.22f, 0.22f), 0.2f, 0f));
                    for (int i = 0; i < 5; i++) Part(b, PrimitiveType.Cube, new Vector3(-0.3f, 0.09f, -1.1f + i * 0.5f), new Vector3(0.06f, 0.01f, 0.25f), Mats.Lit(Color.white * 0.9f, 0.2f, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0.95f, 0.5f, 0.95f), new Vector3(0.5f, 0.9f, 0.5f), steel);
                    Part(b, PrimitiveType.Cube, new Vector3(0.95f, 1.05f, 0.95f), new Vector3(0.65f, 0.25f, 0.65f), Mats.Lit(new Color(0.2f, 0.35f, 0.45f), 0.95f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0.95f, 1.2f, 0.95f), new Vector3(0.7f, 0.05f, 0.7f), teamMat);
                    Part(b, PrimitiveType.Cylinder, new Vector3(-0.3f, 0.1f, 0.9f), new Vector3(0.9f, 0.01f, 0.9f), Mats.Glow(new Color(1f, 0.8f, 0.2f), 0.8f));
                    break;
                case "sam_site":
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.12f, 0), new Vector3(0.85f, 0.12f, 0.85f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.26f, 0), new Vector3(0.6f, 0.03f, 0.6f), teamMat);
                    rig.Turret = Empty(b, "launcher", new Vector3(0, 0.35f, 0));
                    rig.Barrel = Empty(rig.Turret, "rack", new Vector3(0, 0.1f, 0));
                    for (int i = 0; i < 4; i++)
                        Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(-0.15f + (i % 2) * 0.3f, 0.12f + (i / 2) * 0.16f, 0.05f), new Vector3(0.11f, 0.25f, 0.11f), Mats.Lit(new Color(0.85f, 0.85f, 0.8f), 0.4f, 0.2f), new Vector3(-60, 0, 0));
                    break;
                case "laser_tower":
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.15f, 0), new Vector3(0.8f, 0.15f, 0.8f), concrete);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.75f, 0), new Vector3(0.22f, 0.6f, 0.22f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.32f, 0), new Vector3(0.55f, 0.03f, 0.55f), teamMat);
                    rig.Turret = Empty(b, "emitter", new Vector3(0, 1.45f, 0));
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.28f, 0.42f, 0.28f), Mats.Glow(new Color(0.3f, 0.9f, 1f), 3.5f), new Vector3(45, 0, 45));
                    break;

                // ---- New units
                case "repair_truck":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.25f, 0.12f, 0), new Vector3(0.15f, 0.24f, 0.95f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.25f, 0.12f, 0), new Vector3(0.15f, 0.24f, 0.95f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.28f, 0), new Vector3(0.52f, 0.14f, 1.0f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.46f, 0.32f), new Vector3(0.42f, 0.22f, 0.28f), Mats.Lit(new Color(0.2f, 0.3f, 0.4f), 0.9f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.42f, -0.2f), new Vector3(0.46f, 0.16f, 0.5f), Mats.Lit(new Color(0.95f, 0.75f, 0.1f), 0.4f, 0.3f));
                    Part(b, PrimitiveType.Sphere, new Vector3(0.15f, 0.6f, 0.42f), Vector3.one * 0.07f, Mats.Glow(new Color(1f, 0.6f, 0.1f), 3f));
                    // Crane arm that swings to face whatever it's repairing.
                    rig.Turret = Empty(b, "crane", new Vector3(0, 0.52f, -0.25f));
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0.12f, 0.2f), new Vector3(0.06f, 0.06f, 0.55f), steel, new Vector3(-20, 0, 0));
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Sphere, new Vector3(0, 0.22f, 0.48f), Vector3.one * 0.08f, Mats.Glow(new Color(1f, 0.85f, 0.4f), 2.5f));
                    break;
                case "apc":
                    for (int i = 0; i < 6; i++)
                        Part(b, PrimitiveType.Cylinder, new Vector3(i % 2 == 0 ? -0.28f : 0.28f, 0.11f, -0.3f + (i / 2) * 0.3f), new Vector3(0.2f, 0.05f, 0.2f), track, new Vector3(0, 0, 90));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.27f, 0), new Vector3(0.52f, 0.26f, 0.95f), Mats.Lit(DarkSteel, 0.5f, 0f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.41f, 0), new Vector3(0.44f, 0.02f, 0.6f), teamMat); // team roof band
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0.47f), new Vector3(0.5f, 0.18f, 0.1f), Mats.Lit(DarkSteel, 0.5f, 0f), new Vector3(-30, 0, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, -0.49f), new Vector3(0.32f, 0.2f, 0.02f), dark);
                    rig.Turret = Empty(b, "mg", new Vector3(0, 0.44f, 0.1f));
                    Part(rig.Turret, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.14f, 0.04f, 0.14f), teamMat);
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0.04f, 0.15f), new Vector3(0.035f, 0.035f, 0.3f), dark);
                    break;
                case "flak_track":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.25f, 0.11f, 0), new Vector3(0.15f, 0.22f, 0.85f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.25f, 0.11f, 0), new Vector3(0.15f, 0.22f, 0.85f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.24f, 0), new Vector3(0.42f, 0.16f, 0.8f), Mats.Lit(DarkSteel, 0.5f, 0f));
                    rig.Turret = Empty(b, "flak", new Vector3(0, 0.38f, -0.05f));
                    Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.3f, 0.14f, 0.26f), teamMat);
                    rig.Barrel = Empty(rig.Turret, "guns", new Vector3(0, 0.06f, 0.05f));
                    for (int i = 0; i < 4; i++)
                        Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(-0.09f + (i % 2) * 0.18f, 0.08f + (i / 2) * 0.07f, 0.16f), new Vector3(0.035f, 0.18f, 0.035f), steel, new Vector3(50, 0, 0));
                    break;
                case "minelayer":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.25f, 0.11f, 0), new Vector3(0.15f, 0.22f, 0.9f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.25f, 0.11f, 0), new Vector3(0.15f, 0.22f, 0.9f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.26f, 0.15f), new Vector3(0.44f, 0.2f, 0.55f), Mats.Lit(DarkSteel, 0.5f, 0f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.37f, 0.25f), new Vector3(0.36f, 0.02f, 0.3f), teamMat); // team cab roof
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, -0.3f), new Vector3(0.4f, 0.24f, 0.3f), Mats.Lit(Kraft, 0.2f, 0f));
                    for (int i = 0; i < 3; i++) Part(b, PrimitiveType.Cylinder, new Vector3(-0.12f + i * 0.12f, 0.45f, -0.3f), new Vector3(0.1f, 0.02f, 0.1f), dark);
                    break;
                case "mine":
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.03f, 0), new Vector3(0.32f, 0.03f, 0.32f), dark);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.065f, 0), new Vector3(0.22f, 0.01f, 0.22f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(0, 0.08f, 0), Vector3.one * 0.05f, Mats.Glow(PezPalette.EmissiveAmberIndustryDocking, 3f));
                    break;
                case "mammoth_tank":
                    Tank(rig, teamMat, teamDark, steel, track, 1.65f, true);
                    // Missile pods either side of the turret.
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(0.32f, 0.12f, -0.05f), new Vector3(0.12f, 0.12f, 0.3f), dark);
                    Part(rig.Turret, PrimitiveType.Cube, new Vector3(-0.32f, 0.12f, -0.05f), new Vector3(0.12f, 0.12f, 0.3f), dark);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.42f, -0.4f), new Vector3(0.6f, 0.1f, 0.2f), steel);
                    break;
                case "recon_drone":
                    rig.Altitude = 2.8f;
                    Part(b, PrimitiveType.Sphere, Vector3.zero, new Vector3(0.22f, 0.1f, 0.28f), teamMat);
                    Part(b, PrimitiveType.Sphere, new Vector3(0, -0.04f, 0.1f), Vector3.one * 0.07f, Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 2.5f));
                    var props = Empty(b, "props", Vector3.zero); // static discs read as spinning props at game distance
                    for (int i = 0; i < 4; i++)
                    {
                        var arm = new Vector3(i % 2 == 0 ? -0.22f : 0.22f, 0.02f, i < 2 ? 0.22f : -0.22f);
                        Part(b, PrimitiveType.Cube, arm * 0.5f, new Vector3(0.03f, 0.02f, 0.3f), dark, new Vector3(0, i % 3 == 0 ? 45 : -45, 0));
                        Part(props, PrimitiveType.Cylinder, arm + Vector3.up * 0.03f, new Vector3(0.18f, 0.005f, 0.18f), Mats.Unlit(new Color(0.75f, 0.75f, 0.75f, 0.45f)));
                    }
                    break;
                case "transport_chopper":
                    rig.Altitude = 2.6f;
                    Part(b, PrimitiveType.Capsule, new Vector3(0, 0, 0), new Vector3(0.42f, 0.55f, 0.42f), teamMat, new Vector3(90, 0, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.06f, 0.42f), new Vector3(0.3f, 0.16f, 0.14f), Mats.Lit(new Color(0.15f, 0.25f, 0.35f), 0.95f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, -0.2f, 0), new Vector3(0.4f, 0.03f, 0.6f), dark);
                    rig.Spinner = Empty(b, "rotors", Vector3.zero);
                    Part(rig.Spinner, PrimitiveType.Cube, new Vector3(0, 0.28f, 0.35f), new Vector3(1.3f, 0.015f, 0.07f), dark);
                    Part(rig.Spinner, PrimitiveType.Cube, new Vector3(0, 0.28f, 0.35f), new Vector3(0.07f, 0.015f, 1.3f), dark);
                    Part(rig.Spinner, PrimitiveType.Cube, new Vector3(0, 0.32f, -0.4f), new Vector3(1.3f, 0.015f, 0.07f), dark);
                    Part(rig.Spinner, PrimitiveType.Cube, new Vector3(0, 0.32f, -0.4f), new Vector3(0.07f, 0.015f, 1.3f), dark);
                    break;
                case "outpost_truck":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.28f, 0.13f, 0), new Vector3(0.16f, 0.26f, 1.1f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.28f, 0.13f, 0), new Vector3(0.16f, 0.26f, 1.1f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0), new Vector3(0.6f, 0.15f, 1.15f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.5f, 0.4f), new Vector3(0.45f, 0.25f, 0.3f), Mats.Lit(new Color(0.2f, 0.3f, 0.4f), 0.9f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.55f, -0.15f), new Vector3(0.55f, 0.35f, 0.6f), steel);
                    Part(b, PrimitiveType.Cylinder, new Vector3(0.18f, 0.9f, -0.3f), new Vector3(0.03f, 0.25f, 0.03f), dark);
                    break;
                case "scout_buggy":
                    for (int i = 0; i < 4; i++)
                        Part(b, PrimitiveType.Cylinder, new Vector3(i % 2 == 0 ? -0.24f : 0.24f, 0.1f, i < 2 ? 0.25f : -0.25f), new Vector3(0.18f, 0.05f, 0.18f), track, new Vector3(0, 0, 90));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.18f, 0), new Vector3(0.38f, 0.1f, 0.7f), teamMat);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.28f, -0.05f), new Vector3(0.32f, 0.04f, 0.3f), dark);
                    rig.Turret = Empty(b, "mg", new Vector3(0, 0.33f, -0.1f));
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0, 0.15f), new Vector3(0.04f, 0.04f, 0.3f), dark);
                    break;
                case "artillery":
                    Part(b, PrimitiveType.Cube, new Vector3(-0.27f, 0.11f, 0), new Vector3(0.16f, 0.22f, 0.9f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0.27f, 0.11f, 0), new Vector3(0.16f, 0.22f, 0.9f), track);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.24f, -0.05f), new Vector3(0.44f, 0.16f, 0.8f), teamMat);
                    rig.Turret = Empty(b, "gun", new Vector3(0, 0.36f, -0.15f));
                    Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.3f, 0.14f, 0.3f), teamDark);
                    rig.Barrel = Empty(rig.Turret, "barrel", new Vector3(0, 0.05f, 0.1f));
                    Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(0, 0.22f, 0.38f), new Vector3(0.07f, 0.45f, 0.07f), steel, new Vector3(60, 0, 0));
                    break;
                case "laser_tank":
                    Tank(rig, teamMat, teamDark, steel, track, 1.2f, false);
                    // Laser, not plasma: cyan coils (magenta is reserved for plasma and fusion).
                    Part(rig.Turret, PrimitiveType.Sphere, new Vector3(0, 0.15f, -0.05f), Vector3.one * 0.18f, Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 3f));
                    foreach (Transform c in rig.Barrel) c.GetComponent<Renderer>().sharedMaterial = Mats.Glow(PezPalette.EmissiveCyanLaserOptics, 1.6f);
                    break;
                case "gunship":
                    rig.Altitude = 2.4f;
                    Part(b, PrimitiveType.Capsule, new Vector3(0, 0, 0), new Vector3(0.35f, 0.42f, 0.35f), teamMat, new Vector3(90, 0, 0));
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.02f, -0.6f), new Vector3(0.06f, 0.06f, 0.5f), teamDark);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.12f, -0.82f), new Vector3(0.03f, 0.2f, 0.12f), teamDark);
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.05f, 0.28f), new Vector3(0.22f, 0.12f, 0.15f), Mats.Lit(new Color(0.15f, 0.25f, 0.35f), 0.95f, 0.4f));
                    Part(b, PrimitiveType.Cube, new Vector3(0, -0.08f, 0), new Vector3(0.7f, 0.04f, 0.12f), dark);
                    rig.Spinner = Empty(b, "rotor", new Vector3(0, 0.24f, 0));
                    Part(rig.Spinner, PrimitiveType.Cube, Vector3.zero, new Vector3(1.4f, 0.015f, 0.07f), dark);
                    Part(rig.Spinner, PrimitiveType.Cube, Vector3.zero, new Vector3(0.07f, 0.015f, 1.4f), dark);
                    rig.Turret = Empty(b, "pods", new Vector3(0, -0.1f, 0.1f));
                    rig.Barrel = Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0.3f, 0, 0), new Vector3(0.07f, 0.12f, 0.07f), steel, new Vector3(90, 0, 0));
                    break;
                case "stealth_bomber":
                    {
                        rig.Altitude = 3.2f;
                        var hull = Mats.Lit(new Color(0.09f, 0.09f, 0.11f), 0.85f, 0.6f);
                        Part(b, PrimitiveType.Cube, Vector3.zero, new Vector3(0.9f, 0.08f, 0.9f), hull, new Vector3(0, 45, 0));
                        Part(b, PrimitiveType.Cube, new Vector3(0, 0.05f, 0.1f), new Vector3(0.3f, 0.1f, 0.7f), hull);
                        Part(b, PrimitiveType.Cube, new Vector3(0, 0.02f, -0.45f), new Vector3(1.0f, 0.04f, 0.04f), Mats.Glow(tc, 1.2f));
                        Part(b, PrimitiveType.Cube, new Vector3(0, 0.1f, 0.3f), new Vector3(0.14f, 0.04f, 0.18f), Mats.Lit(new Color(0.25f, 0.2f, 0.1f), 0.95f, 0.8f));
                        break;
                    }
                // Deep mining fallbacks, used only while a model is missing. Swapping in art needs no code: drop geological_surveyor.glb, drill_rig.glb or
                // deep_mine.glb into Resources/PezModels and ModelFor picks it up ahead of these.
                case "geological_surveyor": Surveyor(rig, teamMat, steel, track); break;
                case "drill_rig": DrillRig(rig, teamMat, steel, track); break;
                case "deep_mine": DeepMine(rig, teamMat, steel, concrete); break;
                default:
                    Part(b, PrimitiveType.Cube, new Vector3(0, 0.25f, 0), Vector3.one * 0.5f, teamMat);
                    break;
            }
            if (rig.Barrel != null) rig.BarrelRest = rig.Barrel.localPosition;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }
            return rig;
        }

        /// <summary>
        /// Placeholder soldier: hang everything built so far from a hips pivot and stand it on two swinging legs.
        /// </summary>
        static void Legs(Rig rig, float s, Material m)
        {
            var b = rig.Body;
            float hipY = 0.2f * s, legLen = 0.2f * s;
            var kids = new List<Transform>();
            foreach (Transform c in b) kids.Add(c);
            var hips = Empty(b, "hips", new Vector3(0, hipY, 0));
            foreach (var c in kids)
            {
                var lp = c.localPosition;
                c.SetParent(hips, false);
                c.localPosition = lp - hips.localPosition;
            }
            Transform Leg(string name, float x)
            {
                var pivot = Empty(b, name, new Vector3(x, hipY, 0));
                Part(pivot, PrimitiveType.Cube, new Vector3(0, -legLen * 0.5f, 0), new Vector3(0.055f, legLen + 0.02f * s, 0.065f), m);
                return pivot;
            }
            var legL = Leg("leg_l", -0.038f * s);
            var legR = Leg("leg_r", 0.038f * s);
            rig.Gait = new Gait(b, hips, legL, legR, legLen, 1f, ++gaitSeed);
        }

        static void Tank(Rig rig, Material team, Material teamDark, Material steel, Material track, float s, bool twin)
        {
            var b = rig.Body;
            Part(b, PrimitiveType.Cube, new Vector3(-0.27f, 0.11f, 0) * s, new Vector3(0.17f, 0.22f, 0.9f) * s, track);
            Part(b, PrimitiveType.Cube, new Vector3(0.27f, 0.11f, 0) * s, new Vector3(0.17f, 0.22f, 0.9f) * s, track);
            // Stem hull in smoke plastic, team-coloured head (turret) on top: the art pack's 15-25% team mask.
            var hull = Mats.Lit(DarkSteel, 0.5f, 0f);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.22f, 0) * s, new Vector3(0.44f, 0.18f, 0.82f) * s, hull);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.26f, 0.38f) * s, new Vector3(0.42f, 0.1f, 0.12f) * s, hull, new Vector3(-25, 0, 0));
            rig.Turret = Empty(b, "turret", new Vector3(0, 0.34f, -0.04f) * s);
            Part(rig.Turret, PrimitiveType.Cube, new Vector3(0, 0.04f, 0) * s, new Vector3(0.34f, 0.14f, 0.4f) * s, team);
            Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0.08f, 0.13f, -0.08f) * s, new Vector3(0.1f, 0.03f, 0.1f) * s, steel);
            rig.Barrel = Empty(rig.Turret, "barrel", new Vector3(0, 0.05f, 0.2f) * s);
            if (twin)
            {
                Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(-0.06f, 0, 0.25f) * s, new Vector3(0.05f, 0.25f, 0.05f) * s, steel, new Vector3(90, 0, 0));
                Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(0.06f, 0, 0.25f) * s, new Vector3(0.05f, 0.25f, 0.05f) * s, steel, new Vector3(90, 0, 0));
            }
            else Part(rig.Barrel, PrimitiveType.Cylinder, new Vector3(0, 0, 0.22f) * s, new Vector3(0.05f, 0.22f, 0.05f) * s, steel, new Vector3(90, 0, 0));
        }

        // ---- Deep mining placeholders, in the placeholder palette: smoke-plastic hull, team-coloured top, caramel amber
        // for industry. Each is self-contained so it can be deleted when its model lands.
        static Material Smoke => Mats.Lit(DarkSteel, 0.5f, 0f);
        static Material Amber(float glow = 1.6f) => Mats.Glow(PezPalette.EmissiveAmberIndustryDocking, glow);

        /// <summary>Geological Surveyor: a light wheeled vehicle with a sensor mast (Turret) that spins while surveying.</summary>
        static void Surveyor(Rig rig, Material team, Material steel, Material track)
        {
            var b = rig.Body;
            for (int i = 0; i < 4; i++)
                Part(b, PrimitiveType.Cylinder, new Vector3(i % 2 == 0 ? -0.26f : 0.26f, 0.11f, i < 2 ? 0.28f : -0.28f), new Vector3(0.2f, 0.05f, 0.2f), track, new Vector3(0, 0, 90));
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.22f, 0), new Vector3(0.44f, 0.16f, 0.8f), Smoke);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.34f, 0.22f), new Vector3(0.38f, 0.12f, 0.28f), team);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.36f, 0.37f), new Vector3(0.3f, 0.07f, 0.02f), Mats.Lit(new Color(0.15f, 0.25f, 0.35f), 0.95f, 0.4f));
            rig.Turret = Empty(b, "mast", new Vector3(0, 0.3f, -0.18f));
            Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.25f, 0), new Vector3(0.05f, 0.25f, 0.05f), steel);
            Part(rig.Turret, PrimitiveType.Cylinder, new Vector3(0, 0.52f, 0.06f), new Vector3(0.26f, 0.02f, 0.26f), steel, new Vector3(70, 0, 0));
            Part(rig.Turret, PrimitiveType.Sphere, new Vector3(0, 0.54f, 0.1f), Vector3.one * 0.06f, Amber(2f));
        }

        /// <summary>Drill Rig: a slow tracked carrier with its drill tower folded flat along the back.</summary>
        static void DrillRig(Rig rig, Material team, Material steel, Material track)
        {
            var b = rig.Body;
            Part(b, PrimitiveType.Cube, new Vector3(-0.34f, 0.13f, 0), new Vector3(0.2f, 0.26f, 1.3f), track);
            Part(b, PrimitiveType.Cube, new Vector3(0.34f, 0.13f, 0), new Vector3(0.2f, 0.26f, 1.3f), track);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.3f, 0), new Vector3(0.62f, 0.2f, 1.3f), Smoke);
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.5f, 0.45f), new Vector3(0.5f, 0.22f, 0.32f), team);
            // The folded tower: two rails with cross braces, the drill head at the front.
            for (int i = -1; i <= 1; i += 2)
                Part(b, PrimitiveType.Cube, new Vector3(i * 0.14f, 0.47f, -0.2f), new Vector3(0.05f, 0.05f, 1.2f), steel);
            for (int i = 0; i < 5; i++)
                Part(b, PrimitiveType.Cube, new Vector3(0, 0.47f, -0.72f + i * 0.26f), new Vector3(0.28f, 0.03f, 0.03f), steel, new Vector3(0, 35, 0));
            Part(b, PrimitiveType.Cylinder, new Vector3(0, 0.47f, 0.42f), new Vector3(0.12f, 0.14f, 0.12f), Amber(1.2f), new Vector3(90, 0, 0));
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.42f, -0.66f), new Vector3(0.5f, 0.04f, 0.04f), Amber(1.8f));
        }

        /// <summary>Deep Mine (2x2): a derrick headframe over the shaft; its sheave wheel (Turret) turns while pumping.</summary>
        static void DeepMine(Rig rig, Material team, Material steel, Material concrete)
        {
            var b = rig.Body;
            Pad(b, 2, 2, concrete);
            Part(b, PrimitiveType.Cube, new Vector3(0.45f, 0.3f, 0.45f), new Vector3(0.75f, 0.45f, 0.75f), Smoke);
            Part(b, PrimitiveType.Cube, new Vector3(0.45f, 0.56f, 0.45f), new Vector3(0.77f, 0.07f, 0.77f), team);
            Part(b, PrimitiveType.Cylinder, new Vector3(-0.15f, 0.1f, -0.15f), new Vector3(0.6f, 0.04f, 0.6f), Mats.Lit(PezPalette.MaterialsLicorice, 0.2f, 0f));
            // Four-legged derrick leaning in over the shaft.
            for (int i = 0; i < 4; i++)
            {
                float sx = i % 2 == 0 ? -1 : 1, sz = i < 2 ? -1 : 1;
                Part(b, PrimitiveType.Cube, new Vector3(-0.15f + sx * 0.22f, 0.8f, -0.15f + sz * 0.22f), new Vector3(0.05f, 1.45f, 0.05f), steel, new Vector3(-sz * 9f, 0, sx * 9f));
            }
            Part(b, PrimitiveType.Cube, new Vector3(-0.15f, 1.5f, -0.15f), new Vector3(0.3f, 0.06f, 0.3f), team);
            rig.Turret = Empty(b, "sheave", new Vector3(-0.15f, 1.62f, -0.15f));
            Part(rig.Turret, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.34f, 0.025f, 0.34f), steel, new Vector3(0, 0, 90));
            Part(rig.Turret, PrimitiveType.Cube, Vector3.zero, new Vector3(0.03f, 0.3f, 0.04f), Amber(1.4f));
            Part(b, PrimitiveType.Cube, new Vector3(0.45f, 0.35f, 0.07f), new Vector3(0.5f, 0.05f, 0.02f), Amber(1.6f));
        }

        /// <summary>Concrete foundation covering a w x h footprint, centred on the structure origin.</summary>
        static void Pad(Transform b, int w, int h, Material m)
        {
            Part(b, PrimitiveType.Cube, new Vector3(0, 0.04f, 0), new Vector3(w - 0.08f, 0.08f, h - 0.08f), m);
        }
    }
}
