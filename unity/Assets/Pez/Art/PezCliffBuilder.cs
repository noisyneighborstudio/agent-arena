// Tiered licorice massifs from the pathing grid. A port of tools/terrain_kit.py (same algorithm, same hash).
//
//   var mesh = PezCliffBuilder.Build(rockGrid, tileSize, out var dressing);
//   meshFilter.sharedMesh = mesh;               // 5 submeshes: crust T0, crust T1, crust T2, licorice face, talus
//   meshRenderer.sharedMaterials = materials;   // in that order
//   foreach (var d in dressing) Instantiate(boulderPrefabs[d.variant], d.position, Quaternion.Euler(0, d.yaw, 0)).transform.localScale = Vector3.one * d.scale;
//
// The grid stays the source of truth for pathing. The visual outline follows it within about ±0.3 tile.
// Nothing pops: rebuild only at map load, never mid-match.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Pez
{
    public struct PezRockDressing
    {
        public Vector3 position;
        public float radius;     // world units; boulder prefabs are authored at radius 0.16 / 0.26 / 0.40 (s / m / l)
        public float scale;      // radius relative to the chosen prefab's authored radius
        public int variant;      // 0..8 → boulder_s1..s3, m1..m3, l1..l3
        public float yaw;
        public int tier;         // crust tier the boulder sits on (0 = ground level)
    }

    public static class PezCliffBuilder
    {
        public static float[] TierHeights = { 1.0f, 0.85f, 0.7f };
        public static float TierStep = 1.6f;
        public static float TierWobble = 0.9f;
        const float Iso = 0.5f;
        // rings: outward offset, outward jitter, vertical jitter (fraction of tier height)
        static readonly Vector3[] Rings = {
            new Vector3(0.00f, 0.00f, 0.00f), new Vector3(0.07f, 0.02f, 0.00f), new Vector3(0.17f, 0.07f, 0.06f),
            new Vector3(0.32f, 0.07f, 0.00f), new Vector3(0.44f, 0.04f, 0.00f) };
        const int SubFace = 3, SubTalus = 4;
        static readonly float[] AuthoredRadius = { 0.16f, 0.26f, 0.40f };

        // ------------------------------------------------------------------ public API
        public static Mesh Build(bool[,] rock, float tileSize, out List<PezRockDressing> dressing)
        {
            var subs = new List<Vector3>[5];
            for (int i = 0; i < 5; i++) subs[i] = new List<Vector3>();
            var tiers = TierMasks(rock, TierHeights.Length);
            var fields = new Field[tiers.Length];
            for (int k = 0; k < tiers.Length; k++) fields[k] = new Field(tiers[k]);

            float bottom = 0f;
            for (int k = 0; k < tiers.Length; k++)
            {
                if (!Any(tiers[k])) break;
                float top = bottom + TierHeights[k];
                Field next = (k + 1 < tiers.Length && Any(tiers[k + 1])) ? fields[k + 1] : null;
                List<List<Vector2>> polys; List<Vector2[]> segs;
                March(fields[k], out polys, out segs);
                var crust = subs[k];
                foreach (var poly in polys)
                {
                    if (next != null && AllInside(next, poly)) continue;
                    for (int a = 1; a < poly.Count - 1; a++)
                        Tri(crust, V(poly[0], top), V(poly[a + 1], top), V(poly[a], top));
                }
                foreach (var seg in segs)
                {
                    var ring = new Vector3[Rings.Length, 2];
                    for (int r = 0; r < Rings.Length; r++)
                        for (int e = 0; e < 2; e++)
                        {
                            Vector2 p = seg[e];
                            Vector2 n = fields[k].Outward(p.x, p.y);
                            float o = Rings[r].x + (Hash01(p.x, p.y, r + 20 * k) * 2f - 1f) * Rings[r].y;
                            float y = RingY(r, bottom, top, k) + (Hash01(p.x, p.y, r + 20 * k + 10) * 2f - 1f) * Rings[r].z * TierHeights[k];
                            ring[r, e] = new Vector3(p.x + n.x * o, y, p.y + n.y * o);
                        }
                    for (int r = 0; r < Rings.Length - 1; r++)
                    {
                        var list = r == 0 ? crust : r < 3 ? subs[SubFace] : subs[SubTalus];
                        Vector3 a = ring[r, 0], b = ring[r, 1], c = ring[r + 1, 1], d = ring[r + 1, 0];
                        Tri(list, a, c, d); Tri(list, a, b, c);
                    }
                }
                bottom = top;
            }

            dressing = Dressing(tiers, fields, tileSize);

            var mesh = new Mesh { name = "PezMassif", indexFormat = IndexFormat.UInt32 };
            var verts = new List<Vector3>();
            foreach (var s in subs) foreach (var v in s) verts.Add(v * tileSize);
            mesh.SetVertices(verts);
            mesh.subMeshCount = subs.Length;
            int start = 0;
            for (int i = 0; i < subs.Length; i++)
            {
                var idx = new int[subs[i].Count];
                for (int t = 0; t < idx.Length; t++) idx[t] = start + t;
                mesh.SetTriangles(idx, i);
                start += idx.Length;
            }
            mesh.RecalculateNormals();   // vertices are unshared, so this gives flat facets
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------ tiers
        static bool[][,] TierMasks(bool[,] rock, int count)
        {
            int W = rock.GetLength(0), D = rock.GetLength(1);
            var dist = ChamferDistance(rock);
            var outp = new bool[count][,];
            outp[0] = rock;
            for (int k = 1; k < count; k++)
            {
                var m = new bool[W, D];
                for (int x = 0; x < W; x++)
                    for (int z = 0; z < D; z++)
                    {
                        if (!rock[x, z] || !outp[k - 1][x, z]) continue;
                        float n = (Mathf.Sin(x * 0.9f + k * 1.7f) + Mathf.Sin(z * 0.7f - k * 2.3f) + Mathf.Sin((x + z) * 0.45f + k)) / 3f;
                        m[x, z] = dist[x, z] > k * TierStep + n * TierWobble;
                    }
                outp[k] = m;
            }
            return outp;
        }

        static float[,] ChamferDistance(bool[,] g)
        {
            int W = g.GetLength(0), D = g.GetLength(1);
            var d = new float[W, D];
            for (int x = 0; x < W; x++) for (int z = 0; z < D; z++) d[x, z] = g[x, z] ? 1e9f : 0f;
            int[,] fwd = { { -1, 0 }, { 0, -1 }, { -1, -1 }, { 1, -1 } };
            int[,] bwd = { { 1, 0 }, { 0, 1 }, { 1, 1 }, { -1, 1 } };
            float[] w = { 1f, 1f, 1.4f, 1.4f };
            for (int it = 0; it < 2; it++)
            {
                for (int x = 0; x < W; x++)
                    for (int z = 0; z < D; z++) Relax(g, d, x, z, fwd, w);
                for (int x = W - 1; x >= 0; x--)
                    for (int z = D - 1; z >= 0; z--) Relax(g, d, x, z, bwd, w);
            }
            return d;
        }

        static void Relax(bool[,] g, float[,] d, int x, int z, int[,] nb, float[] w)
        {
            if (!g[x, z]) return;
            int W = g.GetLength(0), D = g.GetLength(1);
            for (int i = 0; i < 4; i++)
            {
                int X = x + nb[i, 0], Z = z + nb[i, 1];
                float nd = ((X >= 0 && X < W && Z >= 0 && Z < D) ? d[X, Z] : 0f) + w[i];
                if (nd < d[x, z]) d[x, z] = nd;
            }
        }

        // ------------------------------------------------------------------ field + marching squares
        class Field
        {
            public readonly int W, D;
            readonly float[,] v;   // v[i+1, j+1] = sample at cell (i, j) centre, i in [-1, W]
            public Field(bool[,] g)
            {
                W = g.GetLength(0); D = g.GetLength(1);
                v = new float[W + 2, D + 2];
                for (int i = -1; i <= W; i++)
                    for (int j = -1; j <= D; j++)
                        v[i + 1, j + 1] = 0.6f * G(g, i, j) + 0.1f * (G(g, i - 1, j) + G(g, i + 1, j) + G(g, i, j - 1) + G(g, i, j + 1));
            }
            static float G(bool[,] g, int i, int j) =>
                (i >= 0 && j >= 0 && i < g.GetLength(0) && j < g.GetLength(1) && g[i, j]) ? 1f : 0f;
            public float S(int i, int j) => (i >= -1 && i <= W && j >= -1 && j <= D) ? v[i + 1, j + 1] : 0f;
            public float At(float x, float z)
            {
                float fx = x - 0.5f, fz = z - 0.5f;
                int i = Mathf.FloorToInt(fx), j = Mathf.FloorToInt(fz);
                float tx = fx - i, tz = fz - j;
                return S(i, j) * (1 - tx) * (1 - tz) + S(i + 1, j) * tx * (1 - tz) + S(i + 1, j + 1) * tx * tz + S(i, j + 1) * (1 - tx) * tz;
            }
            public Vector2 Outward(float x, float z)
            {
                const float e = 0.05f;
                float gx = At(x + e, z) - At(x - e, z), gz = At(x, z + e) - At(x, z - e);
                float L = Mathf.Sqrt(gx * gx + gz * gz);
                if (L < 1e-6f) L = 1f;
                return new Vector2(-gx / L, -gz / L);
            }
        }

        static void March(Field f, out List<List<Vector2>> polys, out List<Vector2[]> segs)
        {
            polys = new List<List<Vector2>>(); segs = new List<Vector2[]>();
            var P = new Vector2[4]; var Vv = new float[4]; var ins = new bool[4];
            for (int i = -1; i < f.W; i++)
                for (int j = -1; j < f.D; j++)
                {
                    P[0] = new Vector2(i + .5f, j + .5f); P[1] = new Vector2(i + 1.5f, j + .5f);
                    P[2] = new Vector2(i + 1.5f, j + 1.5f); P[3] = new Vector2(i + .5f, j + 1.5f);
                    Vv[0] = f.S(i, j); Vv[1] = f.S(i + 1, j); Vv[2] = f.S(i + 1, j + 1); Vv[3] = f.S(i, j + 1);
                    int n = 0;
                    for (int k = 0; k < 4; k++) { ins[k] = Vv[k] > Iso; if (ins[k]) n++; }
                    if (n == 0) continue;
                    bool saddle = n == 2 && ins[0] == ins[2];
                    if (saddle && (Vv[0] + Vv[1] + Vv[2] + Vv[3]) / 4f <= Iso)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            if (!ins[k]) continue;
                            var poly = new List<Vector2> { P[k], Cross(P, Vv, k), Cross(P, Vv, (k + 3) % 4) };
                            polys.Add(poly); segs.Add(new[] { poly[1], poly[2] });
                        }
                        continue;
                    }
                    var pl = new List<Vector2>(); var kinds = new List<bool>();
                    for (int k = 0; k < 4; k++)
                    {
                        if (ins[k]) { pl.Add(P[k]); kinds.Add(false); }
                        if (ins[k] != ins[(k + 1) % 4]) { pl.Add(Cross(P, Vv, k)); kinds.Add(true); }
                    }
                    polys.Add(pl);
                    if (n < 4)
                        for (int a = 0; a < pl.Count; a++)
                        {
                            int b = (a + 1) % pl.Count;
                            if (kinds[a] && kinds[b]) segs.Add(new[] { pl[a], pl[b] });
                        }
                }
        }

        static Vector2 Cross(Vector2[] P, float[] V, int k)
        {
            int a = k, b = (k + 1) % 4;
            float t = (Iso - V[a]) / (V[b] - V[a]);
            return P[a] + (P[b] - P[a]) * t;
        }

        static bool AllInside(Field f, List<Vector2> poly)
        {
            foreach (var p in poly) if (f.At(p.x, p.y) <= Iso + 0.15f) return false;
            return true;
        }

        // ------------------------------------------------------------------ dressing
        static List<PezRockDressing> Dressing(bool[][,] tiers, Field[] fields, float tile)
        {
            var outp = new List<PezRockDressing>();
            List<List<Vector2>> polys; List<Vector2[]> segs;
            March(fields[0], out polys, out segs);
            float[] radii = { 0.14f, 0.2f, 0.28f };
            foreach (var s in segs)
            {
                float mx = (s[0].x + s[1].x) / 2f, mz = (s[0].y + s[1].y) / 2f;
                if (Hash01(mx, mz, 99) > 0.30f) continue;
                var n = fields[0].Outward(mx, mz);
                float o = 0.5f + Hash01(mx, mz, 98) * 0.3f;
                float r = radii[(int)(Hash01(mx, mz, 97) * 2.999f)];
                int variant = (int)(Hash01(mx, mz, 96) * 9f);
                outp.Add(Make(new Vector3(mx + n.x * o, 0f, mz + n.y * o) * tile, r * tile, variant, 0, Hash01(mx, mz, 95) * 360f));
            }
            float bottom = 0f;
            for (int k = 0; k < tiers.Length; k++)
            {
                var g = tiers[k];
                if (!Any(g)) break;
                float top = bottom + TierHeights[k];
                var nxt = k + 1 < tiers.Length ? tiers[k + 1] : null;
                for (int x = 0; x < g.GetLength(0); x++)
                    for (int z = 0; z < g.GetLength(1); z++)
                    {
                        if (!g[x, z] || (nxt != null && nxt[x, z]) || Hash01(x, z, 50 + k) >= 0.16f) continue;
                        float jx = Hash01(x, z, 51) - .5f, jz = Hash01(x, z, 52) - .5f;
                        float r = 0.09f + 0.06f * Hash01(x, z, 53);
                        outp.Add(Make(new Vector3(x + .5f + jx * .5f, top - 0.02f, z + .5f + jz * .5f) * tile, r * tile, (int)(Hash01(x, z, 54) * 9f), k, Hash01(x, z, 55) * 360f));
                    }
                bottom = top;
            }
            return outp;
        }

        static PezRockDressing Make(Vector3 pos, float radius, int variant, int tier, float yaw)
        {
            return new PezRockDressing { position = pos, radius = radius, variant = variant, tier = tier, yaw = yaw, scale = radius / AuthoredRadius[variant / 3] };
        }

        // ------------------------------------------------------------------ helpers
        static float RingY(int r, float bottom, float top, int tier)
        {
            switch (r)
            {
                case 0: return top;
                case 1: return top - 0.09f;
                case 2: return bottom + (top - bottom) * 0.58f;
                case 3: return bottom + (tier == 0 ? 0.14f : 0.06f);
                default: return bottom - 0.03f;
            }
        }

        static Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);
        static void Tri(List<Vector3> l, Vector3 a, Vector3 b, Vector3 c) { l.Add(a); l.Add(b); l.Add(c); }
        static bool Any(bool[,] g) { foreach (var b in g) if (b) return true; return false; }

        /// <summary>Position hash shared with terrain_kit.py so both produce the same rocks.</summary>
        public static float Hash01(float x, float z, int k)
        {
            long xi = (long)System.Math.Round(x * 1000.0), zi = (long)System.Math.Round(z * 1000.0);
            ulong h = (ulong)((xi * 73856093L) ^ (zi * 19349663L) ^ ((long)k * 83492791L)) & 0xFFFFFFFFUL;
            h = ((((h >> 16) ^ h) * 0x45d9f3bUL)) & 0xFFFFFFFFUL;
            h = ((((h >> 16) ^ h) * 0x45d9f3bUL)) & 0xFFFFFFFFUL;
            h = (h >> 16) ^ h;
            return (h & 0xFFFF) / 65535f;
        }
    }
}
