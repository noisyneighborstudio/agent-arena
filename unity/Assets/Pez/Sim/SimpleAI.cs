using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>
    /// A scripted opponent that climbs the tech tree, balances ore types, expands with outposts and
    /// attacks in waves. Uses only Commands.Execute (plus reading its own fogged view), like everyone else.
    /// </summary>
    public partial class SimpleAI
    {
        readonly int team;
        /// <summary>House AI in an open arena: builds and defends, never attacks.</summary>
        public readonly bool Passive;
        public int Team => team;
        float nextThink;
        int waveSize = 6;
        readonly Dictionary<int, Vec2> outpostTargets = new Dictionary<int, Vec2>();
        float nextProspect; // when to try prospecting again after the area ran out
        int prospectRadius = 40; // widens as the surveyors cover the ground around the base
        int knownSurface = -1; // surface ore in the fields it has explored (refreshed every 10s)
        readonly Dictionary<int, float> stuckSince = new Dictionary<int, float>(); // build-order steps it can't take yet (by place in the order): when each got stuck
        const float SkipAfter = 60f; // seconds a step may hold up the rest of the order before later ones go first
        bool wantCopper = true, wantCrystal, wantUranium; // ores the trucks go for: on below a low mark, off above a higher one
        bool wentDeep; // it has had a deep mine or a rig: keeps drilling when the surface looks fine again
        const int MaxDeepMines = 4; // pumping at once (each draws 50 power)

        static readonly string[] BuildOrder =
        {
            "power_plant", "mining_refinery", "barracks", "factory", "power_plant", "electronics_plant", "gun_turret",
            "mining_refinery", "power_plant", "optics_lab", "enrichment_plant", "power_plant", "radar_dome", "sam_site",
            "laser_tower", "power_plant", "composite_foundry", "airfield", "fusion_reactor", "gun_turret", "laser_tower", "sam_site",
        };

        public SimpleAI(int team, bool passive = false) { this.team = team; Passive = passive; }

        static Dictionary<string, object> Cmd(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2)
            {
                var v = kv[i + 1];
                d[(string)kv[i]] = v is int n ? (double)n : v is float f ? (double)f : v is IEnumerable<int> ids ? ids.Select(x => (object)(double)x).ToList() : v;
            }
            return d;
        }

        JObj Do(World w, params object[] kv) => Commands.Execute(w, team, Cmd(kv));

        /// <summary>Whether this team can get the item at all: a raw ore (trucks and deep mines bring it), or something it has built makes it.</summary>
        bool Obtainable(World w, string item) =>
            System.Array.IndexOf(Defs.Ores, item) >= 0 || w.Owned(team).Any(s => s.IsStructure && s.IsComplete && s.Def.Recipes.Any(r => r.Outputs.ContainsKey(item)));

        /// <summary>The first step of the build order not built yet (null when it has them all).</summary>
        static string FirstUnbuilt(System.Func<string, int> count)
        {
            var need = new Dictionary<string, int>();
            foreach (var k in BuildOrder)
            {
                need[k] = need.TryGetValue(k, out var c) ? c + 1 : 1;
                if (count(k) < need[k]) return k;
            }
            return null;
        }

        /// <summary>
        /// The structure to build next from the build order: the first one not built yet. If that one can't be taken (short of
        /// something, or a building it needs is gone) for SkipAfter seconds, or at once when it is short of a material nothing
        /// it owns makes yet, later steps go ahead of it, as long as they leave alone what it is short of; it comes first
        /// again as soon as it can be paid for. `head` is the first unbuilt step either way.
        /// </summary>
        string PickStep(World w, Team t, System.Func<string, int> count, out string head)
        {
            head = FirstUnbuilt(count);
            HashSet<string> short0 = null; // what the stuck steps ahead are short of
            bool skipping = false; // a step ahead has been given up on: the stuck ones after it are passed over at once
            var need = new Dictionary<string, int>();
            for (int i = 0; i < BuildOrder.Length; i++)
            {
                var k = BuildOrder[i];
                need[k] = need.TryGetValue(k, out var c) ? c + 1 : 1;
                if (count(k) >= need[k]) { stuckSince.Remove(i); continue; }
                var def = Defs.Get(k);
                var lacking = def.Cost.Where(kv => t.Amount(kv.Key) < kv.Value).Select(kv => kv.Key).ToList();
                if (lacking.Count == 0 && w.MissingPrereq(team, def) == null)
                {
                    stuckSince.Remove(i);
                    // (Power plants are built when the power runs short, not ahead of a stuck step.)
                    if (short0 == null || (k != "power_plant" && !def.Cost.Keys.Any(short0.Contains))) return k;
                    continue;
                }
                if (!stuckSince.TryGetValue(i, out var since)) stuckSince[i] = since = w.Time;
                if (!skipping && w.Time - since < SkipAfter && lacking.All(m => Obtainable(w, m))) return k; // worth waiting for
                skipping = true;
                short0 = short0 ?? new HashSet<string>();
                foreach (var m in lacking) short0.Add(m);
            }
            return head; // nothing behind it can go either: wait for it
        }

        public void Update(World w)
        {
            if (w.Time < nextThink || w.GameOver) return;
            nextThink = w.Time + 1f;
            var t = w.Teams[team];
            if (t.Defeated) return;
            var mine = w.Owned(team).ToList();
            var structures = mine.Where(e => e.IsStructure).ToList();
            var have = structures.GroupBy(s => s.Def.Key).ToDictionary(g => g.Key, g => g.Count());
            int Count(string k) => have.TryGetValue(k, out var c) ? c : 0;

            // Go deep when the surface ore it knows about is thin, not just when the trucks have run dry.
            var trucks0 = mine.Where(e => e.IsHarvester).ToList();
            int surfaceLeft = w.Tick % (World.TickRate * 10) == 0 || knownSurface < 0 ? (knownSurface = StateView.OreFields(w, t.Explored).Sum(f => f.total)) : knownSurface;
            bool surfaceDry = surfaceLeft < 4000 ||
                              (trucks0.Count > 0 && trucks0.Count(tr => tr.Order == Order.Idle) * 2 >= trucks0.Count) ||
                              (t.SurfaceWarnedAt > 0 && w.Time - t.SurfaceWarnedAt < 300);
            // Past the electronics plant it prospects anyway: deep mines are the economy's second stage.
            bool prospect = w.HasComplete(team, "electronics_plant") && t.Amount("steel") > 400;
            // Ores worth mining now: iron always; copper, crystal and uranium while the stock is low (the last two only with
            // something to refine them). Each switches on below a low mark and off above a higher one, so trucks don't flap.
            int copperOnHand = t.Amount("copper") + t.Amount("copper_ore"), ironOnHand = t.Amount("steel") + t.Amount("iron_ore");
            // Trucks queueing for the bays (each bay takes one truck at a time), or iron ore piling up unrefined: another refinery.
            bool moreRefineries = (!surfaceDry && trucks0.Count(e => e.Dock == DockStep.Queue) >= 2 || t.Amount("iron_ore") > 1500) && Count("mining_refinery") is int nr && nr >= 1 && nr < 4;
            // Raw ore that a purchase it is waiting on (the first step of the build order, a refinery it wants more of) costs and it
            // lacks: worth mining, by truck or deep mine, whatever refined copper or steel it has in hand.
            var waiting = FirstUnbuilt(Count);
            bool RawShort(string ore) => (waiting != null && Defs.Get(waiting).Cost.TryGetValue(ore, out var needed) && t.Amount(ore) < needed) ||
                                         (moreRefineries && Defs.Get("mining_refinery").Cost.TryGetValue(ore, out var needed2) && t.Amount(ore) < needed2);
            wantCopper = copperOnHand < (wantCopper ? 1800 : 800);
            wantCrystal = (Count("optics_lab") > 0 || Count("composite_foundry") > 0) && t.Amount("crystal") < (wantCrystal ? 800 : 300);
            wantUranium = Count("enrichment_plant") > 0 && t.Amount("uranium") < (wantUranium ? 600 : 250);
            bool OreWanted(int type) => type == Map.Iron || (type == Map.Copper ? wantCopper || RawShort("copper_ore") : type == Map.Crystal ? wantCrystal : wantUranium);

            // Deep mines still pumping (a dry one draws 50 power for nothing) and the flagged deposits worth a drill rig next:
            // iron and copper it's short of first, then the rest of those, then crystal/uranium it can use (a deep mine pumps
            // 4 a second, far more than anything uses), nearest first.
            var deepMines = mine.Where(e => e.Def.Key == "deep_mine").ToList();
            bool Pumping(Entity m) => w.Map.DepositById(m.DepositId) is DeepDeposit dd && dd.Amount > 0;
            int pumping = deepMines.Count(Pumping);
            bool Short(int type) => type == Map.Iron ? ironOnHand < 400 || RawShort("iron_ore") : type == Map.Copper && (copperOnHand < 300 || copperOnHand * 4 < ironOnHand || RawShort("copper_ore"));
            var rigs = mine.Where(e => e.Def.Key == "drill_rig").ToList();
            // (One mine on crystal or on uranium at a time is plenty: pumping, or a rig actually on its way, as World.RigsBound counts
            // them. A rig that was stopped keeps its old zone but isn't drilling anything.)
            bool Bound(Entity r) => r.ZoneId != 0 && (r.Order == Order.Drill || (r.Order == Order.Refuel && r.ResumeOrder == Order.Drill));
            var drilling = deepMines.Where(Pumping).Select(m => w.Map.DepositById(m.DepositId).Type).Concat(rigs.Where(Bound).Select(r => w.Map.DepositById(r.ZoneId)?.Type ?? -1)).ToList();
            var zones = w.Map.Deep.Where(d => t.Surveyed.Contains(d.Id) && OreWanted(d.Type) && (d.Type < Map.Crystal || !drilling.Contains(d.Type)) && w.DrillBlocker(d, team) == null)
                                  .OrderBy(d => Short(d.Type) ? 0 : d.Type >= Map.Crystal ? 2 : 1).ThenBy(d => Vec2.Dist(d.Pos, t.StartPos)).ToList();
            int rigsInFlight = rigs.Count + t.UnitQueues[Producer.Factory].Count(q => q.Key == "drill_rig");
            wentDeep |= deepMines.Count > 0 || rigsInFlight > 0;
            bool deepOn = w.HasComplete(team, "factory") && (surfaceDry || prospect || wentDeep);
            var surveyors = mine.Where(e => e.Def.Key == "geological_surveyor").ToList();
            bool Queued(string k) => t.UnitQueues[Producer.Factory].Any(q => q.Key == k);
            bool wantRig = deepOn && w.MissingPrereq(team, Defs.Get("drill_rig")) == null && rigsInFlight < System.Math.Min(2, zones.Count) && pumping + rigsInFlight < MaxDeepMines;
            bool wantSurveyor = deepOn && surveyors.Count == 0 && zones.Count < 2 && !Queued("geological_surveyor");
            // What it saves up for, so the army and the build order don't spend it as it arrives and the converters leave it alone:
            // the first two mines' rigs, or the surveyor that finds them.
            string saving = wantRig && pumping < 2 ? "drill_rig" : wantSurveyor && pumping == 0 ? "geological_surveyor" : null;
            var savingCost = saving != null ? Defs.Get(saving).Cost : null;
            if (saving != t.SaveFor) { if (saving != null) Do(w, "type", "save_for", "unit", saving); else Do(w, "type", "save_for"); }

            // With the surface dry and no deep mine pumping, the economy comes first: no army or new buildings (except power)
            // until a rig is on its way.
            // Regrowing fields keep a trickle of surface ore about, so "dry" alone no longer says when to go deep: a free
            // mining zone already flagged and no deep mine pumping means saving for a rig too. (Once it has been deep, with
            // no zone left to drill, it carries on: surveyors are still looking.)
            bool economyFirst = (surfaceDry || zones.Count > 0) && pumping == 0 && rigsInFlight == 0 && w.HasComplete(team, "electronics_plant") && (!wentDeep || zones.Count > 0);

            // Down to its last couple of mining trucks, a truck comes before anything else (they are where the money comes from).
            var trucks = mine.Where(e => e.IsHarvester).ToList();
            int wantTrucks = System.Math.Min(10, 3 + Count("mining_refinery") * 2 + Count("outpost") * 2);
            bool truckWanted = trucks.Count < wantTrucks && t.UnitQueues[Producer.CommandCenter].Count == 0;
            if (truckWanted && trucks.Count < 3 && t.Amount("iron_ore") >= 200) { Do(w, "type", "train", "unit", "mining_truck"); truckWanted = false; }
            // (More trucks are no use while some stand idle with nothing to mine.)
            bool needTrucks = trucks.Count < wantTrucks && trucks.Count(e => e.Order == Order.Idle) < 2;
            truckWanted &= needTrucks;

            // ---- Base building
            // Planned every second, even while something is going up, so the ore it costs can be saved up (see the reserves below).
            // Power comes first, counting what is going up and what the next step, or a rig on its way, will draw.
            var pending = structures.Where(s => !s.IsComplete).Sum(s => s.Def.Power);
            string head = null, step = null;
            if (!economyFirst) step = PickStep(w, t, Count, out head);
            else stuckSince.Clear();
            int draw = (step != null ? System.Math.Max(0, -Defs.Get(step).Power) : 0) + 50 * rigsInFlight;
            string next = null;
            if (t.PowerProduced - t.PowerUsed + pending - draw < 25 && structures.Count > 1)
                // (A fusion reactor only if it can be paid for now: short of circuits, plain power plants keep the lights on.)
                next = w.MissingPrereq(team, Defs.Get("fusion_reactor")) == null && t.Amount("plasma") > 80 && Count("fusion_reactor") == 0 && t.Missing(Defs.Get("fusion_reactor").Cost) == null ? "fusion_reactor" : "power_plant";
            else if (moreRefineries && t.Missing(Defs.Get("mining_refinery").Cost) == null)
                next = "mining_refinery";
            else next = step;
            if (next != null && t.StructureQueue.Count == 0 && t.Missing(Defs.Get(next).Cost) == null && w.MissingPrereq(team, Defs.Get(next)) == null &&
                (saving == null || next == "power_plant" || next == "fusion_reactor" || Defs.Get(next).Cost.All(kv => t.Amount(kv.Key) - (savingCost.TryGetValue(kv.Key, out var rc) ? rc : 0) >= kv.Value)))
                Do(w, "type", "build", "structure", next);
            // What its own units may not spend: the next structure, and what it is saving up for.
            var reserve = new Dictionary<string, int>();
            foreach (var kv in next != null ? Defs.Get(next).Cost : new Dictionary<string, int>()) reserve[kv.Key] = kv.Value;
            if (saving != null) foreach (var kv in savingCost) reserve[kv.Key] = (reserve.TryGetValue(kv.Key, out var also) ? also : 0) + kv.Value;
            bool Affordable(string unit)
            {
                var cost = Defs.Get(unit).Cost;
                return cost.All(kv => t.Amount(kv.Key) - (reserve.TryGetValue(kv.Key, out var r) ? r : 0) >= kv.Value);
            }

            // ---- Mining trucks: enough of them, spread across the ore types the tech needs
            if (truckWanted && t.Amount("iron_ore") >= 200 + (reserve.TryGetValue("iron_ore", out var ri) ? ri : 0))
                Do(w, "type", "train", "unit", "mining_truck");
            // The refineries eat raw ore as it arrives, so an ore cost can't be saved up just by waiting: they leave alone
            // what the next purchase costs in raw ore (the next structure, the first unbuilt step if it is waiting on ore while
            // others go ahead, a refinery it wants more of, and a truck while it is short of them). Not more: a truck lost far
            // afield shouldn't swallow the whole income.
            foreach (var ore in Defs.Ores)
            {
                int hold = 0;
                if (next != null && Defs.Get(next).Cost.TryGetValue(ore, out var c1)) hold = c1;
                if (head != null && Defs.Get(head).Cost.TryGetValue(ore, out var c2)) hold = System.Math.Max(hold, c2);
                if (moreRefineries && Defs.Get("mining_refinery").Cost.TryGetValue(ore, out var c3)) hold = System.Math.Max(hold, c3);
                if (ore == "iron_ore" && (trucks.Count < 3 || (needTrucks && w.HasComplete(team, "factory")))) hold = System.Math.Max(hold, Defs.Get("mining_truck").Cost[ore]);
                if ((t.Reserve.TryGetValue(ore, out var held) ? held : 0) != hold) Do(w, "type", "reserve", "item", ore, "amount", hold);
            }
            // Iron is the bulk resource: roughly 3 of every 5 trucks; one each on copper, then crystal/uranium as tech needs them
            // (and only while it is short of them: ore nothing is using just piles up).
            int copperType = wantCopper || RawShort("copper_ore") ? Map.Copper : Map.Iron; // (a refinery waiting on copper ore: trucks go for it even with copper in hand)
            var plan = new List<int> { Map.Iron, Map.Iron, copperType, Map.Iron };
            if (wantCrystal) plan.Add(Map.Crystal);
            if (wantUranium) plan.Add(Map.Uranium);
            plan.Add(Map.Iron);
            plan.Add(copperType);
            for (int i = 0; i < trucks.Count; i++)
            {
                int type = i < plan.Count ? plan[i] : Map.Iron;
                var tr = trucks[i];
                // Trucks that ran out of ore stand idle: every 20 s they look again (fields regrow, salvage lands).
                bool retry = tr.Order == Order.Idle && (int)w.Time % 20 == 0;
                if ((tr.HarvestType != type || retry) && tr.Cargo == 0 && tr.Order != Order.ReturnOre)
                    Do(w, "type", "harvest", "units", new[] { tr.Id }, "ore", Defs.Ores[type]);
            }

            // ---- Expansion: claim remote ore fields with outposts
            if (w.HasComplete(team, "electronics_plant") && Count("outpost") + mine.Count(e => e.Def.Key == "outpost_truck") < 2 &&
                !t.UnitQueues[Producer.Factory].Any(p => p.Key == "outpost_truck") && Affordable("outpost_truck"))
                Do(w, "type", "train", "unit", "outpost_truck");
            foreach (var ot in mine.Where(e => e.Def.Key == "outpost_truck"))
            {
                if (!outpostTargets.TryGetValue(ot.Id, out var goal))
                {
                    var field = StateView.OreFields(w, t.Explored)
                        .Where(f => !structures.Any(s => Vec2.Dist(s.Center, new Vec2(f.cx, f.cy)) < 12))
                        .OrderBy(f => Vec2.Dist(new Vec2(f.cx, f.cy), t.StartPos)).FirstOrDefault();
                    if (field.tiles == 0) continue;
                    // Park beside the field, not on the ore.
                    var spot = w.FindPlacement(team, "outpost", new Vec2(field.cx, field.cy)) ?? new Int2(field.cx + 3, field.cy);
                    goal = new Vec2(spot.X + 1, spot.Y + 1);
                    outpostTargets[ot.Id] = goal;
                    Do(w, "type", "move", "units", new[] { ot.Id }, "x", goal.X, "y", goal.Y);
                }
                else if (ot.Order == Order.Idle)
                {
                    var r = Do(w, "type", "deploy", "units", new[] { ot.Id });
                    if (!(r["ok"] is bool ok && ok))
                    {
                        // Shuffle and retry next think.
                        var jitter = new Vec2(goal.X + (w.Tick % 5) - 2, goal.Y + (w.Tick % 3) - 1);
                        outpostTargets[ot.Id] = jitter;
                        Do(w, "type", "move", "units", new[] { ot.Id }, "x", jitter.X, "y", jitter.Y);
                    }
                }
            }

            // ---- Construction truck: rebuild a lost HQ; late in a rich game keep one in reserve as insurance.
            var builders = mine.Where(e => e.Def.Key == "construction_truck").ToList();
            bool haveHq = Count("command_center") > 0;
            bool queuedBuilder = t.UnitQueues[Producer.Factory].Any(q => q.Key == "construction_truck");
            if (builders.Count == 0 && !queuedBuilder && w.HasComplete(team, "factory") && w.HasComplete(team, "electronics_plant"))
            {
                var cost = Defs.Get("construction_truck").Cost;
                // Insurance only when it's cheap next to the stockpile: under a third of the steel and circuits on hand.
                bool reserveWorthIt = haveHq && w.Time > 20 * 60 && cost.All(kv => t.Amount(kv.Key) >= kv.Value * 3);
                if ((!haveHq || reserveWorthIt) && t.Missing(cost) == null)
                    Do(w, "type", "train", "unit", "construction_truck");
            }
            foreach (var ct in builders)
            {
                if (haveHq) { outpostTargets.Remove(ct.Id); continue; } // in reserve: it waits at the factory
                if (!outpostTargets.TryGetValue(ct.Id, out var site))
                {
                    // Back where the base was if there's room, else beside whatever still stands, else where it is.
                    var anchors = new List<Vec2> { t.StartPos };
                    anchors.AddRange(structures.Select(s => s.Center));
                    anchors.Add(ct.Pos);
                    Int2? spot = null;
                    foreach (var a in anchors) if ((spot = w.FindPlacement(team, "command_center", a, requireNear: false)).HasValue) break;
                    site = spot.HasValue ? new Vec2(spot.Value.X + 1.5f, spot.Value.Y + 1.5f) : ct.Pos;
                    outpostTargets[ct.Id] = site;
                    Do(w, "type", "move", "units", new[] { ct.Id }, "x", site.X, "y", site.Y);
                }
                else if (ct.Order == Order.Idle)
                {
                    var r = Do(w, "type", "deploy", "units", new[] { ct.Id });
                    if (!(r["ok"] is bool ok && ok)) outpostTargets.Remove(ct.Id); // somewhere else next time
                }
            }

            // ---- Derricks: engineers go for the neutral ones (a raiding AI also takes enemy ones it sees below 50%).
            if (w.HasComplete(team, "barracks"))
            {
                var engineers = mine.Where(e => e.Def.Engineer).ToList();
                var targets = w.Derricks.Where(d => d.Team != team && (d.Team < 0 || (!Passive && World.Capturable(d) && w.IsVisibleTo(team, d))))
                                        .OrderBy(d => Vec2.Dist(d.Center, t.StartPos)).ToList();
                foreach (var d in targets)
                {
                    if (engineers.Any(en => en.Order == Order.Capture && en.TargetId == d.Id)) continue;
                    var free = engineers.FirstOrDefault(en => en.Order == Order.Idle);
                    if (free != null) { Do(w, "type", "capture", "units", new[] { free.Id }, "target", d.Id); engineers.Remove(free); continue; }
                    if (engineers.Count < 2 && !t.UnitQueues[Producer.Barracks].Any(q => q.Key == "engineer") && Affordable("engineer"))
                        Do(w, "type", "train", "unit", "engineer");
                    break;
                }
            }

            // ---- Keep a couple of repair trucks (they auto-repair whatever is damaged near them)
            if (w.HasComplete(team, "factory") && mine.Count(e => e.Def.Key == "repair_truck") < 2 &&
                !t.UnitQueues[Producer.Factory].Any(p => p.Key == "repair_truck") && t.Amount("steel") > 300 && Affordable("repair_truck"))
                Do(w, "type", "train", "unit", "repair_truck");
            if (w.HasComplete(team, "barracks") && mine.Count(e => e.Def.Key == "medic") < 2 &&
                !t.UnitQueues[Producer.Barracks].Any(p => p.Key == "medic") && t.Amount("steel") > 200 && Affordable("medic"))
                Do(w, "type", "train", "unit", "medic");

            // ---- Deep mining: when the surface runs dry, survey outward from the base and drill what turns up.
            // A mine whose deposit is used up pumps nothing but still draws power: sell it.
            foreach (var dm in deepMines.Where(e => e.IsComplete && !Pumping(e)))
                Do(w, "type", "sell", "structure_id", dm.Id);
            if (deepOn)
            {
                // (What it is saving for has its cost in the reserve, so that is not held against it.)
                if (wantSurveyor && (saving == "geological_surveyor" ? t.Missing(savingCost) == null : Affordable("geological_surveyor")))
                    Do(w, "type", "train", "unit", "geological_surveyor");
                // Surveyors prospect around the base on their own, flagging mining zones as they go; when the area is
                // covered they widen it.
                if (w.Time >= nextProspect)
                    foreach (var sv in surveyors.Where(e => e.Order == Order.Idle))
                    {
                        var r = Do(w, "type", "prospect", "units", new[] { sv.Id }, "x", t.StartPos.X, "y", t.StartPos.Y, "radius", prospectRadius);
                        if (r["ok"] is bool ok && ok) continue;
                        if (prospectRadius < 120) prospectRadius += 30;
                        else nextProspect = w.Time + 30; // everything in reach is surveyed: look again later
                    }
                if (wantRig && (saving == "drill_rig" ? t.Missing(savingCost) == null : Affordable("drill_rig")))
                    Do(w, "type", "train", "unit", "drill_rig");
                // Idle rigs go to the best free zone nobody is heading for and deploy there by themselves.
                var open = zones.Where(d => w.RigsBound(team, d.Id).Count == 0).ToList();
                foreach (var rig in rigs.Where(e => e.Order == Order.Idle))
                {
                    if (open.Count == 0) break;
                    var zone = open[0];
                    open.RemoveAt(0);
                    var sent = Do(w, "type", "drill", "units", new[] { rig.Id }, "zone", zone.Id);
                    // (A rig sent to crystal or uranium takes that ore's one mine: no other rig goes for it this tick.)
                    if (sent["ok"] is bool ok && ok && zone.Type >= Map.Crystal) open.RemoveAll(d => d.Type == zone.Type);
                }
            }

            // ---- Army
            void Train(Producer p, params string[] options)
            {
                if (economyFirst) return;
                if (!w.HasComplete(team, Defs.ProducerKey(p)) || t.UnitQueues[p].Count >= 2) return;
                foreach (var u in options)
                    if (w.MissingPrereq(team, Defs.Get(u)) == null && Affordable(u)) { Do(w, "type", "train", "unit", u); return; }
            }
            // Expensive tiers first so cheap infantry doesn't eat all the steel; infantry is capped.
            int roll = w.Tick / 20 % 3;
            Train(Producer.Airfield, roll == 0 ? new[] { "stealth_bomber", "gunship" } : new[] { "gunship" });
            // Answer enemy aircraft with flak; otherwise climb toward mammoths.
            bool enemyAir = w.Entities.Any(e => !e.Dead && e.Team != team && e.IsAir && w.IsVisibleTo(team, e));
            if (enemyAir && mine.Count(e => e.Def.Key == "flak_track") < 3) Train(Producer.Factory, "flak_track");
            if (w.HasComplete(team, "electronics_plant") && mine.Count(e => e.Def.Key == "recon_drone") == 0) Train(Producer.Factory, "recon_drone");
            Train(Producer.Factory, roll == 0 ? new[] { "mammoth_tank", "laser_tank", "heavy_tank", "light_tank" } : roll == 1 ? new[] { "artillery", "heavy_tank", "light_tank" } : new[] { "light_tank", "apc", "scout_buggy" });
            int infantry = mine.Count(e => e.Def.Armor == Armor.Infantry);
            int vehicles = mine.Count(e => !e.IsStructure && e.Def.Armor != Armor.Infantry && !e.IsHarvester);
            if (infantry < 6 + vehicles && t.Amount("steel") > 150)
                Train(Producer.Barracks, roll == 0 ? new[] { "laser_trooper", "sniper", "rocket_soldier", "rifleman" } : roll == 1 ? new[] { "rocket_soldier", "rifleman" } : new[] { "rifleman" });

            // ---- Defense: anything hostile near a base gets everyone nearby.
            var army = mine.Where(e => !e.IsStructure && e.IsArmed).ToList();
            // (Its base, not a derrick out in the contested middle: armies passing a derrick would otherwise keep the whole
            // army home on defence and the AI would never attack.)
            var threat = w.Entities.FirstOrDefault(e => !e.Dead && e.Team != team && e.Team >= 0 && !e.IsStructure && w.IsVisibleTo(team, e) &&
                                                        structures.Any(s => s.Def.Key != "derrick" && Vec2.Dist(e.Pos, s.Center) < 10));
            if (threat != null)
            {
                foreach (var u in army.Where(u => (u.Order == Order.Idle || u.Order == Order.Move) && u.Def.Weapon.CanHit(threat.Def)))
                    w.SetOrder(u, Order.AttackMove, threat.Pos);
                return;
            }

            // ---- Offense: waves at known structures, else toward the nearest unexplored corner.
            if (Passive) return; // the house AI holds its ground
            var idle = army.Where(u => u.Order == Order.Idle).ToList();
            if (idle.Count >= waveSize)
            {
                Vec2 goal;
                // (Not derricks: attack-move leaves a held one alone, so a wave aimed at the nearest derrick stopped beside it and
                // the next wave did the same, for the rest of the game, while the enemy base behind it stood.)
                var known = t.KnownEnemyStructures.Values.Where(k => k.team >= 0 && k.key != "derrick").ToList();
                if (known.Count > 0) { var k = known.OrderBy(x => Vec2.Dist(new Vec2(x.origin.X, x.origin.Y), t.StartPos)).First(); goal = new Vec2(k.origin.X + 1, k.origin.Y + 1); }
                else
                {
                    var corners = w.Map.Spawns.Where(s => !t.Explored[w.Map.Idx((int)s.X, (int)s.Y)]).ToList();
                    if (corners.Count == 0) corners = w.Map.Spawns.Where(s => Vec2.Dist(s, t.StartPos) > 5).ToList();
                    goal = corners.OrderBy(s => Vec2.Dist(s, t.StartPos)).First();
                }
                foreach (var u in idle) w.SetOrder(u, Order.AttackMove, goal);
                waveSize = System.Math.Min(10, waveSize + 1);
                Do(w, "type", "say", "text", $"Wave of {idle.Count} inbound.");
            }
        }
    }
}
