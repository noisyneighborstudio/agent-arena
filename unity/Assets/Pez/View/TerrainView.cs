using System.Collections.Generic;
using System.Linq;
using Pez.Sim;
using UnityEngine;
using Terrain = Pez.Sim.Terrain;

namespace Pez.View
{
    /// <summary>Procedural terrain mesh, water, rock detail, ore crystals and the fog-of-war overlay.</summary>
    public class TerrainView : MonoBehaviour
    {
        const int Sub = 3; // mesh vertices per tile edge
        Map map;
        Mesh fogMesh;
        Color[] fogColors;
        readonly List<(int idx, Transform t, float baseScale)> crystals = new List<(int, Transform, float)>();
        readonly List<(int idx, GameObject go)> decor = new List<(int, GameObject)>();
        bool[] explored; // null = everything revealed (spectator)
        float nextOreUpdate, nextFieldCheck, nextClusters;

        // Ore fields' ground stains (fix 12): each field's tiles and the ground vertices its pad tints, with what's
        // needed to re-dress just those vertices as the stain fades (colour before the pad, pad weight, shore blend and
        // broad variation). Stain strength 1 while the field has ore; 0.24 (a 0.08 "spent ground" scar) once it's empty.
        class OreField
        {
            public int Type;
            public int[] Tiles;
            public readonly List<int> Verts = new List<int>();
            public readonly List<Color> Pre = new List<Color>();
            public readonly List<Vector4> Dress = new List<Vector4>(); // pad weight, shore blend, macro variation
            public float K = 1f, Target = 1f;
        }
        readonly List<OreField> fields = new List<OreField>();
        readonly List<(int cx, int cy, int w, int h, Mesh mesh, Color[] colors)> chunks = new List<(int, int, int, int, Mesh, Color[])>();
        int groundVW, chunkStep, chunksX;
        const float SpentStain = 0.08f / OrePadAlpha;

        // Sugar Flats: biscuit ground, licorice cliffs, cola water (art pack palette).
        public static readonly Color GrassA = PezPalette.TerrainBiscuitGround;
        public static readonly Color GrassB = PezPalette.TerrainBiscuitLight;
        public static readonly Color Dirt = PezPalette.TerrainBiscuitDark;
        public static readonly Color OreGround = PezPalette.TerrainBiscuitDark;
        public static readonly Color RockC = PezPalette.TerrainLicoriceCliffTop;
        public static readonly Color Seabed = PezPalette.TerrainColaWater; // dark under the cola so lakes don't read as tan holes
        // Shroud and fog in licorice (vertex colours bypass the sRGB conversion, so convert once here).
        static Color? fogTint;
        static Color FogTint => fogTint ??= ((Color)PezPalette.MaterialsLicorice).linear;

        public void Build(Map m)
        {
            map = m;
            BuildGround();
            BuildWater();
            BuildRocks();
            BuildOre();
            BuildFog();
        }

        float TileHeight(int x, int y)
        {
            x = Mathf.Clamp(x, 0, map.W - 1); y = Mathf.Clamp(y, 0, map.H - 1);
            var t = map.TerrainAt(x, y);
            return t == Terrain.Water ? -0.55f : 0f; // rock height comes from the cliff massifs, not the ground mesh
        }

        /// <summary>Flat colour of a tile before the per-vertex ground dressing (blotches, ore pads, shore).</summary>
        Color TileColor(int x, int y)
        {
            x = Mathf.Clamp(x, 0, map.W - 1); y = Mathf.Clamp(y, 0, map.H - 1);
            switch (map.Tiles[map.Idx(x, y)])
            {
                case Terrain.Water: return Seabed;
                // Dirt patches are purely cosmetic: keep them within the art pack's 8% "quiet ground" rule so they
                // don't read as holes or shadows. Rock tiles sit under the massifs and use the ground colour.
                case Terrain.Dirt: return GrassA * 0.975f;
                default: return GrassA;
            }
        }

        // ---- Ground dressing (05_Terrain "quiet ground", hero_offaxis): large soft light and dark blotches within
        // the 8% value budget, a soft disc of ore colour under each ore field, and a light shore band around lakes.
        // The Pez/Ground shader adds the cream speckles and the faint diamond tile grid on top.
        const float BlotchStrength = 0.04f; // +-4% value: light and dark together stay inside 8%
        const float MacroStrength = 0.035f; // broad value/warmth variation (quality pass), +-3.5%

