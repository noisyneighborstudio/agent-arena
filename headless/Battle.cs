using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>
    /// --battle: a staged demo battle for looking at the renderer, not a game anyone plays. Every team gets a big mixed army
    /// at a staging point between its base and the middle, they attack-move into each other, and each army is topped up
    /// while it's under strength, so the fight in the middle never runs out. Bases are left alone: nobody is defeated.
    /// </summary>
    public class Battle
    {
        // One wave per team: (unit, count). Heavy on armour and infantry, with artillery behind and air over the top.
        static readonly (string Key, int N)[] Wave =
        {
            ("heavy_tank", 6), ("light_tank", 8), ("mammoth_tank", 2), ("laser_tank", 4), ("artillery", 3), ("flak_track", 2),
            ("apc", 2), ("scout_buggy", 4), ("rifleman", 12), ("rocket_soldier", 8), ("laser_trooper", 4), ("medic", 2),
            ("sniper", 2), ("gunship", 3), ("reaper_drone", 2),
        };
        static readonly (string Key, int N)[] Topup =
        {
            ("heavy_tank", 3), ("light_tank", 4), ("laser_tank", 2), ("rifleman", 6), ("rocket_soldier", 4), ("gunship", 1),
        };

        readonly World w;
        readonly int strength;
        float nextTopup, nextPush;

        public Battle(World w, int strength = 45)
        {
            this.w = w;
            this.strength = strength;
            foreach (var t in w.Teams) t.ProtectedUntil = 0;
            for (int t = 0; t < w.Teams.Count; t++) Spawn(t, Wave);
            Push(toMiddle: true);
            nextTopup = w.Time + 15f;
            nextPush = w.Time + 6f;
        }

        Vec2 Middle => new Vec2(w.Map.W / 2f, w.Map.H / 2f);

        /// <summary>Where a team's army forms up: 55% of the way from its base to the middle.</summary>
        Vec2 Staging(int team)
        {
            var s = w.Teams[team].StartPos;
            return s + (Middle - s) * 0.55f;
        }

        IEnumerable<Entity> Army(int team) => w.Owned(team).Where(e => !e.IsStructure && !e.IsHarvester && !e.IsMine && (e.Def.Weapon != null || e.Def.Key == "medic"));

        void Spawn(int team, (string Key, int N)[] wave)
        {
            var home = w.Owned(team).FirstOrDefault(e => e.IsStructure);
            if (home == null) return;
            var at = Staging(team);
            int cols = 10, i = 0;
            foreach (var (key, n) in wave)
                for (int k = 0; k < n; k++, i++)
                {
                    var u = w.SpawnUnit(team, key, home);
                    var p = at + new Vec2((i % cols - cols / 2f) * 1.1f, (i / cols - 3) * 1.1f);
                    if (!u.Def.IsAir) p = w.Paths.NearestPassable(Int2.Of(p)).Center;
                    u.Pos = u.PrevPos = u.GuardPos = p;
                    u.Order = Order.Idle;
                    u.Path = null;
                }
        }

        /// <summary>Every idle soldier attack-moves on the nearest enemy soldier (the middle if none, or at the start, so all four meet there).</summary>
        void Push(bool toMiddle = false)
        {
            for (int t = 0; t < w.Teams.Count; t++)
            {
                var mine = Army(t).Where(e => e.Order == Order.Idle).ToList();
                if (mine.Count == 0) continue;
                var c = new Vec2(mine.Average(e => e.Pos.X), mine.Average(e => e.Pos.Y));
                var foe = Enumerable.Range(0, w.Teams.Count).Where(o => o != t).SelectMany(Army).OrderBy(e => Vec2.Dist(e.Pos, c)).FirstOrDefault();
                var to = toMiddle || foe == null ? Middle : foe.Pos;
                Commands.Execute(w, t, new Dictionary<string, object>
                {
                    ["type"] = "attack_move", ["units"] = mine.Select(e => (object)(double)e.Id).ToList(), ["x"] = (double)to.X, ["y"] = (double)to.Y,
                });
            }
        }

        public void Tick()
        {
            if (w.Time >= nextPush) { nextPush = w.Time + 6f; Push(); }
            if (w.Time < nextTopup) return;
            nextTopup = w.Time + 15f;
            for (int t = 0; t < w.Teams.Count; t++)
                if (Army(t).Count() < strength) Spawn(t, Topup);
            Push(toMiddle: true); // reinforcements head for the middle
        }
    }
}
