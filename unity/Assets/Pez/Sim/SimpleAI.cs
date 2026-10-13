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
        const int MaxDeepMines = 4; // iron and copper mines at once (each draws 50 power); crystal and uranium have a slot of their own each
        const int ThinOre = 3000; // ore of the kinds it wants within reach of its drop-offs: below this the home fields are thin, and it looks farther afield
        const float EnemyBerth = 30f; // an outpost site keeps this far from every enemy structure it knows of
        const float HouseDefenceReach = 30f; // the house AI answers a threat only with units this close to it: it holds its ground, it does not march across the map
        const float MaxExpansion = 170f; // and is no more than this far from its nearest drop-off
        const int MaxOutposts = 4; // outposts at once while the ore within reach is thin (two otherwise)
        const float SiteClear = 18f; // in an open arena the outpost itself stands at least this far from any base site but its own: the 18 tiles the derricks keep
        const float SiteBerth = 24f; // in an open arena an outpost keeps this far from any base site but its own: the 18 tiles the derricks keep, and 6 for where it lands beside a field
        const int OreBacklog = 1000; // iron ore lying unrefined past this wants another refinery
        const float IncomeWindow = 60f; // seconds the ore coming in is averaged over
        const int CopperLow = 300, CopperHigh = 600; // refined copper on hand: the trucks go for copper below the low mark and leave it above the high one
        const int CopperSurplus = 1500; // refined copper past this is more than anything uses: its deep mines are sold
        /// <summary>Iron ore a second one refinery converts (its recipe's cycles a second times the iron ore a cycle takes): the ore coming in is measured against it.</summary>
        static readonly float RefineryRate = Defs.Get("mining_refinery").Recipes.Where(r => r.Inputs.ContainsKey("iron_ore")).Sum(r => r.Rate * r.Inputs["iron_ore"]);
        /// <summary>Iron ore a second one command center makes on its own (its recipe with no inputs): income that Stats.OreMined, the trucks' and mines' count, leaves out.</summary>
        static readonly float TrickleRate = Defs.Get("command_center").Recipes.Where(r => r.Inputs.Count == 0 && r.Outputs.ContainsKey("iron_ore")).Sum(r => r.Rate * r.Outputs["iron_ore"]);
        float oreRate; // ore a second the trucks and deep mines bring in, averaged over IncomeWindow
        float ratedAt = -1f; // when it was last worked out, and the team's Stats.OreMined then
        int ratedMined;

        static readonly string[] BuildOrder =
        {
            "power_plant", "mining_refinery", "barracks", "factory", "power_plant", "electronics_plant", "gun_turret",
            "mining_refinery", "power_plant", "optics_lab", "enrichment_plant", "power_plant", "radar_dome", "sam_site",
            "laser_tower", "power_plant", "composite_foundry", "airfield", "fusion_reactor", "gun_turret", "laser_tower", "sam_site",
        };

        /// <summary>
        /// The structures the economy stands on: what a drill rig needs standing (factory, electronics plant) and what those need
        /// in turn (refinery, power plant), found from the defs. "Economy first" holds back everything else (army, defences, tech),
        /// never these.
        /// </summary>
        static readonly HashSet<string> EconomyStructures = FindEconomyStructures();

        static HashSet<string> FindEconomyStructures()
        {
            var keys = new HashSet<string>();
            void Need(EntityDef d)
            {
                var wanted = new List<string>(d.Requires);
                var maker = Defs.ProducerKey(d.BuiltBy);
                if (maker != null) wanted.Add(maker);
                foreach (var k in wanted)
                {
                    var need = Defs.Get(k);
                    if (need == null || !need.IsStructure || !need.Buildable || !keys.Add(k)) continue; // (the command center can't be built)
                    Need(need);
                }
            }
            Need(Defs.Get("drill_rig"));
            return keys;
        }

        /// <summary>The first economy structure of the build order that is missing (null when they all stand).</summary>
        static string FirstMissingEconomy(System.Func<string, int> count)
        {
            foreach (var k in BuildOrder)
                if (EconomyStructures.Contains(k) && count(k) == 0) return k;
            return null;
        }

        /// <summary>
        /// The surface fields the team has seen any of, one per field (found from its root tile, as regrowth knows them): where, which ore
        /// (an index into Defs.Ores), and the field's own ore left in the tiles it has explored (salvage on it doesn't count). A field seen
        /// only at its edges is known, with the ore showing there, at its root if that has been seen and at the middle of what has if not.
        /// </summary>
        static List<(Vec2 at, int type, int ore)> KnownFields(World w, Team t)
        {
            var m = w.Map;
            m.FieldIndex(out var tiles, out var roots);
            var list = new List<(Vec2, int, int)>();
            foreach (int i in tiles)
            {
                if (!roots[i]) continue;
                int x = i % m.W, y = i / m.W, type = m.OreBaseType[i], ore = 0, seen = 0;
                float sx = 0, sy = 0;
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        if (!m.InBounds(x + dx, y + dy)) continue;
                        int j = m.Idx(x + dx, y + dy);
                        if (m.OreBase[j] <= 0 || m.OreBaseType[j] != type || !t.Explored[j]) continue;
                        seen++; sx += x + dx + 0.5f; sy += y + dy + 0.5f;
                        if (m.OreType[j] == type) ore += m.FieldOre(j);
                    }
                if (seen == 0) continue;
                list.Add((t.Explored[i] ? new Vec2(x + 0.5f, y + 0.5f) : new Vec2(sx / seen, sy / seen), type, ore));
            }
            return list;
        }

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
            // Choosing the nearest of several things: two equally near (a derrick, field or corner mirrored across the line from this base to
            // the middle of the map) go to the one on the right as the base faces the middle, so the seats' choices are mirror images and not
            // both the first in some list (the same derrick, the same corner).
            var right = w.RightOf(t.StartPos);
            float Near(Vec2 p) => System.MathF.Round(Vec2.Dist(p, t.StartPos), 2);
            float Rightward(Vec2 p) => (p.X - t.StartPos.X) * right.X + (p.Y - t.StartPos.Y) * right.Y;
            if (t.Defeated) return;
            if (ratedAt >= 0f && w.Time > ratedAt) oreRate += ((t.Stats.OreMined - ratedMined) / (w.Time - ratedAt) - oreRate) * System.MathF.Min(1f, (w.Time - ratedAt) / IncomeWindow);
            ratedAt = w.Time; ratedMined = t.Stats.OreMined;
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
            // something to refine them). Each switches on below a low mark and off above a higher one, so trucks don't flap. (Copper's
            // marks are low: little but circuits use it, and steel is what limits them.)
            int copperOnHand = t.Amount("copper") + t.Amount("copper_ore"), ironOnHand = t.Amount("steel") + t.Amount("iron_ore");
            // (Copper it can use: copper, refined or ore, held back for a purchase isn't, since the converters can't touch it; if the purchase
            // can't be made, a refinery with no room or an outpost truck short of circuits, it is held for good.)
            int copperSpendable = System.Math.Max(0, t.Amount("copper") - t.Reserved("copper")) + System.Math.Max(0, t.Amount("copper_ore") - t.Reserved("copper_ore"));
            // Trucks queueing for the bays (each bay takes one truck at a time), or iron ore piling up unrefined: another refinery. Not more
            // than the ore coming in could keep busy (all of it may be iron, and a refinery converts RefineryRate of it a second; the command
            // centers' own trickle counts, at half in low power as in World.UpdateEconomy), and only where there is room for one.
            float trickle = TrickleRate * structures.Count(s => s.IsComplete && s.Def.Key == "command_center") * (t.LowPower ? 0.5f : 1f);
            bool moreRefineries = (!surfaceDry && trucks0.Count(e => e.Dock == DockStep.Queue) >= 2 || t.Amount("iron_ore") > OreBacklog) && Count("mining_refinery") is int nr && nr >= 1 &&
                                  nr < System.MathF.Ceiling((oreRate + trickle) / RefineryRate) && w.FindPlacement(team, "mining_refinery").HasValue;
            // Raw ore that a purchase it is waiting on (the first step of the build order, a refinery it wants more of) costs and it
            // lacks: worth mining, by truck or deep mine, whatever refined copper or steel it has in hand.
            var waiting = FirstUnbuilt(Count);
            bool RawShort(string ore) => (waiting != null && Defs.Get(waiting).Cost.TryGetValue(ore, out var needed) && t.Amount(ore) < needed) ||
                                         (moreRefineries && Defs.Get("mining_refinery").Cost.TryGetValue(ore, out var needed2) && t.Amount(ore) < needed2);
            wantCopper = copperOnHand < (wantCopper ? CopperHigh : CopperLow);
            wantCrystal = (Count("optics_lab") > 0 || Count("composite_foundry") > 0) && t.Amount("crystal") < (wantCrystal ? 800 : 300);
            wantUranium = Count("enrichment_plant") > 0 && t.Amount("uranium") < (wantUranium ? 600 : 250);
            bool OreWanted(int type) => type == Map.Iron || (type == Map.Copper ? wantCopper || RawShort("copper_ore") : type == Map.Crystal ? wantCrystal : wantUranium);

            // ---- The mine plan, worked out once; buying rigs and sending them both read it.
            // Deep mines still pumping (a dry one draws 50 power for nothing), rigs actually on their way (World.RigsBound counts them
            // so: a rig that was stopped keeps its old zone but isn't drilling anything), and what each ore has: mines + rigs heading out.
            var deepMines = mine.Where(e => e.Def.Key == "deep_mine").ToList();
            bool Pumping(Entity m) => w.Map.DepositById(m.DepositId) is DeepDeposit dd && dd.Amount > 0;
            int pumping = deepMines.Count(Pumping);
            var rigs = mine.Where(e => e.Def.Key == "drill_rig").ToList();
            bool Bound(Entity r) => r.ZoneId != 0 && (r.Order == Order.Drill || (r.Order == Order.Refuel && r.ResumeOrder == Order.Drill));
            int queuedRigs = t.UnitQueues[Producer.Factory].Count(q => q.Key == "drill_rig");
            int rigsInFlight = rigs.Count + queuedRigs;
            var has = new int[4];
            foreach (var m in deepMines.Where(Pumping)) has[w.Map.DepositById(m.DepositId).Type]++;
            foreach (var r in rigs.Where(Bound)) { var z = w.Map.DepositById(r.ZoneId); if (z != null) has[z.Type]++; }
            int OnHand(int ore) => ore == Map.Iron ? ironOnHand : ore == Map.Copper ? copperSpendable : t.Amount(Defs.Ores[ore]);
            bool Short(int type) => type == Map.Iron ? ironOnHand < 400 || RawShort("iron_ore") : type == Map.Copper && (copperOnHand < 300 || copperOnHand * 4 < ironOnHand || RawShort("copper_ore"));
            // A basic ore it wants, has no mine on and next to none of (or is short of as the raw ore something is waiting on) gets one
            // whatever the cap says; crystal / uranium it has a plant for and none of go ahead of the ordinary iron and copper (a deep mine
            // pumps 4 a second, far more than anything uses).
            bool Starved(int ore) => ore < Map.Crystal && has[ore] == 0 && OreWanted(ore) && (OnHand(ore) < 100 || RawShort(Defs.Ores[ore]));
            bool Needy(int type) => type >= Map.Crystal && OreWanted(type) && t.Amount(Defs.Ores[type]) < 100;
            // The flagged zones worth a rig, best first (iron and copper it's short of, then the rest, nearest first), and the ones no
            // rig of its own is heading for. (Copper zones only while starved of copper: a deep mine pumps 4 ore a second until its deposit
            // is dry whatever the stockpile holds, where a truck is turned back to iron once there is enough.)
            var freeZones = w.Map.Deep.Where(d => t.Surveyed.Contains(d.Id) && OreWanted(d.Type) && (d.Type != Map.Copper || Starved(Map.Copper)) && w.DrillBlocker(d, team) == null)
                                      .OrderBy(d => Short(d.Type) || Starved(d.Type) ? 0 : Needy(d.Type) ? 1 : d.Type >= Map.Crystal ? 3 : 2).ThenBy(d => Near(d.Pos)).ThenByDescending(d => Rightward(d.Pos)).ToList();
            var open = freeZones.Where(d => w.RigsBound(team, d.Id).Count == 0).ToList();
            // Slots: how many more mines each ore may start. Iron and copper share the cap, except that a starved one always gets its
            // first (and the other keeps its place); crystal and uranium have one each, while wanted.
            int sharedFree = MaxDeepMines - has[Map.Iron] - has[Map.Copper];
            int basicRoom = System.Math.Max(sharedFree, (Starved(Map.Iron) ? 1 : 0) + (Starved(Map.Copper) ? 1 : 0));
            var slots = new int[4];
            for (int o = Map.Iron; o <= Map.Copper; o++)
                slots[o] = System.Math.Max(Starved(o) ? 1 : 0, basicRoom - (Starved(1 - o) ? 1 : 0));
            slots[Map.Copper] = System.Math.Min(slots[Map.Copper], 1); // (copper zones are open only while starved, which allows one mine: a second rig bought for another would find no zone)
            slots[Map.Crystal] = OreWanted(Map.Crystal) ? System.Math.Max(0, 1 - has[Map.Crystal]) : 0;
            slots[Map.Uranium] = OreWanted(Map.Uranium) ? System.Math.Max(0, 1 - has[Map.Uranium]) : 0;
            int Open(int ore) => open.Count(d => d.Type == ore);
            // Rigs worth having in all: as many as there are slots with an open zone to fill them. Buying stops there, so none idles.
            int usable = System.Math.Min(basicRoom, System.Math.Min(slots[Map.Iron], Open(Map.Iron)) + System.Math.Min(slots[Map.Copper], Open(Map.Copper)))
                       + System.Math.Min(slots[Map.Crystal], Open(Map.Crystal)) + System.Math.Min(slots[Map.Uranium], Open(Map.Uranium));
            int unsent = rigs.Count(r => !Bound(r)) + queuedRigs;
            // A starved basic ore with the cap full of the other: retire that one's mine with the most left, to make the room. Only when
            // something is waiting on the missing ore, and only once the rig that replaces it is bought (built or queued, so paid for):
            // until then the rig is saved for, and the mine keeps pumping.
            bool Waiting(int ore)
            {
                string raw = Defs.Ores[ore], refined = ore == Map.Iron ? "steel" : "copper";
                return RawShort(raw) || (waiting != null && Defs.Get(waiting).Cost.Any(kv => (kv.Key == raw || kv.Key == refined) && t.Amount(kv.Key) < kv.Value));
            }
            Entity retire = null;
            bool retireWanted = false;
            for (int o = Map.Iron; o <= Map.Copper && !retireWanted; o++)
                if (sharedFree <= 0 && Starved(o) && Open(o) > 0 && has[1 - o] >= 2 && Waiting(o) && w.MissingPrereq(team, Defs.Get("drill_rig")) == null)
                {
                    retireWanted = true;
                    if (unsent > 0)
                        retire = deepMines.Where(m => m.IsComplete && Pumping(m) && w.Map.DepositById(m.DepositId).Type == 1 - o)
                                          .OrderByDescending(m => w.Map.DepositById(m.DepositId).Amount).FirstOrDefault();
                }
            wentDeep |= deepMines.Count > 0 || rigsInFlight > 0;
            bool wantDeep = surfaceDry || prospect || wentDeep;
            bool deepOn = w.HasComplete(team, "factory") && wantDeep;
            var surveyors = mine.Where(e => e.Def.Key == "geological_surveyor").ToList();
            bool Queued(string k) => t.UnitQueues[Producer.Factory].Any(q => q.Key == k);
            bool wantRig = deepOn && w.MissingPrereq(team, Defs.Get("drill_rig")) == null && unsent < usable && rigsInFlight < 2;
            bool wantSurveyor = deepOn && surveyors.Count == 0 && usable < 2 && !Queued("geological_surveyor");

            // ---- The expansion plan, worked out once: where the next outpost goes.
            // Trucks work a field on their own only within a leash of a finished drop-off (World.LeashDist); past it they go where they
            // are sent. When the ore within the leash runs thin, the next outpost goes to the nearest field, or the nearest base site
            // nobody holds (the rules call empty corners expansion sites), beyond it; trucks then go there (see the trucks below).
            var drops = structures.Where(s => s.IsComplete && s.Def.DropOff).Select(s => s.Center).ToList();
            bool Reached(Vec2 p) => drops.Any(dp => Vec2.Dist(dp, p) <= World.LeashDist);
            var foes = t.KnownEnemyStructures.Values.Where(k => k.team >= 0 && k.key != "derrick").Select(k => k.origin.Center).ToList();
            bool NearFoe(Vec2 p) => foes.Any(f => Vec2.Dist(f, p) < EnemyBerth);
            // The ore tile a truck sent to a field would really mine: `harvest x,y` takes the nearest live ore of the kind within 12 tiles of the
            // point, which need not be at the field's root. One that is not by an enemy base it knows of (a truck choosing for itself keeps
            // HostileOreRadius from those, World.UpdateHarvester's Safe, and a coordinate harvest would not); null if there is none.
            Vec2? SafeOreNear(Vec2 at, int ore, System.Func<Vec2, bool> also = null) // (`also`: a further test of the tile's centre)
            {
                var tile = w.Map.NearestOre(at, 12, tt => !foes.Any(h => Vec2.Dist(h, tt.Center) < World.HostileOreRadius) && (also == null || also(tt.Center)), ore, t.StartPos); // (equally near tiles: as this base's own trucks choose them)
                return tile.HasValue ? tile.Value.Center : (Vec2?)null;
            }
            var fields = KnownFields(w, t);
            bool homeThin = fields.Where(f => OreWanted(f.type) && Reached(f.at)).Sum(f => f.ore) < ThinOre;
            // In an open arena the base sites nobody holds are kept for the players who join later (a newcomer takes a free site, and an
            // outpost by it would pin them or use up the seat), and so is the ore there while they are protected: only neutral fields, then.
            // (Every site but its own counts: it can't tell which of them a player holds.)
            bool OnOthersGround(Vec2 p, float berth) => w.Open && w.Map.Spawns.Any(s => Vec2.Dist(s, t.StartPos) >= 20f && Vec2.Dist(s, p) < berth);
            // Where an outpost for a site would really stand (the centre of its footprint, wherever placement finds open ground near the
            // site), or null if that is no place for one: by an enemy it knows of, or on ground kept for a newcomer, however far the site is.
            Vec2? DeploySpot(Vec2 site)
            {
                var spot = w.FindPlacement(team, "outpost", site, requireNear: false) ?? Int2.Of(site);
                var at = new Vec2(spot.X + 1, spot.Y + 1);
                return NearFoe(at) || OnOthersGround(at, SiteClear) ? (Vec2?)null : at;
            }
            // The spot to send the next outpost truck: the first of the sites, nearest first, that has a place to deploy.
            Vec2? ExpansionSpot()
            {
                var sites = new List<(float score, Vec2 at)>();
                void Consider(Vec2 at, float bonus)
                {
                    if (Reached(at) || NearFoe(at) || OnOthersGround(at, SiteBerth)) return;
                    float d = drops.Count == 0 ? Vec2.Dist(at, t.StartPos) : drops.Min(dp => Vec2.Dist(dp, at));
                    if (d <= MaxExpansion) sites.Add((d - bonus, at)); // (a known field beats a guess)
                }
                foreach (var f in fields) if (f.ore >= 400 && OreWanted(f.type)) Consider(f.at, 25f);
                var middle = new Vec2(w.Map.W / 2f, w.Map.H / 2f);
                // (Not the corner across the map: with the rival's base somewhere unseen, the farthest site is the likeliest to be it.)
                float nearestSite = w.Map.Spawns.Where(s => Vec2.Dist(s, t.StartPos) >= 20f).Select(s => Vec2.Dist(s, t.StartPos)).DefaultIfEmpty(0f).Min();
                foreach (var s in w.Map.Spawns)
                {
                    if (Vec2.Dist(s, t.StartPos) < 20f || structures.Any(b => Vec2.Dist(b.Center, s) < 20f)) continue; // (the corners with a base on them)
                    if (!t.Explored[w.Map.Idx((int)s.X, (int)s.Y)] && Vec2.Dist(s, t.StartPos) > 1.3f * nearestSite) continue;
                    var at = s + (middle - s).Normalized * 7f; // where a corner's iron and copper lie
                    if (t.Explored[w.Map.Idx((int)at.X, (int)at.Y)] && !fields.Any(f => f.ore >= 200 && Vec2.Dist(f.at, at) < 14f)) continue; // seen, and nothing there
                    Consider(at, 0f);
                }
                foreach (var c in sites.OrderBy(x => System.MathF.Round(x.score, 2)).ThenByDescending(x => Rightward(x.at)))
                    if (DeploySpot(c.at) is Vec2 spot) return spot;
                return null;
            }
            // (A truck standing about with nowhere to go isn't on its way anywhere: it must not hold back the scouting that finds a place.)
            // (The electronics plant is needed to buy a truck, not to send one it has paid for: Deploy doesn't need it.)
            bool canBuyTruck = w.HasComplete(team, "electronics_plant");
            Vec2? spotNow = (canBuyTruck || mine.Any(e => e.Def.Key == "outpost_truck") || Queued("outpost_truck")) && Count("outpost") < (homeThin ? MaxOutposts : 2) ? ExpansionSpot() : null;
            bool outpostOnItsWay = Queued("outpost_truck") || mine.Any(e => e.Def.Key == "outpost_truck" && (outpostTargets.ContainsKey(e.Id) || spotNow.HasValue));
            bool wantOutpost = canBuyTruck && spotNow.HasValue && !outpostOnItsWay;
            // What it saves up for, so the army and the build order don't spend it as it arrives and the converters leave it alone:
            // the first two mines' rigs, or the surveyor that finds them. With the command center gone, the construction truck that
            // puts one back comes before any of that (1500 steel is never there if the army spends it as it comes in). With the ore
            // within reach running thin, an outpost truck goes ahead of a rig it can't pay for yet, but only when the truck can be paid
            // for now (half the price, and the ore comes in within minutes: with the trucks idle there is no steel coming in to save the
            // rig's 800 from), or when there is no rig or surveyor to save for. Saving for the truck while it is out of reach too, or
            // ahead of a rig it could buy, delayed the deep mines by more ore than the outposts brought (8 to 12 games a variant, 45 minutes).
            bool needHq = Count("command_center") == 0 && !mine.Any(e => e.Def.Key == "construction_truck") && !Queued("construction_truck") &&
                          w.HasComplete(team, "factory") && w.HasComplete(team, "electronics_plant");
            string deepSaving = wantRig && (pumping < 2 || retireWanted) ? "drill_rig" : wantSurveyor && pumping == 0 ? "geological_surveyor" : null;
            bool outpostFirst = wantOutpost && homeThin && (deepSaving == null || (t.Missing(Defs.Get(deepSaving).Cost) != null && t.Missing(Defs.Get("outpost_truck").Cost) == null));
            string saving = needHq ? "construction_truck" : outpostFirst ? "outpost_truck" : deepSaving;
            var savingCost = saving != null ? Defs.Get(saving).Cost : null;
            if (saving != t.SaveFor) { if (saving != null) Do(w, "type", "save_for", "unit", saving); else Do(w, "type", "save_for"); }

            // With the surface dry and no deep mine pumping, the economy comes first: no army or new buildings (except power)
            // until a rig is on its way.
            // Regrowing fields keep a trickle of surface ore about, so "dry" alone no longer says when to go deep: a free
            // mining zone already flagged and no deep mine pumping means saving for a rig too. (Once it has been deep, with
            // no zone left to drill, it carries on: surveyors are still looking.) It holds back the army, defences and tech, never the
            // buildings the economy stands on: if any of those is gone, putting it back comes first (see FirstMissingEconomy below).
            bool economyFirst = (surfaceDry || usable > 0) && pumping == 0 && rigsInFlight == 0 && w.HasComplete(team, "electronics_plant") && (!wentDeep || usable > 0);

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
            else { stuckSince.Clear(); step = head = FirstMissingEconomy(Count); }
            int draw = (step != null ? System.Math.Max(0, -Defs.Get(step).Power) : 0) + 50 * rigsInFlight;
            // What to build: the step, unless something more urgent goes first. Every such override goes through Override(): it has to be
            // buildable (the buildings it needs stand) and, unless it is worth saving up for, paid for; otherwise the step stands. So a
            // blocked override can never hide the step it was meant to jump ahead of.
            string Override(string key, bool saveUp = false) =>
                key != null && w.MissingPrereq(team, Defs.Get(key)) == null && (saveUp || t.Missing(Defs.Get(key).Cost) == null) ? key : null;
            bool powerShort = t.PowerProduced - t.PowerUsed + pending - draw < 25 && structures.Count > 1;
            // (A fusion reactor only if it can be paid for now: short of circuits, plain power plants keep the lights on.)
            string powerFix = !powerShort ? null : t.Amount("plasma") > 80 && Count("fusion_reactor") == 0 ? Override("fusion_reactor") ?? "power_plant" : "power_plant";
            string next = Override(powerFix, saveUp: true) ?? Override(moreRefineries ? "mining_refinery" : null) ?? step;
            if (next != null && t.StructureQueue.Count == 0 && t.Missing(Defs.Get(next).Cost) == null && w.MissingPrereq(team, Defs.Get(next)) == null &&
                (saving == null || EconomyStructures.Contains(next) || next == "fusion_reactor" || Defs.Get(next).Cost.All(kv => t.Amount(kv.Key) - (savingCost.TryGetValue(kv.Key, out var rc) ? rc : 0) >= kv.Value)))
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
            // (Not for an extra refinery while a building the economy stands on is missing: the factory's copper comes from the very
            // copper ore the refinery would hold back, so holding it for an optional refinery could keep the factory from ever being paid for.)
            bool refineryHold = moreRefineries && FirstMissingEconomy(Count) == null;
            foreach (var ore in Defs.Ores)
            {
                int hold = 0;
                if (next != null && Defs.Get(next).Cost.TryGetValue(ore, out var c1)) hold = c1;
                if (head != null && Defs.Get(head).Cost.TryGetValue(ore, out var c2)) hold = System.Math.Max(hold, c2);
                if (refineryHold && Defs.Get("mining_refinery").Cost.TryGetValue(ore, out var c3)) hold = System.Math.Max(hold, c3);
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
            // The house AI's trucks keep to fair ore (#87). A truck choosing its own ore creeps from field to field, 12 tiles at a time, once its
            // drop-offs' reach is mined out (World.UpdateHarvester), and the nearest ore within 80 tiles can be the rival's: its home field or
            // its corner, where its army sits. Ore by a base site that is not ours is fair only to trucks whose drop-offs reach it (an outpost's).
            bool Fair(Vec2 p) => !Passive || Reached(p) || !w.Map.Spawns.Any(s => Vec2.Dist(s, t.StartPos) >= 20f && Vec2.Dist(s, p) < SiteBerth);
            for (int i = 0; i < trucks.Count; i++)
            {
                int type = i < plan.Count ? plan[i] : Map.Iron;
                var tr = trucks[i];
                // Trucks that ran out of ore stand idle: every 20 s they look again (fields regrow, salvage lands).
                bool retry = tr.Order == Order.Idle && (int)w.Time % 20 == 0;
                bool drifted = tr.Order == Order.Harvest && tr.HarvestTile.HasValue && !Fair(tr.HarvestTile.Value.Center);
                if ((tr.HarvestType != type || retry || drifted) && tr.Cargo == 0 && tr.Order != Order.ReturnOre)
                {
                    // The house AI sends it to the nearest fair ore of its kind that is not by an enemy it knows of; the attacking AI leaves the
                    // pick to the command (the nearest ore of that kind). None within 80 tiles: it goes to the nearest field of that ore its
                    // drop-offs reach, an outpost's perhaps, to a fair tile of it that is safe (the first field that has one).
                    var near = w.Map.NearestOre(tr.Pos, 80, !Passive ? null : (System.Func<Int2, bool>)(tt => Fair(tt.Center) && !foes.Any(h => Vec2.Dist(h, tt.Center) < World.HostileOreRadius)), type, t.StartPos);
                    Vec2? goal = Passive && near.HasValue ? near.Value.Center : (Vec2?)null;
                    if (!near.HasValue && tr.Order == Order.Idle)
                        goal = fields.Where(f => f.type == type && f.ore >= 100 && Reached(f.at)).OrderBy(f => System.MathF.Round(Vec2.Dist(f.at, tr.Pos), 2)).ThenByDescending(f => Rightward(f.at)).Select(f => SafeOreNear(f.at, type, Fair)).FirstOrDefault(p => p.HasValue);
                    if (goal.HasValue) Do(w, "type", "harvest", "units", new[] { tr.Id }, "ore", Defs.Ores[type], "x", goal.Value.X, "y", goal.Value.Y);
                    else if (drifted || (Passive && tr.Order != Order.Idle && tr.HarvestType != type)) Do(w, "type", "stop", "units", new[] { tr.Id }); // (no fair ore of the new kind: not left on the old kind, and not sent to unfair ore either; it looks again every 20 s)
                    else if (!Passive) Do(w, "type", "harvest", "units", new[] { tr.Id }, "ore", Defs.Ores[type]);
                }
            }

            // ---- Expansion: claim remote ore fields with outposts (the plan is above: where, and whether the ore within reach is thin)
            if (wantOutpost && (saving == "outpost_truck" ? t.Missing(savingCost) == null : Affordable("outpost_truck")))
                Do(w, "type", "train", "unit", "outpost_truck");
            // A truck that gives up its destination goes home (to the nearest drop-off), not on towards what it was avoiding.
            void SendHome(Entity truck)
            {
                if (drops.Count == 0) { w.SetOrder(truck, Order.Idle, truck.Pos); return; }
                var home = drops.OrderBy(dp => Vec2.Dist(dp, truck.Pos)).First();
                Do(w, "type", "move", "units", new[] { truck.Id }, "x", home.X, "y", home.Y);
            }
            foreach (var ot in mine.Where(e => e.Def.Key == "outpost_truck"))
            {
                // (A destination that has turned out to be no place for an outpost, an enemy base seen by it, is given up.)
                bool gaveUp = false;
                if (outpostTargets.TryGetValue(ot.Id, out var was) && (NearFoe(was) || OnOthersGround(was, SiteClear))) { outpostTargets.Remove(ot.Id); gaveUp = true; }
                if (!outpostTargets.TryGetValue(ot.Id, out var goal))
                {
                    if (!spotNow.HasValue) { if (gaveUp) SendHome(ot); continue; }
                    goal = spotNow.Value;
                    outpostTargets[ot.Id] = goal;
                    Do(w, "type", "move", "units", new[] { ot.Id }, "x", goal.X, "y", goal.Y);
                }
                else if (ot.Order == Order.Idle)
                {
                    // (Not where it has stopped if that is by an enemy or on ground kept for a newcomer: it is not where it was heading.)
                    var footprint = Defs.Get("outpost"); var tile = Int2.Of(ot.Pos); // (Deploy puts the 2x2 at the truck's tile, and the centre is what counts)
                    var centre = new Vec2(tile.X - (footprint.SizeX - 1) / 2 + footprint.SizeX / 2f, tile.Y - (footprint.SizeY - 1) / 2 + footprint.SizeY / 2f);
                    if (NearFoe(centre) || OnOthersGround(centre, SiteClear)) { outpostTargets.Remove(ot.Id); SendHome(ot); continue; }
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

            // With the ore within reach thin and nowhere known to go, scout: a recon drone flies to the nearest ground it hasn't seen
            // (and back on its tank of fuel). What it sees shows up as fields, and the plan above picks the nearest.
            if (homeThin && !wantOutpost && !outpostOnItsWay && Count("outpost") < MaxOutposts && w.HasComplete(team, "electronics_plant"))
            {
                var drones = mine.Where(e => e.Def.Key == "recon_drone").ToList();
                if (drones.Count == 0 && !Queued("recon_drone") && Affordable("recon_drone")) Do(w, "type", "train", "unit", "recon_drone");
                foreach (var drone in drones.Where(e => e.Order == Order.Idle))
                {
                    Vec2? unseen = null; float nearest = float.MaxValue, range = 0.7f * w.Map.W; // (a drone's tank is 1.5 map widths: there and back)
                    foreach (int grid in new[] { 24, 4, 1 }) // the coarse grid first, then every 4th tile, then every tile in reach: no unseen patch is missed
                    {
                        for (int y = grid / 2; y < w.Map.H; y += grid)
                            for (int x = grid / 2; x < w.Map.W; x += grid)
                            {
                                if (System.Math.Abs(x - drone.Pos.X) > range || System.Math.Abs(y - drone.Pos.Y) > range) continue;
                                var p = new Vec2(x, y); float d = Vec2.Dist(p, drone.Pos);
                                if (d < nearest && d <= range && d >= 6f && !t.Explored[w.Map.Idx(x, y)] && !NearFoe(p)) { nearest = d; unseen = p; }
                            }
                        if (unseen.HasValue) break;
                    }
                    if (unseen.HasValue) Do(w, "type", "move", "units", new[] { drone.Id }, "x", unseen.Value.X, "y", unseen.Value.Y);
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
                                        .OrderBy(d => Near(d.Center)).ThenByDescending(d => Rightward(d.Center)).ToList();
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
            // (Never a structure the team is standing on: with it gone the team would be out of the game, a rig on its way or not.
            // Refused while it is under fire: tried again next second.)
            void Sell(Entity e) { if (w.StillStanding(team, e)) Do(w, "type", "sell", "structure_id", e.Id); }
            foreach (var dm in deepMines.Where(e => e.IsComplete && !Pumping(e))) Sell(dm);
            if (retire != null) Sell(retire);
            // Copper piling up: a deep copper mine adds to it for as long as its deposit lasts and draws 50 power, so past the surplus the
            // mines are sold and their rigs' slots go to iron (unless a purchase is waiting on the raw copper ore that mine pumps).
            if (copperOnHand > CopperSurplus && !RawShort("copper_ore"))
                foreach (var cm in deepMines.Where(e => e.IsComplete && Pumping(e) && w.Map.DepositById(e.DepositId).Type == Map.Copper)) Sell(cm);
            // Surveyors and rigs that exist get their work whether or not there is a factory (it is needed to buy more, not to use them).
            if (wantDeep)
            {
                // Idle rigs go to the best open zone whose ore has a slot left and deploy there by themselves.
                int roomLeft = basicRoom;
                foreach (var rig in rigs.Where(e => e.Order == Order.Idle))
                {
                    var zone = open.FirstOrDefault(d => slots[d.Type] > 0 && (d.Type >= Map.Crystal || roomLeft > 0));
                    if (zone == null) break;
                    open.Remove(zone);
                    var sent = Do(w, "type", "drill", "units", new[] { rig.Id }, "zone", zone.Id);
                    if (!(sent["ok"] is bool ok && ok)) continue;
                    slots[zone.Type]--;
                    if (zone.Type < Map.Crystal) roomLeft--;
                }
                // Surveyors prospect around the base on their own, flagging mining zones as they go; when the area is
                // covered they widen it.
                if (w.Time >= nextProspect)
                    foreach (var sv in surveyors.Where(e => e.Order == Order.Idle))
                    {
                        var r = Do(w, "type", "prospect", "units", new[] { sv.Id }, "x", t.StartPos.X, "y", t.StartPos.Y, "radius", prospectRadius);
                        if (r["ok"] is bool ok2 && ok2) continue;
                        if (prospectRadius < 120) prospectRadius += 30;
                        else nextProspect = w.Time + 30; // everything in reach is surveyed: look again later
                    }
            }
            if (deepOn)
            {
                // (What it is saving for has its cost in the reserve, so that is not held against it.)
                if (wantSurveyor && (saving == "geological_surveyor" ? t.Missing(savingCost) == null : Affordable("geological_surveyor")))
                    Do(w, "type", "train", "unit", "geological_surveyor");
                if (wantRig && (saving == "drill_rig" ? t.Missing(savingCost) == null : Affordable("drill_rig")))
                    Do(w, "type", "train", "unit", "drill_rig");
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
            // (The house AI holds its ground: a unit that can do a base no harm (a mining or outpost truck, surveyor, drill rig, medic or repair
            // truck) by an outpost is no attack, while anything armed, an engineer (captures structures below 50%) or a mine layer is one. Only the
            // units within HouseDefenceReach of a threat answer it. Sending the whole army across the map after trucks left both armies parked in
            // the contested middle, shooting every truck sent to mine there: #87.)
            bool Harmless(Entity e) => Passive && !e.IsArmed && !e.Def.Engineer && !e.Def.LaysMines;
            bool IsThreat(Entity e) => !e.Dead && e.Team != team && e.Team >= 0 && !e.IsStructure && !Harmless(e) && w.IsVisibleTo(team, e) &&
                                       structures.Any(s => s.Def.Key != "derrick" && Vec2.Dist(e.Pos, s.Center) < 10);
            if (Passive)
            {
                // Each defender within reach answers the nearest threat it can hit: a threat at an outpost with no army near it is left,
                // and one at the base is still answered.
                var threats = w.Entities.Where(IsThreat).ToList();
                if (threats.Count > 0)
                {
                    foreach (var u in army.Where(u => u.Order == Order.Idle || u.Order == Order.Move))
                    {
                        var target = threats.Where(x => u.Def.Weapon.CanHit(x.Def) && Vec2.Dist(u.Pos, x.Pos) <= HouseDefenceReach).OrderBy(x => Vec2.Dist(u.Pos, x.Pos)).FirstOrDefault();
                        if (target != null) w.SetOrder(u, Order.AttackMove, target.Pos);
                    }
                    return;
                }
            }
            else
            {
                var threat = w.Entities.FirstOrDefault(IsThreat);
                if (threat != null)
                {
                    foreach (var u in army.Where(u => (u.Order == Order.Idle || u.Order == Order.Move) && u.Def.Weapon.CanHit(threat.Def)))
                        w.SetOrder(u, Order.AttackMove, threat.Pos);
                    return;
                }
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
                if (known.Count > 0) { var k = known.OrderBy(x => Near(new Vec2(x.origin.X, x.origin.Y))).ThenByDescending(x => Rightward(new Vec2(x.origin.X, x.origin.Y))).First(); goal = new Vec2(k.origin.X + 1, k.origin.Y + 1); }
                else
                {
                    var corners = w.Map.Spawns.Where(s => !t.Explored[w.Map.Idx((int)s.X, (int)s.Y)]).ToList();
                    if (corners.Count == 0) corners = w.Map.Spawns.Where(s => Vec2.Dist(s, t.StartPos) > 5).ToList();
                    goal = corners.OrderBy(s => Near(s)).ThenByDescending(s => Rightward(s)).First();
                }
                foreach (var u in idle) w.SetOrder(u, Order.AttackMove, goal);
                waveSize = System.Math.Min(10, waveSize + 1);
                Do(w, "type", "say", "text", $"Wave of {idle.Count} inbound.");
            }
        }
    }
}