        /// <summary>Slope of a smooth dune height field, baked per ground vertex; Pez/Ground shades it against the sun.</summary>
        static Vector2 DuneSlope(float x, float y)
        {
            float H(float a, float b) => Mathf.PerlinNoise(a * 0.32f + 5.1f, b * 0.32f + 2.7f) * 0.6f + Mathf.PerlinNoise(a * 0.7f + 17.3f, b * 0.7f + 8.9f) * 0.4f;
            const float e = 0.1f;
            float h0 = H(x, y);
            return new Vector2(H(x + e, y) - h0, H(x, y + e) - h0) / e;
        }
        const float OrePadAlpha = 0.34f, ShoreWidth = 0.7f;

        /// <summary>GLSL-style smoothstep (Mathf.SmoothStep interpolates between its first two arguments instead).</summary>
        public static float Smooth(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Signed value offset per ground vertex from overlapping soft ellipses (+ light, - dark).</summary>
        float[] Blotches(int vw, int vh)
        {
            var v = new float[vw * vh];
            var rng = new System.Random(map.W * 7919 + map.H * 31 + 5);
            int count = Mathf.Max(6, map.W * map.H / 55);
            for (int n = 0; n < count; n++)
            {
                float cx = (float)rng.NextDouble() * map.W, cy = (float)rng.NextDouble() * map.H;
                float ra = 3.5f + (float)rng.NextDouble() * 7f, rb = ra * (0.45f + (float)rng.NextDouble() * 0.45f);
                float ang = (float)rng.NextDouble() * Mathf.PI, ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                float amp = (rng.Next(2) == 0 ? 1f : -1f) * BlotchStrength * (0.55f + (float)rng.NextDouble() * 0.45f);
                int x0 = Mathf.Max(0, Mathf.FloorToInt((cx - ra) * Sub)), x1 = Mathf.Min(vw - 1, Mathf.CeilToInt((cx + ra) * Sub));
                int y0 = Mathf.Max(0, Mathf.FloorToInt((cy - ra) * Sub)), y1 = Mathf.Min(vh - 1, Mathf.CeilToInt((cy + ra) * Sub));
                for (int vy = y0; vy <= y1; vy++)
                    for (int vx = x0; vx <= x1; vx++)
                    {
                        float dx = vx / (float)Sub - cx, dy = vy / (float)Sub - cy;
                        float u = (dx * ca + dy * sa) / ra, w = (-dx * sa + dy * ca) / rb;
                        float d = u * u + w * w;
                        if (d >= 1f) continue;
                        // A soft but definite edge: the hero's blotches read as shapes, not as noise.
                        v[vy * vw + vx] += amp * (1f - Smooth(0.55f, 1f, d));
                    }
            }
            for (int i = 0; i < v.Length; i++) v[i] = Mathf.Clamp(v[i], -BlotchStrength, BlotchStrength);
            return v;
        }

        /// <summary>Ore fields as soft ellipses: (weight, ore type, field) per ground vertex.</summary>
        (float w, int type, int field)[] OrePads(int vw, int vh)
        {
            var pads = new (float, int, int)[vw * vh];
            fields.Clear();
            var seen = new bool[map.W * map.H];
            var field = new List<int>();
            var stack = new Stack<int>();
            for (int start = 0; start < map.Ore.Length; start++)
            {
                if (map.Ore[start] <= 0 || seen[start]) continue;
                // One field: ore tiles of the same type within two tiles of each other.
                field.Clear();
                int type = map.OreType[start];
                stack.Push(start); seen[start] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop(); field.Add(i);
                    int x = i % map.W, y = i / map.W;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (!map.InBounds(nx, ny)) continue;
                            int j = map.Idx(nx, ny);
                            if (seen[j] || map.Ore[j] <= 0 || map.OreType[j] != type) continue;
                            seen[j] = true; stack.Push(j);
                        }
                }
                int fieldId = fields.Count;
                fields.Add(new OreField { Type = type, Tiles = field.ToArray() });
                // Fit an ellipse to the field (mean and covariance of its tile centres), padded past the clusters.
                float mx = 0, my = 0;
                foreach (int i in field) { mx += i % map.W + 0.5f; my += i / map.W + 0.5f; }
                mx /= field.Count; my /= field.Count;
                float sxx = 0, syy = 0, sxy = 0;
                foreach (int i in field)
                {
                    float dx = i % map.W + 0.5f - mx, dy = i / map.W + 0.5f - my;
                    sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
                }
                sxx /= field.Count; syy /= field.Count; sxy /= field.Count;
                float tr = sxx + syy, det = sxx * syy - sxy * sxy, disc = Mathf.Sqrt(Mathf.Max(0, tr * tr / 4 - det));
                float l1 = tr / 2 + disc, l2 = Mathf.Max(0, tr / 2 - disc);
                float ang = 0.5f * Mathf.Atan2(2 * sxy, sxx - syy), ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                float ra = 2f * Mathf.Sqrt(l1) + 1.2f, rb = 2f * Mathf.Sqrt(l2) + 1.2f;
                rb = Mathf.Max(rb, ra * 0.55f); // keep it a disc-ish pad, never a sliver
                int x0 = Mathf.Max(0, Mathf.FloorToInt((mx - ra) * Sub)), x1 = Mathf.Min(vw - 1, Mathf.CeilToInt((mx + ra) * Sub));
                int y0 = Mathf.Max(0, Mathf.FloorToInt((my - ra) * Sub)), y1 = Mathf.Min(vh - 1, Mathf.CeilToInt((my + ra) * Sub));
                for (int vy = y0; vy <= y1; vy++)
                    for (int vx = x0; vx <= x1; vx++)
                    {
                        float dx = vx / (float)Sub - mx, dy = vy / (float)Sub - my;
                        float u = (dx * ca + dy * sa) / ra, w = (-dx * sa + dy * ca) / rb;
                        float d = Mathf.Sqrt(u * u + w * w);
                        if (d >= 1f) continue;
                        float a = 1f - Smooth(0.72f, 1f, d);
                        int k = vy * vw + vx;
                        if (a > pads[k].Item1) pads[k] = (a, type, fieldId);
                    }
            }
            return pads;
        }

