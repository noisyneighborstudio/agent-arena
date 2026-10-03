using System.Collections.Generic;

namespace Pez.Sim
{
    public enum Order { Idle, Move, AttackMove, Attack, Harvest, ReturnOre, Repair, Board, Capture, LayMines, Refuel, Survey, Drill }

    /// <summary>
    /// A mining truck delivering ore backs into its drop-off's bay, one truck at a time: drive to the head of the lane
    /// (Approach), wait beside it if the bay is busy (Queue), turn to face away from the building (Align), reverse in
    /// (Reverse), settle and tip the load (Unload), then drive out (PullOut). See World.UpdateHarvester.
    /// </summary>
    public enum DockStep { None, Approach, Queue, Align, Reverse, Unload, PullOut }

    public class Entity
    {
        public int Id;
        public int Team;
        public EntityDef Def;
        public Vec2 Pos, PrevPos;
        public float Facing;          // radians, body
        public float TurretFacing;    // radians, weapon
        public float Hp;
        public bool Dead;

        // Structures
        public Int2 Origin;
        public float BuildProgress = 1f;   // 0..1, structures only
        public Vec2? Rally;
        public bool IsComplete => BuildProgress >= 1f;

        // Units
        public Order Order;
        public Vec2 OrderPos;
        public Vec2 GuardPos;
        public int TargetId;
        public List<Vec2> Path;
        public int PathIdx;
        public float Cooldown;
        public float SpeedCap;        // moving as a group: no faster than the slowest member (0 = own speed)
        // Anti-jam: where it was when progress was last checked, and until when it may pass through other units.
        public Vec2 ProgressPos;
        public float ProgressAt, GhostUntil, ProgressDist;
        public float LastFiredAt = -999;
        // Queued waypoints for move / attack_move (taken in order; with WaypointLoop, forever, as a patrol).
        public readonly List<Vec2> Waypoints = new List<Vec2>();
        public bool WaypointLoop;
        // Pull back to base on its own when HP drops below this fraction (0 = never). Stays set until changed.
        public float RetreatBelow;
        public bool Retreating;
        public float RepathTimer;
        public bool Moving;
        public int LastAttackerId;
        public float LastHitTime = -999;
        public float LastCallForHelp = -999;

        // Responding to an attack nearby: remember where to come back to.
        public bool Responding;
        public Vec2 HomePos;

        // Mining trucks
        public int Cargo;
        public int CargoType = -1;   // index into Defs.Ores while carrying
        public int HarvestType = -1; // preferred ore type, -1 = nearest of any
        public Int2? HarvestTile;
        public float WorkTimer;
        public bool Burning;         // a building on fire (below World.BurnBelow): it burns down unless repaired
        public DockStep Dock;        // where it is in backing into a drop-off's bay
        public int DockAt;           // the drop-off structure it's delivering to

        // Deep mines: the deposit underneath
        public int DepositId;

        // Geological surveyors on 'prospect': after each survey they pick the next unsurveyed spot within
        // ProspectRadius of ProspectCenter by themselves (the order stays Survey, so a refuel trip resumes it).
        public bool Prospecting;
        public Vec2 ProspectCenter;
        public float ProspectRadius;
        public readonly List<Vec2> SkipSites = new List<Vec2>();   // prospect sites it couldn't reach (not tried again)
        // Why its last survey couldn't be done (null = no failure since its last survey order).
        public string SurveyFailure;
        public float SurveyFailedAt;
        // Drill rigs on 'drill': the mining zone (deep deposit id) they're driving to and will deploy on.
        public int ZoneId;

        // Converters / reactors: currently producing
        public bool Working;

        // Transports and passengers
        public int CarrierId;                         // != 0 while riding inside a transport
        public readonly List<int> Passengers = new List<int>();
        public bool IsCarried => CarrierId != 0;

        // Fuel (vehicles and aircraft). A refuel trip remembers the order it interrupted and picks it up afterwards.
        public float Fuel;
        public float FuelCap;         // drones: tank size set from the map's width (World.DroneFuel); 0 = the def's
        public float FuelMax => FuelCap > 0 ? FuelCap : Def.Fuel;
        public bool Landed;          // aircraft parked on a pad (refuelling, not burning)
        public bool Stranded;        // ground vehicle out of fuel: can't move until a repair truck tops it up
        public bool AtDepot;         // ground vehicle next to a fuel depot (refuelling)
        public bool FuelWarned;      // told the commander there's nowhere to refuel
        public float NoAutoRefuelUntil;
        public Order ResumeOrder;
        public Vec2 ResumePos, ResumeGuard;
        public int ResumeTarget;
        public readonly List<Vec2> ResumeWaypoints = new List<Vec2>();
        public float ResumeSpeedCap;
        public float FuelFraction => FuelMax > 0 ? Fuel / FuelMax : 1f;

        // Mine layers: where to put the remaining mines
        public readonly List<Vec2> MineQueue = new List<Vec2>();

        public bool IsStructure => Def.IsStructure;
        public bool IsArmed => Def.Weapon != null;
        public bool IsHarvester => Def.HarvestCapacity > 0;
        public bool IsAir => Def.IsAir;
        public bool IsMine => Def.IsMine;

        public Vec2 Center => IsStructure ? new Vec2(Origin.X + Def.SizeX / 2f, Origin.Y + Def.SizeY / 2f) : Pos;

        /// <summary>Distance from a point to this entity's body (footprint edge for structures).</summary>
        public float DistFrom(Vec2 p)
        {
            if (!IsStructure) return System.MathF.Max(0, Vec2.Dist(p, Pos) - Def.Radius);
            float cx = System.MathF.Max(Origin.X, System.MathF.Min(p.X, Origin.X + Def.SizeX));
            float cy = System.MathF.Max(Origin.Y, System.MathF.Min(p.Y, Origin.Y + Def.SizeY));
            return Vec2.Dist(p, new Vec2(cx, cy));
        }

        /// <summary>What a delivering truck is doing at the bay, for players (null when it isn't docking).</summary>
        public string DockName => Order != Order.ReturnOre ? null : Dock switch
        {
            DockStep.Queue => "waiting for the bay",
            DockStep.Align => "lining up to back in",
            DockStep.Reverse => "backing into the bay",
            DockStep.Unload => "unloading",
            DockStep.PullOut => "pulling out",
            _ => null,
        };

        public string OrderName => Order == Order.Survey && Prospecting ? "prospect" : Order.ToString().ToLowerInvariant() switch
        {
            "attackmove" => "attack_move",
            "returnore" => "return_ore",
            "laymines" => "lay_mines",
            var s => s
        };
    }

    public class Projectile
    {
        public int Id;
        public int Team;
        public int SourceId;
        public int TargetId;
        public Vec2 Pos, PrevPos, TargetPos;
        public WeaponDef Weapon;
    }

    public class GameEvent
    {
        public long Seq;
        public int Tick;
        public string Type;    // shot, fire, hit, destroyed, built, trained, placed, sold, chat, defeated, game_over, under_attack
        public int Team = -1;  // team the event mainly concerns (-1 = global)
        public int A, B;       // entity ids (source, target)
        public Vec2 Pos, Pos2;
        public string Key;
        public string Text;
    }
}
