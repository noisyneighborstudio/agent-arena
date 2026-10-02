using System.Collections.Generic;

namespace Pez.Sim
{
    public enum Armor { Infantry, Vehicle, Structure }
    public enum Producer { None, ConstructionYard, Barracks, WarFactory, Refinery }

    public class WeaponDef
    {
        public string Name;
        public float Damage, Range, Cooldown;
        /// <summary>Tiles per second. 0 = hitscan.</summary>
        public float ProjectileSpeed;
        public float SplashRadius;
        public float VsInfantry = 1f, VsVehicle = 1f, VsStructure = 1f;

        public float Multiplier(Armor a) => a == Armor.Infantry ? VsInfantry : a == Armor.Vehicle ? VsVehicle : VsStructure;
    }

    public class EntityDef
    {
        public string Key, Name, Description;
        public bool IsStructure;
        public int Cost;
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
        public int HarvestCapacity; // harvesters only
    }

    public static class Defs
    {
        public static readonly Dictionary<string, EntityDef> All = new Dictionary<string, EntityDef>();

        static readonly WeaponDef Rifle = new WeaponDef { Name = "rifle", Damage = 15, Range = 4.5f, Cooldown = 1.0f, ProjectileSpeed = 0, VsInfantry = 1f, VsVehicle = 0.25f, VsStructure = 0.3f };
        static readonly WeaponDef Rocket = new WeaponDef { Name = "rocket", Damage = 60, Range = 6f, Cooldown = 2.2f, ProjectileSpeed = 9f, VsInfantry = 0.3f, VsVehicle = 1f, VsStructure = 0.75f };
        static readonly WeaponDef Cannon = new WeaponDef { Name = "cannon", Damage = 40, Range = 5f, Cooldown = 1.5f, ProjectileSpeed = 18f, VsInfantry = 0.5f, VsVehicle = 1f, VsStructure = 0.8f };
        static readonly WeaponDef HeavyCannon = new WeaponDef { Name = "heavy_cannon", Damage = 85, Range = 5.5f, Cooldown = 2.0f, ProjectileSpeed = 16f, SplashRadius = 0.8f, VsInfantry = 0.6f, VsVehicle = 1f, VsStructure = 0.9f };
        static readonly WeaponDef TurretGun = new WeaponDef { Name = "turret_gun", Damage = 50, Range = 6.5f, Cooldown = 1.3f, ProjectileSpeed = 20f, VsInfantry = 0.7f, VsVehicle = 1f, VsStructure = 0.5f };

        static Defs()
        {
            Add(new EntityDef { Key = "construction_yard", Name = "Construction Yard", Description = "Builds all structures. Lose every structure and you lose the game.", IsStructure = true, Cost = 3000, BuildTime = 30, MaxHp = 3000, Armor = Armor.Structure, SizeX = 3, SizeY = 3, Power = 15, Sight = 7, Produces = Producer.ConstructionYard, BuiltBy = Producer.None });
            Add(new EntityDef { Key = "power_plant", Name = "Power Plant", Description = "Produces power. Low power halves production speed.", IsStructure = true, Cost = 300, BuildTime = 6, MaxHp = 800, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = 100, BuiltBy = Producer.ConstructionYard });
            Add(new EntityDef { Key = "refinery", Name = "Ore Refinery", Description = "Harvesters unload ore here for credits. Comes with one free harvester.", IsStructure = true, Cost = 1400, BuildTime = 12, MaxHp = 1500, Armor = Armor.Structure, SizeX = 3, SizeY = 3, Power = -40, BuiltBy = Producer.ConstructionYard, Produces = Producer.Refinery, Requires = new[] { "power_plant" } });
            Add(new EntityDef { Key = "barracks", Name = "Barracks", Description = "Trains infantry.", IsStructure = true, Cost = 300, BuildTime = 6, MaxHp = 1000, Armor = Armor.Structure, SizeX = 2, SizeY = 2, Power = -20, BuiltBy = Producer.ConstructionYard, Produces = Producer.Barracks, Requires = new[] { "power_plant" } });
            Add(new EntityDef { Key = "war_factory", Name = "War Factory", Description = "Builds vehicles.", IsStructure = true, Cost = 1500, BuildTime = 14, MaxHp = 2000, Armor = Armor.Structure, SizeX = 3, SizeY = 3, Power = -30, BuiltBy = Producer.ConstructionYard, Produces = Producer.WarFactory, Requires = new[] { "refinery" } });
            Add(new EntityDef { Key = "gun_turret", Name = "Gun Turret", Description = "Static defense. Strong against vehicles.", IsStructure = true, Cost = 600, BuildTime = 7, MaxHp = 1000, Armor = Armor.Structure, SizeX = 1, SizeY = 1, Power = -20, Sight = 7, Weapon = TurretGun, BuiltBy = Producer.ConstructionYard, Requires = new[] { "barracks" } });

            Add(new EntityDef { Key = "rifleman", Name = "Rifleman", Description = "Cheap infantry. Good vs infantry, weak vs armor.", Cost = 100, BuildTime = 4, MaxHp = 125, Armor = Armor.Infantry, Speed = 1.5f, Radius = 0.2f, Weapon = Rifle, BuiltBy = Producer.Barracks });
            Add(new EntityDef { Key = "rocket_soldier", Name = "Rocket Soldier", Description = "Anti-armor infantry. Outranges tanks.", Cost = 300, BuildTime = 7, MaxHp = 125, Armor = Armor.Infantry, Speed = 1.3f, Radius = 0.2f, Weapon = Rocket, BuiltBy = Producer.Barracks });
            Add(new EntityDef { Key = "harvester", Name = "Harvester", Description = "Collects ore automatically and returns it to the nearest refinery.", Cost = 1000, BuildTime = 12, MaxHp = 1000, Armor = Armor.Vehicle, Speed = 1.4f, Radius = 0.5f, Sight = 4, HarvestCapacity = 700, BuiltBy = Producer.WarFactory, Requires = new[] { "refinery" } });
            Add(new EntityDef { Key = "light_tank", Name = "Light Tank", Description = "Fast, all-round tank.", Cost = 600, BuildTime = 8, MaxHp = 400, Armor = Armor.Vehicle, Speed = 2.6f, Radius = 0.45f, Weapon = Cannon, BuiltBy = Producer.WarFactory });
            Add(new EntityDef { Key = "heavy_tank", Name = "Heavy Tank", Description = "Slow, heavily armored, splash damage.", Cost = 1200, BuildTime = 14, MaxHp = 950, Armor = Armor.Vehicle, Speed = 1.6f, Radius = 0.55f, Weapon = HeavyCannon, BuiltBy = Producer.WarFactory });
        }

        static void Add(EntityDef d) => All[d.Key] = d;

        public static EntityDef Get(string key) => key != null && All.TryGetValue(key, out var d) ? d : null;
    }
}