        /// <summary>Distance from a ground vertex to the nearest water tile (capped at 2 tiles).</summary>
        float WaterDistance(float fx, float fy)
        {
            int tx = Mathf.FloorToInt(fx), ty = Mathf.FloorToInt(fy);
            float best = 2f;
            for (int y = ty - 2; y <= ty + 2; y++)
                for (int x = tx - 2; x <= tx + 2; x++)
                {
                    if (!map.InBounds(x, y) || map.TerrainAt(x, y) != Terrain.Water) continue;
                    float dx = Mathf.Max(x - fx, 0, fx - (x + 1)), dy = Mathf.Max(y - fy, 0, fy - (y + 1));
                    best = Mathf.Min(best, Mathf.Sqrt(dx * dx + dy * dy));
                }
            return best;
        }

        void BuildGround()
        {
            int vw = map.W * Sub + 1, vh = map.H * Sub + 1;
            var verts = new Vector3[vw * vh];
            var cols = new Color[vw * vh];
            var blotch = Blotches(vw, vh);
            var dunes = new Vector2[vw * vh];
            var pads = OrePads(vw, vh);
            for (int vy = 0; vy < vh; vy++)
                for (int vx = 0; vx < vw; vx++)
                {
                    float fx = vx / (float)Sub, fy = vy / (float)Sub;
                    // Bilinear blend of the four nearest tile centres for smooth height and colour.
                    float gx = fx - 0.5f, gy = fy - 0.5f;
                    int x0 = Mathf.FloorToInt(gx), y0 = Mathf.FloorToInt(gy);
                    float tx = gx - x0, ty = gy - y0;
                    float h = Mathf.Lerp(Mathf.Lerp(TileHeight(x0, y0), TileHeight(x0 + 1, y0), tx), Mathf.Lerp(TileHeight(x0, y0 + 1), TileHeight(x0 + 1, y0 + 1), tx), ty);
                    var c = Color.Lerp(Color.Lerp(TileColor(x0, y0), TileColor(x0 + 1, y0), tx), Color.Lerp(TileColor(x0, y0 + 1), TileColor(x0 + 1, y0 + 1), tx), ty);
                    int i = vy * vw + vx;
                    float wd = WaterDistance(fx, fy);
                    // Broad variation (quality pass): a little lighter/darker and warmer/cooler every few tiles.
                    float macro = Mathf.PerlinNoise(fx * 0.11f + 3.7f, fy * 0.11f + 1.3f) * 0.65f + Mathf.PerlinNoise(fx * 0.27f + 9.1f, fy * 0.27f + 4.4f) * 0.35f - 0.5f;
                    if (wd > 0f)
                    {
                        c *= 1f + blotch[i];
                        // Cola lakes get a light shore band so they read as liquid, not as holes in the ground.
                        float shore = wd < ShoreWidth + 0.15f ? 1f - Smooth(ShoreWidth - 0.1f, ShoreWidth + 0.15f, wd) : 0f;
                        if (pads[i].w > 0f)
                        {
                            var f = fields[pads[i].field];
                            f.Verts.Add(i); f.Pre.Add(c); f.Dress.Add(new Vector4(pads[i].w, shore, macro, 0f));
                        }
                        c = DressGround(c, pads[i].w, pads[i].type, 1f, shore, macro);
                    }
                    else c = DressGround(c, 0f, 0, 0f, 0f, macro);
                    // Rocks get craggy noise; open ground stays near y=0 so picking with a flat plane is accurate.
                    if (h > -0.05f) h += (Mathf.PerlinNoise(fx * 0.8f + 11, fy * 0.8f + 5) - 0.5f) * 0.05f;
                    dunes[i] = DuneSlope(fx, fy);
                    verts[i] = new Vector3(fx, h, fy);
                    cols[i] = c.linear; // vertex colours aren't converted in linear colour space
                }
            // Normals over the whole field first, so chunk borders shade seamlessly.
            var normals = GroundNormals(verts, vw, vh);
            // Pez/Ground (Resources/PezShaders): cream speckles and the faint diamond grid over the dressed vertex colours.
            var groundShader = Resources.Load<Shader>("PezShaders/PezGround");
            var groundMat = groundShader != null ? new Material(groundShader) : new Material(Mats.Terrain());
            if (groundShader == null) groundMat.SetFloat("_DetailStrength", 0.035f);
            groundMat.SetFloat("_GridStrength", 0.06f); // the art pack's tile grid is a faint 5-7% line, inside the 8% quiet-ground budget
            groundMat.SetFloat("_SpeckDensity", 0.16f); // sparse cream sugar grains, as in hero_offaxis
            groundMat.SetFloat("_SpeckStrength", 0.3f);
            const int ChunkTiles = 24;
            int step = ChunkTiles * Sub;
            // The ground in square chunks, so every camera (the main view and each player stream) draws only the part it
            // sees: an 8-player arena grows to 288 tiles a side, 1.5M triangles as one mesh. It casts no shadows (it's
            // flat; casting only cost a shadow-map pass over the whole field for every camera).
            groundVW = vw; chunkStep = step; chunksX = (vw - 2) / step + 1;
            var root = new GameObject("Ground").transform;
            root.SetParent(transform, false);
            for (int cy = 0; cy < vh - 1; cy += step)
                for (int cx = 0; cx < vw - 1; cx += step)
                {
                    int w = Mathf.Min(step, vw - 1 - cx) + 1, h = Mathf.Min(step, vh - 1 - cy) + 1;
                    var cv = new Vector3[w * h];
                    var cn = new Vector3[w * h];
                    var cc = new Color[w * h];
                    var cd = new Vector2[w * h];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int src = (cy + y) * vw + cx + x, dst = y * w + x;
                            cv[dst] = verts[src]; cn[dst] = normals[src]; cc[dst] = cols[src]; cd[dst] = dunes[src];
                        }
                    var ct = new int[(w - 1) * (h - 1) * 6];
                    int k = 0;
                    for (int y = 0; y < h - 1; y++)
                        for (int x = 0; x < w - 1; x++)
                        {
                            int a = y * w + x, b = a + 1, c = a + w, d = c + 1;
                            ct[k++] = a; ct[k++] = c; ct[k++] = b;
                            ct[k++] = b; ct[k++] = c; ct[k++] = d;
                        }
                    var mesh = new Mesh { name = "terrain_" + cx / step + "_" + cy / step };
                    chunks.Add((cx, cy, w, h, mesh, cc));
                    mesh.vertices = cv; mesh.normals = cn; mesh.colors = cc; mesh.triangles = ct;
                    mesh.SetUVs(1, cd); // dune slope, for Pez/Ground's sun-lit relief
                    mesh.RecalculateBounds();
                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(root, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = groundMat;
                    r.receiveShadows = true;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

            // Skirt: a big dark plane under the map so the edges don't float in the void. Warm licorice like the art
            // pack's map backdrop (#2A2420), not an off-palette olive.
            var skirt = Models.Part(transform, PrimitiveType.Plane, new Vector3(map.W / 2f, -0.6f, map.H / 2f), new Vector3(map.W / 2f, 1, map.H / 2f), Mats.Lit(new Color32(42, 36, 32, 255), 0, 0));
            skirt.name = "Skirt";
        }

        /// <summary>The ground colour after the ore pad (at stain strength k), the shore band and the broad variation.</summary>
        static Color DressGround(Color c, float padW, int type, float k, float shore, float macro)
        {
            if (padW > 0f) c = Color.Lerp(c, WorldView.OreColors[type], padW * OrePadAlpha * k);
            if (shore > 0f) c = Color.Lerp(c, PezPalette.TerrainShore, shore);
            c.r *= 1f + macro * MacroStrength * 2f * 1.03f; c.g *= 1f + macro * MacroStrength * 2f; c.b *= 1f + macro * MacroStrength * 2f * 0.96f;
            return c;
        }

        /// <summary>
        /// Mined-out fields fade (fix 12): every 2 s each field's ore is totalled; an empty field's stain fades over 3 s
        /// to a faint "spent ground" scar, and comes back if the ore regrows. Only that field's ground vertices are
        /// rewritten, in the chunks they're in.
        /// </summary>
        void UpdateFields()
        {
            bool fading = false;
            foreach (var f in fields)
            {
                if (Time.time >= nextFieldCheck)
                {
                    int ore = 0;
                    foreach (int t in f.Tiles) ore += map.Ore[t];
                    f.Target = ore > 0 ? 1f : SpentStain;
                }
                if (Mathf.Approximately(f.K, f.Target)) continue;
                f.K = Mathf.MoveTowards(f.K, f.Target, 0.25f / 3f);
                fading = true;
                for (int n = 0; n < f.Verts.Count; n++)
                {
                    var d = f.Dress[n];
                    var c = DressGround(f.Pre[n], d.x, f.Type, f.K, d.y, d.z).linear;
                    int i = f.Verts[n], vx = i % groundVW, vy = i / groundVW;
                    SetChunkColor(vx, vy, c);
                }
            }
            if (Time.time >= nextFieldCheck) nextFieldCheck = Time.time + 2f;
            if (fading) foreach (var ch in chunks) if (ch.mesh != null && dirty.Remove(ch.mesh)) ch.mesh.colors = ch.colors;
        }

        readonly HashSet<Mesh> dirty = new HashSet<Mesh>();

        void SetChunkColor(int vx, int vy, Color c)
        {
            // A vertex on a chunk border is in two (or four) chunks.
            for (int ox = 0; ox < 2; ox++)
                for (int oy = 0; oy < 2; oy++)
                {
                    int cx = (vx / chunkStep - ox) * chunkStep, cy = (vy / chunkStep - oy) * chunkStep;
                    if (cx < 0 || cy < 0) continue;
                    int k = (cy / chunkStep) * chunksX + cx / chunkStep; // chunks are built row by row
                    if (k >= chunks.Count) continue;
                    var ch = chunks[k];
                    int lx = vx - cx, ly = vy - cy;
                    if (lx >= ch.w || ly >= ch.h) continue;
                    ch.colors[ly * ch.w + lx] = c;
                    dirty.Add(ch.mesh);
                }
        }

        /// <summary>Smooth vertex normals of the ground grid (summed face normals, as Mesh.RecalculateNormals).</summary>
        static Vector3[] GroundNormals(Vector3[] v, int vw, int vh)
        {
            var n = new Vector3[v.Length];
            for (int y = 0; y < vh - 1; y++)
                for (int x = 0; x < vw - 1; x++)
                {
                    int a = y * vw + x, b = a + 1, c = a + vw, d = c + 1;
                    var f1 = Vector3.Cross(v[c] - v[a], v[b] - v[a]).normalized;
                    var f2 = Vector3.Cross(v[c] - v[b], v[d] - v[b]).normalized;
                    n[a] += f1; n[c] += f1; n[b] += f1;
                    n[b] += f2; n[c] += f2; n[d] += f2;
                }
            for (int i = 0; i < n.Length; i++) n[i] = n[i].normalized;
            return n;
        }

        void BuildWater()
        {
            var water = new Material(Mats.Water());
            // Cola #3A2218, glossy and near-opaque: the art pack's lakes are the darkest, shiniest thing on the ground.
            var cola = (Color)PezPalette.TerrainColaWater;
            water.SetColor("_Color", new Color(cola.r * 1.25f, cola.g * 1.25f, cola.b * 1.25f, 0.9f));
            water.SetColor("_Deep", new Color(cola.r * 0.6f, cola.g * 0.6f, cola.b * 0.6f, 0.97f));
            var t = Models.Part(transform, PrimitiveType.Plane, new Vector3(map.W / 2f, -0.18f, map.H / 2f), new Vector3(map.W / 10f, 1, map.H / 10f), water);
            t.name = "Water";
            t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void BuildRocks()
        {
            var rng = new System.Random(map.W * 31 + map.H);
            var parent = new GameObject("Rocks").transform;
            parent.SetParent(transform, false);
            // Tiered licorice massifs built from the blocked grid: the dark rim is exactly where pathing stops.
            var rockGrid = new bool[map.W, map.H];
            for (int y = 0; y < map.H; y++)
                for (int x = 0; x < map.W; x++)
                    rockGrid[x, y] = map.TerrainAt(x, y) == Terrain.Rock;
            var cliffsGo = new GameObject("Cliffs", typeof(MeshFilter), typeof(MeshRenderer));
            cliffsGo.transform.SetParent(transform, false);
            var cliffs = cliffsGo.AddComponent<PezCliffs>();
            // Pez/Model (Look.Model): the massifs get the same crease highlight and grime as the buildings.
            cliffs.materials = new[]
            {
                Look.Model(Hex("7A604C"), 0.08f, 0, 0.30f), Look.Model(Hex("8E7259"), 0.08f, 0, 0.30f), Look.Model(Hex("A58A6C"), 0.08f, 0, 0.30f), // crust tiers
                Look.Model(Hex("2E2629"), 0.32f, 0, 0.40f),  // licorice face
                Look.Model(Hex("4A3D3A"), 0.12f, 0, 0.30f),  // talus
            };
            string[] boulders = { "s1", "s2", "s3", "m1", "m2", "m3", "l1", "l2", "l3" };
            cliffs.boulderPrefabs = boulders.Select(b => Resources.Load<GameObject>("PezModels/terrain/boulder_" + b)).ToArray();
            cliffs.Build(rockGrid);
            Models.FlatShade(cliffsGo); // crease data for the massif and the boulders, and the boulders' PBR materials
            foreach (var r in cliffsGo.GetComponentsInChildren<Renderer>())
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }
            // Scattered trees on open grass far from the bases add life to the field.
            var trunk = Mats.Lit(PezPalette.MaterialsKraft, 0.1f, 0);
            var leaves = new[] { Mats.Lit(PezPalette.TerrainCottonCandyTree, 0.15f, 0), Mats.Lit((Color)PezPalette.TerrainCottonCandyTree * 0.85f, 0.15f, 0) }; // cotton candy
            for (int i = 0; i < 140; i++)
            {
                int x = rng.Next(map.W), y = rng.Next(map.H);
                // Only on the map rim, so trees never sit on playable tiles.
                bool rim = x < 2 || y < 2 || x >= map.W - 2 || y >= map.H - 2;
                if (!rim || map.TerrainAt(x, y) != Terrain.Grass) continue;
                var p = new Vector3(x + (float)rng.NextDouble(), 0, y + (float)rng.NextDouble());
                float s = 0.7f + (float)rng.NextDouble() * 0.6f;
                var tree = new GameObject("tree").transform;
                tree.SetParent(parent, false);
                Models.Part(tree, PrimitiveType.Cylinder, p + Vector3.up * 0.3f * s, new Vector3(0.1f, 0.3f, 0.1f) * s, trunk);
                Models.Part(tree, PrimitiveType.Sphere, p + Vector3.up * 0.85f * s, new Vector3(0.6f, 0.8f, 0.6f) * s, leaves[rng.Next(2)]);
                decor.Add((map.Idx(x, y), tree.gameObject));
            }
        }

        readonly List<(int idx, PezEmerge e, int start, int shown)> oreTiles = new List<(int, PezEmerge, int, int)>();

        void BuildOre()
        {
            // Art-pack ore tiles: five clusters per tile that sink as the tile is mined out.
            var oreModels = new GameObject[4];
            for (int k = 0; k < 4; k++) oreModels[k] = Resources.Load<GameObject>("PezModels/ores/" + Defs.Ores[k]);
            if (oreModels.All(m => m != null))
            {
                var parent0 = new GameObject("Ore").transform;
                parent0.SetParent(transform, false);
                var rng0 = new System.Random(99);
                for (int i = 0; i < map.Ore.Length; i++)
                {
                    if (map.Ore[i] <= 0) continue;
                    int x = i % map.W, y = i / map.W;
                    var go = Instantiate(oreModels[map.OreType[i]], parent0, false);
                    Models.FlatShade(go); // faceted bricks and shards, as in the art pack's renders
                    go.transform.localPosition = new Vector3(x + 0.5f, 0, y + 0.5f);
                    go.transform.localRotation = Quaternion.Euler(0, rng0.Next(4) * 90, 0);
                    foreach (var r in go.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    var em = go.AddComponent<PezEmerge>();
                    oreTiles.Add((i, em, map.Ore[i], 5));
                    decor.Add((i, go));
                }
                return;
            }
            var rng = new System.Random(99);
            // Iron and copper are dull metallic rock; crystal and uranium glow.
            Material[][] mats =
            {
                new[] { Mats.Lit(new Color(0.5f, 0.27f, 0.2f), 0.55f, 0.7f), Mats.Lit(new Color(0.38f, 0.22f, 0.18f), 0.5f, 0.8f) },
                new[] { Mats.Lit(new Color(0.85f, 0.5f, 0.22f), 0.7f, 0.9f), Mats.Lit(new Color(0.3f, 0.7f, 0.6f), 0.4f, 0.3f) },
                new[] { Mats.Glow(new Color(0.3f, 0.75f, 1f), 1.2f), Mats.Glow(new Color(0.55f, 0.9f, 1f), 0.9f) },
                new[] { Mats.Glow(new Color(0.45f, 1f, 0.25f), 1.6f), Mats.Glow(new Color(0.3f, 0.85f, 0.15f), 1.2f) },
            };
            var parent = new GameObject("Ore").transform;
            parent.SetParent(transform, false);
            for (int i = 0; i < map.Ore.Length; i++)
            {
                if (map.Ore[i] <= 0) continue;
                int x = i % map.W, y = i / map.W;
                // A cluster of crystals per tile, tallest in the middle.
                int n = 4 + rng.Next(3);
                var c0 = new Vector2(x + 0.3f + (float)rng.NextDouble() * 0.4f, y + 0.3f + (float)rng.NextDouble() * 0.4f);
                for (int k = 0; k < n; k++)
                {
                    float s = (k == 0 ? 0.16f : 0.07f) + (float)rng.NextDouble() * 0.08f;
                    var off = Random2(rng) * (k == 0 ? 0f : 0.35f);
                    var p = new Vector3(c0.x + off.x, s * 0.5f, c0.y + off.y);
                    var t = Models.Part(parent, PrimitiveType.Cube, p, new Vector3(s, s * 2.2f, s), mats[map.OreType[i]][rng.Next(2)], new Vector3(rng.Next(-25, 25), rng.Next(360), rng.Next(-25, 25)));
                    t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    crystals.Add((i, t, s));
                }
            }
        }

        static Vector2 Random2(System.Random rng)
        {
            double a = rng.NextDouble() * System.Math.PI * 2, r = 0.4 + rng.NextDouble() * 0.6;
            return new Vector2((float)(System.Math.Cos(a) * r), (float)(System.Math.Sin(a) * r));
        }

        static Color Hex(string h) => ColorUtility.TryParseHtmlString("#" + h, out var c) ? c : Color.magenta;

        void BuildFog()
        {
            int vw = map.W + 1, vh = map.H + 1;
            var verts = new Vector3[vw * vh];
            fogColors = new Color[vw * vh];
            for (int y = 0; y < vh; y++)
                for (int x = 0; x < vw; x++)
                {
                    // Hug the ground, rising over rock so it doesn't clip.
                    float h = Mathf.Max(Mathf.Max(TileHeight(x - 1, y - 1), TileHeight(x, y - 1)), Mathf.Max(TileHeight(x - 1, y), TileHeight(x, y)));
                    verts[y * vw + x] = new Vector3(x, Mathf.Max(0.12f, h * 1.2f + 0.15f), y);
                }
            var tris = new int[map.W * map.H * 6];
            int k = 0;
            for (int y = 0; y < map.H; y++)
                for (int x = 0; x < map.W; x++)
                {
                    int a = y * vw + x, b = a + 1, c = a + vw, d = c + 1;
                    tris[k++] = a; tris[k++] = c; tris[k++] = b; tris[k++] = b; tris[k++] = c; tris[k++] = d;
                }
            fogMesh = new Mesh { name = "fog" };
            fogMesh.vertices = verts; fogMesh.triangles = tris; fogMesh.colors = fogColors;
            var go = new GameObject("Fog") { layer = MainFogLayer };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = fogMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Mats.Unlit(Color.white);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(false);
        }

        /// <summary>
        /// Shroud (never seen) is near-black and hides decorations; fog (seen before, not in sight now)
        /// is dimmed. team &lt; 0 reveals everything (spectator).
        /// </summary>
        // Layers: the main view's fog, and one fog per team for the per-player live streams.
        public const int MainFogLayer = 28, TeamFogLayerBase = 20;
        public static int TeamFogMask => 0xFF << TeamFogLayerBase;
        readonly Dictionary<int, (Mesh mesh, Color[] colors, float next)> teamFogs = new Dictionary<int, (Mesh, Color[], float)>();

        /// <summary>Keep a team's own fog overlay current (for its live stream). Throttled to 4 updates a second.</summary>
        public void UpdateTeamFog(World w, int team)
        {
            if (fogMesh == null || team < 0 || team > 7) return;
            if (!teamFogs.TryGetValue(team, out var f))
            {
                var mesh = new Mesh { name = "fog" + team, vertices = fogMesh.vertices, triangles = fogMesh.triangles };
                var go = new GameObject("Fog" + team) { layer = TeamFogLayerBase + team };
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = Mats.Unlit(Color.white);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                f = (mesh, new Color[fogColors.Length], 0f);
            }
            if (Time.time < f.next) { teamFogs[team] = f; return; }
            FillFog(w, team, f.colors);
            f.mesh.colors = f.colors;
            teamFogs[team] = (f.mesh, f.colors, Time.time + 0.25f);
        }

        void FillFog(World w, int team, Color[] colors)
        {
            var vis = w.Teams[team].Visible;
            var exp = w.Teams[team].Explored;
            int vw = map.W + 1;
            for (int y = 0; y <= map.H; y++)
                for (int x = 0; x <= map.W; x++)
                {
                    int seen = 0, known = 0, total = 0;
                    for (int dy = -1; dy <= 0; dy++)
                        for (int dx = -1; dx <= 0; dx++)
                        {
                            int tx = x + dx, ty = y + dy;
                            if (!map.InBounds(tx, ty)) continue;
                            total++;
                            if (vis[map.Idx(tx, ty)]) seen++;
                            if (exp[map.Idx(tx, ty)]) known++;
                        }
                    float a = total == 0 ? 0 : (1f - seen / (float)total) * 0.5f + (1f - known / (float)total) * 0.48f;
                    colors[y * vw + x] = new Color(FogTint.r, FogTint.g, FogTint.b, a);
                }
        }

        /// <summary>The main view's fog overlay (only active when the host plays a team), for renders that must not show it.</summary>
        public GameObject MainFog => fogMesh == null ? null : transform.Find("Fog")?.gameObject;

        public void UpdateFog(World w, int team)
        {
            var go = fogMesh == null ? null : transform.Find("Fog")?.gameObject;
            if (go == null) return;
            go.SetActive(team >= 0);
            explored = team >= 0 ? w.Teams[team].Explored : null;
            foreach (var (idx, d) in decor)
            {
                bool on = explored == null || explored[idx];
                if (d.activeSelf != on) d.SetActive(on);
            }
            if (team < 0) return;
            var vis = w.Teams[team].Visible;
            var exp = w.Teams[team].Explored;
            int vw = map.W + 1;
            for (int y = 0; y <= map.H; y++)
                for (int x = 0; x <= map.W; x++)
                {
                    int seen = 0, known = 0, total = 0;
                    for (int dy = -1; dy <= 0; dy++)
                        for (int dx = -1; dx <= 0; dx++)
                        {
                            int tx = x + dx, ty = y + dy;
                            if (!map.InBounds(tx, ty)) continue;
                            total++;
                            if (vis[map.Idx(tx, ty)]) seen++;
                            if (exp[map.Idx(tx, ty)]) known++;
                        }
                    float a = total == 0 ? 0 : (1f - seen / (float)total) * 0.5f + (1f - known / (float)total) * 0.48f;
                    fogColors[y * vw + x] = new Color(FogTint.r, FogTint.g, FogTint.b, a);
                }
            fogMesh.colors = fogColors;
        }

        void Update()
        {
            if (map == null || Time.time < nextOreUpdate) return;
            nextOreUpdate = Time.time + 0.25f;
            UpdateFields();
            if (Time.time < nextClusters) return;
            nextClusters = Time.time + 0.5f;
            for (int n = 0; n < oreTiles.Count; n++)
            {
                var (idx, em, start, shown) = oreTiles[n];
                // Clusters disappear one by one as the tile's ore runs down.
                int want = map.Ore[idx] <= 0 ? 0 : Mathf.Clamp(Mathf.CeilToInt(5f * map.Ore[idx] / start), 1, 5);
                if (want == shown || !em.gameObject.activeInHierarchy) continue; // sinking needs a live object; shrouded tiles catch up later
                for (int c = 0; c < 5; c++) em.SetClusterAmount(c, c < want ? 1f : 0f);
                oreTiles[n] = (idx, em, start, want);
            }
            foreach (var (idx, t, s) in crystals)
            {
                float f = Mathf.Clamp01(map.Ore[idx] / 300f);
                bool on = f > 0.01f && (explored == null || explored[idx]);
                if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
                if (on) t.localScale = new Vector3(s, s * 2.2f, s) * Mathf.Lerp(0.35f, 1f, f);
            }
        }
    }
}
