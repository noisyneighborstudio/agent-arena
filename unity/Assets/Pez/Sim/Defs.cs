using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public enum Armor { Infantry, Vehicle, Aircraft, Structure }
    public enum Producer { None, CommandCenter, Barracks, Factory, Airfield }

    public class WeaponDef
    {
        public string Name;
        public float Damage, Range, Cooldown;
        /// <summary>Tiles per second. 0 = hitscan (bullets, lasers).</summary>
        public float ProjectileSpeed;
        public float SplashRadius;
        public bool HitsGround = true, HitsAir;
        public float VsInfantry = 1f, VsVehicle = 1f, VsStructure = 1f, VsAir = 1f;

        public float Multiplier(Armor a) => a switch
        {
            Armor.Infantry => VsInfantry,
            Armor.Vehicle => VsVehicle,
            Armor.Aircraft => VsAir,
            _ => VsStructure,
        };
        /// <summary>Reaches high-altitude aircraft (the Reaper): SAM sites, flak and laser towers only.</summary>
        public bool HitsHighAir;
        public bool CanHit(EntityDef d) => (d.IsAir ? HitsAir && (!d.HighAltitude || HitsHighAir) : HitsGround) && Multiplier(d.Armor) > 0;
    }

    /// <summary>A converter recipe: consumes Inputs to make Outputs, Rate cycles per second.</summary>
    public class Recipe
    {
        public Dictionary<string, int> Inputs, Outputs;
        public float Rate;
        public override string ToString() =>
            $"{string.Join(" + ", Inputs.Select(kv => $"{kv.Value} {kv.Key}"))} -> {string.Join(" + ", Outputs.Select(kv => $"{kv.Value} {kv.Key}"))} ({Rate}/s)";
    }

    public class EntityDef
    {
        public string Key, Name, Description;
        public bool IsStructure;
        public Dictionary<string, int> Cost = new Dictionary<string, int>();
        public float BuildTime;   // seconds
        public int MaxHp;
        public Armor Armor;
        public float Speed;       // tiles/sec (units)
        public float Sight = 6f;
        public float Radius = 0.35f;
        public int SizeX = 1, SizeY = 1; // footprint (structures)
        public int Power;         // + produces, - consumes
        public WeaponDef Weapon;
        public Producer BuiltBy;
        public Producer Produces;
        public string[] Requires = new string[0];
        public int HarvestCapacity;   // mining trucks only
        public float RepairRate;      // HP/s restored: repair trucks fix machines, medics heal infantry
        public bool Medic;            // heals infantry (free) instead of repairing machines (costs steel)
        public int Capacity;          // transports: infantry passengers carried
        public bool Engineer;         // captures damaged enemy structures
        public bool LaysMines;        // mine layers
        public bool IsMine;           // a mine: stationary, hidden, explodes under enemy ground units
        public float SelfRepairTo;    // regenerates up to this fraction of max HP
        /// <summary>Drones: fuel for this many map widths of flight, set from the map when one is built (World.DroneFuel).</summary>
        public float RangeMaps;
        /// <summary>Flies high (the Reaper): only weapons that reach high altitude (WeaponDef.HitsHighAir) can hit it.</summary>
        public bool HighAltitude;
        /// <summary>Drawn with another unit's model (no art of its own yet), at this scale.</summary>
        public string ModelAs;
        public float ModelScale = 1f;
        public float SelfRepairRate;  // HP/s
        public const int MineCost = 30; // steel per mine laid
        public float RepairRange = 1.5f;
        public const float RepairSteelPerHp = 0.1f;
        public bool DropOff;          // trucks can unload ore here
        public float Fuel;            // vehicles and aircraft: seconds of full-speed travel (aircraft burn while airborne); 0 = no fuel
        public bool FuelDepot;        // vehicles refuel next to it
        public bool Helipad;          // aircraft land and refuel here
        public const float RefuelSeconds = 10f;  // empty to full at a depot, pad or tanker
        public const float DeepMineRate = 4f;    // ore per second from a deep mine
        public const float SurveyRadius = 12f, SurveySeconds = 8f;
        public bool UsesFuel => Fuel > 0;
        public Recipe[] Recipes = new Recipe[0];
        public string DeploysInto;    // e.g. outpost_truck -> outpost
        public bool IsAir, Stealth;
        public bool Buildable = true; // false for structures you can't place from the build menu
        /// <summary>Agent inventions (Invention.cs): the standard unit this was derived from, and the only team that may build it.</summary>
        public string Chassis;
        public int OwnerTeam = -1;
        /// <summary>The standard def whose model, icon and animation profile to use (an invention looks like its chassis).</summary>
        public string ModelKey => Chassis ?? ModelAs ?? Key;
        public EntityDef Clone() => (EntityDef)MemberwiseClone();

        public bool IsArmor(Armor a) => Armor == a;
        public string CostText => Cost.Count == 0 ? "free" : string.Join(", ", Cost.Select(kv => $"{kv.Value} {kv.Key}"));
    }

    public static class Defs
    {
        public static readonly Dictionary<string, EntityDef> All = new Dictionary<string, EntityDef>();

        /// <summary>Stockpile items in display order: raw ores, then manufactured materials.</summary>
        public static readonly string[] Ores = { "iron_ore", "copper_ore", "crystal", "uranium" };
        public static readonly string[] Materials = { "steel", "copper", "circuits", "lenses", "plasma", "composite" };
        public static IEnumerable<string> Items => Ores.Concat(Materials);

        static Dictionary<string, int> C(params object[] kv)
        {
            var d = new Dictionary<string, int>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = (int)kv[i + 1];
            return d;
        }
        static Recipe R(float rate, Dictionary<string, int> inputs, Dictionary<string, int> outputs) => new Recipe { Rate = rate, Inputs = inputs, Outputs = outputs };

        // Weapons
        static readonly WeaponDef Rifle = new WeaponDef { Name = "rifle", Damage = 15, Range = 4.5f, Cooldown = 1.0f, HitsAir = true, VsInfantry = 1f, VsVehicle = 0.25f, VsStructure = 0.3f, VsAir = 0.3f };
        static readonly WeaponDef Rocket = new WeaponDef { Name = "rocket", Damage = 60, Range = 6f, Cooldown = 2.2f, ProjectileSpeed = 9f, HitsAir = true, VsInfantry = 0.3f, VsVehicle = 1f, VsStructure = 0.75f, VsAir = 1f };
        static readonly WeaponDef Laser = new WeaponDef { Name = "laser", Damage = 55, Range = 6f, Cooldown = 1.4f, HitsAir = true, VsInfantry = 1f, VsVehicle = 1f, VsStructure = 0.8f, VsAir = 0.9f };
        static readonly WeaponDef MachineGun = new WeaponDef { Name = "mg", Damage = 10, Range = 5f, Cooldown = 0.5f, HitsAir = true, VsInfantry = 1f, VsVehicle = 0.3f, VsStructure = 0.25f, VsAir = 0.35f };
        static readonly WeaponDef Cannon = new WeaponDef { Name = "cannon", Damage = 40, Range = 5f, Cooldown = 1.5f, ProjectileSpeed = 18f, VsInfantry = 0.5f, VsVehicle = 1f, VsStructure = 0.8f };
        static readonly WeaponDef HeavyCannon = new WeaponDef { Name = "heavy_cannon", Damage = 85, Range = 5.5f, Cooldown = 2.0f, ProjectileSpeed = 16f, SplashRadius = 0.8f, VsInfantry = 0.6f, VsVehicle = 1f, VsStructure = 0.9f };
        static readonly WeaponDef Shell = new WeaponDef { Name = "artillery", Damage = 90, Range = 11f, Cooldown = 3.5f, ProjectileSpeed = 8f, SplashRadius = 1.5f, VsInfantry = 1f, VsVehicle = 0.7f, VsStructure = 1f };
        static readonly WeaponDef LongShell = new WeaponDef { Name = "artillery", Damage = 90, Range = 12f, Cooldown = 3.5f, ProjectileSpeed = 8f, SplashRadius = 1.5f, VsInfantry = 1f, VsVehicle = 0.7f, VsStructure = 1f };
        static readonly WeaponDef BeamCannon = new WeaponDef { Name = "beam", Damage = 110, Range = 6.5f, Cooldown = 2.0f, HitsAir = true, VsInfantry = 0.8f, VsVehicle = 1.2f, VsStructure = 1f, VsAir = 1f };
        static readonly WeaponDef GunshipRockets = new WeaponDef { Name = "gunship_rockets", Damage = 45, Range = 5.5f, Cooldown = 1.2f, ProjectileSpeed = 12f, HitsAir = true, VsInfantry = 0.7f, VsVehicle = 1f, VsStructure = 0.6f, VsAir = 1f };
        static readonly WeaponDef Bombs = new WeaponDef { Name = "bombs", Damage = 260, Range = 0.9f, Cooldown = 4f, ProjectileSpeed = 6f, SplashRadius = 2f, VsInfantry = 0.6f, VsVehicle = 0.8f, VsStructure = 1.6f };
        static readonly WeaponDef TurretGun = new WeaponDef { Name = "turret_gun", Damage = 50, Range = 6.5f, Cooldown = 1.3f, ProjectileSpeed = 20f, VsInfantry = 0.7f, VsVehicle = 1f, VsStructure = 0.5f };
        static readonly WeaponDef Sam = new WeaponDef { Name = "sam", Damage = 90, Range = 8f, Cooldown = 1.6f, ProjectileSpeed = 16f, HitsGround = false, HitsAir = true, HitsHighAir = true, VsAir = 1f };
        static readonly WeaponDef Hellfire = new WeaponDef { Name = "hellfire", Damage = 140, Range = 7f, Cooldown = 5f, ProjectileSpeed = 14f, SplashRadius = 0.6f, VsInfantry = 0.8f, VsVehicle = 1.2f, VsStructure = 0.8f };
        static readonly WeaponDef SniperRifle = new WeaponDef { Name = "sniper", Damage = 150, Range = 9f, Cooldown = 3f, VsInfantry = 1f, VsVehicle = 0.08f, VsStructure = 0.03f };
        static readonly WeaponDef C4 = new WeaponDef { Name = "c4", Damage = 900, Range = 1.0f, Cooldown = 5f, VsInfantry = 0f, VsVehicle = 0.6f, VsStructure = 1f };
        static readonly WeaponDef Flak = new WeaponDef { Name = "flak", Damage = 35, Range = 8f, Cooldown = 0.8f, ProjectileSpeed = 22f, SplashRadius = 1.0f, HitsGround = false, HitsAir = true, HitsHighAir = true, VsAir = 1f };
        static readonly WeaponDef MammothCannon = new WeaponDef { Name = "mammoth_cannon", Damage = 120, Range = 6f, Cooldown = 2.2f, ProjectileSpeed = 16f, SplashRadius = 1.0f, HitsAir = true, VsInfantry = 0.7f, VsVehicle = 1f, VsStructure = 1f, VsAir = 0.6f };
        static readonly WeaponDef TowerLaser = new WeaponDef { Name = "laser", Damage = 90, Range = 7.5f, Cooldown = 1.1f, HitsAir = true, HitsHighAir = true, VsInfantry = 1f, VsVehicle = 1.1f, VsStructure = 0.6f, VsAir = 1f };

        static Defs()
        {
            // ---- Structures
            Add(new EntityDef { Key = "command_center", Name = "Command Center", Description = "Builds every structure, trains Mining Trucks, accepts ore, and extracts 1 iron_ore/s on its own. Can't be built: deploy a Construction Truck for another. Lose all structures (with no Construction Truck left) and you lose.", IsStructure = true, BuildTime = 30, Recipes = new[] { R(1, C(), C("iron_ore", 1)) }, MaxHp = 3000, Armor = Armor.Structure, SizeX = 3, SizeY = 3, Power = 20, Sight = 10, Produces = Producer.CommandCenter, DropOff = true, Buildable = false });
            Add(new EntityDef { Key = "power_plant", Name = "Power Plant", Description = "+100 power. Low power halves all production and refining.", IsStructure = true, Cost = C("iron_ore", 250), BuildTime = 6, MaxHp = 800, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = 100, BuiltBy = Producer.CommandCenter });
            Add(new EntityDef { Key = "mining_refinery", Name = "Mining Refinery", Description = "Refines iron_ore into steel and copper_ore into copper. Trucks can unload here. Comes with a free Mining Truck.", IsStructure = true, Cost = C("iron_ore", 300, "copper_ore", 100), BuildTime = 10, MaxHp = 1500, Armor = Armor.Structure, SizeX = 3, SizeY = 3, Power = -30, BuiltBy = Producer.CommandCenter, DropOff = true, Requires = new[] { "power_plant" },
                Recipes = new[] { R(5, C("iron_ore", 1), C("steel", 1)), R(5, C("copper_ore", 1), C("copper", 1)) } });
            Add(new EntityDef { Key = "barracks", Name = "Barracks", Description = "Trains infantry.", IsStructure = true, Cost = C("steel", 150), BuildTime = 6, MaxHp = 1000, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = -20, BuiltBy = Producer.CommandCenter, Produces = Producer.Barracks, Requires = new[] { "mining_refinery" } });
            Add(new EntityDef { Key = "factory", Name = "Factory", Description = "Builds ground vehicles and Outpost Trucks.", IsStructure = true, Cost = C("steel", 300, "copper", 100), BuildTime = 12, MaxHp = 2000, Armor = Armor.Structure, SizeX = 3, SizeY = 3, Power = -40, BuiltBy = Producer.CommandCenter, Produces = Producer.Factory, Requires = new[] { "mining_refinery" } });
            Add(new EntityDef { Key = "gun_turret", Name = "Gun Turret", Description = "Ground defense. Can't hit aircraft. Upkeep: your first 4 armed defenses are free, each one past that costs 3 steel/min.", IsStructure = true, Cost = C("steel", 150, "copper", 30), BuildTime = 6, MaxHp = 900, Armor = Armor.Structure, Power = -15, Sight = 7, Weapon = TurretGun, BuiltBy = Producer.CommandCenter, Requires = new[] { "barracks" } });
            Add(new EntityDef { Key = "electronics_plant", Name = "Electronics Plant", Description = "Makes circuits from copper and steel.", IsStructure = true, Cost = C("steel", 250, "copper", 150), BuildTime = 10, MaxHp = 1000, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = -40, BuiltBy = Producer.CommandCenter, Requires = new[] { "factory" },
                Recipes = new[] { R(1, C("copper", 2, "steel", 1), C("circuits", 1)) } });
            Add(new EntityDef { Key = "radar_dome", Name = "Radar Dome", Description = "Reveals 16 tiles around it and detects stealth units in that range.", IsStructure = true, Cost = C("steel", 200, "circuits", 60), BuildTime = 10, MaxHp = 1000, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = -40, Sight = 16, BuiltBy = Producer.CommandCenter, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "sam_site", Name = "SAM Site", Description = "Anti-air missiles. Can't hit ground targets.", IsStructure = true, Cost = C("steel", 200, "circuits", 60), BuildTime = 7, MaxHp = 800, Armor = Armor.Structure, Power = -20, Sight = 9, Weapon = Sam, BuiltBy = Producer.CommandCenter, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "optics_lab", Name = "Optics Lab", Description = "Grinds crystal into lenses.", IsStructure = true, Cost = C("steel", 300, "circuits", 80), BuildTime = 10, MaxHp = 1000, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = -40, BuiltBy = Producer.CommandCenter, Requires = new[] { "electronics_plant" },
                Recipes = new[] { R(0.5f, C("crystal", 2), C("lenses", 1)) } });
            Add(new EntityDef { Key = "enrichment_plant", Name = "Enrichment Plant", Description = "Enriches uranium into plasma.", IsStructure = true, Cost = C("steel", 400, "circuits", 120), BuildTime = 12, MaxHp = 1200, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = -60, BuiltBy = Producer.CommandCenter, Requires = new[] { "electronics_plant" },
                Recipes = new[] { R(0.4f, C("uranium", 2), C("plasma", 1)) } });
            Add(new EntityDef { Key = "laser_tower", Name = "Laser Tower", Description = "Heavy beam defense; hits ground and air.", IsStructure = true, Cost = C("steel", 250, "lenses", 60, "circuits", 60), BuildTime = 9, MaxHp = 1200, Armor = Armor.Structure, Power = -50, Sight = 8, Weapon = TowerLaser, BuiltBy = Producer.CommandCenter, Requires = new[] { "optics_lab" } });
            Add(new EntityDef { Key = "composite_foundry", Name = "Composite Foundry", Description = "Fuses steel and crystal into stealth composite.", IsStructure = true, Cost = C("steel", 400, "circuits", 120), BuildTime = 12, MaxHp = 1200, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = -50, BuiltBy = Producer.CommandCenter, Requires = new[] { "optics_lab" },
                Recipes = new[] { R(0.5f, C("steel", 2, "crystal", 1), C("composite", 1)) } });
            Add(new EntityDef { Key = "fusion_reactor", Name = "Fusion Reactor", Description = "+500 power while it has plasma to burn (0.1/s).", IsStructure = true, Cost = C("steel", 600, "circuits", 200, "plasma", 50), BuildTime = 16, MaxHp = 2000, Armor = Armor.Structure, SizeX = 3, SizeY = 3, Power = 500, BuiltBy = Producer.CommandCenter, Requires = new[] { "enrichment_plant" } });
            Add(new EntityDef { Key = "airfield", Name = "Airfield", Description = "Builds aircraft.", IsStructure = true, Cost = C("steel", 500, "circuits", 200), BuildTime = 14, MaxHp = 1800, Armor = Armor.Structure, SizeX = 3, SizeY = 3, Power = -60, BuiltBy = Producer.CommandCenter, Produces = Producer.Airfield, Requires = new[] { "enrichment_plant" } });
            // Neutral derricks near the middle (World.EnsureDerricks): nobody's until an engineer takes one.
            Add(new EntityDef { Key = "derrick", Name = "Derrick", ModelAs = "deep_mine", Description = $"A neutral derrick near the middle of the map. Nobody owns it at first and it can't be hurt; any engineer captures it, whatever its health. Once owned it pays its holder {World.DerrickSteel} steel/s (no power needed) and can be taken back like any building (an engineer once it's below 50%) or destroyed: it leaves salvage, and a fresh neutral derrick rises on the spot {World.DerrickRespawn / 60:0} minutes later.", IsStructure = true, BuildTime = 1, MaxHp = 1600, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Sight = 5, Buildable = false });
            Add(new EntityDef { Key = "outpost", Name = "Outpost", Description = "Forward base: ore drop-off, territory anchor for building, vision 9.", IsStructure = true, BuildTime = 1, MaxHp = 1500, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = 10, Sight = 9, DropOff = true, Buildable = false });

            // ---- Units
            Add(new EntityDef { Key = "mining_truck", Name = "Mining Truck", Description = "Mines ore (150 per trip, one type at a time) and unloads at the nearest drop-off.", Cost = C("iron_ore", 200), BuildTime = 8, MaxHp = 900, Armor = Armor.Vehicle, Speed = 1.8f, Radius = 0.5f, Sight = 5, HarvestCapacity = 150, BuiltBy = Producer.CommandCenter });
            // Infantry walk: every soldier is slower than the slowest vehicle (0.8-1.05 tiles/s vs 1.2+).
            Add(new EntityDef { Key = "rifleman", Name = "Rifleman", Description = "Cheap anti-infantry. Weak vs armor, can plink aircraft.", Cost = C("steel", 40), BuildTime = 4, MaxHp = 125, Armor = Armor.Infantry, Speed = 0.9f, Radius = 0.2f, Weapon = Rifle, BuiltBy = Producer.Barracks });
            Add(new EntityDef { Key = "rocket_soldier", Name = "Rocket Soldier", Description = "Anti-armor and anti-air infantry.", Cost = C("steel", 80, "copper", 30), BuildTime = 6, MaxHp = 125, Armor = Armor.Infantry, Speed = 0.8f, Radius = 0.2f, Weapon = Rocket, BuiltBy = Producer.Barracks });
            Add(new EntityDef { Key = "medic", Name = "Medic", Description = "Unarmed. Heals friendly infantry for free (15 HP/s). Auto-heals wounded infantry within 6 tiles when idle.", Cost = C("steel", 60, "copper", 20), BuildTime = 5, MaxHp = 100, Armor = Armor.Infantry, Speed = 0.95f, Radius = 0.2f, Sight = 6, RepairRate = 15, Medic = true, BuiltBy = Producer.Barracks });
            Add(new EntityDef { Key = "laser_trooper", Name = "Laser Trooper", Description = "Elite beam infantry; good against everything.", Cost = C("steel", 80, "lenses", 30), BuildTime = 8, MaxHp = 180, Armor = Armor.Infantry, Speed = 0.85f, Radius = 0.2f, Weapon = Laser, BuiltBy = Producer.Barracks, Requires = new[] { "optics_lab" } });
            Add(new EntityDef { Key = "scout_buggy", Name = "Scout Buggy", Description = "Very fast, long sight, machine gun.", Cost = C("steel", 100, "copper", 20), BuildTime = 5, MaxHp = 220, Armor = Armor.Vehicle, Speed = 4f, Radius = 0.35f, Sight = 9, Weapon = MachineGun, BuiltBy = Producer.Factory });
            Add(new EntityDef { Key = "light_tank", Name = "Light Tank", Description = "Fast all-round tank. Can't hit aircraft.", Cost = C("steel", 200, "copper", 40), BuildTime = 8, MaxHp = 400, Armor = Armor.Vehicle, Speed = 2.6f, Radius = 0.45f, Weapon = Cannon, BuiltBy = Producer.Factory });
            Add(new EntityDef { Key = "repair_truck", Name = "Repair Truck", Description = "Unarmed. Repairs friendly vehicles, aircraft and structures (30 HP/s, costs 1 steel per 10 HP). Auto-repairs anything damaged within 6 tiles when idle.", Cost = C("steel", 180, "copper", 60), BuildTime = 8, MaxHp = 500, Armor = Armor.Vehicle, Speed = 2.2f, Radius = 0.45f, Sight = 6, RepairRate = 30, BuiltBy = Producer.Factory });
            Add(new EntityDef { Key = "outpost_truck", Name = "Outpost Truck", Description = "Drive to a remote ore field and 'deploy' it into an Outpost.", Cost = C("steel", 400, "copper", 100, "circuits", 50), BuildTime = 12, MaxHp = 800, Armor = Armor.Vehicle, Speed = 1.4f, Radius = 0.55f, Sight = 6, DeploysInto = "outpost", BuiltBy = Producer.Factory, Requires = new[] { "electronics_plant" } });
            // Deep mining gear costs steel only: steel keeps coming from the command center's trickle even when the surface
            // is mined out, so a player short of copper can still reach the copper underground.
            Add(new EntityDef { Key = "geological_surveyor", Name = "Geological Surveyor", Description = "Unarmed. Finds deep ore when the surface runs out: it stops for 8s, and every deep deposit within 12 tiles gets its flag (a mining zone only your team sees). 'survey' one spot, or 'prospect' to let it roam an area, flagging deposits and moving on by itself.", Cost = C("steel", 200), BuildTime = 7, MaxHp = 300, Armor = Armor.Vehicle, Speed = 2.6f, Radius = 0.45f, Sight = 7, BuiltBy = Producer.Factory });
            // Insurance, priced so keeping one in reserve is a real decision: 1500 steel is about five minutes of a refinery at full tilt.
            Add(new EntityDef { Key = "construction_truck", Name = "Construction Truck", ModelAs = "outpost_truck", ModelScale = 1.3f, Description = "Expensive insurance: drive it anywhere and 'deploy' it into a new Command Center where it stands (the same placement rules as an outpost: open ground, no ore, a clear truck lane). Rebuild after losing your HQ, or expand with a second one. While you have one, losing every structure doesn't knock you out.", Cost = C("steel", 1500, "circuits", 200), BuildTime = 30, MaxHp = 1400, Armor = Armor.Vehicle, Speed = 1.1f, Radius = 0.75f, Sight = 6, DeploysInto = "command_center", BuiltBy = Producer.Factory, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "drill_rig", Name = "Drill Rig", Description = "Deep mining equipment. 'drill' it to one of your mining zones (deep deposits your surveyors flagged): it drives there and deploys into a Deep Mine on arrival.", Cost = C("steel", 800), BuildTime = 14, MaxHp = 900, Armor = Armor.Vehicle, Speed = 1.2f, Radius = 0.6f, Sight = 5, DeploysInto = "deep_mine", BuiltBy = Producer.Factory, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "deep_mine", Name = "Deep Mine", Description = $"Pumps ore from the deep deposit under it straight into your stockpile ({EntityDef.DeepMineRate}/s) until the deposit runs dry. Power hungry. Deployed from a Drill Rig.", IsStructure = true, BuildTime = 1, MaxHp = 1600, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = -50, Sight = 6, Buildable = false });
            Add(new EntityDef { Key = "heavy_tank", Name = "Heavy Tank", Description = "Slow, heavily armored, splash damage.", Cost = C("steel", 400, "circuits", 80), BuildTime = 13, MaxHp = 950, Armor = Armor.Vehicle, Speed = 1.6f, Radius = 0.55f, Weapon = HeavyCannon, BuiltBy = Producer.Factory, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "artillery", Name = "Artillery", Description = "Range 11 splash shells. Fragile; keep it behind your tanks.", Cost = C("steel", 300, "circuits", 100), BuildTime = 12, MaxHp = 300, Armor = Armor.Vehicle, Speed = 1.3f, Radius = 0.5f, Sight = 7, Weapon = Shell, BuiltBy = Producer.Factory, Requires = new[] { "electronics_plant" } });
            // The first agent invention adopted into the roster: Knight Claude's Ballista and Queen Claude's Longbow, designed
            // independently in room 1 on 2026-10-03 (artillery at the 12-tile cap), priced as the invention validator priced them.
            Add(new EntityDef { Key = "long_range_artillery", Name = "Long-Range Artillery", ModelAs = "artillery", Description = "Range 12 splash shells, a tile past standard artillery, for a little more. Sight 7, so it needs a spotter (a scout, drone or radar) to fire at full range. Fragile. First designed by players: Knight Claude's Ballista and Queen Claude's Longbow, invented independently.", Cost = C("steel", 350, "circuits", 120), BuildTime = 13.5f, MaxHp = 300, Armor = Armor.Vehicle, Speed = 1.3f, Radius = 0.5f, Sight = 7, Weapon = LongShell, BuiltBy = Producer.Factory, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "laser_tank", Name = "Laser Tank", Description = "Beam cannon; hits ground and air.", Cost = C("steel", 350, "lenses", 60, "plasma", 40, "circuits", 60), BuildTime = 15, MaxHp = 750, Armor = Armor.Vehicle, Speed = 2f, Radius = 0.55f, Weapon = BeamCannon, BuiltBy = Producer.Factory, Requires = new[] { "optics_lab", "enrichment_plant" } });
            // ---- Specialists, transports, mines, super-heavy
            Add(new EntityDef { Key = "engineer", Name = "Engineer", Description = "Unarmed. Captures an enemy structure below 50% HP, or a neutral derrick at any health (the engineer is used up). Use 'capture'.", Cost = C("steel", 120), BuildTime = 6, MaxHp = 100, Armor = Armor.Infantry, Speed = 0.85f, Radius = 0.2f, Sight = 5, Engineer = true, BuiltBy = Producer.Barracks });
            Add(new EntityDef { Key = "sniper", Name = "Sniper", Description = "Range 9 rifle that one-shots infantry; nearly useless against armor and buildings.", Cost = C("steel", 120, "lenses", 20), BuildTime = 8, MaxHp = 100, Armor = Armor.Infantry, Speed = 0.8f, Radius = 0.2f, Sight = 10, Weapon = SniperRifle, BuiltBy = Producer.Barracks, Requires = new[] { "optics_lab" } });
            Add(new EntityDef { Key = "commando", Name = "Commando", Description = "Plants C4 that levels structures and wrecks vehicles. Can't fight infantry.", Cost = C("steel", 300, "circuits", 50), BuildTime = 12, MaxHp = 250, Armor = Armor.Infantry, Speed = 1.05f, Radius = 0.2f, Sight = 7, Weapon = C4, BuiltBy = Producer.Barracks, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "apc", Name = "APC", Description = "Armored transport for 5 infantry, with a machine gun. Use 'load' and 'unload'. Passengers die if it's destroyed.", Cost = C("steel", 250, "copper", 40), BuildTime = 9, MaxHp = 600, Armor = Armor.Vehicle, Speed = 2.8f, Radius = 0.5f, Sight = 7, Weapon = MachineGun, Capacity = 5, BuiltBy = Producer.Factory });
            Add(new EntityDef { Key = "flak_track", Name = "Flak Track", Description = "Mobile anti-air; splash flak bursts. Can't hit ground targets.", Cost = C("steel", 250, "circuits", 40), BuildTime = 9, MaxHp = 450, Armor = Armor.Vehicle, Speed = 2.4f, Radius = 0.45f, Sight = 9, Weapon = Flak, BuiltBy = Producer.Factory, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "minelayer", Name = "Mine Layer", Description = $"Unarmed. Lays hidden mines (each costs {EntityDef.MineCost} steel) with 'lay_mines'. Mines blow up under enemy ground units.", Cost = C("steel", 250, "copper", 50), BuildTime = 9, MaxHp = 450, Armor = Armor.Vehicle, Speed = 2.0f, Radius = 0.45f, Sight = 6, LaysMines = true, BuiltBy = Producer.Factory, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "mine", Name = "Mine", Description = "Hidden from enemies unless they're within 1.5 tiles or inside a radar dome's range. Explodes under enemy ground units.", BuildTime = 0, MaxHp = 60, Armor = Armor.Vehicle, Speed = 0, Radius = 0.25f, Sight = 1, Stealth = true, IsMine = true, BuiltBy = Producer.None });
            Add(new EntityDef { Key = "mammoth_tank", Name = "Mammoth Tank", Description = "Super-heavy twin-cannon tank. Hits ground and air, splash, and repairs itself up to 50% HP.", Cost = C("steel", 800, "circuits", 200, "plasma", 40), BuildTime = 24, MaxHp = 1800, Armor = Armor.Vehicle, Speed = 1.2f, Radius = 0.7f, Sight = 7, Weapon = MammothCannon, SelfRepairTo = 0.5f, SelfRepairRate = 6f, BuiltBy = Producer.Factory, Requires = new[] { "enrichment_plant" } });
            Add(new EntityDef { Key = "recon_drone", Name = "Recon Drone", RangeMaps = 1.5f, Description = "Light drone: cheap, fast, unarmed flying scout with sight 12. Fuel for about 1.5 map widths: across the map and halfway back, so a run to the far side is one-way unless it can land on the way.", Cost = C("steel", 120, "circuits", 30), BuildTime = 6, MaxHp = 120, Armor = Armor.Aircraft, Speed = 4.5f, Radius = 0.35f, Sight = 12, IsAir = true, BuiltBy = Producer.Factory, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "transport_chopper", Name = "Transport Chopper", Description = "Flying transport for 6 infantry; ignores terrain. Use 'load' and 'unload'. Passengers die if it's shot down.", Cost = C("steel", 300, "circuits", 80), BuildTime = 12, MaxHp = 600, Armor = Armor.Aircraft, Speed = 3.5f, Radius = 0.7f, Sight = 7, Capacity = 6, IsAir = true, BuiltBy = Producer.Airfield });
            Add(new EntityDef { Key = "long_range_drone", Name = "Long-Range Drone", RangeMaps = 2f, ModelAs = "recon_drone", ModelScale = 1.35f, Description = "Unarmed scout with fuel for about 2 map widths: across the map and back. Sight 12. Built at an airfield; lands on one to refuel.", Cost = C("steel", 220, "circuits", 80), BuildTime = 10, MaxHp = 180, Armor = Armor.Aircraft, Speed = 4f, Radius = 0.4f, Sight = 12, IsAir = true, BuiltBy = Producer.Airfield, Requires = new[] { "electronics_plant" } });
            Add(new EntityDef { Key = "reaper_drone", Name = "Reaper Drone", RangeMaps = 24f, HighAltitude = true, ModelAs = "stealth_bomber", ModelScale = 0.7f, Description = "Ultra drone: flies high (only SAM sites, flak and laser towers can hit it), stays up for about 24 map widths of flight (recon all day: around half an hour on a big map), sight 12, and fires hellfire missiles at ground targets. Very expensive.", Cost = C("steel", 600, "circuits", 300, "plasma", 120, "lenses", 80), BuildTime = 30, MaxHp = 450, Armor = Armor.Aircraft, Speed = 2.8f, Radius = 0.6f, Sight = 12, Weapon = Hellfire, IsAir = true, BuiltBy = Producer.Airfield, Requires = new[] { "radar_dome", "enrichment_plant" } });
            Add(new EntityDef { Key = "gunship", Name = "Gunship", Description = "Aircraft. Flies over terrain; rockets vs ground and air.", Cost = C("steel", 300, "circuits", 120, "plasma", 30), BuildTime = 14, MaxHp = 500, Armor = Armor.Aircraft, Speed = 3.2f, Radius = 0.6f, Sight = 8, Weapon = GunshipRockets, IsAir = true, BuiltBy = Producer.Airfield });
            Add(new EntityDef { Key = "stealth_bomber", Name = "Stealth Bomber", Description = "Invisible unless within 3 tiles of an enemy or inside enemy radar range. Bombs wreck structures.", Cost = C("composite", 300, "circuits", 150, "plasma", 80), BuildTime = 20, MaxHp = 600, Armor = Armor.Aircraft, Speed = 3.8f, Radius = 0.7f, Sight = 7, Weapon = Bombs, IsAir = true, Stealth = true, BuiltBy = Producer.Airfield, Requires = new[] { "composite_foundry" } });

            // Fuel. Ground vehicles burn it while driving (a parked tank burns nothing); aircraft burn it the whole time
            // they're airborne. Vehicles refuel next to a command center, outpost, refinery or factory, or from a repair
            // truck in the field; aircraft land on an airfield (recon drones also at a factory) and refuel there.
            var tanks = new Dictionary<string, float>
            {
                ["mining_truck"] = 300, ["scout_buggy"] = 160, ["light_tank"] = 200, ["repair_truck"] = 360, ["outpost_truck"] = 260,
                ["heavy_tank"] = 200, ["artillery"] = 200, ["laser_tank"] = 200, ["apc"] = 200, ["flak_track"] = 200, ["minelayer"] = 220,
                ["mammoth_tank"] = 220, ["geological_surveyor"] = 260, ["drill_rig"] = 220, ["construction_truck"] = 300, ["recon_drone"] = 150, ["transport_chopper"] = 150, ["gunship"] = 120, ["stealth_bomber"] = 150,
            };
            foreach (var d in All.Values)
            {
                if (tanks.TryGetValue(d.Key, out var f)) d.Fuel = f;
                else if (!d.IsStructure && !d.IsMine && (d.IsAir || d.Armor == Armor.Vehicle)) d.Fuel = 200; // any vehicle added later
                if (d.Key is "command_center" or "outpost" or "mining_refinery" or "factory") d.FuelDepot = true;
                if (d.Key == "airfield") d.Helipad = true;
            }
        }

        static void Add(EntityDef d)
        {
            // A unit should see at least as far as it shoots, or it gets picked apart by things it can't see.
            // Artillery is the deliberate exception: it needs spotters.
            if (d.Weapon != null && d.Key != "artillery") d.Sight = System.Math.Max(d.Sight, d.Weapon.Range + 1f);
            All[d.Key] = d;
        }

        public static EntityDef Get(string key) => key == null ? null : All.TryGetValue(key, out var d) ? d : Invented?.Invoke(key);

        /// <summary>
        /// Inventions live on each World (World.Inventions; sim code resolves keys with World.Def). This hook lets code
        /// that only has a key and no world (the Unity view's event handlers, the HUD, the API status) resolve the
        /// invention keys of the current game too: the most recently created World installs it. Invention keys
        /// (t&lt;team&gt;:&lt;name&gt;) can't collide with standard ones.
        /// </summary>
        public static System.Func<string, EntityDef> Invented;

        public static string ProducerKey(Producer p) => p switch
        {
            Producer.CommandCenter => "command_center",
            Producer.Barracks => "barracks",
            Producer.Factory => "factory",
            Producer.Airfield => "airfield",
            _ => null,
        };
    }
}
