using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public enum Terrain : byte { Grass, Dirt, Rock, Water }

    /// <summary>
    /// Ore deep underground: invisible until a geological surveyor finds it, then mined by a drill rig deployed into a
    /// deep mine. Far bigger than surface fields, for when those run out.
    /// </summary>
    public class DeepDeposit
    {
        public int Id;
        public Vec2 Pos;
        public int Type;          // index into Defs.Ores
        public float Amount, Initial;
        public int MineId;        // the deep mine working it (0 = none)
    }

    public partial class Map
    {
        public readonly int W, H;
        public readonly Terrain[] Tiles;
        public readonly int[] Ore;         // units of ore left on the tile
        public readonly byte[] OreType;    // index into Defs.Ores (iron_ore, copper_ore, crystal, uranium)
        public readonly int[] Occupant;    // structure id occupying the tile, 0 = none
        /// <summary>
        /// Each surface field tile's original ore (and its type), as the map made it: mined fields slowly grow back toward
        /// it (World.Regrow). 0 for ground that never had a field (salvage piles there never come back).
        /// </summary>
        public readonly int[] OreBase;
        public readonly byte[] OreBaseType;
        public readonly List<Vec2> Spawns = new List<Vec2>();
        public readonly List<DeepDeposit> Deep = new List<DeepDeposit>();
        /// <summary>Scales every surface ore field (1 = normal; below 1 = a scarce map where deep mining matters early).</summary>
        public float OreScale = 1f;
        int nextDepositId = 1;
        public const int MaxOrePerTile = 1000;
        /// <summary>While growing, restricts terrain edits to the new strip (null = anywhere).</summary>
        Func<int, int, bool> writable;

        public Map(int w, int h)
        {
            W = w; H = h;
            Tiles = new Terrain[w * h];
            Ore = new int[w * h];
            OreType = new byte[w * h];
            Occupant = new int[w * h];
            OreBase = new int[w * h];
            OreBaseType = new byte[w * h];
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
        public int Idx(int x, int y) => y * W + x;
        public Terrain TerrainAt(int x, int y) => Tiles[Idx(x, y)];
        public bool TerrainPassable(int x, int y) => InBounds(x, y) && Tiles[Idx(x, y)] <= Terrain.Dirt;
        public bool Passable(int x, int y) => TerrainPassable(x, y) && Occupant[Idx(x, y)] == 0;
        public int OreAt(int x, int y) => InBounds(x, y) ? Ore[Idx(x, y)] : 0;
        public string OreName(int i) => Defs.Ores[OreType[i]];
        public const byte Iron = 0, Copper = 1, Crystal = 2, Uranium = 3;

        public static Map Generate(int w, int h, int seed, float oreScale = 1f)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var m = TryGenerate(w, h, seed + attempt * 7919, oreScale);
                if (m.SpawnsConnected()) return m;
            }
            return TryGenerate(w, h, seed, oreScale);
        }

        public const int MinSize = 48, MaxSize = 320;
        /// <summary>Menu presets. Any size between MinSize and MaxSize works through the API and command line.</summary>
        public static readonly (string name, int size)[] Presets = { ("Small", 56), ("Medium", 80), ("Large", 112), ("Huge", 160), ("Vast", 240) };

        static Map TryGenerate(int w, int h, int seed, float oreScale = 1f)
        {
            // Scatter counts were tuned on an 80x80 map; scale them with area so bigger maps aren't empty.
            float area = w * h / 6400f;
            var rng = new Random(seed);
            var m = new Map(w, h) { OreScale = oreScale };
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
            m.SeedDeep(rng, (x, y) => true);
            return m;
        }

        /// <summary>Scatter deep deposits over the ground `where` allows (about 12 per 80x80), clear of base sites.</summary>
        void SeedDeep(Random rng, Func<int, int, bool> where)
        {
            int cells = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (where(x, y)) cells++;
            int want = Math.Max(2, (int)(cells / 6400f * 12));
            for (int tries = 0; tries < want * 30 && want > 0; tries++)
            {
                int x = 3 + rng.Next(W - 6), y = 3 + rng.Next(H - 6);
                if (!where(x, y) || !Passable(x, y)) continue;
                var p = new Vec2(x + 0.5f, y + 0.5f);
                if (Spawns.Any(s => Vec2.Dist(s, p) < 10) || Deep.Any(d => Vec2.Dist(d.Pos, p) < 9)) continue;
                int roll = rng.Next(100), type = roll < 35 ? Iron : roll < 65 ? Copper : roll < 85 ? Crystal : Uranium;
                float amount = type <= Copper ? rng.Next(4000, 8000) : type == Crystal ? rng.Next(2000, 4000) : rng.Next(1500, 3000);
                Deep.Add(new DeepDeposit { Id = nextDepositId++, Pos = p, Type = type, Amount = amount, Initial = amount });
                want--;
            }
        }

        public DeepDeposit DepositById(int id) => Deep.FirstOrDefault(d => d.Id == id);

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
                    if (!InBounds(x, y) || (writable != null && !writable(x, y))) continue;
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d <= r + (float)rng.NextDouble() * 0.9f) set(x, y);
                }
        }

        void OreField(Random rng, int cx, int cy, int r, int min, int max, byte type)
        {
            Blob(rng, cx, cy, r, (x, y) =>
            {
                int i = Idx(x, y);
                if (Occupant[i] != 0 || Ore[i] > 0) return; // never bury buildings or overwrite live ore
                Tiles[i] = Terrain.Dirt;
                OreType[i] = type;
                float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                Ore[i] = Math.Min(MaxOrePerTile, (int)(rng.Next(min, max) * (1.2f - d / (r + 1)) * OreScale));
                OreBase[i] = Ore[i]; OreBaseType[i] = type; // what it grows back to
                fieldsChanged = true;
            });
        }

        /// <summary>
        /// A bigger copy of this map for a new player. Existing tiles keep their coordinates (nobody's positions
        /// change mid-game); the new strip along the east and north edges gets terrain, a base site with its own
        /// iron and copper, and a neutral crystal and uranium deposit. The new site is always reachable. It goes where
        /// it's farthest from every threat (enemy structures and armed units); clearance is that distance.
        /// </summary>
        public Map Grown(int newW, int newH, int seed, IList<Vec2> bases, IList<Vec2> threats, out Vec2 spawn, out float clearance)
        {
            var m = new Map(newW, newH);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int a = Idx(x, y), b = m.Idx(x, y);
                    m.Tiles[b] = Tiles[a]; m.Ore[b] = Ore[a]; m.OreType[b] = OreType[a]; m.Occupant[b] = Occupant[a];
                    m.OreBase[b] = OreBase[a]; m.OreBaseType[b] = OreBaseType[a];
                }
            m.Spawns.AddRange(Spawns);
            m.OreScale = OreScale; // newcomers' fields are as scarce as everyone else's
            var rng = new Random(seed);
            int oldW = W, oldH = H;
            bool InNew(int x, int y) => x >= oldW || y >= oldH;

            // Base site: inset from the new edges, as far as possible from every threat.
            const int inset = 9;
            var cands = new List<Vec2>();
            for (int x = inset; x <= newW - inset; x += 3) cands.Add(new Vec2(x, newH - inset));
            for (int y = inset; y <= newH - inset; y += 3) cands.Add(new Vec2(newW - inset, y));
            spawn = cands.OrderByDescending(c => Clearance(c, threats)).First();
            clearance = Clearance(spawn, threats);
            var sp = spawn;

            // Terrain scatter, only in the new strip and clear of the new base.
            m.writable = InNew;
            float area = (newW * newH - oldW * oldH) / 6400f;
            for (int i = 0; i < (int)(40 * area); i++)
                m.Blob(rng, oldW + rng.Next(newW - oldW), rng.Next(newH), rng.Next(2, 5), (x, y) => m.Tiles[m.Idx(x, y)] = Terrain.Dirt);
            for (int i = 0; i < (int)(40 * area); i++)
                m.Blob(rng, rng.Next(newW), oldH + rng.Next(newH - oldH), rng.Next(2, 5), (x, y) => m.Tiles[m.Idx(x, y)] = Terrain.Dirt);
            for (int i = 0; i < (int)(26 * area) + 1; i++)
            {
                bool east = rng.NextDouble() < 0.5;
                int cx = east ? oldW + rng.Next(newW - oldW) : rng.Next(newW), cy = east ? rng.Next(newH) : oldH + rng.Next(newH - oldH);
                if (Vec2.Dist(new Vec2(cx, cy), sp) < 13 || bases.Any(b => Vec2.Dist(new Vec2(cx, cy), b) < 13)) continue;
                var t = rng.NextDouble() < 0.7 ? Terrain.Rock : Terrain.Water;
                m.Blob(rng, cx, cy, rng.Next(1, 3), (x, y) => { if (m.Occupant[m.Idx(x, y)] == 0 && m.Ore[m.Idx(x, y)] == 0) m.Tiles[m.Idx(x, y)] = t; });
            }
            m.writable = null;

            // Open ground for the base itself.
            for (int y = (int)sp.Y - 6; y <= (int)sp.Y + 6; y++)
                for (int x = (int)sp.X - 6; x <= (int)sp.X + 6; x++)
                    if (m.InBounds(x, y) && m.Occupant[m.Idx(x, y)] == 0 && m.Tiles[m.Idx(x, y)] >= Terrain.Rock) m.Tiles[m.Idx(x, y)] = Terrain.Grass;

            // Resources to accommodate the newcomer: its own iron and copper, plus a contested crystal/uranium deposit.
            var centre = new Vec2(newW / 2f, newH / 2f);
            var dir = (centre - sp).Normalized; var side = new Vec2(-dir.Y, dir.X);
            var iron = sp + dir * 9f; var copper = sp + dir * 5f + side * 7f;
            m.OreField(rng, (int)iron.X, (int)iron.Y, 3, 260, 460, Iron);
            m.OreField(rng, (int)copper.X, (int)copper.Y, 2, 200, 340, Copper);
            var mid = Vec2.Lerp(sp, centre, 0.45f) + side * 6f;
            m.OreField(rng, (int)mid.X, (int)mid.Y, 2, 90, 170, Crystal);
            var mid2 = Vec2.Lerp(sp, centre, 0.55f) - side * 5f;
            m.OreField(rng, (int)mid2.X, (int)mid2.Y, 1, 70, 130, Uranium);
            // Wider strips get extra neutral deposits scattered through the new ground, so it's worth expanding into.
            int extra = (int)(area * 1.5f);
            for (int i = 0, tries = 0; i < extra && tries < 200; tries++)
            {
                bool east = rng.NextDouble() < 0.5;
                int cx = east ? oldW + 4 + rng.Next(Math.Max(1, newW - oldW - 8)) : 6 + rng.Next(newW - 12);
                int cy = east ? 6 + rng.Next(newH - 12) : oldH + 4 + rng.Next(Math.Max(1, newH - oldH - 8));
                var c = new Vec2(cx, cy);
                if (Vec2.Dist(c, sp) < 16 || bases.Any(b => Vec2.Dist(c, b) < 16)) continue;
                int roll = rng.Next(4);
                m.OreField(rng, cx, cy, roll < 2 ? 2 : 1, 120, 260, roll == 0 ? Iron : roll == 1 ? Copper : roll == 2 ? Crystal : Uranium);
                i++;
            }

            m.Spawns.Add(sp);
            // Deep deposits carry over unchanged; the new strip gets its own.
            m.Deep.AddRange(Deep); m.nextDepositId = nextDepositId;
            m.SeedDeep(rng, InNew);
            if (bases.Count > 0) m.EnsureConnected(bases[0], sp);
            return m;
        }

        /// <summary>Distance from p to the nearest threat (large when there are none).</summary>
        public static float Clearance(Vec2 p, IList<Vec2> threats)
        {
            float best = 9999f;
            foreach (var t in threats) best = MathF.Min(best, Vec2.Dist(t, p));
            return best;
        }

        /// <summary>If b isn't reachable from a, carve a 2-wide dirt road between them.</summary>
        public void EnsureConnected(Vec2 a, Vec2 b)
        {
            var reach = Reachable(Int2.Of(a));
            if (reach[Idx((int)b.X, (int)b.Y)]) return;
            int steps = (int)(Vec2.Dist(a, b) * 2) + 1;
            for (int k = 0; k <= steps; k++)
            {
                var p = Vec2.Lerp(a, b, k / (float)steps);
                for (int dy = 0; dy <= 1; dy++)
                    for (int dx = 0; dx <= 1; dx++)
                    {
                        int x = (int)p.X + dx, y = (int)p.Y + dy;
                        if (InBounds(x, y) && Occupant[Idx(x, y)] == 0 && Tiles[Idx(x, y)] >= Terrain.Rock) Tiles[Idx(x, y)] = Terrain.Dirt;
                    }
            }
        }

        bool[] Reachable(Int2 start)
        {
            var seen = new bool[W * H];
            var q = new Queue<Int2>();
            if (!InBounds(start.X, start.Y)) return seen;
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
            return seen;
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
