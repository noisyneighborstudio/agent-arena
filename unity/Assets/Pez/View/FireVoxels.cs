using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// A building turned into the grid its fire simulates on (<see cref="FireSim"/>): once per building, from the finished
    /// model's meshes. The domain is the building's bounds with room around it for flames to lean and lick out, and above
    /// it for the plume. Per cell:
    ///  - the signed distance to the building's surface (cells, negative inside), so the fluid flows around the real shape
    ///    and flames hug the walls and roof instead of a bounding box;
    ///  - what can burn there (Emit): r, the surface skin just outside the building (roofs most, walls less, nothing
    ///    under overhangs); g, the openings fire licks out of; b, the coverage at which this patch catches (a low-frequency
    ///    noise, so a spreading fire takes the roof in patches); a, a phase so each opening pulses on its own.
    /// Openings are the model's dark recesses, doors, vents and glass where they face sideways (the art has no literal
    /// windows: those parts read as them), else spots on the walls at about half height.
    /// </summary>
    public class FireVoxels
    {
        public Vector3Int Dim;
        public Vector3 Origin;   // world position of cell (0,0,0)'s corner
        public float H;          // cell size (world units; one tile is one unit)
        public float RoofY;      // the building's top
        public Bounds Body;      // the model's bounds
        public float[] Sdf;      // signed distance to the surface in cells, x fastest then y then z
        public Color[] Emit;
        public readonly List<(Vector3 pos, Vector3 normal)> Openings = new List<(Vector3, Vector3)>();
        public readonly List<Vector3> RoofSpots = new List<Vector3>();
        public Vector3 Size => new Vector3(Dim.x * H, Dim.y * H, Dim.z * H);
        public Vector3 Center => Origin + Size * 0.5f;

        /// <summary>The most cells a fire may have (about 9 MB of GPU state); bigger buildings get coarser cells.</summary>
        const int MaxCells = 120000;
        const float MinCell = 0.075f;

        static readonly string[] OpeningMats = { "M_Dark", "M_FrostGlass", "M_GrapeVent" };

        /// <summary>Does this part read as an opening (dark recess, door, vent, glass)? Fire licks out of it and it glows.</summary>
        public static bool IsOpening(string material, string node)
        {
            if (node.StartsWith("door")) return true;
            foreach (var o in OpeningMats) if (material.StartsWith(o)) return true;
            return false;
        }

        int Idx(int x, int y, int z) => x + Dim.x * (y + Dim.y * z);

        /// <summary>A model's meshes in world space (read on the main thread; Build then runs on any thread).</summary>
        public class Source
        {
            public readonly List<(Vector3[] verts, int[] tris, string mat, string node)> Parts = new List<(Vector3[], int[], string, string)>();
            public Bounds Body;
        }

        public static Source Gather(GameObject model)
        {
            var src = new Source();
            bool any = false;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>();
                var mesh = mf.sharedMesh;
                if (mesh == null || mr == null || !mr.enabled || !mesh.isReadable) continue;
                var m = mf.transform.localToWorldMatrix;
                var verts = mesh.vertices;
                for (int i = 0; i < verts.Length; i++) verts[i] = m.MultiplyPoint3x4(verts[i]);
                var tris = new List<int>();
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    if (mesh.GetTopology(sub) == MeshTopology.Triangles) tris.AddRange(mesh.GetTriangles(sub));
                src.Parts.Add((verts, tris.ToArray(), mr.sharedMaterial != null ? mr.sharedMaterial.name : "", mf.gameObject.name));
                if (!any) { src.Body = mr.bounds; any = true; } else src.Body.Encapsulate(mr.bounds);
            }
            return any ? src : null;
        }

        /// <summary>The grid for these meshes. Pure computation (no Unity objects), so FireSim runs it off the main thread.</summary>
        public static FireVoxels Build(Source src, int seed)
        {
            var body = src.Body;
            var filters = src.Parts;
            var v = new FireVoxels { Body = body, RoofY = body.max.y };
            float size = Mathf.Max(body.size.x, body.size.z);
            float margin = 0.55f + 0.12f * size, plume = 2.1f + 0.45f * size;
            var min = new Vector3(body.min.x - margin, 0f, body.min.z - margin);
            var max = new Vector3(body.max.x + margin, body.max.y + plume, body.max.z + margin);
            var ext = max - min;
            float h = Mathf.Max(MinCell, Mathf.Pow(ext.x * ext.y * ext.z / MaxCells, 1f / 3f));
            // Whole thread groups (4 cells) on every axis.
            int Round4(float len) => Mathf.Max(8, Mathf.CeilToInt(len / h / 4f) * 4);
            v.Dim = new Vector3Int(Round4(ext.x), Round4(ext.y), Round4(ext.z));
            v.H = h;
            v.Origin = new Vector3((min.x + max.x) * 0.5f - v.Dim.x * h * 0.5f, 0f, (min.z + max.z) * 0.5f - v.Dim.z * h * 0.5f);
            v.Voxelize(filters);
            v.DistanceField();
            v.Emitters(filters, seed);
            return v;
        }

        // ---------------------------------------------------------------- solid

        bool[] solid;

        /// <summary>
        /// Fill the cells inside each mesh: a vertical ray up every column, its crossings with the mesh's triangles sorted
        /// and filled between in pairs (inside/outside parity). A part that isn't closed (an odd count) fills between its
        /// lowest and highest crossing, which is right for the blockouts' convex pieces.
        /// </summary>
        void Voxelize(List<(Vector3[] verts, int[] tris, string mat, string node)> filters)
        {
            solid = new bool[Dim.x * Dim.y * Dim.z];
            var hits = new List<float>[Dim.x * Dim.z];
            for (int i = 0; i < hits.Length; i++) hits[i] = new List<float>(4);
            const float jx = 0.0137f, jz = 0.0071f; // off the cell centre, so rays don't run exactly along shared edges
            foreach (var (w, tris, _, _) in filters)
            {
                foreach (var l in hits) l.Clear();
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 a = w[tris[t]], b = w[tris[t + 1]], c = w[tris[t + 2]];
                    float area = (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z);
                    if (Mathf.Abs(area) < 1e-9f) continue; // vertical: no crossing
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - Origin.x) / H - 0.5f));
                    int x1 = Mathf.Min(Dim.x - 1, Mathf.CeilToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - Origin.x) / H - 0.5f));
                    int z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - Origin.z) / H - 0.5f));
                    int z1 = Mathf.Min(Dim.z - 1, Mathf.CeilToInt((Mathf.Max(a.z, Mathf.Max(b.z, c.z)) - Origin.z) / H - 0.5f));
                    for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++)
                        {
                            float px = Origin.x + (x + 0.5f) * H + jx * H, pz = Origin.z + (z + 0.5f) * H + jz * H;
                            // Barycentric in the xz plane.
                            float u = ((b.x - px) * (c.z - pz) - (c.x - px) * (b.z - pz)) / area;
                            float vv = ((c.x - px) * (a.z - pz) - (a.x - px) * (c.z - pz)) / area;
                            float ww = 1f - u - vv;
                            if (u < 0f || vv < 0f || ww < 0f) continue;
                            hits[x + Dim.x * z].Add(u * a.y + vv * b.y + ww * c.y);
                        }
                }
                for (int z = 0; z < Dim.z; z++)
                    for (int x = 0; x < Dim.x; x++)
                    {
                        var l = hits[x + Dim.x * z];
                        if (l.Count < 2) continue;
                        l.Sort();
                        if ((l.Count & 1) == 0) for (int k = 0; k < l.Count; k += 2) Fill(x, z, l[k], l[k + 1]);
                        else Fill(x, z, l[0], l[l.Count - 1]);
                    }
            }
        }

        void Fill(int x, int z, float y0, float y1)
        {
            int a = Mathf.Max(0, Mathf.CeilToInt((y0 - Origin.y) / H - 0.5f));
            int b = Mathf.Min(Dim.y - 1, Mathf.FloorToInt((y1 - Origin.y) / H - 0.5f));
            for (int y = a; y <= b; y++) solid[Idx(x, y, z)] = true;
        }

        // ---------------------------------------------------------------- distance

        /// <summary>
        /// Signed distance in cells: a two-pass 3D chamfer transform (weights 1, sqrt 2, sqrt 3 over the 26 neighbours),
        /// once for the distance to the building outside and once for the distance to the air inside. Capped at 12 cells.
        /// </summary>
        void DistanceField()
        {
            int n = solid.Length;
            var dOut = new float[n];
            var dIn = new float[n];
            const float Big = 12f;
            for (int i = 0; i < n; i++) { dOut[i] = solid[i] ? 0f : Big; dIn[i] = solid[i] ? Big : 0f; }
            Chamfer(dOut); Chamfer(dIn);
            Sdf = new float[n];
            // Half a cell either side of the surface, so the zero crossing sits between solid and air.
            for (int i = 0; i < n; i++) Sdf[i] = solid[i] ? -(dIn[i] - 0.5f) : dOut[i] - 0.5f;
        }

        void Chamfer(float[] d)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                int s = pass == 0 ? 1 : -1;
                int zb = pass == 0 ? 0 : Dim.z - 1, ze = pass == 0 ? Dim.z : -1;
                int yb = pass == 0 ? 0 : Dim.y - 1, ye = pass == 0 ? Dim.y : -1;
                int xb = pass == 0 ? 0 : Dim.x - 1, xe = pass == 0 ? Dim.x : -1;
                for (int z = zb; z != ze; z += s)
                    for (int y = yb; y != ye; y += s)
                        for (int x = xb; x != xe; x += s)
                        {
                            int i = Idx(x, y, z);
                            float best = d[i];
                            if (best == 0f) continue;
                            // The 13 neighbours already visited in this pass's order.
                            for (int dz = -1; dz <= 0; dz++)
                                for (int dy = -1; dy <= 1; dy++)
                                    for (int dx = -1; dx <= 1; dx++)
                                    {
                                        if (dz == 0 && (dy > 0 || (dy == 0 && dx >= 0))) continue;
                                        int nx = x + dx * s, ny = y + dy * s, nz = z + dz * s;
                                        if (nx < 0 || ny < 0 || nz < 0 || nx >= Dim.x || ny >= Dim.y || nz >= Dim.z) continue;
                                        int k = dx * dx + dy * dy + dz * dz;
                                        float c = d[Idx(nx, ny, nz)] + (k == 1 ? 1f : k == 2 ? 1.4142f : 1.7321f);
                                        if (c < best) best = c;
                                    }
                            d[i] = best;
                        }
            }
        }

        Vector3 Gradient(int x, int y, int z)
        {
            float S(int a, int b, int c) => Sdf[Idx(Mathf.Clamp(a, 0, Dim.x - 1), Mathf.Clamp(b, 0, Dim.y - 1), Mathf.Clamp(c, 0, Dim.z - 1))];
            var g = new Vector3(S(x + 1, y, z) - S(x - 1, y, z), S(x, y + 1, z) - S(x, y - 1, z), S(x, y, z + 1) - S(x, y, z - 1));
            return g.sqrMagnitude > 1e-6f ? g.normalized : Vector3.up;
        }

        // ---------------------------------------------------------------- what burns

        void Emitters(List<(Vector3[] verts, int[] tris, string mat, string node)> filters, int seed)
        {
            var rnd = new System.Random(seed * 7919 + 31);
            float R() => (float)rnd.NextDouble();
            Emit = new Color[Sdf.Length];
            var noiseOff = new Vector3(R() * 50f, R() * 50f, R() * 50f);

            // The skin: cells within about a cell and a half outside the surface.
            for (int z = 0; z < Dim.z; z++)
                for (int y = 0; y < Dim.y; y++)
                    for (int x = 0; x < Dim.x; x++)
                    {
                        int i = Idx(x, y, z);
                        float d = Sdf[i];
                        if (d <= 0f || d > 1.6f) continue;
                        var nrm = Gradient(x, y, z);
                        var wp = Origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * H;
                        if (wp.y < 0.12f) continue; // not the ground at the foot of the walls
                        float face = nrm.y > 0.5f ? 1f : nrm.y > -0.3f ? 0.2f : 0f;
                        float skin = face * Mathf.Clamp01(1.6f - d);
                        // Patches: where on the building the fire takes first (low) and last (high).
                        float patch = Noise3((wp + noiseOff) * 1.3f);
                        patch = Mathf.Clamp01((patch - 0.2f) / 0.6f) * 0.85f + 0.08f;
                        Emit[i] = new Color(skin, 0f, patch, 0f);
                    }

            // Openings: sideways faces of the materials that read as windows, doors and vents.
            var cands = new List<(Vector3 p, Vector3 n, float area)>();
            foreach (var (verts, tris, mat, node) in filters)
            {
                if (!IsOpening(mat, node)) continue;
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 a = verts[tris[t]], b = verts[tris[t + 1]], c = verts[tris[t + 2]];
                    var cr = Vector3.Cross(b - a, c - a);
                    float area = cr.magnitude * 0.5f;
                    if (area < 0.002f) continue;
                    var n = cr / (area * 2f);
                    if (Mathf.Abs(n.y) > 0.45f) continue;
                    var p = (a + b + c) / 3f;
                    if (p.y < 0.15f || p.y > RoofY - 0.08f) continue;
                    // Only faces on the outside of the building (the cell just out along the normal is air).
                    if (SdfAt(p + n * H * 1.5f) < 0.3f) continue;
                    cands.Add((p, n, area));
                }
            }
            int want = Mathf.Clamp(Mathf.RoundToInt(2f + Mathf.Max(Body.size.x, Body.size.z) * 1.3f), 2, 7);
            PickOpenings(cands, want, rnd);
            if (Openings.Count < 2) WallOpenings(want - Openings.Count, rnd);
            for (int k = 0; k < Openings.Count; k++) MarkOpening(Openings[k].pos, Openings[k].normal, R());

            // Roof spots (for the particle fallback, sparks and the light): the highest skin cells, spread out.
            var tops = new List<Vector3>();
            for (int z = 0; z < Dim.z; z++)
                for (int x = 0; x < Dim.x; x++)
                    for (int y = Dim.y - 1; y >= 0; y--)
                        if (solid[Idx(x, y, z)]) { tops.Add(Origin + new Vector3(x + 0.5f, y + 1.2f, z + 0.5f) * H); break; }
            tops.Sort((p, q) => q.y.CompareTo(p.y));
            for (int k = 0; k < tops.Count && RoofSpots.Count < 3; k++)
            {
                var p = tops[Mathf.Min(tops.Count - 1, k + (int)(R() * Mathf.Min(12, tops.Count / 4)))];
                bool far = true;
                foreach (var q in RoofSpots) if ((q - p).sqrMagnitude < 0.35f) far = false;
                if (far) RoofSpots.Add(p);
            }
            while (RoofSpots.Count < 3) RoofSpots.Add(new Vector3(Body.center.x, RoofY, Body.center.z));
        }

        /// <summary>Farthest-point picks among the candidate faces (area-weighted start), so openings spread round the building.</summary>
        void PickOpenings(List<(Vector3 p, Vector3 n, float area)> cands, int want, System.Random rnd)
        {
            if (cands.Count == 0) return;
            float total = 0f; foreach (var c in cands) total += c.area;
            float pick = (float)rnd.NextDouble() * total;
            int first = 0;
            for (int i = 0; i < cands.Count; i++) { pick -= cands[i].area; if (pick <= 0f) { first = i; break; } }
            Openings.Add((cands[first].p, cands[first].n));
            while (Openings.Count < want)
            {
                int best = -1; float bestD = 0.36f; // at least 0.6 apart
                for (int i = 0; i < cands.Count; i++)
                {
                    float d = float.MaxValue;
                    foreach (var o in Openings) d = Mathf.Min(d, (o.pos - cands[i].p).sqrMagnitude);
                    d *= 0.7f + 0.6f * Mathf.Sqrt(cands[i].area); // bigger openings a little preferred
                    if (d > bestD) { bestD = d; best = i; }
                }
                if (best < 0) break;
                Openings.Add((cands[best].p, cands[best].n));
            }
        }

        /// <summary>No parts that read as openings: spots on the walls at 35-70% of the height, facing out.</summary>
        void WallOpenings(int n, System.Random rnd)
        {
            for (int tries = 0; tries < 200 && n > 0; tries++)
            {
                int x = rnd.Next(Dim.x), z = rnd.Next(Dim.z);
                float yy = Mathf.Lerp(0.35f, 0.7f, (float)rnd.NextDouble()) * RoofY;
                int y = Mathf.Clamp((int)((yy - Origin.y) / H), 0, Dim.y - 1);
                int i = Idx(x, y, z);
                if (Sdf[i] <= 0.2f || Sdf[i] > 1.2f) continue;
                var nrm = Gradient(x, y, z);
                if (Mathf.Abs(nrm.y) > 0.3f) continue;
                var p = Origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * H - nrm * Sdf[i] * H;
                bool far = true;
                foreach (var o in Openings) if ((o.pos - p).sqrMagnitude < 0.3f) far = false;
                if (!far) continue;
                nrm.y = 0f; nrm.Normalize();
                Openings.Add((p, nrm));
                n--;
            }
        }

        /// <summary>The skin cells within an opening's radius burn as that opening (g), each with its own pulse phase (a).</summary>
        void MarkOpening(Vector3 p, Vector3 n, float phase)
        {
            float r = Mathf.Max(0.18f, 3f * H);
            int cx = Mathf.FloorToInt((p.x - Origin.x) / H), cy = Mathf.FloorToInt((p.y - Origin.y) / H), cz = Mathf.FloorToInt((p.z - Origin.z) / H);
            int rc = Mathf.CeilToInt(r / H) + 2;
            for (int z = cz - rc; z <= cz + rc; z++)
                for (int y = cy - rc; y <= cy + rc; y++)
                    for (int x = cx - rc; x <= cx + rc; x++)
                    {
                        if (x < 0 || y < 0 || z < 0 || x >= Dim.x || y >= Dim.y || z >= Dim.z) continue;
                        int i = Idx(x, y, z);
                        if (Sdf[i] <= 0f || Sdf[i] > 2.2f) continue;
                        var wp = Origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * H;
                        var d = wp - p;
                        float along = Vector3.Dot(d, n);
                        if (along < -0.5f * H) continue;
                        float lateral = (d - n * along).magnitude;
                        // From the opening's upper half, so the flame rolls out of its head and up the wall above.
                        float k = Mathf.Clamp01(1f - lateral / r) * Mathf.Clamp01(1f - along / (3f * H)) * Mathf.Clamp01(0.6f + d.y / r);
                        if (k <= 0f) continue;
                        var e = Emit[i];
                        if (k > e.g) { e.g = k; e.a = phase; }
                        Emit[i] = e;
                    }
        }

        public float SdfAt(Vector3 world)
        {
            int x = Mathf.FloorToInt((world.x - Origin.x) / H), y = Mathf.FloorToInt((world.y - Origin.y) / H), z = Mathf.FloorToInt((world.z - Origin.z) / H);
            if (x < 0 || y < 0 || z < 0 || x >= Dim.x || y >= Dim.y || z >= Dim.z) return 99f;
            return Sdf[Idx(x, y, z)];
        }

        static float Hash(int x, int y, int z)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + z * 1442695041;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffffff) / (float)0xffffff;
            }
        }

        static float Noise3(Vector3 p)
        {
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), z = Mathf.FloorToInt(p.z);
            float fx = p.x - x, fy = p.y - y, fz = p.z - z;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
            float L(float a, float b, float t) => a + (b - a) * t;
            return L(L(L(Hash(x, y, z), Hash(x + 1, y, z), fx), L(Hash(x, y + 1, z), Hash(x + 1, y + 1, z), fx), fy),
                     L(L(Hash(x, y, z + 1), Hash(x + 1, y, z + 1), fx), L(Hash(x, y + 1, z + 1), Hash(x + 1, y + 1, z + 1), fx), fy), fz);
        }
    }
}
