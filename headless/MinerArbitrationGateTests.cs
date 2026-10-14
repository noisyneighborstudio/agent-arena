using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static void GateMinerArbitration()
        {
            var (game, trucks) = MinerResumeHitScene(); var w = game.World;
            // The same field also has an objective and support guard: miner ownership must suppress their writes
            // until withdrawal/resumption arbitration commits the winning policy intent.
            Commands.Execute(w, 0, Cmd("type", "squad", "name", "miners", "add", trucks.Select(t => (object)t.Id).ToList()));
            Commands.Execute(w, 0, Cmd("type", "guard", "squad", "miners", "hold_for_support", Cmd("within", 3)));
            Commands.Execute(w, 0, Cmd("type", "objective", "id", "mine-hold", "squad", "miners", "verb", "hold",
                "at", new List<object> { 31d, 31d }, "radius", 8));
            int maximum = 0;
            for (int i = 0; i < 60 * World.TickRate; i++)
            {
                var touched = new List<Entity>(); w.Touched = touched;
                try { w.Step(); } finally { w.Touched = null; }
                foreach (var truck in trucks) maximum = Math.Max(maximum, touched.Count(e => e.Id == truck.Id));
            }
            Check(maximum <= 1 && w.Errors == 0 && trucks.All(t => !t.Dead),
                $"GateMinerArbitration: resume, projectile-hit withdrawal and objective/guard share one order writer per truck per tick over 60 seconds (max {maximum})");
            Check(w.SettleCounts.Count == 0,
                $"GateMinerArbitration: simultaneous resume/hit with objective and support guard has zero settles ({Settles(w)})");
        }
    }
}
