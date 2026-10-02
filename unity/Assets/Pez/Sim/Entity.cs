using System.Collections.Generic;

namespace Pez.Sim
{
    public enum Order { Idle, Move, AttackMove, Attack, Harvest, ReturnOre, Repair, Board, Capture, LayMines }

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

        // Converters / reactors: currently producing
        public bool Working;

        // Transports and passengers
        public int CarrierId;                         // != 0 while riding inside a transport
        public readonly List<int> Passengers = new List<int>();
        public bool IsCarried => CarrierId != 0;

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

        public string OrderName => Order.ToString().ToLowerInvariant() switch
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
