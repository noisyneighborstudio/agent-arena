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
        /// <summary>
        /// How much of each tile's Ore is salvage (put down by a kill, a spill or a dismantled base) rather than the field's own.
        /// Trucks take the salvage part first and it counts as mined only when delivered (World.TipOre), and regrowth fills a
        /// field up to its original amount whatever salvage lies on it. Read it through SalvageOn: it can't exceed the ore.
        /// </summary>
        public readonly int[] Salvage;
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
            Salvage = new int[w * h];
        }

        /// <summary>The salvage on tile i (never more than the ore there: ore set or cleared directly leaves the layer behind).</summary>
        public int SalvageOn(int i) => Math.Min(Salvage[i], Ore[i]);
        /// <summary>The field's own ore on tile i: what's there besides salvage.</summary>
        public int FieldOre(int i) => Ore[i] - SalvageOn(i);

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
        public int Idx(int x, int y) => y * W + x;
        public Terrain TerrainAt(int x, int y) => Tiles[Idx(x, y)];
        public bool TerrainPassable(int x, int y) => InBounds(x, y) && Tiles[Idx(x, y)] <= Terrain.Dirt;
        public bool Passable(int x, int y) => TerrainPassable(x, y) && Occupant[Idx(x, y)] == 0;
        public int OreAt(int x, int y) => InBounds(x, y) ? Ore[Idx(x, y)] : 0;
        public string OreName(int i) => Defs.Ores[OreType[i]];
        public const byte Iron = 0, Copper = 1, Crystal = 2, Uranium = 3;

        /// <param name="extraFields">Add the neutral surface fields a bigger map gets (false rebuilds a map from before them: see RebuildBase).</param>
        public static Map Generate(int w, int h, int seed, float oreScale = 1f, bool extraFields = true)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var m = TryGenerate(w, h, seed + attempt * 7919, oreScale, extraFields);
                if (m.SpawnsConnected()) return m;
            }
            return TryGenerate(w, h, seed, oreScale, extraFields);
        }

        public const int MinSize = 48, MaxSize = 320;
        /// <summary>Menu presets. Any size between MinSize and MaxSize works through the API and command line.</summary>
        public static readonly (string name, int size)[] Presets = { ("Small", 56), ("Medium", 80), ("Large", 112), ("Huge", 160), ("Vast", 240) };

        static Map TryGenerate(int w, int h, int seed, float oreScale = 1f, bool extraFields = true)
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
            if (extraFields) m.SeedExtraFields(rng, area);
            m.SeedStartingDeep(rng);
            return m;
        }

        // ---- Neutral surface fields. The corners, the crystal ring and the centre hold about 54k ore at richness 1 whatever the
        // map's size. A bigger map gets a few small stepping-stone fields on top, in sets of four turned round the centre, placed
        // to fill the gaps between the corners and toward the middle (so there is ore an outpost can reach from the last): a
        // modest share, up to about 40% of the corner fields' ore, so the ore per base stays near an 80x80 map's. A join's strip
        // gets one set's worth, on top of the new base's own fields.

        /// <summary>The most sets of four neutral fields a new map gets (about 10k ore at richness 1 a set, so about 20k in all).</summary>
        public const int ExtraSetsMax = 2;

        /// <summary>The most neutral fields a join's strip gets, whatever its size (about 10k ore at richness 1).</summary>
        public const int ExtraFieldsPerJoin = 4;

        /// <summary>A neutral field is best about a truck's hop (30 tiles) from the nearest ore or base site: far enough to fill a gap, near enough to be reached from the last field or base.</summary>
        const float FieldHop = 30f;

        /// <summary>A neutral field's radius and amounts: a stepping stone, no bigger than a corner's copper field.</summary>
        static (int r, int min, int max) FieldOf(byte ore) => ore == Iron || ore == Copper ? (2, 200, 340) : ore == Crystal ? (2, 90, 170) : (1, 70, 130);

        /// <summary>The ore of a random neutral field, weighted as the deep deposits are: 35% iron, 30% copper, 20% crystal, 15% uranium.</summary>
        static byte NeutralOre(Random rng) { int roll = rng.Next(100); return roll < 35 ? Iron : roll < 65 ? Copper : roll < 85 ? Crystal : Uranium; }

        /// <summary>
        /// Whether a field of this radius fits at c: inside the map, open ground (no rock, water or other ore, live or regrowing)
        /// a tile all round, at least `away` from every base site, clear of any deep deposit's mine spot, and clear of the middle
        /// where the derricks stand.
        /// </summary>
        bool FieldRoom(Vec2 c, int r, float away)
        {
            int cx = (int)c.X, cy = (int)c.Y, reach = r + 1;
            if (cx - reach < 1 || cy - reach < 1 || cx + reach >= W - 1 || cy + reach >= H - 1) return false;
            if (Spawns.Any(s => Vec2.Dist(s, c) < away) || Vec2.Dist(c, new Vec2(W / 2f, H / 2f)) < DerrickRing * MathF.Min(W, H) + 8f) return false;
            if (Deep.Any(d => Vec2.Dist(d.Pos, c) < r + 4)) return false;
            for (int y = cy - reach; y <= cy + reach; y++)
                for (int x = cx - reach; x <= cx + reach; x++)
                {
                    int i = Idx(x, y);
                    if (Tiles[i] > Terrain.Dirt || Ore[i] > 0 || OreBase[i] > 0 || Occupant[i] != 0) return false;
                }
            return true;
        }

        /// <summary>How far every tile is from the nearest surface field (mined or not; salvage piles aren't fields) or base site, in tiles (a two-pass chamfer): large in the map's far gaps.</summary>
        float[] OreGaps()
        {
            var d = new float[W * H];
            for (int i = 0; i < d.Length; i++) d[i] = OreBase[i] > 0 ? 0f : 9999f;
            foreach (var s in Spawns) d[Idx((int)s.X, (int)s.Y)] = 0f;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = Idx(x, y); float v = d[i];
                    if (x > 0) v = MathF.Min(v, d[i - 1] + 1f);
                    if (y > 0) { v = MathF.Min(v, d[i - W] + 1f); if (x > 0) v = MathF.Min(v, d[i - W - 1] + 1.414f); if (x < W - 1) v = MathF.Min(v, d[i - W + 1] + 1.414f); }
                    d[i] = v;
                }
            for (int y = H - 1; y >= 0; y--)
                for (int x = W - 1; x >= 0; x--)
                {
                    int i = Idx(x, y); float v = d[i];
                    if (x < W - 1) v = MathF.Min(v, d[i + 1] + 1f);
                    if (y < H - 1) { v = MathF.Min(v, d[i + W] + 1f); if (x < W - 1) v = MathF.Min(v, d[i + W + 1] + 1.414f); if (x > 0) v = MathF.Min(v, d[i + W - 1] + 1.414f); }
                    d[i] = v;
                }
            return d;
        }

        /// <summary>
        /// Where a field of this radius should go: of the sets of spots `pick` offers (null for none), up to `want` that fit
        /// (FieldRoom, `away` from the base sites) within `tries`, the one that fills the biggest gap in the map's ore while still
        /// about a hop (FieldHop) from the nearest ore or base, so the map's far gaps shrink and each field can be reached from the
        /// last. Null if nothing fits.
        /// </summary>
        Vec2[] BestFieldSpots(Func<Vec2[]> pick, int r, float away, int tries, int want)
        {
            var gaps = OreGaps();
            Vec2[] best = null; float bestScore = float.MinValue;
            for (int t = 0, found = 0; t < tries && found < want; t++)
            {
                var spots = pick();
                if (spots == null || !spots.All(p => FieldRoom(p, r, away))) continue;
                found++;
                float gap = gaps[Idx((int)spots[0].X, (int)spots[0].Y)];
                float score = gap <= FieldHop ? gap : FieldHop - (gap - FieldHop);
                if (score > bestScore) { bestScore = score; best = spots; }
            }
            return best;
        }

        /// <summary>
        /// Neutral mixed fields in the open ground between the corners, in proportion to the map's area. Each is one ore laid at a
        /// spot and at its three turned copies (Images), rolled alike, so every corner has the same share. The spots are chosen to
        /// fill the biggest gaps between the corners and toward the middle (BestFieldSpots).
        /// </summary>
        void SeedExtraFields(Random rng, float area)
        {
            int sets = Math.Min(ExtraSetsMax, (int)MathF.Round(1.5f * (area - 1f))); // none up to 80x80, the most from about 128x128
            for (int n = 0; n < sets; n++)
            {
                byte ore = NeutralOre(rng); // the ore is settled first, so the big iron fields aren't squeezed out by the small ones fitting more easily
                var (r, min, max) = FieldOf(ore);
                var spots = BestFieldSpots(() => Images(DeepTile(new Vec2(rng.Next(W), rng.Next(H)))), r, 18f, 400, 12);
                if (spots == null) continue;
                // One field at the first spot, then its tiles (shape and ore) turned onto the other three: exactly alike, whichever quarter they cross.
                OreField(rng, (int)spots[0].X, (int)spots[0].Y, r, min, max, ore);
                StampTurned((int)spots[0].X, (int)spots[0].Y, r + 1);
            }
        }

        /// <summary>Copy the ore tiles within `reach` of (cx, cy) onto the same tiles' turned copies (Images): terrain, ore and what it grows back to.</summary>
        void StampTurned(int cx, int cy, int reach)
        {
            for (int y = cy - reach; y <= cy + reach; y++)
                for (int x = cx - reach; x <= cx + reach; x++)
                {
                    int i = Idx(x, y);
                    if (Ore[i] <= 0) continue;
                    var copies = Images(new Vec2(x + 0.5f, y + 0.5f));
                    for (int k = 1; k < copies.Length; k++)
                    {
                        int j = Idx((int)copies[k].X, (int)copies[k].Y);
                        Tiles[j] = Tiles[i]; Ore[j] = Ore[i]; OreType[j] = OreType[i]; OreBase[j] = OreBase[i]; OreBaseType[j] = OreBaseType[i];
                    }
                }
            fieldsChanged = true;
        }

        // ---- Deep deposits. Every base site gets the same ones, so no corner is richer than another: the starting map lays
        // each deposit down with its turned copies (Images), and a base added by a join is given a copy of the first base's kit.

        /// <summary>
        /// A base's own deep deposits, its kit (one iron, one copper), lie within this many tiles of its site, and no other
        /// deposit is put that close to any base: past this the ground is neutral (the middle, the far edges).
        /// </summary>
        public const float DeepKitReach = 22f;

        /// <summary>One deposit of a base's kit as seen from its site: the ore, the amount, how far ahead (toward the middle of the map) and aside it lies.</summary>
        struct KitSpot { public byte Type; public float Amount, Ahead, Aside; }

        /// <summary>A deposit's amount, in the ranges deep ore always had: 4000-8000 iron or copper, 2000-4000 crystal, 1500-3000 uranium.</summary>
        static float DeepAmount(Random rng, int type) => type <= Copper ? rng.Next(4000, 8000) : type == Crystal ? rng.Next(2000, 4000) : rng.Next(1500, 3000);

        /// <summary>The centre of the tile p is on: where a deposit goes.</summary>
        static Vec2 DeepTile(Vec2 p) => Int2.Of(p).Center;

        /// <summary>The ring the neutral derricks stand on, as a fraction of the map's size (World.EnsureDerricks). Deposits keep 5 tiles off it, so no derrick is shoved aside by one.</summary>
        const float DerrickRing = 0.12f;

        /// <summary>How far neutral deposits keep from every base site: past its kit, or less on a small map where the middle is close.</summary>
        float NeutralGap() => MathF.Min(DeepKitReach + 4f, 0.75f * Vec2.Dist(new Vec2(W / 2f, H / 2f), Spawns[0]));

        /// <summary>
        /// p and its copies turned a quarter, half and three quarters round the centre of the map, which carry each corner base
        /// site onto the next (a map that isn't square is mirrored across and down instead). Tile centres stay tile centres, and
        /// a copy is exactly as far from its own site as p is from the first.
        /// </summary>
        Vec2[] Images(Vec2 p) => W == H
            ? new[] { p, new Vec2(W - p.Y, p.X), new Vec2(W - p.X, H - p.Y), new Vec2(p.Y, H - p.X) }
            : new[] { p, new Vec2(W - p.X, p.Y), new Vec2(p.X, H - p.Y), new Vec2(W - p.X, H - p.Y) };

        /// <summary>
        /// Whether a deep mine could work a deposit here: inside the map, the tile open and reachable, and room for the mine's 2x2
        /// over or beside it (as World.DeepMineSpot looks) that holds no structure and no surface ore.
        /// </summary>
        bool DeepSpotClear(Vec2 p, bool[] reach)
        {
            int x = (int)p.X, y = (int)p.Y;
            if (x < 3 || y < 3 || x >= W - 3 || y >= H - 3 || !Passable(x, y) || !reach[Idx(x, y)]) return false;
            bool Free(int tx, int ty) { int i = Idx(tx, ty); return InBounds(tx, ty) && Tiles[i] <= Terrain.Dirt && Occupant[i] == 0 && Ore[i] == 0 && OreBase[i] == 0; }
            for (int oy = y - 2; oy <= y + 1; oy++)
                for (int ox = x - 2; ox <= x + 1; ox++)
                    if (Free(ox, oy) && Free(ox + 1, oy) && Free(ox, oy + 1) && Free(ox + 1, oy + 1)) return true;
            return false;
        }

        /// <summary>
        /// Add a deposit of this ore and amount at each of `spots`, if every one can go there: clear ground, at least `apart`
        /// from each other and from every deposit already made, at least `away` from every base site, and off the derricks' ring.
        /// If not, none is added (and null `spots`, a spot that fell outside a kit's reach, never fits).
        /// </summary>
        bool AddDeep(Vec2[] spots, byte type, float amount, bool[] reach, float apart, float away)
        {
            if (spots == null) return false;
            for (int a = 0; a < spots.Length; a++)
            {
                var p = spots[a];
                if (!DeepSpotClear(p, reach) || Spawns.Any(s => Vec2.Dist(s, p) < away) || Deep.Any(d => Vec2.Dist(d.Pos, p) < apart)) return false;
                if (MathF.Abs(Vec2.Dist(p, new Vec2(W / 2f, H / 2f)) - DerrickRing * MathF.Min(W, H)) < 5f) return false;
                for (int b = 0; b < a; b++) if (Vec2.Dist(spots[b], p) < apart) return false;
            }
            foreach (var p in spots) Deep.Add(new DeepDeposit { Id = nextDepositId++, Pos = p, Type = type, Amount = amount, Initial = amount });
            return true;
        }

        /// <summary>
        /// Try the spots `pick` offers until a set fits (AddDeep). With `ease`, a crowded map is let off: the spacing between deposits
        /// eases from 9 tiles to 5, then the distance from base sites from `away` to 10, rather than leave an ore out.
        /// </summary>
        bool PlaceDeep(Func<Vec2[]> pick, byte type, float amount, bool[] reach, float away, int tries, bool ease)
        {
            for (float keep = away; ; keep = 10f)
            {
                for (float apart = 9f; apart >= (ease ? 5f : 9f); apart -= 2f)
                    for (int i = 0; i < tries; i++)
                        if (AddDeep(pick(), type, amount, reach, apart, keep)) return true;
                if (!ease || keep <= 10f) return false;
            }
        }

        /// <summary>
        /// The deep deposits of a new map, the same from every corner. Each base site gets a kit of one deep iron and one deep
        /// copper deposit; the contested middle gets deep crystal and deep uranium, so every ore exists deep; bigger maps get
        /// more neutral deposits. Each is laid with its turned copies: the same ore and amount at the same distance from its
        /// own site. Deterministic from the seed.
        /// </summary>
        void SeedStartingDeep(Random rng)
        {
            var reach = new bool[W * H];
            foreach (var s in Spawns) { var r = Reachable(Int2.Of(s)); for (int i = 0; i < reach.Length; i++) reach[i] |= r[i]; }
            var centre = new Vec2(W / 2f, H / 2f);
            float gap = NeutralGap();
            LayKit(rng, Spawns[0], centre, reach, true, null);
            Func<Vec2[]> anywhere = () => Images(DeepTile(new Vec2(3 + rng.Next(W - 6), 3 + rng.Next(H - 6))));
            // The contested middle: crystal, then uranium, round the centre (anywhere there's room, if the middle is full).
            float near = DerrickRing * MathF.Min(W, H) + 5f, far = MathF.Max(near + 3f, 0.3f * MathF.Min(W, H));
            Func<Vec2[]> middle = () =>
            {
                float a = (float)rng.NextDouble() * 2 * MathF.PI, rho = near + (float)rng.NextDouble() * (far - near);
                return Images(DeepTile(centre + new Vec2(MathF.Cos(a), MathF.Sin(a)) * rho));
            };
            foreach (byte ore in new[] { Crystal, Uranium })
            {
                float amount = DeepAmount(rng, ore);
                if (!PlaceDeep(middle, ore, amount, reach, gap, 300, true)) PlaceDeep(anywhere, ore, amount, reach, gap, 300, true);
            }
            // The rest of the old density (about 12 per 80x80), as more neutral sets anywhere clear of the bases.
            int more = Math.Max(0, (int)MathF.Round(3f * W * H / 6400f) - 4);
            for (int n = 0; n < more; n++)
            {
                int roll = rng.Next(100), type = roll < 35 ? Iron : roll < 65 ? Copper : roll < 85 ? Crystal : Uranium;
                PlaceDeep(anywhere, (byte)type, DeepAmount(rng, type), reach, gap, 30, false);
            }
        }

        /// <summary>
        /// A base's kit: one deep iron and one deep copper deposit, usually 12-21 tiles from `home` (nearer on a small map) and
        /// toward the middle of the map. With a `template` (another base's kit) each lies as far from `home` at the same bearing,
        /// and holds the same amount, turning round the site a little if that ground isn't clear; without one the kit is rolled.
        /// `all` lays each deposit with its turned copies at the other corners too (the starting map), otherwise only here.
        /// </summary>
        void LayKit(Random rng, Vec2 home, Vec2 centre, bool[] reach, bool all, KitSpot[] template)
        {
            var dir = (centre - home).Normalized; var side = new Vec2(-dir.Y, dir.X);
            float toCentre = Vec2.Dist(centre, home);
            // The spot, on its tile centre (which can be most of a tile from where it was aimed), with its copies; null if that tile is past the kit's reach.
            Vec2[] Spots(float ahead, float aside)
            {
                var p = DeepTile(home + dir * ahead + side * aside);
                return Vec2.Dist(p, home) > DeepKitReach ? null : all ? Images(p) : new[] { p };
            }
            bool Copy(KitSpot k)
            {
                float r = MathF.Sqrt(k.Ahead * k.Ahead + k.Aside * k.Aside), bearing = MathF.Atan2(k.Aside, k.Ahead);
                for (int turn = 0; turn <= 16; turn++) // 0, +6, -6, +12, -12 ... degrees
                {
                    float a = bearing + (turn + 1) / 2 * (turn % 2 == 1 ? 1 : -1) * 6f * MathF.PI / 180f;
                    if (AddDeep(Spots(r * MathF.Cos(a), r * MathF.Sin(a)), k.Type, k.Amount, reach, 9f, 10f)) return true;
                }
                return false;
            }
            for (byte ore = Iron; ore <= Copper; ore++)
            {
                if (template != null && Copy(template[ore])) continue;
                // Rolled: iron to one side of the line to the middle and copper to the other, so the two don't crowd each other;
                // if the ground there won't take every copy, anywhere within the base's reach.
                float amount = template != null ? template[ore].Amount : DeepAmount(rng, ore);
                int sideSign = ore == Iron ? -1 : 1;
                for (int pass = 0; pass < 2; pass++)
                {
                    float near = pass == 0 ? MathF.Min(12f, toCentre * 0.5f) : 10.5f, far = pass == 0 ? MathF.Min(DeepKitReach - 1f, toCentre * 0.65f) : DeepKitReach - 1f;
                    float spread = pass == 0 ? 32f : 60f;
                    if (PlaceDeep(() =>
                    {
                        float r = near + (float)rng.NextDouble() * (far - near), deg = 10f + (float)rng.NextDouble() * spread;
                        float a = (pass == 0 ? sideSign : rng.Next(2) * 2 - 1) * deg * MathF.PI / 180f;
                        return Spots(r * MathF.Cos(a), r * MathF.Sin(a));
                    }, ore, amount, reach, 10f, 300, true)) break;
                }
            }
        }

        /// <summary>
        /// The kit of the base at `site` (with `centre` the middle of the map it was made for), to give another base the same: its
        /// nearest deep iron and nearest deep copper within DeepKitReach, with the amounts they began with. Null if either is
        /// missing, as on a map made before bases had kits.
        /// </summary>
        KitSpot[] KitAt(Vec2 site, Vec2 centre)
        {
            var dir = (centre - site).Normalized; var side = new Vec2(-dir.Y, dir.X);
            var kit = new KitSpot[2];
            for (byte ore = Iron; ore <= Copper; ore++)
            {
                var near = Deep.Where(d => d.Type == ore && Vec2.Dist(d.Pos, site) <= DeepKitReach).OrderBy(d => Vec2.Dist(d.Pos, site)).FirstOrDefault();
                if (near == null) return null;
                var off = near.Pos - site;
                kit[ore] = new KitSpot { Type = ore, Amount = near.Initial, Ahead = off.X * dir.X + off.Y * dir.Y, Aside = off.X * side.X + off.Y * side.Y };
            }
            return kit;
        }

        /// <summary>
        /// Scatter neutral deep deposits over the ground `where` allows (about 12 per 80x80), keeping out of every base site's kit
        /// area. This is what the strips added as the map grows get, on top of the new base's kit.
        /// </summary>
        void SeedDeep(Random rng, Func<int, int, bool> where, bool[] reach)
        {
            int cells = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (where(x, y)) cells++;
            int want = Math.Max(2, (int)(cells / 6400f * 12));
            float gap = NeutralGap();
            for (int tries = 0; tries < want * 30 && want > 0; tries++)
            {
                int x = 3 + rng.Next(W - 6), y = 3 + rng.Next(H - 6);
                if (!where(x, y)) continue;
                var p = new Vec2(x + 0.5f, y + 0.5f);
                if (!DeepSpotClear(p, reach) || Spawns.Any(s => Vec2.Dist(s, p) < gap) || Deep.Any(d => Vec2.Dist(d.Pos, p) < 9)) continue;
                int roll = rng.Next(100), type = roll < 35 ? Iron : roll < 65 ? Copper : roll < 85 ? Crystal : Uranium;
                float amount = DeepAmount(rng, type);
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
                    m.OreBase[b] = OreBase[a]; m.OreBaseType[b] = OreBaseType[a]; m.Salvage[b] = Salvage[a];
                }
            m.Spawns.AddRange(Spawns);
            m.OreScale = OreScale; // newcomers' fields are as scarce as everyone else's
            m.Deep.AddRange(Deep); m.nextDepositId = nextDepositId; // deep deposits carry over unchanged (and new fields keep clear of them)
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
            // The strip gets a few neutral fields in the new ground, so it's worth expanding into: one set's worth, however wide it is.
            int extra = Math.Min(ExtraFieldsPerJoin, (int)(area * ExtraFieldsPerJoin));
            for (int i = 0; i < extra; i++)
            {
                byte ore = NeutralOre(rng);
                var (r, min, max) = FieldOf(ore);
                var spot = m.BestFieldSpots(() =>
                {
                    bool east = rng.NextDouble() < 0.5;
                    int cx = east ? oldW + 4 + rng.Next(Math.Max(1, newW - oldW - 8)) : 6 + rng.Next(newW - 12);
                    int cy = east ? 6 + rng.Next(newH - 12) : oldH + 4 + rng.Next(Math.Max(1, newH - oldH - 8));
                    var c = new Vec2(cx, cy);
                    return Vec2.Dist(c, sp) < 16 || bases.Any(b => Vec2.Dist(c, b) < 16) ? null : new[] { c };
                }, r, 16f, 60, 8);
                if (spot != null) m.OreField(rng, (int)spot[0].X, (int)spot[0].Y, r, min, max, ore);
            }

            m.Spawns.Add(sp);
            // The new base gets the same kit as the first one (rolled fresh on a map that has none), and the strip's random
            // extras go in neutral ground only, away from every base.
            var reach = m.Reachable(Int2.Of(sp)); // as it will be once EnsureConnected below has joined the new site to the first base
            if (bases.Count > 0) { var fromBase = m.Reachable(Int2.Of(bases[0])); for (int i = 0; i < reach.Length; i++) reach[i] |= fromBase[i]; }
            m.LayKit(rng, sp, centre, reach, false, KitAt(Spawns[0], new Vec2(oldW / 2f, oldH / 2f)));
            m.SeedDeep(rng, InNew, reach);
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
