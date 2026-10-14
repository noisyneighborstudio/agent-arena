using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public class ProdItem
    {
        public string Key;
        public float Progress;   // seconds of work done
        public long CmdId;       // the API command that queued it (observation feed), 0 = none
        public float GrantedShare; // units: the share of its price the arena's gift paid (the unit carries it, and a refund returns it as gift)
        public int StructureId;  // structures: the foundation entity being built; units: the building chosen to train it (0 = any free one)
        public int WorkerId;     // units: the building working on it right now (0 = waiting for a free one)
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
        /// <summary>The ore value of everything this team destroyed (World.ValueOf), and the salvage its kills left on the ground.</summary>
        public int KillValue, SalvageLeft;
        /// <summary>How many of Kills are counted in KillValue. Kills from before kill value was tracked (a game resumed
        /// across that upgrade) are backfilled at a typical unit's value (World.BackfillKillValue).</summary>
        public int KillsValued;
        /// <summary>Of OreMined, what came from deep mines (the rest is surface ore and salvage, by truck).</summary>
        public int DeepMined;
        /// <summary>Derricks this team has captured, and the steel they've paid it.</summary>
        public int DerricksCaptured;
        public float DerrickSteel;
        /// <summary>Of OreMined, the ore its trucks delivered that was salvage (put down by kills, spills and leavers' bases, on any ground: Map.Salvage). Counted at delivery, so a load lost on the way never counts.</summary>
        public int SalvageMined;
        /// <summary>Steel paid in upkeep for defences beyond the free ones.</summary>
        public float UpkeepPaid;
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
        /// <summary>
        /// The numbers behind the little scatter its new units stand off their door by (World.SpawnUnit). Every team's begins at the same
        /// place, so a team's n-th unit is scattered as every other team's n-th is; drawn from one stream shared by the teams, the scatter
        /// went to whichever team's unit came first (seat 0's, in a tick), and the first truck reached its ore sooner or later than its twin.
        /// </summary>
        public readonly SimRng Scatter = new SimRng(ScatterStart);
        public const ulong ScatterStart = 7;
        /// <summary>Enemy stealth units this team can currently see.</summary>
        public readonly HashSet<int> Detected = new HashSet<int>();
        /// <summary>Enemies briefly revealed by firing on this team (entity id -> game time the reveal ends).</summary>
        public readonly Dictionary<int, float> Revealed = new Dictionary<int, float>();

        /// <summary>Plasma the team's fusion reactors have burned but the stockpile hasn't paid yet (World.SettleFusionBurn): not available to spend.</summary>
        public float PlasmaOwed;
        public int Amount(string item) => Stock.TryGetValue(item, out var v) ? (int)(item == "plasma" ? v - PlasmaOwed : v) : 0;
        /// <summary>Change the stockpile. Taking something out spends the gift first (see Granted); adding is income, and is the team's own.</summary>
        public void Add(string item, float n)
        {
            Stock[item] = (Stock.TryGetValue(item, out var v) ? v : 0) + n;
            if (n < 0 && Granted.TryGetValue(item, out var g)) Granted[item] = g - MathF.Min(-n, GiftFree(item));
        }
        /// <summary>
        /// The gift still free to spend. Plasma the reactors have burned but the stockpile hasn't paid yet (PlasmaOwed) is charged
        /// to the gift first, as any spending is, so that much of it is already spoken for.
        /// </summary>
        float GiftFree(string item) => item == "plasma" ? MathF.Max(0f, Gift(item) - PlasmaOwed) : Gift(item);
        /// <summary>
        /// The stockpile pays `take` of the plasma the reactors owe (World.SettleFusionBurn): it comes off the gift first, like
        /// the rest of the spending, so the gift left free (GiftFree) is the same before and after.
        /// </summary>
        public void SettleOwed(float take)
        {
            Stock["plasma"] = (Stock.TryGetValue("plasma", out var v) ? v : 0f) - take;
            PlasmaOwed -= take;
            if (Granted.TryGetValue("plasma", out var g)) Granted["plasma"] = MathF.Max(0f, g - take);
        }
        /// <summary>
        /// The part of the stockpile the arena gave this team and it hasn't spent yet, per item (the starting ore, a late joiner's
        /// catch-up materials). It isn't the team's own value, so a stockpile spill leaves it out (Own) and joining and leaving
        /// cashes nothing out. Spending takes from it first (Add, Pay), and what a purchase took from it is remembered on the
        /// thing bought (Entity.GrantedShare) and handed back by a refund (Refund) or on to what a converter makes (Gain).
        /// So it never exceeds the stock. Saved with the game.
        /// </summary>
        public readonly Dictionary<string, float> Granted = new Dictionary<string, float>();
        /// <summary>Stock handed to the team by the arena: in the stockpile like any other, and counted in Granted.</summary>
        public void Give(string item, float n) { Add(item, n); Granted[item] = Gift(item) + n; }
        public float Gift(string item) => Granted.TryGetValue(item, out var g) ? g : 0f;
        /// <summary>What of an item the team holds above its gift: the part it mined, made or won itself, which a spill gives up.</summary>
        public float Own(string item) => Stock.TryGetValue(item, out var v) ? MathF.Max(0f, (item == "plasma" ? v - PlasmaOwed : v) - GiftFree(item)) : 0f;
        /// <summary>Add `n` of an item of which `giftShare` is gift: a refund of something bought with gift, or what a converter makes of it.</summary>
        public void Gain(string item, float n, float giftShare)
        {
            Add(item, n);
            if (giftShare > 0f && n > 0f) Granted[item] = Gift(item) + n * giftShare;
        }
        /// <summary>Give back `factor` of a cost; `giftShare` of it was paid from the gift and goes back as gift.</summary>
        public void Refund(Dictionary<string, int> cost, float factor, float giftShare) { foreach (var kv in cost) Gain(kv.Key, kv.Value * factor, giftShare); }
        static float Worth(string item) => World.OreMix.TryGetValue(item, out var m) ? m[0] + m[1] + m[2] + m[3] : 0f;
        public string Missing(Dictionary<string, int> cost)
        {
            var lacking = cost.Where(kv => Amount(kv.Key) < kv.Value).Select(kv => $"{kv.Key} {Amount(kv.Key)}/{kv.Value}").ToList();
            return lacking.Count == 0 ? null : "not enough " + string.Join(", ", lacking);
        }
        /// <summary>
        /// Pay `factor` of a cost, from the gift first. Returns the share of what was paid (in raw ore) that came out of the
        /// gift: 1 if the arena's gift paid for all of it. What was bought carries that share, so it's worth that much less
        /// as kill value, salvage or a refund (Entity.GrantedShare).
        /// </summary>
        public float Pay(Dictionary<string, int> cost, float factor = 1f)
        {
            float total = 0f, fromGift = 0f;
            foreach (var kv in cost)
            {
                float amount = kv.Value * factor, w = Worth(kv.Key);
                total += amount * w; fromGift += MathF.Min(GiftFree(kv.Key), amount) * w;
                Add(kv.Key, -amount);
            }
            float share = total > 0f ? fromGift / total : 0f;
            return share > 0.9999f ? 1f : share < 0.0001f ? 0f : share; // (all or nothing stays exact)
        }
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
        /// <summary>The units of one kind in production right now: those a building is working on (several at once with several buildings), in queue order.</summary>
        public List<ProdItem> Working(Producer kind)
        {
            var now = new List<ProdItem>();
            foreach (var p in UnitQueues[kind]) if (p.WorkerId != 0) now.Add(p);
            return now;
        }
        /// <summary>Enemy structures this team has seen: id -> (key, origin, team).</summary>
        // Sorted by id so every reader (the scripted AI, rally points) sees the same order in a game and in its resumed copy.
        public readonly SortedDictionary<int, (string key, Int2 origin, int team)> KnownEnemyStructures = new SortedDictionary<int, (string, Int2, int)>();
        /// <summary>Deep deposits this team's surveyors have found (by deposit id), and where they've surveyed.</summary>
        public readonly HashSet<int> Surveyed = new HashSet<int>();
        public readonly List<Vec2> SurveySites = new List<Vec2>();
        /// <summary>Mining zones: the flag a surveyor planted on each deep deposit it found (key = deposit id = zone id).</summary>
        public readonly Dictionary<int, ZoneFlag> Zones = new Dictionary<int, ZoneFlag>();
        public float SurfaceWarnedAt = -999;
        /// <summary>Home ores already warned about at half left (bit per ore type): each gets its own warning.</summary>
        public int HomeOreWarned;
        /// <summary>Game time of this team's last command, state or wait call (or of its seating): the lobby's idle_s, and CheckIdle resigns seats nobody plays.</summary>
        public float LastActiveAt;
        /// <summary>Items converters leave alone below this amount ('reserve' command), so raw-ore costs stay payable.</summary>
        public readonly Dictionary<string, int> Reserve = new Dictionary<string, int>();
        /// <summary>What the team is saving up for ('save_for'): converters leave its cost alone until it's bought.</summary>
        public string SaveFor;
        public readonly Dictionary<string, int> SaveForCost = new Dictionary<string, int>();
        public int Reserved(string item) => System.Math.Max(Reserve.TryGetValue(item, out var n) ? n : 0, SaveForCost.TryGetValue(item, out var s) ? s : 0);
        /// <summary>Bought what it was saving for: the hold on converters lifts.</summary>
        public bool Bought(string key) { if (SaveFor != key) return false; SaveFor = null; SaveForCost.Clear(); return true; }
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
        /// <summary>
        /// A copy of a game the server owns (the browser build): it moves and fights on between snapshots, but never
        /// decides the game: no defeats, stalls, arena clears, match clock or next match. Those arrive in the next
        /// snapshot. A player's copy is fogged, so its own count of who's still standing would be wrong anyway.
        /// </summary>
        public bool Mirror;

        /// <summary>
        /// Orders players gave over the API, the last CommandLogTicks of them, numbered (CommandSeq is the last number, and
        /// is saved). A view's copy of the game, which runs on between snapshots, gives them again at the same ticks
        /// (Game.Replay). Without them it learns of an order only at the next snapshot, seconds later, and every unit
        /// ordered meanwhile slides over to where it should be.
        /// </summary>
        public readonly List<LoggedCommand> CommandLog = new List<LoggedCommand>();
        public long CommandSeq;
        public const int CommandLogTicks = 30 * TickRate;

        /// <summary>Logs a command that worked (ApiServer), unless it changes nothing a copy shows (chat, a tech proposal).</summary>
        public void LogCommand(int team, Dictionary<string, object> cmd)
        {
            var type = cmd.Str("type", "").ToLowerInvariant();
            if (type == "say" || type == "propose_tech" || type == "start_match") return;
            CommandLog.Add(new LoggedCommand { Seq = ++CommandSeq, Tick = Tick, Team = team, Cmd = cmd });
            int cut = 0;
            while (cut < CommandLog.Count && (CommandLog[cut].Tick < Tick - CommandLogTicks || CommandLog.Count - cut > 400)) cut++;
            if (cut > 0) CommandLog.RemoveRange(0, cut);
        }
        public int MaxPlayers = 8;
        public int MaxMapSize = Map.MaxSize;
        public int GrowStep = 32;
        /// <summary>A joiner's base goes at least this far from any enemy structure or armed unit, if the map can grow that far.</summary>
        public float SafeJoinDistance = 56f;
        /// <summary>
        /// The later the join, the farther out: established players have bigger armies that range farther, so the clearance
        /// grows this many tiles per hour of match time, up to MaxJoinClearance (the map grows a wider strip to fit it).
        /// </summary>
        public const float JoinClearancePerHour = 32f, MaxJoinClearance = 160f;
        public float JoinClearance => MathF.Min(SafeJoinDistance + JoinClearancePerHour * Time / 3600f, MathF.Max(SafeJoinDistance, MaxJoinClearance));
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

        /// <summary>Protection covers a newcomer's base, not the contested middle: a derrick it holds can be shot and captured like anyone's.</summary>
        public bool Shielded(Entity t) => t.Def.Key != "derrick" && IsProtected(t.Team);

        /// <summary>The centres of the enemy structures a team knows of (derricks aside, as in the trucks' own Safe rule), which a protected
        /// team's trucks keep HostileOreRadius from: the mirror of OreReserved, so protection can't be used to mine an enemy's home field
        /// untouched. Only what the team has seen counts, so a refusal tells it nothing about fogged buildings. Empty when the team isn't
        /// protected; built once a tick per team.</summary>
        static readonly List<Vec2> NoBases = new List<Vec2>();
        readonly Dictionary<int, (int tick, List<Vec2> bases)> barredBases = new Dictionary<int, (int, List<Vec2>)>();
        public List<Vec2> BarredBases(int team)
        {
            if (!IsProtected(team)) return NoBases;
            if (barredBases.TryGetValue(team, out var c) && c.tick == Tick) return c.bases;
            var bases = Teams[team].KnownEnemyStructures.Values.Where(k => k.team >= 0 && k.team != team && k.key != "derrick").Select(k => k.origin.Center).ToList();
            barredBases[team] = (Tick, bases);
            return bases;
        }

        public static bool NearBarredBase(Int2 tile, List<Vec2> bases)
        {
            foreach (var b in bases) if (Vec2.DistSq(b, tile.Center) < HostileOreRadius * HostileOreRadius) return true;
            return false;
        }
        /// <summary>The default team names, by seat. An agent can name its own team instead (NameTeam); the colour stays with the seat.</summary>
        public static readonly string[] Flavors = { "Blueberry", "Cherry", "Lime", "Lemon", "Grape", "Blackberry", "Spearmint", "Plum" };
        /// <summary>Names that would pass for the game itself in chat ("arena: ...") or in the lobby.</summary>
        static readonly string[] ReservedTeamNames = { "arena", "server", "neutral", "spectator", "nobody", "you" };
        static string NameKey(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        /// <summary>
        /// Give a team a name of its own. Names are cleaned (Text.TeamName), and must differ from every other team's name
        /// and from the other seats' default flavours, ignoring case, spaces and punctuation. Returns null when applied,
        /// else the reason it wasn't (the team keeps the name it had).
        /// </summary>
        public string NameTeam(Team team, string wanted)
        {
            var n = Text.TeamName(wanted);
            if (n == null) return "a team name needs some letters or digits";
            var key = NameKey(n);
            if (ReservedTeamNames.Contains(key)) return "that name is reserved";
            for (int i = 0; i < Flavors.Length; i++)
                if (i != team.Id % Flavors.Length && NameKey(Flavors[i]) == key) return "that is another seat's default name";
            foreach (var o in Teams)
                if (o != team && NameKey(o.Name ?? "") == key) return "another team already has that name";
            team.Name = n;
            return null;
        }
        public readonly List<Team> Teams = new List<Team>();
        public readonly Dictionary<int, Entity> ById = new Dictionary<int, Entity>();
        public readonly List<Entity> Entities = new List<Entity>();
        public readonly List<Projectile> Projectiles = new List<Projectile>();
        public readonly List<GameEvent> Events = new List<GameEvent>();
        /// <summary>How many of each event type have happened (the event list itself is a rolling window).</summary>
        public readonly Dictionary<string, int> EventCounts = new Dictionary<string, int>();
        /// <summary>Units the dance guard settled (WatchDances), by "unit_key order": a designed situation should never need it.</summary>
        public readonly SortedDictionary<string, int> SettleCounts = new SortedDictionary<string, int>();
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
            EnsureDerricks();
            UpdatePower();
            UpdateVisibility();
        }

        int seatCounter;

        /// <summary>Create a team in a new slot, or in slot `reuse` (a seat whose player left or was eliminated).</summary>
        Team CreateTeam(Vec2 spawn, int reuse = -1)
        {
            int t = reuse >= 0 ? reuse : Teams.Count;
            var team = new Team { Id = t, Name = Flavors[t % Flavors.Length], StartPos = spawn, Visible = new bool[Map.W * Map.H], Explored = new bool[Map.W * Map.H], Seat = ++seatCounter, LastActiveAt = Time };
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
            // All of it is a gift: it isn't the team's own value to cash out or to be farmed for (Team.Granted, Entity.GrantedShare).
            team.Give("iron_ore", 500);
            team.Give("copper_ore", 150);
            var s = Int2.Of(spawn);
            var hq = SpawnStructure(t, "command_center", new Int2(s.X - 1, s.Y - 1), 1f, grantedShare: 1f);
            SpawnUnit(t, "mining_truck", hq).GrantedShare = 1f;
            for (int i = 0; i < 2; i++) SpawnUnit(t, "rifleman", hq).GrantedShare = 1f;
            return team;
        }

        // ------------------------------------------------------------------ open arena: join and leave

        public int ActivePlayers => Teams.Count(t => !t.Left && !t.Defeated);
        /// <summary>Seats a join could take now: MaxPlayers, and at most one per team colour (AddTeam recycles departed players' seats).</summary>
        public int SeatsFree => Math.Max(0, Math.Min(MaxPlayers, Flavors.Length) - ActivePlayers);

        /// <summary>
        /// A new player joins mid-game. The map grows (east and north, existing coordinates unchanged) to make
        /// room and add resources, up to MaxMapSize; at the cap, a free base site is reused. Returns null with a
        /// reason if the arena is full.
        /// </summary>
        public Team AddTeam(string controller, string playerName, out string error) => AddTeam(controller, playerName, null, out error, out _);

        /// <summary>
        /// As above, and the joiner may name their team (NameTeam). If the name can't be used the team keeps its flavour
        /// and nameProblem says why. A recycled seat starts over: its old occupant's name goes with them.
        /// </summary>
        public Team AddTeam(string controller, string playerName, string teamName, out string error, out string nameProblem)
        {
            error = null; nameProblem = null;
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
                    if (clear >= JoinClearance) break;
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
            if (teamName != null) nameProblem = NameTeam(team, teamName);
            GiveCatchUp(team);
            team.ProtectedUntil = Time + ProtectionSeconds;
            scoreCache = null; // on the scoreboard now, even within the second (or in a waiting room, where no time passes)
            UpdatePower();
            UpdateVisibility();
            // Everyone hears that someone joined, never where: a base's location is for scouting (the newcomer's own join
            // reply and state say where theirs is). So the event carries no position either (it's public).
            Emit("joined", team.Id, text: $"{playerName} joined as {team.Name}. The map is now {Map.W}x{Map.H}.");
            Emit("chat", -1, text: $"{playerName} joined as {team.Name}.");
            return team;
        }

        /// <summary>
        /// Late joiners get a head start that scales with the arena's age: for each minute (up to 30) 60 steel, 30 copper,
        /// 8 circuits and 20 iron ore, and once the arena is 3 minutes old, a finished power plant and mining refinery (with its
        /// free truck). About a fifth to a third of what a founding scripted team has had come in by then (measured with
        /// headless --econ-report), and enough for a house AI to stand the same barracks, factory, turret and refineries as a
        /// founder inside the 5 minutes of protection.
        /// </summary>
        void GiveCatchUp(Team team)
        {
            float minutes = MathF.Min(Time / 60f, 30f);
            if (minutes < 0.5f) return;
            team.Give("steel", 60 * minutes);
            team.Give("copper", 30 * minutes);
            team.Give("circuits", 8 * minutes);
            team.Give("iron_ore", 20 * minutes);
            if (minutes < 3f) return;
            foreach (var key in new[] { "power_plant", "mining_refinery" })
            {
                var spot = FindPlacement(team.Id, key);
                if (spot.HasValue) { SpawnStructure(team.Id, key, spot.Value, 1f, grantedShare: 1f); team.Stats.Count(key); }
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
            EnsureDerricks(); // a bigger map gets more, around its new middle
        }

        /// <summary>
        /// The raw ores one unit of each item is made from, as [iron, copper, crystal, uranium] (the Map.Iron... order): what a
        /// unit of it is worth in kill value, salvage, spills and leaves. Every conversion reads this table, and a test keeps it
        /// in step with the converters' recipes.
        /// </summary>
        internal static readonly Dictionary<string, float[]> OreMix = new Dictionary<string, float[]>
        {
            { "iron_ore", new float[] { 1, 0, 0, 0 } }, { "steel", new float[] { 1, 0, 0, 0 } },
            { "copper_ore", new float[] { 0, 1, 0, 0 } }, { "copper", new float[] { 0, 1, 0, 0 } },
            { "circuits", new float[] { 1, 2, 0, 0 } },  // 2 copper + 1 steel
            { "crystal", new float[] { 0, 0, 1, 0 } }, { "lenses", new float[] { 0, 0, 2, 0 } },
            { "composite", new float[] { 2, 0, 1, 0 } }, // 2 steel + 1 crystal
            { "uranium", new float[] { 0, 0, 0, 1 } }, { "plasma", new float[] { 0, 0, 0, 2 } },
        };

        /// <summary>
        /// A player leaves for good. Their buildings, units and stockpile become salvage: ore piles on the old
        /// base's footprint that anyone's mining trucks can collect, first come, first served.
        /// </summary>
        /// <summary>Other teams forget what they knew about this team's buildings (it's gone, or its seat is reused).</summary>
        void ForgetTeam(int teamId)
        {
            foreach (var o in Teams)
                foreach (var kv in o.KnownEnemyStructures.Where(kv => kv.Value.team == teamId).ToList())
                {
                    // Its derricks go back to neutral (DerrickReleased); everyone who knew of one still knows where it is.
                    if (kv.Value.key == "derrick") o.KnownEnemyStructures[kv.Key] = (kv.Value.key, kv.Value.origin, -1);
                    else o.KnownEnemyStructures.Remove(kv.Key);
                }
        }

        /// <summary>
        /// Spread ore values (per ore type) as salvage anyone can mine: on these tiles first, then on open ground around
        /// them, as many tiles as it takes (a tile holds at most Map.MaxOrePerTile). Returns the amount actually placed,
        /// and lists where each pile landed in `landed` (for saying where it is).
        /// </summary>
        float SpillSalvage(float[] value, List<Int2> tiles, List<(Int2 tile, int ore)> landed = null)
        {
            if (value.Sum() <= 0 || tiles.Count == 0) return 0;
            // Free ground, nearest the footprint first: the footprint itself, then open tiles with no ore on them.
            var footprint = new HashSet<Int2>(tiles);
            float cx = (float)tiles.Average(t => t.X) + 0.5f, cy = (float)tiles.Average(t => t.Y) + 0.5f;
            // A tile something still stands on (a captured command center's: trucks couldn't reach it) or that already holds ore takes none.
            var queue = tiles.Where(p => Map.Occupant[Map.Idx(p.X, p.Y)] == 0 && Map.Ore[Map.Idx(p.X, p.Y)] == 0).ToList();
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
                    Map.Salvage[i] = Map.Ore[i];
                    placed += Map.Ore[i];
                    landed?.Add((queue[ti], Map.Ore[i]));
                }
            }
            MapVersion++;
            if (placed > 0) oreDirty = true; // the viewers fetch the ore map again when OreVersion moves
            return placed;
        }

        /// <summary>
        /// Where spilled ore landed, in words and as a spot for an alert: the sector it all lies in, or the sectors it is
        /// split across (biggest first), and the middle of the biggest lot. `fallback` if none landed.
        /// </summary>
        static (string where, Vec2 at) SpillSite(List<(Int2 tile, int ore)> landed, Vec2 fallback)
        {
            if (landed.Count == 0) return ($"sector {StateView.Sector(fallback)}", fallback);
            var lots = landed.GroupBy(l => StateView.Sector(l.tile.Center))
                             .Select(g => (sector: g.Key, ore: g.Sum(l => l.ore), at: new Vec2(g.Average(l => l.tile.Center.X), g.Average(l => l.tile.Center.Y))))
                             .OrderByDescending(l => l.ore).ThenBy(l => l.sector).ToList();
            if (lots.Count == 1) return ($"sector {lots[0].sector}", lots[0].at);
            string list = string.Join(", ", lots.Take(3).Select(l => $"{l.sector} ({l.ore} units)")) + (lots.Count > 3 ? $" and {lots.Count - 3} more" : "");
            return ($"sectors {list}", lots[0].at);
        }

        /// <summary>
        /// A command center has left its owner (destroyed, sold or captured: Remove and the capture both come here, so no
        /// route misses it). If it was the team's last, everything in its stockpile spills out as salvage ore on the
        /// footprint, first come, first served; one still standing (captured) gets it on the open ground around it.
        /// The team plays on with whatever else it has. `how` finishes "its last command center ...", e.g. "fell".
        /// </summary>
        void ReleaseStockpile(Entity cc, int owner, string how)
        {
            var t = Teams[owner];
            // A team that's already out is taken apart whole, stockpile and all (Dismantle).
            if (t.Left || t.Defeated || Entities.Any(o => !o.Dead && o.Team == owner && o.Def.Key == "command_center")) return;
            bool had = t.Stock.Values.Any(v => v > 0);
            var value = TakeStockpile(t);
            var tiles = new List<Int2>();
            for (int y = 0; y < cc.Def.SizeY; y++) for (int x = 0; x < cc.Def.SizeX; x++) tiles.Add(new Int2(cc.Origin.X + x, cc.Origin.Y + y));
            float total = SpillSalvage(value, tiles);
            var where = StateView.Sector(cc.Center);
            var truck = Entities.FirstOrDefault(e => !e.Dead && e.Team == t.Id && e.Def.DeploysInto == "command_center");
            string rebuild = truck != null ? $". Deploy your construction_truck #{truck.Id} into a new command center somewhere safe to rebuild" : "";
            if (total <= 0)
            {
                // Nothing above what the arena gave it, so nothing to scavenge: the owner still hears that its stockpile is gone.
                if (had) Alerts.Raise(this, t.Id, "stockpile_lost", Priority.Critical, cc.Center, hit: false).Lost.Add($"your last command center {how}, so your stockpile is gone (it was only what you were given at the start, so there is nothing to scavenge){rebuild}");
                return;
            }
            string msg = $"{t.Name}'s last command center {how} at sector {where}: its stockpile is gone, and about {(int)total} units of it spilled out as salvage ore. First come, first served.";
            Emit("chat", -1, text: msg);
            Alerts.Raise(this, t.Id, "stockpile_lost", Priority.Critical, cc.Center, hit: false).Lost.Add($"your last command center {how}, so your stockpile is gone: the {(int)total} units of it above what you were given at the start spilled out as salvage at {where}; anyone can mine it, so get your trucks there first{rebuild}");
            foreach (var other in Teams.Where(o => o.Id != t.Id && !o.Left && !o.Defeated))
                Alerts.Raise(this, other.Id, "salvage_available", Priority.High, cc.Center, hit: false).Lost.Add($"{(int)total} units of salvage ore at {where}");
        }

        /// <summary>
        /// A team that's out for good (left, or knocked out) is taken apart: its derricks go back to neutral, and every
        /// building, unit and the whole stockpile turn to salvage ore on its old base (DismantleStructureShare of a
        /// building's value, DismantleUnitShare of a unit's, all of the stockpile). Returns the ore placed and where it landed
        /// (the structures' footprints, so not always the first base).
        /// </summary>
        (float total, string where, Vec2 at) Dismantle(Team t)
        {
            ReleaseDerricks(t.Id);
            t.Defeated = true; // first: a command center going with the rest isn't a "last one fell" spill (ReleaseStockpile)
            var value = TakeStockpile(t);
            var tiles = new List<Int2>();
            foreach (var e in Entities.Where(e => !e.Dead && e.Team == t.Id).ToList())
            {
                if (e.IsStructure)
                {
                    AddWorth(value, e, DismantleStructureShare);
                    for (int y = 0; y < e.Def.SizeY; y++) for (int x = 0; x < e.Def.SizeX; x++) tiles.Add(new Int2(e.Origin.X + x, e.Origin.Y + y));
                }
                else if (!e.IsMine) AddWorth(value, e, DismantleUnitShare);
                Emit("salvaged", e.Team, e.Id, 0, e.Center, key: e.Def.Key); // dismantled, not destroyed: no wreck
                Remove(e);
            }
            if (tiles.Count == 0) tiles.Add(Int2.Of(t.StartPos));
            var landed = new List<(Int2 tile, int ore)>();
            float total = SpillSalvage(value, tiles, landed);
            var (where, at) = SpillSite(landed, t.StartPos);
            return (total, where, at);
        }

        /// <summary>
        /// A team knocked out (no structures or construction truck left, or resigned in a closed game) is dismantled into
        /// salvage like a leave. Returns the sentence to add to the announcement; the others are told where the ore is.
        /// </summary>
        string Eliminate(Team t)
        {
            var (total, where, at) = Dismantle(t);
            if (total <= 0) return "";
            foreach (var other in Teams.Where(o => o.Id != t.Id && !o.Left && !o.Defeated))
                Alerts.Raise(this, other.Id, "salvage_available", Priority.High, at, hit: false).Lost.Add($"{(int)total} units of salvage ore at {where}");
            return $". What it had left (stockpile and units) is now about {(int)total} units of salvage ore at {where}. First come, first served.";
        }

        public string Leave(int teamId, string reason = null)
        {
            var t = Teams[teamId];
            if (t.Left) return "already left";
            var (total, where, at) = Dismantle(t);
            t.Left = true;
            scoreCache = null;
            ForgetTeam(teamId);
            MapVersion++;
            string msg = $"{t.PlayerName ?? t.Name} ({t.Name}) {reason ?? "left the arena"}. Their base at {where} is now about {(int)total} units of salvage ore. First come, first served.";
            Emit("left", teamId, pos: t.StartPos, text: msg);
            Emit("chat", -1, text: msg);
            foreach (var other in Teams.Where(o => !o.Left && !o.Defeated))
                Alerts.Raise(this, other.Id, "salvage_available", Priority.High, at, hit: false).Lost.Add($"{(int)total} units of salvage ore at {where}");
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

        public Entity SpawnStructure(int team, string key, Int2 origin, float progress, float grantedShare = 0f)
        {
            var def = Def(key);
            var e = NewEntity(team, def);
            e.GrantedShare = grantedShare;
            e.Origin = origin;
            e.BuildProgress = progress;
            e.Pos = e.PrevPos = e.Center;
            e.Hp = def.MaxHp * (progress >= 1 ? 1f : 0.1f);
            if (def.DropOff) e.BaySide = ChooseBaySide(origin, def, NearestOreField(e.Center)); // its trucks back in from the ore's side, for good
            else if (HasDoorSide(def)) e.BaySide = ChooseDoorSide(origin, def); // its units come out on the side facing the middle of the map
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
            var r = team >= 0 && team < Teams.Count ? Teams[team].Scatter : rng; // (each team draws from its own: see Team.Scatter)
            // The doorway: south, where the models' doors are, except a drop-off's (its bay), a barracks' and a factory's (the side it was
            // built to face): their models are turned to match.
            var door = at.IsStructure ? BaySides[DoorSide(at)] : BaySides[0];
            // A little scatter, measured along the door and across it, so two bases that are half-turns of each other scatter alike.
            float along = (float)(r.NextDouble() - 0.5) * 0.6f, abeam = (float)(r.NextDouble() - 0.5) * 0.6f;
            var jitter = door * along + new Vec2(-door.Y, door.X) * abeam;
            if (def.IsAir) e.Pos = e.PrevPos = at.Center + jitter;
            else
            {
                // Exit just outside the producer's footprint, in the middle of its door side.
                var exit = Int2.Of(at.Center + door * ((MathF.Abs(door.X) > 0.5f ? at.Def.SizeX : at.Def.SizeY) / 2f + 0.5f));
                var tile = Paths.NearestPassable(exit);
                e.Pos = e.PrevPos = tile.Center + jitter;
            }
            e.Facing = e.TurretFacing = door.Angle;
            e.GuardPos = e.Pos;
            e.FuelCap = DroneFuel(def);
            e.Fuel = e.FuelMax;
            if (e.IsHarvester) SetOrder(e, Order.Harvest, e.Pos);
            else if (at.Rally.HasValue) SetOrder(e, Order.Move, at.Rally.Value);
            else if (!def.IsAir)
            {
                // Roll a few tiles clear of the door so new units don't park in front of it (or a refinery's dock).
                var across = new Vec2(-door.Y, door.X);
                var clear = Paths.NearestPassable(Int2.Of(e.Pos + across * ((float)(r.NextDouble() - 0.5) * 4f) + door * 3f)).Center;
                SetOrder(e, Order.Move, clear);
                e.GuardPos = clear;
            }
            return e;
        }

        SimRng rng = new SimRng(42); // serializable, so a resumed game draws the same numbers (for a unit that belongs to no team)

        void OnStructureComplete(Entity e)
        {
            StartReactor(e);
            UpdatePower();
            if (e.Def.Key == "mining_refinery") SpawnUnit(e.Team, "mining_truck", e).GrantedShare = e.GrantedShare; // its free truck is as much a gift as the refinery was
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
            }
            Int2 origin;
            if (deposit != null)
            {
                // Anywhere the mine covers or touches the deposit will do: rock, ore or a truck lane on its exact tile
                // shouldn't make a whole zone undrillable.
                var spot = DeepMineSpot(deposit, u.Team, out var blocked);
                if (spot == null) return $"can't deploy here: {blocked}";
                origin = spot.Value;
            }
            else
            {
                origin = new Int2(t.X - (def.SizeX - 1) / 2, t.Y - (def.SizeY - 1) / 2);
                var why = CanPlace(u.Team, key, origin.X, origin.Y, requireNear: false);
                if (why != null)
                {
                    // Like build: name the nearest clear spot, so the next move-then-deploy lands first time.
                    Int2? alt = null; float ad = float.MaxValue;
                    for (int dy = -6; dy <= 6; dy++)
                        for (int dx = -6; dx <= 6; dx++)
                        {
                            var o = new Int2(origin.X + dx, origin.Y + dy);
                            if (dx * dx + dy * dy >= ad || CanPlace(u.Team, key, o.X, o.Y, requireNear: false) != null) continue;
                            ad = dx * dx + dy * dy; alt = o;
                        }
                    // Deploying builds with the truck's own tile at the footprint's (SizeX-1)/2 offset: name that tile's centre.
                    return $"can't deploy here: {why}" + (alt.HasValue ? $". Nearest clear spot: move it to {alt.Value.X + (def.SizeX - 1) / 2}.5,{alt.Value.Y + (def.SizeY - 1) / 2}.5 and deploy there" : "");
                }
            }
            Remove(u);
            var s = SpawnStructure(u.Team, key, origin, 1f, u.GrantedShare); // a structure is worth what the truck that deployed into it was
            if (deposit != null) { s.DepositId = deposit.Id; deposit.MineId = s.Id; }
            Teams[u.Team].Stats.Count(key);
            Emit("built", u.Team, s.Id, pos: s.Center, key: key);
            return null;
        }

        /// <summary>
        /// Take an entity out of the world. `how` is only for a command center: what happened to a team's last one,
        /// worded for the alert and chat ("fell" unless the caller says otherwise).
        /// </summary>
        public void Remove(Entity e, string how = "fell")
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
                if (e.Team >= 0) Teams[e.Team].StructureQueue.RemoveAll(p => p.StructureId == e.Id);
                UpdatePower();
                if (e.Team >= 0 && e.Def.Key == "command_center") ReleaseStockpile(e, e.Team, how);
                if (e.Def.Key == "derrick") DerrickDestroyed(e); // however it goes, the site comes back neutral
            }
        }

        // ------------------------------------------------------------------ events

        public GameEvent Emit(string type, int team = -1, int a = 0, int b = 0, Vec2 pos = default, Vec2 pos2 = default, string key = null, string text = null)
        {
            var ev = new GameEvent { Seq = nextSeq++, Tick = Tick, Type = type, Team = team, A = a, B = b, Pos = pos, Pos2 = pos2, Key = key, Text = text };
            // Which teams saw it go: only they may be told it was destroyed (the observation feed never reports deaths in the fog).
            // A dismantled unit (its team left or was knocked out) goes the same way: the feed forgets it where it was seen go.
            if ((type == "destroyed" || type == "salvaged") && Feed != null && ById.TryGetValue(a, out var gone))
                for (int t = 0; t < Teams.Count && t < 31; t++)
                    if (t != gone.Team && !gone.IsCarried && IsVisibleTo(t, gone)) ev.SeenBy |= 1 << t;
            Events.Add(ev);
            Feed?.OnEmit(ev);
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
                return $"a {def.Key} needs a clear 2-tile truck lane on one side (the side facing your ore is preferred) for trucks to back into";
            if (!requireNear) return null;
            // Must be near an existing friendly structure: territory grows outward from your bases.
            const int reach = 6;
            bool near = Entities.Any(s => !s.Dead && s.Team == team && s.IsStructure &&
                ox <= s.Origin.X + s.Def.SizeX - 1 + reach && ox + def.SizeX - 1 >= s.Origin.X - reach &&
                oy <= s.Origin.Y + s.Def.SizeY - 1 + reach && oy + def.SizeY - 1 >= s.Origin.Y - reach);
            if (!near) return $"must be within {reach} tiles of one of your structures";
            return null;
        }

        /// <summary>
        /// Finds a valid spot near the team's base, leaving a 1-tile walkway around it. The same for every seat: the base is
        /// measured from the middle of its start tile (where its command center stands), and spots that score alike are told
        /// apart by what mirrors between seats (nearer the map centre, then on the right as the base faces it), never by scan
        /// order, which would favour whoever sits at the low end of the map. A drop-off placed without a spot in mind sits
        /// toward the nearest ore field, so its trucks' trips are short.
        /// </summary>
        public Int2? FindPlacement(int team, string key, Vec2? near = null, bool requireNear = true)
        {
            var def = Defs.Get(key);
            if (def == null || !def.IsStructure) return null;
            var anchor = near ?? Int2.Of(Teams[team].StartPos).Center;
            var centre = new Vec2(Map.W / 2f, Map.H / 2f);
            var field = def.DropOff && near == null ? NearestOreField(anchor) : new List<Vec2>();
            var right = RightOf(anchor);
            Int2? best = null; float bestScore = float.MaxValue, bestCentre = 0, bestRight = 0;
            for (int r = 2; r <= 14; r++)
            {
                // Each ring of spots (middle within r tiles of the anchor on both axes) is looked at once, so both seats see the same window.
                for (int y = (int)MathF.Floor(anchor.Y - r - def.SizeY / 2f); y <= (int)MathF.Ceiling(anchor.Y + r - def.SizeY / 2f); y++)
                    for (int x = (int)MathF.Floor(anchor.X - r - def.SizeX / 2f); x <= (int)MathF.Ceiling(anchor.X + r - def.SizeX / 2f); x++)
                    {
                        var c = new Vec2(x + def.SizeX / 2f, y + def.SizeY / 2f);
                        float reach = MathF.Max(MathF.Abs(c.X - anchor.X), MathF.Abs(c.Y - anchor.Y));
                        if (reach > r || (r > 2 && reach <= r - 1)) continue;
                        if (CanPlace(team, key, x, y, padding: 1, requireNear: requireNear) != null) continue;
                        // Turrets lean toward the map centre; everything else stays compact. Trucks drive to the head of a drop-off's lane
                        // (which opens toward the ore) and back, so each tile between that and the ore counts twice.
                        float score = Vec2.Dist(c, anchor) + (key == "gun_turret" ? Vec2.Dist(c, centre) * 0.6f : 0);
                        if (field.Count > 0) { var at = new Int2(x, y); score += 2f * DistToField(field, Bay(at, def, ChooseBaySide(at, def, field)).head); }
                        int cmp = TieCmp(score, bestScore);
                        float toCentre = 0, toRight = 0;
                        if (cmp <= 0)
                        {
                            toCentre = Vec2.Dist(c, centre);
                            var d = c - anchor;
                            toRight = d.X * right.X + d.Y * right.Y;
                            if (cmp == 0) cmp = TieCmp(toCentre, bestCentre);
                            if (cmp == 0) cmp = TieCmp(bestRight, toRight); // the further right wins
                        }
                        if (cmp < 0) { bestScore = score; bestCentre = toCentre; bestRight = toRight; best = new Int2(x, y); }
                    }
                // A drop-off's score is at least its distance from the base, so once the best is under the next ring's distance
                // nothing further out can beat it; before that, a spot beside the ore a few rings out may still be far better.
                if (best.HasValue && (field.Count > 0 ? bestScore + Tie <= r : r > 4)) break;
            }
            return best;
        }

        const float Tie = 1e-4f; // closer than this is a tie (no two different spots are this close, and rounding is far smaller)
        static int TieCmp(float a, float b) => a < b - Tie ? -1 : a > b + Tie ? 1 : 0;

        static float DistToField(List<Vec2> field, Vec2 p)
        {
            float d = float.MaxValue;
            foreach (var f in field) d = MathF.Min(d, Vec2.Dist(p, f));
            return d;
        }

        /// <summary>Which way is "right" for something at a point facing the map centre (one on the centre faces east): the same turn for every seat.</summary>
        internal Vec2 RightOf(Vec2 from)
        {
            var facing = (new Vec2(Map.W / 2f, Map.H / 2f) - from).Normalized;
            if (facing.LengthSq < 0.5f) facing = new Vec2(1, 0);
            return new Vec2(facing.Y, -facing.X);
        }

        /// <summary>
        /// The middle of every tile of the ore field a base would haul from, as far out as the near half of the field: the
        /// nearest iron (the bulk of what trucks carry) unless some other ore is much nearer. Looks 30 tiles out; empty
        /// when there is no field that near.
        /// </summary>
        List<Vec2> NearestOreField(Vec2 from)
        {
            const int look = 30; const float spread = 6f, ironSlack = 8f;
            int x0 = Math.Max(0, (int)from.X - look), x1 = Math.Min(Map.W - 1, (int)from.X + look);
            int y0 = Math.Max(0, (int)from.Y - look), y1 = Math.Min(Map.H - 1, (int)from.Y + look);
            float nearest = float.MaxValue, nearestIron = float.MaxValue; byte kind = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = Map.Idx(x, y);
                    if (Map.OreBase[i] <= 0) continue; // the map's fields: not salvage piles, and still there once mined out
                    float d = Vec2.Dist(new Vec2(x + 0.5f, y + 0.5f), from);
                    if (d < nearest) { nearest = d; kind = Map.OreBaseType[i]; }
                    if (Map.OreBaseType[i] == Map.Iron && d < nearestIron) nearestIron = d;
                }
            var tiles = new List<Vec2>();
            if (nearest == float.MaxValue) return tiles;
            if (nearestIron <= nearest + ironSlack) { kind = Map.Iron; nearest = nearestIron; }
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = Map.Idx(x, y);
                    var p = new Vec2(x + 0.5f, y + 0.5f);
                    if (Map.OreBase[i] > 0 && Map.OreBaseType[i] == kind && Vec2.Dist(p, from) <= nearest + spread) tiles.Add(p);
                }
            return tiles;
        }

        // ------------------------------------------------------------------ orders

        public void SetOrder(Entity e, Order o, Vec2 pos, int target = 0)
        {
            Touched?.Add(e);
            // The dance guard's two-second averages describe the order in progress: given an order, a unit that had stopped starts them afresh.
            // (One that arrives and is sent straight back the way it came has the old direction in its average velocity for a second or so,
            // which reads as "getting nowhere" and, with a short jam in a doorway, settled a soldier walking out of its base.)
            if (e.Order == Order.Idle && o != Order.Idle) { e.DanceVel = default; e.DanceSpeed = 0f; }
            e.Order = o;
            e.OrderPos = pos;
            e.TargetId = target;
            if (o != Order.Refuel) { e.UntilCapturable = false; e.EngageDerricks = false; } // a refuel trip picks the order back up as it was (FinishRefuel)
            e.Path = null;
            e.RepathTimer = 0;
            e.Dock = DockStep.None; e.DockAt = 0;
            if (o == Order.Idle) { e.GuardPos = e.Pos; e.Moving = false; } // stopped where it is (an idle truck left Moving parked in soldiers' way)
            e.Responding = false; // any new order (from a commander or the response itself) replaces the old one
            e.SpeedCap = 0; e.Group = 0;
            e.Waypoints.Clear(); e.WaypointLoop = false;
            e.Retreating = false;
            e.GivingWay = false;
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
            Guard("groups", MeasureGroups);
            if (Tick % (TickRate * 10) == 0) Guard("home ore", CheckHomeOre);
            double tu = Profile ? profileClock.Elapsed.TotalSeconds : 0;
            // Units act one after another, and whoever acts later sees the other's move: it fires first as two armies close, and a
            // hit kills before the dead unit's turn. With a fixed order (the first seat's units have the lower ids) that favoured one
            // seat (a mirrored fight won about two in three by the later one), so the order runs backwards every other tick.
            bool backwards = (Tick & 1) == 1;
            for (int k = 0, n = Entities.Count; k < (backwards ? n : Entities.Count); k++)
            {
                var e = Entities[backwards ? n - 1 - k : k];
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
                        if (!e.Dead && e.IsArmed && e.IsComplete && !e.Offline) UpdateTurret(e);
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
            Guard("dodge", DodgeVehicles);
            Guard("separation", Separate);
            Guard("run over", RunOver);
            Guard("dance guard", WatchDances);
            Guard("cleanup", Cleanup);
            if (Tick % 4 == 0) Guard("visibility", UpdateVisibility);
            if (!Mirror) Guard("victory", CheckVictory);
            if (!Mirror) Guard("match", MatchClock);
            Guard("derricks", DerrickUpkeepTick);
            Guard("upkeep", Upkeep);
            Guard("regrowth", Regrow);
            TickOreVersion();
            if (Showcase != null) Guard("showcase", Showcase.Hold); // the kitchen sink room only (KitchenSink.cs)
            if (!Mirror) Guard("observe", Observe.Tick);
        }

        /// <summary>Each team's observation feed (Observations.cs): what its commander could notice, logged as it happens.</summary>
        public ObservationFeed Feed;
        public ObservationFeed Observe => Feed ??= new ObservationFeed(this);
        /// <summary>While a tracked command runs: every unit whose order it set (the feed stamps them with the command's id).</summary>
        public List<Entity> Touched;

        void UpdatePower()
        {
            var wasLow = Teams.Select(t => t.LowPower).ToList();
            foreach (var t in Teams) { t.PowerProduced = 0; t.PowerUsed = 0; }
            foreach (var e in Entities)
            {
                if (e.Dead || !e.IsStructure || !e.IsComplete || e.Team < 0) continue;
                var t = Teams[e.Team];
                if (e.Offline) continue; // an offline defence (upkeep unpaid) draws no power
                if (e.Def.Key == "fusion_reactor") { t.PowerProduced += ReactorPower(e, e.Output); continue; } // as much as its plasma pays for
                if (e.Def.Power > 0) t.PowerProduced += e.Def.Power; else t.PowerUsed -= e.Def.Power;
            }
            // Say when a team tips into low power (and out of it): otherwise half-speed production goes unnoticed.
            for (int i = 0; i < Teams.Count; i++)
            {
                var t = Teams[i];
                if (t.Defeated || t.LowPower == wasLow[i]) continue;
                var at = t.StartPos;
                if (t.LowPower)
                {
                    Alerts.Raise(this, t.Id, "low_power", Priority.Medium, at, hit: false)
                          .Lost.Add($"power {t.PowerProduced} produced, {t.PowerUsed} used: building, training and refining run at half speed until you build a power_plant or sell something that draws power" + ReactorShortNote(t));
                    Emit("low_power", t.Id, 0, 0, at, text: $"low power ({t.PowerProduced} produced, {t.PowerUsed} used): everything runs at half speed until you add a power_plant");
                }
                else Emit("power_restored", t.Id, 0, 0, at, text: $"power restored ({t.PowerProduced} produced, {t.PowerUsed} used): full speed again");
            }
        }

        /// <summary>Seconds the reported per-second rates are averaged over.</summary>
        public const float RateWindow = 15f;
        readonly Dictionary<int, Dictionary<string, float>> rateSnapshot = new Dictionary<int, Dictionary<string, float>>();

        /// <summary>Converter buildings turn inputs into outputs; fusion reactors burn plasma.</summary>
        void UpdateEconomy()
        {
            bool powerChanged = false;
            foreach (var e in Entities)
            {
                if (e.Dead || !e.IsStructure || !e.IsComplete || e.Team < 0) continue; // neutral derricks pay nobody
                var team = Teams[e.Team];
                if (e.Def.Key == "derrick") { DerrickTick(e, team); continue; }
                if (e.Def.Key == "fusion_reactor") { if (FusionTick(e, team)) powerChanged = true; continue; }
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
                    // The gift stays a gift when it's refined: what the inputs took from it, the outputs are made of.
                    float giftShare = team.Pay(r.Inputs, cycles * frac);
                    foreach (var kv in r.Outputs) team.Gain(kv.Key, kv.Value * cycles * frac, giftShare);
                    any = true;
                }
                e.Working = any;
            }
            if (Tick % TickRate == 0) SettleFusionBurn();
            if (powerChanged) UpdatePower();

            // Per-second net rates, from a stock snapshot each second, averaged over about 15 s: a truck unloading 150 ore
            // in one second isn't a rate of +150/s.
            if (Tick % TickRate == 0)
                foreach (var t in Teams)
                {
                    if (rateSnapshot.TryGetValue(t.Id, out var prev))
                        foreach (var item in Defs.Items)
                        {
                            float step = (t.Stock.TryGetValue(item, out var now) ? now : 0) - (prev.TryGetValue(item, out var p) ? p : 0);
                            float was = t.Rates.TryGetValue(item, out var r0) ? r0 : step;
                            float avg = was + (step - was) / RateWindow;
                            t.Rates[item] = MathF.Abs(avg) < 0.005f ? 0f : avg;
                        }
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

                // Units: each completed building of a kind works one item of the team's queue for that kind, all at once.
                foreach (var kv in team.UnitQueues)
                {
                    var q = kv.Value;
                    if (q.Count == 0) continue;
                    AssignWorkers(team.Id, kv.Key, q);
                    for (int i = 0; i < q.Count; i++)
                    {
                        var item = q[i];
                        var producer = item.WorkerId != 0 ? Get(item.WorkerId) : null;
                        if (producer == null) continue; // waiting for a free building
                        var def = Def(item.Key);
                        item.Progress += Dt * rate;
                        if (item.Progress >= def.BuildTime)
                        {
                            q.RemoveAt(i--);
                            var u = SpawnUnit(team.Id, item.Key, producer);
                            u.GrantedShare = item.GrantedShare; // as much of it as the gift paid for is worth nothing as a kill
                            team.Stats.UnitsBuilt++;
                            team.Stats.Count(item.Key);
                            if (u.Def.OwnerTeam >= 0 && Inventions.TryGetValue(item.Key, out var inv)) inv.Built++;
                            u.CmdId = item.CmdId; // the observation feed credits its train command, then clears it
                            Emit("trained", team.Id, u.Id, pos: u.Pos, key: u.Def.Key);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gives each completed building of a kind (barracks, factory...) one item of the team's queue for it. Items already at
        /// work stay with their building. Of the rest, an item queued for a chosen building (StructureId) takes it as soon as
        /// it is free and otherwise waits for it while it stands, so it never holds up an idle building; then the other items
        /// take whatever is left, in queue order. An item with no building to go to waits and keeps the progress it has.
        /// </summary>
        void AssignWorkers(int team, Producer kind, List<ProdItem> q)
        {
            var all = new List<Entity>();
            foreach (var e in Entities)
                if (!e.Dead && e.Team == team && e.IsStructure && e.IsComplete && e.Def.Produces == kind) all.Add(e);
            var free = new List<Entity>(all);
            foreach (var p in q)
            {
                int at = p.WorkerId != 0 ? free.FindIndex(e => e.Id == p.WorkerId) : -1;
                if (at >= 0) free.RemoveAt(at);
                else p.WorkerId = 0; // its building is gone
            }
            if (free.Count == 0) return;
            foreach (var p in q)
            {
                if (p.WorkerId != 0 || p.StructureId == 0) continue;
                int at = free.FindIndex(e => e.Id == p.StructureId);
                if (at >= 0) { p.WorkerId = p.StructureId; free.RemoveAt(at); }
            }
            foreach (var p in q)
            {
                if (free.Count == 0) break;
                if (p.WorkerId != 0 || (p.StructureId != 0 && all.Exists(e => e.Id == p.StructureId))) continue; // at work, or waiting for its own building
                p.WorkerId = free[0].Id;
                free.RemoveAt(0);
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
                    if (FollowPath(e, e.OrderPos, 0.3f) && !NextWaypoint(e))
                    {
                        bool back = e.Retreating; SetOrder(e, Order.Idle, e.Pos); e.Retreating = back;
                        DerrickHint(e);
                        // A loaded truck brought home unloads instead of sitting on its cargo (sent elsewhere, it waits there).
                        if (e.IsHarvester && e.Cargo > 0 && Entities.Any(s => !s.Dead && s.Team == e.Team && s.IsStructure && s.IsComplete && s.Def.DropOff && s.DistFrom(e.Pos) <= 12f))
                            e.Order = Order.ReturnOre;
                    }
                    if (e.IsArmed && (e.Def.Armor == Armor.Vehicle || e.IsAir)) OpportunisticFire(e);
                    break;
                case Order.AttackMove:
                    {
                        // Unarmed units (medics, repair trucks...) swept up in an attack-move just travel.
                        if (!e.IsArmed) { if (FollowPath(e, e.OrderPos, 0.5f) && !NextWaypoint(e)) FinishOrder(e); break; }
                        var t = AcquireTarget(e, e.Def.Sight);
                        if (t != null) { Engage(e, t); break; }
                        if (FollowPath(e, e.OrderPos, 0.5f) && !NextWaypoint(e)) { FinishOrder(e); DerrickHint(e); }
                        break;
                    }
                case Order.Attack:
                    {
                        var t = Get(e.TargetId);
                        if (t == null || t.Team == e.Team || !e.Def.Weapon.CanHit(t.Def)) { FinishOrder(e); break; }
                        // A derrick is a landmark: march on it unseen, and open fire once it's in sight. So is a building to be
                        // captured (it doesn't move): an attack_move there could shoot it down past capturable.
                        if ((t.Def.Key == "derrick" || e.UntilCapturable) && !IsVisibleTo(e.Team, t)) { e.OrderPos = t.Center; Chase(e, t); break; }
                        if (!IsVisibleTo(e.Team, t))
                        {
                            // Lost sight of it: push to where it was last seen and fight whatever is there.
                            bool resp = e.Responding; var home = e.HomePos;
                            SetOrder(e, Order.AttackMove, e.OrderPos);
                            e.Responding = resp; e.HomePos = home;
                            break;
                        }
                        e.OrderPos = t.Center; // last known position
                        // "until":"capturable": hold fire while an engineer could take it (and open up again if it's repaired).
                        if (e.UntilCapturable && Capturable(t)) { e.Moving = false; e.Path = null; break; }
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
            // A patrol on the spot (orders from before the move command refused them) settles where it is.
            if (e.WaypointLoop && TightLoop(e.OrderPos, e.Waypoints)) { e.Waypoints.Clear(); e.WaypointLoop = false; return false; }
            var next = e.Waypoints[0];
            var rest = e.Waypoints.Skip(1).ToList();
            bool loop = e.WaypointLoop, derricks = e.EngageDerricks; float cap = e.SpeedCap; int group = e.Group;
            if (loop) rest.Add(e.OrderPos);
            SetOrder(e, e.Order, next);
            e.Waypoints.AddRange(rest); e.WaypointLoop = loop; e.SpeedCap = cap; e.Group = group; e.EngageDerricks = derricks;
            return true;
        }

        /// <summary>A patrol whose points all lie within this many tiles of each other is pacing on the spot, not patrolling.</summary>
        public const float MinPatrolSpan = 4f;
        public static bool TightLoop(Vec2 at, List<Vec2> waypoints) =>
            waypoints.All(p => Vec2.Dist(p, at) < MinPatrolSpan && waypoints.All(q => Vec2.Dist(p, q) < MinPatrolSpan));

        /// <summary>How far apart (in tiles of route left) a group moving together lets its members get.</summary>
        public const float GroupSlack = 3f;
        /// <summary>A member this far behind the group's front is left to catch up rather than held for.</summary>
        const float GroupGiveUp = 80f;
        readonly Dictionary<int, (float front, float rear)> groupSpan = new Dictionary<int, (float, float)>();

        /// <summary>Tiles of route a moving unit has left: to its current goal, then through its waypoints. (Formation
        /// slots differ by a tile or two, inside GroupSlack.)</summary>
        static float RouteLeft(Entity e)
        {
            float d = Vec2.Dist(e.Pos, e.OrderPos);
            if (e.WaypointLoop) return d;
            var at = e.OrderPos;
            foreach (var p in e.Waypoints) { d += Vec2.Dist(at, p); at = p; }
            return d;
        }

        static bool GroupMoving(Entity e) => !e.Dead && e.Group != 0 && !e.IsCarried && !e.Stranded && (e.Order == Order.Move || e.Order == Order.AttackMove);
        // The rear the group waits for: members still on the move (one held up in a fight or jammed isn't waited on).
        static bool GroupPacing(Entity e) => GroupMoving(e) && (e.Moving || e.Order == Order.Move);

        /// <summary>Each group's front (least route left) and rear (most, among members not hopelessly behind).</summary>
        void MeasureGroups()
        {
            groupSpan.Clear();
            var front = new Dictionary<int, float>();
            foreach (var e in Entities)
                if (GroupMoving(e)) { float r = RouteLeft(e); front[e.Group] = front.TryGetValue(e.Group, out var f) ? MathF.Min(f, r) : r; }
            foreach (var e in Entities)
            {
                if (!GroupMoving(e)) continue;
                float r = RouteLeft(e), f = front[e.Group];
                if (!GroupPacing(e)) r = f;
                if (r - f > GroupGiveUp) r = f;
                groupSpan[e.Group] = groupSpan.TryGetValue(e.Group, out var s) ? (f, MathF.Max(s.rear, r)) : (f, r);
            }
        }

        /// <summary>Units told to pull back below an HP threshold head for the nearest base building on their own, once per
        /// wound: repaired back above the line, they'll do it again. Aircraft pull back to a pad and land (a base that
        /// can't refuel them is just somewhere to hover until the tank runs dry).</summary>
        void CheckRetreat(Entity e)
        {
            if (e.RetreatBelow <= 0) return;
            if (e.Hp >= e.Def.MaxHp * e.RetreatBelow) { e.RetreatSpent = false; return; }
            if (e.RetreatSpent || e.Retreating || (Tick + e.Id) % 5 != 0) return;
            Entity home = null; float bd = float.MaxValue;
            if (e.IsAir) home = NearestFuelPoint(e);
            if (home == null)
                foreach (var s in Owned(e.Team))
                {
                    if (!s.IsStructure || !s.IsComplete) continue;
                    // Pull back to a real base (production, drop-off or depot), not a lone turret, derrick or deep mine out in the field.
                    bool baseLike = s.Def.FuelDepot || s.Def.DropOff || s.Def.Produces != Producer.None;
                    float d = Vec2.DistSq(s.Center, e.Pos) * (baseLike ? 1f : 9f);
                    if (d < bd) { bd = d; home = s; }
                }
            e.RetreatSpent = true;
            if (home == null || home.DistFrom(e.Pos) < 4f) return;
            if (e.IsAir && IsFuelPoint(e, home))
            {
                BeginRefuel(e, home);
                e.ResumeOrder = Order.Idle; // and stays parked on the pad
            }
            else SetOrder(e, Order.Move, e.IsAir ? home.Center : DockPoint(home));
            e.Retreating = true;
            Emit("retreating", e.Team, e.Id, home.Id, e.Pos, key: e.Def.Key,
                 text: $"{e.Def.Key} #{e.Id} fell below {StateView.Pct(e.RetreatBelow)}% HP ({(int)e.Hp}/{e.Def.MaxHp}) and is pulling back to {home.Def.Key} #{home.Id}" +
                       (e.IsAir && IsFuelPoint(e, home) ? " to land" : "") + ". It won't pull back again until it's repaired above that, so an order you give it now sticks.");
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
            if (d <= FireBand(e)) { e.Moving = false; e.Path = null; FireAt(e, t); }
            else if (Vec2.Dist(e.Pos, e.GuardPos) < 6f) Chase(e, t); // path around obstacles, not straight into them
            else e.Moving = false; // at the end of its leash: holds there (it used to stay "moving" on the spot)
        }

        /// <summary>
        /// How close a unit gets before it stops to fire, with a band: one closing in stops at 0.9x its range, one already
        /// stopped keeps firing out to its full range. With a single threshold, a shove or a step of the target across it
        /// flipped the unit between chasing and firing every tick (the attack-move firing line).
        /// </summary>
        static float FireBand(Entity e) => e.Def.Weapon.Range * (e.Moving ? 0.9f : 1f);

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
                if (u.Dead || u.Team != victim.Team || u.IsStructure || !u.IsArmed || u.IsCarried || u.Order != Order.Idle || IsProtected(u.Team)) continue; // protected units don't retaliate
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
            if (RunsDown(e, t, d))
            {
                // Close enough to drive over: run it down, shooting on the way if the gun bears.
                if (d <= e.Def.Weapon.Range) FireAt(e, t);
                Chase(e, t);
                return;
            }
            if (d <= FireBand(e))
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
            // A derrick destroyed and rebuilt on its site is a new entity: an engineer bound for the site takes the new one.
            if ((t == null || t.Dead) && Derricks.FirstOrDefault(d => Vec2.Dist(d.Center, e.OrderPos) < 1.5f) is Entity again) { e.TargetId = again.Id; t = again; }
            if (t == null || t.Dead)
            {
                bool pending = DerrickRespawns.Any(r => Vec2.Dist(new Vec2(r.origin.X + 1, r.origin.Y + 1), e.OrderPos) < 1.5f);
                if (pending) { if (Vec2.Dist(e.Pos, e.OrderPos) > 3f) FollowPath(e, e.OrderPos, 2.5f); else e.Moving = false; return; } // wait at the site
                CaptureStopped(e, "its target is gone");
                return;
            }
            if (!t.IsStructure || t.Team == e.Team) { CaptureStopped(e, t.Team == e.Team ? $"{t.Def.Key} #{t.Id} is already yours" : "that can't be captured"); return; }
            // An engineer can set out for a derrick it can't see; what it finds is judged once it's in sight.
            if (t.Def.Key == "derrick" && !IsVisibleTo(e.Team, t)) { Chase(e, t); return; }
            if (!t.IsComplete || !Capturable(t) || (t.Team >= 0 && (IsProtected(e.Team) || Shielded(t))))
            {
                CaptureStopped(e, t.Team < 0 ? $"the neutral {t.Def.Key} #{t.Id} isn't finished"
                    : !Capturable(t) ? $"{Teams[t.Team].Name} holds {t.Def.Key} #{t.Id} at {(int)(100 * t.Hp / t.Def.MaxHp)}% health: damage it below 50% first"
                    : IsProtected(e.Team) ? "you can't take other players' buildings during newcomer protection"
                    : $"{Teams[t.Team].Name} is under newcomer protection");
                return;
            }
            if (t.DistFrom(e.Pos) > 0.6f) { Chase(e, t); return; }
            int old = t.Team;
            t.Team = e.Team;
            t.Rally = null;
            // A capture is a capture: the engineer puts any fire out and patches it up to 50%, and whoever hurt it before
            // has no claim on it (a fire burning on would hand the kill and salvage to a third team).
            t.Burning = false; t.LastAttackerTeam = -1; t.LastAttackerId = 0;
            t.Hp = MathF.Max(t.Hp, t.Def.MaxHp * CaptureThreshold);
            Teams[e.Team].KnownEnemyStructures.Remove(t.Id);
            if (old >= 0) Teams[old].Stats.StructuresLost++;
            Teams[e.Team].Stats.Count(t.Def.Key);
            Emit("captured", e.Team, t.Id, e.Id, t.Center, key: t.Def.Key,
                 text: $"{Teams[e.Team].Name}'s engineer captured {(old >= 0 ? $"{Teams[old].Name}'s" : "a neutral")} {t.Def.Key} #{t.Id} at {(int)t.Center.X},{(int)t.Center.Y}");
            if (old >= 0) Alerts.Raise(this, old, "structure_lost", Priority.Critical, t.Center, attacker: e).Lost.Add($"{t.Def.Key} #{t.Id} (captured by an engineer)");
            if (t.Def.Key == "derrick") DerrickCaptured(t, old);
            StartReactor(t);
            if (old >= 0 && t.Def.Key == "command_center") ReleaseStockpile(t, old, $"was captured by {Teams[e.Team].Name}");
            Remove(e); // the engineer moves in for good
            UpdatePower();
        }

        /// <summary>An engineer giving up on a capture says why, so it isn't left idle without explanation.</summary>
        void CaptureStopped(Entity e, string why)
        {
            SetOrder(e, Order.Idle, e.Pos);
            Emit("capture_stopped", e.Team, e.Id, 0, e.Pos, key: e.Def.Key, text: $"engineer #{e.Id} stopped and is waiting at {(int)e.Pos.X},{(int)e.Pos.Y}: {why}");
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
            if (IsProtected(m.Team)) return; // a protected team's mines are inert until protection ends
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
            if (e.Def.Armor == Armor.Infantry) e.Facing = RotateToward(e.Facing, (t.Center - e.Pos).Angle, TurnRate(e) * Dt); // a medic turns to its patient, as infantry turn to fire
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
                if (o.Dead || o.Team == e.Team || o.IsCarried || o.Team < 0) continue; // neutral derricks aren't enemies
                // Nor are held ones a threat: worth capturing, so only shot when ordered (attack, or attack_move with "derricks":true).
                if (o.Def.Key == "derrick" && !(e.Order == Order.AttackMove && e.EngageDerricks)) continue;
                if (Shielded(o) || IsProtected(e.Team)) continue; // newcomer protection: no fighting either way
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
            // Newcomer protection: a protected team can't attack, whatever order its units are on. Not in the showcase, whose teams are
            // protected for good (ProtectedUntil = MaxValue, which a restored copy keeps though it has no Showcase) so nothing takes damage.
            if (IsProtected(e.Team) && Showcase == null && Teams[e.Team].ProtectedUntil < float.MaxValue) return;
            var w = e.Def.Weapon;
            if (w.ArmTime > 0f)
            {
                // A charge has to be planted (C4): in range of the same target and standing still, every tick without a
                // break, for the weapon's ArmTime before the first goes off. Walking off, falling out of range or switching
                // target starts it again; staying on it, the next ones follow on the cooldown.
                float gap = Time - e.LastFiredAt;
                bool working = !e.Moving && e.ArmingTarget == t.Id && gap <= Dt * 1.5f;
                if (!working) e.Arming = 0f;
                else if (gap > 0f) e.Arming = MathF.Min(e.Arming + gap, w.ArmTime);
                e.ArmingTarget = t.Id;
            }
            e.LastFiredAt = Time;
            var dir = t.Center - e.Pos;
            float want = dir.Angle;
            e.TurretFacing = RotateToward(e.TurretFacing, want, 6f * Dt);
            if (!e.IsStructure && !e.Moving && e.Def.Armor == Armor.Infantry && Time >= e.DodgeUntil) e.Facing = RotateToward(e.Facing, want, TurnRate(e) * Dt); // dodging: running, not aiming
            if (e.Cooldown > 0) return;
            if (w.ArmTime > 0f && e.Arming < w.ArmTime - Dt * 0.5f) return; // still planting it
            if (MathF.Abs(AngleDiff(e.TurretFacing, want)) > 0.35f) return;
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
            if (e.Team < 0) return;
            float f = e.Hp / e.Def.MaxHp;
            if (f >= BurnBelow || e.Hp <= 0f)
            {
                if (e.Burning) { e.Burning = false; Emit("fire_out", e.Team, e.Id, 0, e.Center, key: e.Def.Key, text: $"the fire at your {e.Def.Key} #{e.Id} is out"); }
                return;
            }
            if (Shielded(e)) return;
            if (!e.Burning)
            {
                e.Burning = true;
                Emit("burning", e.Team, e.Id, 0, e.Center, key: e.Def.Key,
                     text: $"your {e.Def.Key} #{e.Id} is on fire ({(int)(f * 100)}% health): repair it above {(int)(BurnBelow * 100)}% (repair truck) or it burns down within about a minute");
                Alerts.Raise(this, e.Team, "building_burning", Priority.High, e.Center, e, hit: false);
            }
            float rate = e.Def.MaxHp * (0.003f + 0.009f * (1f - f / BurnBelow));
            // Whoever last hurt it set the fire: the kill (and the salvage) is theirs even if that unit has since died.
            // Nobody else's: a fire on a building never hit by an enemy (a capture forgets who hit it before) is no one's kill.
            int setBy = e.LastAttackerTeam >= 0 ? e.LastAttackerTeam : e.LastAttackerId != 0 ? Get(e.LastAttackerId)?.Team ?? -1 : -1;
            Damage(e, rate * Dt, null, setBy != e.Team ? setBy : -1, burn: true);
        }

        void Damage(Entity t, float amount, Entity src, int srcTeam = -1, bool burn = false)
        {
            if (t.Dead) return;
            if (t.Team < 0) return; // neutral (an unclaimed derrick): can't be hurt
            if (Shielded(t)) return; // newcomer protection (not for a derrick it holds)
            int team = src?.Team ?? srcTeam;
            t.Hp -= amount;
            if (src != null) t.LastAttackerId = src.Id;
            if (!burn && team >= 0) t.LastAttackerTeam = team;
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
                if (team >= 0 && team < Teams.Count) { Teams[team].Stats.Kills++; Teams[team].Stats.KillsValued++; }
                if (t.IsStructure) Teams[t.Team].Stats.StructuresLost++; else Teams[t.Team].Stats.UnitsLost++;
                TallyDeath(t, src);
                var lost = Alerts.Raise(this, t.Team, t.IsStructure ? "structure_lost" : "units_lost", t.IsStructure ? Priority.Critical : Priority.Medium, t.Center, attacker: src);
                lost.Lost.Add($"{t.Def.Key} #{t.Id}");
                // Killed by another team (its unit, turret, mine, or a fire it set): part of the wreck is left as salvage.
                bool enemyKill = team >= 0 && team < Teams.Count && team != t.Team;
                float value = 0;
                var salvage = enemyKill ? KillSalvage(t, out value) : null;
                if (enemyKill) Teams[team].Stats.KillValue += (int)value;
                Remove(t); // a team's last command center spills its stockpile as it goes (ReleaseStockpile)
                if (salvage != null) DropSalvage(t, salvage, team);
            }
        }

        /// <summary>Hurt t as if src (or a shot from srcTeam whose shooter is gone) hit it. For tests and the showcase.</summary>
        internal void Hurt(Entity t, float amount, Entity src, int srcTeam = -1) => Damage(t, amount, src, srcTeam);

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
                var barred = BarredBases(e.Team); // while protected: no mining by other teams' structures
                bool tileGood = e.HarvestTile.HasValue && Map.OreAt(e.HarvestTile.Value.X, e.HarvestTile.Value.Y) > 0 &&
                                (want < 0 || Map.OreType[Map.Idx(e.HarvestTile.Value.X, e.HarvestTile.Value.Y)] == want) &&
                                !OreReserved(e.HarvestTile.Value, e.Team) && !NearBarredBase(e.HarvestTile.Value, barred);
                if (!tileGood)
                {
                    var from = e.HarvestTile.HasValue ? e.HarvestTile.Value.Center : e.Pos;
                    // A truck left to choose skips fields by enemy bases it knows of and, while there's something better,
                    // ores nothing of yours refines and that you already have plenty of (crystal, uranium before the labs).
                    var hostile = Teams[e.Team].KnownEnemyStructures.Values.Where(k => k.team >= 0 && k.key != "derrick").Select(k => k.origin.Center).ToList();
                    bool Safe(Int2 t) => !hostile.Any(h => Vec2.DistSq(h, t.Center) < HostileOreRadius * HostileOreRadius);
                    var useful = new bool[Defs.Ores.Length];
                    for (int k = 0; k < useful.Length; k++) useful[k] = want >= 0 || OreUseful(e.Team, k);
                    bool Wanted(Int2 t) => useful[Map.OreType[Map.Idx(t.X, t.Y)]];
                    // On a leash: a truck choosing for itself keeps to its own field (within 12 tiles of where it was mining)
                    // or within LeashDist of one of your drop-offs. It doesn't drive off across the map into who knows what:
                    // sending trucks to a far field is the commander's call (harvest x,y), and so is building a refinery there.
                    var drops = Entities.Where(s => !s.Dead && s.Team == e.Team && s.IsStructure && s.IsComplete && s.Def.DropOff).Select(s => s.Center).ToList();
                    bool hasField = e.HarvestTile.HasValue;
                    bool Leashed(Int2 t) => (hasField && Vec2.DistSq(t.Center, from) <= 144f) || drops.Any(dp => Vec2.DistSq(dp, t.Center) <= LeashDist * LeashDist);
                    // Spread trucks out: avoid tiles another truck is already working.
                    bool Free(Int2 t) => !OreReserved(t, e.Team) && !NearBarredBase(t, barred) && Safe(t) && Leashed(t);
                    bool Unclaimed(Int2 t) => !Entities.Any(o => o != e && !o.Dead && o.IsHarvester && o.HarvestTile.HasValue && o.HarvestTile.Value.Equals(t));
                    // (Equally near tiles: the one nearer its nearest drop-off, whichever corner it works from.)
                    Vec2? home = drops.Count > 0 ? drops.OrderBy(dp => Vec2.DistSq(dp, from)).First() : (Vec2?)null;
                    // Ore within its reach first: it mines a patch from where it stands rather than hopping tile to tile
                    // (drive a tile, stop, mine a few, drive a tile: a slide-pause rhythm on thin ore).
                    var inReach = e.Moving ? null : Map.NearestOre(e.Pos, 3, t => Vec2.Dist(t.Center, e.Pos) <= MineReach && Free(t) && Wanted(t) && Unclaimed(t), want, home);
                    // Picked-over ground, where the best stop collects under ThinStop: a loaded truck on it sets out for its next spot only on
                    // a beat (ThinHopEvery) of the world's clock, so scraping a field is a hop every couple of seconds, not a stop-go beat of
                    // one a second. (The ore regrows no faster for the hurry.) Mid-drive, or empty (just out of the bay), it picks at once.
                    bool offBeat = e.Cargo > 0 && !e.Moving && Tick % ThinHopEvery != 0;
                    // Off the beat, with nothing in reach, it has nothing to decide when no spot around it collects that much: it waits without
                    // the search below, which would only find the same (the count ignores who has claimed what, so it can only overrate a
                    // spot; the check after the search catches the rest).
                    if (inReach == null && offBeat && PickedOver(e.Pos, HopRadius, want)) return;
                    int collects = int.MaxValue; // what the stop RichestNear picks collects
                    var spot = inReach
                               ?? RichestNear(e.Pos, HopRadius, t => Free(t) && Wanted(t) && Unclaimed(t), want, home, out collects) // where a stop collects most
                               ?? Map.NearestOre(from, 40, t => Free(t) && Wanted(t) && Unclaimed(t), want, home)
                               ?? Map.NearestOre(from, 80, t => Free(t) && Wanted(t), want, home)
                               ?? Map.NearestOre(from, 80, Free, want, home);
                    if (collects < ThinStop && offBeat) return;
                    e.HarvestTile = spot;
                    e.Path = null;
                    if (!e.HarvestTile.HasValue)
                    {
                        WarnSurfaceExhausted(e, want);
                        if (e.Cargo > 0) { e.Order = Order.ReturnOre; return; }
                        SetOrder(e, Order.Idle, e.Pos);
                        return;
                    }
                    WarnIfFarAfield(e, e.HarvestTile.Value);
                }
                var tile = e.HarvestTile.Value;
                // It drives onto a tile until within 0.4; parked, it mines anything within MineReach without moving (and isn't
                // made to re-centre by a nudge: trucks on neighbouring tiles used to push each other off and turn round).
                if (Vec2.Dist(e.Pos, tile.Center) > (e.Moving ? 0.4f : MineReach)) { FollowPath(e, tile.Center, 0.3f); return; }
                e.Moving = false;
                e.WorkTimer += Dt;
                if (e.WorkTimer >= 0.1f)
                {
                    e.WorkTimer = 0;
                    int i = Map.Idx(tile.X, tile.Y);
                    int take = Math.Min(Math.Min(4, Map.Ore[i]), cap - e.Cargo);
                    // Salvage lying on the tile (even a field's) goes first, and is counted when it's delivered (TipOre), not now.
                    int salvage = Math.Min(take, Map.SalvageOn(i));
                    Map.Salvage[i] = Map.SalvageOn(i) - salvage;
                    Map.Ore[i] -= take;
                    if (take > 0) { e.Cargo += take; e.CargoSalvage += salvage; e.CargoType = Map.OreType[i]; }
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
                case DockStep.Enter:
                    {
                        // One truck at a time in the bay, the rest in a single-file queue of their own spots beside the lane,
                        // in the order they joined: when the bay frees, only the front one goes, and the rest each move up a
                        // spot. (Trucks used to pick spots by id, two to a spot, and all rush the lane when it freed.)
                        bool queued = e.Dock == DockStep.Queue;
                        int ahead = queued ? QueueAhead(refinery, e) : 0;
                        bool near = Vec2.Dist(e.Pos, head) < QueueJoinDist;
                        // Its turn: already pulling up, or at the front of the queue (or near the lane with no queue) and the
                        // bay isn't spoken for. Only then does it claim the bay (Enter); a truck merely on its way doesn't.
                        if (e.Dock == DockStep.Enter || (!BayClaimed(refinery, e) && (queued ? ahead == 0 : near && QueueAhead(refinery, null) == 0)))
                        {
                            if (e.Dock != DockStep.Enter) e.Path = null;
                            e.Dock = DockStep.Enter;
                            // A unit parked on the head of the lane counts as the truck being there (FollowPath stops it as close
                            // as it can get): it swings in from where it stopped, and Align holds its line, so separation shoves
                            // the parked unit aside. (Waiting for the unit to leave, a truck sat in Enter for good behind a tank
                            // parked on the lane.) Only over open ground: Align and Reverse move in a straight line, so a truck
                            // stopped across rock beside the lane stays in Enter, trying for the head, until the unit moves.
                            if (Vec2.Dist(e.Pos, head) > 0.35f && !(FollowPath(e, head, 0.3f) && Vec2.Dist(e.Pos, head) <= TakenReach(e) && LineClear(e.Pos, head))) return;
                            e.Dock = DockStep.Align;
                            e.Moving = false;
                            e.Path = null;
                            return;
                        }
                        // Still on its way: drive on toward the lane; near it, join the back of the queue.
                        if (!queued && !near) { e.Dock = DockStep.Approach; FollowPath(e, head, 0.35f); return; }
                        if (!queued) { e.Dock = DockStep.Queue; e.QueuedAt = Time; e.Path = null; ahead = QueueAhead(refinery, e); }
                        var spot = QueueSpot(head, outward, across, ahead) ?? e.Pos; // nowhere to line up: wait where it is
                        float off = Vec2.Dist(e.Pos, spot);
                        // A band: once at its spot it stays, unless shoved well off it (it doesn't re-centre on every nudge).
                        if (off > 0.9f || (e.Moving && off > 0.3f)) { FollowPath(e, spot, 0.3f); return; }
                        e.Moving = false; e.Path = null;
                        return;
                    }
                case DockStep.Align:
                    // Turn on the spot to face away from the building (the bin end toward it), settling onto the lane.
                    e.Moving = false;
                    // It only ever steps onto open ground (the check before Align samples the line, and can miss a clipped corner): a
                    // step that wouldn't be sends it back to Enter with a fresh path, to find its way round.
                    var settle = e.Pos + (head - e.Pos) * MathF.Min(1f, 4f * Dt);
                    if (!StepOpen(e.Pos, settle)) { e.Dock = DockStep.Enter; e.Path = null; return; }
                    e.Facing = RotateToward(e.Facing, bayFacing, AlignTurnRate * Dt);
                    e.Pos = settle;
                    // Reverse moves straight to the bay, so the truck must be on the lane first, not just facing the right way
                    // (one that came in beside the head already faced out, and backed in at a slant, across what lay beside the lane).
                    if (MathF.Abs(AngleDiff(e.Facing, bayFacing)) < 0.02f && Vec2.Dist(e.Pos, head) < 0.1f) { e.Facing = bayFacing; e.Dock = DockStep.Reverse; }
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

        /// <summary>How far a parked mining truck's cutter reaches: its own tile and the eight around it.</summary>
        public const float MineReach = 1.5f;
        /// <summary>Ore a stop has to collect (within MineReach of it) to be worth a drive of its own. Under it, the ground is picked over (see ThinHopEvery).</summary>
        public const int ThinStop = 8;
        /// <summary>Ticks between the beats a truck on picked-over ground sets out for its next spot on (two seconds).</summary>
        public const int ThinHopEvery = TickRate * 2;
        /// <summary>How far from where it stands a truck looks for its next spot to mine (RichestNear).</summary>
        public const float HopRadius = 8f;

        /// <summary>The ore of type `want` (any, if negative) a truck parked on tile t reaches: the tile and the eight around it.</summary>
        int OreAround(Int2 t, int want)
        {
            int sum = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (Map.OreAt(t.X + dx, t.Y + dy) > 0 && (want < 0 || Map.OreType[Map.Idx(t.X + dx, t.Y + dy)] == want)) sum += Map.OreAt(t.X + dx, t.Y + dy);
            return sum;
        }

        /// <summary>Is the ground within `radius` of `from` picked over: some ore of type `want` (any, if negative) on it, but no stop that collects ThinStop?</summary>
        bool PickedOver(Vec2 from, float radius, int want)
        {
            bool any = false;
            int r = (int)MathF.Ceiling(radius), cx = (int)from.X, cy = (int)from.Y;
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (Map.OreAt(x, y) <= 0 || (want >= 0 && Map.OreType[Map.Idx(x, y)] != want) || Vec2.Dist(new Int2(x, y).Center, from) > radius) continue;
                    if (OreAround(new Int2(x, y), want) >= ThinStop) return false;
                    any = true;
                }
            return any;
        }

        /// <summary>
        /// The ore tile within `radius` of `from` where a parked truck collects the most: the ore within its reach there
        /// (MineReach, the tile and the eight around it), less a little for the drive. On a thin or picked-over field a
        /// truck used to drive to the nearest tile, take a unit or two, and drive a tile again: a slide-pause rhythm.
        /// `collects` is that ore, at the tile it returns.
        /// </summary>
        internal Int2? RichestNear(Vec2 from, float radius, Func<Int2, bool> ok, int want, Vec2? home, out int collects)
        {
            collects = int.MaxValue;
            Int2? best = null; float bestScore = float.MinValue, bestHome = 0, bestRight = 0;
            var right = home.HasValue ? RightOf(home.Value) : new Vec2(0, 0);
            int r = (int)MathF.Ceiling(radius), cx = (int)from.X, cy = (int)from.Y;
            bool Good(int x, int y) => Map.InBounds(x, y) && Map.OreAt(x, y) > 0 && (want < 0 || Map.OreType[Map.Idx(x, y)] == want) && ok(new Int2(x, y));
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (!Good(x, y)) continue;
                    float d = Vec2.Dist(new Int2(x, y).Center, from);
                    if (d > radius) continue;
                    int sum = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) if (Good(x + dx, y + dy)) sum += Map.OreAt(x + dx, y + dy);
                    float score = sum - 4f * d;
                    // (Equal scores: as NearestOre breaks its ties, the tile nearer home, then the one on the right as home faces the middle of
                    // the map, never the scan order, which favours one seat.)
                    var centre = new Int2(x, y).Center;
                    float toHome = home.HasValue ? Vec2.DistSq(centre, home.Value) : 0f, toRight = (centre.X - from.X) * right.X + (centre.Y - from.Y) * right.Y;
                    int cmp = best == null ? -1 : TieCmp(bestScore, score);
                    if (cmp == 0) cmp = TieCmp(toHome, bestHome);
                    if (cmp == 0) cmp = TieCmp(bestRight, toRight); // the further right wins
                    if (cmp < 0) { bestScore = score; bestHome = toHome; bestRight = toRight; best = new Int2(x, y); collects = sum; }
                }
            return best;
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
            // The salvage in the load is delivered in proportion, and only now is it counted: a truck lost on the way never delivered it.
            int salvage = give >= e.Cargo ? e.CargoSalvage : Math.Min(e.CargoSalvage, e.CargoSalvage * give / e.Cargo);
            e.Cargo -= give;
            e.CargoSalvage -= salvage;
            if (e.CargoType >= 0) Teams[e.Team].Add(Defs.Ores[e.CargoType], give);
            Teams[e.Team].Stats.OreMined += give;
            Teams[e.Team].Stats.SalvageMined += salvage;
        }

        void FinishDelivery(Entity e)
        {
            e.CargoSalvage = 0;
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
                if (s.Id == e.DockAt) cost = cost * 0.75f - 2f; // sticks with the one it has unless another is clearly better
                if (cost < bestCost) { best = s; bestCost = cost; }
            }
            return best;
        }

        /// <summary>Debug: each drop-off's lane and queue (PEZ_QUEUEDBG).</summary>
        public string QueueReport()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Entities.Where(x => !x.Dead && x.IsStructure && x.Def.DropOff))
            {
                var (bay, head, _, ok) = Bay(s);
                var outward = (head - bay).Normalized; var across = new Vec2(-outward.Y, outward.X);
                sb.AppendLine($"#{s.Id} {s.Def.Key} t{s.Team} origin {s.Origin.X},{s.Origin.Y} size {s.Def.SizeX}x{s.Def.SizeY} bay {bay} head {head} lane {ok}");
                for (int r = 0; r < 4; r++) { var sp = QueueSpot(head, outward, across, r) ?? new Vec2(-1, -1); var t = Int2.Of(sp); sb.AppendLine($"   spot {r} {sp} passable {Map.Passable(t.X, t.Y)} occupant {(Map.InBounds(t.X, t.Y) ? Map.Occupant[Map.Idx(t.X, t.Y)] : -9)}"); }
                foreach (var o in Entities.Where(o => !o.Dead && o.IsHarvester && o.DockAt == s.Id))
                    sb.AppendLine($"   truck #{o.Id} {o.Dock} at {o.Pos} rank {(o.Dock == DockStep.Queue ? QueueAhead(s, o) : -1)} path {(o.Path == null ? "-" : string.Join(" ", o.Path))}");
            }
            return sb.ToString();
        }

        /// <summary>How near the head of its lane a truck on its way joins a drop-off's queue (when it has to wait).</summary>
        public const float QueueJoinDist = 5f;

        /// <summary>Trucks queued for drop-off s ahead of e (joined earlier; ties by id). With e null: all of them.</summary>
        int QueueAhead(Entity s, Entity e)
        {
            int n = 0;
            foreach (var o in Entities)
            {
                if (o == e || o.Dead || !o.IsHarvester || o.DockAt != s.Id || o.Dock != DockStep.Queue) continue;
                if (e == null || o.QueuedAt < e.QueuedAt || (o.QueuedAt == e.QueuedAt && o.Id < e.Id)) n++;
            }
            return n;
        }

        /// <summary>The bay is spoken for: another truck is pulling up to it (its turn), or in it (lining up, backing in, unloading or pulling out).</summary>
        bool BayClaimed(Entity s, Entity e)
        {
            foreach (var o in Entities)
                if (o != e && !o.Dead && o.IsHarvester && o.DockAt == s.Id && o.Dock >= DockStep.Enter) return true;
            return false;
        }

        /// <summary>
        /// Queue spot `rank` (0 = next in): a single file beside the lane, 1.25 apart (trucks are 1 across), starting just
        /// past where a truck pulling out stops, so the lane stays clear; the other side if that's blocked; a line across
        /// the head of the lane if the lane runs into rock or off the map; straight back along it as a last resort. Null
        /// if none of those is open ground (it waits where it is).
        /// </summary>
        Vec2? QueueSpot(Vec2 head, Vec2 outward, Vec2 across, int rank)
        {
            float along = PullOutPast + 0.2f + rank * 1.25f;
            foreach (var spot in new[] { head + outward * along + across * 1.7f, head + outward * along - across * 1.7f,
                                         head + across * (2.2f + rank * 1.25f), head - across * (2.2f + rank * 1.25f), head + outward * (along + 1.6f) })
            {
                var t = Int2.Of(spot);
                if (Map.Passable(t.X, t.Y)) return spot;
            }
            return null;
        }

        /// <summary>Another truck is in this bay (lining up, backing in, unloading or pulling out).</summary>
        bool BayTaken(Entity dropOff, Entity e) =>
            Entities.Any(o => o != e && !o.Dead && o.IsHarvester && o.DockAt == dropOff.Id && o.Dock >= DockStep.Align);

        // A bay's four sides, by Entity.BaySide: south (where the models' doors are, and what old saves have), east, west, north.
        public static readonly Vec2[] BaySides = { new Vec2(0, -1), new Vec2(1, 0), new Vec2(-1, 0), new Vec2(0, 1) };

        /// <summary>
        /// A drop-off's truck bay: the point trucks back onto (just outside the footprint), the head of its lane
        /// (BayLength further out), and the facing a docked truck has (out of the bay). On the drop-off's own side
        /// (Entity.BaySide, facing its ore) unless that lane is blocked, then the first clear side in the order south,
        /// east, west, north. laneOk is false when no side has a clear lane (trucks unload beside it, on its own side).
        /// </summary>
        public (Vec2 bay, Vec2 head, float facing, bool laneOk) Bay(Entity s) => Bay(s.Origin, s.Def, s.BaySide);

        public (Vec2 bay, Vec2 head, float facing, bool laneOk) Bay(Int2 origin, EntityDef def, int side = 0)
        {
            var (open, clear) = OpenBaySide(origin, def, side);
            var (bay, head, f) = BayOn(origin, def, BaySides[open]);
            return (bay, head, f, clear);
        }

        /// <summary>The side a bay really opens on: its own unless that lane is blocked, then the first clear one (clear is false if none is, and then it is its own).</summary>
        (int side, bool clear) OpenBaySide(Int2 origin, EntityDef def, int side)
        {
            side = Math.Clamp(side, 0, BaySides.Length - 1);
            for (int k = 0; k < BaySides.Length; k++)
            {
                int i = k == 0 ? side : (k - 1 < side ? k - 1 : k); // its own side, then the rest in order
                var (bay, head, _) = BayOn(origin, def, BaySides[i]);
                if (LaneClear(bay, head)) return (i, true);
            }
            return (side, false);
        }

        /// <summary>Does the building have a door of its own to turn (and its units to come out of)? A drop-off has its bay; a barracks and a factory their doors.</summary>
        public static bool HasDoorSide(EntityDef d) => d.IsStructure && (d.DropOff || d.Produces == Producer.Barracks || d.Produces == Producer.Factory);

        /// <summary>The side (an index into BaySides) a building's door faces: where a drop-off's bay opens now, a barracks' or factory's door as built, else south (where the models' doors are).</summary>
        int DoorSide(Entity s) => s.Def.DropOff ? OpenBaySide(s.Origin, s.Def, s.BaySide).side : HasDoorSide(s.Def) ? Math.Clamp(s.BaySide, 0, BaySides.Length - 1) : 0;

        /// <summary>
        /// The side a new barracks' or factory's door faces: toward the middle of the map, along the axis it is farthest from the middle on
        /// (a building on the diagonal takes the right-hand side as it faces the middle). So both seats' armies come out on the side facing
        /// the enemy, mirrored, instead of one seat's coming out facing the front and the other's behind its building.
        /// </summary>
        int ChooseDoorSide(Int2 origin, EntityDef def)
        {
            var c = new Vec2(origin.X + def.SizeX / 2f, origin.Y + def.SizeY / 2f);
            var d = new Vec2(Map.W / 2f, Map.H / 2f) - c;
            int ex = d.X > 0 ? 1 : 2, ey = d.Y > 0 ? 3 : 0;
            if (MathF.Abs(MathF.Abs(d.X) - MathF.Abs(d.Y)) > 0.01f) return MathF.Abs(d.X) > MathF.Abs(d.Y) ? ex : ey;
            var right = RightOf(c);
            return BaySides[ex].X * right.X + BaySides[ex].Y * right.Y > BaySides[ey].X * right.X + BaySides[ey].Y * right.Y ? ex : ey;
        }

        /// <summary>Choose every drop-off's bay side again, from the map's ore as it is now (for tests that rewrite the ore after the world is built).</summary>
        internal void ChooseBays()
        {
            foreach (var e in Entities)
                if (!e.Dead && e.IsStructure && e.Def.DropOff) e.BaySide = ChooseBaySide(e.Origin, e.Def, NearestOreField(e.Center));
        }

        static (Vec2 bay, Vec2 head, float facing) BayOn(Int2 origin, EntityDef def, Vec2 n)
        {
            var c = new Vec2(origin.X + def.SizeX / 2f, origin.Y + def.SizeY / 2f);
            var bay = c + new Vec2(n.X * (def.SizeX / 2f + 0.5f), n.Y * (def.SizeY / 2f + 0.5f));
            return (bay, bay + n * BayLength, n.Angle);
        }

        bool LaneClear(Vec2 bay, Vec2 head) => LaneTiles(bay, head).All(t => Map.InBounds(t.X, t.Y) && Map.Passable(t.X, t.Y));

        /// <summary>
        /// The side a new drop-off's bay opens on: of the sides with a clear lane, the one whose lane head is nearest the ore
        /// field (ties go to the right-hand side as the building faces the map centre, so mirrored bases choose mirrored sides).
        /// South when there is no field, or no side is clear (as it always was).
        /// </summary>
        int ChooseBaySide(Int2 origin, EntityDef def, List<Vec2> field)
        {
            if (field.Count == 0) return 0;
            var right = RightOf(new Vec2(origin.X + def.SizeX / 2f, origin.Y + def.SizeY / 2f));
            int best = -1; float bestDist = 0, bestRight = 0;
            for (int i = 0; i < BaySides.Length; i++)
            {
                var (bay, head, _) = BayOn(origin, def, BaySides[i]);
                if (!LaneClear(bay, head)) continue;
                float dist = DistToField(field, head), toRight = BaySides[i].X * right.X + BaySides[i].Y * right.Y;
                int cmp = best < 0 ? -1 : TieCmp(dist, bestDist);
                if (cmp == 0) cmp = TieCmp(bestRight, toRight);
                if (cmp < 0) { best = i; bestDist = dist; bestRight = toRight; }
            }
            return Math.Max(0, best);
        }

        /// <summary>Which way a structure's door faces, in the sim's radians: its bay for a drop-off, a barracks' or factory's door as built, else south, where the models' doors are.</summary>
        public float DoorFacing(Entity s) => s.IsStructure ? BaySides[DoorSide(s)].Angle : -MathF.PI / 2f;

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
            Vec2? best = null; float bestScore = float.MaxValue, bestRight = 0;
            var right = RightOf(e.Pos);
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
                    // (Equally good spots: the one on the right as the surveyor faces the middle, not the first the scan meets.)
                    float toRight = (q.X - e.Pos.X) * right.X + (q.Y - e.Pos.Y) * right.Y;
                    int cmp = TieCmp(score, bestScore);
                    if (cmp == 0) cmp = TieCmp(bestRight, toRight);
                    if (cmp < 0) { bestScore = score; bestRight = toRight; best = q; }
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
        /// <summary>Where a deep mine can stand on deposit d: its 2x2 footprint over or beside the deposit's tile, nearest
        /// first. Null (and why) when nothing around it is clear.</summary>
        public Int2? DeepMineSpot(DeepDeposit d, int team, out string why)
        {
            var def = Defs.Get("deep_mine");
            var c = Int2.Of(d.Pos);
            why = null;
            Int2? best = null; float bd = float.MaxValue;
            for (int dy = -def.SizeY; dy <= 1; dy++)
                for (int dx = -def.SizeX; dx <= 1; dx++)
                {
                    var o = new Int2(c.X + dx, c.Y + dy);
                    var no = CanPlace(team, "deep_mine", o.X, o.Y, requireNear: false);
                    if (no != null) { if (dx == -(def.SizeX - 1) / 2 && dy == -(def.SizeY - 1) / 2) why = no; continue; }
                    float dist = Vec2.DistSq(new Vec2(o.X + def.SizeX / 2f, o.Y + def.SizeY / 2f), d.Pos);
                    if (dist < bd) { bd = dist; best = o; }
                }
            if (best == null) why = $"nowhere around zone #{d.Id} is clear for a 2x2 deep mine ({why ?? "blocked"})";
            return best;
        }

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
            if (DeepMineSpot(d, team, out var blocked) == null) return $"zone #{d.Id} can't be drilled right now: {blocked}";
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

        /// <summary>Ore left at which a deep mine warns it's running low: five minutes of pumping.</summary>
        public const float DeepMineWarnAmount = EntityDef.DeepMineRate * 300f; // 5 minutes: time to survey, build a rig and drive it

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
            if (e.WorkTimer >= 1f) { team.Stats.OreMined += (int)e.WorkTimer; team.Stats.DeepMined += (int)e.WorkTimer; e.WorkTimer -= (int)e.WorkTimer; }
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

        /// <summary>Trucks choosing their own field keep this far from enemy structures they know of.</summary>
        public const float HostileOreRadius = 14f;
        /// <summary>A field farther than this from every drop-off of yours means the home fields are dry.</summary>
        public const float FarFieldDist = 30f;

        /// <summary>Worth a truck picking on its own: a basic ore (iron, copper), something of yours refines it, or you have
        /// little of it. Crystal and uranium pile up uselessly until you have the lab or plant that takes them.</summary>
        bool OreUseful(int team, int type)
        {
            string ore = Defs.Ores[type];
            if (type <= 1 || Teams[team].Amount(ore) < 300) return true;
            return Entities.Any(s => !s.Dead && s.Team == team && s.IsStructure && s.IsComplete && s.Def.Recipes.Any(r => r.Inputs.ContainsKey(ore)));
        }

        /// <summary>How far from a drop-off a truck will pick a new field on its own.</summary>
        public const float LeashDist = 35f;
        /// <summary>
        /// What a team can do once the surface ore near its drop-offs is gone: there are only two ways on, and every
        /// surface-ore alert says so (agents left idle trucks parked for an hour, not knowing).
        /// </summary>
        public static readonly string OreWaysOn =
            "there are only two ways on. " +
            "1) Go underground: train a geological_surveyor (factory) and 'prospect' (or 'survey' a spot) to flag deep deposits as mining_zones, then 'drill' a drill_rig onto one: a deep_mine pumps 4 ore/s with no trucks. " +
            "2) Explore the map: ore_fields lists only fields in ground you've explored, so scout the rest (scout_buggy, recon_drone) to find more surface ore, then deploy an outpost_truck (or build a mining_refinery) beside a field you find and send trucks there with harvest x,y. " +
            $"Trucks won't go looking on their own: they only mine within {(int)LeashDist} tiles of a drop-off";

        /// <summary>Within this many tiles of a drop-off counts as "home" ore.</summary>
        public const float HomeOreRadius = 20f;

        /// <summary>
        /// Warn while there's still time to act: when the surface ore around a team's drop-offs is down to half of what
        /// was there, before the trucks start wandering off to far fields (or the economy simply stops).
        /// </summary>
        void CheckHomeOre()
        {
            foreach (var team in Teams)
            {
                if (team.Defeated) continue;
                var drops = Entities.Where(s => !s.Dead && s.Team == team.Id && s.IsStructure && s.IsComplete && s.Def.DropOff).ToList();
                if (drops.Count == 0) continue;
                var seen = new HashSet<int>();
                // Iron and copper, each on its own: untouched crystal and uranium nearby mustn't mask a dry iron field.
                var left = new long[2]; var was = new long[2];
                int r = (int)HomeOreRadius;
                foreach (var d in drops)
                {
                    int cx = (int)d.Center.X, cy = (int)d.Center.Y;
                    for (int y = Math.Max(0, cy - r); y <= Math.Min(Map.H - 1, cy + r); y++)
                        for (int x = Math.Max(0, cx - r); x <= Math.Min(Map.W - 1, cx + r); x++)
                        {
                            int i = Map.Idx(x, y);
                            if (Map.OreBase[i] <= 0 || Map.OreBaseType[i] > 1 || !seen.Add(i)) continue;
                            left[Map.OreBaseType[i]] += Map.FieldOre(i); was[Map.OreBaseType[i]] += Map.OreBase[i];
                        }
                }
                // At half: the second half goes much faster than the first (more trucks by then), so a quarter left is too late.
                // Each ore once, when it reaches half (test player Reed's copper warning hid that his iron was going too).
                int k = Enumerable.Range(0, 2).Where(j => (team.HomeOreWarned & (1 << j)) == 0 && was[j] >= 1000 && left[j] <= was[j] / 2).Select(j => (int?)j).FirstOrDefault() ?? -1;
                if (k < 0) continue;
                team.HomeOreWarned |= 1 << k;
                team.SurfaceWarnedAt = Time;
                Alerts.Raise(this, team.Id, "surface_ore_exhausted", Priority.Medium, drops[0].Center, hit: false)
                      .Lost.Add($"the surface {Defs.Ores[k]} within {(int)HomeOreRadius} tiles of your drop-offs is down to {left[k]} ({100 * left[k] / was[k]}% of what was there). " +
                                "Plan the next step now: once it's gone, " + OreWaysOn);
            }
        }

        /// <summary>A truck heading a long way out for ore: the fields near home are used up, and that trip is a risk.</summary>
        void WarnIfFarAfield(Entity truck, Int2 tile)
        {
            var team = Teams[truck.Team];
            if (Time - team.SurfaceWarnedAt < 120f) return;
            float near = float.MaxValue;
            foreach (var s in Entities)
                if (!s.Dead && s.Team == truck.Team && s.IsStructure && s.Def.DropOff) near = MathF.Min(near, s.DistFrom(tile.Center));
            if (near == float.MaxValue || near <= FarFieldDist) return;
            team.SurfaceWarnedAt = Time;
            Alerts.Raise(this, truck.Team, "surface_ore_exhausted", Priority.Medium, tile.Center, hit: false)
                  .Lost.Add($"the ore near your bases is running out: mining truck #{truck.Id} is heading {(int)near} tiles from your nearest drop-off, to {tile.X},{tile.Y}. " +
                            "Escort it. Once the ore near home is gone, " + OreWaysOn);
        }

        /// <summary>A mining truck that can't find any surface ore means it's time to go deep.</summary>
        void WarnSurfaceExhausted(Entity truck, int type)
        {
            var team = Teams[truck.Team];
            if (Time - team.SurfaceWarnedAt < 120f) return;
            team.SurfaceWarnedAt = Time;
            string what = type >= 0 ? Defs.Ores[type] : "surface ore";
            Alerts.Raise(this, truck.Team, "surface_ore_exhausted", Priority.Medium, truck.Pos, hit: false)
                  .Lost.Add($"mining truck #{truck.Id} can't find any {what} within {(int)LeashDist} tiles of your drop-offs and is waiting" +
                          (IsProtected(truck.Team) ? $" (under newcomer protection your trucks also keep {(int)HostileOreRadius} tiles from other teams' structures)" : "") + ". Your call: " + OreWaysOn);
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
                // Running dry over its own pad, an aircraft glides in rather than falling a tile short of home.
                if (e.IsAir && Owned(e.Team).Any(s => IsFuelPoint(e, s) && s.DistFrom(e.Pos) <= 3f)) { e.Fuel = 0.01f; return; } // still flying: it gets to its slot
                else if (e.IsAir) { Crash(e); return; }
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
            // Ground units don't think about it above half a tank; aircraft always do (a drone half a tank out is already
            // at the point of no return: waiting for 50% stranded them a tile from home).
            if (!e.IsAir && e.Fuel > max * 0.5f) return;
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
            float need = BingoTiles(e, Vec2.Dist(e.Pos, p.Center)) / MathF.Max(0.1f, e.Def.Speed);
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
                // Its own slot on the pad (PadSpot), not the pad's centre: every aircraft used to fly to the centre, where
                // two can't both be, and they shoved each other over it until they ran dry. A full pad: it holds nearby.
                if ((Tick + e.Id) % 20 == 0 || !IsPadSlot(p, e.OrderPos) && !IsHoldingSpot(e, p, e.OrderPos)) e.OrderPos = PadSpot(e, p);
                if (Vec2.Dist(e.Pos, e.OrderPos) > 0.05f) { StepToward(e, e.OrderPos); return; }
                if (!IsPadSlot(p, e.OrderPos)) { e.Moving = false; if (e.Fuel >= e.FuelMax * 0.99f) FinishRefuel(e); return; } // holding for a slot: hovering, re-asking each second
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

        /// <summary>Spacing of landing slots on a pad: the widest pair of aircraft (two bombers) needs 1.4.</summary>
        public const float PadSlotSpacing = 1.5f;

        /// <summary>Where aircraft land on pad p: a grid PadSlotSpacing apart that fits its footprint (one on a small pad).</summary>
        List<Vec2> PadSlots(Entity p)
        {
            padSlots.Clear();
            int nx = Math.Max(1, (int)((p.Def.SizeX - 0.5f) / PadSlotSpacing) + 1), ny = Math.Max(1, (int)((p.Def.SizeY - 0.5f) / PadSlotSpacing) + 1);
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                    padSlots.Add(p.Center + new Vec2((ix - (nx - 1) / 2f) * PadSlotSpacing, (iy - (ny - 1) / 2f) * PadSlotSpacing));
            return padSlots;
        }
        readonly List<Vec2> padSlots = new List<Vec2>();

        bool IsPadSlot(Entity p, Vec2 at)
        {
            foreach (var slot in PadSlots(p)) if (Vec2.DistSq(slot, at) < 0.0025f) return true;
            return false;
        }

        /// <summary>Where aircraft e waits for a slot on full pad p: its own point on a ring just outside it.</summary>
        Vec2 HoldingSpot(Entity e, Entity p)
        {
            float a = e.Id * 2.3999632f; // the golden angle: neighbours' spots spread round the ring
            float r = MathF.Sqrt(p.Def.SizeX * p.Def.SizeX + p.Def.SizeY * p.Def.SizeY) / 2f + 1.8f; // clear of the pad (OnPad's 1.5) even off a corner
            var at = p.Center + new Vec2(MathF.Cos(a), MathF.Sin(a)) * r;
            return new Vec2(Math.Clamp(at.X, 0.5f, Map.W - 0.5f), Math.Clamp(at.Y, 0.5f, Map.H - 0.5f));
        }

        bool IsHoldingSpot(Entity e, Entity p, Vec2 at) => Vec2.DistSq(HoldingSpot(e, p), at) < 0.0025f;

        /// <summary>A slot is taken by another of the team's aircraft bound for it, or parked on it.</summary>
        bool SlotTaken(Entity e, Entity p, Vec2 slot)
        {
            foreach (var o in Entities)
            {
                if (o == e || o.Dead || !o.IsAir || o.Team != e.Team || o.IsCarried) continue;
                if (o.Order == Order.Refuel && o.TargetId == p.Id && Vec2.DistSq(o.OrderPos, slot) < 0.0025f) return true;
                if (!o.Moving && Vec2.DistSq(o.Pos, slot) < 0.49f) return true;
            }
            return false;
        }

        /// <summary>
        /// Aircraft e's landing slot on pad p: the one it has while it's still its own, else the nearest free one. The pad
        /// full, it holds at its HoldingSpot; the thirstiest of those waiting gets the next free slot, and one under 40%
        /// fuel has an idle, full aircraft parked on a slot move off to hold so it can land.
        /// </summary>
        Vec2 PadSpot(Entity e, Entity p)
        {
            var slots = new List<Vec2>(PadSlots(p));
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < slots.Count; i++)
            {
                if (SlotTaken(e, p, slots[i])) continue;
                if (Vec2.DistSq(slots[i], e.OrderPos) < 0.0025f) return slots[i]; // still its own
                float d = Vec2.DistSq(slots[i], e.Pos);
                if (d < bd) { bd = d; best = i; }
            }
            bool waitingTurn = Entities.Any(o => o != e && !o.Dead && o.IsAir && o.Team == e.Team && o.Order == Order.Refuel && o.TargetId == p.Id &&
                                                 IsHoldingSpot(o, p, o.OrderPos) && o.Fuel < e.Fuel);
            if (best >= 0 && !waitingTurn) return slots[best];
            if (best < 0 && e.Fuel < e.FuelMax * 0.4f)
                foreach (var slot in slots)
                {
                    var parked = Entities.FirstOrDefault(o => o != e && !o.Dead && o.IsAir && o.Team == e.Team && o.Order == Order.Idle && !o.Moving &&
                                                             o.Fuel >= o.FuelMax * 0.95f && Vec2.DistSq(o.Pos, slot) < 0.49f);
                    if (parked == null) continue;
                    SetOrder(parked, Order.Move, HoldingSpot(parked, p));
                    parked.NoAutoRefuelUntil = Time + 60f;
                    Emit("made_room", e.Team, parked.Id, e.Id, p.Center, key: parked.Def.Key,
                         text: $"{parked.Def.Key} #{parked.Id} lifted off {p.Def.Key} #{p.Id} to make room for {e.Def.Key} #{e.Id}, low on fuel; it holds nearby. More pads (an airfield lands {PadSlots(p).Count}) keep aircraft from waiting.");
                    return slot;
                }
            return HoldingSpot(e, p);
        }

        /// <summary>Tiles of travel one unit of fuel buys (aircraft burn by the second, vehicles by the tile).</summary>
        public static float FuelTiles(Entity u, float fuel) => fuel * MathF.Max(0.1f, u.Def.Speed);
        /// <summary>Fuel burnt per straight-line tile: roads wind for vehicles, aircraft fly straight.</summary>
        public static float BurnPerTile(Entity u) => u.IsAir ? 1f : 1.4f;
        /// <summary>The bingo rule, in tiles of fuel: enough to get padDist back to fuel (with a margin) plus a reserve.
        /// The sim turns units back on it and the move planner predicts with it, so the two agree.</summary>
        public static float BingoTiles(Entity u, float padDist) =>
            u.IsAir ? padDist * 1.05f + 6f * MathF.Max(0.1f, u.Def.Speed) : padDist * 1.4f + 10f * MathF.Max(0.1f, u.Def.Speed); // aircraft fly straight: a thin margin

        /// <summary>Whether a full tank from here gets to `to` and back to fuel: a resumed trip that can't is dropped, not
        /// yo-yoed (out, turn back, refuel, out again) forever.</summary>
        bool TripInRange(Entity e, Vec2 to)
        {
            var near = Entities.Where(s => IsFuelPoint(e, s)).Select(s => Vec2.Dist(s.Center, to)).DefaultIfEmpty(0f).Min();
            return FuelTiles(e, e.FuelMax) >= Vec2.Dist(e.Pos, to) * BurnPerTile(e) + BingoTiles(e, near);
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
                else if ((o == Order.Move || o == Order.AttackMove) && !e.WaypointLoop && !TripInRange(e, e.OrderPos))
                {
                    var goal = e.OrderPos;
                    SetOrder(e, Order.Idle, e.Pos);
                    e.ResumeOrder = Order.Idle;
                    float there = Vec2.Dist(e.Pos, goal) * BurnPerTile(e), back = BingoTiles(e, Entities.Where(s => IsFuelPoint(e, s)).Select(s => Vec2.Dist(s.Center, goal)).DefaultIfEmpty(0f).Min());
                    Emit("out_of_range", e.Team, e.Id, 0, e.Pos, key: e.Def.Key,
                         text: $"{e.Def.Key} #{e.Id} refuelled and is staying put: {(int)goal.X},{(int)goal.Y} is out of its range there and back " +
                               $"(~{there:0} tiles there, ~{back:0} back to fuel with its reserve, and a full tank is ~{FuelTiles(e, e.FuelMax):0}). " +
                               (e.IsAir ? "Build an airfield nearer, or use a longer-range aircraft" : "Deploy an outpost along the way, or send a repair truck with it"));
                    return;
                }
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
            float dd = Vec2.Dist(e.Pos, dest);
            if (dd <= tolerance || (dd <= TakenReach(e) && GoalTaken(e, dest))) { e.Moving = false; e.Path = null; return true; }
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

        /// <summary>Can a unit step from a to b: onto open ground, and a diagonal step not squeezing between two blocked tiles?</summary>
        bool StepOpen(Vec2 a, Vec2 b)
        {
            Int2 ta = Int2.Of(a), tb = Int2.Of(b);
            bool Open(int x, int y) => Map.InBounds(x, y) && Map.Passable(x, y);
            return Open(tb.X, tb.Y) && (ta.X == tb.X || ta.Y == tb.Y || Open(tb.X, ta.Y) || Open(ta.X, tb.Y));
        }

        /// <summary>Open ground all along the straight line from a to b (sampled every quarter tile).</summary>
        bool LineClear(Vec2 a, Vec2 b)
        {
            float len = Vec2.Dist(a, b);
            int n = Math.Max(1, (int)(len / 0.25f));
            for (int i = 1; i <= n; i++)
            {
                var t = Int2.Of(a + (b - a) * (i / (float)n));
                if (!Map.Passable(t.X, t.Y)) return false;
            }
            return true;
        }

        /// <summary>How near a point another unit stands on e has to get for that to count as there (GoalTaken): a couple of its radii, and a little.</summary>
        static float TakenReach(Entity e) => e.Def.Radius * 2f + 0.6f;

        /// <summary>
        /// Another unit stands still on the point e is going to: as close as e can get counts as there. (Pushing in to the
        /// point and being shoved back off it, it rocked back and forth on the spot.)
        /// </summary>
        bool GoalTaken(Entity e, Vec2 dest)
        {
            if (e.IsAir) return false;
            Near(dest, 1.5f, nearSpot);
            foreach (var o in nearSpot)
                if (o != e && !o.Dead && !o.IsAir && !o.IsStructure && !o.IsMine && !o.IsCarried && !o.Moving && Vec2.Dist(o.Pos, dest) < o.Def.Radius + e.Def.Radius) return true;
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
            if (Time < e.SettledUntil) { e.Moving = false; return; } // settled by the dance guard: it stands still a moment
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
            else
            {
                // A soldier getting out of a vehicle's way finishes doing so, and never walks deeper into one's path: it
                // waits at the edge for the vehicle to pass (walking in to be dodged out again, it flipped round every tick).
                if (Time < e.DodgeUntil) { e.Moving = true; return; }
                if (!e.IsAir && Threats(e) > 0f && Threat(e.Pos + d / len * MathF.Min(len, e.Def.Speed * Dt)) > Threat(e.Pos) + 1e-4f) { e.Moving = true; return; }
                e.Facing = RotateToward(e.Facing, want, TurnRate(e) * Dt);
            }
            if (!e.IsArmed || e.Cooldown <= 0 || e.Order != Order.Attack) e.TurretFacing = RotateToward(e.TurretFacing, e.Facing, 4f * Dt);
            float speed = e.SpeedCap > 0 ? MathF.Min(e.Def.Speed, e.SpeedCap) : e.Def.Speed;
            if (e.Group != 0 && groupSpan.TryGetValue(e.Group, out var span))
            {
                float mine = RouteLeft(e);
                if (mine - span.front > GroupSlack) speed = e.Def.Speed;                     // left behind: catch up
                else if (span.rear - mine > GroupSlack) speed *= MathF.Max(0.1f, 1f - (span.rear - mine - GroupSlack) / 4f); // ahead: wait
            }
            float step = MathF.Min(len, speed * Dt);
            // A vehicle goes where it's facing (a gentle curve as it comes round), never sideways toward the point: within
            // 0.6 rad of it, it used to slide crabwise while it turned. Infantry and aircraft step straight to it.
            var heading = e.Def.Armor == Armor.Vehicle ? new Vec2(MathF.Cos(e.Facing), MathF.Sin(e.Facing)) : d / len;
            var next = e.Pos + heading * step;
            // Never roll onto its own infantry: wait for them to get clear (they dodge). Only a vehicle held up by its own for
            // OwnBlockLimit seconds forces its way through, and can catch one with nowhere to go.
            if (Crusher(e) && OntoOwnInfantry(e, next) is Entity inWay)
            {
                if (e.OwnBlockedSince <= 0f) e.OwnBlockedSince = Time;
                if (Time - e.OwnBlockedSince < OwnBlockLimit)
                {
                    GiveWay(inWay, e); // one standing still steps aside to a clear spot; one on the move dodges (DodgeVehicles)
                    e.Moving = true;
                    return;
                }
            }
            else e.OwnBlockedSince = 0f;
            var nt = Int2.Of(next);
            var ct = Int2.Of(e.Pos);
            if (e.IsAir) { if (Map.InBounds(nt.X, nt.Y) || !Map.InBounds(ct.X, ct.Y)) e.Pos = next; } // off the map (pushed out), it may fly back in
            else if (Map.Passable(nt.X, nt.Y) || !Map.Passable(ct.X, ct.Y)) e.Pos = next;
            else
            {
                // Blocked (usually clipping a corner): slide along whichever axis is free.
                var sx = new Vec2(next.X, e.Pos.Y); var sy = new Vec2(e.Pos.X, next.Y);
                var tx = Int2.Of(sx); var ty = Int2.Of(sy);
                if (Map.Passable(tx.X, tx.Y) && MathF.Abs(d.X) >= 0.01f) e.Pos = new Vec2(e.Pos.X + MathF.Sign(d.X) * step, e.Pos.Y);
                else if (Map.Passable(ty.X, ty.Y) && MathF.Abs(d.Y) >= 0.01f) e.Pos = new Vec2(e.Pos.X, e.Pos.Y + MathF.Sign(d.Y) * step);
                // Its way to the point runs through something built since its path was planned: plan again from here
                // (sliding along the wall, it wiggled against it).
                if (!e.IsAir && e.Path != null && !LineClear(e.Pos, p)) e.Path = null;
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

        /// <summary>An idle unit in a moving friendly's way steps aside to a clear spot (and makes that spot its new post).
        /// A unit that's stepping aside itself doesn't move anyone else on: two of them would take turns forever.</summary>
        void GiveWay(Entity idle, Entity mover)
        {
            if (idle.Order != Order.Idle || idle.Moving || idle.IsAir || Time < idle.SettledUntil || idle.Team != mover.Team || Time < idle.GhostUntil - 2.5f || mover.GivingWay) return;
            var dir = mover.Path != null && mover.PathIdx < mover.Path.Count ? mover.Path[mover.PathIdx] - mover.Pos : idle.Pos - mover.Pos;
            if (dir.LengthSq < 1e-4f) dir = new Vec2(1, 0);
            // To the side it already stands on (an id's parity decided it: left for one unit, right for the next, so a unit and its
            // half-turned twin stepped aside to opposite sides).
            var perp = new Vec2(-dir.Y, dir.X).Normalized;
            var from = idle.Pos - mover.Pos;
            var side = perp * (from.X * perp.X + from.Y * perp.Y >= 0f ? 1.6f : -1.6f);
            foreach (var s in new[] { side, side * -1f, side + dir.Normalized * 1.2f })
            {
                var spot = idle.Pos + s;
                var t = Int2.Of(spot);
                if (!Map.Passable(t.X, t.Y) || Crowded(idle, spot)) continue;
                SetOrder(idle, Order.Move, spot);
                idle.GivingWay = true;
                idle.GuardPos = spot;
                idle.GhostUntil = Time + 0.5f; // marks that it just gave way (no ping-pong)
                return;
            }
        }

        // ------------------------------------------------------------------ run over

        /// <summary>Ground vehicles from a light tank up (trucks too) crush infantry they drive over. Scout buggies are too light.</summary>
        public static bool Crusher(Entity e) => !e.IsAir && !e.IsStructure && !e.IsMine && e.Def.Armor == Armor.Vehicle && e.Def.Radius >= 0.45f;
        /// <summary>Moved this tick (rolling, even creeping), not just nudged by a neighbour.</summary>
        static bool Rolling(Entity v) => v.Moving && !v.Stranded && Vec2.DistSq(v.Pos, v.PrevPos) > 0.004f * 0.004f;
        /// <summary>Seconds a vehicle waits on its own soldiers in its path before it forces its way through.</summary>
        public const float OwnBlockLimit = 6f;
        /// <summary>How close an armed vehicle drives at enemy infantry it's fighting, to run it over.</summary>
        public const float RunDownRange = 5f;

        bool RunsDown(Entity e, Entity t, float d) =>
            Crusher(e) && t.Def.Armor == Armor.Infantry && !t.IsAir && !t.IsCarried && t.Team != e.Team && d <= RunDownRange && !IsProtected(t.Team) && !IsProtected(e.Team);

        /// <summary>A vehicle rolling into enemy infantry: separation leaves them to dodge or be crushed.</summary>
        bool Overruns(Entity v, Entity s) =>
            Crusher(v) && Rolling(v) && s.Def.Armor == Armor.Infantry && s.Team != v.Team && !IsProtected(s.Team) && !IsProtected(v.Team);

        /// <summary>The soldier of its own that moving v to next would put (closer) under its hull, if any.</summary>
        Entity OntoOwnInfantry(Entity v, Vec2 next)
        {
            Near(next, v.Def.Radius + 0.5f, nearCrush);
            foreach (var s in nearCrush)
            {
                if (s.Dead || s.Team != v.Team || s.Def.Armor != Armor.Infantry || s.IsCarried || s.IsAir) continue;
                float d = Vec2.Dist(s.Pos, next);
                if (d < v.Def.Radius * 0.6f + s.Def.Radius && d < Vec2.Dist(s.Pos, v.Pos)) return s;
            }
            return null;
        }

        /// <summary>
        /// Infantry get out of a vehicle's way, friend's or foe's: anyone in its path sprints out of it, the short way, at
        /// 1.4x their pace, once they see it coming (about 0.45 s before it reaches them, 0.25 s if they're shooting). That's
        /// all a body can do: one near the middle of a tank's path, boxed in (rock, buildings, a crowd) or too slow is run over.
        /// Every vehicle bearing down counts at once: a soldier takes the step (of eight directions) that leaves it least in
        /// anyone's path, and only one that's better than standing, so between two tanks it never steps out of one's path
        /// into the other's and back (it used to, every tick, spinning on the spot).
        /// </summary>
        void DodgeVehicles()
        {
            foreach (var s in Entities)
            {
                if (s.Dead || s.Def.Armor != Armor.Infantry || s.IsCarried || s.IsAir || s.IsStructure || s.Stranded || Time < s.SettledUntil) continue;
                float deepest = Threats(s, out var primary);
                if (deepest <= 0f) continue;
                float step = s.Def.Speed * 1.4f * Dt, here = Threat(s.Pos), best = here - 1e-4f;
                Vec2 bestDir = default;
                float a0 = primary.Angle;
                for (int k = 0; k < 8; k++)
                {
                    float a = a0 + k * (MathF.PI / 4f);
                    var dir = new Vec2(MathF.Cos(a), MathF.Sin(a));
                    var to = s.Pos + dir * step;
                    var t = Int2.Of(to);
                    if (!Map.Passable(t.X, t.Y)) continue;
                    float score = Threat(to) + 0.002f * k; // ties: straight out first
                    if (score < best) { best = score; bestDir = dir; }
                }
                if (best >= here - 1e-4f) continue; // boxed in, or no way out that isn't into another's path
                s.Pos += bestDir * step;
                s.Facing = RotateToward(s.Facing, bestDir.Angle, TurnRate(s) * Dt);
                s.DodgeUntil = Time + 0.25f;
                if (s.Order == Order.Idle) s.GuardPos = s.Pos; // standing where vehicles drive: it doesn't stroll back into their way
            }
        }

        /// <summary>
        /// Gathers the vehicles bearing down on soldier s (nearThreat: driving, or held up waiting for their own to clear;
        /// not parked or turning on the spot), and returns how deep it is in the deepest one's path, with the way straight
        /// out of it (on the side it's already on).
        /// </summary>
        float Threats(Entity s, out Vec2 primary)
        {
            primary = default;
            nearThreat.Clear();
            Near(s.Pos, 3.2f, nearCrush);
            float deepest = 0f;
            foreach (var v in nearCrush)
            {
                if (v.Dead || !Crusher(v) || v.IsCarried) continue;
                if (!Rolling(v) && !(v.Team == s.Team && v.Moving && v.OwnBlockedSince > 0f)) continue;
                var heading = new Vec2(MathF.Cos(v.Facing), MathF.Sin(v.Facing));
                float clear = v.Def.Radius + s.Def.Radius + 0.25f;
                // Its own side's soldiers clear its path in good time (a driver they know is coming). An enemy's get moving
                // about 0.45 s before it reaches them (0.25 s if they're busy shooting).
                float react = Time - s.LastFiredAt < 1f ? 0.25f : 0.45f;
                float reach = s.Team == v.Team ? v.Def.Radius + 1.5f : MathF.Max(0.3f, Vec2.Dist(v.Pos, v.PrevPos) / Dt * react);
                nearThreat.Add((v.Pos, heading, clear, reach, v.Def.Radius + s.Def.Radius));
                float depth = InPath(nearThreat[nearThreat.Count - 1], s.Pos);
                if (depth > deepest)
                {
                    deepest = depth;
                    var rel = s.Pos - v.Pos;
                    float lateral = rel.X * heading.Y - rel.Y * heading.X;
                    float sign = MathF.Abs(lateral) > 0.02f ? MathF.Sign(lateral) : ((s.Id & 1) == 0 ? 1f : -1f);
                    primary = new Vec2(heading.Y, -heading.X) * sign;
                }
            }
            return deepest;
        }

        /// <summary>Gathers the vehicles bearing down on s (nearThreat); how many.</summary>
        int Threats(Entity s) { Threats(s, out _); return nearThreat.Count; }

        /// <summary>How deep p is in one vehicle's path (0 = clear of it); being under its hull counts double.</summary>
        static float InPath((Vec2 pos, Vec2 heading, float clear, float reach, float touch) v, Vec2 p)
        {
            var rel = p - v.pos;
            float ahead = rel.X * v.heading.X + rel.Y * v.heading.Y, lateral = MathF.Abs(rel.X * v.heading.Y - rel.Y * v.heading.X);
            float depth = ahead >= -0.2f && ahead <= v.reach && lateral < v.clear ? v.clear - lateral : 0f;
            float d = rel.Length;
            return d < v.touch ? depth + 2f * (v.touch - d) : depth;
        }

        /// <summary>How deep p is in the paths of the vehicles bearing down on the soldier now (nearThreat).</summary>
        float Threat(Vec2 p)
        {
            float sum = 0f;
            foreach (var v in nearThreat) sum += InPath(v, p);
            return sum;
        }

        readonly List<(Vec2 pos, Vec2 heading, float clear, float reach, float touch)> nearThreat = new List<(Vec2, Vec2, float, float, float)>();

        /// <summary>
        /// Infantry a vehicle drives over (moving at 30% of its speed or more, towards them) are crushed: an enemy's anywhere
        /// under the hull; one of its own only right under it (it won't roll onto its own and they dodge, so it takes a
        /// vehicle forcing its way out of a jam and a soldier with nowhere to go). Walking into a parked tank is harmless.
        /// </summary>
        void RunOver()
        {
            for (int i = 0; i < Entities.Count; i++)
            {
                var v = Entities[i];
                if (v.Dead || !Crusher(v) || !Rolling(v) || v.IsCarried) continue;
                Near(v.Pos, v.Def.Radius + 0.5f, nearCrush);
                foreach (var s in nearCrush)
                {
                    if (s.Dead || s.Def.Armor != Armor.Infantry || s.IsCarried || s.IsAir || s.IsStructure) continue;
                    bool own = s.Team == v.Team;
                    float under = Vec2.Dist(s.Pos, v.Pos);
                    if (under > v.Def.Radius * (own ? 0.45f : 1f)) continue;
                    // Driven over, not walked into: the vehicle is really moving (not inching), and towards the soldier.
                    var moved = v.Pos - v.PrevPos; var rel = s.Pos - v.PrevPos;
                    if (moved.Length < v.Def.Speed * Dt * 0.3f || moved.X * rel.X + moved.Y * rel.Y <= 0f) continue;
                    if (IsProtected(s.Team) || (!own && IsProtected(v.Team))) continue;
                    if (own && (Time < s.GhostUntil || v.OwnBlockedSince <= 0f || Time - v.OwnBlockedSince < OwnBlockLimit)) continue; // only a vehicle forcing its way through
                    string what = $"{s.Def.Key} #{s.Id}", by = $"{v.Def.Key} #{v.Id}";
                    Emit("crushed", s.Team, s.Id, v.Id, s.Pos, key: s.Def.Key,
                         text: own ? $"{what} was run over by your own {by}: it couldn't get out of the way" : $"{what} was run over by enemy {by}");
                    if (!own) Emit("crushed", v.Team, s.Id, v.Id, s.Pos, key: s.Def.Key, text: $"your {by} ran over enemy {what}");
                    Damage(s, s.Hp + 1f, v);
                }
            }
        }
        readonly List<Entity> nearCrush = new List<Entity>();

        /// <summary>Whether another ground unit already stands where e would stand at p.</summary>
        bool Crowded(Entity e, Vec2 p)
        {
            Near(p, 1.5f, nearSpot);
            foreach (var o in nearSpot)
                if (o != e && !o.IsStructure && !o.IsAir && !o.IsCarried && !o.IsMine && Vec2.Dist(o.Pos, p) < (o.Def.Radius + e.Def.Radius) * 0.9f) return true;
            return false;
        }
        readonly List<Entity> nearSpot = new List<Entity>();

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
                    if (a.IsAir && (a.Landed || b.Landed)) continue; // a landed aircraft sits on its slot: pinned, and those flying over are above it
                    if (Time < a.GhostUntil && a.Moving || Time < b.GhostUntil && b.Moving) continue; // unjamming: pass through
                    if (Overruns(a, b) || Overruns(b, a)) continue; // a rolling vehicle isn't held off by enemy infantry: it goes over them
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

        /// <summary>Seconds a unit may be on the move without getting anywhere before the guard settles it.</summary>
        public const float DanceLimit = 2f;
        /// <summary>Seconds a settled unit stands still (and is held fixed against pushes) before it carries on.</summary>
        public const float SettleSeconds = 3f;

        /// <summary>
        /// The dance guard (docs/MOVEMENT.md): no unit may dance, whatever new interaction of rules sets it off. Each unit
        /// keeps two-second running averages of its per-tick step, as a velocity and as a speed. One on the move (above a
        /// crawl) whose average velocity is under a quarter of its average speed for DanceLimit seconds is going back and
        /// forth, round in circles or jittering in place; so is one that reverses direction four times in about two
        /// seconds. Either way it settles: it stops where it is, is held fixed for SettleSeconds (others go round it, nobody
        /// shoves it), and a move that has got it within 2.5 tiles of its point counts as arrived. The world counts every
        /// settle (SettleCounts): in a designed situation it's a bug, and the movement gate fails on it.
        /// </summary>
        void WatchDances()
        {
            const float k = Dt / 2f;
            float decay = MathF.Exp(-Dt / 2f);
            foreach (var e in Entities)
            {
                if (e.Dead || e.IsStructure || e.IsMine || e.IsCarried) continue;
                var step = e.Pos - e.PrevPos;
                float len = step.Length;
                // A jump (unloaded, evicted), a truck in its bay's choreography (swing round, back in, unload, pull out) or a
                // landed aircraft: not walking anywhere, so nothing to judge. The averages start afresh after.
                if (len > 1f || e.Stranded || e.Dock >= DockStep.Align || (e.IsAir && e.Landed)) { e.DanceVel = default; e.DanceSpeed = e.DanceFor = e.DanceFlips = 0f; e.DanceStep = e.DanceHeading = default; continue; }
                e.DanceVel = e.DanceVel * (1f - k) + step * k;
                e.DanceSpeed = e.DanceSpeed * (1f - k) + len * k;
                // Reversals of its motion over about 0.3 s (DanceStep: a short running average of its step), against the last
                // direction it was clearly going (DanceHeading): a shove inside a march doesn't count, going back and forth does.
                e.DanceStep = e.DanceStep * (1f - Dt / 0.3f) + step * (Dt / 0.3f);
                float sl = e.DanceStep.Length;
                if (sl > 0.006f) // over 0.12 tiles a second, sustained
                {
                    var dir = e.DanceStep / sl;
                    if (e.DanceHeading.LengthSq > 0.5f && dir.X * e.DanceHeading.X + dir.Y * e.DanceHeading.Y < -0.5f) e.DanceFlips += 1f;
                    e.DanceHeading = dir;
                }
                e.DanceFlips *= decay;
                if (Time < e.SettledUntil) continue;
                // Judged only on ticks it moves: standing still (a truck that has just moved up the queue, waiting) drains the
                // count rather than adding to it, however busy its last two seconds were.
                bool going = e.DanceSpeed > 0.004f; // over 0.08 tiles a second, averaged
                if (len <= 0.001f) e.DanceFor = MathF.Max(0f, e.DanceFor - Dt);
                else e.DanceFor = going && e.DanceVel.Length < 0.25f * e.DanceSpeed ? e.DanceFor + Dt : 0f;
                if (e.DanceFor >= DanceLimit) Settle(e, "nowhere");
                else if (e.DanceFlips >= 4f) Settle(e, "reversing");
            }
        }

        void Settle(Entity e, string why)
        {
            e.SettledUntil = Time + SettleSeconds;
            e.DanceFor = e.DanceFlips = 0f; e.DanceVel = e.DanceStep = e.DanceHeading = default; e.DanceSpeed = 0f;
            e.Moving = false; e.Path = null;
            // Close enough counts as arrived (a move, or a survey, which reads the ground for tiles around it); a truck
            // that can't get onto its ore tile gives it up and picks another.
            if ((e.Order == Order.Move || e.Order == Order.AttackMove || e.Order == Order.Survey) && Vec2.Dist(e.Pos, e.OrderPos) <= 2.5f) e.OrderPos = e.Pos;
            if (e.Order == Order.Harvest && e.IsHarvester) e.HarvestTile = null;
            string key = $"{e.Def.Key} {e.OrderName} ({why})";
            SettleCounts[key] = SettleCounts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        void TryNudge(Entity e, Vec2 delta)
        {
            if (Time < e.SettledUntil) return; // settled: fixed while it lasts, others go round
            var next = e.Pos + delta;
            var t = Int2.Of(next);
            if (e.IsAir ? Map.InBounds(t.X, t.Y) : Map.Passable(t.X, t.Y)) e.Pos = next;
        }

        /// <summary>
        /// How fast a unit turns (rad/s): nothing snaps round. A soldier turns about twice a second, an aircraft banks round
        /// in under a second, vehicles at the rate StepToward drives them. Infantry and aircraft walk or fly where they're
        /// going while they turn; vehicles turn first.
        /// </summary>
        public static float TurnRate(Entity e) => e.IsAir ? 4f : e.Def.Armor == Armor.Infantry ? 12f : 5f;

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

        /// <summary>Compass direction of d (y grows north).</summary>
        public static string Compass(Vec2 d)
        {
            string[] names = { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };
            int i = (int)MathF.Round(MathF.Atan2(d.Y, d.X) / (MathF.PI / 4));
            return names[((i % 8) + 8) % 8];
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
            if (mine.Any(e => e.IsStructure && e.Working && e.Def.Key != "derrick")) return null; // a derrick alone can't change the game
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

        /// <summary>
        /// An outside agent's seat with no command, state or wait call for this long (game time) is warned, then resigned.
        /// The lobby counts a seat as active if it's been heard from within ActiveWithin.
        /// </summary>
        public const float IdleWarnAfter = 15 * 60f, IdleResignAfter = 20 * 60f, ActiveWithin = 5 * 60f;
        /// <summary>Still playing, and heard from within ActiveWithin (house AI always is): the lobby's active.</summary>
        public bool IsActive(Team t) => !t.Left && !t.Defeated && (t.House || Time - t.LastActiveAt < ActiveWithin);

        /// <summary>
        /// Open arena: a seat nobody plays doesn't keep its place and score (Blueberry came second after 40 minutes with no
        /// orders). Outside agents' seats ("llm": joined players, and placeholder seats from an older game's config) go
        /// through Leave after IdleResignAfter: salvage, derricks back to neutral, off the scoreboard. House AI and a local
        /// human are exempt. No game time passes in a frozen room, so it doesn't count.
        /// </summary>
        void CheckIdle()
        {
            if (!Open || Tick % TickRate != 0) return;
            foreach (var t in Teams)
            {
                if (t.Controller != "llm" || t.Left || t.Defeated) continue;
                float idle = Time - t.LastActiveAt;
                if (idle >= IdleResignAfter) Leave(t.Id, $"was resigned after {IdleResignAfter / 60:0} minutes with no command, state or wait call");
                else if (idle >= IdleWarnAfter && idle < IdleWarnAfter + 2f) // a 2 s window, so no once-a-second check misses it; a repeat merges into the same alert
                {
                    var a = Alerts.Raise(this, t.Id, "idle", Priority.Critical, t.StartPos, hit: false);
                    if (a.Lost.Count == 0) a.Lost.Add($"no command, state or wait call from you for {IdleWarnAfter / 60:0} minutes. At {IdleResignAfter / 60:0} minutes your team is resigned and its base becomes salvage. Any command, state or wait call keeps your seat");
                }
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
            string salvage = Eliminate(t);
            Emit("defeated", t.Id, text: $"{t.Name} ({t.PlayerName ?? t.Controller}) could make no further progress ({why}) and was resigned as lost{salvage}");
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

        /// <summary>A team is still in the game while it has a structure, or a construction truck that could deploy into one. `without`: as if that one were gone.</summary>
        public bool StillStanding(int team, Entity without = null) =>
            Entities.Any(e => e != without && !e.Dead && e.Team == team && ((e.IsStructure && e.Def.Key != "derrick") || e.Def.DeploysInto == "command_center"));

        void CheckVictory()
        {
            CheckProtection();
            CheckStalled();
            CheckIdle();
            CheckRadar();
            if (Open)
            {
                // An open arena never ends: teams that lose every structure are out, everyone else plays on.
                foreach (var team in Teams)
                {
                    if (team.Defeated || StillStanding(team.Id)) continue;
                    team.Defeated = true;
                    string salvage = Eliminate(team);
                    Emit("defeated", team.Id, text: $"{team.Name} ({team.PlayerName ?? team.Controller}) has been eliminated{salvage}");
                    ForgetTeam(team.Id);
                }
                CheckArenaCleared();
                return;
            }
            foreach (var team in Teams)
            {
                if (team.Defeated) continue;
                if (!StillStanding(team.Id))
                {
                    team.Defeated = true;
                    string salvage = Eliminate(team);
                    Emit("defeated", team.Id, text: $"{team.Name} ({team.PlayerName ?? team.Controller}) has been defeated{salvage}");
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

    /// <summary>An order a player gave, numbered, at the tick it was given (after that tick's step, before the next).</summary>
    public class LoggedCommand
    {
        public long Seq;
        public int Tick;
        public int Team;
        public Dictionary<string, object> Cmd;
    }
}
