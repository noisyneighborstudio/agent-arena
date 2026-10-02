using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>Scenario tests: build a world, set up a situation, step the sim, check the outcome. Run with --test.</summary>
    public static class Tests
    {
        static int failures;

        static void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok) failures++;
        }

        static void Run(World w, float seconds) { for (int i = 0; i < seconds * World.TickRate; i++) w.Step(); }

        static Dictionary<string, object> Cmd(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2)
                d[(string)kv[i]] = kv[i + 1] is int n ? (double)n : kv[i + 1] is int[] ids ? ids.Select(x => (object)(double)x).ToList() : kv[i + 1];
            return d;
        }

        static bool Ok(JObj r) => r["ok"] is bool b && b;

        public static int Run()
        {
            RepairTruck();
            Medic();
            Console.WriteLine(failures == 0 ? "\nAll tests passed." : $"\n{failures} test(s) FAILED.");
            return failures == 0 ? 0 : 1;
        }

        static void RepairTruck()
        {
            var w = new World(2, 7);
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var truck = w.SpawnUnit(0, "repair_truck", hq);
            var tank = w.SpawnUnit(0, "light_tank", hq);
            tank.Hp = 100;
            hq.Hp = 2000;
            w.Teams[0].Add("steel", 1000);
            float steel0 = w.Teams[0].Amount("steel");

            Run(w, 20);
            Check(tank.Hp >= tank.Def.MaxHp - 1, $"idle repair truck auto-repairs a nearby tank ({tank.Hp:0}/{tank.Def.MaxHp})");
            Check(w.Teams[0].Amount("steel") < steel0, $"repairs cost steel ({steel0} -> {w.Teams[0].Amount("steel")})");
            Run(w, 60);
            Check(hq.Hp >= hq.Def.MaxHp - 1, $"it then repairs the damaged command center ({hq.Hp:0}/{hq.Def.MaxHp})");

            // No steel: stalls instead of repairing for free.
            tank.Hp = 100;
            w.Teams[0].Stock["steel"] = 0;
            Run(w, 10);
            Check(tank.Hp < 110, $"without steel the truck can't repair ({tank.Hp:0})");

            var rifle = w.SpawnUnit(0, "rifleman", hq);
            rifle.Hp = 50;
            var r = Commands.Execute(w, 0, Cmd("type", "repair", "units", new[] { truck.Id }, "target", rifle.Id));
            Check(!Ok(r) && r["error"].ToString().Contains("medic"), $"repair truck refuses infantry: {r["error"]}");
            var enemy = w.Owned(1).First(e => !e.IsStructure);
            r = Commands.Execute(w, 0, Cmd("type", "repair", "units", new[] { truck.Id }, "target", enemy.Id));
            Check(!Ok(r), $"can't repair enemy units: {r["error"]}");
        }

        static void Medic()
        {
            var w = new World(2, 7);
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            var medic = w.SpawnUnit(0, "medic", hq);
            var rifle = w.Owned(0).First(e => e.Def.Key == "rifleman");
            rifle.Hp = 20;
            float steel0 = w.Teams[0].Amount("steel");
            Run(w, 15);
            Check(rifle.Hp >= rifle.Def.MaxHp - 1, $"idle medic auto-heals a nearby rifleman ({rifle.Hp:0}/{rifle.Def.MaxHp})");
            Check(w.Teams[0].Amount("steel") == steel0, "healing is free");

            var tank = w.SpawnUnit(0, "light_tank", hq);
            tank.Hp = 100;
            Run(w, 10);
            Check(tank.Hp <= 100.5f, "medic leaves damaged vehicles alone");
            var r = Commands.Execute(w, 0, Cmd("type", "heal", "units", new[] { medic.Id }, "target", tank.Id));
            Check(!Ok(r) && r["error"].ToString().Contains("repair truck"), $"medic refuses vehicles: {r["error"]}");

            // Explicit order across the map: medic walks over and heals.
            var far = w.SpawnUnit(0, "rifleman", hq);
            far.Pos = far.PrevPos = new Vec2(20.5f, 20.5f);
            far.Hp = 30;
            medic.Pos = medic.PrevPos = new Vec2(10.5f, 6.5f);
            r = Commands.Execute(w, 0, Cmd("type", "heal", "units", new[] { medic.Id }, "target", far.Id));
            Check(Ok(r), $"heal order accepted: {r["result"]}");
            Run(w, 25);
            Check(far.Hp >= far.Def.MaxHp - 1, $"medic travels to and heals a distant rifleman ({far.Hp:0}/{far.Def.MaxHp})");
            Check(medic.Order == Order.Idle, "medic goes idle once the patient is healed");
        }
    }
}
