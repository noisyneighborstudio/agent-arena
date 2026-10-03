using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>The kitchen sink showcase room (Sim/KitchenSink.cs): it builds, shows everything, and holds every state for minutes.</summary>
    public static partial class Tests
    {
        /// <summary>--test-kitchen-sink: just this test (it's the slow one).</summary>
        public static int RunKitchenSink()
        {
            KitchenSinkRoom();
            Console.WriteLine(failures == 0 ? "\nAll tests passed." : $"\n{failures} test(s) FAILED.");
            return failures == 0 ? 0 : 1;
        }

        static void KitchenSinkRoom()
        {
            World w;
            try { w = KitchenSink.Build(); }
            catch (Exception ex) { Check(false, $"the kitchen sink builds: {ex.Message}"); return; }
            var k = w.Showcase;
            Check(k != null && k.Districts.Count >= 12 && k.Districts.All(d => d.Items.Count > 0), $"the kitchen sink builds, with {k?.Districts.Count} districts, each with exhibits");
            var shown = new HashSet<string>(w.Entities.Where(e => !e.Dead).Select(e => e.Def.Key));
            var missing = Defs.All.Keys.Where(key => !shown.Contains(key)).ToList();
            Check(missing.Count == 0, $"every unit and structure def is on show (missing: {string.Join(", ", missing)})");
            Check(w.Entities.Any(e => e.Def.OwnerTeam >= 0 && e.Team == KitchenSink.Blue) && w.Entities.Any(e => e.Def.OwnerTeam >= 0 && e.Team == KitchenSink.Red), "agent-invented units for both teams");
            var cat = Json.Write(k.Catalog());
            Check(cat.Contains("\"districts\"") && cat.Contains("\"legend\"") && cat.Contains("Raging"), "the catalog lists districts and the legend");
            foreach (var d in k.Districts)
                foreach (var i in d.Items.Append(new KitchenSink.Exhibit { Name = d.Name, Focus = d.Focus }))
                    if (!w.Map.InBounds((int)i.Focus.X, (int)i.Focus.Y)) Check(false, $"{d.Id}: {i.Name} is on the map ({i.Focus})");

            w.Step();
            var fires = w.Entities.Where(e => e.IsStructure && e.Team == KitchenSink.Red && e.Hp < e.Def.MaxHp * 0.99f).ToList();
            var gallery = w.Entities.First(e => e.Def.Key == "rifleman" && e.Team == KitchenSink.Blue && e.Order == Order.Idle);
            var galleryAt = gallery.Pos;
            var oreDocked = new HashSet<int>();
            bool queued = false, landed = false, siteRising = false;
            int liveAt60 = 0, maxLive = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 1; i <= 360 * World.TickRate; i++)
            {
                w.Step();
                foreach (var e in w.Entities)
                {
                    if (e.Dead) continue;
                    if (e.IsHarvester && e.Order == Order.ReturnOre && e.Dock == DockStep.Unload && e.CargoType >= 0) oreDocked.Add(e.CargoType);
                    if (e.IsHarvester && e.Dock == DockStep.Queue) queued = true;
                    if (e.IsAir && e.Landed) landed = true;
                    if (e.IsStructure && e.Team == KitchenSink.Yellow && !e.IsComplete && e.BuildProgress > 0.5f && e.Def.Key == "barracks") siteRising = true;
                }
                if (i % World.TickRate == 0)
                {
                    int live = w.Entities.Count(e => !e.Dead);
                    if (i == 60 * World.TickRate) liveAt60 = live;
                    if (i > 60 * World.TickRate) maxLive = Math.Max(maxLive, live);
                }
            }
            double secs = sw.Elapsed.TotalSeconds;

            Check(w.Errors == 0, $"six minutes of the kitchen sink with no sim errors ({w.LastError}); {secs:0.0}s to simulate");
            Check(!w.GameOver && w.Teams.All(t => !t.Defeated), "nobody is defeated and the room never ends");
            Check(maxLive - liveAt60 < 25, $"the entity count holds steady ({liveAt60} at 1 min, at most {maxLive} after)");
            Check(Vec2.Dist(gallery.Pos, galleryAt) < 0.01f && gallery.Order == Order.Idle, "gallery units stay on their spot");
            Check(fires.Count >= 9 && fires.All(e => !e.Dead && e.Hp / e.Def.MaxHp is var f && (MathF.Abs(f - 0.45f) < 0.01f || MathF.Abs(f - 0.25f) < 0.01f || MathF.Abs(f - 0.08f) < 0.01f)),
                  $"the damage rows hold their health ({fires.Count} burning buildings)");
            Check(oreDocked.Count == 4, $"trucks back in and unload every ore type ({string.Join(", ", oreDocked.Select(o => Defs.Ores[o]))})");
            Check(queued, "trucks queue for a busy bay");
            int Count(string type) => w.EventCounts.TryGetValue(type, out var n) ? n : 0;
            Check(Count("trained") >= 100, $"the works keep rolling units out ({Count("trained")} trained)");
            Check(siteRising, "the construction site rises");
            Check(w.Teams[KitchenSink.Yellow].StructureQueue.Count == 1 && w.Entities.Count(e => !e.Dead && e.IsStructure && e.Team == KitchenSink.Yellow && !e.IsComplete) == 6,
                  "construction holds: one site building, a queued site and four stage sites");
            Check(Count("surveyed") >= 10, $"the surveyor keeps surveying ({Count("surveyed")} surveys)");
            Check(Count("drilled") >= 10, $"the drill rig keeps deploying ({Count("drilled")} deep mines)");
            Check(Count("boarded") >= 16 && Count("unloaded") >= 4, $"transports load and unload ({Count("boarded")} boardings, {Count("unloaded")} unloads)");
            Check(landed && Count("refuelled") >= 5, $"an aircraft lands and refuels ({Count("refuelled")} times)");
            Check(Count("destroyed") >= 100, $"wrecks and explosions keep coming ({Count("destroyed")} destroyed events)");
            var firing = w.Entities.Count(e => !e.Dead && e.Team == KitchenSink.Blue && e.IsArmed && w.Time - e.LastFiredAt < 10f);
            Check(firing >= 17, $"every weapon in the combat range is firing ({firing} shooters fired in the last 10 s)");
            Check(w.Entities.Where(e => !e.Dead && e.Team == KitchenSink.Red && !e.IsStructure).All(e => e.Hp >= e.Def.MaxHp), "targets take no damage");
            Check(w.Teams[KitchenSink.Green].LowPower && !w.Teams[KitchenSink.Blue].LowPower && !w.Teams[KitchenSink.Red].LowPower && !w.Teams[KitchenSink.Yellow].LowPower,
                  $"Lime is on low power and everyone else is powered (blue {w.Teams[0].PowerUsed}/{w.Teams[0].PowerProduced}, red {w.Teams[1].PowerUsed}/{w.Teams[1].PowerProduced}, lime {w.Teams[2].PowerUsed}/{w.Teams[2].PowerProduced}, lemon {w.Teams[3].PowerUsed}/{w.Teams[3].PowerProduced})");
            var strandedTank = w.Entities.FirstOrDefault(e => !e.Dead && e.Stranded);
            Check(strandedTank != null && strandedTank.Fuel == 0, "the stranded tank stays stranded");
            Check(w.Entities.Where(e => !e.Dead && !e.IsStructure && e.Def.UsesFuel && !e.Stranded && e.Order != Order.Refuel && !e.Landed).All(e => e.Fuel >= e.FuelMax * 0.59f), "everything else keeps its fuel");
        }
    }
}
