using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public class ProdItem
    {
        public string Key;
        public float Progress;   // seconds of work done
        public int StructureId;  // structures: the foundation entity being built
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
        /// <summary>Team stockpile: raw ores and manufactured materials (see Defs.Items).</summary>
        public readonly Dictionary<string, float> Stock = new Dictionary<string, float>();
        /// <summary>Net change per second of each item over the last second, for display.</summary>
        public readonly Dictionary<string, float> Rates = new Dictionary<string, float>();
        public int PowerProduced, PowerUsed;
        /// <summary>Enemy stealth units this team can currently see.</summary>
        public readonly HashSet<int> Detected = new HashSet<int>();

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
        public readonly List<ProdItem> StructureQueue = new List<ProdItem>();
        public readonly Dictionary<Producer, List<ProdItem>> UnitQueues = new Dictionary<Producer, List<ProdItem>>
        {
            { Producer.CommandCenter, new List<ProdItem>() },
            { Producer.Barracks, new List<ProdItem>() },
            { Producer.Factory, new List<ProdItem>() },
            { Producer.Airfield, new List<ProdItem>() },
        };
        /// <summary>Enemy structures this team has seen: id -> (key, origin, team).</summary>
        public readonly Dictionary<int, (string key, Int2 origin, int team)> KnownEnemyStructures = new Dictionary<int, (string, Int2, int)>();
        public readonly TeamStats Stats = new TeamStats();
        public bool LowPower => PowerUsed > PowerProduced;
    }

    public class World
    {
        public const int TickRate = 20;
        public const float Dt = 1f / TickRate;

        public readonly Map Map;
        public readonly Pathfinder Paths;
        public readonly List<Team> Teams = new List<Team>();
        public readonly Dictionary<int, Entity> ById = new Dictionary<int, Entity>();
        public readonly List<Entity> Entities = new List<Entity>();
        public readonly List<Projectile> Projectiles = new List<Projectile>();
        public readonly List<GameEvent> Events = new List<GameEvent>();
        public readonly AlertLog Alerts = new AlertLog();
        public int Tick;
        public float Time => Tick * Dt;
        public bool GameOver;
        public int Winner = -1;
        int nextId = 1;
        long nextSeq = 1;
        const int MaxEvents = 4000;

        public World(int teamCount = 2, int seed = 1337, int size = 80)
        {
            Map = Map.Generate(size, size, seed);
            Paths = new Pathfinder(Map);
            string[] names = { "Blue", "Red", "Green", "Yellow" };
            for (int t = 0; t < teamCount; t++)
            {
                var team = new Team { Id = t, Name = names[t], StartPos = Map.Spawns[t], Visible = new bool[Map.W * Map.H], Explored = new bool[Map.W * Map.H] };
                Teams.Add(team);
                // Enough raw ore for a power plant; everything after that has to be mined.
                team.Add("iron_ore", 500);
                team.Add("copper_ore", 150);
                var s = Int2.Of(team.StartPos);
                var hq = SpawnStructure(t, "command_center", new Int2(s.X - 1, s.Y - 1), 1f);
                SpawnUnit(t, "mining_truck", hq);
                for (int i = 0; i < 2; i++) SpawnUnit(t, "rifleman", hq);
            }
            UpdatePower();
            UpdateVisibility();
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
            var def = Defs.Get(key);
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
            var def = Defs.Get(key);
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
            if (e.IsHarvester) SetOrder(e, Order.Harvest, e.Pos);
            else if (at.Rally.HasValue) SetOrder(e, Order.Move, at.Rally.Value);
            return e;
        }

        readonly Random rng = new Random(42);

        void OnStructureComplete(Entity e)
        {
            UpdatePower();
            if (e.Def.Key == "mining_refinery") SpawnUnit(e.Team, "mining_truck", e);
        }

        /// <summary>Turn a deployable unit (Outpost Truck) into its structure where it stands.</summary>
        public string Deploy(Entity u)
        {
            var key = u.Def.DeploysInto;
            if (key == null) return $"{u.Def.Key} can't deploy";
            var def = Defs.Get(key);
            var t = Int2.Of(u.Pos);
            var origin = new Int2(t.X - (def.SizeX - 1) / 2, t.Y - (def.SizeY - 1) / 2);
            var why = CanPlace(u.Team, key, origin.X, origin.Y, requireNear: false);
            if (why != null) return $"can't deploy here: {why}";
            Remove(u);
            var s = SpawnStructure(u.Team, key, origin, 1f);
            Teams[u.Team].Stats.Count(key);
            Emit("built", u.Team, s.Id, pos: s.Center, key: key);
            return null;
        }

        public void Remove(Entity e)
        {
            e.Dead = true;
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
            var producerKey = Defs.ProducerKey(def.BuiltBy);
            if (producerKey != null && !HasComplete(team, producerKey)) return $"requires a completed {producerKey}";
            foreach (var r in def.Requires) if (!HasComplete(team, r)) return $"requires a completed {r}";
            return null;
        }

        public bool IsVisibleTo(int team, Entity e)
        {
            if (e.Team == team) return true;
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
            if (o == Order.Idle) e.GuardPos = e.Pos;
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
            foreach (var p in Projectiles) p.PrevPos = p.Pos;

            UpdateEconomy();
            UpdateProduction();
            for (int i = 0; i < Entities.Count; i++)
            {
                var e = Entities[i];
                if (e.Dead) continue;
                if (e.Cooldown > 0) e.Cooldown -= Dt;
                if (!e.IsStructure) UpdateUnit(e);
                else if (e.IsArmed && e.IsComplete) UpdateTurret(e);
            }
            UpdateProjectiles();
            Separate();
            Cleanup();
            if (Tick % 4 == 0) UpdateVisibility();
            CheckVictory();
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
                        float have = team.Stock.TryGetValue(kv.Key, out var v) ? v : 0;
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
                    var producer = FirstProducer(team.Id, kv.Key);
                    if (producer == null) continue;
                    var item = q[0];
                    var def = Defs.Get(item.Key);
                    item.Progress += Dt * rate;
                    if (item.Progress >= def.BuildTime)
                    {
                        q.RemoveAt(0);
                        var u = SpawnUnit(team.Id, item.Key, producer);
                        team.Stats.UnitsBuilt++;
                        team.Stats.Count(item.Key);
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
                case Order.Move:
                    if (FollowPath(e, e.OrderPos, 0.3f)) SetOrder(e, Order.Idle, e.Pos);
                    if (e.IsArmed && (e.Def.Armor == Armor.Vehicle || e.IsAir)) OpportunisticFire(e);
                    break;
                case Order.AttackMove:
                    {
                        var t = AcquireTarget(e, e.Def.Sight);
                        if (t != null) { Engage(e, t); break; }
                        if (FollowPath(e, e.OrderPos, 0.5f)) SetOrder(e, Order.Idle, e.Pos);
                        break;
                    }
                case Order.Attack:
                    {
                        var t = Get(e.TargetId);
                        if (t == null || t.Team == e.Team || !IsVisibleTo(e.Team, t) || !e.Def.Weapon.CanHit(t.Def)) { SetOrder(e, Order.Idle, e.Pos); break; }
                        Engage(e, t);
                        break;
                    }
                case Order.Harvest:
                case Order.ReturnOre:
                    UpdateHarvester(e);
                    break;
            }
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
            else if (Vec2.Dist(e.Pos, e.GuardPos) < 6f) { e.Path = null; StepToward(e, t.Center); }
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

        // ------------------------------------------------------------------ repair

        /// <summary>Medics heal infantry; repair trucks fix everything else. Neither works on itself.</summary>
        public static bool CanTend(Entity healer, Entity t) =>
            t != healer && !t.Dead && t.Team == healer.Team && t.IsComplete && t.Hp < t.Def.MaxHp - 0.5f &&
            (healer.Def.Medic ? t.Def.Armor == Armor.Infantry : t.Def.Armor != Armor.Infantry);

        void IdleRepair(Entity e)
        {
            e.Moving = false;
            if ((Tick + e.Id) % 10 != 0) return; // scanning every tick is wasteful
            Entity best = null; float bd = 6f;
            foreach (var o in Entities)
            {
                if (!CanTend(e, o)) continue;
                float d = o.DistFrom(e.Pos);
                if (d < bd) { bd = d; best = o; }
            }
            if (best != null) SetOrder(e, Order.Repair, best.Center, best.Id);
        }

        void UpdateRepair(Entity e)
        {
            var t = Get(e.TargetId);
            if (t == null || !CanTend(e, t)) { SetOrder(e, Order.Idle, e.Pos); return; }
            if (t.DistFrom(e.Pos) > e.Def.RepairRange) { Chase(e, t); return; }
            e.Moving = false;
            e.Path = null;
            e.TurretFacing = RotateToward(e.TurretFacing, (t.Center - e.Pos).Angle, 6f * Dt);
            var team = Teams[e.Team];
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
            foreach (var o in Entities)
            {
                if (o.Dead || o.Team == e.Team) continue;
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
                            if (!o.Dead && o != t && o.Team != p.Team && p.Weapon.CanHit(o.Def) && o.DistFrom(p.Pos) < p.Weapon.SplashRadius)
                                Damage(o, p.Weapon.Damage * 0.4f * p.Weapon.Multiplier(o.Def.Armor), src, p.Team);
                }
                else p.Pos += to.Normalized * step;
            }
        }

        void Damage(Entity t, float amount, Entity src, int srcTeam = -1)
        {
            if (t.Dead) return;
            int team = src?.Team ?? srcTeam;
            t.Hp -= amount;
            if (src != null) t.LastAttackerId = src.Id;
            if (Time - t.LastHitTime > 10f && (t.IsStructure || t.IsHarvester))
                Emit("under_attack", t.Team, t.Id, src?.Id ?? 0, t.Center, key: t.Def.Key);
            t.LastHitTime = Time;
            RaiseDamageAlert(t, src);
            // Retaliate if idle.
            if (src != null && !t.IsStructure && t.IsArmed && t.Order == Order.Idle && !src.Dead && t.Def.Weapon.CanHit(src.Def) && IsVisibleTo(t.Team, src))
                SetOrder(t, Order.Attack, src.Pos, src.Id);
            if (t.Hp <= 0)
            {
                Emit("destroyed", t.Team, t.Id, src?.Id ?? 0, t.Center, key: t.Def.Key);
                if (team >= 0 && team < Teams.Count) Teams[team].Stats.Kills++;
                if (t.IsStructure) Teams[t.Team].Stats.StructuresLost++; else Teams[t.Team].Stats.UnitsLost++;
                var lost = Alerts.Raise(this, t.Team, t.IsStructure ? "structure_lost" : "units_lost", t.IsStructure ? Priority.Critical : Priority.Medium, t.Center, attacker: src);
                lost.Lost.Add($"{t.Def.Key} #{t.Id}");
                Remove(t);
            }
        }

        /// <summary>
        /// Classify a hit the way a human commander would hear it: buildings and trucks under fire, or units
        /// caught off guard, interrupt; fights the army picked on purpose are just a combat report.
        /// </summary>
        void RaiseDamageAlert(Entity t, Entity src)
        {
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
                                (want < 0 || Map.OreType[Map.Idx(e.HarvestTile.Value.X, e.HarvestTile.Value.Y)] == want);
                if (!tileGood)
                {
                    var from = e.HarvestTile.HasValue ? e.HarvestTile.Value.Center : e.Pos;
                    // Spread trucks out: avoid tiles another truck is already working.
                    e.HarvestTile = Map.NearestOre(from, 40, t => !Entities.Any(o => o != e && !o.Dead && o.IsHarvester && o.HarvestTile.HasValue && o.HarvestTile.Value.Equals(t)), want)
                                    ?? Map.NearestOre(from, 80, null, want);
                    e.Path = null;
                    if (!e.HarvestTile.HasValue)
                    {
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

            // ReturnOre
            var refinery = Entities.Where(s => !s.Dead && s.Team == e.Team && s.IsStructure && s.IsComplete && s.Def.DropOff)
                                   .OrderBy(s => Vec2.DistSq(s.Center, e.Pos)).FirstOrDefault();
            if (refinery == null) { e.Moving = false; return; }
            var dock = DockPoint(refinery);
            if (Vec2.Dist(e.Pos, dock) > 0.6f) { FollowPath(e, dock, 0.5f); return; }
            e.Moving = false;
            e.WorkTimer += Dt;
            if (e.WorkTimer >= 0.1f)
            {
                e.WorkTimer = 0;
                int give = Math.Min(10, e.Cargo);
                e.Cargo -= give;
                if (e.CargoType >= 0) Teams[e.Team].Add(Defs.Ores[e.CargoType], give);
                Teams[e.Team].Stats.OreMined += give;
                if (e.Cargo <= 0) { e.CargoType = -1; e.Order = Order.Harvest; e.Path = null; }
            }
        }

        public Vec2 DockPoint(Entity refinery) => new Vec2(refinery.Origin.X + refinery.Def.SizeX / 2f, refinery.Origin.Y - 0.5f);

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
            float step = MathF.Min(len, e.Def.Speed * Dt);
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

        void Separate()
        {
            for (int i = 0; i < Entities.Count; i++)
            {
                var a = Entities[i];
                if (a.Dead || a.IsStructure) continue;
                for (int j = i + 1; j < Entities.Count; j++)
                {
                    var b = Entities[j];
                    if (b.Dead || b.IsStructure || a.IsAir != b.IsAir) continue;
                    float min = a.Def.Radius + b.Def.Radius;
                    var d = b.Pos - a.Pos;
                    float dsq = d.LengthSq;
                    if (dsq >= min * min) continue;
                    float len = MathF.Sqrt(dsq);
                    var n = len > 1e-4f ? d / len : new Vec2(1, 0);
                    float push = (min - len) * 0.25f;
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
            foreach (var team in Teams)
            {
                Array.Clear(team.Visible, 0, team.Visible.Length);
                foreach (var e in Entities)
                {
                    if (e.Dead || e.Team != team.Id) continue;
                    var c = e.Center;
                    int r = (int)MathF.Ceiling(e.Def.Sight + (e.IsStructure ? e.Def.SizeX / 2f : 0));
                    float rr = (e.Def.Sight + (e.IsStructure ? e.Def.SizeX / 2f : 0));
                    rr *= rr;
                    for (int y = (int)c.Y - r; y <= (int)c.Y + r; y++)
                        for (int x = (int)c.X - r; x <= (int)c.X + r; x++)
                        {
                            if (!Map.InBounds(x, y)) continue;
                            if (Vec2.DistSq(new Vec2(x + 0.5f, y + 0.5f), c) <= rr) { int vi = Map.Idx(x, y); team.Visible[vi] = true; team.Explored[vi] = true; }
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
                        float range = o.Def.Key == "radar_dome" && o.IsComplete ? 16f : 3f;
                        if (Vec2.DistSq(o.Center, s.Pos) <= range * range)
                        {
                            team.Detected.Add(s.Id);
                            if (!wasDetected.Contains(s.Id)) Alerts.Raise(this, team.Id, "stealth_detected", Priority.Critical, s.Pos, attacker: s, hit: false);
                            break;
                        }
                    }
                }
                // Enemy forces showing up near any of this team's structures.
                foreach (var e in Entities)
                {
                    if (e.Dead || e.Team == team.Id || e.IsStructure || e.IsHarvester || !IsVisibleTo(team.Id, e)) continue;
                    if (Entities.Any(s => !s.Dead && s.Team == team.Id && s.IsStructure && s.DistFrom(e.Pos) <= 10f))
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

        void CheckVictory()
        {
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
