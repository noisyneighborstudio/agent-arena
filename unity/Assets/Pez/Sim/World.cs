using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public class ProdItem
    {
        public string Key;
        public float Progress;   // seconds of work done
        public int StructureId;  // structures: the foundation entity being built; units: the building chosen to train it (0 = the first)
    }

    /// <summary>A surveyor's flag on a deep deposit: the team's mining zone, with the same id as the deposit.</summary>
    public class ZoneFlag
    {
        public int ZoneId;
        public int FlaggedBy;     // the surveyor's entity id
        public float FlaggedAt;   // game time, seconds
    }

    public class TeamStats
    {
        public int UnitsBuilt, StructuresBuilt, UnitsLost, StructuresLost, Kills, OreMined;
        public readonly Dictionary<string, int> Built = new Dictionary<string, int>();
        public void Count(string key) => Built[key] = (Built.TryGetValue(key, out var n) ? n : 0) + 1;
    }

    public class Team
    {
        public int Id;
        public string Name;
        public string Controller = "human"; // human | ai | llm
        public string PlayerName;
        /// <summary>Standing instructions from the human commander to whoever (usually an LLM) plays this team.</summary>
        public string StandingOrders = "";
        public int OrdersVersion;
        /// <summary>Team stockpile: raw ores and manufactured materials (see Defs.Items).</summary>
        public readonly Dictionary<string, float> Stock = new Dictionary<string, float>();
        /// <summary>Net change per second of each item over the last second, for display.</summary>
        public readonly Dictionary<string, float> Rates = new Dictionary<string, float>();
        public int PowerProduced, PowerUsed;
        /// <summary>Enemy stealth units this team can currently see.</summary>
        public readonly HashSet<int> Detected = new HashSet<int>();
        /// <summary>Enemies briefly revealed by firing on this team (entity id -> game time the reveal ends).</summary>
        public readonly Dictionary<int, float> Revealed = new Dictionary<int, float>();

        public int Amount(string item) => Stock.TryGetValue(item, out var v) ? (int)v : 0;
        public void Add(string item, float n) => Stock[item] = (Stock.TryGetValue(item, out var v) ? v : 0) + n;
        public string Missing(Dictionary<string, int> cost)
        {
            var lacking = cost.Where(kv => Amount(kv.Key) < kv.Value).Select(kv => $"{kv.Key} {Amount(kv.Key)}/{kv.Value}").ToList();
            return lacking.Count == 0 ? null : "not enough " + string.Join(", ", lacking);
        }
        public void Pay(Dictionary<string, int> cost, float factor = 1f) { foreach (var kv in cost) Add(kv.Key, -kv.Value * factor); }
        public bool Defeated;
        public Vec2 StartPos;
        /// <summary>Tiles currently in sight of any of this team's entities.</summary>
        public bool[] Visible;
        /// <summary>Tiles this team has ever seen. Everything else is shroud.</summary>
        public bool[] Explored;
        /// <summary>Open arena: the player left for good (their base became salvage).</summary>
        public bool Left;
        /// <summary>Resigned as lost because nothing they had could change the game any more.</summary>
        public bool Resigned;
        /// <summary>When the team was first seen unable to make progress (-1 = it can).</summary>
        public float StalledSince = -1;
        /// <summary>Open arena: a scripted house player that keeps the world populated.</summary>
        public bool House;
        /// <summary>Unique per occupant. Seats are recycled, so tokens bind to this, not just the team index.</summary>
        public int Seat;
        /// <summary>Open arena newcomer protection: until this game time the team can't be hurt and can't attack.</summary>
        public float ProtectedUntil;
        public readonly List<ProdItem> StructureQueue = new List<ProdItem>();
        public readonly Dictionary<Producer, List<ProdItem>> UnitQueues = new Dictionary<Producer, List<ProdItem>>
        {
            { Producer.CommandCenter, new List<ProdItem>() },
            { Producer.Barracks, new List<ProdItem>() },
            { Producer.Factory, new List<ProdItem>() },
            { Producer.Airfield, new List<ProdItem>() },
        };
        /// <summary>Enemy structures this team has seen: id -> (key, origin, team).</summary>
        // Sorted by id so every reader (the scripted AI, rally points) sees the same order in a game and in its resumed copy.
        public readonly SortedDictionary<int, (string key, Int2 origin, int team)> KnownEnemyStructures = new SortedDictionary<int, (string, Int2, int)>();
        /// <summary>Deep deposits this team's surveyors have found (by deposit id), and where they've surveyed.</summary>
        public readonly HashSet<int> Surveyed = new HashSet<int>();
        public readonly List<Vec2> SurveySites = new List<Vec2>();
        /// <summary>Mining zones: the flag a surveyor planted on each deep deposit it found (key = deposit id = zone id).</summary>
        public readonly Dictionary<int, ZoneFlag> Zones = new Dictionary<int, ZoneFlag>();
        public float SurfaceWarnedAt = -999;
        /// <summary>Game time of this team's last command from its player (-1 = none yet); the lobby reports idle seats.</summary>
        public float LastCommandAt = -1;
        /// <summary>Items converters leave alone below this amount ('reserve' command), so raw-ore costs stay payable.</summary>
        public readonly Dictionary<string, int> Reserve = new Dictionary<string, int>();
        public int Reserved(string item) => Reserve.TryGetValue(item, out var n) ? n : 0;
        public readonly TeamStats Stats = new TeamStats();
        public bool LowPower => PowerUsed > PowerProduced;
    }

    public partial class World
    {
        public const int TickRate = 20;
        public const float Dt = 1f / TickRate;

        public Map Map { get; private set; }
        public Pathfinder Paths { get; private set; }
        /// <summary>Bumped whenever the map's tiles change wholesale (growth, salvage), so views can rebuild terrain.</summary>
        public int MapVersion;

        // ---- Open arena: players can join and leave mid-game
        public bool Open;
        public int MaxPlayers = 8;
        public int MaxMapSize = Map.MaxSize;
        public int GrowStep = 32;
        /// <summary>A joiner's base goes at least this far from any enemy structure or armed unit, if the map can grow that far.</summary>
        public float SafeJoinDistance = 56f;
        /// <summary>Open arena: seconds of protection a joiner gets to set up before they can be attacked.</summary>
        public float ProtectionSeconds = 300f;

        /// <summary>A protected newcomer's starting ore (within this many tiles of their base) is theirs alone until protection ends.</summary>
        public const float ReservedOreRadius = 16f;

        public bool OreReserved(Int2 tile, int team)
        {
            foreach (var t in Teams)
                if (t.Id != team && t.ProtectedUntil > Time && !t.Left && Vec2.Dist(tile.Center, t.StartPos) <= ReservedOreRadius) return true;
            return false;
        }

        public bool IsProtected(int team) => team >= 0 && team < Teams.Count && Teams[team].ProtectedUntil > Time;
        public static readonly string[] Flavors = { "Blueberry", "Cherry", "Lime", "Lemon", "Grape", "Blackberry", "Spearmint", "Plum" };
        public readonly List<Team> Teams = new List<Team>();
        public readonly Dictionary<int, Entity> ById = new Dictionary<int, Entity>();
        public readonly List<Entity> Entities = new List<Entity>();
        public readonly List<Projectile> Projectiles = new List<Projectile>();
        public readonly List<GameEvent> Events = new List<GameEvent>();
        /// <summary>How many of each event type have happened (the event list itself is a rolling window).</summary>
        public readonly Dictionary<string, int> EventCounts = new Dictionary<string, int>();
        public readonly AlertLog Alerts = new AlertLog();
        /// <summary>Agent-invented unit designs in this game, by key (t&lt;team&gt;:&lt;name&gt;). See Invention.cs.</summary>
        public readonly Dictionary<string, Invention> Inventions = new Dictionary<string, Invention>();
        /// <summary>Identifies this game across saves and resumes (the invention registry keys designs by it). Not used by the sim.</summary>
        public string GameId = Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>Invention tallies for the registry: a unit of an invented type died (and who killed it).</summary>
        void TallyDeath(Entity dead, Entity killer)
        {
            if (dead.Def.OwnerTeam >= 0 && Inventions.TryGetValue(dead.Def.Key, out var lost) && lost.Def == dead.Def) lost.Lost++;
            if (killer != null && killer.Def.OwnerTeam >= 0 && killer.Team != dead.Team && Inventions.TryGetValue(killer.Def.Key, out var won) && won.Def == killer.Def) won.Kills++;
        }

        /// <summary>Let key-only lookups (Defs.Get, used by the view and API) see this world's inventions. A new World does this itself.</summary>
        public void MakeCurrent() => Defs.Invented = key => Inventions.TryGetValue(key, out var i) ? i.Def : null;

        /// <summary>A def by key: a standard one, or one of this game's inventions.</summary>
        public EntityDef Def(string key) => key == null ? null : Defs.All.TryGetValue(key, out var d) ? d : Inventions.TryGetValue(key, out var i) ? i.Def : null;
        public int Tick;
        public float Time => Tick * Dt;
        public bool GameOver;
        public int Winner = -1;
        /// <summary>Exceptions caught inside the sim (each is logged once per site and the game carries on).</summary>
        public int Errors;
        public string LastError;
        public Action<string> ErrorLog = _ => { };
        readonly HashSet<string> loggedErrors = new HashSet<string>();

        void RecordError(string where, Exception ex)
        {
            Errors++;
            LastError = $"[{Time:0}s] {where}: {ex.GetType().Name}: {ex.Message}";
            if (loggedErrors.Add(where.Split(' ')[0] + ex.GetType().Name)) ErrorLog(LastError + "\n" + ex.StackTrace);
        }

        // ------------------------------------------------------------------ spatial grid
        // Entities bucketed by 4x4-tile cell, rebuilt each tick, so "what's near me" doesn't scan the whole world.

        const int CellSize = 4;
        List<Entity>[] cells;
        int cellsW, cellsH;
        readonly List<Entity> nearTarget = new List<Entity>(), nearHelp = new List<Entity>(), nearMine = new List<Entity>(),
                              nearSep = new List<Entity>(), nearVis = new List<Entity>();

        void RebuildGrid()
        {
            if (cells == null)
            {
                cellsW = (Map.W + CellSize - 1) / CellSize; cellsH = (Map.H + CellSize - 1) / CellSize;
                cells = new List<Entity>[cellsW * cellsH];
                for (int i = 0; i < cells.Length; i++) cells[i] = new List<Entity>();
            }
            foreach (var c in cells) c.Clear();
            foreach (var e in Entities)
            {
                if (e.Dead || e.IsCarried) continue;
                var p = e.Center;
                int cx = Math.Clamp((int)p.X / CellSize, 0, cellsW - 1), cy = Math.Clamp((int)p.Y / CellSize, 0, cellsH - 1);
                cells[cy * cellsW + cx].Add(e);
            }
        }

        /// <summary>Entities whose centre is within r (+2 tiles of slack for building footprints and movement this tick).</summary>
        void Near(Vec2 p, float r, List<Entity> into)
        {
            into.Clear();
            if (cells == null) RebuildGrid();
            float rr = r + 2f;
            int x0 = Math.Clamp((int)((p.X - rr) / CellSize), 0, cellsW - 1), x1 = Math.Clamp((int)((p.X + rr) / CellSize), 0, cellsW - 1);
            int y0 = Math.Clamp((int)((p.Y - rr) / CellSize), 0, cellsH - 1), y1 = Math.Clamp((int)((p.Y + rr) / CellSize), 0, cellsH - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    foreach (var e in cells[y * cellsW + x])
                        if (!e.Dead) into.Add(e);
        }

        /// <summary>Optional per-phase timing (seconds), filled when Profile is on. Used by the headless --profile run.</summary>
        public static bool Profile;
        public static readonly Dictionary<string, double> Timings = new Dictionary<string, double>();
        static readonly System.Diagnostics.Stopwatch profileClock = System.Diagnostics.Stopwatch.StartNew();

        void Guard(string where, Action a)
        {
            double t0 = Profile ? profileClock.Elapsed.TotalSeconds : 0;
            try { a(); } catch (Exception ex) { RecordError(where, ex); }
            if (Profile) Timings[where] = (Timings.TryGetValue(where, out var v) ? v : 0) + profileClock.Elapsed.TotalSeconds - t0;
        }
        int nextId = 1;
        long nextSeq = 1;
        const int MaxEvents = 4000;

        public World(int teamCount = 2, int seed = 1337, int size = 80, float oreScale = 1f)
        {
            size = Math.Clamp(size, Map.MinSize, Map.MaxSize);
            Map = Map.Generate(size, size, seed, Math.Clamp(oreScale, 0.05f, 4f));
            Paths = new Pathfinder(Map);
            MakeCurrent();
            for (int t = 0; t < teamCount; t++) CreateTeam(Map.Spawns[t]);
            UpdatePower();
            UpdateVisibility();
        }

        int seatCounter;

        /// <summary>Create a team in a new slot, or in slot `reuse` (a seat whose player left or was eliminated).</summary>
        Team CreateTeam(Vec2 spawn, int reuse = -1)
        {
            int t = reuse >= 0 ? reuse : Teams.Count;
            var team = new Team { Id = t, Name = Flavors[t % Flavors.Length], StartPos = spawn, Visible = new bool[Map.W * Map.H], Explored = new bool[Map.W * Map.H], Seat = ++seatCounter };
            if (reuse >= 0)
            {
                // A fresh occupant: drop everything that referred to the old one.
                Teams[t] = team;
                Alerts.ClearTeam(t);
                ForgetTeam(t);
                foreach (var k in Inventions.Where(kv => kv.Value.Team == t).Select(kv => kv.Key).ToList()) Inventions.Remove(k); // the old occupant's designs
            }
            else Teams.Add(team);
            // Enough raw ore for a power plant; everything after that has to be mined.
            team.Add("iron_ore", 500);
            team.Add("copper_ore", 150);
            var s = Int2.Of(spawn);
            var hq = SpawnStructure(t, "command_center", new Int2(s.X - 1, s.Y - 1), 1f);
            SpawnUnit(t, "mining_truck", hq);
            for (int i = 0; i < 2; i++) SpawnUnit(t, "rifleman", hq);
            return team;
        }

        // ------------------------------------------------------------------ open arena: join and leave

        public int ActivePlayers => Teams.Count(t => !t.Left && !t.Defeated);

        /// <summary>
        /// A new player joins mid-game. The map grows (east and north, existing coordinates unchanged) to make
        /// room and add resources, up to MaxMapSize; at the cap, a free base site is reused. Returns null with a
        /// reason if the arena is full.
        /// </summary>
        public Team AddTeam(string controller, string playerName, out string error)
        {
            error = null;
            if (!Open) { error = "this game is not open for joining"; return null; }
            if (ActivePlayers >= MaxPlayers) { error = $"arena is full ({MaxPlayers} players)"; return null; }
            // Flavours (and colours) are capped at eight, so long-running arenas recycle the seats of departed players.
            int reuse = -1;
            if (Teams.Count >= Flavors.Length)
            {
                reuse = Teams.FindIndex(t => t.Left || t.Defeated);
                if (reuse < 0) { error = $"arena is full ({Flavors.Length} seats)"; return null; }
            }
            var bases = Teams.Where(t => !t.Left && !t.Defeated).Select(t => t.StartPos).ToList();
            // Somewhere nobody can pounce on: away from every enemy structure and armed unit, not just their HQs.
            var threats = Entities.Where(e => !e.Dead && e.Team >= 0 && (e.IsStructure || e.IsArmed)).Select(e => e.Pos).ToList();
            threats.AddRange(bases);
            Vec2 spawn;
            int size = Math.Max(Map.W, Map.H);
            if (size + GrowStep <= MaxMapSize)
            {
                // Grow one step; if that strip is still within reach of someone, grow a wider one (up to the cap).
                Map best = null; Vec2 bestSpawn = default; float bestClear = -1f;
                for (int grow = GrowStep; size + grow <= MaxMapSize; grow += GrowStep)
                {
                    var m = Map.Grown(Map.W + grow, Map.H + grow, Tick * 7919 + Teams.Count, bases, threats, out var sp, out float clear);
                    if (clear > bestClear) { best = m; bestSpawn = sp; bestClear = clear; }
                    if (clear >= SafeJoinDistance) break;
                }
                spawn = bestSpawn;
                ReplaceMap(best);
            }
            else
            {
                // At the size cap: reuse the site nobody holds that's farthest from trouble.
                var free = Map.Spawns.Where(s => bases.All(b => Vec2.Dist(b, s) > 16) && Map.Occupant[Map.Idx((int)s.X, (int)s.Y)] == 0)
                                     .OrderByDescending(s => Map.Clearance(s, threats)).ToList();
                if (free.Count == 0) { error = "arena is at its maximum size and every base site is taken"; return null; }
                spawn = free[0];
            }
            var team = CreateTeam(spawn, reuse);
            team.Controller = controller;
            team.PlayerName = playerName;
            GiveCatchUp(team);
            team.ProtectedUntil = Time + ProtectionSeconds;
            UpdatePower();
            UpdateVisibility();
            Emit("joined", team.Id, pos: spawn, text: $"{playerName} joined as {team.Name} at sector {StateView.Sector(Map, spawn)}. The map is now {Map.W}x{Map.H}.");
            Emit("chat", -1, text: $"{playerName} joined as {team.Name} at sector {StateView.Sector(Map, spawn)}.");
            return team;
        }

        /// <summary>
        /// Late joiners get a head start that scales with the arena's age: refined materials, and once the arena is
        /// a few minutes old, a finished power plant and mining refinery (with its free truck). Enough to reach
        /// barracks, factory and defenses inside the protection window.
        /// </summary>
        void GiveCatchUp(Team team)
        {
            float minutes = MathF.Min(Time / 60f, 30f);
            if (minutes < 0.5f) return;
            team.Add("steel", 60 * minutes);
            team.Add("copper", 30 * minutes);
            team.Add("circuits", 8 * minutes);
            team.Add("iron_ore", 20 * minutes);
            if (minutes < 3f) return;
            foreach (var key in new[] { "power_plant", "mining_refinery" })
            {
                var spot = FindPlacement(team.Id, key);
                if (spot.HasValue) { SpawnStructure(team.Id, key, spot.Value, 1f); team.Stats.Count(key); }
            }
        }

        /// <summary>
        /// A drone's tank in seconds of flight: its def's RangeMaps map widths at its speed, on this map (so "across and
        /// halfway back" stays true as the arena grows). 0 for everything else (their tank is the def's Fuel).
        /// </summary>
        public float DroneFuel(EntityDef d) => d.RangeMaps > 0 ? d.RangeMaps * Map.W / MathF.Max(0.1f, d.Speed) : 0f;

        void ReplaceMap(Map m)
        {
            int oldW = Map.W, oldH = Map.H;
            foreach (var t in Teams)
            {
                var vis = new bool[m.W * m.H]; var exp = new bool[m.W * m.H];
                for (int y = 0; y < oldH; y++)
                    for (int x = 0; x < oldW; x++) { vis[m.Idx(x, y)] = t.Visible[y * oldW + x]; exp[m.Idx(x, y)] = t.Explored[y * oldW + x]; }
                t.Visible = vis; t.Explored = exp;
            }
            Map = m;
            Paths = new Pathfinder(m);
            cells = null; // spatial grid is sized to the map
            // Drones' tanks grow with the map, keeping how full they are.
            foreach (var e in Entities)
                if (!e.Dead && e.Def.RangeMaps > 0) { float f = e.FuelFraction; e.FuelCap = DroneFuel(e.Def); e.Fuel = f * e.FuelMax; }
            MapVersion++;
        }

        static readonly Dictionary<string, (byte ore, float mult)> SalvageOf = new Dictionary<string, (byte, float)>
        {
            { "iron_ore", (Map.Iron, 1f) }, { "steel", (Map.Iron, 1f) },
            { "copper_ore", (Map.Copper, 1f) }, { "copper", (Map.Copper, 1f) }, { "circuits", (Map.Copper, 2f) },
            { "crystal", (Map.Crystal, 1f) }, { "lenses", (Map.Crystal, 2f) }, { "composite", (Map.Crystal, 2f) },
            { "uranium", (Map.Uranium, 1f) }, { "plasma", (Map.Uranium, 2f) },
        };

        /// <summary>
        /// A player leaves for good. Their buildings, units and stockpile become salvage: ore piles on the old
        /// base's footprint that anyone's mining trucks can collect, first come, first served.
        /// </summary>
        /// <summary>Other teams forget what they knew about this team's buildings (it's gone, or its seat is reused).</summary>
        void ForgetTeam(int teamId)
        {
            foreach (var o in Teams)
                foreach (var id in o.KnownEnemyStructures.Where(kv => kv.Value.team == teamId).Select(kv => kv.Key).ToList())
                    o.KnownEnemyStructures.Remove(id);
        }

        /// <summary>
        /// Spread ore values (per ore type) as salvage anyone can mine: on these tiles first, then on open ground around
        /// them, as many tiles as it takes (a tile holds at most Map.MaxOrePerTile). Returns the amount actually placed.
        /// </summary>
        float SpillSalvage(float[] value, List<Int2> tiles)
        {
            if (value.Sum() <= 0 || tiles.Count == 0) return 0;
            // Free ground, nearest the footprint first: the footprint itself, then open tiles with no ore on them.
            var footprint = new HashSet<Int2>(tiles);
            float cx = (float)tiles.Average(t => t.X) + 0.5f, cy = (float)tiles.Average(t => t.Y) + 0.5f;
            var queue = new List<Int2>(tiles);
            // Each ore type covers its share of the footprint, and more ground if that won't hold it all.
            float total = value.Sum();
            var share = new int[4];
            for (int k = 0; k < 4; k++)
                if (value[k] > 0) share[k] = Math.Max(Math.Max(1, (int)MathF.Round(tiles.Count * value[k] / total)), (int)MathF.Ceiling(value[k] / Map.MaxOrePerTile));
            int tilesNeeded = share.Sum();
            for (int r = 1; queue.Count < tilesNeeded && r <= 40; r++)
            {
                var ring = new List<Int2>();
                for (int y = (int)cy - r; y <= (int)cy + r; y++)
                    for (int x = (int)cx - r; x <= (int)cx + r; x++)
                    {
                        if (Math.Max(Math.Abs(x - (int)cx), Math.Abs(y - (int)cy)) != r || !Map.InBounds(x, y)) continue;
                        var p = new Int2(x, y);
                        int i = Map.Idx(x, y);
                        if (footprint.Contains(p) || !Map.TerrainPassable(x, y) || Map.Occupant[i] != 0 || Map.Ore[i] > 0) continue;
                        ring.Add(p);
                    }
                queue.AddRange(ring.OrderBy(p => Vec2.DistSq(p.Center, new Vec2(cx, cy))));
            }
            // Each ore type takes the tiles it needs, biggest pile first.
            float placed = 0;
            int ti = 0;
            foreach (var k in Enumerable.Range(0, 4).Where(k => value[k] > 0).OrderByDescending(k => value[k]))
            {
                int n = share[k];
                int per = (int)(value[k] / n);
                for (int j = 0; j < n && ti < queue.Count; j++, ti++)
                {
                    int i = Map.Idx(queue[ti].X, queue[ti].Y);
                    Map.Tiles[i] = Terrain.Dirt;
                    Map.OreType[i] = (byte)k;
                    Map.Ore[i] = Math.Min(Map.MaxOrePerTile, per);
                    placed += Map.Ore[i];
                }
            }
            MapVersion++;
            return placed;
        }

        /// <summary>
        /// A team's last command center has fallen: everything in its stockpile spills out as salvage ore on the
        /// footprint, first come, first served. (The team plays on with whatever else it has.)
        /// </summary>
        void ReleaseStockpile(Entity cc)
        {
            var t = Teams[cc.Team];
            var value = new float[4];
            foreach (var kv in t.Stock) if (SalvageOf.TryGetValue(kv.Key, out var sv) && kv.Value > 0) value[sv.ore] += kv.Value * sv.mult;
            var tiles = new List<Int2>();
            for (int y = 0; y < cc.Def.SizeY; y++) for (int x = 0; x < cc.Def.SizeX; x++) tiles.Add(new Int2(cc.Origin.X + x, cc.Origin.Y + y));
            float total = SpillSalvage(value, tiles);
            t.Stock.Clear();
            if (total <= 0) return;
            var where = StateView.Sector(Map, cc.Center);
            string msg = $"{t.Name}'s last command center fell at sector {where}: its stockpile spilled out as about {(int)total} units of salvage ore. First come, first served.";
            Emit("chat", -1, text: msg);
            Alerts.Raise(this, t.Id, "stockpile_lost", Priority.Critical, cc.Center, hit: false).Lost.Add($"your whole stockpile ({(int)total} units) spilled out as salvage at {where}; anyone can mine it, so get your trucks there first");
            foreach (var other in Teams.Where(o => o.Id != t.Id && !o.Left && !o.Defeated))
                Alerts.Raise(this, other.Id, "salvage_available", Priority.High, cc.Center, hit: false).Lost.Add($"{(int)total} units of salvage ore at {where}");
        }

        public string Leave(int teamId, string reason = null)
        {
            var t = Teams[teamId];
            if (t.Left) return "already left";
            var value = new float[4];
            void AddCost(Dictionary<string, int> cost, float f) { foreach (var kv in cost) if (SalvageOf.TryGetValue(kv.Key, out var s)) value[s.ore] += kv.Value * s.mult * f; }
            foreach (var kv in t.Stock) if (SalvageOf.TryGetValue(kv.Key, out var s) && kv.Value > 0) value[s.ore] += kv.Value * s.mult;
            var tiles = new List<Int2>();
            foreach (var e in Entities.Where(e => !e.Dead && e.Team == teamId).ToList())
            {
                if (e.IsStructure)
                {
                    AddCost(e.Def.Key == "command_center" ? new Dictionary<string, int> { { "iron_ore", 1500 }, { "copper_ore", 500 } } : e.Def.Cost, 0.6f * e.BuildProgress);
                    for (int y = 0; y < e.Def.SizeY; y++) for (int x = 0; x < e.Def.SizeX; x++) tiles.Add(new Int2(e.Origin.X + x, e.Origin.Y + y));
                }
                else if (!e.IsMine) AddCost(e.Def.Cost, 0.5f);
                Emit("salvaged", e.Team, e.Id, 0, e.Center, key: e.Def.Key); // dismantled, not destroyed: no wreck
                Remove(e);
            }
            if (tiles.Count == 0) tiles.Add(Int2.Of(t.StartPos));
            float total = SpillSalvage(value, tiles);
            t.Left = true;
            t.Defeated = true;
            t.Stock.Clear();
            ForgetTeam(teamId);
            MapVersion++;
            var where = StateView.Sector(Map, t.StartPos);
            string msg = $"{t.PlayerName ?? t.Name} ({t.Name}) {reason ?? "left the arena"}. Their base at sector {where} is now about {(int)total} units of salvage ore. First come, first served.";
            Emit("left", teamId, pos: t.StartPos, text: msg);
            Emit("chat", -1, text: msg);
            foreach (var other in Teams.Where(o => !o.Left && !o.Defeated))
                Alerts.Raise(this, other.Id, "salvage_available", Priority.High, t.StartPos, hit: false).Lost.Add($"{(int)total} units of salvage ore");
            return msg;
        }

        // ------------------------------------------------------------------ entities

        public Entity Get(int id) => ById.TryGetValue(id, out var e) && !e.Dead ? e : null;

        Entity NewEntity(int team, EntityDef def)
        {
            var e = new Entity { Id = nextId++, Team = team, Def = def, Hp = def.MaxHp };
            ById[e.Id] = e;
            Entities.Add(e);
            return e;
        }

        public Entity SpawnStructure(int team, string key, Int2 origin, float progress)
        {
            var def = Def(key);
            var e = NewEntity(team, def);
            e.Origin = origin;
            e.BuildProgress = progress;
            e.Pos = e.PrevPos = e.Center;
            e.Hp = def.MaxHp * (progress >= 1 ? 1f : 0.1f);
            for (int y = 0; y < def.SizeY; y++)
                for (int x = 0; x < def.SizeX; x++)
                    Map.Occupant[Map.Idx(origin.X + x, origin.Y + y)] = e.Id;
            EvictUnits(e);
            if (progress >= 1) OnStructureComplete(e);
            return e;
        }

        void EvictUnits(Entity s)
        {
            foreach (var u in Entities)
            {
                if (u.Dead || u.IsStructure) continue;
                var t = Int2.Of(u.Pos);
                if (Map.Occupant[Map.Idx(t.X, t.Y)] != s.Id) continue;
                var free = Paths.NearestPassable(t);
                u.Pos = u.PrevPos = free.Center;
                u.Path = null;
            }
        }

        public Entity SpawnUnit(int team, string key, Entity at)
        {
            var def = Def(key);
            var e = NewEntity(team, def);
            var jitter = new Vec2((float)(rng.NextDouble() - 0.5) * 0.6f, (float)(rng.NextDouble() - 0.5) * 0.6f);
            if (def.IsAir) e.Pos = e.PrevPos = at.Center + jitter;
            else
            {
                // Exit just below the producer's footprint.
                var exit = new Int2(at.Origin.X + at.Def.SizeX / 2, at.Origin.Y - 1);
                var tile = Paths.NearestPassable(exit);
                e.Pos = e.PrevPos = tile.Center + jitter;
            }
            e.Facing = e.TurretFacing = -MathF.PI / 2;
            e.GuardPos = e.Pos;
            e.FuelCap = DroneFuel(def);
            e.Fuel = e.FuelMax;
            if (e.IsHarvester) SetOrder(e, Order.Harvest, e.Pos);
            else if (at.Rally.HasValue) SetOrder(e, Order.Move, at.Rally.Value);
            else if (!def.IsAir)
            {
                // Roll a few tiles clear of the door so new units don't park in front of it (or a refinery's dock).
                var clear = Paths.NearestPassable(Int2.Of(e.Pos + new Vec2((float)(rng.NextDouble() - 0.5) * 4f, -3f))).Center;
                SetOrder(e, Order.Move, clear);
                e.GuardPos = clear;
            }
            return e;
        }

        SimRng rng = new SimRng(42); // serializable, so a resumed game draws the same numbers

        void OnStructureComplete(Entity e)
        {
            UpdatePower();
            if (e.Def.Key == "mining_refinery") SpawnUnit(e.Team, "mining_truck", e);
        }

        /// <summary>Turn a deployable unit (Outpost Truck) into its structure where it stands.</summary>
        public string Deploy(Entity u, DeepDeposit want = null)
        {
            var key = u.Def.DeploysInto;
            if (key == null) return $"{u.Def.Key} can't deploy";
            var def = Defs.Get(key);
            var t = Int2.Of(u.Pos);
            DeepDeposit deposit = null;
            if (key == "deep_mine")
            {
                // A drill rig only works on a deep deposit your surveyors have found, and one mine per deposit.
                deposit = want != null && Teams[u.Team].Surveyed.Contains(want.Id) && Vec2.Dist(want.Pos, u.Pos) <= 3f ? want
                        : Map.Deep.Where(d => Teams[u.Team].Surveyed.Contains(d.Id) && Vec2.Dist(d.Pos, u.Pos) <= 3f)
                                  .OrderBy(d => Vec2.Dist(d.Pos, u.Pos)).FirstOrDefault();
                if (deposit == null) return "no deep deposit your team has surveyed within 3 tiles: survey first, then drive the rig onto a deposit";
                if (deposit.MineId != 0 && Get(deposit.MineId) != null) return $"deposit #{deposit.Id} already has a deep mine on it";
                if (deposit.Amount <= 0) return $"deposit #{deposit.Id} is exhausted";
                t = Int2.Of(deposit.Pos);
            }
            var origin = new Int2(t.X - (def.SizeX - 1) / 2, t.Y - (def.SizeY - 1) / 2);
            var why = CanPlace(u.Team, key, origin.X, origin.Y, requireNear: false);
            if (why != null) return $"can't deploy here: {why}";
            Remove(u);
            var s = SpawnStructure(u.Team, key, origin, 1f);
            if (deposit != null) { s.DepositId = deposit.Id; deposit.MineId = s.Id; }
            Teams[u.Team].Stats.Count(key);
            Emit("built", u.Team, s.Id, pos: s.Center, key: key);
            return null;
        }

        public void Remove(Entity e)
        {
            if (e.Dead) return;
            e.Dead = true;
            if (e.IsCarried) Get(e.CarrierId)?.Passengers.Remove(e.Id);
            foreach (var pid in e.Passengers.ToList())
            {
                var p = Get(pid);
                if (p == null) continue;
                // Nobody gets out of a destroyed transport.
                Emit("destroyed", p.Team, p.Id, 0, e.Center, key: p.Def.Key);
                Teams[p.Team].Stats.UnitsLost++;
                TallyDeath(p, null);
                p.CarrierId = 0;
                Remove(p);
            }
            e.Passengers.Clear();
            if (e.IsStructure)
            {
                for (int y = 0; y < e.Def.SizeY; y++)
                    for (int x = 0; x < e.Def.SizeX; x++)
                    {
                        int i = Map.Idx(e.Origin.X + x, e.Origin.Y + y);
                        if (Map.Occupant[i] == e.Id) Map.Occupant[i] = 0;
                    }
                var team = Teams[e.Team];
                team.StructureQueue.RemoveAll(p => p.StructureId == e.Id);
                UpdatePower();
            }
        }

        // ------------------------------------------------------------------ events

        public GameEvent Emit(string type, int team = -1, int a = 0, int b = 0, Vec2 pos = default, Vec2 pos2 = default, string key = null, string text = null)
        {
            var ev = new GameEvent { Seq = nextSeq++, Tick = Tick, Type = type, Team = team, A = a, B = b, Pos = pos, Pos2 = pos2, Key = key, Text = text };
            Events.Add(ev);
            EventCounts[type] = (EventCounts.TryGetValue(type, out var n) ? n : 0) + 1;
            if (Events.Count > MaxEvents) Events.RemoveRange(0, Events.Count - MaxEvents);
            return ev;
        }

        // ------------------------------------------------------------------ queries

        public IEnumerable<Entity> Owned(int team) => Entities.Where(e => !e.Dead && e.Team == team);

        public bool HasComplete(int team, string key) => Entities.Any(e => !e.Dead && e.Team == team && e.IsStructure && e.IsComplete && e.Def.Key == key);

        public Entity FirstProducer(int team, Producer p) =>
            Entities.FirstOrDefault(e => !e.Dead && e.Team == team && e.IsStructure && e.IsComplete && e.Def.Produces == p);

        public string MissingPrereq(int team, EntityDef def)
        {
            if (def.BuiltBy == Producer.None) return $"{def.Key} cannot be built";
            if (def.OwnerTeam >= 0)
            {
                if (def.OwnerTeam != team) return $"is {Teams[def.OwnerTeam].Name}'s invention; only they can build it";
                if (Inventions.TryGetValue(def.Key, out var inv) && !inv.Done) return $"is still being researched ({inv.Pct}%)";
            }
            var producerKey = Defs.ProducerKey(def.BuiltBy);
            if (producerKey != null && !HasComplete(team, producerKey)) return $"requires a completed {producerKey}";
            foreach (var r in def.Requires) if (!HasComplete(team, r)) return $"requires a completed {r}";
            return null;
        }

        public void SetOrders(int team, string text)
        {
            var t = Teams[team];
            text = (text ?? "").Trim();
            if (text.Length > 2000) text = text.Substring(0, 2000);
            if (text == t.StandingOrders) return;
            t.StandingOrders = text;
            t.OrdersVersion++;
            Emit("orders", team, text: text.Length == 0 ? "(orders cleared)" : text);
        }

        public bool IsVisibleTo(int team, Entity e)
        {
            if (e.Team == team) return true;
            if (e.IsCarried) return false; // inside a transport
            // Muzzle flash: whoever just shot at this team is visible to it for a few seconds.
            if (Teams[team].Revealed.TryGetValue(e.Id, out var until) && until >= Time) return true;
            if (e.Def.Stealth) return Teams[team].Detected.Contains(e.Id);
            var vis = Teams[team].Visible;
            if (!e.IsStructure) { var t = Int2.Of(e.Pos); return Map.InBounds(t.X, t.Y) && vis[Map.Idx(t.X, t.Y)]; }
            for (int y = 0; y < e.Def.SizeY; y++)
                for (int x = 0; x < e.Def.SizeX; x++)
                    if (vis[Map.Idx(e.Origin.X + x, e.Origin.Y + y)]) return true;
            return false;
        }

        /// <summary>Returns null if placement is valid, else the reason.</summary>
        public string CanPlace(int team, string key, int ox, int oy, int padding = 0, bool requireNear = true)
        {
            var def = Defs.Get(key);
            if (def == null || !def.IsStructure) return $"unknown structure '{key}'";
            for (int y = -padding; y < def.SizeY + padding; y++)
                for (int x = -padding; x < def.SizeX + padding; x++)
                {
                    int tx = ox + x, ty = oy + y;
                    bool inFootprint = x >= 0 && y >= 0 && x < def.SizeX && y < def.SizeY;
                    if (!Map.InBounds(tx, ty)) { if (inFootprint) return "out of bounds"; continue; }
                    int i = Map.Idx(tx, ty);
                    if (Map.Occupant[i] != 0) return $"tile ({tx},{ty}) is occupied by a structure";
                    if (!inFootprint) continue;
                    if (!Map.TerrainPassable(tx, ty)) return $"tile ({tx},{ty}) is {Map.Tiles[i].ToString().ToLowerInvariant()}";
                    if (Map.Ore[i] > 0) return $"tile ({tx},{ty}) has ore on it";
                }
            // Truck bays: mining trucks back into drop-offs along a 2-tile lane, so nothing may be built on one.
            if (BlocksLane(team, ox, oy, def) is Entity d)
                return $"it would block the truck lane of your {d.Def.Key} #{d.Id} (trucks back into its bay; keep the 2 tiles outside it clear)";
            if (def.DropOff && !Bay(new Int2(ox, oy), def).laneOk)
                return $"a {def.Key} needs a clear 2-tile truck lane on one side (south preferred) for trucks to back into";
            if (!requireNear) return null;
            // Must be near an existing friendly structure: territory grows outward from your bases.
            const int reach = 6;
            bool near = Entities.Any(s => !s.Dead && s.Team == team && s.IsStructure &&
                ox <= s.Origin.X + s.Def.SizeX - 1 + reach && ox + def.SizeX - 1 >= s.Origin.X - reach &&
                oy <= s.Origin.Y + s.Def.SizeY - 1 + reach && oy + def.SizeY - 1 >= s.Origin.Y - reach);
            if (!near) return $"must be within {reach} tiles of one of your structures";
            return null;
        }

        /// <summary>Finds a valid spot near the team's base, leaving a 1-tile walkway around it.</summary>
        public Int2? FindPlacement(int team, string key, Vec2? near = null)
        {
            var def = Defs.Get(key);
            var anchor = near ?? Teams[team].StartPos;
            var centre = new Vec2(Map.W / 2f, Map.H / 2f);
            Int2? best = null; float bestScore = float.MaxValue;
            for (int r = 2; r <= 14; r++)
            {
                for (int y = (int)anchor.Y - r; y <= (int)anchor.Y + r; y++)
                    for (int x = (int)anchor.X - r; x <= (int)anchor.X + r; x++)
                    {
                        if (CanPlace(team, key, x, y, padding: 1) != null) continue;
                        var c = new Vec2(x + def.SizeX / 2f, y + def.SizeY / 2f);
                        // Turrets lean toward the map centre; everything else stays compact.
                        float score = Vec2.Dist(c, anchor) + (key == "gun_turret" ? Vec2.Dist(c, centre) * 0.6f : 0);
                        if (score < bestScore) { bestScore = score; best = new Int2(x, y); }
                    }
                if (best.HasValue && r > 4) break;
            }
            return best;
        }

        // ------------------------------------------------------------------ orders

        public void SetOrder(Entity e, Order o, Vec2 pos, int target = 0)
        {
            e.Order = o;
            e.OrderPos = pos;
            e.TargetId = target;
            e.Path = null;
            e.RepathTimer = 0;
            e.Dock = DockStep.None; e.DockAt = 0;
            if (o == Order.Idle) e.GuardPos = e.Pos;
            e.Responding = false; // any new order (from a commander or the response itself) replaces the old one
            e.SpeedCap = 0;
            e.Waypoints.Clear(); e.WaypointLoop = false;
            e.Retreating = false;
            if (o == Order.Harvest && e.IsHarvester)
            {
                var t = Int2.Of(pos);
                bool onOre = Map.OreAt(t.X, t.Y) > 0;
                e.HarvestTile = onOre ? t : (Int2?)null;
                // Ordering a truck onto a specific ore makes it stick to that type.
                if (onOre) e.HarvestType = Map.OreType[Map.Idx(t.X, t.Y)];
            }
        }

        // ------------------------------------------------------------------ simulation

        public void Step()
        {
            if (GameOver) return;
            Tick++;
            foreach (var e in Entities) e.PrevPos = e.Pos;
            RebuildGrid();
            foreach (var p in Projectiles) p.PrevPos = p.Pos;

            Guard("economy", UpdateEconomy);
            Guard("production", UpdateProduction);
            double tu = Profile ? profileClock.Elapsed.TotalSeconds : 0;
            for (int i = 0; i < Entities.Count; i++)
            {
                var e = Entities[i];
                if (e.Dead) continue;
                if (e.Cooldown > 0) e.Cooldown -= Dt;
                if (e.IsCarried) { var c = Get(e.CarrierId); if (c != null) e.Pos = c.Pos; continue; } // riding along
                if (e.Def.SelfRepairRate > 0 && e.Hp < e.Def.MaxHp * e.Def.SelfRepairTo)
                    e.Hp = MathF.Min(e.Def.MaxHp * e.Def.SelfRepairTo, e.Hp + e.Def.SelfRepairRate * Dt);
                // One misbehaving entity must never stop the rest of the world from updating.
                try
                {
                    if (e.IsMine) { MineTick(e); continue; }
                    if (!e.IsStructure) { CheckRetreat(e); UpdateUnit(e); if (e.Def.UsesFuel && !e.Dead) UpdateFuel(e); if (!e.Dead) CheckJam(e); }
                    else
                    {
                        if (e.IsComplete) Burn(e);
                        if (!e.Dead && e.IsArmed && e.IsComplete) UpdateTurret(e);
                    }
                }
                catch (Exception ex)
                {
                    RecordError($"{e.Def.Key} #{e.Id} ({e.OrderName})", ex);
                    if (!e.IsStructure) SetOrder(e, Order.Idle, e.Pos);
                }
            }
            if (Profile) Timings["units"] = (Timings.TryGetValue("units", out var uv) ? uv : 0) + profileClock.Elapsed.TotalSeconds - tu;
            Guard("projectiles", UpdateProjectiles);
            Guard("separation", Separate);
            Guard("cleanup", Cleanup);
            if (Tick % 4 == 0) Guard("visibility", UpdateVisibility);
            Guard("victory", CheckVictory);
        }

        void UpdatePower()
        {
            foreach (var t in Teams) { t.PowerProduced = 0; t.PowerUsed = 0; }
            foreach (var e in Entities)
            {
                if (e.Dead || !e.IsStructure || !e.IsComplete) continue;
                var t = Teams[e.Team];
                if (e.Def.Key == "fusion_reactor" && !e.Working) continue; // out of plasma
                if (e.Def.Power > 0) t.PowerProduced += e.Def.Power; else t.PowerUsed -= e.Def.Power;
            }
        }

        readonly Dictionary<int, Dictionary<string, float>> rateSnapshot = new Dictionary<int, Dictionary<string, float>>();

        /// <summary>Converter buildings turn inputs into outputs; fusion reactors burn plasma.</summary>
        void UpdateEconomy()
        {
            bool powerChanged = false;
            foreach (var e in Entities)
            {
                if (e.Dead || !e.IsStructure || !e.IsComplete) continue;
                var team = Teams[e.Team];
                if (e.Def.Key == "fusion_reactor")
                {
                    float burn = 0.1f * Dt;
                    bool fueled = (team.Stock.TryGetValue("plasma", out var pl) ? pl : 0) >= burn;
                    if (fueled) team.Add("plasma", -burn);
                    if (fueled != e.Working) { e.Working = fueled; powerChanged = true; }
                    continue;
                }
                if (e.Def.Key == "deep_mine") { DeepMineTick(e, team); continue; }
                if (e.Def.Recipes.Length == 0) continue;
                float speed = team.LowPower ? 0.5f : 1f;
                bool any = false;
                foreach (var r in e.Def.Recipes)
                {
                    float cycles = r.Rate * Dt * speed;
                    // Run as much of this tick's cycles as the stockpile allows.
                    float frac = 1f;
                    foreach (var kv in r.Inputs)
                    {
                        float have = (team.Stock.TryGetValue(kv.Key, out var v) ? v : 0) - team.Reserved(kv.Key);
                        frac = MathF.Min(frac, have / (kv.Value * cycles));
                    // (Recipes with no inputs, like the command center's trickle, always run.)
                    }
                    if (frac <= 0.001f) continue;
                    frac = MathF.Min(1f, frac);
                    foreach (var kv in r.Inputs) team.Add(kv.Key, -kv.Value * cycles * frac);
                    foreach (var kv in r.Outputs) team.Add(kv.Key, kv.Value * cycles * frac);
                    any = true;
                }
                e.Working = any;
            }
            if (powerChanged) UpdatePower();

            // Per-second net rates, from a stock snapshot each second.
            if (Tick % TickRate == 0)
                foreach (var t in Teams)
                {
                    if (rateSnapshot.TryGetValue(t.Id, out var prev))
                        foreach (var item in Defs.Items)
                            t.Rates[item] = (t.Stock.TryGetValue(item, out var now) ? now : 0) - (prev.TryGetValue(item, out var p) ? p : 0);
                    rateSnapshot[t.Id] = new Dictionary<string, float>(t.Stock);
                }
        }

        void UpdateProduction()
        {
            foreach (var team in Teams)
            {
                if (team.Defeated) continue;
                float rate = team.LowPower ? 0.5f : 1f;
                Tech.Tick(this, team, rate);

                // Structures: one at a time per team, needs a command center.
                if (team.StructureQueue.Count > 0 && HasComplete(team.Id, "command_center"))
                {
                    var item = team.StructureQueue[0];
                    var s = Get(item.StructureId);
                    if (s == null) team.StructureQueue.RemoveAt(0);
                    else
                    {
                        item.Progress += Dt * rate;
                        float before = s.BuildProgress;
                        s.BuildProgress = MathF.Min(1f, item.Progress / s.Def.BuildTime);
                        s.Hp = MathF.Min(s.Def.MaxHp, s.Hp + (s.BuildProgress - before) * s.Def.MaxHp * 0.9f);
                        if (s.IsComplete)
                        {
                            team.StructureQueue.RemoveAt(0);
                            team.Stats.StructuresBuilt++;
                            team.Stats.Count(s.Def.Key);
                            Emit("built", team.Id, s.Id, pos: s.Center, key: s.Def.Key);
                            OnStructureComplete(s);
                        }
                    }
                }

                foreach (var kv in team.UnitQueues)
                {
                    var q = kv.Value;
                    if (q.Count == 0) continue;
                    var item = q[0];
                    // A unit trained at a chosen building comes out there while it stands; otherwise the first one.
                    var chosen = item.StructureId != 0 ? Get(item.StructureId) : null;
                    var producer = chosen != null && chosen.Team == team.Id && chosen.IsComplete && chosen.Def.Produces == kv.Key ? chosen : FirstProducer(team.Id, kv.Key);
                    if (producer == null) continue;
                    var def = Def(item.Key);
                    item.Progress += Dt * rate;
                    if (item.Progress >= def.BuildTime)
                    {
                        q.RemoveAt(0);
                        var u = SpawnUnit(team.Id, item.Key, producer);
                        team.Stats.UnitsBuilt++;
                        team.Stats.Count(item.Key);
                        if (u.Def.OwnerTeam >= 0 && Inventions.TryGetValue(item.Key, out var inv)) inv.Built++;
                        Emit("trained", team.Id, u.Id, pos: u.Pos, key: u.Def.Key);
                    }
                }
            }
        }

        void UpdateUnit(Entity e)
        {
            switch (e.Order)
            {
                case Order.Idle:
                    if (e.IsArmed) IdleCombat(e);
                    else if (e.Def.RepairRate > 0) IdleRepair(e);
                    break;
                case Order.Repair:
                    UpdateRepair(e);
                    break;
                case Order.Board:
                    UpdateBoard(e);
                    break;
                case Order.Capture:
                    UpdateCapture(e);
                    break;
                case Order.LayMines:
                    UpdateLayMines(e);
                    break;
                case Order.Survey:
                    UpdateSurvey(e);
                    break;
                case Order.Drill:
                    UpdateDrill(e);
                    break;
                case Order.Refuel:
                    UpdateRefuelTrip(e);
                    if (e.IsArmed && !e.Dead) OpportunisticFire(e);
                    break;
                case Order.Move:
                    if (FollowPath(e, e.OrderPos, 0.3f) && !NextWaypoint(e)) { bool back = e.Retreating; SetOrder(e, Order.Idle, e.Pos); e.Retreating = back; }
                    if (e.IsArmed && (e.Def.Armor == Armor.Vehicle || e.IsAir)) OpportunisticFire(e);
                    break;
                case Order.AttackMove:
                    {
                        // Unarmed units (medics, repair trucks...) swept up in an attack-move just travel.
                        if (!e.IsArmed) { if (FollowPath(e, e.OrderPos, 0.5f) && !NextWaypoint(e)) FinishOrder(e); break; }
                        var t = AcquireTarget(e, e.Def.Sight);
                        if (t != null) { Engage(e, t); break; }
                        if (FollowPath(e, e.OrderPos, 0.5f) && !NextWaypoint(e)) FinishOrder(e);
                        break;
                    }
                case Order.Attack:
                    {
                        var t = Get(e.TargetId);
                        if (t == null || t.Team == e.Team || !e.Def.Weapon.CanHit(t.Def)) { FinishOrder(e); break; }
                        if (!IsVisibleTo(e.Team, t))
                        {
                            // Lost sight of it: push to where it was last seen and fight whatever is there.
                            bool resp = e.Responding; var home = e.HomePos;
                            SetOrder(e, Order.AttackMove, e.OrderPos);
                            e.Responding = resp; e.HomePos = home;
                            break;
                        }
                        e.OrderPos = t.Center; // last known position
                        Engage(e, t);
                        break;
                    }
                case Order.Harvest:
                case Order.ReturnOre:
                    UpdateHarvester(e);
                    break;
            }
        }

        /// <summary>Arrived: head for the next queued waypoint (same kind of order), keeping the group pace. False if none.</summary>
        bool NextWaypoint(Entity e)
        {
            if (e.Waypoints.Count == 0) return false;
            var next = e.Waypoints[0];
            var rest = e.Waypoints.Skip(1).ToList();
            bool loop = e.WaypointLoop; float cap = e.SpeedCap;
            if (loop) rest.Add(e.OrderPos);
            SetOrder(e, e.Order, next);
            e.Waypoints.AddRange(rest); e.WaypointLoop = loop; e.SpeedCap = cap;
            return true;
        }

        /// <summary>Units told to pull back below an HP threshold head for the nearest base building on their own.</summary>
        void CheckRetreat(Entity e)
        {
            if (e.RetreatBelow <= 0 || e.Retreating || e.Hp >= e.Def.MaxHp * e.RetreatBelow || (Tick + e.Id) % 5 != 0) return;
            Entity home = null; float bd = float.MaxValue;
            foreach (var s in Owned(e.Team))
            {
                if (!s.IsStructure || !s.IsComplete) continue;
                float d = Vec2.DistSq(s.Center, e.Pos) - (s.Def.FuelDepot ? 100f : 0f); // prefer real bases over a lone turret
                if (d < bd) { bd = d; home = s; }
            }
            if (home == null || home.DistFrom(e.Pos) < 4f) return;
            SetOrder(e, Order.Move, e.IsAir ? home.Center : DockPoint(home));
            e.Retreating = true;
            Emit("retreating", e.Team, e.Id, home.Id, e.Pos, key: e.Def.Key,
                 text: $"{e.Def.Key} #{e.Id} fell below {StateView.Pct(e.RetreatBelow)}% HP ({(int)e.Hp}/{e.Def.MaxHp}) and is pulling back to {home.Def.Key} #{home.Id}");
        }

        void IdleCombat(Entity e)
        {
            var t = AcquireTarget(e, e.Def.Sight);
            if (t == null)
            {
                // Drift back to the guard position after chasing.
                if (Vec2.Dist(e.Pos, e.GuardPos) > 1.5f) FollowPath(e, e.GuardPos, 0.5f); else e.Moving = false;
                return;
            }
            float d = t.DistFrom(e.Pos);
            if (d <= e.Def.Weapon.Range) { e.Moving = false; FireAt(e, t); }
            else if (Vec2.Dist(e.Pos, e.GuardPos) < 6f) Chase(e, t); // path around obstacles, not straight into them
        }

        /// <summary>Done with an order. Units that left their post to answer an attack head back to it.</summary>
        void FinishOrder(Entity e)
        {
            bool back = e.Responding;
            var home = e.HomePos;
            SetOrder(e, Order.Idle, e.Pos);
            if (back) e.GuardPos = home; // IdleCombat walks them home
        }

        /// <summary>
        /// Someone hit one of our units or buildings: idle armed units nearby that can hit the attacker go after it,
        /// the way a garrison reacts to an alarm. Commanded units (moving, attacking, harvesting...) stay on task.
        /// </summary>
        void CallForHelp(Entity victim, Entity attacker)
        {
            if (Time - victim.LastCallForHelp < 1.5f) return;
            victim.LastCallForHelp = Time;
            Near(victim.Center, 10f, nearHelp);
            foreach (var u in nearHelp)
            {
                if (u.Dead || u.Team != victim.Team || u.IsStructure || !u.IsArmed || u.IsCarried || u.Order != Order.Idle) continue;
                if (!u.Def.Weapon.CanHit(attacker.Def)) continue;
                if (Vec2.Dist(u.Pos, victim.Center) > 10f) continue;
                var home = u.GuardPos;
                if (IsVisibleTo(u.Team, attacker)) SetOrder(u, Order.Attack, attacker.Center, attacker.Id);
                else SetOrder(u, Order.AttackMove, attacker.Center);
                u.OrderPos = attacker.Center;
                u.Responding = true;
                u.HomePos = home;
            }
        }

        void OpportunisticFire(Entity e)
        {
            var t = AcquireTarget(e, e.Def.Weapon.Range);
            if (t != null) FireAt(e, t);
        }

        void Engage(Entity e, Entity t)
        {
            float d = t.DistFrom(e.Pos);
            if (d <= e.Def.Weapon.Range * 0.95f)
            {
                e.Moving = false;
                e.Path = null;
                FireAt(e, t);
            }
            else Chase(e, t);
        }

        /// <summary>Close on a (possibly moving) entity, repathing once a second.</summary>
        void Chase(Entity e, Entity t)
        {
            if (e.IsAir) { StepToward(e, t.Center); return; }
            e.RepathTimer -= Dt;
            if (e.Path == null || e.RepathTimer <= 0)
            {
                e.Path = Paths.Find(e.Pos, t.Center);
                e.PathIdx = 0;
                e.RepathTimer = 1f;
            }
            AdvancePath(e);
        }

        // ------------------------------------------------------------------ transports

        public int FreeSeats(Entity t) => t.Def.Capacity - t.Passengers.Count - Entities.Count(o => !o.Dead && o.Order == Order.Board && o.TargetId == t.Id && !o.IsCarried);

        void UpdateBoard(Entity e)
        {
            var t = Get(e.TargetId);
            if (t == null || t.Team != e.Team || t.Def.Capacity == 0 || t.Passengers.Count >= t.Def.Capacity) { SetOrder(e, Order.Idle, e.Pos); return; }
            if (Vec2.Dist(e.Pos, t.Pos) > t.Def.Radius + 0.8f) { Chase(e, t); return; }
            e.CarrierId = t.Id;
            e.Path = null;
            e.Moving = false;
            e.Order = Order.Idle;
            t.Passengers.Add(e.Id);
            Emit("boarded", e.Team, e.Id, t.Id, t.Pos, key: e.Def.Key);
        }

        /// <summary>Drop everyone off around the transport (aircraft land their troops on the nearest open ground).</summary>
        public int Unload(Entity t)
        {
            int n = 0;
            var baseTile = Paths.NearestPassable(Int2.Of(t.Pos));
            foreach (var pid in t.Passengers.ToList())
            {
                var p = Get(pid);
                if (p == null) continue;
                float a = n * 2.4f;
                var spot = baseTile.Center + new Vec2(MathF.Cos(a), MathF.Sin(a)) * (0.5f + 0.15f * n);
                var st = Int2.Of(spot);
                if (!Map.Passable(st.X, st.Y)) spot = baseTile.Center;
                p.CarrierId = 0;
                p.Pos = p.PrevPos = p.GuardPos = spot;
                SetOrder(p, Order.Idle, spot);
                n++;
            }
            t.Passengers.Clear();
            if (n > 0) Emit("unloaded", t.Team, t.Id, 0, t.Pos, key: t.Def.Key);
            return n;
        }

        // ------------------------------------------------------------------ engineers

        public const float CaptureThreshold = 0.5f;

        void UpdateCapture(Entity e)
        {
            var t = Get(e.TargetId);
            if (t == null || !t.IsStructure || t.Team == e.Team || !t.IsComplete || t.Hp > t.Def.MaxHp * CaptureThreshold) { SetOrder(e, Order.Idle, e.Pos); return; }
            if (t.DistFrom(e.Pos) > 0.6f) { Chase(e, t); return; }
            int old = t.Team;
            t.Team = e.Team;
            t.Rally = null;
            Teams[e.Team].KnownEnemyStructures.Remove(t.Id);
            Teams[old].Stats.StructuresLost++;
            Teams[e.Team].Stats.Count(t.Def.Key);
            Emit("captured", e.Team, t.Id, e.Id, t.Center, key: t.Def.Key);
            Alerts.Raise(this, old, "structure_lost", Priority.Critical, t.Center, attacker: e).Lost.Add($"{t.Def.Key} #{t.Id} (captured by an engineer)");
            Remove(e); // the engineer moves in for good
            UpdatePower();
        }

        // ------------------------------------------------------------------ mines

        public Entity SpawnMine(int team, Vec2 pos)
        {
            var m = NewEntity(team, Defs.Get("mine"));
            m.Pos = m.PrevPos = m.GuardPos = pos;
            return m;
        }

        void UpdateLayMines(Entity e)
        {
            if (e.MineQueue.Count == 0) { SetOrder(e, Order.Idle, e.Pos); return; }
            var spot = e.MineQueue[0];
            if (!FollowPath(e, spot, 0.3f)) return;
            var team = Teams[e.Team];
            if (team.Amount("steel") < EntityDef.MineCost) { e.MineQueue.Clear(); SetOrder(e, Order.Idle, e.Pos); return; }
            var t = Int2.Of(e.Pos);
            bool clear = Map.Passable(t.X, t.Y) && !Entities.Any(o => !o.Dead && o.IsMine && Vec2.Dist(o.Pos, e.Pos) < 0.7f);
            if (clear)
            {
                team.Add("steel", -EntityDef.MineCost);
                SpawnMine(e.Team, e.Pos);
                Emit("mine_laid", e.Team, e.Id, 0, e.Pos);
            }
            e.MineQueue.RemoveAt(0);
            e.Path = null;
        }

        /// <summary>An enemy ground unit on top of a mine sets it off: splash damage, then it's gone.</summary>
        void MineTick(Entity m)
        {
            Entity trigger = null;
            Near(m.Pos, 1.5f, nearMine);
            foreach (var o in nearMine)
                if (!o.Dead && o.Team != m.Team && !IsProtected(o.Team) && !o.IsStructure && !o.IsAir && !o.IsCarried && !o.IsMine && Vec2.Dist(o.Pos, m.Pos) <= 0.35f + o.Def.Radius)
                { trigger = o; break; }
            if (trigger == null) return;
            Emit("hit", m.Team, m.Id, trigger.Id, m.Pos, key: "mine");
            foreach (var o in nearMine.ToList())
                if (!o.Dead && o.Team != m.Team && !o.IsStructure && !o.IsAir && !o.IsCarried && !o.IsMine && Vec2.Dist(o.Pos, m.Pos) <= 1.3f + o.Def.Radius)
                    Damage(o, o == trigger ? 260 : 120, m);
            Remove(m);
        }

        // ------------------------------------------------------------------ repair

        /// <summary>Medics heal infantry; repair trucks fix everything else. Neither works on itself.</summary>
        public static bool CanTend(Entity healer, Entity t) =>
            t != healer && !t.Dead && !t.IsMine && !t.IsCarried && t.Team == healer.Team && t.IsComplete && (t.Hp < t.Def.MaxHp - 0.5f || NeedsTanker(healer, t)) &&
            (healer.Def.Medic ? t.Def.Armor == Armor.Infantry : t.Def.Armor != Armor.Infantry);

        /// <summary>Repair trucks double as field tankers for ground vehicles (aircraft refuel on a pad).</summary>
        public static bool NeedsTanker(Entity healer, Entity t) => !healer.Def.Medic && t.Def.UsesFuel && !t.IsAir && t.Fuel < t.FuelMax * 0.6f;

        void IdleRepair(Entity e)
        {
            e.Moving = false;
            if ((Tick + e.Id) % 10 != 0) return; // scanning every tick is wasteful
            Entity best = null; float bd = 6f;
            foreach (var o in Entities)
            {
                if (!CanTend(e, o)) continue;
                // Stranded vehicles are worth a longer drive than a scratch.
                float d = o.DistFrom(e.Pos) - (o.Stranded ? 14f : 0f);
                if (d < bd) { bd = d; best = o; }
            }
            if (best != null) SetOrder(e, Order.Repair, best.Center, best.Id);
        }

        void UpdateRepair(Entity e)
        {
            var t = Get(e.TargetId);
            // Keep going until it's whole and (for a vehicle being refuelled) the tank is full.
            bool topping = t != null && !e.Def.Medic && t.Team == e.Team && t.Def.UsesFuel && !t.IsAir && t.Fuel < t.FuelMax * 0.99f;
            if (t == null || !(CanTend(e, t) || topping)) { SetOrder(e, Order.Idle, e.Pos); return; }
            if (t.DistFrom(e.Pos) > e.Def.RepairRange) { Chase(e, t); return; }
            e.Moving = false;
            e.Path = null;
            e.TurretFacing = RotateToward(e.TurretFacing, (t.Center - e.Pos).Angle, 6f * Dt);
            var team = Teams[e.Team];
            if (topping) Refill(t, t.FuelMax / (EntityDef.RefuelSeconds * 1.2f) * Dt);
            float hp = MathF.Min(e.Def.RepairRate * Dt, t.Def.MaxHp - t.Hp);
            if (!e.Def.Medic)
            {
                // Repairs burn steel; with none in the stockpile the truck just waits.
                float steel = team.Stock.TryGetValue("steel", out var s) ? s : 0;
                hp = MathF.Min(hp, steel / EntityDef.RepairSteelPerHp);
                if (hp <= 0) return;
                team.Add("steel", -hp * EntityDef.RepairSteelPerHp);
            }
            t.Hp += hp;
            e.WorkTimer += Dt;
            if (e.WorkTimer >= 0.4f) { e.WorkTimer = 0; Emit(e.Def.Medic ? "heal" : "repair", e.Team, e.Id, t.Id, e.Pos, t.Center); }
        }

        Entity AcquireTarget(Entity e, float radius)
        {
            Entity best = null; float bestScore = float.MaxValue;
            Near(e.Pos, radius, nearTarget);
            foreach (var o in nearTarget)
            {
                if (o.Dead || o.Team == e.Team || o.IsCarried) continue;
                if (IsProtected(o.Team) || IsProtected(e.Team)) continue; // newcomer protection: no fighting either way
                if (e.Def.Weapon != null && !e.Def.Weapon.CanHit(o.Def)) continue;
                float d = o.DistFrom(e.Pos);
                if (d > radius) continue;
                if (!IsVisibleTo(e.Team, o)) continue;
                // Prefer armed threats, then units, then structures.
                float score = d + (o.IsArmed ? 0 : 4) + (o.IsStructure ? 6 : 0);
                if (score < bestScore) { bestScore = score; best = o; }
            }
            return best;
        }

        void UpdateTurret(Entity e)
        {
            var t = AcquireTarget(e, e.Def.Weapon.Range);
            if (t != null) FireAt(e, t);
        }

        void FireAt(Entity e, Entity t)
        {
            e.LastFiredAt = Time;
            var dir = t.Center - e.Pos;
            float want = dir.Angle;
            e.TurretFacing = RotateToward(e.TurretFacing, want, 6f * Dt);
            if (!e.IsStructure && !e.Moving && e.Def.Armor == Armor.Infantry) e.Facing = want;
            if (e.Cooldown > 0) return;
            if (MathF.Abs(AngleDiff(e.TurretFacing, want)) > 0.35f) return;
            var w = e.Def.Weapon;
            e.Cooldown = w.Cooldown;
            if (w.ProjectileSpeed <= 0)
            {
                Emit("shot", e.Team, e.Id, t.Id, e.Pos, t.Center, w.Name);
                Damage(t, w.Damage * w.Multiplier(t.Def.Armor), e);
            }
            else
            {
                var muzzle = e.Pos + new Vec2(MathF.Cos(e.TurretFacing), MathF.Sin(e.TurretFacing)) * (e.IsStructure ? 0.5f : e.Def.Radius + 0.2f);
                var p = new Projectile { Id = nextId++, Team = e.Team, SourceId = e.Id, TargetId = t.Id, Pos = muzzle, PrevPos = muzzle, TargetPos = t.Center, Weapon = w };
                Projectiles.Add(p);
                Emit("fire", e.Team, e.Id, t.Id, muzzle, t.Center, w.Name);
            }
        }

        void UpdateProjectiles()
        {
            for (int i = Projectiles.Count - 1; i >= 0; i--)
            {
                var p = Projectiles[i];
                var t = Get(p.TargetId);
                if (t != null) p.TargetPos = t.Center;
                var to = p.TargetPos - p.Pos;
                float step = p.Weapon.ProjectileSpeed * Dt;
                if (to.Length <= step)
                {
                    p.Pos = p.TargetPos;
                    Projectiles.RemoveAt(i);
                    Emit("hit", p.Team, p.SourceId, p.TargetId, p.Pos, key: p.Weapon.Name);
                    var src = Get(p.SourceId);
                    if (t != null) Damage(t, p.Weapon.Damage * p.Weapon.Multiplier(t.Def.Armor), src, p.Team);
                    if (p.Weapon.SplashRadius > 0)
                        foreach (var o in Entities.ToList())
                            if (!o.Dead && o != t && !o.IsCarried && o.Team != p.Team && p.Weapon.CanHit(o.Def) && o.DistFrom(p.Pos) < p.Weapon.SplashRadius)
                                Damage(o, p.Weapon.Damage * 0.4f * p.Weapon.Multiplier(o.Def.Armor), src, p.Team);
                }
                else p.Pos += to.Normalized * step;
            }
        }

        /// <summary>A finished building below this share of its health is on fire: it burns down unless repaired above it.</summary>
        public const float BurnBelow = 0.3f;

        /// <summary>
        /// Fire: a finished building below BurnBelow loses health on its own, 0.3% of its max a second at the threshold
        /// rising to 1.2% near the end (about a minute from catching fire to collapse). Repairing it above the threshold
        /// puts it out. Its owner is alerted once when it catches; the kill goes to whoever set it alight.
        /// </summary>
        void Burn(Entity e)
        {
            float f = e.Hp / e.Def.MaxHp;
            if (f >= BurnBelow || e.Hp <= 0f)
            {
                if (e.Burning) { e.Burning = false; Emit("fire_out", e.Team, e.Id, 0, e.Center, key: e.Def.Key, text: $"the fire at your {e.Def.Key} #{e.Id} is out"); }
                return;
            }
            if (IsProtected(e.Team)) return;
            if (!e.Burning)
            {
                e.Burning = true;
                Emit("burning", e.Team, e.Id, 0, e.Center, key: e.Def.Key,
                     text: $"your {e.Def.Key} #{e.Id} is on fire ({(int)(f * 100)}% health): repair it above {(int)(BurnBelow * 100)}% (repair truck) or it burns down within about a minute");
                Alerts.Raise(this, e.Team, "building_burning", Priority.High, e.Center, e, hit: false);
            }
            float rate = e.Def.MaxHp * (0.003f + 0.009f * (1f - f / BurnBelow));
            var setBy = e.LastAttackerId != 0 ? Get(e.LastAttackerId) : null;
            Damage(e, rate * Dt, null, setBy != null && setBy.Team != e.Team ? setBy.Team : -1, burn: true);
        }

        void Damage(Entity t, float amount, Entity src, int srcTeam = -1, bool burn = false)
        {
            if (t.Dead) return;
            if (IsProtected(t.Team)) return; // newcomer protection
            int team = src?.Team ?? srcTeam;
            t.Hp -= amount;
            if (src != null) t.LastAttackerId = src.Id;
            if (!burn)
            {
                if (Time - t.LastHitTime > 10f && (t.IsStructure || t.IsHarvester))
                    Emit("under_attack", t.Team, t.Id, src?.Id ?? 0, t.Center, key: t.Def.Key);
                t.LastHitTime = Time;
                RaiseDamageAlert(t, src);
            }
            if (src != null && !src.Dead && src.Team != t.Team && !src.IsMine)
            {
                Teams[t.Team].Revealed[src.Id] = Time + 3f; // muzzle flash gives the shooter away
                CallForHelp(t, src);                         // the victim (if idle) and idle units nearby respond
            }
            if (t.Hp <= 0)
            {
                Emit("destroyed", t.Team, t.Id, src?.Id ?? 0, t.Center, key: t.Def.Key);
                if (team >= 0 && team < Teams.Count) Teams[team].Stats.Kills++;
                if (t.IsStructure) Teams[t.Team].Stats.StructuresLost++; else Teams[t.Team].Stats.UnitsLost++;
                TallyDeath(t, src);
                var lost = Alerts.Raise(this, t.Team, t.IsStructure ? "structure_lost" : "units_lost", t.IsStructure ? Priority.Critical : Priority.Medium, t.Center, attacker: src);
                lost.Lost.Add($"{t.Def.Key} #{t.Id}");
                bool lastHq = t.Def.Key == "command_center" && !Entities.Any(o => !o.Dead && o != t && o.Team == t.Team && o.Def.Key == "command_center");
                Remove(t);
                if (lastHq) ReleaseStockpile(t);
            }
        }

        /// <summary>
        /// Classify a hit the way a human commander would hear it: buildings and trucks under fire, or units
        /// caught off guard, interrupt; fights the army picked on purpose are just a combat report.
        /// </summary>
        void RaiseDamageAlert(Entity t, Entity src)
        {
            if (t.IsMine) return; // a mine being swept isn't news
            string kind; Priority p;
            if (t.IsStructure) { kind = "base_under_attack"; p = Priority.Critical; }
            else if (t.IsHarvester) { kind = "harvester_under_attack"; p = Priority.High; }
            else if (t.Order == Order.Attack || t.Order == Order.AttackMove) { kind = "combat"; p = Priority.Medium; }
            else { kind = "units_ambushed"; p = Priority.High; }
            Alerts.Raise(this, t.Team, kind, p, t.Center, t, src);
        }

        // ------------------------------------------------------------------ harvesting

        void UpdateHarvester(Entity e)
        {
            int cap = e.Def.HarvestCapacity;
            if (e.Order == Order.Harvest)
            {
                if (e.Cargo >= cap) { e.Order = Order.ReturnOre; e.Path = null; return; }
                // A partly loaded truck can only take more of the same ore.
                int want = e.Cargo > 0 ? e.CargoType : e.HarvestType;
                bool tileGood = e.HarvestTile.HasValue && Map.OreAt(e.HarvestTile.Value.X, e.HarvestTile.Value.Y) > 0 &&
                                (want < 0 || Map.OreType[Map.Idx(e.HarvestTile.Value.X, e.HarvestTile.Value.Y)] == want) &&
                                !OreReserved(e.HarvestTile.Value, e.Team);
                if (!tileGood)
                {
                    var from = e.HarvestTile.HasValue ? e.HarvestTile.Value.Center : e.Pos;
                    // Spread trucks out: avoid tiles another truck is already working.
                    e.HarvestTile = Map.NearestOre(from, 40, t => !OreReserved(t, e.Team) && !Entities.Any(o => o != e && !o.Dead && o.IsHarvester && o.HarvestTile.HasValue && o.HarvestTile.Value.Equals(t)), want)
                                    ?? Map.NearestOre(from, 80, t => !OreReserved(t, e.Team), want);
                    e.Path = null;
                    if (!e.HarvestTile.HasValue)
                    {
                        WarnSurfaceExhausted(e, want);
                        if (e.Cargo > 0) { e.Order = Order.ReturnOre; return; }
                        SetOrder(e, Order.Idle, e.Pos);
                        return;
                    }
                }
                var tile = e.HarvestTile.Value;
                if (Vec2.Dist(e.Pos, tile.Center) > 0.45f) { FollowPath(e, tile.Center, 0.4f); return; }
                e.Moving = false;
                e.WorkTimer += Dt;
                if (e.WorkTimer >= 0.1f)
                {
                    e.WorkTimer = 0;
                    int i = Map.Idx(tile.X, tile.Y);
                    int take = Math.Min(Math.Min(4, Map.Ore[i]), cap - e.Cargo);
                    Map.Ore[i] -= take;
                    if (take > 0) { e.Cargo += take; e.CargoType = Map.OreType[i]; }
                    e.TurretFacing += 0.3f; // spin the cutter for the view
                }
                return;
            }

            // ReturnOre: back into a drop-off's bay, one truck at a time, unload, drive out.
            var refinery = e.DockAt != 0 ? Get(e.DockAt) : null;
            // Until it reaches a lane, a truck keeps reconsidering (another bay may have freed up or been built).
            bool rethink = e.Dock <= DockStep.Approach && (Tick + e.Id) % TickRate == 0;
            if (refinery == null || refinery.Dead || !refinery.IsComplete || refinery.Team != e.Team || rethink)
            {
                var was = refinery;
                if (e.Dock >= DockStep.Reverse && e.Cargo <= 0) { FinishDelivery(e); return; } // the bay's building is gone; the load is already in
                refinery = ChooseDropOff(e);
                if (refinery != was) { e.DockAt = refinery?.Id ?? 0; e.Dock = DockStep.Approach; e.Path = null; }
            }
            if (refinery == null) { e.Moving = false; return; }
            var (bay, head, bayFacing, laneOk) = Bay(refinery);
            var outward = (head - bay).Normalized;
            var across = new Vec2(-outward.Y, outward.X);
            if (!laneOk)
            {
                // Nowhere to back in (the lane is built over or blocked by terrain): unload beside the drop-off.
                if (Vec2.Dist(e.Pos, bay) > 0.6f) { FollowPath(e, bay, 0.5f); return; }
                e.Moving = false;
                TipOre(e);
                if (e.Cargo <= 0) FinishDelivery(e);
                return;
            }
            switch (e.Dock)
            {
                case DockStep.None:
                case DockStep.Approach:
                case DockStep.Queue:
                    if (BayTaken(refinery, e))
                    {
                        // Wait beside the lane, out of the way, until the bay is free.
                        if (e.Dock == DockStep.Queue || Vec2.Dist(e.Pos, head) < 4f)
                        {
                            if (e.Dock != DockStep.Queue) e.Path = null;
                            e.Dock = DockStep.Queue;
                            var spot = head + across * (e.Id % 2 == 0 ? 1.7f : -1.7f) + outward * 1.3f;
                            if (!Map.Passable(Int2.Of(spot).X, Int2.Of(spot).Y)) spot = head + across * (e.Id % 2 == 0 ? -1.7f : 1.7f) + outward * 1.3f;
                            if (!Map.Passable(Int2.Of(spot).X, Int2.Of(spot).Y)) spot = head + outward * 2.5f;
                            FollowPath(e, spot, 0.4f);
                            return;
                        }
                        e.Dock = DockStep.Approach;
                        FollowPath(e, head, 0.35f);
                        return;
                    }
                    if (e.Dock == DockStep.Queue) e.Path = null;
                    e.Dock = DockStep.Approach;
                    if (Vec2.Dist(e.Pos, head) > 0.35f) { FollowPath(e, head, 0.3f); return; }
                    e.Dock = DockStep.Align;
                    e.Moving = false;
                    e.Path = null;
                    return;
                case DockStep.Align:
                    // Turn on the spot to face away from the building (the bin end toward it), settling onto the lane.
                    e.Moving = false;
                    e.Facing = RotateToward(e.Facing, bayFacing, AlignTurnRate * Dt);
                    e.Pos = e.Pos + (head - e.Pos) * MathF.Min(1f, 4f * Dt);
                    if (MathF.Abs(AngleDiff(e.Facing, bayFacing)) < 0.02f) { e.Facing = bayFacing; e.Dock = DockStep.Reverse; }
                    return;
                case DockStep.Reverse:
                    {
                        // Back in slowly; the truck keeps facing out of the bay.
                        var to = bay - e.Pos;
                        float dist = to.Length, step = MathF.Min(dist, e.Def.Speed * ReverseSpeed * Dt);
                        if (dist > 1e-4f) e.Pos = e.Pos + to / dist * step;
                        e.Moving = true;
                        if (dist - step < 0.01f) { e.Pos = bay; e.Moving = false; e.Dock = DockStep.Unload; e.WorkTimer = -UnloadSettle; }
                        return;
                    }
                case DockStep.Unload:
                    e.Moving = false;
                    TipOre(e);
                    if (e.Cargo <= 0) { e.Dock = DockStep.PullOut; e.WorkTimer = -BedLower; }
                    return;
                case DockStep.PullOut:
                    {
                        // Lower the bed, then drive forward out of the bay and on past the head of the lane, between the
                        // waiting trucks, so the next one has a clear lane when the bay is released.
                        e.WorkTimer += Dt;
                        if (e.WorkTimer < 0) { e.Moving = false; return; }
                        var exit = head + outward * PullOutPast;
                        if (!Map.Passable(Int2.Of(exit).X, Int2.Of(exit).Y)) exit = head;
                        var to = exit - e.Pos;
                        float dist = to.Length, step = MathF.Min(dist, e.Def.Speed * PullOutSpeed * Dt);
                        if (dist > 1e-4f) e.Pos = e.Pos + to / dist * step;
                        e.Moving = true;
                        if (dist - step < 0.01f) FinishDelivery(e);
                        return;
                    }
            }
        }

        // The bay: trucks line up BayLength tiles out, turn to face away, reverse in, and unload. These timings are the
        // delivery's cost in the economy (about 6 s a trip on top of the 1.5 s unload), and the one-truck-at-a-time
        // bay makes a big fleet want more than one drop-off.
        public const float BayLength = 2.0f, AlignTurnRate = 3.2f, ReverseSpeed = 0.7f, PullOutSpeed = 1.0f, UnloadSettle = 0.3f, BedLower = 0.25f, PullOutPast = 1.6f;

        /// <summary>Tip 10 ore every 0.1 s into the team's stockpile (WorkTimer starts below zero for a settle first).</summary>
        void TipOre(Entity e)
        {
            e.WorkTimer += Dt;
            if (e.WorkTimer < 0.1f) return;
            e.WorkTimer = 0;
            int give = Math.Min(10, e.Cargo);
            e.Cargo -= give;
            if (e.CargoType >= 0) Teams[e.Team].Add(Defs.Ores[e.CargoType], give);
            Teams[e.Team].Stats.OreMined += give;
        }

        void FinishDelivery(Entity e)
        {
            e.CargoType = -1;
            e.Order = Order.Harvest;
            e.Path = null;
            e.Dock = DockStep.None;
            e.DockAt = 0;
            e.Moving = false;
        }

        /// <summary>
        /// The drop-off whose bay is quickest to reach: distance to the head of its lane, plus a few tiles for each truck
        /// already headed there, and a big penalty if its lane is built over (it would have to unload beside it).
        /// </summary>
        Entity ChooseDropOff(Entity e)
        {
            Entity best = null; float bestCost = float.MaxValue;
            foreach (var s in Entities)
            {
                if (s.Dead || s.Team != e.Team || !s.IsStructure || !s.IsComplete || !s.Def.DropOff) continue;
                var (_, head, _, laneOk) = Bay(s);
                int waiting = 0;
                foreach (var o in Entities) if (o != e && !o.Dead && o.IsHarvester && o.DockAt == s.Id) waiting++;
                float cost = Vec2.Dist(head, e.Pos) + 4f * waiting + (laneOk ? 0 : 30f);
                if (cost < bestCost) { best = s; bestCost = cost; }
            }
            return best;
        }

        /// <summary>Another truck is in this bay (lining up, backing in, unloading or pulling out).</summary>
        bool BayTaken(Entity dropOff, Entity e) =>
            Entities.Any(o => o != e && !o.Dead && o.IsHarvester && o.DockAt == dropOff.Id && o.Dock >= DockStep.Align);

        // A bay's sides in order of preference: south (where the doors are), then east, west, north.
        static readonly Vec2[] BaySides = { new Vec2(0, -1), new Vec2(1, 0), new Vec2(-1, 0), new Vec2(0, 1) };

        /// <summary>
        /// A drop-off's truck bay: the point trucks back onto (just outside the footprint), the head of its lane
        /// (BayLength further out), and the facing a docked truck has (out of the bay). South unless terrain blocks
        /// that lane, then the first clear side. laneOk is false when no side has a clear lane (trucks unload beside it).
        /// </summary>
        public (Vec2 bay, Vec2 head, float facing, bool laneOk) Bay(Entity s) => Bay(s.Origin, s.Def);

        public (Vec2 bay, Vec2 head, float facing, bool laneOk) Bay(Int2 origin, EntityDef def)
        {
            (Vec2, Vec2, float, bool) Side(Vec2 n)
            {
                var c = new Vec2(origin.X + def.SizeX / 2f, origin.Y + def.SizeY / 2f);
                var bay = c + new Vec2(n.X * (def.SizeX / 2f + 0.5f), n.Y * (def.SizeY / 2f + 0.5f));
                return (bay, bay + n * BayLength, n.Angle, true);
            }
            foreach (var n in BaySides)
            {
                var (bay, head, f, _) = Side(n);
                if (LaneTiles(bay, head).All(t => Map.InBounds(t.X, t.Y) && Map.Passable(t.X, t.Y))) return (bay, head, f, true);
            }
            var (b0, h0, f0, _) = Side(BaySides[0]);
            return (b0, h0, f0, false);
        }

        /// <summary>The tiles a truck's body sweeps backing from the head of a lane into its bay.</summary>
        static IEnumerable<Int2> LaneTiles(Vec2 bay, Vec2 head)
        {
            var along = head - bay;
            var side = new Vec2(-along.Y, along.X).Normalized * 0.4f;
            var seen = new HashSet<Int2>();
            for (float f = 0; f <= 1.001f; f += 0.25f)
                foreach (var off in new[] { -1f, 0f, 1f })
                {
                    var t = Int2.Of(bay + along * f + side * off);
                    if (seen.Add(t)) yield return t;
                }
        }

        /// <summary>Would a structure at this footprint sit on one of the team's drop-off truck lanes? Returns that drop-off.</summary>
        Entity BlocksLane(int team, int ox, int oy, EntityDef def)
        {
            foreach (var s in Entities)
            {
                if (s.Dead || s.Team != team || !s.IsStructure || !s.Def.DropOff) continue;
                var (bay, head, _, laneOk) = Bay(s);
                if (!laneOk) continue;
                foreach (var t in LaneTiles(bay, head))
                    if (t.X >= ox && t.X < ox + def.SizeX && t.Y >= oy && t.Y < oy + def.SizeY) return s;
            }
            return null;
        }

        /// <summary>A docking truck holds its line: separation moves others out of its way, never it.</summary>
        static bool HoldsLine(Entity e) => e.Dock >= DockStep.Align;

        public Vec2 DockPoint(Entity refinery) => new Vec2(refinery.Origin.X + refinery.Def.SizeX / 2f, refinery.Origin.Y - 0.5f);

        // ------------------------------------------------------------------ deep mining

        /// <summary>
        /// A surveyor drives to its spot, stands for SurveySeconds, finds the deep deposits around it and plants a flag on
        /// each (a mining zone for its team). On 'prospect' it then moves on to the next unsurveyed spot by itself.
        /// </summary>
        void UpdateSurvey(Entity e)
        {
            if (!Map.InBounds((int)e.OrderPos.X, (int)e.OrderPos.Y)) { SurveyFailed(e, $"the site is outside the {Map.W}x{Map.H} map"); return; }
            if (Vec2.Dist(e.Pos, e.OrderPos) > 0.6f)
            {
                e.WorkTimer = 0;
                if (e.Stranded) { SurveyFailed(e, $"out of fuel, stranded at {(int)e.Pos.X},{(int)e.Pos.Y} (send a repair truck to refuel it)"); return; }
                if (!FollowPath(e, e.OrderPos, 0.5f)) return;
                // Stopped short. A few tiles off (the site itself is rock, water or a building) it surveys from there;
                // further off it says why it can't get there instead of surveying the wrong ground.
                if (Vec2.Dist(e.Pos, e.OrderPos) <= 3f) e.OrderPos = e.Pos;
                else SurveyFailed(e, UnreachableWhy(e, e.OrderPos));
                return;
            }
            e.Moving = false;
            e.TurretFacing += 0.15f; // the sensor mast turns while it listens
            e.WorkTimer += Dt;
            if (e.WorkTimer < EntityDef.SurveySeconds) return;
            var team = Teams[e.Team];
            var found = Map.Deep.Where(d => Vec2.Dist(d.Pos, e.Pos) <= EntityDef.SurveyRadius).ToList();
            var fresh = found.Where(d => team.Surveyed.Add(d.Id)).ToList();
            foreach (var d in found)
                if (!team.Zones.ContainsKey(d.Id)) team.Zones[d.Id] = new ZoneFlag { ZoneId = d.Id, FlaggedBy = e.Id, FlaggedAt = Time };
            team.SurveySites.Add(e.Pos);
            if (team.SurveySites.Count > 200) team.SurveySites.RemoveAt(0);
            e.WorkTimer = 0;
            e.SurveyFailure = null;

            // Prospecting: pick the next spot before reporting, so the report says where it's headed.
            Vec2? next = e.Prospecting ? FindProspectSite(e, e.ProspectCenter, e.ProspectRadius) : null;
            string list = string.Join(", ", found.Select(d => $"zone #{d.Id} {Defs.Ores[d.Type]} at {(int)d.Pos.X},{(int)d.Pos.Y} ({(int)d.Amount} left, {ZoneStatus(d, e.Team, out _)})"));
            string then = !e.Prospecting ? ""
                : next.HasValue ? $" Prospecting on: heading to {(int)next.Value.X},{(int)next.Value.Y}."
                : $" Prospecting done: nothing unsurveyed left within {e.ProspectRadius:0} tiles of {(int)e.ProspectCenter.X},{(int)e.ProspectCenter.Y} that it can reach; it's idle (give it a new prospect area).";
            Emit("surveyed", e.Team, e.Id, 0, e.Pos, key: e.Def.Key,
                 text: (found.Count == 0 ? $"surveyor #{e.Id} found no deep deposits within {EntityDef.SurveyRadius:0} tiles of {(int)e.Pos.X},{(int)e.Pos.Y}."
                     : $"surveyor #{e.Id} flagged {found.Count} mining zone(s){(fresh.Count < found.Count ? $" ({fresh.Count} new)" : "")}: {list}. Send a drill_rig with {{\"type\":\"drill\",\"units\":[RIG],\"zone\":{found[0].Id}}}.") + then);
            if (next.HasValue) SetOrder(e, Order.Survey, next.Value); // Prospecting stays set
            else { e.Prospecting = false; SetOrder(e, Order.Idle, e.Pos); }
        }

        string UnreachableWhy(Entity e, Vec2 site)
        {
            var t = Int2.Of(site);
            var open = Paths.NearestPassable(t, 3);
            if (!Map.Passable(open.X, open.Y))
                return Map.TerrainPassable(t.X, t.Y) ? "the site is built over and there's no open ground within 3 tiles of it"
                                                     : $"the site is {Map.Tiles[Map.Idx(t.X, t.Y)].ToString().ToLowerInvariant()} with no open ground within 3 tiles of it";
            if (!ReachableFrom(e.Pos)[Map.Idx(open.X, open.Y)])
                return $"no route over open ground from {(int)e.Pos.X},{(int)e.Pos.Y}: the site is cut off by rock, water or buildings";
            return $"blocked on the way: stuck at {(int)e.Pos.X},{(int)e.Pos.Y}, {Vec2.Dist(e.Pos, site):0} tiles short";
        }

        /// <summary>The surveyor can't do its survey: say why. Prospecting, it skips that site and moves on if it can.</summary>
        void SurveyFailed(Entity e, string why)
        {
            var site = e.OrderPos;
            e.SurveyFailure = $"unable to reach the site {(int)site.X},{(int)site.Y}: {why}";
            e.SurveyFailedAt = Time;
            e.WorkTimer = 0;
            Vec2? next = null;
            if (e.Prospecting && !e.Stranded)
            {
                e.SkipSites.Add(site);
                next = FindProspectSite(e, e.ProspectCenter, e.ProspectRadius);
            }
            Emit("survey_failed", e.Team, e.Id, 0, site, key: e.Def.Key,
                 text: $"surveyor #{e.Id} was {e.SurveyFailure}." +
                       (next.HasValue ? $" Prospecting on: heading to {(int)next.Value.X},{(int)next.Value.Y}." : e.Prospecting ? " It has stopped prospecting." : " It's waiting for orders."));
            if (next.HasValue) { SetOrder(e, Order.Survey, next.Value); return; }
            e.Prospecting = false;
            SetOrder(e, Order.Idle, e.Pos);
        }

        /// <summary>Surveys closer together than this would mostly cover the same ground.</summary>
        public const float ProspectSpacing = 18f;

        /// <summary>
        /// Where a prospecting surveyor goes next: the nearest spot it can drive to, within radius of centre, at least
        /// ProspectSpacing from every survey its team has done and from where its team's other surveyors are headed,
        /// and clear of known enemy bases. Null when there's nothing left.
        /// </summary>
        public Vec2? FindProspectSite(Entity e, Vec2 centre, float radius)
        {
            var team = Teams[e.Team];
            var reach = ReachableFrom(e.Pos);
            var taken = Owned(e.Team).Where(o => o != e && o.Def.Key == e.Def.Key &&
                                                 (o.Order == Order.Survey || (o.Order == Order.Refuel && o.ResumeOrder == Order.Survey)))
                                     .Select(o => o.Order == Order.Survey ? o.OrderPos : o.ResumePos).ToList();
            var enemy = team.KnownEnemyStructures.Values.Select(k => new Vec2(k.origin.X + 1, k.origin.Y + 1)).ToList();
            const float step = 9f, enemyClearance = 16f;
            Vec2? best = null; float bestScore = float.MaxValue;
            int n = (int)(radius / step);
            for (int gy = -n; gy <= n; gy++)
                for (int gx = -n; gx <= n; gx++)
                {
                    var p = centre + new Vec2(gx * step, gy * step);
                    if (Vec2.Dist(p, centre) > radius) continue;
                    int tx = (int)p.X, ty = (int)p.Y;
                    if (tx < 2 || ty < 2 || tx >= Map.W - 2 || ty >= Map.H - 2) continue;
                    var tile = Paths.NearestPassable(new Int2(tx, ty), 3);
                    if (!Map.Passable(tile.X, tile.Y) || !reach[Map.Idx(tile.X, tile.Y)]) continue;
                    var q = tile.Center;
                    bool near = false;
                    foreach (var s in team.SurveySites) if (Vec2.Dist(s, q) < ProspectSpacing) { near = true; break; }
                    if (!near) foreach (var s in e.SkipSites) if (Vec2.Dist(s, q) < 3f) { near = true; break; }
                    if (near || taken.Any(o => Vec2.Dist(o, q) < ProspectSpacing) || enemy.Any(x => Vec2.Dist(x, q) < enemyClearance)) continue;
                    float score = Vec2.Dist(e.Pos, q) + 0.25f * Vec2.Dist(centre, q);
                    if (score < bestScore) { bestScore = score; best = q; }
                }
            return best;
        }

        /// <summary>Tiles a ground unit at `from` can drive to (4-way flood fill over open ground).</summary>
        bool[] ReachableFrom(Vec2 from)
        {
            var seen = new bool[Map.W * Map.H];
            var s = Paths.NearestPassable(Int2.Of(from), 3);
            if (!Map.InBounds(s.X, s.Y)) return seen;
            var q = new Queue<int>();
            int si = Map.Idx(s.X, s.Y);
            seen[si] = true; q.Enqueue(si);
            while (q.Count > 0)
            {
                int i = q.Dequeue(), x = i % Map.W, y = i / Map.W;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (!Map.Passable(nx, ny)) continue;
                    int ni = Map.Idx(nx, ny);
                    if (seen[ni]) continue;
                    seen[ni] = true; q.Enqueue(ni);
                }
            }
            return seen;
        }

        /// <summary>A mining zone's status for a team: free, yours (your deep mine on it), taken (someone else's), exhausted.</summary>
        public string ZoneStatus(DeepDeposit d, int team, out Entity mine)
        {
            mine = d.MineId != 0 ? Get(d.MineId) : null;
            if (d.Amount <= 0) return "exhausted";
            if (mine == null) return "free";
            return mine.Team == team ? "yours" : "taken";
        }

        /// <summary>Why a team's drill rig can't go to work on this deposit, or null if it can.</summary>
        public string DrillBlocker(DeepDeposit d, int team)
        {
            if (d == null) return "that zone doesn't exist";
            if (!Teams[team].Surveyed.Contains(d.Id)) return $"zone #{d.Id} isn't one of your mining zones (your surveyors haven't flagged it)";
            switch (ZoneStatus(d, team, out var mine))
            {
                case "exhausted": return $"zone #{d.Id} is exhausted";
                case "yours": return $"zone #{d.Id} already has your deep_mine #{mine.Id} on it (one mine per zone)";
                case "taken": return $"zone #{d.Id} is taken: another team's deep mine is on it";
            }
            return null;
        }

        /// <summary>This team's drill rigs heading for a zone (including any on a refuel stop along the way).</summary>
        public List<Entity> RigsBound(int team, int zone) =>
            Owned(team).Where(e => e.ZoneId == zone && (e.Order == Order.Drill || (e.Order == Order.Refuel && e.ResumeOrder == Order.Drill))).ToList();

        /// <summary>A drill rig on 'drill' drives to its zone and deploys into a deep mine on arrival.</summary>
        void UpdateDrill(Entity e)
        {
            var d = Map.DepositById(e.ZoneId);
            var why = DrillBlocker(d, e.Team);
            if (why != null) { DrillFailed(e, why); return; }
            e.OrderPos = d.Pos;
            if (Vec2.Dist(e.Pos, d.Pos) > 1.2f)
            {
                if (!FollowPath(e, d.Pos, 1f)) return;
                if (Vec2.Dist(e.Pos, d.Pos) > 3f) { DrillFailed(e, $"it can't reach {(int)d.Pos.X},{(int)d.Pos.Y}"); return; }
            }
            e.Moving = false;
            int rig = e.Id, zone = d.Id;
            why = Deploy(e, d);
            if (why != null) { DrillFailed(e, why); return; }
            Emit("drilled", e.Team, rig, d.MineId, d.Pos, key: "deep_mine",
                 text: $"drill_rig #{rig} reached zone #{zone} and deployed into deep_mine #{d.MineId}: {Defs.Ores[d.Type]}, {(int)d.Amount} left");
        }

        void DrillFailed(Entity e, string why)
        {
            int zone = e.ZoneId;
            Emit("drill_failed", e.Team, e.Id, zone, e.Pos, key: e.Def.Key, text: $"drill_rig #{e.Id} stopped short of zone #{zone}: {why}");
            e.ZoneId = 0;
            SetOrder(e, Order.Idle, e.Pos);
        }

        /// <summary>Ore left at which a deep mine warns it's running low: two minutes of pumping.</summary>
        public const float DeepMineWarnAmount = EntityDef.DeepMineRate * 120f;

        /// <summary>Seconds of pumping left in a deposit at this team's current rate (low power halves it).</summary>
        public static float DeepMineSecondsLeft(DeepDeposit d, Team team) => d.Amount / (EntityDef.DeepMineRate * (team.LowPower ? 0.5f : 1f));

        void DeepMineTick(Entity e, Team team)
        {
            var d = Map.DepositById(e.DepositId);
            if (d == null || d.Amount <= 0) { e.Working = false; return; }
            float take = MathF.Min(d.Amount, EntityDef.DeepMineRate * Dt * (team.LowPower ? 0.5f : 1f));
            // Heads-up before it runs dry, so there's time to survey and drill the next one.
            if (d.Amount > DeepMineWarnAmount && d.Amount - take <= DeepMineWarnAmount)
                Alerts.Raise(this, e.Team, "deep_mine_running_low", Priority.Medium, e.Center, hit: false)
                      .Lost.Add($"deep_mine #{e.Id}: {(int)(d.Amount - take)} {Defs.Ores[d.Type]} left, dry in about {DeepMineSecondsLeft(d, team):0}s; survey and send a drill_rig to the next deposit now");
            d.Amount -= take;
            team.Add(Defs.Ores[d.Type], take);
            e.WorkTimer += take;
            if (e.WorkTimer >= 1f) { team.Stats.OreMined += (int)e.WorkTimer; e.WorkTimer -= (int)e.WorkTimer; }
            e.Working = true;
            if (d.Amount <= 0)
            {
                d.Amount = 0;
                e.Working = false;
                Alerts.Raise(this, e.Team, "deep_mine_depleted", Priority.Medium, e.Center, hit: false)
                      .Lost.Add($"deep_mine #{e.Id}: the {Defs.Ores[d.Type]} deposit is used up; sell it and survey for another");
                Emit("depleted", e.Team, e.Id, 0, e.Center, key: e.Def.Key, text: $"deep_mine #{e.Id} has exhausted its {Defs.Ores[d.Type]} deposit");
            }
        }

        /// <summary>A mining truck that can't find any surface ore means it's time to go deep.</summary>
        void WarnSurfaceExhausted(Entity truck, int type)
        {
            var team = Teams[truck.Team];
            if (Time - team.SurfaceWarnedAt < 120f) return;
            team.SurfaceWarnedAt = Time;
            string what = type >= 0 ? Defs.Ores[type] : "surface ore";
            Alerts.Raise(this, truck.Team, "surface_ore_exhausted", Priority.Medium, truck.Pos, hit: false)
                  .Lost.Add($"mining truck #{truck.Id} can't find any {what} within reach. Train a geological_surveyor at a factory and survey for deep deposits, then deploy a drill_rig on one");
        }

        // ------------------------------------------------------------------ fuel

        /// <summary>Where a unit can refuel: aircraft on a landing pad (an airfield, or the factory that built a drone), vehicles at a depot.</summary>
        public static bool IsFuelPoint(Entity u, Entity s) =>
            !s.Dead && s.Team == u.Team && s != u &&
            (s.IsStructure
                ? s.IsComplete && (u.IsAir ? s.Def.Helipad || (u.Def.BuiltBy == Producer.Factory && s.Def.Produces == Producer.Factory) : s.Def.FuelDepot)
                : IsTanker(s) && !u.IsAir); // a repair truck refuels ground vehicles in the field

        /// <summary>A repair truck that can top up vehicles where they are: escort your army with one.</summary>
        public static bool IsTanker(Entity s) => !s.IsStructure && s.Def.RepairRate > 0 && !s.Def.Medic && !s.Stranded && !s.IsCarried && !s.Dead;

        public Entity NearestFuelPoint(Entity u)
        {
            Entity best = null; float bd = float.MaxValue;
            foreach (var s in Entities)
            {
                if (!IsFuelPoint(u, s)) continue;
                float d = Vec2.DistSq(s.Center, u.Pos);
                if (d < bd) { bd = d; best = s; }
            }
            return best;
        }

        void Refill(Entity u, float amount)
        {
            u.Fuel = MathF.Min(u.FuelMax, u.Fuel + amount);
            // A stranded vehicle stays put until it has enough to be worth moving, so it doesn't drive off a drop at a time.
            if (u.Stranded && u.Fuel >= u.FuelMax * 0.3f) u.Stranded = false;
            if (u.Fuel > u.FuelMax * 0.3f) u.FuelWarned = false;
        }

        /// <summary>An idle aircraft this close to a pad lands on it rather than hover.</summary>
        public const float IdleLandingRange = 12f;

        /// <summary>Burn, refuel, head home on low fuel, and run dry: a stranded vehicle, or a crashed aircraft.</summary>
        void UpdateFuel(Entity e)
        {
            float max = e.FuelMax;
            bool refuelling;
            if (e.IsAir)
            {
                // Parked on a pad: landed, refuelling, burning nothing.
                e.Landed = !e.Moving && (e.Order == Order.Idle || e.Order == Order.Refuel) &&
                           ((Tick + e.Id) % 5 == 0 ? OnPad(e) : e.Landed);
                refuelling = e.Landed;
            }
            else
            {
                if ((Tick + e.Id) % 5 == 0) e.AtDepot = NearDepot(e);
                refuelling = e.AtDepot;
            }
            if (refuelling) { if (e.Fuel < max) Refill(e, max / EntityDef.RefuelSeconds * Dt); }
            else if (e.IsAir) e.Fuel -= Dt * (e.Moving ? 1f : 0.6f); // hovering still burns
            else e.Fuel -= Vec2.Dist(e.Pos, e.PrevPos) / MathF.Max(0.1f, e.Def.Speed);

            if (e.Fuel <= 0)
            {
                e.Fuel = 0;
                if (e.IsAir) { Crash(e); return; }
                if (!e.Stranded)
                {
                    e.Stranded = true;
                    e.Moving = false;
                    var a = Alerts.Raise(this, e.Team, "units_stranded", Priority.High, e.Pos);
                    a.Lost.Add($"{e.Def.Key} #{e.Id} (out of fuel)");
                    Emit("stranded", e.Team, e.Id, 0, e.Pos, key: e.Def.Key,
                         text: $"{e.Def.Key} #{e.Id} ran out of fuel at {(int)e.Pos.X},{(int)e.Pos.Y}. It can still shoot, but can't move until a repair truck refuels it.");
                }
                return;
            }

            // An aircraft left hovering at home lands on the pad instead of burning its tank in the air. (Far from a pad
            // it keeps station: that's a spotter, and the bingo rule below still brings it home in time.)
            if (e.IsAir && e.Order == Order.Idle && !refuelling && !e.Moving && (Tick + e.Id) % 20 == 0 && Time >= e.NoAutoRefuelUntil)
            {
                var pad = NearestFuelPoint(e);
                if (pad != null && pad.DistFrom(e.Pos) <= IdleLandingRange) { BeginRefuel(e, pad); return; }
            }

            // Bingo fuel: head for the nearest pad or depot with enough left to get there, then carry on.
            if (e.Order == Order.Refuel || refuelling || (Tick + e.Id) % 10 != 0 || Time < e.NoAutoRefuelUntil) return;
            if (e.Fuel > max * 0.5f) return;
            if (!e.IsAir && e.Order == Order.Idle && !e.Moving) return; // a parked vehicle burns nothing
            var p = NearestFuelPoint(e);
            if (p == null)
            {
                if (!e.FuelWarned && e.Fuel < max * 0.3f)
                {
                    e.FuelWarned = true;
                    var a = Alerts.Raise(this, e.Team, "low_fuel", e.IsAir ? Priority.High : Priority.Medium, e.Pos);
                    a.Lost.Add($"{e.Def.Key} #{e.Id} ({StateView.Pct(e.FuelFraction)}% fuel, nowhere to refuel: {(e.IsAir ? "build an airfield" : "needs a command center, outpost, refinery or factory, or a repair truck")})");
                }
                return;
            }
            float speed = MathF.Max(0.1f, e.Def.Speed), dist = Vec2.Dist(e.Pos, p.Center);
            float need = e.IsAir ? dist / speed * 1.15f + 8f : dist * 1.4f / speed + 10f; // roads wind; keep a reserve
            // In a firefight a unit keeps a thinner reserve and fights on; it heads off once the shooting stops.
            // Cutting it that fine can strand it: that's the commander's risk to manage (escort with a tanker).
            if (Time - e.LastHitTime < 4f || Time - e.LastFiredAt < 4f) need = need * 0.55f;
            if (e.Fuel > need) return;
            BeginRefuel(e, p);
            Emit("low_fuel", e.Team, e.Id, p.Id, e.Pos, key: e.Def.Key,
                 text: $"{e.Def.Key} #{e.Id} is low on fuel ({StateView.Pct(e.FuelFraction)}%) and is heading to {p.Def.Key} #{p.Id} to refuel; it picks up its {e.ResumeOrder.ToString().ToLowerInvariant()} order afterwards.");
        }

        bool OnPad(Entity e)
        {
            foreach (var s in Owned(e.Team)) if (IsFuelPoint(e, s) && s.DistFrom(e.Pos) <= 1.5f) return true;
            return false;
        }

        bool NearDepot(Entity e)
        {
            foreach (var s in Owned(e.Team))
            {
                if (s.IsStructure && s.Def.FuelDepot && s.IsComplete && s.DistFrom(e.Pos) <= 2.5f) return true;
                if (s != e && IsTanker(s) && Vec2.Dist(s.Pos, e.Pos) <= 2f) return true; // parked beside a tanker
            }
            return false;
        }

        /// <summary>Send a unit to refuel at p, remembering what it was doing.</summary>
        public void BeginRefuel(Entity e, Entity p)
        {
            if (e.Order != Order.Refuel)
            {
                e.ResumeOrder = e.Order; e.ResumePos = e.OrderPos; e.ResumeTarget = e.TargetId; e.ResumeGuard = e.GuardPos;
                e.ResumeWaypoints.Clear(); e.ResumeWaypoints.AddRange(e.Waypoints); e.ResumeSpeedCap = e.SpeedCap;
                if (e.WaypointLoop) e.ResumeWaypoints.Add(new Vec2(float.NaN, 0)); // marker: it was a patrol
            }
            SetOrder(e, Order.Refuel, p.Center, p.Id);
        }

        void UpdateRefuelTrip(Entity e)
        {
            var p = Get(e.TargetId);
            if (p == null || !IsFuelPoint(e, p))
            {
                p = NearestFuelPoint(e);
                if (p == null) { FinishRefuel(e); return; }
                e.TargetId = p.Id; e.OrderPos = p.Center; e.Path = null;
            }
            if (e.IsAir)
            {
                if (Vec2.Dist(e.Pos, p.Center) > 0.6f) { StepToward(e, p.Center); return; }
            }
            else if (!p.IsStructure)
            {
                // Meeting a tanker in the field: pull up beside it (it may be moving with the army).
                if (Vec2.Dist(e.Pos, p.Pos) > 1.6f) { Chase(e, p); return; }
            }
            else if (p.DistFrom(e.Pos) > 2.2f)
            {
                if (FollowPath(e, DockPoint(p), 0.8f) && p.DistFrom(e.Pos) > 2.5f)
                {
                    // Can't get there: carry on, and don't try again for a while.
                    e.NoAutoRefuelUntil = Time + 30f;
                    FinishRefuel(e);
                }
                return;
            }
            e.Moving = false; e.Path = null;
            if (e.Fuel >= e.FuelMax * 0.99f) FinishRefuel(e);
        }

        void FinishRefuel(Entity e)
        {
            var o = e.ResumeOrder;
            if (o == Order.Idle || o == Order.Refuel)
            {
                // Aircraft stay parked on the pad; a vehicle drives back to its post.
                var post = e.ResumeGuard;
                SetOrder(e, Order.Idle, e.Pos);
                if (!e.IsAir && Vec2.Dist(post, e.Pos) > 4f) SetOrder(e, Order.Move, post);
            }
            else
            {
                e.Order = o; e.OrderPos = e.ResumePos; e.TargetId = e.ResumeTarget; e.GuardPos = e.ResumeGuard; e.Path = null; e.RepathTimer = 0;
                e.WaypointLoop = e.ResumeWaypoints.RemoveAll(p => float.IsNaN(p.X)) > 0;
                e.Waypoints.Clear(); e.Waypoints.AddRange(e.ResumeWaypoints); e.SpeedCap = e.ResumeSpeedCap;
                if (o == Order.Attack && Get(e.TargetId) == null) FinishOrder(e);
            }
            e.ResumeOrder = Order.Idle;
            Emit("refuelled", e.Team, e.Id, 0, e.Pos, key: e.Def.Key, text: $"{e.Def.Key} #{e.Id} refuelled and is back on {e.OrderName}.");
        }

        void Crash(Entity e)
        {
            Emit("destroyed", e.Team, e.Id, 0, e.Pos, key: e.Def.Key, text: $"{e.Def.Key} #{e.Id} ran out of fuel and crashed");
            Emit("crashed", e.Team, e.Id, 0, e.Pos, key: e.Def.Key);
            Teams[e.Team].Stats.UnitsLost++;
            TallyDeath(e, null);
            var a = Alerts.Raise(this, e.Team, "aircraft_crashed", Priority.High, e.Pos);
            a.Lost.Add($"{e.Def.Key} #{e.Id} (out of fuel)" + (e.Passengers.Count > 0 ? $" with {e.Passengers.Count} passengers" : ""));
            Remove(e);
        }

        // ------------------------------------------------------------------ movement

        /// <summary>Moves along a path to dest. Returns true on arrival.</summary>
        bool FollowPath(Entity e, Vec2 dest, float tolerance)
        {
            if (Vec2.Dist(e.Pos, dest) <= tolerance) { e.Moving = false; e.Path = null; return true; }
            if (e.IsAir) { StepToward(e, dest); return false; }
            if (e.Path == null)
            {
                e.Path = Paths.Find(e.Pos, dest);
                e.PathIdx = 0;
                if (e.Path == null || e.Path.Count == 0) { e.Moving = false; return true; }
            }
            if (!AdvancePath(e))
            {
                // Path exhausted but not at dest (unreachable): stop here.
                bool closeEnough = Vec2.Dist(e.Pos, dest) <= tolerance + 1.5f || !Map.Passable(Int2.Of(dest).X, Int2.Of(dest).Y);
                e.Path = null;
                if (closeEnough) { e.Moving = false; return true; }
                e.RepathTimer += Dt;
                if (e.RepathTimer > 3f) { e.Moving = false; return true; }
            }
            return false;
        }

        /// <summary>Steps along the current path. Returns false when the path is exhausted.</summary>
        bool AdvancePath(Entity e)
        {
            if (e.Path == null || e.PathIdx >= e.Path.Count) { e.Moving = false; return false; }
            var wp = e.Path[e.PathIdx];
            var wt = Int2.Of(wp);
            if (!Map.Passable(wt.X, wt.Y) && e.PathIdx < e.Path.Count - 1) { e.Path = null; return true; } // blocked: repath next tick
            StepToward(e, wp);
            if (Vec2.Dist(e.Pos, wp) < 0.25f) e.PathIdx++;
            return true;
        }

        void StepToward(Entity e, Vec2 p)
        {
            if (e.Stranded) { e.Moving = false; return; } // out of fuel
            var d = p - e.Pos;
            float len = d.Length;
            if (len < 1e-4f) return;
            float want = d.Angle;
            // Vehicles turn before driving; infantry pivot instantly.
            if (e.Def.Armor == Armor.Vehicle)
            {
                e.Facing = RotateToward(e.Facing, want, 5f * Dt);
                if (MathF.Abs(AngleDiff(e.Facing, want)) > 0.6f) { e.Moving = true; return; }
            }
            else e.Facing = want;
            if (!e.IsArmed || e.Cooldown <= 0 || e.Order != Order.Attack) e.TurretFacing = RotateToward(e.TurretFacing, e.Facing, 4f * Dt);
            float speed = e.SpeedCap > 0 ? MathF.Min(e.Def.Speed, e.SpeedCap) : e.Def.Speed;
            float step = MathF.Min(len, speed * Dt);
            var next = e.Pos + d / len * step;
            var nt = Int2.Of(next);
            var ct = Int2.Of(e.Pos);
            if (e.IsAir) { if (Map.InBounds(nt.X, nt.Y)) e.Pos = next; }
            else if (Map.Passable(nt.X, nt.Y) || !Map.Passable(ct.X, ct.Y)) e.Pos = next;
            else
            {
                // Blocked (usually clipping a corner): slide along whichever axis is free.
                var sx = new Vec2(next.X, e.Pos.Y); var sy = new Vec2(e.Pos.X, next.Y);
                var tx = Int2.Of(sx); var ty = Int2.Of(sy);
                if (Map.Passable(tx.X, tx.Y) && MathF.Abs(d.X) >= 0.01f) e.Pos = new Vec2(e.Pos.X + MathF.Sign(d.X) * step, e.Pos.Y);
                else if (Map.Passable(ty.X, ty.Y) && MathF.Abs(d.Y) >= 0.01f) e.Pos = new Vec2(e.Pos.X, e.Pos.Y + MathF.Sign(d.Y) * step);
            }
            e.Moving = true;
        }

        /// <summary>
        /// No two units may stay locked together: a unit that's trying to move but has made no real progress for 2s
        /// passes through other units for the next 3s (and replans its route) until it's clear.
        /// </summary>
        void CheckJam(Entity e)
        {
            if (e.IsAir || e.IsMine || e.Stranded) return;
            // Progress means getting closer to where it's headed: being shoved backwards doesn't count.
            var goal = e.Path != null && e.Path.Count > 0 ? e.Path[e.Path.Count - 1]
                     : e.Order == Order.Harvest && e.HarvestTile.HasValue ? e.HarvestTile.Value.Center : e.OrderPos;
            float d = Vec2.Dist(e.Pos, goal);
            if (!e.Moving) { e.ProgressPos = e.Pos; e.ProgressDist = d; e.ProgressAt = Time; return; }
            if (Time - e.ProgressAt < 2f) return;
            if (e.ProgressDist - d < 0.3f && Time >= e.GhostUntil)
            {
                e.GhostUntil = Time + 3f;
                e.Path = null; e.RepathTimer = 0;
            }
            e.ProgressPos = e.Pos; e.ProgressDist = d; e.ProgressAt = Time;
        }

        /// <summary>An idle unit in a moving friendly's way steps aside (and makes that spot its new post).</summary>
        void GiveWay(Entity idle, Entity mover)
        {
            if (idle.Order != Order.Idle || idle.Moving || idle.Team != mover.Team || Time < idle.GhostUntil - 2.5f) return;
            var dir = mover.Path != null && mover.PathIdx < mover.Path.Count ? mover.Path[mover.PathIdx] - mover.Pos : idle.Pos - mover.Pos;
            if (dir.LengthSq < 1e-4f) dir = new Vec2(1, 0);
            var side = new Vec2(-dir.Y, dir.X).Normalized * (((idle.Id & 1) == 0) ? 1.6f : -1.6f);
            foreach (var s in new[] { side, side * -1f, side + dir.Normalized * 1.2f })
            {
                var spot = idle.Pos + s;
                var t = Int2.Of(spot);
                if (!Map.Passable(t.X, t.Y)) continue;
                SetOrder(idle, Order.Move, spot);
                idle.GuardPos = spot;
                idle.GhostUntil = Time + 0.5f; // marks that it just gave way (no ping-pong)
                return;
            }
        }

        void Separate()
        {
            for (int i = 0; i < Entities.Count; i++)
            {
                var a = Entities[i];
                if (a.Dead || a.IsStructure || a.IsCarried || a.IsMine) continue;
                Near(a.Pos, 1.5f, nearSep);
                foreach (var b in nearSep)
                {
                    if (b.Id <= a.Id) continue; // each pair once
                    if (b.Dead || b.IsStructure || b.IsCarried || b.IsMine || a.IsAir != b.IsAir) continue;
                    if (Time < a.GhostUntil && a.Moving || Time < b.GhostUntil && b.Moving) continue; // unjamming: pass through
                    float min = a.Def.Radius + b.Def.Radius;
                    var d = b.Pos - a.Pos;
                    float dsq = d.LengthSq;
                    if (dsq >= min * min) continue;
                    float len = MathF.Sqrt(dsq);
                    var n = len > 1e-4f ? d / len : new Vec2(1, 0);
                    float push = (min - len) * 0.25f;
                    bool ha = HoldsLine(a), hb = HoldsLine(b);
                    if (ha && hb) continue;
                    if (ha) { GiveWay(b, a); TryNudge(b, n * (push * 2)); continue; }
                    if (hb) { GiveWay(a, b); TryNudge(a, n * (-push * 2)); continue; }
                    // Idle units get out of a moving friendly's way rather than wedging it.
                    if (a.Moving && !b.Moving) GiveWay(b, a); else if (b.Moving && !a.Moving) GiveWay(a, b);
                    // Stationary units yield to moving ones a bit more.
                    float wa = a.Moving ? 0.35f : 0.65f, wb = b.Moving ? 0.35f : 0.65f;
                    TryNudge(a, n * (-push * wa * 2));
                    TryNudge(b, n * (push * wb * 2));
                }
            }
        }

        void TryNudge(Entity e, Vec2 delta)
        {
            var next = e.Pos + delta;
            var t = Int2.Of(next);
            if (e.IsAir ? Map.InBounds(t.X, t.Y) : Map.Passable(t.X, t.Y)) e.Pos = next;
        }

        static float AngleDiff(float a, float b)
        {
            float d = b - a;
            while (d > MathF.PI) d -= 2 * MathF.PI;
            while (d < -MathF.PI) d += 2 * MathF.PI;
            return d;
        }

        static float RotateToward(float cur, float want, float maxStep)
        {
            float d = AngleDiff(cur, want);
            return MathF.Abs(d) <= maxStep ? want : cur + MathF.Sign(d) * maxStep;
        }

        // ------------------------------------------------------------------ bookkeeping

        void Cleanup()
        {
            if (Tick % 100 == 0) Entities.RemoveAll(e => e.Dead);
        }

        public void UpdateVisibility()
        {
            RebuildGrid();
            foreach (var team in Teams)
            {
                Array.Clear(team.Visible, 0, team.Visible.Length);
                foreach (var e in Entities)
                {
                    if (e.Dead || e.Team != team.Id || e.IsCarried) continue;
                    var c = e.Center;
                    float sight = e.Def.Sight + (e.IsStructure ? e.Def.SizeX / 2f : 0);
                    int r = (int)MathF.Ceiling(sight) + 1;
                    float rr = sight * sight;
                    // A tile is in view when sight reaches any part of it (not just its centre). Structures' footprints
                    // are whole tiles, so this keeps sight mutual: a unit sees a turret from as far as the turret sees it.
                    for (int y = (int)c.Y - r; y <= (int)c.Y + r; y++)
                        for (int x = (int)c.X - r; x <= (int)c.X + r; x++)
                        {
                            if (!Map.InBounds(x, y)) continue;
                            float dx = MathF.Max(0, MathF.Max(x - c.X, c.X - (x + 1))), dy = MathF.Max(0, MathF.Max(y - c.Y, c.Y - (y + 1)));
                            if (dx * dx + dy * dy <= rr) { int vi = Map.Idx(x, y); team.Visible[vi] = true; team.Explored[vi] = true; }
                        }
                }
                // Stealth units are only seen up close or inside one of this team's radar domes.
                var wasDetected = new HashSet<int>(team.Detected);
                team.Detected.Clear();
                foreach (var s in Entities)
                {
                    if (s.Dead || !s.Def.Stealth || s.Team == team.Id) continue;
                    foreach (var o in Entities)
                    {
                        if (o.Dead || o.Team != team.Id) continue;
                        if (o.IsCarried || o.IsMine) continue;
                        float range = o.Def.Key == "radar_dome" && o.IsComplete ? 16f : s.IsMine ? 1.5f : 3f;
                        if (Vec2.DistSq(o.Center, s.Pos) <= range * range)
                        {
                            team.Detected.Add(s.Id);
                            if (!wasDetected.Contains(s.Id) && !s.IsMine) Alerts.Raise(this, team.Id, "stealth_detected", Priority.Critical, s.Pos, attacker: s, hit: false);
                            break;
                        }
                    }
                }
                // Enemy forces showing up near any of this team's structures.
                foreach (var e in Entities)
                {
                    if (e.Dead || e.Team == team.Id || e.IsStructure || e.IsHarvester || e.IsMine || !IsVisibleTo(team.Id, e)) continue;
                    Near(e.Pos, 10f, nearVis);
                    if (nearVis.Any(s => !s.Dead && s.Team == team.Id && s.IsStructure && s.DistFrom(e.Pos) <= 10f))
                        Alerts.Raise(this, team.Id, "enemy_near_base", Priority.High, e.Pos, attacker: e, hit: false);
                }
                // Remember enemy structures that are in view; forget ones seen to be gone.
                foreach (var e in Entities)
                    if (!e.Dead && e.IsStructure && e.Team != team.Id && IsVisibleTo(team.Id, e))
                        team.KnownEnemyStructures[e.Id] = (e.Def.Key, e.Origin, e.Team);
                foreach (var id in team.KnownEnemyStructures.Keys.ToList())
                {
                    var known = team.KnownEnemyStructures[id];
                    if (Get(id) == null && team.Visible[Map.Idx(known.origin.X, known.origin.Y)]) team.KnownEnemyStructures.Remove(id);
                }
            }
        }

        void CheckProtection()
        {
            foreach (var t in Teams)
            {
                if (t.ProtectedUntil <= 0 || t.ProtectedUntil > Time || t.Left) continue;
                t.ProtectedUntil = 0;
                Emit("chat", -1, text: $"{t.Name} ({t.PlayerName ?? t.Controller}) is no longer protected.");
                Alerts.Raise(this, t.Id, "protection_ended", Priority.High, t.StartPos, hit: false);
            }
        }

        // ------------------------------------------------------------------ radar

        /// <summary>How far a radar dome picks up enemy aircraft as blips (beyond what it can actually see).</summary>
        public const float RadarRange = 28f;
        readonly Dictionary<(int team, int id), float> airWarned = new Dictionary<(int, int), float>();

        /// <summary>Enemy aircraft on this team's radar: within range of a working radar dome, not stealthed, not in plain sight.</summary>
        public List<Entity> RadarContacts(int team)
        {
            var domes = Entities.Where(s => !s.Dead && s.Team == team && s.IsStructure && s.IsComplete && s.Def.Key == "radar_dome").ToList();
            var list = new List<Entity>();
            if (domes.Count == 0 || Teams[team].LowPower) return list;
            foreach (var e in Entities)
                if (!e.Dead && e.IsAir && e.Team != team && !e.IsCarried && !e.Landed && (!e.Def.Stealth || IsVisibleTo(team, e)) &&
                    domes.Any(d => Vec2.Dist(d.Center, e.Pos) <= RadarRange))
                    list.Add(e);
            return list;
        }

        /// <summary>Early warning: enemy aircraft on radar raise an alert (high if they're close to your base).</summary>
        void CheckRadar()
        {
            if (Tick % TickRate != 0) return;
            foreach (var t in Teams)
            {
                if (t.Defeated || t.Left) continue;
                foreach (var a in RadarContacts(t.Id))
                {
                    if (airWarned.TryGetValue((t.Id, a.Id), out var at) && Time - at < 20f) continue;
                    airWarned[(t.Id, a.Id)] = Time;
                    bool close = Entities.Any(s => !s.Dead && s.Team == t.Id && s.IsStructure && s.DistFrom(a.Pos) <= 14f);
                    Alerts.Raise(this, t.Id, "air_contact", close ? Priority.High : Priority.Medium, a.Pos, attacker: a, hit: false)
                          .Lost.Add($"{(a.IsArmed ? "armed " : "")}enemy aircraft heading {Heading(a)}");
                }
            }
            if (airWarned.Count > 500) foreach (var k in airWarned.Where(kv => Time - kv.Value > 60).Select(kv => kv.Key).ToList()) airWarned.Remove(k);
        }

        static string Heading(Entity a)
        {
            var d = a.Pos - a.PrevPos;
            if (d.Length < 1e-4f) return "nowhere (hovering)";
            string[] names = { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };
            int i = (int)MathF.Round(MathF.Atan2(d.Y, d.X) / (MathF.PI / 4)); // y grows north
            return names[((i % 8) + 8) % 8];
        }

        /// <summary>Seconds a team may go without any way to make progress before it's resigned as lost.</summary>
        public float StallGrace = 90f;

        /// <summary>
        /// Why this team can't change the game any more, or null if it still can. It can if it has income (a command
        /// center's trickle, a working mining truck with ore left and somewhere to unload, a converter running),
        /// something being built or trained, something it can afford to build or train, or units that can still move
        /// and act (armed units, engineers, outpost trucks, or a repair truck that could refuel a stranded vehicle).
        /// </summary>
        public string Stalled(Team t)
        {
            var mine = Entities.Where(e => !e.Dead && e.Team == t.Id).ToList();
            bool hq = mine.Any(e => e.IsStructure && e.IsComplete && e.Def.Recipes.Any(r => r.Inputs.Count == 0));
            if (hq) return null;
            bool dropOff = mine.Any(e => e.IsStructure && e.IsComplete && e.Def.DropOff);
            if (dropOff && mine.Any(e => e.IsHarvester && !e.Stranded && !e.IsCarried) && (mine.Any(e => e.IsHarvester && e.Cargo > 0) || Map.Ore.Any(o => o > 0))) return null;
            if (mine.Any(e => e.IsStructure && e.Working)) return null;
            if (t.UnitQueues.Values.Any(q => q.Count > 0)) return null;
            foreach (var d in Defs.All.Values)
                if (d.BuiltBy != Producer.None && d.BuiltBy != Producer.CommandCenter && d.Cost.Count > 0 && MissingPrereq(t.Id, d) == null && t.Missing(d.Cost) == null)
                    return null; // can still train something (structures need a command center, which would already count)
            bool anyStranded = mine.Any(e => e.Stranded);
            if (mine.Any(e => !e.IsStructure && !e.IsMine && !e.IsCarried && !e.Stranded &&
                              (e.IsArmed || e.Def.Key == "engineer" || e.Def.DeploysInto != null || (e.Def.RepairRate > 0 && !e.Def.Medic && anyStranded))))
                return null;
            return "no command center, no working mining trucks, nothing affordable to train, and no units that can still move and fight";
        }

        void CheckStalled()
        {
            if (Tick % (TickRate * 5) != 0) return;
            foreach (var t in Teams)
            {
                if (t.Defeated || t.Left) { t.StalledSince = -1; continue; }
                var why = Stalled(t);
                if (why == null)
                {
                    if (t.StalledSince >= 0) Emit("unstalled", t.Id, text: $"{t.Name} can make progress again");
                    t.StalledSince = -1;
                    continue;
                }
                if (t.StalledSince < 0)
                {
                    t.StalledSince = Time;
                    Alerts.Raise(this, t.Id, "stalled", Priority.Critical, t.StartPos, hit: false)
                          .Lost.Add($"{why}. Unless that changes within {StallGrace:0}s you'll be resigned as lost");
                    continue;
                }
                if (Time - t.StalledSince >= StallGrace) Resign(t, why);
            }
        }

        void Resign(Team t, string why)
        {
            t.Resigned = true;
            if (Open)
            {
                Leave(t.Id, $"could make no further progress ({why}) and was resigned as lost");
                return;
            }
            t.Defeated = true;
            foreach (var e in Entities.Where(e => !e.Dead && e.Team == t.Id).ToList()) { Emit("destroyed", e.Team, e.Id, 0, e.Center, key: e.Def.Key); Remove(e); }
            Emit("defeated", t.Id, text: $"{t.Name} ({t.PlayerName ?? t.Controller}) could make no further progress ({why}) and was resigned as lost");
        }

        /// <summary>Open arena: someone has beaten every opponent in the room (it stays open for new challengers).</summary>
        public string ArenaChampion;
        bool contested;
        void CheckArenaCleared()
        {
            var alive = Teams.Where(t => !t.Left && !t.Defeated).ToList();
            if (alive.Count >= 2) { contested = true; ArenaChampion = null; return; }
            if (!contested || alive.Count != 1 || ArenaChampion != null) return;
            var champ = alive[0];
            ArenaChampion = champ.Name;
            contested = false; // a newcomer makes it a contest again, and a later clear is announced again
            string who = $"{champ.Name} ({champ.PlayerName ?? champ.Controller})";
            Emit("arena_cleared", champ.Id, pos: champ.StartPos, text: $"🏆 {who} has cleared the arena at {(int)(Time / 60)}:{(int)(Time % 60):00}: every opponent is defeated. The room stays open: new challengers can join and take them on.");
            Emit("chat", -1, text: $"🏆 {who} cleared the arena: every opponent defeated. The room stays open for new challengers.");
            Alerts.Raise(this, champ.Id, "arena_cleared", Priority.High, champ.StartPos, hit: false).Lost.Add("every opponent is defeated; new challengers may join at any time, so keep your defences up");
        }

        void CheckVictory()
        {
            CheckProtection();
            CheckStalled();
            CheckRadar();
            if (Open)
            {
                // An open arena never ends: teams that lose every structure are out, everyone else plays on.
                foreach (var team in Teams)
                {
                    if (team.Defeated || Entities.Any(e => !e.Dead && e.Team == team.Id && e.IsStructure)) continue;
                    team.Defeated = true;
                    foreach (var e in Entities.Where(e => !e.Dead && e.Team == team.Id).ToList()) { Emit("destroyed", e.Team, e.Id, 0, e.Center, key: e.Def.Key); Remove(e); }
                    Emit("defeated", team.Id, text: $"{team.Name} ({team.PlayerName ?? team.Controller}) has been eliminated");
                    ForgetTeam(team.Id);
                }
                CheckArenaCleared();
                return;
            }
            foreach (var team in Teams)
            {
                if (team.Defeated) continue;
                bool hasStructure = Entities.Any(e => !e.Dead && e.Team == team.Id && e.IsStructure);
                if (!hasStructure)
                {
                    team.Defeated = true;
                    foreach (var e in Entities) if (!e.Dead && e.Team == team.Id) { Emit("destroyed", e.Team, e.Id, 0, e.Center, key: e.Def.Key); Remove(e); }
                    Emit("defeated", team.Id, text: $"{team.Name} ({team.PlayerName ?? team.Controller}) has been defeated");
                }
            }
            var alive = Teams.Where(t => !t.Defeated).ToList();
            if (alive.Count <= 1)
            {
                GameOver = true;
                Winner = alive.Count == 1 ? alive[0].Id : -1;
                Emit("game_over", Winner, text: Winner >= 0 ? $"{Teams[Winner].Name} ({Teams[Winner].PlayerName ?? Teams[Winner].Controller}) wins" : "draw");
            }
        }
    }
}
