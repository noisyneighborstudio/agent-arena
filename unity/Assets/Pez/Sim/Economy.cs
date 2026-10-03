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
            DropSalvage(victim.Center, foot, victim.IsStructure ? (victim.Def.SizeX + 1) / 2 + 3 : 3, value, victim.Def.Key, victim.Id, killer, victim.Team);
        }

        /// <summary>The kitchen sink's wrecks: what an enemy kill of a `key` at `at` would leave.</summary>
        internal int ShowcaseSalvage(string key, Vec2 at, int killer, int victimTeam)
        {
            var value = new float[4];
            var d = Def(key);
            AddValue(value, ValueOf(d), d.Armor == Armor.Infantry ? InfantrySalvageShare : KillSalvageShare);
            return DropSalvage(at, new HashSet<Int2>(), 3, value, key, 0, killer, victimTeam);
        }

        int DropSalvage(Vec2 at, HashSet<Int2> foot, int reach, float[] value, string key, int victimId, int killer, int victimTeam)
        {
            int placed = PlaceSalvage(at, foot, value, reach);
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
        int PlaceSalvage(Vec2 at, HashSet<Int2> footprint, float[] value, int reach)
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
                var order = tiles.Where(c => SalvageFits(c.t, k))
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
        /// <summary>Salvage of ore type k may land on this (empty) tile. Every tile, until fields can regrow.</summary>
        public bool SalvageMayLand(int i, int k) => true;
        /// <summary>Whether ore can grow back on this tile, and how much it grows back to (none yet).</summary>
        public bool Regrows(int i) => false;
        public int RegrowTarget(int i) => 0;
    }
}
