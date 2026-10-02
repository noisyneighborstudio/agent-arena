using System.Collections.Generic;
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
        float nextOreUpdate;

        public static readonly Color GrassA = new Color(0.27f, 0.42f, 0.16f);
        public static readonly Color GrassB = new Color(0.36f, 0.47f, 0.2f);
        public static readonly Color Dirt = new Color(0.47f, 0.38f, 0.26f);
        public static readonly Color OreGround = new Color(0.45f, 0.32f, 0.2f);
        public static readonly Color RockC = new Color(0.42f, 0.4f, 0.38f);
        public static readonly Color Seabed = new Color(0.25f, 0.27f, 0.2f);

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
            return t == Terrain.Rock ? 0.9f + Mathf.PerlinNoise(x * 0.5f, y * 0.5f) * 0.9f : t == Terrain.Water ? -0.55f : 0f;
        }

        Color TileColor(int x, int y)
        {
            x = Mathf.Clamp(x, 0, map.W - 1); y = Mathf.Clamp(y, 0, map.H - 1);
            int i = map.Idx(x, y);
            if (map.Ore[i] > 0) return Color.Lerp(OreGround, WorldView.OreColors[map.OreType[i]], 0.25f);
            switch (map.Tiles[i])
            {
                case Terrain.Rock: return RockC;
                case Terrain.Water: return Seabed;
                case Terrain.Dirt: return Dirt;
                default: return Color.Lerp(GrassA, GrassB, Mathf.PerlinNoise(x * 0.15f + 3.1f, y * 0.15f + 7.7f));
            }
        }

        void BuildGround()
        {
            int vw = map.W * Sub + 1, vh = map.H * Sub + 1;
            var verts = new Vector3[vw * vh];
            var cols = new Color[vw * vh];
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
                    // Rocks get craggy noise; open ground stays near y=0 so picking with a flat plane is accurate.
                    if (h > 0.05f) h += (Mathf.PerlinNoise(fx * 1.7f, fy * 1.7f) - 0.5f) * 0.5f * h;
                    else h += (Mathf.PerlinNoise(fx * 0.8f + 11, fy * 0.8f + 5) - 0.5f) * 0.05f;
                    int i = vy * vw + vx;
                    verts[i] = new Vector3(fx, h, fy);
                    cols[i] = c.linear; // vertex colours aren't converted in linear colour space
                }
            var tris = new int[(vw - 1) * (vh - 1) * 6];
            int k = 0;
            for (int y = 0; y < vh - 1; y++)
                for (int x = 0; x < vw - 1; x++)
                {
                    int a = y * vw + x, b = a + 1, c = a + vw, d = c + 1;
                    tris[k++] = a; tris[k++] = c; tris[k++] = b;
                    tris[k++] = b; tris[k++] = c; tris[k++] = d;
                }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, name = "terrain" };
            mesh.vertices = verts; mesh.colors = cols; mesh.triangles = tris;
            mesh.RecalculateNormals();
            var go = new GameObject("Ground");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Mats.Terrain();
            r.receiveShadows = true;

            // Skirt: a big dark plane under the map so the edges don't float in the void.
            var skirt = Models.Part(transform, PrimitiveType.Plane, new Vector3(map.W / 2f, -0.6f, map.H / 2f), new Vector3(map.W / 2f, 1, map.H / 2f), Mats.Lit(new Color(0.12f, 0.14f, 0.1f), 0, 0));
            skirt.name = "Skirt";
        }

        void BuildWater()
        {
            var t = Models.Part(transform, PrimitiveType.Plane, new Vector3(map.W / 2f, -0.18f, map.H / 2f), new Vector3(map.W / 10f, 1, map.H / 10f), Mats.Water());
            t.name = "Water";
            t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void BuildRocks()
        {
            var rng = new System.Random(map.W * 31 + map.H);
            var rockMats = new[] { Mats.Lit(new Color(0.38f, 0.36f, 0.34f), 0.15f, 0), Mats.Lit(new Color(0.3f, 0.29f, 0.28f), 0.2f, 0), Mats.Lit(new Color(0.48f, 0.45f, 0.4f), 0.1f, 0) };
            var parent = new GameObject("Rocks").transform;
            parent.SetParent(transform, false);
            for (int y = 0; y < map.H; y++)
                for (int x = 0; x < map.W; x++)
                {
                    if (map.TerrainAt(x, y) != Terrain.Rock || rng.NextDouble() > 0.55) continue;
                    float s = 0.5f + (float)rng.NextDouble() * 0.9f;
                    var p = new Vector3(x + 0.5f + (float)(rng.NextDouble() - 0.5), TileHeight(x, y) * 0.8f, y + 0.5f + (float)(rng.NextDouble() - 0.5));
                    var rock = Models.Part(parent, PrimitiveType.Sphere, p, new Vector3(s, s * (0.6f + (float)rng.NextDouble() * 0.8f), s * 0.9f), rockMats[rng.Next(3)],
                        new Vector3(rng.Next(360), rng.Next(360), rng.Next(360)));
                    decor.Add((map.Idx(x, y), rock.gameObject));
                }
            // Scattered trees on open grass far from the bases add life to the field.
            var trunk = Mats.Lit(new Color(0.3f, 0.2f, 0.12f), 0.1f, 0);
            var leaves = new[] { Mats.Lit(new Color(0.12f, 0.3f, 0.1f), 0.1f, 0), Mats.Lit(new Color(0.18f, 0.36f, 0.12f), 0.1f, 0) };
            for (int i = 0; i < 140; i++)
            {
                int x = rng.Next(map.W), y = rng.Next(map.H);
                // Only on the map rim, so trees never sit on playable tiles.
                bool rim = x < 2 || y < 2 || x >= map.W - 2 || y >= map.H - 2;
                bool nearRock = map.TerrainAt(x, y) == Terrain.Rock;
                if (!rim && !nearRock) continue;
                var p = new Vector3(x + (float)rng.NextDouble(), nearRock ? TileHeight(x, y) * 0.7f : 0, y + (float)rng.NextDouble());
                float s = 0.7f + (float)rng.NextDouble() * 0.6f;
                var tree = new GameObject("tree").transform;
                tree.SetParent(parent, false);
                Models.Part(tree, PrimitiveType.Cylinder, p + Vector3.up * 0.3f * s, new Vector3(0.1f, 0.3f, 0.1f) * s, trunk);
                Models.Part(tree, PrimitiveType.Sphere, p + Vector3.up * 0.85f * s, new Vector3(0.6f, 0.8f, 0.6f) * s, leaves[rng.Next(2)]);
                decor.Add((map.Idx(x, y), tree.gameObject));
            }
        }

        void BuildOre()
        {
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
            var go = new GameObject("Fog");
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
                    fogColors[y * vw + x] = new Color(0.02f, 0.03f, 0.05f, a);
                }
            fogMesh.colors = fogColors;
        }

        void Update()
        {
            if (map == null || Time.time < nextOreUpdate) return;
            nextOreUpdate = Time.time + 0.5f;
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
