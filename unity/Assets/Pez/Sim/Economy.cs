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
        /// RegrowEdge in the corners), RegrowByOre (iron and copper regrow faster than crystal and uranium: crystal's fields
        /// ring the middle, where regrowth is fastest, and it used to take over) and the square root of the map's ore
        /// richness (a scarce map regrows less, but not in proportion: its stalemates need the long tail most).
        /// Measured on a mined-bare 96-tile map (mean of four seeds), over the first 20 minutes:
        ///   richness 1:   6.6 ore/s (iron 2.6, copper 1.3, crystal 2.0, uranium 0.7), 41% of it in the middle;
        ///                 6.3 in minutes 20-30, when the middle's crystal and uranium have refilled and iron and copper keep coming;
        ///   richness 0.3: 3.2 ore/s (iron 1.3, copper 0.65, crystal 1.0, uranium 0.3), 40% of it in the middle.
        /// (Before RegrowByOre: 6.9 and 2.9 ore/s, of which iron 1.6 and copper 0.6 at richness 1, 68% in the middle.)
        /// A deep mine pumps 4/s, so deep mining stays the mid-game economy and regrowth is the long tail that keeps a
        /// base's iron and copper trickling and makes the middle worth fighting for.
        /// </summary>
        public const float RegrowRate = 0.12f, RegrowEdge = 0.04f, RegrowFalloff = 2.5f;
        /// <summary>Regrowth speed of each ore relative to the rest, per tile (in Defs.Ores order: iron_ore, copper_ore, crystal, uranium).</summary>
        public static readonly float[] RegrowByOre = { 1.5f, 2f, 0.5f, 0.5f };
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
                // The field's own ore grows back toward its original amount whatever salvage lies on it (salvage isn't field ore).
                int target = Map.OreBase[i], ore = Map.Ore[i], field = Map.FieldOre(i);
                if (field >= target || ore >= Map.MaxOrePerTile || Map.Occupant[i] != 0 || beingMined.Contains(i) || !Map.TerrainPassable(i % Map.W, i / Map.W)) continue;
                if (ore > 0 && Map.OreType[i] != Map.OreBaseType[i]) continue; // salvage of another ore lies on it
                if (field == 0 && !roots[i] && !Map.FieldNeighbourHasOre(i)) continue;
                float amount = scale * Map.RegrowWeight(i) * RegrowByOre[Map.OreBaseType[i]];
                uint h = (uint)i * 2654435761u ^ (uint)Tick * 40503u;
                int add = (int)(amount + ((h >> 8) & 0xFFFF) / 65536f);
                if (add > 0) regrowNow.Add((i, Math.Min(Math.Min(add, target - field), Map.MaxOrePerTile - ore)));
            }
            foreach (var (i, add) in regrowNow)
            {
                if (Map.Ore[i] == 0) { Map.OreType[i] = Map.OreBaseType[i]; oreDirty = true; }
                Map.Ore[i] += add;
                Regrown += add;
            }
        }

        // ------------------------------------------------------------------ defence upkeep

        /// <summary>Armed defences a team keeps for free; each one beyond these costs UpkeepPercent of its steel price a minute.</summary>
        public const int FreeDefences = 4;
        public const float UpkeepPercent = 4f;

        /// <summary>Steel a minute one defence costs once it's past the free ones: UpkeepPercent of its steel price (a gun turret 6, a SAM site 8, a laser tower 10).</summary>
        public static float UpkeepPerMinute(EntityDef d) => UpkeepPerMinute(d.Cost.TryGetValue("steel", out var steel) ? steel : 0);
        public static float UpkeepPerMinute(int steelPrice) => steelPrice * UpkeepPercent / 100f;

        /// <summary>A team's finished armed defences (turrets, SAM sites, laser towers), oldest first.</summary>
        public List<Entity> Defences(int team) =>
            Entities.Where(e => !e.Dead && e.Team == team && e.IsStructure && e.IsArmed && e.IsComplete).OrderBy(e => e.Id).ToList();

        /// <summary>
        /// Upkeep for defences, every second: a team's first FreeDefences armed defences are free, and each one past
        /// those costs UpkeepPercent of its steel price a minute, so a big wall of expensive towers costs something real
        /// (11 laser towers: 70 steel a minute, 11 gun turrets: 42). What the stockpile can't pay for goes offline, newest
        /// first: it stands and draws no power, but doesn't fire until the steel is there again. So a base can always
        /// defend itself, and walling up for good has a running cost.
        /// </summary>
        void Upkeep()
        {
            if (Tick % TickRate != 0) return;
            foreach (var t in Teams)
            {
                if (t.Left || t.Defeated) continue;
                var defs = Defences(t.Id);
                float steel = t.Stock.TryGetValue("steel", out var s) ? s : 0, paid = 0;
                var wentOff = new List<Entity>();
                bool cameBack = false, changed = false, broke = false;
                for (int i = 0; i < defs.Count; i++)
                {
                    bool off = false;
                    if (i >= FreeDefences)
                    {
                        float due = UpkeepPerMinute(defs[i].Def) / 60f;
                        if (!broke && steel + 1e-4f >= due) { due = MathF.Min(due, steel); steel -= due; paid += due; }
                        else { broke = true; off = true; } // the newer ones can't be paid either
                    }
                    if (off && !defs[i].Offline) wentOff.Add(defs[i]);
                    if (!off && defs[i].Offline) cameBack = true;
                    if (defs[i].Offline != off) { defs[i].Offline = off; changed = true; }
                }
                if (paid > 0) { t.Add("steel", -paid); t.Stats.UpkeepPaid += paid; }
                if (changed) UpdatePower(); // offline defences draw no power
                if (wentOff.Count > 0)
                {
                    var a = Alerts.Raise(this, t.Id, "defences_offline", Priority.High, wentOff[0].Center, hit: false);
                    a.Lost.Add($"{string.Join(", ", wentOff.Select(e => $"{e.Def.Key} #{e.Id}"))} offline: upkeep is {UpkeepPercent:0.#}% of a defence's steel price a minute for each defence past your first {FreeDefences}, and you're out of steel. They hold fire and draw no power until it's paid (newest go offline first)");
                    Emit("defences_offline", t.Id, wentOff[0].Id, 0, wentOff[0].Center, text: $"{wentOff.Count} defence(s) went offline: no steel for their upkeep ({UpkeepPercent:0.#}% of their steel price a minute each past the first {FreeDefences})");
                }
                if (cameBack && defs.All(e => !e.Offline)) Emit("defences_online", t.Id, text: "all your defences are back online: their upkeep is paid");
            }
        }

        // ------------------------------------------------------------------ fusion reactor

        /// <summary>
        /// Plasma a fusion reactor burns a second at full output (+500 power): 108 plasma an hour, from 216 uranium.
        /// Its price is about 750 ore (400 steel, 100 circuits, 25 plasma: 500 iron, 200 copper and 50 uranium) against
        /// 1,250 iron ore for the five power plants that make the same power, in 9 tiles instead of 20. Counting its fuel
        /// it stays cheaper in ore than the plants for the first 2.3 hours (750 + 216 an hour, against 1,250); what you
        /// pay for that is one building carrying all the power, and fuel to find.
        /// </summary>
        public const float FusionBurn = 0.03f;

        /// <summary>The power a reactor makes at this share of full output.</summary>
        public static int ReactorPower(Entity e, float share) => (int)MathF.Round(e.Def.Power * share);

        /// <summary>
        /// A reactor burns each tick's plasma from what the stockpile holds above 'reserve' and 'save_for' (they hold it
        /// back from reactors as from converters), and makes the share of its power that burn pays for: short of plasma
        /// it makes less power rather than none. Returns whether its power changed, so the team's power is added up again.
        /// A tick's burn (0.0015) is less than half the gap between floats once a stockpile holds tens of thousands, so
        /// taking it straight off the stock would change nothing there (free fuel). The team keeps what its reactors have
        /// burned in PlasmaOwed instead, and SettleFusionBurn takes it off the stockpile once a second (0.03 a reactor:
        /// well above the gap at any stock a game reaches). Every reactor's check, and Team.Amount, count the owed part
        /// as already spent, so reactors can't burn plasma that isn't there and a purchase can't spend plasma that's burned.
        /// </summary>
        bool FusionTick(Entity e, Team team)
        {
            float burn = FusionBurn * Dt;
            float free = (team.Stock.TryGetValue("plasma", out var pl) ? pl : 0) - team.Reserved("plasma") - team.PlasmaOwed;
            float paid = MathF.Max(0f, MathF.Min(burn, free));
            team.PlasmaOwed += paid;
            float share = paid / burn;
            bool changed = ReactorPower(e, share) != ReactorPower(e, e.Output);
            e.Output = share;
            e.Working = ReactorPower(e, share) > 0;
            return changed;
        }

        /// <summary>Once a second: take the plasma the team's reactors have burned off its stockpile (never more than it holds).</summary>
        void SettleFusionBurn()
        {
            foreach (var t in Teams)
            {
                if (t.PlasmaOwed <= 0) continue;
                float take = MathF.Min(t.PlasmaOwed, MathF.Max(0f, t.Stock.TryGetValue("plasma", out var pl) ? pl : 0));
                t.SettleOwed(take);
            }
        }

        /// <summary>A reactor that has just been finished or captured starts at the share its new team can pay now, so its power counts from the first sum (not the old owner's, and not none).</summary>
        void StartReactor(Entity e)
        {
            if (e.Def.Key == "fusion_reactor" && e.Team >= 0) FusionTick(e, Teams[e.Team]);
        }

        /// <summary>For the low-power alert (the owner's only: events can reach other players' copies): when a fusion reactor is making less than its full power for want of plasma, say so and what to do.</summary>
        string ReactorShortNote(Team t)
        {
            var r = Entities.FirstOrDefault(e => !e.Dead && e.Team == t.Id && e.Def.Key == "fusion_reactor" && e.IsComplete && e.Output < 0.995f);
            return r == null ? "" : $". Your fusion_reactor #{r.Id} is short of plasma and makes only {StateView.Pct(r.Output)}% of its {r.Def.Power} power: enrich more uranium, or lift the reserve/save_for on plasma";
        }

        // ------------------------------------------------------------------ neutral derricks

        /// <summary>
        /// A derrick's pay to its holder (steel a second, no power needed): about a third of a refinery running flat out,
        /// 90 steel a minute. Worth a fight, not a game on its own.
        /// </summary>
        public const float DerrickSteel = 1.5f;
        /// <summary>Seconds after a derrick is destroyed before a fresh neutral one rises on its site.</summary>
        public const float DerrickRespawn = 120f;
        /// <summary>What a derrick is worth when destroyed: its kill value (Match.HowScored says "about 600"); its salvage is a quarter of this.</summary>
        public const int DerrickWorth = 600;
        static readonly Dictionary<string, int> DerrickValue = new Dictionary<string, int> { { "steel", DerrickWorth } };

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

        /// <summary>When each team was last told about each held derrick its units stopped beside: (team, derrick id) to game time. Saved, so a resumed game doesn't say it again.</summary>
        readonly Dictionary<(int team, int id), float> derrickHinted = new Dictionary<(int, int), float>();

        /// <summary>
        /// Armed units stopping beside an enemy-held derrick leave it alone (AcquireTarget): say so, and how to take or
        /// destroy it, once per derrick every 2 minutes (test players Scarlett and Clark left tanks idle by one).
        /// </summary>
        void DerrickHint(Entity e)
        {
            if (!e.IsArmed || IsProtected(e.Team)) return;
            var d = Derricks.FirstOrDefault(x => x.Team >= 0 && x.Team != e.Team && x.DistFrom(e.Pos) <= e.Def.Sight && e.Def.Weapon.CanHit(x.Def) && IsVisibleTo(e.Team, x));
            if (d == null) return;
            if (derrickHinted.TryGetValue((e.Team, d.Id), out var said) && Time - said < 120f) return;
            derrickHinted[(e.Team, d.Id)] = Time;
            if (derrickHinted.Count > 200) foreach (var k in derrickHinted.Where(kv => Time - kv.Value >= 120f).Select(kv => kv.Key).ToList()) derrickHinted.Remove(k);
            Emit("derrick_skipped", e.Team, e.Id, d.Id, d.Center, key: e.Def.Key,
                 text: $"{e.Def.Key} #{e.Id} stopped by {Teams[d.Team].Name}'s derrick #{d.Id} at {(int)d.Center.X},{(int)d.Center.Y} and leaves it alone: units only shoot a held derrick when told to (it's worth capturing). " +
                       $"To take it: attack with \"until\":\"capturable\", then capture it with an engineer. To destroy it (about {DerrickWorth} kill value, and salvage): attack it, or attack_move with \"derricks\":true");
        }

        void DerrickCaptured(Entity d, int old)
        {
            var t = Teams[d.Team];
            t.Stats.DerricksCaptured++;
            Emit("derrick_captured", d.Team, d.Id, 0, d.Center, key: "derrick");
            Emit("chat", -1, text: $"{t.Name} ({t.PlayerName ?? t.Controller}) took {(old >= 0 ? $"{Teams[old].Name}'s" : "a neutral")} derrick at sector {StateView.Sector(d.Center)}: +{DerrickSteel} steel/s while they hold it.");
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

        /// <summary>
        /// The share of its value a team's base leaves as salvage when the team leaves or is knocked out (Dismantle): 60% of a
        /// structure's, half a unit's. Its stockpile goes in full.
        /// </summary>
        public const float DismantleStructureShare = 0.6f, DismantleUnitShare = 0.5f;

        /// <summary>
        /// Value counted for each kill that predates kill-value tracking: the median ore value of the standard armed units
        /// (a fair stand-in when we know how many things a team destroyed but not what).
        /// </summary>
        public static int TypicalKillValue
        {
            get
            {
                if (typicalKill > 0) return typicalKill;
                var vals = new List<float>();
                foreach (var d in Defs.All.Values)
                    if (!d.IsStructure && !d.IsMine && d.Weapon != null && d.Buildable && d.OwnerTeam < 0 && d.Cost.Count > 0)
                    { var v = new float[4]; AddValue(v, d.Cost, 1f); vals.Add(v.Sum()); }
                vals.Sort();
                return typicalKill = vals.Count == 0 ? 200 : (int)vals[vals.Count / 2];
            }
        }
        static int typicalKill;

        /// <summary>Count kills that were never valued (they predate kill-value tracking) at TypicalKillValue each.</summary>
        public void BackfillKillValue()
        {
            foreach (var t in Teams)
            {
                var s = t.Stats;
                if (s.Kills <= s.KillsValued) continue;
                s.KillValue += (s.Kills - s.KillsValued) * TypicalKillValue;
                s.KillsValued = s.Kills;
            }
        }

        /// <summary>
        /// What a thing is worth in materials, the one price list behind kill value, kill salvage, a leaving team's base and
        /// the showcase: its cost, or for things that aren't bought (deployed structures), the cost of the unit that deploys
        /// into it (an outpost is an outpost truck, a deep mine a drill rig, a command center a construction truck). A
        /// derrick is DerrickWorth.
        /// </summary>
        public Dictionary<string, int> ValueOf(EntityDef d)
        {
            if (d.Cost.Count > 0) return d.Cost;
            if (d.Key == "derrick") return DerrickValue;
            foreach (var u in Defs.All.Values) if (u.DeploysInto == d.Key && u.Cost.Count > 0) return u.Cost;
            return d.Cost;
        }

        /// <summary>Add an amount of one item, as the raw ores it's made from (OreMix), to a per-ore total.</summary>
        static void AddOre(float[] into, string item, float amount)
        {
            if (!OreMix.TryGetValue(item, out var mix)) return;
            for (int k = 0; k < mix.Length; k++) into[k] += amount * mix[k];
        }

        /// <summary>Ore value of a cost, per ore type (circuits are two copper ore and an iron ore, and so on), times `factor`.</summary>
        static void AddValue(float[] into, Dictionary<string, int> cost, float factor)
        {
            foreach (var kv in cost) AddOre(into, kv.Key, kv.Value * factor);
        }

        /// <summary>
        /// Add `share` of what an entity is worth as ore: the one place a unit or structure is priced, for kill value, kill
        /// salvage and a dismantled base alike. A structure counts at its full price however far it got: its owner paid all of
        /// it when placing it. What the arena's gift paid for (Entity.GrantedShare: all of it, for the starting base and units) isn't
        /// worth anything: not the team's own value, so there's no kill value and no salvage in that share, whoever holds it by then.
        /// </summary>
        void AddWorth(float[] into, Entity e, float share)
        {
            if (e.GrantedShare < 1f) AddValue(into, ValueOf(e.Def), share * (1f - e.GrantedShare));
        }

        /// <summary>
        /// A save from before salvage counted as mined when it's delivered (SalvageMined went up at pickup): the salvage still aboard
        /// a truck at save time is already in the count, and off the economy score, though it was never delivered (if that truck is
        /// lost it would stay off for good). A load can't be told from field ore there, so each truck's whole load comes back out of
        /// its team's count, not below zero. A guess, and it errs toward the player: a little too much comes back out, so the
        /// score ends a little too high rather than too low.
        /// </summary>
        internal void MigrateSalvageCount()
        {
            foreach (var e in Entities)
                if (!e.Dead && e.IsHarvester && e.Cargo > 0 && e.Team >= 0 && e.Team < Teams.Count)
                    Teams[e.Team].Stats.SalvageMined = Math.Max(0, Teams[e.Team].Stats.SalvageMined - e.Cargo);
        }

        /// <summary>
        /// A team's stockpile as ore, emptying it: what a spill or a dismantled base gives up. Only what the team holds above
        /// what it was given (Team.Own) counts; the gift goes with the rest of the stockpile, and nothing is left owing.
        /// </summary>
        float[] TakeStockpile(Team t)
        {
            var value = new float[4];
            foreach (var kv in t.Stock) AddOre(value, kv.Key, t.Own(kv.Key)); // (Own leaves out the plasma its reactors have burned: that's gone)
            t.Stock.Clear(); t.Granted.Clear(); t.PlasmaOwed = 0;
            return value;
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
                AddWorth(full, v, 1f);
                AddWorth(share, v, v.Def.Armor == Armor.Infantry ? InfantrySalvageShare : KillSalvageShare);
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
                    Map.Salvage[i] = Map.SalvageOn(i) + put; // all of it is salvage
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

        /// <summary>Loaded from a snapshot older than the salvage layer: DeriveSalvage reads it off the map instead.</summary>
        public bool SalvageMissing;
        /// <summary>
        /// For a game saved before the salvage layer: ore on ground that was never a field was all salvage (and field tiles' ore
        /// was the field's own, as the old count had it).
        /// </summary>
        public void DeriveSalvage()
        {
            for (int i = 0; i < Ore.Length; i++) Salvage[i] = OreBase[i] == 0 ? Ore[i] : 0;
            SalvageMissing = false;
        }

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
                var g = Generate(size, size, seed, oreScale, extraFields: false); // as it was made: the neutral fields came later
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
                    if (OreBase[j] > 0 && OreBaseType[j] == type && FieldOre(j) > 0 && OreType[j] == type) return true;
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
