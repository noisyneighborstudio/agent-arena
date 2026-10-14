using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static void GateSupportRelease()
        {
            var (game, units) = GuardScene("light_tank", "rifleman"); var w = game.World;
            At(units[0], new Vec2(44.5f, 30.5f));
            Commands.Execute(w, 0, Cmd("type", "move", "squad", "west", "x", 65, "y", 30));
            GuardConfig(w, "hold_for_support", Cmd("within", 6)); Run(w, 0.1f);
            var guard = w.Teams[0].Squads["west"].Guards;
            Check(guard.Units[units[0].Id].SupportHeld, "support-release gate starts with the leader held for its rear squadmate");
            w.Remove(units[1]);
            int maximumWrites = 0;
            for (int i = 0; i < 60 * World.TickRate; i++)
            {
                var touched = new List<Entity>(); w.Touched = touched;
                try { w.Step(); } finally { w.Touched = null; }
                maximumWrites = Math.Max(maximumWrites, touched.Count(e => e.Id == units[0].Id));
            }
            Check(w.SettleCounts.Count == 0 && w.Errors == 0 && maximumWrites <= 1 &&
                !guard.Units[units[0].Id].SupportHeld && units[0].Pos.X > 60 &&
                GuardNotes(guard, "guard_triggered", "hold_for_support") == 1 &&
                GuardNotes(guard, "guard_released", "hold_for_support") == 1,
                $"support-release gate: losing the last supporter resumes once, with one order writer and zero settles over 60 seconds (max {maximumWrites}; {Settles(w)})");
        }
    }
}
