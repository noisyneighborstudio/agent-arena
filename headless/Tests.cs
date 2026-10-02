using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Pez.Api;
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
            Alerts();
            InterruptibleWait();
            CommanderOrders();
            Transports();
            Engineers();
            Mines();
            Specialists();
            DefendersRespond();
            UnarmedAttackMove();
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
        static Entity Enemy(World w, string key, Vec2 at)
        {
            var e = w.SpawnUnit(1, key, w.Owned(1).First(x => x.Def.Key == "command_center"));
            e.Pos = e.PrevPos = e.GuardPos = at;
            return e;
        }

        static void Alerts()
        {
            var w = new World(2, 7);
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            // Clear out the defenders so the raid actually reaches the building, and make the raid deliberate.
            foreach (var d in w.Owned(0).Where(e => e.IsArmed).ToList()) w.Remove(d);
            var raider = Enemy(w, "light_tank", hq.Center + new Vec2(4.5f, 0.5f));
            w.UpdateVisibility(); // the raider was teleported in; let Red's fog catch up before ordering the attack
            w.SetOrder(raider, Order.Attack, hq.Center, hq.Id);
            var guard = w.SpawnUnit(0, "heavy_tank", hq);
            guard.Pos = guard.PrevPos = guard.GuardPos = hq.Center + new Vec2(-14f, -3f); // out of range, but close enough to help
            Run(w, 6);
            var att = w.Alerts.Active(w, 0).Where(a => a.Kind == "base_under_attack").ToList();
            Check(att.Count == 1 && att[0].Priority == Priority.Critical, $"enemy tank shelling the HQ raises one CRITICAL base_under_attack alert (got {att.Count})");
            Check(att.Count == 1 && att[0].Count > 2, $"repeated hits merge into that alert ({(att.Count == 1 ? att[0].Count : 0)} hits)");
            var text = att.Count == 1 ? AlertLog.Describe(w, att[0]) : "";
            Console.WriteLine("      " + text);
            Check(text.Contains("light_tank") && text.Contains("command_center"), "description names the attacker and the building being hit");
            Check(text.Contains("your combat units within 18 tiles"), "description lists units that could respond");
            Check(w.Alerts.Active(w, 0).Any(a => a.Kind == "enemy_near_base" && a.Priority == Priority.High), "enemy near base raises a HIGH alert");
            Check(!w.Alerts.Active(w, 1).Any(a => a.Priority >= Priority.High), "the attacker's own side gets no priority alert for a fight it started");
            var state = Json.Write(StateView.TeamState(w, 0));
            Check(state.Contains("BASE UNDER ATTACK"), "get_state carries the alert");

            // Ambush vs. a fight you picked.
            var w2 = new World(2, 7);
            var hq2 = w2.Owned(0).First(e => e.Def.Key == "command_center");
            var mine = w2.SpawnUnit(0, "light_tank", hq2);
            mine.Pos = mine.PrevPos = mine.GuardPos = new Vec2(40.5f, 40.5f);
            w2.SetOrder(mine, Order.Move, new Vec2(41.5f, 40.5f)); // driving somewhere, not looking for a fight
            Enemy(w2, "rocket_soldier", new Vec2(44.5f, 40.5f));
            Run(w2, 4);
            Check(w2.Alerts.Active(w2, 0).Any(a => a.Kind == "units_ambushed" && a.Priority == Priority.High), "a unit hit while moving raises HIGH units_ambushed");
        }

        static void InterruptibleWait()
        {
            var game = new Game(new GameConfig { Seed = 7, Controllers = new[] { "llm", "llm" } });
            var api = new ApiServer(7799);
            api.Start();
            Entity raiderToPlace = null;
            bool stop = false;
            var loop = new Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
                while (!stop)
                {
                    double now = sw.Elapsed.TotalSeconds;
                    game.Advance((float)(now - last)); last = now;
                    // All sim mutation happens on this thread, like the real host.
                    if (now > 2 && raiderToPlace == null)
                    {
                        var hq = game.World.Owned(0).First(e => e.Def.Key == "command_center");
                        raiderToPlace = Enemy(game.World, "light_tank", hq.Center + new Vec2(4.5f, 0.5f));
                    }
                    api.Pump(game);
                    Thread.Sleep(5);
                }
            }) { IsBackground = true };
            loop.Start();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            var t0 = DateTime.UtcNow;
            var body = http.GetStringAsync("http://127.0.0.1:7799/api/wait?team=0&seconds=25&since=0").Result;
            double took = (DateTime.UtcNow - t0).TotalSeconds;
            var r = (Dictionary<string, object>)Json.Parse(body);
            Check(r["interrupted"] is bool b && b && took < 8, $"wait(25s) returns early when the base is attacked ({took:0.0}s)");
            Check(body.Contains("BASE UNDER ATTACK") || body.Contains("ENEMY NEAR BASE"), "the interrupted wait carries the alert");
            t0 = DateTime.UtcNow;
            // Red is fighting too, so only a critical alert should cut its wait short; nothing critical happens to Red here.
            body = http.GetStringAsync($"http://127.0.0.1:7799/api/wait?team=1&seconds=1.5&min=critical").Result;
            took = (DateTime.UtcNow - t0).TotalSeconds;
            r = (Dictionary<string, object>)Json.Parse(body);
            Check(r["interrupted"] is bool b2 && !b2 && took >= 1.4 && took < 4, $"with nothing happening, wait runs its full time ({took:0.0}s)");
            stop = true;
            api.Stop();
        }
        static void CommanderOrders()
        {
            var game = new Game(new GameConfig { Seed = 7, Controllers = new[] { "llm", "llm" }, Orders = new[] { "Rush with infantry.", "" } });
            var w = game.World;
            Check(w.Teams[0].StandingOrders == "Rush with infantry." && w.Teams[0].OrdersVersion == 1, "initial orders from the game config are applied");
            var st = Json.Write(StateView.TeamState(w, 0));
            Check(st.Contains("\"standing_orders\":\"Rush with infantry.\""), "get_state carries standing_orders");
            Check(Json.Write(StateView.TeamState(w, 1)).Contains("\"standing_orders\":\"none\""), "the other team sees only its own (none)");

            var api = new ApiServer(7798);
            api.Start();
            bool stop = false, sent = false;
            var loop = new Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
                while (!stop)
                {
                    double now = sw.Elapsed.TotalSeconds;
                    game.Advance((float)(now - last)); last = now;
                    if (now > 1.5 && !sent) { sent = true; game.World.SetOrders(1, "Expand to the uranium in the middle."); }
                    api.Pump(game);
                    Thread.Sleep(5);
                }
            }) { IsBackground = true };
            loop.Start();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            var t0 = DateTime.UtcNow;
            var body = http.GetStringAsync("http://127.0.0.1:7798/api/wait?team=1&seconds=20&orders_version=0").Result;
            double took = (DateTime.UtcNow - t0).TotalSeconds;
            var r = (Dictionary<string, object>)Json.Parse(body);
            Check(r["interrupted"] is bool b && b && took < 5, $"new orders cut a 20s wait short ({took:0.0}s)");
            Check(r["orders_changed"] is bool oc && oc && body.Contains("Expand to the uranium"), "the wait result carries the new orders");
            var post = http.PostAsync("http://127.0.0.1:7798/api/admin/orders?team=0", new StringContent("{\"text\":\"Turtle.\"}")).Result.Content.ReadAsStringAsync().Result;
            Check(post.Contains("\"orders_version\":2") && post.Contains("Turtle."), "POST /api/admin/orders replaces orders and bumps the version");
            stop = true;
            api.Stop();
        }
        static Entity Mine0(World w, string key) => w.SpawnUnit(0, key, w.Owned(0).First(x => x.Def.Key == "command_center"));
        static Entity At(Entity e, Vec2 p) { e.Pos = e.PrevPos = e.GuardPos = p; return e; }

        static void Transports()
        {
            var w = new World(2, 7);
            var apc = At(Mine0(w, "apc"), new Vec2(20.5f, 20.5f));
            var inf = Enumerable.Range(0, 6).Select(i => At(Mine0(w, "rifleman"), new Vec2(16.5f + i * 0.4f, 18.5f))).ToList();
            var r = Commands.Execute(w, 0, Cmd("type", "load", "units", inf.Select(i => i.Id).ToArray(), "transport", apc.Id));
            Check(Ok(r) && r["result"].ToString().Contains("left behind"), $"load fills 5 seats and leaves the 6th behind: {r["result"]}");
            Run(w, 10);
            Check(apc.Passengers.Count == 5 && inf.Count(i => i.IsCarried) == 5, $"5 riflemen boarded ({apc.Passengers.Count})");
            w.UpdateVisibility();
            Check(!w.IsVisibleTo(1, inf.First(i => i.IsCarried)), "passengers are invisible to the enemy");
            w.SetOrder(apc, Order.Move, new Vec2(30.5f, 20.5f));
            Run(w, 6);
            var rider = inf.First(i => i.IsCarried);
            Check(Vec2.Dist(rider.Pos, apc.Pos) < 0.01f, "passengers travel with the transport");
            var riders = inf.Where(i => i.IsCarried).ToList();
            r = Commands.Execute(w, 0, Cmd("type", "unload", "units", new[] { apc.Id }));
            Check(Ok(r) && riders.All(i => !i.IsCarried && Vec2.Dist(i.Pos, apc.Pos) < 2.5f), $"unload drops them beside the APC: {r["result"]}");

            // Death takes the passengers with it.
            var heli = At(w.SpawnUnit(0, "transport_chopper", w.Owned(0).First(x => x.IsStructure)), new Vec2(30.5f, 22.5f));
            foreach (var i in inf.Take(3)) { i.CarrierId = heli.Id; heli.Passengers.Add(i.Id); }
            w.Remove(heli);
            Check(inf.Take(3).All(i => i.Dead), "passengers die when their transport is destroyed");
        }

        static void Engineers()
        {
            var w = new World(2, 7);
            var ehq = w.Owned(1).First(e => e.Def.Key == "command_center");
            var eng = At(Mine0(w, "engineer"), ehq.Center + new Vec2(-4f, -3f));
            w.UpdateVisibility();
            var r = Commands.Execute(w, 0, Cmd("type", "capture", "units", new[] { eng.Id }, "target", ehq.Id));
            Check(!Ok(r) && r["error"].ToString().Contains("below 50%"), $"can't capture a healthy building: {r["error"]}");
            ehq.Hp = ehq.Def.MaxHp * 0.4f;
            foreach (var d in w.Owned(1).Where(e => e.IsArmed).ToList()) w.Remove(d); // no defenders
            r = Commands.Execute(w, 0, Cmd("type", "capture", "units", new[] { eng.Id }, "target", ehq.Id));
            Check(Ok(r), $"capture accepted at 40% HP: {r["result"]}");
            Run(w, 12);
            Check(ehq.Team == 0 && eng.Dead, $"engineer captures the enemy command center (owner now team {ehq.Team})");
            Check(w.Alerts.Active(w, 1).Any(a => a.Kind == "structure_lost" && a.Priority == Priority.Critical), "the victim gets a CRITICAL structure_lost alert");
            Run(w, 1);
            Check(w.Teams[1].Defeated, "losing its only structure to capture defeats the team");
        }

        static void Mines()
        {
            var w = new World(2, 7);
            w.Teams[0].Add("steel", 200);
            var layer = At(Mine0(w, "minelayer"), new Vec2(30.5f, 30.5f));
            var r = Commands.Execute(w, 0, Cmd("type", "lay_mines", "units", new[] { layer.Id }, "x", 34, "y", 30, "count", 2));
            Check(Ok(r), $"lay_mines accepted: {r["result"]}");
            Run(w, 10);
            var mines = w.Owned(0).Where(e => e.IsMine).ToList();
            Check(mines.Count == 2 && w.Teams[0].Amount("steel") == 140, $"2 mines laid for 60 steel (mines {mines.Count}, steel {w.Teams[0].Amount("steel")})");
            w.SetOrder(layer, Order.Move, new Vec2(20.5f, 20.5f));
            var tank = At(w.SpawnUnit(1, "light_tank", w.Owned(1).First(x => x.IsStructure)), new Vec2(44.5f, 30.5f));
            w.UpdateVisibility();
            Check(!w.IsVisibleTo(1, mines[0]), "mines are hidden from an enemy 10 tiles away");
            var own = At(Mine0(w, "light_tank"), new Vec2(34.5f, 32.5f));
            w.SetOrder(own, Order.Move, new Vec2(34.5f, 28.5f));
            Run(w, 3);
            Check(!mines[0].Dead && own.Hp == own.Def.MaxHp, "your own units drive over your mines safely");
            w.SetOrder(tank, Order.Move, new Vec2(30.5f, 30.5f));
            Run(w, 8);
            Check(tank.Dead || tank.Hp < tank.Def.MaxHp - 100, $"an enemy tank driving through the field hits a mine (hp {(tank.Dead ? 0 : (int)tank.Hp)})");
            Check(w.Owned(0).Count(e => e.IsMine) < 2, "the mine is used up");
        }

        static void Specialists()
        {
            var flak = Defs.Get("flak_track").Weapon;
            Check(!flak.CanHit(Defs.Get("light_tank")) && flak.CanHit(Defs.Get("gunship")), "flak hits aircraft but not ground units");
            var c4 = Defs.Get("commando").Weapon;
            Check(!c4.CanHit(Defs.Get("rifleman")) && c4.CanHit(Defs.Get("factory")), "commando C4 hits buildings but can't target infantry");
            Check(Defs.Get("sniper").Weapon.Damage >= Defs.Get("rifleman").MaxHp, "a sniper shot kills a rifleman outright");

            var w = new World(2, 7);
            var mam = At(Mine0(w, "mammoth_tank"), new Vec2(20.5f, 20.5f));
            mam.Hp = 100;
            Run(w, 150); // 6 HP/s from 100 to 900 takes ~133s
            Check(MathF.Abs(mam.Hp - mam.Def.MaxHp * 0.5f) < 1f, $"mammoth self-repairs to 50% and stops there ({mam.Hp:0}/{mam.Def.MaxHp})");

            var drone = Mine0(w, "recon_drone");
            w.SetOrder(drone, Order.Move, new Vec2(60.5f, 60.5f));
            Run(w, 20);
            Check(Vec2.Dist(drone.Pos, new Vec2(60.5f, 60.5f)) < 1f, "recon drone flies straight across the map");
        }
        /// <summary>The live bug: artillery shells a building from beyond sight while tanks nearby sit idle.</summary>
        static void DefendersRespond()
        {
            var w = new World(2, 7);
            var hq = w.Owned(0).First(e => e.Def.Key == "command_center");
            foreach (var d in w.Owned(0).Where(e => e.IsArmed).ToList()) w.Remove(d);
            var guardTank = At(Mine0(w, "heavy_tank"), hq.Center + new Vec2(4f, -3f));
            var home = guardTank.Pos;
            var busy = At(Mine0(w, "light_tank"), hq.Center + new Vec2(-3f, -3f));
            w.SetOrder(busy, Order.Move, new Vec2(60.5f, 6.5f)); // commanded far away: should stay on task
            // Just outside the HQ's 11.5-tile sight but inside artillery range of its footprint.
            var arty = At(w.SpawnUnit(1, "artillery", w.Owned(1).First(e => e.IsStructure)), hq.Center + new Vec2(11.6f, 3.2f));
            // A flying spotter over the HQ gives the artillery its target (the tank's cannon can't hit aircraft).
            var spotter = At(w.SpawnUnit(1, "recon_drone", w.Owned(1).First(e => e.IsStructure)), hq.Center + new Vec2(0f, 2f));
            w.SetOrder(spotter, Order.Move, spotter.Pos);
            w.UpdateVisibility();
            Check(!w.IsVisibleTo(0, arty), "the artillery starts out unseen (outside everyone's sight)");
            w.SetOrder(arty, Order.Attack, hq.Center, hq.Id);
            // Run until the first shell lands.
            for (int i = 0; i < 20 * 20 && hq.Hp >= hq.Def.MaxHp; i++) w.Step();
            Check(hq.Hp < hq.Def.MaxHp, "the artillery hits the command center from out of sight");
            Check(w.IsVisibleTo(0, arty), "the shot reveals the artillery to the defender (muzzle flash)");
            Check(guardTank.Order == Order.Attack || guardTank.Order == Order.AttackMove, $"the idle heavy tank responds ({guardTank.OrderName})");
            Check(busy.Order == Order.Move, "a unit on a commander's move order stays on task");
            Run(w, 25);
            Check(arty.Dead, $"the defender kills the artillery (artillery {(arty.Dead ? "dead" : $"hp {(int)arty.Hp}")})");
            Run(w, 25);
            Check(Vec2.Dist(guardTank.Pos, home) < 2.5f, $"then it returns to its post ({Vec2.Dist(guardTank.Pos, home):0.0} tiles away)");
            Run(w, 10);
            Check(!w.Teams[0].Revealed.Any(kv => kv.Value >= w.Time), "reveals expire");
            Check(Defs.Get("rifleman").Sight >= Defs.Get("rifleman").Weapon.Range + 1 && Defs.Get("gun_turret").Sight >= Defs.Get("gun_turret").Weapon.Range + 1, "units see at least weapon range + 1");
        }
        /// <summary>The live crash: an attack_move that includes medics and a repair truck froze the whole simulation.</summary>
        static void UnarmedAttackMove()
        {
            var w = new World(2, 7);
            var medic = Mine0(w, "medic");
            var truck = Mine0(w, "repair_truck");
            var tank = Mine0(w, "light_tank");
            var foe = At(w.SpawnUnit(1, "rifleman", w.Owned(1).First(e => e.IsStructure)), new Vec2(26.5f, 14.5f));
            var r = Commands.Execute(w, 0, Cmd("type", "attack_move", "units", new[] { medic.Id, truck.Id, tank.Id }, "x", 27, "y", 14));
            Check(Ok(r), "attack_move with unarmed units is accepted");
            Run(w, 20);
            Check(w.Errors == 0, $"no sim errors ({w.Errors}{(w.LastError != null ? ": " + w.LastError : "")})");
            Check(Vec2.Dist(medic.Pos, new Vec2(27, 14)) < 3f && Vec2.Dist(truck.Pos, new Vec2(27, 14)) < 3f, "unarmed units simply travel with the attack");
            Check(foe.Dead || foe.Hp < foe.Def.MaxHp, "the armed unit still fights");
        }
    }
}
