using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public partial class World
    {
        /// <summary>The kitchen sink's scenario when this world is the showcase room (see KitchenSink); null in every real game.</summary>
        public KitchenSink Showcase;

        /// <summary>A bare world on a given map: teams with no base, units or stockpile. Scenarios build the rest.</summary>
        World(Map map, int teamCount)
        {
            Map = map;
            Paths = new Pathfinder(map);
            MakeCurrent();
            for (int t = 0; t < teamCount; t++)
                Teams.Add(new Team
                {
                    Id = t, Name = Flavors[t % Flavors.Length], StartPos = new Vec2(-1000, -1000),
                    Visible = new bool[map.W * map.H], Explored = new bool[map.W * map.H], Seat = ++seatCounter,
                });
        }

        internal static World Bare(Map map, int teamCount) => new World(map, teamCount);

        /// <summary>A structure exactly as given: no free truck, no events, no power update (the caller refreshes it).</summary>
        internal Entity PlaceStructure(int team, string key, Int2 origin, float progress)
        {
            var def = Def(key);
            var e = NewEntity(team, def);
            e.Origin = origin;
            e.BuildProgress = progress;
            e.Pos = e.PrevPos = e.Center;
            e.Hp = def.MaxHp * (progress >= 1 ? 1f : 0.1f + 0.9f * progress);
            for (int y = 0; y < def.SizeY; y++)
                for (int x = 0; x < def.SizeX; x++)
                    Map.Occupant[Map.Idx(origin.X + x, origin.Y + y)] = e.Id;
            return e;
        }

        /// <summary>A unit standing at pos, facing `facing`, fuelled and idle.</summary>
        internal Entity PlaceUnit(int team, string key, Vec2 pos, float facing)
        {
            var e = NewEntity(team, Def(key));
            e.Pos = e.PrevPos = e.GuardPos = e.OrderPos = pos;
            e.Facing = e.TurretFacing = facing;
            e.FuelCap = DroneFuel(e.Def);
            e.Fuel = e.FuelMax;
            return e;
        }

        /// <summary>Shoot at t now if the weapon is ready (turrets, which only pick targets by themselves).</summary>
        internal void FireFor(Entity e, Entity t) => FireAt(e, t);
        internal void RefreshPower() => UpdatePower();
    }

    /// <summary>
    /// The kitchen sink: a showcase room that holds every unit and structure in every state the view can show, laid out
    /// as a labelled gallery for artistic review (docs/art/KITCHEN_SINK.md). Started with the app's -kitchensink flag
    /// (GameConfig.KitchenSink); real games never build it, and the sim runs unchanged around it.
    ///
    /// How it stays put forever: every team is under newcomer protection, so nothing fights or takes damage on its own
    /// and mines never go off; shooters get explicit attack orders on their targets (turrets are fired by Hold), so the
    /// combat range fires without hurting anyone. Hold runs after every tick and puts the rest back: pinned exhibits
    /// return to their spot and stance, health is set for the damage rows, construction sites keep their progress,
    /// production queues refill, ore fields refill (except the depleting demo), stockpiles stay full, fuel stays topped
    /// up, and each looping demo (docking, roll-outs, surveys, drilling, transports, refuelling, wrecks, explosions)
    /// steps its own little state machine.
    ///
    /// The layout is screen-aligned for the game's default camera (yaw 45): "a" runs left to right across the screen
    /// (map +x, -y) and "b" runs up the screen (map +x, +y). Each district has a rectangle in (a, b) and its exhibits are
    /// placed in district-local (a, b); the catalog (Districts) gives the viewer page its index and camera targets.
    /// </summary>
    public class KitchenSink
    {
        public const int Blue = 0, Red = 1, Green = 2, Yellow = 3, TeamCount = 4;

        public class Exhibit { public string Name, States; public Vec2 Focus; public float Zoom; }
        public class District
        {
            public string Id, Name, About;
            public Vec2 Focus;
            public float Zoom;
            public float A, B;   // bottom-left corner in screen space
            public readonly List<Exhibit> Items = new List<Exhibit>();
        }

        public readonly List<District> Districts = new List<District>();

        /// <summary>What the states on show mean, for the viewer's legend.</summary>
        public static readonly (string term, string meaning)[] Legend =
        {
            ("Blueberry / Cherry", "the two team colours side by side (Lime is the low-power base, Lemon the works yard)"),
            ("Smouldering (45% HP)", "grey wisps off the roof, a dull orange glow, the odd ember"),
            ("Standing fire (25% HP)", "tongues of flame from fixed roof spots, darker smoke, flickering ground light"),
            ("Raging (8% HP)", "three flame spots, a thick black column, embers streaming downwind"),
            ("Health bar", "amber below 50%, hazard stripes below 25%; a cream ruler is construction progress"),
            ("Fuel bar", "cyan; amber below 30%; hazard stripes when stranded"),
            ("Queued site", "staked out with a pallet of bricks, waiting for the command center's crane"),
            ("Stages 0-3", "the structure rises out of its pad; dust kicks out at each stage landing"),
            ("Low power", "power plant cores flicker at 4 Hz and drop out; consumers' lamps at half, machinery at half speed"),
            ("Roll-out", "amber lamps blink in the last 1.5 s, three vent puffs, the door rolls up, the unit eases out of a dark bay"),
            ("Docking", "approach, queue beside the lane, align, reverse in, tip the load (ore-coloured pour), pull out"),
            ("Invented badge", "the small amber badge at a bar's left end marks an agent-invented design"),
            ("Wreck", "a destroyed vehicle's hull darkens and lingers about 20 s"),
        };

        readonly World w;
        readonly List<Action> holds = new List<Action>();
        readonly Dictionary<int, (Vec2 pos, float facing)> pins = new Dictionary<int, (Vec2, float)>();
        readonly HashSet<int> fuelExempt = new HashSet<int>();
        District cur;
        Vec2 origin; // map position of screen (0, 0)

        // At yaw 45 screen-right is map (+x, -y) and screen-up is map (+x, +y).
        static readonly Vec2 Right = new Vec2(0.70710678f, -0.70710678f), Up = new Vec2(0.70710678f, 0.70710678f);
        // Sim facings (radians, map space): toward the viewer (down-right on screen), along the screen, up the screen.
        const float Toward = -MathF.PI / 2, AlongRight = -MathF.PI / 4, AlongLeft = 3 * MathF.PI / 4;

        // Screen-space extent of the whole layout (every district fits inside), and the map margin around it.
        const float A0 = 0, A1 = 156, B0 = 0, B1 = 148, Margin = 6;

        KitchenSink(World w) { this.w = w; }

        /// <summary>Build the showcase world: map, teams, every exhibit, and the hold that keeps them.</summary>
        public static World Build()
        {
            const float r2 = 1.41421356f;
            int size = (int)MathF.Ceiling((A1 - A0 + B1 - B0) / r2 + 2 * Margin);
            var map = new Map(size, size);
            var w = World.Bare(map, TeamCount);
            var k = new KitchenSink(w);
            k.origin = new Vec2(Margin - (A0 + B0) / r2, Margin - (B0 - A1) / r2);
            map.Spawns.Add(new Vec2(size / 2f, size / 2f));
            k.Lay();
            w.Showcase = k;
            // Everyone is protected for good: nothing picks fights or takes damage, mines stay quiet. (Protection's dome
            // is drawn at a team's start position, which for these teams is off the map.)
            foreach (var t in w.Teams) t.ProtectedUntil = float.MaxValue;
            k.Stock();
            w.RefreshPower();
            w.UpdateVisibility();
            return w;
        }

        Vec2 S(float a, float b) => origin + Right * a + Up * b;
        Vec2 P(float a, float b) => S(cur.A + a, cur.B + b);

        void Lay()
        {
            Structures();
            Works();
            Economy();
            Power();
            Fires();
            Units();
            Combat();
            Motion();
            Deep();
            Air();
            Wrecks();
            Ground();
        }

        // ------------------------------------------------------------------ building blocks

        /// <summary>
        /// A new district: its rectangle in screen space (bottom-left a, b; size wide x tall). Its camera zoom fits the
        /// rectangle on a 16:9 screen (the zoom is the view's half-height in tiles, and the ground is foreshortened by
        /// the camera's 55 degree pitch). Districts sit 12 tiles apart, so a framed district shows little of its neighbours.
        /// </summary>
        District Begin(string id, string name, string about, float a, float b, float wide, float tall)
        {
            float zoom = Math.Clamp(MathF.Max((wide + 4) / 3.56f, (tall + 4) / 2.44f), 8f, 40f);
            cur = new District { Id = id, Name = name, About = about, A = a, B = b, Zoom = MathF.Round(zoom, 1), Focus = S(a + wide / 2, b + tall / 2) };
            Districts.Add(cur);
            return cur;
        }

        void Item(string name, string states, float a, float b, float zoom = 8f) =>
            cur.Items.Add(new Exhibit { Name = name, States = states, Focus = P(a, b), Zoom = zoom });

        Entity Bldg(int team, string key, float a, float b, float progress = 1f) => w.PlaceStructure(team, key, Spot(key, a, b), progress);

        /// <summary>The footprint origin that centres a structure on a screen point (checked clear).</summary>
        Int2 Spot(string key, float a, float b)
        {
            var def = w.Def(key);
            var c = P(a, b);
            var o = new Int2((int)MathF.Round(c.X - def.SizeX / 2f), (int)MathF.Round(c.Y - def.SizeY / 2f));
            Room(key, o);
            return o;
        }

        /// <summary>Throws if a structure's footprint at o isn't clear, open ground (a layout mistake, caught by the tests).</summary>
        void Room(string key, Int2 o)
        {
            var def = w.Def(key);
            for (int y = 0; y < def.SizeY; y++)
                for (int x = 0; x < def.SizeX; x++)
                {
                    int tx = o.X + x, ty = o.Y + y;
                    if (!w.Map.InBounds(tx, ty) || w.Map.Occupant[w.Map.Idx(tx, ty)] != 0 || !w.Map.TerrainPassable(tx, ty) || w.Map.Ore[w.Map.Idx(tx, ty)] > 0)
                        throw new InvalidOperationException($"kitchen sink: no room for {key} at {o} in {cur.Id}");
                }
        }

        Entity Unit(int team, string key, float a, float b, float facing = Toward, bool pin = true)
        {
            var p = P(a, b);
            var t = Int2.Of(p);
            if (!w.Map.InBounds(t.X, t.Y)) throw new InvalidOperationException($"kitchen sink: {key} off the map in {cur.Id}");
            var e = w.PlaceUnit(team, key, p, facing);
            if (pin) pins[e.Id] = (p, facing);
            return e;
        }

        /// <summary>A blob of ore around a screen point; returns its tiles and their starting amounts.</summary>
        List<(int idx, int ore)> OreField(float a, float b, int type, float r, int amount)
        {
            var c = P(a, b);
            var tiles = new List<(int, int)>();
            int ri = (int)MathF.Ceiling(r);
            for (int y = (int)c.Y - ri; y <= (int)c.Y + ri; y++)
                for (int x = (int)c.X - ri; x <= (int)c.X + ri; x++)
                {
                    if (!w.Map.InBounds(x, y)) continue;
                    float d = Vec2.Dist(new Vec2(x + 0.5f, y + 0.5f), c);
                    if (d > r) continue;
                    int i = w.Map.Idx(x, y);
                    w.Map.Tiles[i] = Terrain.Dirt;
                    w.Map.OreType[i] = (byte)type;
                    w.Map.Ore[i] = (int)(amount * (1.15f - 0.35f * d / r));
                    tiles.Add((i, w.Map.Ore[i]));
                }
            return tiles;
        }

        /// <summary>Remove all ore within r tiles of a map point.</summary>
        void ClearOre(Vec2 at, int r)
        {
            for (int y = (int)at.Y - r; y <= (int)at.Y + r; y++)
                for (int x = (int)at.X - r; x <= (int)at.X + r; x++)
                    if (w.Map.InBounds(x, y)) w.Map.Ore[w.Map.Idx(x, y)] = 0;
        }

        void Patch(float a, float b, float r, Terrain t)
        {
            var c = P(a, b);
            int ri = (int)MathF.Ceiling(r);
            for (int y = (int)c.Y - ri; y <= (int)c.Y + ri; y++)
                for (int x = (int)c.X - ri; x <= (int)c.X + ri; x++)
                    if (w.Map.InBounds(x, y) && Vec2.Dist(new Vec2(x + 0.5f, y + 0.5f), c) <= r) w.Map.Tiles[w.Map.Idx(x, y)] = t;
        }

        DeepDeposit Deposit(float a, float b, int type, float fraction, int surveyedBy = Blue)
        {
            var c = P(a, b);
            var at = Int2.Of(c);
            float initial = type <= Map.Copper ? 6000 : 3000;
            var d = new DeepDeposit { Id = w.Map.Deep.Count + 1, Pos = at.Center, Type = type, Initial = initial, Amount = initial * fraction };
            w.Map.Deep.Add(d);
            if (surveyedBy >= 0)
            {
                var t = w.Teams[surveyedBy];
                t.Surveyed.Add(d.Id);
                t.Zones[d.Id] = new ZoneFlag { ZoneId = d.Id, FlaggedBy = 0, FlaggedAt = 0 };
            }
            return d;
        }

        /// <summary>A deep mine on a deposit at a screen point, its reserve held at `fraction` (0 = exhausted).</summary>
        Entity DeepMineOn(int team, float a, float b, int type, float fraction)
        {
            var d = Deposit(a, b, type, fraction, team);
            // A 2x2 mine's origin is the deposit tile ((SizeX - 1) / 2 = 0 back), as Deploy places it.
            var o = Int2.Of(d.Pos);
            Room("deep_mine", o);
            var m = w.PlaceStructure(team, "deep_mine", o, 1f);
            m.DepositId = d.Id; d.MineId = m.Id;
            holds.Add(() => d.Amount = d.Initial * fraction);
            return m;
        }

        float T => w.Time;

        /// <summary>Keep every stockpile full (converters keep working, the fusion reactors keep burning).</summary>
        void Stock()
        {
            foreach (var t in w.Teams)
                foreach (var item in Defs.Items)
                    if (t.Amount(item) < 4000) t.Stock[item] = 5000;
        }

        // ------------------------------------------------------------------ the hold

        /// <summary>Run after every tick (World.Step): put every exhibit back in its state and advance the looping demos.</summary>
        public void Hold()
        {
            if (w.Tick % World.TickRate == 0) Stock();
            // One exhibit going wrong must not stop the others: each runs on its own, and the first failure is rethrown
            // at the end (World.Step's guard records it once).
            Exception failed = null;
            foreach (var h in holds)
                try { h(); } catch (Exception ex) { failed ??= ex; }
            foreach (var kv in pins)
            {
                var e = w.Get(kv.Key);
                if (e == null || e.IsCarried) continue;
                var (p, f) = kv.Value;
                if (e.Order != Order.Idle) w.SetOrder(e, Order.Idle, p);
                e.Pos = e.PrevPos = e.GuardPos = p;
                e.Facing = f;
                e.Path = null;
                e.Moving = false;
            }
            foreach (var e in w.Entities)
                if (!e.Dead && !e.IsStructure && e.Def.UsesFuel && !fuelExempt.Contains(e.Id)) { e.Fuel = e.FuelMax; e.Stranded = false; }
            if (failed != null) throw failed;
        }

        // ------------------------------------------------------------------ districts

        static readonly string[] BaseRow = { "command_center", "power_plant", "mining_refinery", "barracks", "factory", "airfield", "fusion_reactor", "outpost", "deep_mine" };
        static readonly string[] TechRow = { "electronics_plant", "radar_dome", "optics_lab", "enrichment_plant", "composite_foundry", "gun_turret", "sam_site", "laser_tower" };

        void Structures()
        {
            Begin("structures", "Structures", "Every building, complete and powered, Blueberry above Cherry.", 19, 120, 58, 28);
            void Row(string[] keys, float b, string group)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    float a = 4 + 6 * i;
                    foreach (var (team, db) in new[] { (Blue, 6f), (Red, 0f) })
                    {
                        if (keys[i] == "deep_mine") DeepMineOn(team, a, b + db, team == Blue ? Map.Iron : Map.Crystal, 0.75f);
                        else Bldg(team, keys[i], a, b + db);
                    }
                    Item($"{Pretty(keys[i])}", $"{group}: complete, powered, idle; both teams", a, b + 3, 8);
                }
            }
            Row(BaseRow, 15, "base");
            Row(TechRow, 2, "tech and defence");
        }

        void Works()
        {
            Begin("works", "Works: construction and production", "Lemon's yard: the crane builds, factories roll units out on a loop.", 89, 120, 48, 22);
            var cc = Bldg(Yellow, "command_center", 4, 15);
            Item("Command center crane", "slews to the site under construction; cream beam and welding sparks every 0.25 s", 4, 15);

            // The site being built: rises from a fresh stake-out to 97% over 24 s, then is cleared and staked again.
            var team = w.Teams[Yellow];
            Entity site = null; ProdItem head = null; float started = 0;
            var siteAt = Spot("barracks", 10.5f, 15); // closures run later, after cur has moved on: positions are taken now
            void NewSite()
            {
                site = w.PlaceStructure(Yellow, "barracks", siteAt, 0f);
                head = new ProdItem { Key = "barracks", StructureId = site.Id };
                team.StructureQueue.Insert(0, head);
                started = T;
            }
            NewSite();
            holds.Add(() =>
            {
                float age = T - started;
                if (age >= 28f) { w.Remove(site); NewSite(); age = 0; }
                float p = Math.Clamp((age - 2f) / 24f, 0f, 0.97f);
                site.BuildProgress = p;
                head.Progress = p * site.Def.BuildTime;
                site.Hp = site.Def.MaxHp * (0.1f + 0.9f * p);
                if (team.StructureQueue.Count == 0 || team.StructureQueue[0] != head) { team.StructureQueue.Remove(head); team.StructureQueue.Insert(0, head); }
            });
            Item("Site under construction", "staked, then rising through stages 0-3 with stage dust; loops every 28 s", 10.5f, 15);

            Bldg(Yellow, "power_plant", 16.5f, 15, 0f);
            Item("Queued site", "staked out with its pallet of bricks, waiting in the queue", 16.5f, 15);
            float[] stages = { 0.10f, 0.45f, 0.75f, 0.94f }; // where stage 0, 1, 2 and 3 have just landed (PezEmerge.StageWindows)
            for (int i = 0; i < stages.Length; i++)
            {
                var s = Bldg(Yellow, "factory", 23 + 6 * i, 15, stages[i]);
                float p = stages[i];
                holds.Add(() => { s.BuildProgress = p; s.Hp = s.Def.MaxHp * (0.1f + 0.9f * p); });
                Item($"Factory at stage {i}", $"held at {(int)MathF.Round(p * 100)}% built: stage {i} has landed", 23 + 6 * i, 15);
            }

            // Production: each producer's queue never empties; every unit rolls out about 5 s after the last.
            Produce(Bldg(Yellow, "factory", 4, 4), Producer.Factory, new[] { "light_tank", "heavy_tank", "artillery", "scout_buggy", "apc", "flak_track", "repair_truck", "laser_tank", "mammoth_tank", "geological_surveyor", "drill_rig", "minelayer", "outpost_truck" });
            Item("Factory roll-out", "lamps blink, vents puff, door rolls up, the unit eases out of the dark bay; looping", 4, 4);
            Produce(Bldg(Yellow, "barracks", 11, 4), Producer.Barracks, new[] { "rifleman", "rocket_soldier", "medic", "laser_trooper", "engineer", "sniper", "commando" });
            Item("Barracks roll-out", "lamps, vent and door as infantry march out; looping", 11, 4);
            Produce(Bldg(Yellow, "airfield", 18, 4), Producer.Airfield, new[] { "gunship", "transport_chopper", "stealth_bomber", "long_range_drone", "reaper_drone" });
            Item("Airfield launch", "the lift raises each new aircraft; looping", 18, 4);
            Bldg(Yellow, "power_plant", 25, 4);
            Bldg(Yellow, "power_plant", 31, 4);
            Item("Lemon's power plants", "powered: steady cores and steam under load", 28, 4);

            // Units the works produce drive (or fly) to their rally point and are cleared away after a few seconds.
            var born = new Dictionary<int, float>();
            var keep = new HashSet<int>();
            holds.Add(() =>
            {
                foreach (var e in w.Entities)
                {
                    if (e.Dead || e.Team != Yellow || e.IsStructure) continue;
                    if (!born.TryGetValue(e.Id, out var at)) { born[e.Id] = T; continue; }
                    if (T - at > 7f) keep.Add(e.Id);
                }
                foreach (var id in keep) { var e = w.Get(id); if (e != null) w.Remove(e); born.Remove(id); }
                keep.Clear();
            });
        }

        void Produce(Entity building, Producer kind, string[] cycle)
        {
            // Rally a few tiles out from the door (south of the footprint, toward the viewer).
            building.Rally = building.Center + new Vec2(0.5f, -4.5f);
            var q = w.Teams[building.Team].UnitQueues[kind];
            int next = 0;
            holds.Add(() =>
            {
                if (q.Count > 0) return;
                var key = cycle[next++ % cycle.Length];
                q.Add(new ProdItem { Key = key, Progress = MathF.Max(0f, w.Def(key).BuildTime - 5f) });
            });
        }

        void Economy()
        {
            Begin("economy", "Economy loop", "Trucks harvest each ore and dock at each kind of drop-off: queue, line up, reverse, tip, pull out.", 0, 80, 60, 18);
            Loop(Blue, "mining_refinery", Map.Iron, 6, "Iron ore into a refinery");
            Loop(Red, "mining_refinery", Map.Copper, 20, "Copper ore into a refinery");
            Loop(Blue, "outpost", Map.Crystal, 34, "Crystal into an outpost");
            Loop(Red, "command_center", Map.Uranium, 48, "Uranium into a command center");

            Unit(Blue, "mining_truck", 57, 12);
            Unit(Red, "mining_truck", 57, 7);
            Item("Idle trucks", "parked and powered down (nothing left to mine)", 57, 9.5f);
        }

        /// <summary>A drop-off, an ore field below it on screen and three trucks working it.</summary>
        void Loop(int team, string dropOff, int ore, float a, string name)
        {
            var d = Bldg(team, dropOff, a, 13);
            var field = OreField(a + 3.5f, 4, ore, 2.3f, 420);
            for (int i = 0; i < 3; i++)
            {
                var t = Unit(team, "mining_truck", a + 1.5f + 2f * i, 1.5f, Toward, pin: false);
                var (idx, _) = field[(i * 5) % field.Count];
                w.SetOrder(t, Order.Harvest, new Vec2(idx % w.Map.W + 0.5f, idx / w.Map.W + 0.5f));
            }
            // The field never runs out.
            holds.Add(() =>
            {
                if (w.Tick % World.TickRate != 0) return;
                foreach (var (idx, amount) in field) { w.Map.Ore[idx] = amount; w.Map.OreType[idx] = (byte)ore; }
            });
            Item(name, "harvest, queue, align, reverse, unload with an ore-coloured pour, pull out", a + 1.5f, 8.5f, 9);
        }

        void Power()
        {
            Begin("power", "Power", "Blueberry is powered; Lime's base draws more than its plant makes.", 72, 80, 40, 16);
            string[] row = { "power_plant", "barracks", "electronics_plant", "radar_dome", "optics_lab" };
            for (int i = 0; i < row.Length; i++)
            {
                Bldg(Blue, row[i], 4 + 6 * i, 10);
                Bldg(Green, row[i], 4 + 6 * i, 3);
            }
            Bldg(Blue, "fusion_reactor", 34, 10);
            Bldg(Green, "command_center", 34, 3);
            Item("Powered (Blueberry)", "power plant cores glow with the load, steady steam; consumers at full light and speed", 16, 10, 9);
            Item("Low power (Lime)", "plant cores flicker at 4 Hz with dropouts, steam sputters; consumers dimmed to half, machinery at half speed", 16, 3, 9);
        }

        void Fires()
        {
            Begin("fires", "Fires and damage", "Each building at full health, smouldering, standing fire and raging. Smoke and embers drift with the wind.", 124, 80, 30, 28);
            string[] keys = { "power_plant", "barracks", "factory" };
            float[] hp = { 1f, 0.45f, 0.25f, 0.08f };
            string[] state = { "full health", "smouldering (45%)", "standing fire (25%)", "raging (8%)" };
            for (int r = 0; r < keys.Length; r++)
                for (int c = 0; c < hp.Length; c++)
                {
                    var e = Bldg(Red, keys[r], 4 + 7 * c, 3 + 8 * r);
                    float f = hp[c];
                    holds.Add(() => e.Hp = e.Def.MaxHp * f);
                }
            for (int c = 0; c < hp.Length; c++) Item($"{state[c][0].ToString().ToUpperInvariant()}{state[c].Substring(1)}", $"power plant, barracks and factory at {MathF.Round(hp[c] * 100)}% health", 4 + 7 * c, 11, 11);
            Item("Wind", "smoke columns and embers lean and drift downwind", 14, 18, 14);
        }

        static readonly string[] Infantry = { "rifleman", "rocket_soldier", "medic", "laser_trooper", "engineer", "sniper", "commando", "mine" };
        static readonly string[] Vehicles = { "mining_truck", "scout_buggy", "light_tank", "apc", "flak_track", "repair_truck", "minelayer", "outpost_truck", "geological_surveyor", "drill_rig" };
        static readonly string[] Heavy = { "heavy_tank", "artillery", "laser_tank", "mammoth_tank" };
        static readonly string[] Aircraft = { "recon_drone", "long_range_drone", "reaper_drone", "transport_chopper", "gunship", "stealth_bomber" };

        void Units()
        {
            Begin("units", "Units", "Every unit idle, Blueberry above Cherry; the last heavy is an agent-invented design with its badge.", 0, 34, 34, 28);
            void Row(IList<string> keys, float b, string group, float pair = 2.6f)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    float a = 2 + 3 * i;
                    Unit(Blue, keys[i], a, b + pair);
                    Unit(Red, keys[i], a, b);
                }
                // Aircraft hover about 1.5 tiles up the screen from where they stand: frame them there.
                Item(group, $"{keys.Count} kinds, idle; Blueberry above Cherry", 2 + 1.5f * (keys.Count - 1), b + pair / 2 + (pair > 3 ? 1.5f : 0), 8);
            }
            var hornet = Invent(Blue, "hornet", "Hornet", "scout_buggy", "rocket_soldier");
            var lancer = Invent(Red, "lancer", "Lancer", "light_tank", "laser_tank");
            Row(Infantry, 23f, "Infantry and the mine");
            Row(Vehicles, 16.5f, "Vehicles");
            Unit(Blue, hornet.Def.Key, 2 + 3 * Heavy.Length, 10f + 2.6f);
            Unit(Red, lancer.Def.Key, 2 + 3 * Heavy.Length, 10f);
            Row(Heavy, 10f, "Heavy armour");
            Item("Invented units (Hornet, Lancer)", "agent-invented designs: their chassis model plus the amber badge", 2 + 3 * Heavy.Length, 11.3f, 8);
            Row(Aircraft, 0.5f, "Aircraft (hovering)", 3.6f);
        }

        /// <summary>An agent-invented design, already researched: a chassis carrying a donor's weapon.</summary>
        Invention Invent(int team, string name, string display, string chassis, string donor)
        {
            var ch = Defs.Get(chassis); var dn = Defs.Get(donor);
            var wp = Tech.MakeWeapon(dn.Weapon, dn.Weapon.Damage, MathF.Min(dn.Weapon.Range, 6.5f), dn.Weapon.Cooldown);
            var def = Tech.MakeDef(ch, dn, team, $"t{team}:{name}", display, wp, ch.MaxHp, ch.Speed, MathF.Max(ch.Sight, wp.Range + 1), ch.Fuel);
            def.Cost = new Dictionary<string, int>(ch.Cost);
            def.BuildTime = ch.BuildTime;
            def.Description = $"Kitchen sink sample invention: a {Pretty(chassis)} with a {dn.Weapon.Name}.";
            var inv = new Invention
            {
                Key = def.Key, Name = display, Team = team, Chassis = chassis, WeaponFrom = donor, Def = def,
                ResearchCost = new Dictionary<string, int>(), ResearchTime = 1, Progress = 1, ResearchedAt = 0, Summary = Tech.Spec(def),
            };
            w.Inventions[def.Key] = inv;
            return inv;
        }

        void Combat()
        {
            Begin("combat", "Combat range", "Every weapon firing at a durable target (nobody takes damage here).", 46, 34, 34, 34);
            // Column one: direct fire at 80% of range.
            var lanes1 = new (string shooter, string target, string name)[]
            {
                ("rifleman", "rifleman", "Rifle"), ("scout_buggy", "light_tank", "Machine gun"), ("rocket_soldier", "light_tank", "Rockets"),
                ("laser_trooper", "light_tank", "Laser"), ("light_tank", "light_tank", "Light cannon"), ("heavy_tank", "heavy_tank", "Heavy cannon"),
                ("laser_tank", "heavy_tank", "Beam cannon"), ("mammoth_tank", "heavy_tank", "Mammoth twin cannon"),
            };
            for (int j = 0; j < lanes1.Length; j++)
            {
                var (sk, tk, name) = lanes1[j];
                float b = 2 + 3.5f * j;
                float range = Defs.Get(sk).Weapon.Range * 0.8f;
                Lane(sk, tk, 2, b, range, name);
            }
            // Column two: indirect fire, specialists, anti-air, aircraft and defences.
            float a0 = 18;
            Lane("artillery", "power_plant", a0, 2, 9.5f, "Artillery (range 11, lobbed shells)", 13);
            Unit(Blue, "recon_drone", a0 + 5, 4); // a spotter: artillery needs someone else to see its target
            Lane("sniper", "rifleman", a0, 6, 7.2f, "Sniper");
            Lane("commando", "barracks", a0, 10, 2.3f, "Commando C4");
            Lane("flak_track", "recon_drone", a0, 14, 6.2f, "Flak (air bursts)");
            Lane("gunship", "light_tank", a0, 18, 4.4f, "Gunship rockets");
            Lane("stealth_bomber", "outpost", a0, 22.5f, 0f, "Stealth bomber (bombs)");
            Lane("gun_turret", "light_tank", a0, 26, 5f, "Gun turret");
            Lane("sam_site", "gunship", a0, 29, 6f, "SAM site");
            Lane("laser_tower", "heavy_tank", a0, 32, 6f, "Laser tower");
            Lane("reaper_drone", "light_tank", a0 + 14, 2, 5.5f, "Reaper hellfire (flies high)");
        }

        /// <summary>A Blueberry shooter at (a, b) and a Cherry target `dist` to its right, fired on forever.</summary>
        void Lane(string shooterKey, string targetKey, float a, float b, float dist, string name, float zoom = 8f)
        {
            var tdef = Defs.Get(targetKey);
            var target = tdef.IsStructure ? Bldg(Red, targetKey, a + dist + 1.4f, b) : Unit(Red, targetKey, a + dist, b, AlongLeft);
            var sdef = Defs.Get(shooterKey);
            Entity shooter = sdef.IsStructure ? Bldg(Blue, shooterKey, a, b) : Unit(Blue, shooterKey, a, b, AlongRight, pin: false);
            if (sdef.IsStructure) holds.Add(() => { if (!shooter.Dead && !target.Dead) w.FireFor(shooter, target); });
            else
                holds.Add(() =>
                {
                    if (shooter.Dead || target.Dead) return;
                    if (shooter.Order != Order.Attack || shooter.TargetId != target.Id) w.SetOrder(shooter, Order.Attack, target.Center, target.Id);
                });
            Item(name, $"{Pretty(shooterKey)} firing at a {Pretty(targetKey)}", a + dist / 2, b, zoom);
        }

        void Motion()
        {
            Begin("motion", "Units in motion", "Each weight class driving back and forth: accelerate, brake, turn.", 92, 34, 22, 24);
            var lanes = new (string key, string name)[]
            {
                ("rifleman", "Infantry walk"), ("scout_buggy", "Light and fast (buggy)"), ("light_tank", "Light hull (bobs twice)"),
                ("heavy_tank", "Heavy hull (one slow rock, start squat)"), ("mammoth_tank", "Super-heavy"), ("artillery", "Artillery"),
                ("mining_truck", "Laden mining truck"), ("apc", "APC"),
            };
            for (int j = 0; j < lanes.Length; j++)
            {
                var (key, name) = lanes[j];
                float b = 2 + 2.8f * j;
                var u = Unit(Blue, key, 2, b, AlongRight, pin: false);
                if (u.IsHarvester) { u.Cargo = u.Def.HarvestCapacity; u.CargoType = Map.Iron; }
                var from = P(2, b); var to = P(18, b);
                void Go() { w.SetOrder(u, Order.Move, to); u.Waypoints.Add(from); u.WaypointLoop = true; }
                Go();
                holds.Add(() => { if (!u.Dead && u.Order != Order.Move) Go(); if (u.IsHarvester) u.Cargo = u.Def.HarvestCapacity; });
                Item(name, "moving back and forth", 10, b, 8);
            }
        }

        void Deep()
        {
            Begin("deep", "Deep mining", "Deep deposits with their zone stakes, the surveyor, the drill rig and deep mines.", 126, 34, 30, 24);
            string[] ores = { "Iron", "Copper", "Crystal", "Uranium" };
            for (int i = 0; i < 4; i++) Deposit(4 + 6 * i, 18, i, 1f);
            Item("Deep deposits, one per ore", "x-ray veins, three richness rings, a flagged zone stake", 13, 18, 9);
            Deposit(4, 10.5f, Map.Iron, 0.62f);
            Deposit(10, 10.5f, Map.Iron, 0.3f);
            Item("Deposits running down", "two rings left, then one (a ring fades per third of the reserve)", 7, 10.5f);
            DeepMineOn(Blue, 16, 10.5f, Map.Copper, 0.6f);
            Item("Deep mine working", "sheave turning, ore tube tinted copper at 60%", 16, 10.5f);
            DeepMineOn(Blue, 22, 10.5f, Map.Uranium, 0f);
            Item("Deep mine exhausted", "deposit dry: tube empty, sheave still, no veins", 22, 10.5f);

            // The surveyor drives between two spots and surveys each (thumps and ground ripples) in turn.
            var sv = Unit(Blue, "geological_surveyor", 3, 3, AlongRight, pin: false);
            Vec2[] spots = { P(3, 3), P(9, 3) };
            int s = 1;
            holds.Add(() => { if (!sv.Dead && sv.Order != Order.Survey) w.SetOrder(sv, Order.Survey, spots[s++ % 2]); });
            Item("Surveyor surveying", "drives to a spot, stops and thumps for 8 s (ripples), moves on", 6, 3);

            // The drill rig drives onto its zone and deploys into a deep mine; 14 s later the mine is cleared and a new rig rolls up.
            var zone = Deposit(23, 3, Map.Crystal, 0.8f);
            holds.Add(() => zone.Amount = zone.Initial * 0.8f);
            Entity rig = null, mine = null; float mineAt = 0, respawn = 0;
            var rigFrom = P(16, 2);
            holds.Add(() =>
            {
                if (rig != null && rig.Dead) { rig = null; mine = w.Get(zone.MineId); mineAt = T; }
                if (mine != null && (mine.Dead || T - mineAt > 14f)) { if (!mine.Dead) w.Remove(mine); zone.MineId = 0; mine = null; respawn = T + 2f; }
                if (rig == null && mine == null && T >= respawn)
                {
                    rig = w.PlaceUnit(Blue, "drill_rig", rigFrom, AlongRight);
                    w.SetOrder(rig, Order.Drill, zone.Pos);
                    rig.ZoneId = zone.Id;
                }
                else if (rig != null && rig.Order != Order.Drill && !rig.Dead) { w.SetOrder(rig, Order.Drill, zone.Pos); rig.ZoneId = zone.Id; }
            });
            Item("Drill rig drilling", "drives onto its zone, the mast stands up and it deploys into a deep mine; loops", 19.5f, 3);
        }

        void Air()
        {
            Begin("air", "Air and logistics", "Aircraft flying, landing and refuelling; transports loading; a stranded tank.", 0, 0, 46, 22);
            // A patrol circuit with four aircraft spaced around it.
            Vec2[] box = { P(3, 11), P(20, 11), P(20, 18), P(3, 18) };
            string[] flyers = { "recon_drone", "gunship", "transport_chopper", "stealth_bomber", "long_range_drone", "reaper_drone" };
            for (int i = 0; i < flyers.Length; i++)
            {
                int start = i % 4;
                // Six aircraft on four corners: the last two start halfway along the first two legs.
                var a = w.PlaceUnit(Blue, flyers[i], i < 4 ? box[i] : Vec2.Lerp(box[start], box[(start + 1) % 4], 0.5f), AlongRight);
                void Fly()
                {
                    w.SetOrder(a, Order.Move, box[(start + 1) % 4]);
                    for (int k = 2; k <= 4; k++) a.Waypoints.Add(box[(start + k) % 4]);
                    a.WaypointLoop = true;
                }
                Fly();
                holds.Add(() => { if (!a.Dead && a.Order != Order.Move) Fly(); });
            }
            Item("Aircraft flying", "drones (light, long-range, the high-flying Reaper), gunship, chopper and bomber on a circuit: banking into turns", 11.5f, 14.5f, 9);

            // An airfield and a gunship that flies a short beat, then lands on the pad, refuels and takes off again.
            var field = Bldg(Blue, "airfield", 32, 15);
            var g = w.PlaceUnit(Blue, "gunship", P(26, 7), AlongRight);
            fuelExempt.Add(g.Id);
            Vec2 g1 = P(26, 7), g2 = P(40, 7);
            void Beat() { w.SetOrder(g, Order.Move, g2); g.Waypoints.Add(g1); g.WaypointLoop = true; }
            Beat();
            float lastRefuel = 0;
            holds.Add(() =>
            {
                if (g.Dead) return;
                if (g.Order == Order.Idle) Beat();
                if (g.Order == Order.Move && T - lastRefuel > 26f) { g.Fuel = g.Def.Fuel * 0.15f; w.BeginRefuel(g, field); lastRefuel = T; }
                if (g.Order == Order.Move && g.Fuel < g.Def.Fuel * 0.6f) g.Fuel = g.Def.Fuel * 0.6f; // never runs dry on the beat
            });
            Item("Landing and refuelling", "the gunship flies a beat, lands on the airfield, refuels (fuel bar fills), lifts off; every 26 s", 31, 11, 9);

            Transport("apc", "rifleman", 4, 4, "APC loading");
            Transport("transport_chopper", "rocket_soldier", 18, 4, "Transport chopper loading");

            var stranded = Unit(Blue, "light_tank", 42, 2);
            fuelExempt.Add(stranded.Id);
            holds.Add(() => { stranded.Fuel = 0; stranded.Stranded = true; });
            Item("Stranded vehicle", "out of fuel: hazard-striped fuel bar, can't move", 42, 2);
        }

        /// <summary>Infantry walk up and board a transport, ride 3 s, get out, walk back; forever.</summary>
        void Transport(string carrierKey, string paxKey, float a, float b, string name)
        {
            var carrier = Unit(Blue, carrierKey, a, b, AlongRight);
            var pax = new List<Entity>();
            var muster = new List<Vec2>();
            for (int i = 0; i < 4; i++)
            {
                muster.Add(P(a + 4 + 1.2f * i, b - 2.5f));
                pax.Add(Unit(Blue, paxKey, a + 4 + 1.2f * i, b - 2.5f, AlongLeft, pin: false));
            }
            int phase = 0; float since = 0;
            holds.Add(() =>
            {
                if (carrier.Dead) return;
                switch (phase)
                {
                    case 0: // boarding
                        foreach (var p in pax) if (!p.Dead && !p.IsCarried && p.Order != Order.Board) w.SetOrder(p, Order.Board, carrier.Pos, carrier.Id);
                        if (pax.All(p => p.Dead || p.IsCarried)) { phase = 1; since = T; }
                        else if (T - since > 25f) { phase = 2; since = T; } // stuck: start over
                        break;
                    case 1: // riding
                        if (T - since > 3f) { w.Unload(carrier); phase = 2; since = T; for (int i = 0; i < pax.Count; i++) if (!pax[i].Dead) w.SetOrder(pax[i], Order.Move, muster[i]); }
                        break;
                    case 2: // walking back out
                        for (int i = 0; i < pax.Count; i++) if (!pax[i].Dead && !pax[i].IsCarried && pax[i].Order == Order.Idle && Vec2.Dist(pax[i].Pos, muster[i]) > 0.6f) w.SetOrder(pax[i], Order.Move, muster[i]);
                        if (T - since > 7f) { phase = 0; since = T; }
                        break;
                }
            });
            Item(name, "infantry walk up and board (shrinking into it), ride, get out and walk back; loops", a + 3, b - 1, 8);
        }

        void Wrecks()
        {
            Begin("wrecks", "Wrecks and explosions", "Vehicles destroyed on a loop (wrecks darken and fade), every explosion recipe, scorch marks.", 58, 0, 38, 16);
            // Four wreck slots: a vehicle stands for 2 s, is destroyed, its wreck lingers, and the slot refills 24 s later.
            string[] kinds = { "light_tank", "heavy_tank", "artillery", "scout_buggy", "mammoth_tank", "apc", "flak_track", "mining_truck" };
            int kind = 0;
            for (int i = 0; i < 4; i++)
            {
                float a = 4 + 5 * i;
                Entity e = null; float phase = i * 6f;
                var at = P(a, 10);
                holds.Add(() =>
                {
                    float c = (T + 24f - phase) % 24f;
                    if (e == null && c < 1f)
                    {
                        int team = kind % 2 == 0 ? Blue : Red;
                        e = w.PlaceUnit(team, kinds[kind++ % kinds.Length], at, Toward);
                    }
                    else if (e != null && c >= 2f && c < 3f)
                    {
                        w.Emit("destroyed", e.Team, e.Id, 0, e.Center, key: e.Def.Key);
                        w.Remove(e);
                        // An enemy kill leaves a quarter of the wreck's cost as ore: the piles grow up out of the ground.
                        w.ShowcaseSalvage(e.Def.Key, at, e.Team == Blue ? Red : Blue, e.Team);
                        e = null;
                    }
                    else if (e == null && c >= 22f) ClearOre(at, 2); // the slot's salvage is cleared before the next wreck
                });
            }
            Item("Wrecks", "destroyed one every 6 s: blast, burning hull, darkening wreck that lingers about 20 s", 11.5f, 10, 9);
            Item("Salvage piles", "each wreck leaves a quarter of its cost as ore piles (iron, copper, uranium) that grow up around it; cleared after 20 s", 11.5f, 8, 9);

            // Explosion pads: one effect every 2 s, in turn, each on its own pad so its scorch builds up there.
            var pads = new (string type, string key, string name)[]
            {
                ("hit", "artillery", "artillery shell"), ("hit", "heavy_cannon", "heavy cannon"), ("hit", "rocket", "rocket"),
                ("hit", "bombs", "bombs"), ("hit", "mine", "mine"), ("shot", "c4", "C4 charge"),
                ("destroyed", "light_tank", "vehicle destroyed"), ("destroyed", "factory", "building destroyed"), ("destroyed", "rifleman", "infantry down"),
            };
            int next = 0; float nextAt = 3f;
            var padAt = Enumerable.Range(0, pads.Length).Select(i => P(3 + 3.8f * i, 3)).ToArray();
            holds.Add(() =>
            {
                if (T < nextAt) return;
                nextAt = T + 2f;
                int i = next++ % pads.Length;
                var (type, key, _) = pads[i];
                var p = padAt[i];
                if (type == "shot") w.Emit(type, Red, 0, 0, p, p, key);
                else w.Emit(type, Red, 0, 0, p, key: key);
            });
            Item("Explosions", string.Join(", ", pads.Select(x => x.name)) + "; one every 2 s, left to right", 18, 3, 10);
            Item("Scorch marks", "each pad's scorch builds up and fades over 75 s", 18, 2, 8);
        }

        void Ground()
        {
            Begin("terrain", "Terrain and ore", "Each ground type and water; ore fields full, depleting and mined out.", 108, 0, 40, 22);
            Patch(5, 15, 3.2f, Terrain.Dirt);
            Patch(13, 15, 3.2f, Terrain.Rock);
            Patch(22, 15, 3.6f, Terrain.Water);
            Item("Grass", "the default ground", 30, 15);
            Item("Dirt", "a dirt patch: deliberately quiet, 2.5% darker than grass (the art pack's quiet-ground rule)", 5, 15);
            Item("Rock", "an impassable outcrop", 13, 15);
            Item("Water", "a lake with its shore", 22, 15);
            string[] ores = { "Iron ore", "Copper ore", "Crystal", "Uranium" };
            for (int i = 0; i < 4; i++)
            {
                OreField(4 + 6 * i, 5, i, 2.2f, 420);
                Item($"{ores[i]} field", "full", 4 + 6 * i, 5);
            }
            // Depleting: runs down over 40 s, lies mined out for 12 s (the stain fades to a scar), then grows back.
            var dep = OreField(28, 5, Map.Iron, 2.2f, 420);
            holds.Add(() =>
            {
                if (w.Tick % 5 != 0) return;
                float c = T % 60f, k = c < 40f ? 1f - c / 40f : c < 52f ? 0f : 1f;
                foreach (var (idx, amount) in dep) w.Map.Ore[idx] = (int)MathF.Ceiling(amount * k);
            });
            Item("Depleting field", "iron running down over 40 s (clusters sink one by one), mined out for 12 s, then regrows", 28, 5);
            // Mined out: the field is there when the view is built and emptied on the first tick, so it shows as a scar.
            var spent = OreField(34, 5, Map.Copper, 2.2f, 420);
            holds.Add(() => { foreach (var (idx, _) in spent) w.Map.Ore[idx] = 0; });
            Item("Mined-out field", "a spent copper field: the stain faded to a scar", 34, 5);
        }

        static string Pretty(string key) => key.Contains(':') ? key : char.ToUpperInvariant(key[0]) + key.Substring(1).Replace('_', ' ');

        // ------------------------------------------------------------------ catalog

        /// <summary>The viewer's index: districts and exhibits with camera targets (map x, y and zoom), plus the legend.</summary>
        public JObj Catalog() => new JObj()
            .Set("map_size", w.Map.W)
            .Set("yaw", 45)
            .Set("districts", Districts.Select(d => (object)new JObj()
                .Set("id", d.Id).Set("name", d.Name).Set("about", d.About)
                .Set("x", R(d.Focus.X)).Set("y", R(d.Focus.Y)).Set("zoom", d.Zoom)
                .Set("items", d.Items.Select(i => (object)new JObj().Set("name", i.Name).Set("states", i.States).Set("x", R(i.Focus.X)).Set("y", R(i.Focus.Y)).Set("zoom", i.Zoom)).ToList())).ToList())
            .Set("legend", Legend.Select(l => (object)new JObj().Set("term", l.term).Set("meaning", l.meaning)).ToList());

        static float R(float v) => MathF.Round(v, 1);
    }
}
