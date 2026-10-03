using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>
    /// The economy rules that reward fighting over ground: salvage from kills (and, below, ore regrowth, derricks and
    /// defence upkeep). Each is a small, separate rule; World.Step calls them.
    /// </summary>
    public partial class World
    {
        /// <summary>
        /// Bumped (at most every 10 s) when ore appears where there was none (salvage, regrowth), so a client that draws
        /// the ore from a one-off map download (the web viewer) fetches it again. Not saved: clients only compare it.
        /// </summary>
        public int OreVersion;
        bool oreDirty;

        void TickOreVersion()
        {
            if (oreDirty && Tick % (TickRate * 10) == 0) { OreVersion++; oreDirty = false; }
        }

        // ------------------------------------------------------------------ ore regrowth

        /// <summary>
        /// Ore regrowth, per field tile, in ore a second at the map's centre, scaled by RegrowWeight (1 in the middle,
        /// RegrowEdge in the corners) and the square root of the map's ore richness (a scarce map regrows less, but not
        /// in proportion: its stalemates need the long tail most). A mined-bare 96-tile map earns back about 7 ore/s at
        /// richness 1 and 3.5 at 0.3, about 70% of it in the middle: a deep mine pumps 4/s, so deep mining stays the
        /// mid-game economy and regrowth is the long tail that makes the middle worth fighting for.
        /// </summary>
        public const float RegrowRate = 0.12f, RegrowEdge = 0.04f, RegrowFalloff = 2.5f;
        public const int RegrowEvery = TickRate * 2;
        /// <summary>Selftest baseline only: no regrowth at all.</summary>
        public static bool RegrowthDisabled;
        /// <summary>Ore regrown on the map this game (for balance reports).</summary>
        public int Regrown;
        /// <summary>Regrowth runs until sudden death (the match clock).</summary>
        public bool RegrowthOn => !RegrowthDisabled && (SuddenDeathAt <= 0 || Time < SuddenDeathAt);
        readonly HashSet<int> beingMined = new HashSet<int>();
        readonly List<(int i, int add)> regrowNow = new List<(int, int)>();

        /// <summary>
        /// Every 2 s each mined surface field tile grows back a little toward its original amount: from ore it still
        /// has, from a neighbouring tile of its field that has ore, or (a field mined to nothing) from the field's root.
        /// Never under a structure, never past the original amount, never on a tile a truck is mining, and never over
        /// salvage of another ore. Fractions are dithered by a hash of tile and tick, so it's deterministic.
        /// </summary>
        void Regrow()
        {
            if (Tick % RegrowEvery != 0 || !RegrowthOn) return;
            Map.FieldIndex(out var tiles, out var roots);
            if (tiles.Length == 0) return;
            beingMined.Clear();
            foreach (var e in Entities)
                if (!e.Dead && e.IsHarvester && e.Order == Order.Harvest && e.HarvestTile.HasValue) beingMined.Add(Map.Idx(e.HarvestTile.Value.X, e.HarvestTile.Value.Y));
            regrowNow.Clear();
            float scale = RegrowRate * MathF.Sqrt(Map.OreScale) * RegrowEvery * Dt;
            foreach (int i in tiles)
            {
                int target = Map.OreBase[i], ore = Map.Ore[i];
                if (ore >= target || Map.Occupant[i] != 0 || beingMined.Contains(i) || !Map.TerrainPassable(i % Map.W, i / Map.W)) continue;
                if (ore > 0 && Map.OreType[i] != Map.OreBaseType[i]) continue; // salvage of another ore lies on it
                if (ore == 0 && !roots[i] && !Map.FieldNeighbourHasOre(i)) continue;
                float amount = scale * Map.RegrowWeight(i);
                uint h = (uint)i * 2654435761u ^ (uint)Tick * 40503u;
                int add = (int)(amount + ((h >> 8) & 0xFFFF) / 65536f);
                if (add > 0) regrowNow.Add((i, Math.Min(add, target - ore)));
            }
            foreach (var (i, add) in regrowNow)
            {
                if (Map.Ore[i] == 0) { Map.OreType[i] = Map.OreBaseType[i]; oreDirty = true; }
                Map.Ore[i] += add;
                Regrown += add;
            }
        }

        // ------------------------------------------------------------------ neutral derricks

        /// <summary>
        /// A derrick's pay to its holder (steel a second, no power needed): about a third of a refinery running flat out,
        /// 90 steel a minute. Worth a fight, not a game on its own.
        /// </summary>
        public const float DerrickSteel = 1.5f;
        /// <summary>Seconds after a derrick is destroyed before a fresh neutral one rises on its site.</summary>
        public const float DerrickRespawn = 120f;
        /// <summary>What a derrick is worth when destroyed (its salvage is a quarter of this).</summary>
        static readonly Dictionary<string, int> DerrickValue = new Dictionary<string, int> { { "steel", 600 } };

        /// <summary>Destroyed derricks waiting to come back: (site origin, game time it rises again). In order of destruction.</summary>
        public readonly List<(Int2 origin, float at)> DerrickRespawns = new List<(Int2, float)>();
        bool derricksChecked;

        /// <summary>How many derricks a map this size has: two up to about 130 tiles a side, then more, up to eight.</summary>
        public static int DerrickCount(int w, int h) => Math.Clamp((int)MathF.Round(w * h / 9000f), 2, 8);

        public IEnumerable<Entity> Derricks => Entities.Where(e => !e.Dead && e.Def.Key == "derrick");

        /// <summary>
        /// Keep the map's derricks: on a ring around the middle (radius 12% of the map), evenly spaced and starting across
        /// the diagonal the first two bases share, so the first pair is the same distance from both. Sites near an
        /// existing derrick (or one waiting to come back) are skipped. Each takes the nearest open 2x2 ground: no ore or
        /// ore field, no structure, no truck lane, off the deep deposits, clear of base sites.
        /// </summary>
        void EnsureDerricks()
        {
            derricksChecked = true;
            if (Showcase != null) return;
            int want = DerrickCount(Map.W, Map.H);
            var have = Derricks.Select(d => d.Center).Concat(DerrickRespawns.Select(r => new Vec2(r.origin.X + 1, r.origin.Y + 1))).ToList();
            if (have.Count >= want) return;
            var centre = new Vec2(Map.W / 2f, Map.H / 2f);
            float radius = 0.12f * MathF.Min(Map.W, Map.H);
            for (int k = 0; k < want * 2 && have.Count < want; k++)
            {
                // The first `want` angles evenly round the ring; then the halfway angles, if sites were blocked.
                float a = 3 * MathF.PI / 4 + (k < want ? k : k - want + 0.5f) * 2 * MathF.PI / want;
                var site = centre + new Vec2(MathF.Cos(a), MathF.Sin(a)) * radius;
                if (have.Any(h => Vec2.Dist(h, site) < 8f)) continue;
                var spot = DerrickSpot(Int2.Of(site));
                if (!spot.HasValue) continue;
                var d = PlaceDerrick(spot.Value);
                have.Add(d.Center);
            }
        }

        Int2? DerrickSpot(Int2 near)
        {
            for (int r = 0; r <= 10; r++)
                for (int y = near.Y - r; y <= near.Y + r; y++)
                    for (int x = near.X - r; x <= near.X + r; x++)
                    {
                        if (Math.Max(Math.Abs(x - near.X), Math.Abs(y - near.Y)) != r) continue;
                        if (DerrickFits(new Int2(x, y))) return new Int2(x, y);
                    }
            return null;
        }

        bool DerrickFits(Int2 o)
        {
            for (int y = -1; y < 3; y++)
                for (int x = -1; x < 3; x++)
                {
                    int tx = o.X + x, ty = o.Y + y;
                    if (!Map.InBounds(tx, ty)) return false;
                    int i = Map.Idx(tx, ty);
                    if (Map.Occupant[i] != 0 || !Map.TerrainPassable(tx, ty)) return false; // open ground all round: reachable from every side
                    bool foot = x >= 0 && y >= 0 && x < 2 && y < 2;
                    if (foot && (Map.Ore[i] > 0 || Map.OreBase[i] > 0)) return false;
                }
            var c = new Vec2(o.X + 1, o.Y + 1);
            if (Map.Spawns.Any(s => Vec2.Dist(s, c) < 18) || Map.Deep.Any(d => Vec2.Dist(d.Pos, c) < 3)) return false;
            foreach (var t in Teams) if (BlocksLane(t.Id, o.X, o.Y, Defs.Get("derrick")) != null) return false;
            return true;
        }

        Entity PlaceDerrick(Int2 origin)
        {
            var d = SpawnStructure(-1, "derrick", origin, 1f);
            Emit("derrick", -1, d.Id, 0, d.Center, key: "derrick");
            return d;
        }

        void DerrickTick(Entity e, Team team)
        {
            team.Add("steel", DerrickSteel * Dt);
            team.Stats.DerrickSteel += DerrickSteel * Dt;
            e.Working = true;
        }

        /// <summary>Who may capture it now: anyone's engineer if it's neutral (whatever its health), else below CaptureThreshold.</summary>
        public static bool Capturable(Entity t) => t.Team < 0 || t.Hp <= t.Def.MaxHp * CaptureThreshold;

        void DerrickCaptured(Entity d, int old)
        {
            var t = Teams[d.Team];
            t.Stats.DerricksCaptured++;
            Emit("derrick_captured", d.Team, d.Id, 0, d.Center, key: "derrick");
            Emit("chat", -1, text: $"🛢️ {t.Name} ({t.PlayerName ?? t.Controller}) took {(old >= 0 ? $"{Teams[old].Name}'s" : "a neutral")} derrick at sector {StateView.Sector(Map, d.Center)}: +{DerrickSteel} steel/s while they hold it.");
        }

        /// <summary>A destroyed derrick leaves salvage (as any kill) and comes back neutral on its site after DerrickRespawn.</summary>
        void DerrickDestroyed(Entity d) => DerrickRespawns.Add((d.Origin, Time + DerrickRespawn));

        /// <summary>A team that leaves or is out gives its derricks back: they stand neutral (and whole) for anyone to take.</summary>
        void ReleaseDerricks(int team)
        {
            foreach (var d in Derricks.Where(d => d.Team == team).ToList())
            {
                d.Team = -1; d.Hp = d.Def.MaxHp; d.Working = false; d.Burning = false; d.LastAttackerTeam = -1;
                Emit("derrick_released", -1, d.Id, 0, d.Center, key: "derrick");
            }
        }

        /// <summary>Once a second: destroyed derricks rise again when their time comes (if the site is clear), and a world
        /// that had none (a game from before them) gets its set.</summary>
        void DerrickUpkeepTick()
        {
            if (!derricksChecked) EnsureDerricks();
            if (Tick % TickRate != 0 || DerrickRespawns.Count == 0) return;
            for (int i = 0; i < DerrickRespawns.Count; i++)
            {
                var (origin, at) = DerrickRespawns[i];
                if (Time < at) continue;
                if (DerrickFits(origin)) { PlaceDerrick(origin); DerrickRespawns.RemoveAt(i); i--; }
                else DerrickRespawns[i] = (origin, Time + 10f); // something stands there: try again shortly
            }
        }

        // ------------------------------------------------------------------ salvage from kills

        /// <summary>Share of a destroyed thing's cost left as salvage ore when an enemy kills it (infantry leave less).</summary>
        public const float KillSalvageShare = 0.25f, InfantrySalvageShare = 0.10f;
        /// <summary>Piles smaller than this aren't left (a rifleman leaves 4 iron ore; a single bullet's worth isn't worth a truck's trip).</summary>
        public const int MinSalvagePile = 4;
        /// <summary>At most this much salvage per tile from one kill: a big wreck scatters over several tiles.</summary>
        public const int SalvagePerTile = 300;

        /// <summary>What a command center is worth when nothing builds it (the same figure a leaving player's base uses).</summary>
        static readonly Dictionary<string, int> HqValue = new Dictionary<string, int> { { "iron_ore", 1500 }, { "copper_ore", 500 } };

        /// <summary>
        /// What a thing is worth in materials: its cost, or for things that aren't bought (deployed structures), the cost of
        /// the unit that deploys into it (an outpost is an outpost truck, a deep mine a drill rig).
        /// </summary>
        public Dictionary<string, int> ValueOf(EntityDef d)
        {
            if (d.Cost.Count > 0) return d.Cost;
            if (d.Key == "derrick") return DerrickValue;
            foreach (var u in Defs.All.Values) if (u.DeploysInto == d.Key && u.Cost.Count > 0) return u.Cost;
            if (d.Key == "command_center") return HqValue;
            return d.Cost;
        }

        /// <summary>Ore value of a cost, per ore type, using the salvage conversion (circuits are worth two copper ore, and so on).</summary>
        static void AddValue(float[] into, Dictionary<string, int> cost, float factor)
        {
            foreach (var kv in cost) if (SalvageOf.TryGetValue(kv.Key, out var s)) into[s.ore] += kv.Value * s.mult * factor;
        }

        /// <summary>
        /// The salvage an enemy kill leaves: a share of the victim's value (and of any passengers' it took down with it) and
        /// all the ore a mining truck was carrying. `value` is what the killer destroyed, at full value. Mines leave nothing.
        /// </summary>
        float[] KillSalvage(Entity t, out float value)
        {
            var full = new float[4];
            var share = new float[4];
            value = 0;
            if (t.IsMine) return null;
            void Victim(Entity v)
            {
                float progress = v.IsStructure ? v.BuildProgress : 1f;
                var cost = ValueOf(v.Def);
                AddValue(full, cost, progress);
                AddValue(share, cost, progress * (v.Def.Armor == Armor.Infantry ? InfantrySalvageShare : KillSalvageShare));
            }
            Victim(t);
            foreach (var pid in t.Passengers) { var p = Get(pid); if (p != null) Victim(p); }
            if (t.IsHarvester && t.Cargo > 0 && t.CargoType >= 0) share[t.CargoType] += t.Cargo; // the load spills too
            value = full.Sum();
            return share;
        }

        /// <summary>
        /// Leave salvage ore where something was destroyed: on its footprint and the open ground around it, nearest first,
        /// topping up a nearby pile of the same ore before starting a new one (so a firefight's dead pile up rather than
        /// scattering specks). Never on structures, rock or water, never past the per-tile cap, and never on a tile that
        /// holds (or regrows) a different ore. Both sides hear about it through one merged alert per area.
        /// </summary>
        void DropSalvage(Entity victim, float[] value, int killer)
        {
            var foot = new HashSet<Int2>();
            if (victim.IsStructure)
                for (int y = 0; y < victim.Def.SizeY; y++) for (int x = 0; x < victim.Def.SizeX; x++) foot.Add(new Int2(victim.Origin.X + x, victim.Origin.Y + y));
            // A derrick's site must stay clear for the one that rises there again: its salvage lands around it, not on it.
            var avoid = victim.Def.Key == "derrick" ? foot : null;
            if (avoid != null) foot = new HashSet<Int2>();
            DropSalvage(victim.Center, foot, victim.IsStructure ? (victim.Def.SizeX + 1) / 2 + 3 : 3, value, victim.Def.Key, victim.Id, killer, victim.Team, avoid);
        }

        /// <summary>The kitchen sink's wrecks: what an enemy kill of a `key` at `at` would leave.</summary>
        internal int ShowcaseSalvage(string key, Vec2 at, int killer, int victimTeam)
        {
            var value = new float[4];
            var d = Def(key);
            AddValue(value, ValueOf(d), d.Armor == Armor.Infantry ? InfantrySalvageShare : KillSalvageShare);
            return DropSalvage(at, new HashSet<Int2>(), 3, value, key, 0, killer, victimTeam);
        }

        int DropSalvage(Vec2 at, HashSet<Int2> foot, int reach, float[] value, string key, int victimId, int killer, int victimTeam, HashSet<Int2> avoid = null)
        {
            int placed = PlaceSalvage(at, foot, value, reach, avoid);
            if (placed <= 0) return 0;
            if (killer >= 0 && killer < Teams.Count) Teams[killer].Stats.SalvageLeft += placed;
            Emit("salvage", killer, victimId, 0, at, key: key, text: $"{placed} ore of salvage from {key} #{victimId}");
            foreach (int team in new[] { killer, victimTeam })
            {
                if (team < 0 || team >= Teams.Count || Teams[team].Left || Teams[team].Defeated) continue;
                var a = Alerts.Raise(this, team, "salvage_dropped", Priority.Medium, at);
                a.Amount += placed;
            }
            return placed;
        }

        /// <summary>Put salvage ore down around a point (footprint tiles first). Returns the amount placed.</summary>
        int PlaceSalvage(Vec2 at, HashSet<Int2> footprint, float[] value, int reach, HashSet<Int2> avoid = null)
        {
            int placed = 0;
            int cx = (int)at.X, cy = (int)at.Y;
            var tiles = new List<(Int2 t, float d)>();
            for (int y = cy - reach; y <= cy + reach; y++)
                for (int x = cx - reach; x <= cx + reach; x++)
                    if (Map.InBounds(x, y)) tiles.Add((new Int2(x, y), Vec2.Dist(new Int2(x, y).Center, at)));
            for (int k = 0; k < 4; k++)
            {
                int left = (int)value[k];
                if (left < MinSalvagePile) continue;
                // Footprint first, then nearest; an existing pile of the same ore within reach is topped up first.
                var order = tiles.Where(c => SalvageFits(c.t, k) && (avoid == null || !avoid.Contains(c.t)))
                                 .OrderBy(c => (footprint.Contains(c.t) ? -100f : 0f) + c.d - (Map.Ore[Map.Idx(c.t.X, c.t.Y)] > 0 ? 1.5f : 0f))
                                 .ThenBy(c => c.t.Y).ThenBy(c => c.t.X).ToList();
                foreach (var (t, _) in order)
                {
                    if (left <= 0) break;
                    int i = Map.Idx(t.X, t.Y);
                    int put = Math.Min(left, Math.Min(SalvagePerTile, Map.MaxOrePerTile - Map.Ore[i]));
                    if (put <= 0) continue;
                    Map.OreType[i] = (byte)k;
                    if (Map.Ore[i] == 0) oreDirty = true;
                    Map.Ore[i] += put;
                    left -= put; placed += put;
                }
            }
            return placed;
        }

        /// <summary>Salvage of ore type k can lie here: open ground, no structure, and no other ore on it (or regrowing there).</summary>
        bool SalvageFits(Int2 t, int k)
        {
            int i = Map.Idx(t.X, t.Y);
            if (!Map.TerrainPassable(t.X, t.Y) || Map.Occupant[i] != 0) return false;
            if (Map.Ore[i] > 0) return Map.OreType[i] == k && Map.Ore[i] < Map.MaxOrePerTile;
            return Map.SalvageMayLand(i, k);
        }
    }

    public partial class Map
    {
        /// <summary>Salvage of ore type k may land on this (empty) tile: anywhere but a field of another ore (which regrows there).</summary>
        public bool SalvageMayLand(int i, int k) => OreBase[i] == 0 || OreBaseType[i] == k;
        /// <summary>Whether ore grows back on this tile (it was part of a field), and how much it grows back to.</summary>
        public bool Regrows(int i) => OreBase[i] > 0;
        public int RegrowTarget(int i) => OreBase[i];

        /// <summary>Loaded from a snapshot older than regrowth: Game.Restore rebuilds OreBase from the game's seed.</summary>
        public bool BaseMissing;

        /// <summary>
        /// For a game saved before regrowth: the fields' original amounts, rebuilt. The map as first generated (same seed,
        /// size and richness) gives the starting area's fields exactly, if its terrain still matches; each base site added
        /// as the map grew gets the iron and copper fields (and the contested crystal and uranium) a join lays down,
        /// placed the same way. Only where there's open, unclaimed ground. Ore on the ground now is left as it is.
        /// </summary>
        public void RebuildBase(int seed, int initialSize, float oreScale)
        {
            BaseMissing = false;
            fieldsChanged = true;
            int size = Math.Clamp(initialSize, MinSize, MaxSize);
            if (size <= W && size <= H)
            {
                var g = Generate(size, size, seed, oreScale);
                int same = 0, total = size * size;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        if ((g.Tiles[g.Idx(x, y)] >= Terrain.Rock) == (Tiles[Idx(x, y)] >= Terrain.Rock)) same++;
                if (same >= total * 0.9f)
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            int a = g.Idx(x, y), b = Idx(x, y);
                            if (g.OreBase[a] > 0 && TerrainPassable(x, y)) { OreBase[b] = g.OreBase[a]; OreBaseType[b] = g.OreBaseType[a]; }
                        }
            }
            // Base sites added by joins: the fields Grown lays around a new site (it sits 9 tiles in from the new edge).
            for (int s = 4; s < Spawns.Count; s++)
            {
                var sp = Spawns[s];
                int grownTo = (int)MathF.Round(MathF.Max(sp.X, sp.Y)) + 9;
                var centre = new Vec2(grownTo / 2f, grownTo / 2f);
                var dir = (centre - sp).Normalized; var side = new Vec2(-dir.Y, dir.X);
                var rng = new Random(seed ^ (int)(sp.X * 7919 + sp.Y * 31));
                void Field(Vec2 c, int r, int min, int max, byte type) => BaseField(rng, (int)c.X, (int)c.Y, r, min, max, type, oreScale);
                Field(sp + dir * 9f, 3, 260, 460, Iron);
                Field(sp + dir * 5f + side * 7f, 2, 200, 340, Copper);
                Field(Vec2.Lerp(sp, centre, 0.45f) + side * 6f, 2, 90, 170, Crystal);
                Field(Vec2.Lerp(sp, centre, 0.55f) - side * 5f, 1, 70, 130, Uranium);
            }
        }

        /// <summary>A field's original amounts only (no ore now), on open ground no other field claims.</summary>
        void BaseField(Random rng, int cx, int cy, int r, int min, int max, byte type, float oreScale)
        {
            for (int y = cy - r - 1; y <= cy + r + 1; y++)
                for (int x = cx - r - 1; x <= cx + r + 1; x++)
                {
                    if (!InBounds(x, y)) continue;
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d > r + (float)rng.NextDouble() * 0.9f) continue;
                    int i = Idx(x, y);
                    if (!TerrainPassable(x, y) || OreBase[i] > 0 || (Ore[i] > 0 && OreType[i] != type)) continue;
                    OreBase[i] = Math.Min(MaxOrePerTile, (int)(rng.Next(min, max) * (1.2f - d / (r + 1)) * oreScale));
                    OreBaseType[i] = type;
                }
        }

        // Caches over OreBase (rebuilt when the fields change; not state): every field tile, and each field's root.
        bool fieldsChanged = true;
        int[] fieldTiles;
        bool[] fieldRoots;

        /// <summary>
        /// Every field tile, and each field's root: the tile that held the most ore when the map was made. A field
        /// regrows from the ore it has left; a field mined to nothing starts again from its root.
        /// </summary>
        internal void FieldIndex(out int[] tiles, out bool[] roots)
        {
            if (fieldsChanged || fieldTiles == null)
            {
                var list = new List<int>();
                fieldRoots = new bool[W * H];
                var seen = new bool[W * H];
                var stack = new Stack<int>();
                for (int s = 0; s < OreBase.Length; s++)
                {
                    if (OreBase[s] <= 0 || seen[s]) continue;
                    int best = s; seen[s] = true; stack.Push(s);
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop(); list.Add(i);
                        if (OreBase[i] > OreBase[best] || (OreBase[i] == OreBase[best] && i < best)) best = i;
                        int x = i % W, y = i / W;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (!InBounds(nx, ny)) continue;
                                int j = Idx(nx, ny);
                                if (seen[j] || OreBase[j] <= 0 || OreBaseType[j] != OreBaseType[s]) continue;
                                seen[j] = true; stack.Push(j);
                            }
                    }
                    fieldRoots[best] = true;
                }
                list.Sort();
                fieldTiles = list.ToArray();
                fieldsChanged = false;
            }
            tiles = fieldTiles; roots = fieldRoots;
        }

        /// <summary>OreBase was edited directly (the kitchen sink): rebuild the field index.</summary>
        internal void FieldsChanged() => fieldsChanged = true;

        /// <summary>A neighbouring tile of the same field still has ore: this one can grow back from it.</summary>
        internal bool FieldNeighbourHasOre(int i)
        {
            int x = i % W, y = i / W;
            byte type = OreBaseType[i];
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (!InBounds(nx, ny)) continue;
                    int j = Idx(nx, ny);
                    if (OreBase[j] > 0 && OreBaseType[j] == type && Ore[j] > 0 && OreType[j] == type) return true;
                }
            return false;
        }

        /// <summary>
        /// How fast a tile regrows relative to the map's centre (1 there, RegrowEdge in the corners): falls off steeply
        /// with distance, so the middle recovers and the edges barely do.
        /// </summary>
        public float RegrowWeight(int i)
        {
            float dx = i % W + 0.5f - W / 2f, dy = i / W + 0.5f - H / 2f;
            float d = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy) / (0.5f * MathF.Sqrt(W * W + H * H)));
            return World.RegrowEdge + (1f - World.RegrowEdge) * MathF.Pow(1f - d, World.RegrowFalloff);
        }
    }
}
