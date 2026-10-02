using System;
using System.Collections.Generic;

namespace Pez.Sim
{
    public enum Terrain : byte { Grass, Dirt, Rock, Water }

    public class Map
    {
        public readonly int W, H;
        public readonly Terrain[] Tiles;
        public readonly int[] Ore;         // units of ore left on the tile
        public readonly byte[] OreType;    // index into Defs.Ores (iron_ore, copper_ore, crystal, uranium)
        public readonly int[] Occupant;    // structure id occupying the tile, 0 = none
        public readonly List<Vec2> Spawns = new List<Vec2>();
        public const int MaxOrePerTile = 1000;

        public Map(int w, int h)
        {
            W = w; H = h;
            Tiles = new Terrain[w * h];
            Ore = new int[w * h];
            OreType = new byte[w * h];
            Occupant = new int[w * h];
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
        public int Idx(int x, int y) => y * W + x;
        public Terrain TerrainAt(int x, int y) => Tiles[Idx(x, y)];
        public bool TerrainPassable(int x, int y) => InBounds(x, y) && Tiles[Idx(x, y)] <= Terrain.Dirt;
        public bool Passable(int x, int y) => TerrainPassable(x, y) && Occupant[Idx(x, y)] == 0;
        public int OreAt(int x, int y) => InBounds(x, y) ? Ore[Idx(x, y)] : 0;
        public string OreName(int i) => Defs.Ores[OreType[i]];
        public const byte Iron = 0, Copper = 1, Crystal = 2, Uranium = 3;

        public static Map Generate(int w, int h, int seed)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var m = TryGenerate(w, h, seed + attempt * 7919);
                if (m.SpawnsConnected()) return m;
            }
            return TryGenerate(w, h, seed);
        }

        public const int MinSize = 48, MaxSize = 160;
        /// <summary>Menu presets. Any size between MinSize and MaxSize works through the API and command line.</summary>
        public static readonly (string name, int size)[] Presets = { ("Small", 56), ("Medium", 80), ("Large", 112), ("Huge", 144) };

        static Map TryGenerate(int w, int h, int seed)
        {
            // Scatter counts were tuned on an 80x80 map; scale them with area so bigger maps aren't empty.
            float area = w * h / 6400f;
            var rng = new Random(seed);
            var m = new Map(w, h);
            int inset = 9;
            m.Spawns.Add(new Vec2(inset, inset));
            m.Spawns.Add(new Vec2(w - inset, h - inset));
            m.Spawns.Add(new Vec2(w - inset, inset));
            m.Spawns.Add(new Vec2(inset, h - inset));

            // Dirt patches for visual variety.
            for (int i = 0; i < (int)(40 * area); i++)
                m.Blob(rng, rng.Next(w), rng.Next(h), rng.Next(2, 5), (x, y) => m.Tiles[m.Idx(x, y)] = Terrain.Dirt);

            // Rock outcrops and lakes, kept away from bases.
            for (int i = 0; i < (int)(26 * area); i++)
            {
                int cx = rng.Next(w), cy = rng.Next(h);
                if (m.NearSpawn(cx, cy, 13)) continue;
                var t = rng.NextDouble() < 0.7 ? Terrain.Rock : Terrain.Water;
                m.Blob(rng, cx, cy, rng.Next(1, 4), (x, y) => m.Tiles[m.Idx(x, y)] = t);
            }

            // Every corner (including empty ones, which are expansion sites) gets iron and copper.
            // Crystal rings the middle; uranium sits in small contested deposits at the centre.
            var centre = new Vec2(w / 2f, h / 2f);
            foreach (var s in m.Spawns)
            {
                var dir = (centre - s).Normalized;
                var side = new Vec2(-dir.Y, dir.X);
                var p = s + dir * 9f;
                m.OreField(rng, (int)p.X, (int)p.Y, 3, 260, 460, Iron);
                var q = s + dir * 5f + side * 7f;
                m.OreField(rng, (int)q.X, (int)q.Y, 2, 200, 340, Copper);
            }
            for (int i = 0; i < 4; i++)
            {
                float a = i * MathF.PI / 2 + MathF.PI / 4 + MathF.PI / 4;
                var p = centre + new Vec2(MathF.Cos(a), MathF.Sin(a)) * (w * 0.2f);
                m.OreField(rng, (int)p.X, (int)p.Y, 2, 90, 170, Crystal);
            }
            m.OreField(rng, (int)centre.X - 2, (int)centre.Y + 2, 1, 70, 130, Uranium);
            m.OreField(rng, (int)centre.X + 2, (int)centre.Y - 2, 1, 70, 130, Uranium);
            return m;
        }

        bool NearSpawn(int x, int y, float r)
        {
            foreach (var s in Spawns) if (Vec2.Dist(s, new Vec2(x, y)) < r) return true;
            return false;
        }

        void Blob(Random rng, int cx, int cy, int r, Action<int, int> set)
        {
            for (int y = cy - r - 1; y <= cy + r + 1; y++)
                for (int x = cx - r - 1; x <= cx + r + 1; x++)
                {
                    if (!InBounds(x, y)) continue;
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d <= r + (float)rng.NextDouble() * 0.9f) set(x, y);
                }
        }

        void OreField(Random rng, int cx, int cy, int r, int min, int max, byte type)
        {
            Blob(rng, cx, cy, r, (x, y) =>
            {
                int i = Idx(x, y);
                Tiles[i] = Terrain.Dirt;
                OreType[i] = type;
                float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                Ore[i] = Math.Min(MaxOrePerTile, (int)(rng.Next(min, max) * (1.2f - d / (r + 1))));
            });
        }

        bool SpawnsConnected()
        {
            var start = Int2.Of(Spawns[0]);
            var seen = new bool[W * H];
            var q = new Queue<Int2>();
            q.Enqueue(start);
            seen[Idx(start.X, start.Y)] = true;
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                for (int d = 0; d < 4; d++)
                {
                    int nx = c.X + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = c.Y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (!TerrainPassable(nx, ny) || seen[Idx(nx, ny)]) continue;
                    seen[Idx(nx, ny)] = true;
                    q.Enqueue(new Int2(nx, ny));
                }
            }
            foreach (var s in Spawns) { var t = Int2.Of(s); if (!seen[Idx(t.X, t.Y)]) return false; }
            return true;
        }

        /// <summary>Nearest tile with ore, searching outward from a point.</summary>
        public Int2? NearestOre(Vec2 from, float maxDist, Func<Int2, bool> filter = null, int type = -1)
        {
            Int2? best = null;
            float bestD = maxDist * maxDist;
            int r = (int)MathF.Ceiling(maxDist);
            int fx = (int)from.X, fy = (int)from.Y;
            for (int y = Math.Max(0, fy - r); y <= Math.Min(H - 1, fy + r); y++)
                for (int x = Math.Max(0, fx - r); x <= Math.Min(W - 1, fx + r); x++)
                {
                    if (Ore[Idx(x, y)] <= 0 || (type >= 0 && OreType[Idx(x, y)] != type)) continue;
                    var t = new Int2(x, y);
                    float d = Vec2.DistSq(t.Center, from);
                    if (d < bestD && (filter == null || filter(t))) { bestD = d; best = t; }
                }
            return best;
        }
    }
}
