using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        public static int RunMinerPolicy()
        {
            MinerProtection(); GateMinerProtection(); GateMinerArbitration();
            Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} miner policy tests FAILED.");
            return failures == 0 ? 0 : 1;
        }

        static void MinerProtection()
        {
            MinerPolicyData(); MinerPolicyDefaults(); MinerPolicyTriggers(); MinerPolicyFog();
            MinerPolicyOverride(); MinerPolicyPersistence(); MinerPolicyCooldown(); MinerPolicyPrivateField(); MinerPolicyDeaths(); MinerPolicyNewcomers();
            MinerPolicyResumeHitArbitration();
        }

        static (Game game, List<Entity> trucks, Entity scout) MinerScene(int count = 3)
        {
            var game = new Game(new GameConfig { Seed = 7, MapSize = 80, Controllers = new[] { "llm", "llm" } });
            var w = game.World;
            foreach (var t in w.Teams) t.ProtectedUntil = 0;
            foreach (var e in w.Entities.Where(e => !e.IsStructure).ToList()) w.Remove(e);
            Array.Fill(w.Map.Tiles, Terrain.Grass);
            Array.Clear(w.Map.Ore, 0, w.Map.Ore.Length); Array.Clear(w.Map.OreBase, 0, w.Map.OreBase.Length);
            Array.Clear(w.Map.Salvage, 0, w.Map.Salvage.Length);
            for (int y = 30; y <= 33; y++) for (int x = 30; x <= 33; x++)
            {
                int i = w.Map.Idx(x, y);
                w.Map.Ore[i] = w.Map.OreBase[i] = 500;
                w.Map.OreType[i] = w.Map.OreBaseType[i] = Map.Iron;
            }
            w.Map.FieldsChanged();
            var trucks = new List<Entity>();
            for (int i = 0; i < count; i++)
            {
                var at = new Vec2(30.5f + i, 31.5f);
                var truck = At(Mine0(w, "mining_truck"), at);
                w.SetOrder(truck, Order.Harvest, at); trucks.Add(truck);
            }
            var scout = At(Mine0(w, "geological_surveyor"), new Vec2(31.5f, 28.5f));
            scout.Fuel = scout.FuelMax;
            Array.Fill(w.Teams[0].Explored, true); Array.Fill(w.Teams[0].Visible, true);
            return (game, trucks, scout);
        }

        static JObj EnableMiners(World w, float delay = 20, object to = null) => Commands.Execute(w, 0,
            Cmd("type", "set_policy", "miners", Cmd("withdraw_on_attack", true, "to", to ?? new List<object> { 20d, 31d }, "resume_after_s", delay)));

        static void MinerPolicyDefaults()
        {
            var (game, trucks, _) = MinerScene(); var w = game.World;
            string before = Snap(game);
            Check(!Ok(Commands.Execute(w, 0, Cmd("type", "set_policy", "miners", Cmd("withdraw_on_attack", true, "to", "base")))),
                "miner protection requires an agent-supplied resume delay");
            Check(!Ok(EnableMiners(w, float.PositiveInfinity)), "miner protection rejects infinite delay");
            Check(!Ok(EnableMiners(w, 20, new List<object> { -1d, 10d })), "miner protection rejects destinations outside the map");
            Check(!Ok(EnableMiners(w, 20, new List<object> { -0.1d, 10d })), "miner protection rejects a negative fractional coordinate too");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "set_policy", "miners", Cmd("withdraw_on_attack", false)))) && Snap(game) == before,
                "disabling the default-off policy does not change existing sim state");
            w.Hurt(trucks[0], 1, null, 1);
            Check(trucks.All(t => t.Order == Order.Harvest) && w.Teams[0].Miners.Fields.Count == 0,
                "unconfigured miners do not withdraw when attacked");
            var copy = Resume(Snap(game));
            MinerPolicies.Step(w, 0);
            Check(Snap(game) == Snap(copy), "default-off policy stepping leaves the full snapshot identical");
            EnableMiners(w, 12, "base");
            Commands.Execute(w, 0, Cmd("type", "set_policy", "miners", Cmd("withdraw_on_attack", false)));
            copy = Resume(Snap(game));
            Check(!copy.World.Teams[0].Miners.WithdrawOnAttack && copy.World.Teams[0].Miners.Fields.Count == 0 &&
                Json.Write(copy.World.Teams[0].Miners.View()) == Json.Write(w.Teams[0].Miners.View()),
                "disabled miner configuration still round-trips when no field was ever registered");
        }

        static void MinerPolicyTriggers()
        {
            var (game, trucks, _) = MinerScene(); var w = game.World;
            // The second field is distinct, even with the same ore type.
            int i = w.Map.Idx(50, 50); w.Map.Ore[i] = w.Map.OreBase[i] = 500;
            w.Map.OreType[i] = w.Map.OreBaseType[i] = Map.Iron; w.Map.FieldsChanged();
            var other = At(Mine0(w, "mining_truck"), new Vec2(50.5f, 50.5f)); w.SetOrder(other, Order.Harvest, other.Pos);
            Check(Ok(EnableMiners(w)), "agent can enable miner protection with its destination and delay");
            w.Observe.Log(0); w.Observe.Log(1);
            w.Hurt(trucks[0], 1, null, 1);
            var f = w.Teams[0].Miners.Fields.Single(field => field.Withdrawn);
            Check(trucks.All(t => t.Order == Order.Move) && other.Order == Order.Harvest && f.Trucks.Count == trucks.Count,
                "an attacked truck withdraws every working truck of that field, leaving another field alone");
            Check(trucks.Select(t => t.OrderPos).Distinct().Count() == trucks.Count,
                "withdrawn trucks own distinct destination slots");
            var ev = Events(w, 0).Single(e => e.Str("kind") == "miners_withdrawn");
            Check(ev.Str("cat") == "orders" && ev.Str("reason") == "hit" && ev.In("hit_truck") == trucks[0].Id && ev.Arr("threats_seen").Count == 0,
                "withdrawal explains the own hit without inventing hidden attacker facts");
            Check(!Events(w, 1).Any(e => e.Str("kind") == "miners_withdrawn"), "miner policy interventions stay in the owning team's feed");

            var single = MinerScene(1); w = single.game.World;
            EnableMiners(w);
            var foe = Enemy(w, "rifleman", single.trucks[0].Pos + new Vec2(4, 0)); foe.Cooldown = 1000;
            float range = foe.Def.Weapon.Range;
            At(foe, single.trucks[0].Pos + new Vec2(range + 0.1f, 0));
            MinerPolicies.Step(w, 0);
            Check(single.trucks[0].Order == Order.Harvest, "visible enemies outside weapon range do not trigger withdrawal");
            At(foe, single.trucks[0].Pos + new Vec2(range - 0.1f, 0));
            MinerPolicies.Step(w, 0);
            Check(single.trucks[0].Order == Order.Move && Events(w, 0).Any(e => e.Str("kind") == "miners_withdrawn" && e.Str("reason") == "seen_weapon_range"),
                "a visible enemy entering its weapon range proactively withdraws miners");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "set_policy", "miners", Cmd("withdraw_on_attack", false)))) &&
                single.trucks[0].Order == Order.Move && !MinerPolicies.Controls(w, single.trucks[0]),
                "turning protection off releases policy ownership and preserves the current order");
        }

        static void MinerPolicyFog()
        {
            var (game, trucks, _) = MinerScene(1); var w = game.World;
            EnableMiners(w, 2);
            var foe = Enemy(w, "rifleman", trucks[0].Pos + new Vec2(4, 0)); foe.Cooldown = 1000;
            Array.Clear(w.Teams[0].Visible, 0, w.Teams[0].Visible.Length);
            MinerPolicies.Step(w, 0);
            Check(trucks[0].Order == Order.Harvest, "an enemy in true weapon range but hidden by fog cannot trigger miner policy");
            Array.Fill(w.Teams[0].Visible, true); MinerPolicies.Step(w, 0);
            var field = w.Teams[0].Miners.Fields.Single();
            At(foe, new Vec2(65, 65));
            Array.Clear(w.Teams[0].Visible, 0, w.Teams[0].Visible.Length);
            w.Tick += 40 * World.TickRate; MinerPolicies.Step(w, 0);
            Check(field.Withdrawn && field.ClearSince < 0, "a raid lost in fog stays a threat; elapsed time alone never resumes mining");
            // Knowing the field is empty is not enough when the remembered attacker's last spot is hidden.
            foreach (var tile in field.Tiles) w.Teams[0].Visible[w.Map.Idx(tile.X, tile.Y)] = true;
            MinerPolicies.Step(w, 0);
            Check(field.Withdrawn && field.ClearSince < 0, "a remembered unseen attacker near the field must also be seen gone");
            Array.Fill(w.Teams[0].Visible, true); MinerPolicies.Step(w, 0);
            Check(field.Withdrawn && field.ClearSince == w.Time, "seeing the field and raid gone starts the agent's resume timer");
            w.Tick += World.TickRate; MinerPolicies.Step(w, 0);
            Check(field.Withdrawn, "miners stay withdrawn during the configured clear-field delay");
            w.Tick += 2 * World.TickRate; MinerPolicies.Step(w, 0);
            Check(!field.Withdrawn && trucks[0].Order == Order.Harvest && Events(w, 0).Any(e => e.Str("kind") == "miners_resumed"),
                "only a visibly clear field held clear for the supplied delay resumes mining");
        }

        static void MinerPolicyOverride()
        {
            var (game, trucks, _) = MinerScene(); var w = game.World;
            EnableMiners(w, 1); w.Hurt(trucks[0], 1, null, 1);
            var dest = new Vec2(45, 20);
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "move", "units", new[] { trucks[0].Id }, "x", dest.X, "y", dest.Y))),
                "a direct order can take over a withdrawn miner");
            w.Tick += 30 * World.TickRate; MinerPolicies.Step(w, 0); w.Tick += 2 * World.TickRate; MinerPolicies.Step(w, 0);
            Check(trucks[0].Order == Order.Move && trucks[0].OrderPos.Equals(dest) && trucks.Skip(1).All(t => t.Order == Order.Harvest),
                "miner resumption preserves explicit orders on named trucks while resuming the rest");
            Check(Events(w, 0).Any(e => e.Str("kind") == "detached_by_order" && e.In("unit") == trucks[0].Id),
                "taking over a policy miner explains the direct-order detachment");
            Check(Ok(Commands.Execute(w, 0, Cmd("type", "harvest", "units", new[] { trucks[0].Id }, "x", 31, "y", 31))) &&
                !w.Teams[0].Miners.Detached.Contains(trucks[0].Id), "a later harvest command explicitly hands a detached miner back to the policy");
            w.Hurt(trucks[0], 1, null, 1);
            Check(MinerPolicies.Controls(w, trucks[0]), "a returned miner is protected on the next attack");
        }

        static void MinerPolicyPersistence()
        {
            var (game, trucks, _) = MinerScene(); var w = game.World;
            EnableMiners(w, 3); w.Hurt(trucks[0], 1, null, 1);
            Commands.Execute(w, 0, Cmd("type", "stop", "units", new[] { trucks[0].Id }));
            MinerPolicies.Step(w, 0);
            var copy = Resume(Snap(game));
            Check(Json.Write(w.Teams[0].Miners.Save()) == Json.Write(copy.World.Teams[0].Miners.Save()),
                "snapshot round-trip retains active miner policy, withdrawal targets, cooldown and detached trucks");
            int from = w.Tick;
            Step(game, 25); Step(copy, 25);
            Check(SnapForCompare(game, from) == SnapForCompare(copy, from),
                "withdrawn miners continue identically after a save and resume");
            Check(((JObj)StateData.Team(w, 0)["policies"])["miners"] is JObj &&
                ((JObj)w.Observe.Log(0).Snapshot()["policies"])["miners"] is JObj,
                "state and observation snapshots expose the owning team's configured miner policy");
        }

        static void MinerPolicyCooldown()
        {
            var (game, trucks, _) = MinerScene(1); var w = game.World;
            EnableMiners(w, 0); w.Hurt(trucks[0], 1, null, 1); MinerPolicies.Step(w, 0);
            var field = w.Teams[0].Miners.Fields.Single();
            w.Tick += 19 * World.TickRate; MinerPolicies.Step(w, 0);
            Check(field.Withdrawn, "even a zero-delay policy cannot shuttle a field inside the 20-second withdrawal cooldown");
            w.Tick += World.TickRate; MinerPolicies.Step(w, 0);
            Check(!field.Withdrawn && trucks[0].Order == Order.Harvest, "a visibly clear zero-delay field resumes once its withdrawal cooldown expires");
            int withdrawals = Events(w, 0).Count(e => e.Str("kind") == "miners_withdrawn");
            w.Hurt(trucks[0], 1, null, 1);
            Check(field.Withdrawn && Events(w, 0).Count(e => e.Str("kind") == "miners_withdrawn") == withdrawals + 1,
                "the same field can withdraw again after 20 seconds, never sooner");
        }

        static void MinerPolicyPrivateField()
        {
            var (game, trucks, _) = MinerScene(1); var w = game.World;
            var assignment = Int2.Of(trucks[0].Pos); var hiddenRoot = new Int2(45, 31);
            for (int x = 34; x <= hiddenRoot.X; x++)
            {
                int i = w.Map.Idx(x, 31); w.Map.Ore[i] = w.Map.OreBase[i] = x == hiddenRoot.X ? 900 : 500;
                w.Map.OreType[i] = w.Map.OreBaseType[i] = Map.Iron;
            }
            w.Map.FieldsChanged(); Array.Clear(w.Teams[0].Explored, 0, w.Teams[0].Explored.Length);
            w.Teams[0].Explored[w.Map.Idx(assignment.X, assignment.Y)] = true;
            EnableMiners(w); w.Hurt(trucks[0], 1, null, 1);
            var field = w.Teams[0].Miners.Fields.Single();
            Check(w.Map.FieldRoot(assignment).Equals((Int2?)hiddenRoot) && field.Root.Equals(assignment) && field.Tiles.SequenceEqual(new[] { assignment }),
                "an unexplored canonical ore root stays scratch-only; saved policy uses an own miner's known tile");
            var packet = Events(w, 0).Single(e => e.Str("kind") == "miners_withdrawn");
            Check(packet.Arr("field").SequenceEqual(new object[] { (double)assignment.Center.X, (double)assignment.Center.Y }),
                "withdrawal facts do not reveal hidden ore field geography");
        }

        static void MinerPolicyDeaths()
        {
            var (game, trucks, _) = MinerScene(1); var w = game.World;
            EnableMiners(w, 0);
            var foe = Enemy(w, "stealth_bomber", trucks[0].Pos + new Vec2(0.5f, 0)); foe.Cooldown = 1000;
            w.Teams[0].Detected.Add(foe.Id); MinerPolicies.Step(w, 0);
            var field = w.Teams[0].Miners.Fields.Single();
            Check(field.Withdrawn && field.Threats.ContainsKey(foe.Id), "a currently detected stealth attacker can trigger miner protection");
            w.Teams[0].Detected.Remove(foe.Id); w.Tick += 30 * World.TickRate;
            MinerPolicies.Step(w, 0);
            Check(field.Withdrawn && field.Threats.ContainsKey(foe.Id), "visible terrain alone cannot disprove a remembered stealth attacker");
            w.Hurt(foe, foe.Def.MaxHp + 1, null, 0); MinerPolicies.Step(w, 0);
            Check(field.Withdrawn && field.Threats.ContainsKey(foe.Id), "an enemy dying while undetected cannot clear policy threat memory");

            var witnessed = MinerScene(1); w = witnessed.game.World; EnableMiners(w, 0);
            foe = Enemy(w, "stealth_bomber", witnessed.trucks[0].Pos + new Vec2(0.5f, 0)); foe.Cooldown = 1000;
            w.Teams[0].Detected.Add(foe.Id); MinerPolicies.Step(w, 0);
            field = w.Teams[0].Miners.Fields.Single();
            w.Feed = null; // death knowledge belongs to sim state, independent of an observation reader
            w.Hurt(foe, foe.Def.MaxHp + 1, null, 0);
            var copy = Resume(Snap(witnessed.game));
            Check(field.Threats.Count == 0 && copy.World.Teams[0].Miners.Fields.Single().Threats.Count == 0,
                "a witnessed enemy death clears saved threat memory immediately, even without an observation feed");
            w.Tick += 30 * World.TickRate; MinerPolicies.Step(w, 0);
            copy.World.Tick = w.Tick; MinerPolicies.Step(copy.World, 0);
            Check(!field.Withdrawn && !copy.World.Teams[0].Miners.Fields.Single().Withdrawn,
                "a snapshot taken immediately after a witnessed stealth death resumes the same policy as the original");
        }

        static void MinerPolicyNewcomers()
        {
            var (game, trucks, _) = MinerScene(); var w = game.World;
            EnableMiners(w); w.Tick = 4; w.Hurt(trucks[0], 1, null, 1);
            var oldGoals = trucks.Select(t => t.OrderPos).ToList();
            var field = w.Teams[0].Miners.Fields.Single(); float firstWithdrawal = field.LastWithdrawal;
            var fresh = At(Mine0(w, "mining_truck"), new Vec2(33.5f, 31.5f)); w.SetOrder(fresh, Order.Harvest, fresh.Pos);
            MinerPolicies.Step(w, 0);
            Check(MinerPolicies.Controls(w, fresh) && fresh.Order == Order.Move && field.LastWithdrawal == firstWithdrawal,
                "a new miner assigned to an already withdrawn field joins the withdrawal without restarting its cooldown");
            Check(oldGoals.SequenceEqual(trucks.Select(t => t.OrderPos)) && oldGoals.All(goal => Vec2.Dist(goal, fresh.OrderPos) >= 1.08f),
                "new policy miners receive unoccupied slots without reordering existing withdrawn miners");
            Commands.Execute(w, 0, Cmd("type", "stop", "units", new[] { trucks[0].Id }));
            Commands.Execute(w, 0, Cmd("type", "harvest", "units", new[] { trucks[0].Id }, "x", 31, "y", 31));
            MinerPolicies.Step(w, 0);
            Check(MinerPolicies.Controls(w, trucks[0]) && trucks[0].Order == Order.Move,
                "explicit harvest hands a miner back into an ongoing field withdrawal");
            Check(trucks.All(t => t.LastOrderBy == "miner_policy" && t.LastRetreatTick == w.Tick),
                "miner policy orders record their source and withdrawal tick for stale-command facts");
        }

        static (Game game, List<Entity> trucks) MinerResumeHitScene()
        {
            var (game, trucks, _) = MinerScene(); var w = game.World;
            EnableMiners(w, 0); w.Hurt(trucks[0], 1, null, 1);
            var field = w.Teams[0].Miners.Fields.Single(); field.ClearSince = 0;
            // A shot already in flight lands after the policy's first eligible resume evaluation.
            // Its shooter is gone: no currently visible enemy can pre-empt the hit during policy evaluation.
            w.Tick = 20 * World.TickRate - 1;
            w.Projectiles.Add(new Projectile { Team = 1, TargetId = trucks[0].Id,
                Pos = trucks[0].Pos, TargetPos = trucks[0].Pos, Weapon = Defs.Get("light_tank").Weapon });
            return (game, trucks);
        }

        static void MinerPolicyResumeHitArbitration()
        {
            var (game, trucks) = MinerResumeHitScene(); var w = game.World;
            var copy = Resume(Snap(game)); var touched = new List<Entity>(); w.Touched = touched;
            try { w.Step(); } finally { w.Touched = null; }
            copy.World.Step();
            var field = w.Teams[0].Miners.Fields.Single();
            Check(trucks.All(t => touched.Count(e => e.Id == t.Id) <= 1),
                "MinerPolicyResumeHitArbitration: a policy resume and later projectile hit write each truck's order at most once in a tick");
            Check(field.Withdrawn && field.LastWithdrawal == w.Time && trucks.All(t => t.Order != Order.Harvest) &&
                !Events(w, 0).Any(e => e.Str("kind") == "miners_resumed"),
                "MinerPolicyResumeHitArbitration: the later hit wins without executing or announcing the staged harvest resume");
            Check(Json.Write(w.Teams[0].Miners.Save()) == Json.Write(copy.World.Teams[0].Miners.Save()),
                "MinerPolicyResumeHitArbitration: snapshot replay resolves simultaneous resume and hit identically");
        }

        static void MinerPolicyData()
        {
            var policy = new MinerPolicy();
            Check(!policy.WithdrawOnAttack && policy.Fields.Count == 0, "miner protection is off by default");
            policy.WithdrawOnAttack = true; policy.ToBase = true; policy.ResumeAfterS = 20;
            var field = new MinerField { Root = new Int2(10, 11), Withdrawn = true, LastWithdrawal = 4, ClearSince = 8 };
            field.Tiles.Add(field.Root);
            field.Trucks[3] = new MinerResume { Target = field.Root.Center, HarvestType = Map.Iron, HarvestTile = field.Root };
            field.Threats[4] = new MinerThreat { Position = new Vec2(12, 13), Range = 5, Stealth = true };
            policy.Fields.Add(field); policy.Detached.Add(7);
            var copy = new MinerPolicy(); copy.Load((Dictionary<string, object>)Json.Parse(Json.Write(policy.Save())));
            Check(Json.Write(policy.Save()) == Json.Write(copy.Save()), "miner policy persists withdrawal, clear timer, threat memories, targets and overrides");
        }
    }
}
